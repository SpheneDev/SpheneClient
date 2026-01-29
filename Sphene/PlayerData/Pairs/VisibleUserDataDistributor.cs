using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.PlayerData.Factories;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.Files;
using Microsoft.Extensions.Logging;

namespace Sphene.PlayerData.Pairs;

public class VisibleUserDataDistributor : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileUploadManager _fileTransferManager;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly PairManager _pairManager;
    private readonly SessionAcknowledgmentManager _sessionAcknowledgmentManager;
    private CharacterData? _lastCreatedData;
    private CharacterData? _uploadingCharacterData = null;
    private readonly List<UserData> _previouslyVisiblePlayers = [];
    private Task<CharacterData>? _fileUploadTask = null;
    private readonly HashSet<UserData> _usersToPushDataTo = [];
    private readonly SemaphoreSlim _pushDataSemaphore = new(1, 1);
    private readonly CancellationTokenSource _runtimeCts = new();
    
    // Hash-based acknowledgment tracking
    private readonly Dictionary<UserData, string> _lastSentHashPerUser = new();
    
    // Delayed push tracking for newly connected users
    private readonly Dictionary<UserData, DateTime> _delayedPushUsers = new();
    private readonly Dictionary<UserData, DateTime> _delayedReloadUsers = new();
    private readonly Timer _delayedPushTimer;
    private readonly Timer _characterReloadTimer;
    
    private const int DELAYED_PUSH_SECONDS = 3;
    private const int CHARACTER_RELOAD_DELAY_SECONDS = 3;


    public VisibleUserDataDistributor(ILogger<VisibleUserDataDistributor> logger, ApiController apiController, DalamudUtilService dalamudUtil,
        PairManager pairManager, SpheneMediator mediator, FileUploadManager fileTransferManager, SessionAcknowledgmentManager sessionAcknowledgmentManager,
        GameObjectHandlerFactory gameObjectHandlerFactory) : base(logger, mediator)
    {
        _apiController = apiController;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _fileTransferManager = fileTransferManager;
        _sessionAcknowledgmentManager = sessionAcknowledgmentManager;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        

        
        // Initialize delayed push timer for newly connected users
        _delayedPushTimer = new Timer(ProcessDelayedPushes, null, 
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        
        // Initialize character reload timer - triggers every second to check for expired reload delays
        _characterReloadTimer = new Timer(ProcessDelayedReloads, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => FrameworkOnUpdate());
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            var previousHash = _lastCreatedData?.DataHash?.Value;
            var newHash = msg.CharacterData.DataHash?.Value;
            
            _lastCreatedData = msg.CharacterData;
            
            // Only push if hash actually changed or if we have users waiting for data
            if (!string.Equals(previousHash, newHash, StringComparison.Ordinal) || _usersToPushDataTo.Count > 0)
            {
                Logger.LogDebug("Character data hash changed from {oldHash} to {newHash}, pushing to users", 
                    previousHash ?? "null", newHash ?? "null");
                PushToAllVisibleUsers(forced: true);
            }
            else
            {
                Logger.LogDebug("Character data hash unchanged ({hash}), skipping push", newHash ?? "null");
            }
        });

        Mediator.Subscribe<ConnectedMessage>(this, (_) => PushToAllVisibleUsers());
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => 
        {
            _previouslyVisiblePlayers.Clear();
            _lastSentHashPerUser.Clear();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runtimeCts.Cancel();
            _runtimeCts.Dispose();
            _delayedPushTimer?.Dispose();
            _characterReloadTimer?.Dispose();
            _pushDataSemaphore?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PushToAllVisibleUsers(bool forced = false)
    {
        var currentHash = _lastCreatedData?.DataHash.Value;
        if (string.IsNullOrEmpty(currentHash)) return;
        
        // Always validate the current hash for the current user to ensure it's stored in the database
        _ = Task.Run(async () => {
            try {
                var currentUserUID = _apiController.UID;
                if (!string.IsNullOrEmpty(currentUserUID)) {
                    var response = await _apiController.ValidateCharaDataHash(currentUserUID, currentHash).ConfigureAwait(false);
                    if (response != null) {
                        Logger.LogDebug("Validated current user hash: {hash}, IsValid: {isValid}, CurrentHash: {currentHash}", 
                            currentHash, response.IsValid, response.CurrentHash);
                    } else {
                        Logger.LogWarning("Hash validation returned null response for user {userUID} and hash {hash}", 
                            currentUserUID, currentHash);
                    }
                }
            } catch (Exception ex) {
                Logger.LogError(ex, "Failed to validate current user hash for {hash}", currentHash);
            }
        });
        
        foreach (var user in _pairManager.GetVisibleUsers())
        {
            // Only add users who haven't received this hash yet or if forced
            _lastSentHashPerUser.TryGetValue(user, out var lastHash);
            if (forced || lastHash == null || !string.Equals(lastHash, currentHash, StringComparison.Ordinal))
            {
                _usersToPushDataTo.Add(user);
                Logger.LogTrace("Adding user {user} to push queue - LastHash: {lastHash}, CurrentHash: {currentHash}, Forced: {forced}", 
                    user.AliasOrUID, lastHash ?? "NONE", currentHash, forced);
            }
            else
            {
                Logger.LogTrace("Skipping user {user} - already has hash {hash}", user.AliasOrUID, currentHash);
            }
        }

        if (_usersToPushDataTo.Count > 0)
        {
            Logger.LogDebug("Pushing data {hash} for {count} visible players (out of {total} total)", 
                currentHash, _usersToPushDataTo.Count, _pairManager.GetVisibleUsers().Count);
            PushCharacterData(forced);
        }
        else
        {
            Logger.LogTrace("No users need data push - all have current hash {hash}", currentHash);
        }
    }

    private void FrameworkOnUpdate()
    {
        if (!_dalamudUtil.GetIsPlayerPresent() || !_apiController.IsConnected) return;

        var allVisibleUsers = _pairManager.GetVisibleUsers();
        var newVisibleUsers = allVisibleUsers.Except(_previouslyVisiblePlayers).ToList();
        _previouslyVisiblePlayers.Clear();
        _previouslyVisiblePlayers.AddRange(allVisibleUsers);
        if (newVisibleUsers.Count == 0) return;

        Logger.LogDebug("New users entered view: {users}. Scheduling delayed data push check.",
            string.Join(", ", newVisibleUsers.Select(k => k.AliasOrUID)));
        
        // Add new users to delayed push queue to give them time to stabilize connection
        var currentTime = DateTime.UtcNow;
        foreach (var user in newVisibleUsers)
        {
            _delayedPushUsers[user] = currentTime;
            Logger.LogDebug("Added user {user} to delayed push queue - will push in {seconds} seconds", 
                user.AliasOrUID, DELAYED_PUSH_SECONDS);
        }
    }

    private void PushCharacterData(bool forced = false)
    {
        if (_lastCreatedData == null || _usersToPushDataTo.Count == 0) return;

        _ = Task.Run(async () =>
        {
            forced |= _uploadingCharacterData?.DataHash != _lastCreatedData.DataHash;

            if (_fileUploadTask == null || (_fileUploadTask?.IsCompleted ?? false) || forced)
            {
                _uploadingCharacterData = _lastCreatedData.DeepClone();
                Logger.LogDebug("Starting UploadTask for {hash}, Reason: TaskIsNull: {task}, TaskIsCompleted: {taskCpl}, Forced: {frc}",
                    _lastCreatedData.DataHash, _fileUploadTask == null, _fileUploadTask?.IsCompleted ?? false, forced);
                _fileUploadTask = _fileTransferManager.UploadFiles(_uploadingCharacterData, [.. _usersToPushDataTo]);
            }

            if (_fileUploadTask != null)
            {
                var dataToSend = await _fileUploadTask.ConfigureAwait(false);
                await _pushDataSemaphore.WaitAsync(_runtimeCts.Token).ConfigureAwait(false);
                try
                {
                    if (_usersToPushDataTo.Count == 0) return;
                    
                    // Validate hashes before pushing to avoid unnecessary data transfers
                    var usersNeedingData = new List<UserData>();
                    var currentHash = dataToSend.DataHash.Value;
                    
                    foreach (var user in _usersToPushDataTo.ToList())
                    {
                        if (_lastSentHashPerUser.TryGetValue(user, out var lastSentHash) && !forced)
                        {
                            // Validate if the hash is still valid on the client side
                            try {
                                var validationResponse = await _apiController.ValidateCharaDataHash(user.UID, lastSentHash).ConfigureAwait(false);
                                if (validationResponse != null && validationResponse.IsValid && string.Equals(lastSentHash, currentHash, StringComparison.Ordinal))
                                {
                                    Logger.LogTrace("Hash still valid for user {user}, skipping push", user.AliasOrUID);
                                    continue;
                                }
                            }
                            catch (Exception ex) {
                                Logger.LogWarning(ex, "Hash validation failed for user {user}, proceeding with push", user.AliasOrUID);
                            }
                        }
                        
                        usersNeedingData.Add(user);
                    }
                    
                    if (usersNeedingData.Count == 0)
                    {
                        Logger.LogDebug("No users need data push after hash validation");
                        _usersToPushDataTo.Clear();
                        return;
                    }
                    
                    // Create hash key for acknowledgment tracking
                    var hashKey = dataToSend.DataHash.Value;

                    // Revoke AckYou for all users before pushing new data
                    // This ensures partners see a yellow eye indicating that the sender has changed state
                    Logger.LogDebug("Revoking AckYou status before pushing new data hash: {hash}", hashKey);
                    _ = _apiController.UserUpdateAckYou(false);

                    _pairManager.SetPendingAcknowledgmentForSender([.. usersNeedingData], hashKey);
                    _sessionAcknowledgmentManager.SetPendingAcknowledgmentForHashVersion([.. usersNeedingData], hashKey);
                    
                    // Track the hash sent to each user
                    foreach (var user in usersNeedingData)
                    {
                        _lastSentHashPerUser[user] = currentHash;
                        Logger.LogTrace("Updated hash tracking for user {user}: {hash}", user.AliasOrUID, currentHash);
                    }
                    
                    Logger.LogDebug("Pushing {data} to {users} with hash key {hashKey}", dataToSend.DataHash, string.Join(", ", usersNeedingData.Select(k => k.AliasOrUID)), hashKey);
                    await _apiController.PushCharacterData(dataToSend, [.. usersNeedingData], hashKey).ConfigureAwait(false);
                    _usersToPushDataTo.Clear();
                }
                finally
                {
                    _pushDataSemaphore.Release();
                }
            }
        });
    }
    
    

    
    /// Processes delayed pushes for newly connected users after the stabilization period
    
    private void ProcessDelayedPushes(object? state)
    {
        if (_delayedPushUsers.Count == 0) return;
        
        var currentTime = DateTime.UtcNow;
        var usersToProcess = new List<UserData>();
        
        // Find users whose delay period has expired
        foreach (var kvp in _delayedPushUsers.ToList())
        {
            var user = kvp.Key;
            var delayStartTime = kvp.Value;
            
            if ((currentTime - delayStartTime).TotalSeconds >= DELAYED_PUSH_SECONDS)
            {
                usersToProcess.Add(user);
                _delayedPushUsers.Remove(user);
                Logger.LogDebug("Delay period expired for user {user} - adding to push queue", user.AliasOrUID);
            }
        }
        
        // Add users to push queue and trigger push if we have data
        if (usersToProcess.Count > 0)
        {
            foreach (var user in usersToProcess)
            {
                _usersToPushDataTo.Add(user);
                // Schedule character reload after acknowledgment is sent
                _delayedReloadUsers[user] = DateTime.UtcNow;
                Logger.LogDebug("Scheduled character reload for user {user} in {seconds} seconds", 
                    user.AliasOrUID, CHARACTER_RELOAD_DELAY_SECONDS);
            }
            
            // Only push if we have data to push
            if (_lastCreatedData != null)
            {
                Logger.LogDebug("Pushing character data {hash} to {count} delayed users: {users}",
                    _lastCreatedData.DataHash?.Value ?? "null", usersToProcess.Count,
                    string.Join(", ", usersToProcess.Select(u => u.AliasOrUID)));
                PushCharacterData();
            }
            else
            {
                Logger.LogDebug("No character data available yet for delayed users: {users}",
                    string.Join(", ", usersToProcess.Select(u => u.AliasOrUID)));
            }
        }
    }
    
    /// Processes delayed character reloads after acknowledgment has been sent
    
    private async void ProcessDelayedReloads(object? state)
    {
        if (_delayedReloadUsers.Count == 0) return;
        
        var currentTime = DateTime.UtcNow;
        var usersToReload = new List<UserData>();
        
        // Find users whose reload delay period has expired
        foreach (var kvp in _delayedReloadUsers.ToList())
        {
            var user = kvp.Key;
            var delayStartTime = kvp.Value;
            
            if ((currentTime - delayStartTime).TotalSeconds >= CHARACTER_RELOAD_DELAY_SECONDS)
            {
                usersToReload.Add(user);
                _delayedReloadUsers.Remove(user);
                Logger.LogDebug("Character reload delay expired for user {user} - triggering reload", user.AliasOrUID);
            }
        }
        
        // Trigger character reload for users
        if (usersToReload.Count > 0)
        {
            Logger.LogDebug("Triggering character reload for {count} users: {users}",
                usersToReload.Count, string.Join(", ", usersToReload.Select(u => u.AliasOrUID)));
            
            // Trigger character data recreation by creating a temporary handler and sending CreateCacheForObjectMessage
            try
            {
                var tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => _dalamudUtil.GetPlayerPtr(), isWatched: false).ConfigureAwait(false);
                Mediator.Publish(new CreateCacheForObjectMessage(tempHandler));
                tempHandler.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to trigger character reload");
            }
        }
    }
    
    
    /// Checks if a user needs data based on hash comparison
    
    public bool UserNeedsData(UserData user, string dataHash)
    {
        if (!_lastSentHashPerUser.TryGetValue(user, out var lastHash))
            return true; // User never received any data
            
        return !string.Equals(lastHash, dataHash, StringComparison.Ordinal);
    }
    
    
    /// Gets the last sent hash for a user
    
    public string? GetLastSentHashForUser(UserData user)
    {
        return _lastSentHashPerUser.TryGetValue(user, out var hash) ? hash : null;
    }
}
