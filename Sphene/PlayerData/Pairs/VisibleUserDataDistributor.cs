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
    private const string SyncProgressTag = "[SyncProgress]";
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
            Logger.LogDebug("{tag} Data created: previousHash={oldHash} newHash={newHash}", SyncProgressTag, previousHash ?? "null", newHash ?? "null");
            
            // Only push if hash actually changed or if we have users waiting for data
            if (!string.Equals(previousHash, newHash, StringComparison.Ordinal) || _usersToPushDataTo.Count > 0)
            {
                Logger.LogDebug("{tag} Data change detected: oldHash={oldHash} newHash={newHash} queuedUsers={count}",
                    SyncProgressTag, previousHash ?? "null", newHash ?? "null", _usersToPushDataTo.Count);
                PushToAllVisibleUsers(forced: true);
            }
            else
            {
                Logger.LogDebug("{tag} Data unchanged: hash={hash} skip push", SyncProgressTag, newHash ?? "null");
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
        Logger.LogDebug("{tag} Push check: currentHash={hash} forced={forced}", SyncProgressTag, currentHash, forced);
        
        // Always validate the current hash for the current user to ensure it's stored in the database
        _ = Task.Run(async () => {
            try {
                var currentUserUID = _apiController.UID;
                if (!string.IsNullOrEmpty(currentUserUID)) {
                    var response = await _apiController.ValidateCharaDataHash(currentUserUID, currentHash).ConfigureAwait(false);
                    if (response != null) {
                        Logger.LogDebug("{tag} Hash validate self: hash={hash} isValid={isValid} currentHash={currentHash}", 
                            SyncProgressTag, currentHash, response.IsValid, response.CurrentHash);
                    } else {
                        Logger.LogWarning("{tag} Hash validate self: null response user={userUID} hash={hash}", 
                            SyncProgressTag, currentUserUID, currentHash);
                    }
                }
            } catch (Exception ex) {
                Logger.LogError(ex, "{tag} Hash validate self failed: hash={hash}", SyncProgressTag, currentHash);
            }
        });
        
        foreach (var user in _pairManager.GetVisibleUsers())
        {
            // Only add users who haven't received this hash yet or if forced
            _lastSentHashPerUser.TryGetValue(user, out var lastHash);
            if (forced || lastHash == null || !string.Equals(lastHash, currentHash, StringComparison.Ordinal))
            {
                _usersToPushDataTo.Add(user);
                Logger.LogDebug("{tag} Push queue add: user={user} lastHash={lastHash} currentHash={currentHash} forced={forced}", 
                    SyncProgressTag, user.AliasOrUID, lastHash ?? "NONE", currentHash, forced);
            }
            else
            {
                Logger.LogDebug("{tag} Push skip: user={user} already has hash={hash}", SyncProgressTag, user.AliasOrUID, currentHash);
            }
        }

        if (_usersToPushDataTo.Count > 0)
        {
            Logger.LogDebug("{tag} Push start: hash={hash} count={count} totalVisible={total}", 
                SyncProgressTag, currentHash, _usersToPushDataTo.Count, _pairManager.GetVisibleUsers().Count);
            PushCharacterData(forced);
        }
        else
        {
            Logger.LogDebug("{tag} Push skip: no users need data hash={hash}", SyncProgressTag, currentHash);
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

        Logger.LogDebug("{tag} New users visible: users={users} scheduling delayed push",
            SyncProgressTag, string.Join(", ", newVisibleUsers.Select(k => k.AliasOrUID)));
        
        // Add new users to delayed push queue to give them time to stabilize connection
        var currentTime = DateTime.UtcNow;
        foreach (var user in newVisibleUsers)
        {
            _delayedPushUsers[user] = currentTime;
            Logger.LogDebug("{tag} Delayed push queued: user={user} delaySeconds={seconds}", 
                SyncProgressTag, user.AliasOrUID, DELAYED_PUSH_SECONDS);
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
                Logger.LogDebug("{tag} Upload start: hash={hash} taskNull={task} taskCompleted={taskCpl} forced={frc}",
                    SyncProgressTag, _lastCreatedData.DataHash, _fileUploadTask == null, _fileUploadTask?.IsCompleted ?? false, forced);
                _fileUploadTask = _fileTransferManager.UploadFiles(_uploadingCharacterData, [.. _usersToPushDataTo]);
            }

            if (_fileUploadTask != null)
            {
                var dataToSend = await _fileUploadTask.ConfigureAwait(false);
                Logger.LogDebug("{tag} Upload complete: hash={hash} users={users}", SyncProgressTag, dataToSend.DataHash.Value, string.Join(", ", _usersToPushDataTo.Select(u => u.AliasOrUID)));
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
                                    Logger.LogDebug("{tag} Hash still valid: user={user} hash={hash} skip push", SyncProgressTag, user.AliasOrUID, lastSentHash);
                                    continue;
                                }
                            }
                            catch (Exception ex) {
                                Logger.LogWarning(ex, "{tag} Hash validation failed: user={user} proceeding with push", SyncProgressTag, user.AliasOrUID);
                            }
                        }
                        
                        usersNeedingData.Add(user);
                    }
                    
                    if (usersNeedingData.Count == 0)
                    {
                        Logger.LogDebug("{tag} Push skip: no users need data after validation", SyncProgressTag);
                        _usersToPushDataTo.Clear();
                        return;
                    }
                    
                    // Create hash key for acknowledgment tracking
                    var hashKey = dataToSend.DataHash.Value;

                    // Revoke AckYou for all users before pushing new data
                    // This ensures partners see a yellow eye indicating that the sender has changed state
                    Logger.LogDebug("{tag} Ack reset before push: hash={hash}", SyncProgressTag, hashKey);
                    _ = _apiController.UserUpdateAckYou(false);

                    _pairManager.SetPendingAcknowledgmentForSender([.. usersNeedingData], hashKey);
                    _sessionAcknowledgmentManager.SetPendingAcknowledgmentForHashVersion([.. usersNeedingData], hashKey);
                    
                    // Track the hash sent to each user
                    foreach (var user in usersNeedingData)
                    {
                        _lastSentHashPerUser[user] = currentHash;
                        Logger.LogDebug("{tag} Hash tracking update: user={user} hash={hash}", SyncProgressTag, user.AliasOrUID, currentHash);
                    }
                    
                    Logger.LogDebug("{tag} Push send: hash={hash} ackKey={hashKey} users={users}", SyncProgressTag, dataToSend.DataHash, hashKey, string.Join(", ", usersNeedingData.Select(k => k.AliasOrUID)));
                    await _apiController.PushCharacterData(dataToSend, [.. usersNeedingData], hashKey).ConfigureAwait(false);
                    Logger.LogDebug("{tag} Push complete: hash={hash} users={users}", SyncProgressTag, dataToSend.DataHash, string.Join(", ", usersNeedingData.Select(k => k.AliasOrUID)));
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
                Logger.LogDebug("{tag} Delayed push expired: user={user}", SyncProgressTag, user.AliasOrUID);
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
                Logger.LogDebug("{tag} Reload scheduled: user={user} delaySeconds={seconds}", 
                    SyncProgressTag, user.AliasOrUID, CHARACTER_RELOAD_DELAY_SECONDS);
            }
            
            // Only push if we have data to push
            if (_lastCreatedData != null)
            {
                Logger.LogDebug("{tag} Delayed push: hash={hash} count={count} users={users}",
                    SyncProgressTag, _lastCreatedData.DataHash?.Value ?? "null", usersToProcess.Count,
                    string.Join(", ", usersToProcess.Select(u => u.AliasOrUID)));
                PushCharacterData();
            }
            else
            {
                Logger.LogDebug("{tag} Delayed push skipped: no data users={users}",
                    SyncProgressTag, string.Join(", ", usersToProcess.Select(u => u.AliasOrUID)));
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
                Logger.LogDebug("{tag} Reload delay expired: user={user}", SyncProgressTag, user.AliasOrUID);
            }
        }
        
        // Trigger character reload for users
        if (usersToReload.Count > 0)
        {
            Logger.LogDebug("{tag} Reload trigger: count={count} users={users}",
                SyncProgressTag, usersToReload.Count, string.Join(", ", usersToReload.Select(u => u.AliasOrUID)));
            
            // Trigger character data recreation by creating a temporary handler and sending CreateCacheForObjectMessage
            try
            {
                var tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => _dalamudUtil.GetPlayerPtr(), isWatched: false).ConfigureAwait(false);
                Mediator.Publish(new CreateCacheForObjectMessage(tempHandler));
                tempHandler.Dispose();
                Logger.LogDebug("{tag} Reload trigger complete", SyncProgressTag);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{tag} Reload trigger failed", SyncProgressTag);
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
