using Sphene.API.Data;
using Sphene.API.Data.Comparer;
using Sphene.API.Data.Enum;
using Sphene.FileCache;
using Sphene.PlayerData.Factories;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.Files;
using Sphene.Interop.Ipc;
using Microsoft.Extensions.Logging;
using Sphene.PlayerData.Factories;

namespace Sphene.PlayerData.Pairs;

public class VisibleUserDataDistributor : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileUploadManager _fileTransferManager;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SessionAcknowledgmentManager _sessionAcknowledgmentManager;
    private readonly IpcCallerPenumbra _ipcCallerPenumbra;
    private readonly PlayerDataFactory _playerDataFactory;
    private readonly FileCacheManager _fileCacheManager;
    private CharacterData? _lastCreatedData;
    private CharacterData? _uploadingCharacterData = null;
    private readonly List<UserData> _previouslyVisiblePlayers = [];
    private Task<CharacterData>? _fileUploadTask = null;
    private readonly HashSet<UserData> _usersToPushDataTo = [];
    private readonly SemaphoreSlim _pushDataSemaphore = new(1, 1);
    private readonly CancellationTokenSource _runtimeCts = new();
    
    // Hash-based acknowledgment tracking
    private readonly Dictionary<UserData, string> _lastSentHashPerUser = new(UserDataComparer.Instance);
    private readonly Dictionary<UserData, string> _lastReceivedHashPerUser = new(UserDataComparer.Instance);
    private readonly Dictionary<UserData, DateTime> _lastSentTimePerUser = new(UserDataComparer.Instance);
    private readonly Dictionary<UserData, DateTime> _lastReceivedTimePerUser = new(UserDataComparer.Instance);
    private readonly Dictionary<string, DateTime> _userHashLastUpdate = new();
    
    // Track which hashes have been seen before for first-time detection
    private readonly Dictionary<UserData, HashSet<string>> _seenHashesPerUser = new(UserDataComparer.Instance);
    
    // Track users waiting for character reload confirmation after first hash reception
    private readonly Dictionary<UserData, PendingFirstHashInfo> _pendingFirstHashReloads = new(UserDataComparer.Instance);

    
    // Delayed push tracking for newly connected users
    private readonly Dictionary<UserData, DateTime> _delayedPushUsers = new();
    private readonly Dictionary<UserData, DateTime> _delayedReloadUsers = new();
    private readonly Timer _delayedPushTimer;
    private readonly Timer _characterReloadTimer;
    private const int DELAYED_PUSH_SECONDS = 3;
    private const int CHARACTER_RELOAD_DELAY_SECONDS = 3;


    public VisibleUserDataDistributor(ILogger<VisibleUserDataDistributor> logger, ApiController apiController, DalamudUtilService dalamudUtil,
        PairManager pairManager, SpheneMediator mediator, FileUploadManager fileTransferManager, SessionAcknowledgmentManager sessionAcknowledgmentManager,
        GameObjectHandlerFactory gameObjectHandlerFactory, IpcCallerPenumbra ipcCallerPenumbra, DalamudUtilService dalamudUtilService, PlayerDataFactory playerDataFactory,
        FileCacheManager fileCacheManager) : base(logger, mediator)
    {
        _apiController = apiController;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _fileTransferManager = fileTransferManager;
         _pairManager = pairManager;
        _sessionAcknowledgmentManager = sessionAcknowledgmentManager;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _ipcCallerPenumbra = ipcCallerPenumbra;
        _dalamudUtilService = dalamudUtil;
        _playerDataFactory = playerDataFactory;
        _fileCacheManager = fileCacheManager;
        

        
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
            if (previousHash != newHash || _usersToPushDataTo.Count > 0)
            {
                // Clear acknowledgment ID cache when character data changes to ensure new IDs are generated
                if (previousHash != newHash)
                {
                    _sessionAcknowledgmentManager.ClearCharacterDataCache();
                    Logger.LogInformation("Character data hash changed from {oldHash} to {newHash}, cleared acknowledgment ID cache", 
                        previousHash ?? "null", newHash ?? "null");
                }
                
                Logger.LogInformation("Character data hash changed from {oldHash} to {newHash}, pushing to users", 
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
            _lastSentTimePerUser.Clear();
            _lastReceivedHashPerUser.Clear();
            _lastReceivedTimePerUser.Clear();
            _userHashLastUpdate.Clear();
            _seenHashesPerUser.Clear();
            _pendingFirstHashReloads.Clear();
            _sessionAcknowledgmentManager.ClearCharacterDataCache();
            Logger.LogInformation("Cleared all hash tracking data and acknowledgment cache due to disconnection");
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

    // Check if another player can see the current player by examining their object table
    private async Task<bool> CanPlayerSeeCurrentPlayerAsync(UserData userData)
    {
        try
        {
            var pair = _pairManager.GetPairForUser(userData);
            if (pair == null || !pair.HasCachedPlayer)
            {
                return false;
            }
            
            var currentPlayerAddress = await _dalamudUtilService.RunOnFrameworkThread(() => 
                _dalamudUtilService.GetPlayerPtr()).ConfigureAwait(false);
            
            if (currentPlayerAddress == IntPtr.Zero)
            {
                return false;
            }
            
            // Check if the current player is in the other player's object table
            // This indicates bidirectional visibility
            var isVisible = await _dalamudUtilService.RunOnFrameworkThread(() => 
                _dalamudUtilService.IsGameObjectPresent(currentPlayerAddress)).ConfigureAwait(false);
            
            Logger.LogTrace("Bidirectional visibility check for {user}: {visible}", 
                userData.AliasOrUID, isVisible);
            
            return isVisible;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error checking bidirectional visibility for user {user}", userData.AliasOrUID);
            return false;
        }
    }

    private void PushToAllVisibleUsers(bool forced = false)
    {
        var currentHash = _lastCreatedData?.DataHash.Value;
        if (string.IsNullOrEmpty(currentHash)) return;
        
        foreach (var user in _pairManager.GetVisibleUsers())
        {
            // Only add users who haven't received this hash yet or if forced
            _lastSentHashPerUser.TryGetValue(user, out var lastHash);
            if (forced || lastHash == null || !string.Equals(lastHash, currentHash, StringComparison.Ordinal))
            {
                // Check bidirectional visibility and file cache before adding to push queue
                Task.Run(async () =>
                {
                    var canRecipientSeeUs = await CanPlayerSeeCurrentPlayerAsync(user).ConfigureAwait(false);
                    if (canRecipientSeeUs || forced)
                    {
                        // Check if this hash is already known in file cache to prevent duplicate acknowledgment requests
                        var isHashKnown = _fileCacheManager.IsHashKnown(currentHash);
                        
                        if (!isHashKnown || forced)
                        {
                            _usersToPushDataTo.Add(user);
                            Logger.LogTrace("Adding user {user} to push queue - LastHash: {lastHash}, CurrentHash: {currentHash}, Forced: {forced}, BidirectionalVisible: {bidirectional}, HashKnown: {hashKnown}", 
                                user.AliasOrUID, lastHash ?? "NONE", currentHash, forced, canRecipientSeeUs, isHashKnown);
                        }
                        else
                        {
                            Logger.LogTrace("Skipping user {user} - hash {hash} is already known in file cache", user.AliasOrUID, currentHash.Substring(0, Math.Min(8, currentHash.Length)));
                        }
                    }
                    else
                    {
                        Logger.LogTrace("Skipping user {user} - recipient cannot see sender (asymmetric visibility)", user.AliasOrUID);
                    }
                });
            }
            else
            {
                Logger.LogTrace("Skipping user {user} - already has hash {hash}", user.AliasOrUID, currentHash);
            }
        }

        // Delay the push to allow async visibility checks to complete
        Task.Delay(100).ContinueWith(_ =>
        {
            if (_usersToPushDataTo.Count > 0)
            {
                Logger.LogDebug("Pushing data {hash} for {count} visible players (out of {total} total)", 
                    currentHash, _usersToPushDataTo.Count, _pairManager.GetVisibleUsers().Count());
                PushCharacterData(forced);
            }
            else
            {
                Logger.LogTrace("No users need data push - all have current hash {hash}", currentHash);
            }
        });
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
                    
                    // Generate separate acknowledgment ID for each user with character data hash
                    var userAcknowledgmentIds = new Dictionary<UserData, string>();
                    foreach (var user in _usersToPushDataTo)
                    {
                        var acknowledgmentId = _sessionAcknowledgmentManager.GenerateAcknowledgmentId(dataToSend.DataHash.Value, user);
                        userAcknowledgmentIds[user] = acknowledgmentId;
                        _pairManager.SetPendingAcknowledgmentForSender([user], acknowledgmentId);
                    }
                    
                    // Track the hash sent to each user and send data with individual acknowledgment IDs
                    var currentHash = dataToSend.DataHash.Value;
                    var now = DateTime.UtcNow;
                    foreach (var user in _usersToPushDataTo)
                    {
                        _lastSentHashPerUser[user] = currentHash;
                        _lastSentTimePerUser[user] = now;
                        _userHashLastUpdate[user.UID] = now;
                        Logger.LogTrace("Updated hash tracking for user {user}: {hash}", user.AliasOrUID, currentHash);
                    }
                    
                    // Send data to each user with their individual acknowledgment ID
                    foreach (var kvp in userAcknowledgmentIds)
                    {
                        Logger.LogDebug("Pushing {data} to user {user} with acknowledgment ID {ackId}", dataToSend.DataHash, kvp.Key.AliasOrUID, kvp.Value);
                        await _apiController.PushCharacterData(dataToSend, [kvp.Key], kvp.Value).ConfigureAwait(false);
                    }
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
                var tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => _dalamudUtil.GetPlayerPtr(), isWatched: false);
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
    
    /// Gets the last sent time for a user's hash
    
    public DateTime? GetLastSentTimeForUser(UserData user)
    {
        return _lastSentTimePerUser.TryGetValue(user, out var time) ? time : null;
    }
    
    /// Gets all currently tracked character data hashes for all users
    
    public Dictionary<UserData, string> GetAllTrackedCharacterHashes()
    {
        return new Dictionary<UserData, string>(_lastSentHashPerUser);
    }
    
    /// Gets all users that currently have a specific character data hash
    
    public List<UserData> GetUsersWithHash(string characterDataHash)
    {
        return _lastSentHashPerUser
            .Where(kvp => string.Equals(kvp.Value, characterDataHash, StringComparison.Ordinal))
            .Select(kvp => kvp.Key)
            .ToList();
    }
    
    /// Gets the current character data hash that this user is sending to others
    
    public string? GetMyCurrentCharacterHash()
    {
        return _lastCreatedData?.DataHash.Value;
    }
    
    /// Checks if the received hash from a user matches their currently active Penumbra collection
    
    public async Task<bool> IsReceivedHashMatchingActiveCollectionAsync(UserData userData)
    {
        try
        {
            var receivedHash = GetLastReceivedHashForUser(userData);
            if (string.IsNullOrEmpty(receivedHash))
            {
                Logger.LogDebug("No received hash available for user {user}", userData.AliasOrUID);
                return false;
            }
            
            // Get the current collection for this user
            var collectionData = await GetPenumbraCollectionForUserAsync(userData).ConfigureAwait(false);
            if (!collectionData.HasValue || !collectionData.Value.ObjectValid)
            {
                Logger.LogDebug("No valid Penumbra collection found for user {user}", userData.AliasOrUID);
                return false;
            }
            
            // Generate hash for the current collection state
            var currentCollectionHash = await GenerateHashForUserCollectionAsync(userData).ConfigureAwait(false);
            if (string.IsNullOrEmpty(currentCollectionHash))
            {
                Logger.LogDebug("Could not generate hash for current collection of user {user}", userData.AliasOrUID);
                return false;
            }
            
            var isMatching = receivedHash.Equals(currentCollectionHash, StringComparison.Ordinal);
            Logger.LogDebug("Hash comparison for user {user}: received={receivedHash}, current={currentHash}, matching={matching}", 
                userData.AliasOrUID, receivedHash[..Math.Min(8, receivedHash.Length)], 
                currentCollectionHash[..Math.Min(8, currentCollectionHash.Length)], isMatching);
            
            return isMatching;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error checking if received hash matches active collection for user {user}", userData.AliasOrUID);
            return false;
        }
    }
    
    /// Generates a hash for the current collection state of a user
    
    private async Task<string?> GenerateHashForUserCollectionAsync(UserData userData)
    {
        try
        {
            var pair = _pairManager.GetPairForUser(userData);
            if (pair == null || !pair.HasCachedPlayer)
            {
                return null;
            }
            
            var characterAddress = await _dalamudUtilService.RunOnFrameworkThread(() => 
                _dalamudUtilService.GetPlayerCharacterFromCachedTableByIdent(pair.PlayerName)).ConfigureAwait(false);
            
            using var gameObjectHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => characterAddress, isWatched: false).ConfigureAwait(false);
            
            if (gameObjectHandler?.GetGameObject() == null)
            {
                return null;
            }
            
            // Create character data using the same pattern as CharaDataFileHandler
            PlayerData.Data.CharacterData newCdata = new();
            var fragment = await _playerDataFactory.BuildCharacterData(gameObjectHandler, CancellationToken.None).ConfigureAwait(false);
            
            if (fragment == null)
                return null;
                
            newCdata.SetFragment(ObjectKind.Player, fragment);
            return newCdata.ToAPI()?.DataHash?.Value;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error generating hash for user collection {user}", userData.AliasOrUID);
            return null;
        }
    }
    
    /// Gets the current Penumbra collection assigned to a specific user's character
    
    public async Task<(bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)?> GetPenumbraCollectionForUserAsync(UserData userData)
    {
        try
        {
            return await _dalamudUtilService.RunOnFrameworkThread(() =>
            {
                var pair = _pairManager.GetPairForUser(userData);
                if (pair == null || !pair.HasCachedPlayer)
                {
                    return ((bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)?)null;
                }
                
                var characterAddress = _dalamudUtilService.GetPlayerCharacterFromCachedTableByIdent(pair.PlayerName);
                
                var gameObjectHandler = _gameObjectHandlerFactory.Create(ObjectKind.Player, () => characterAddress, isWatched: false).GetAwaiter().GetResult();
                if (gameObjectHandler?.GetGameObject() == null)
                {
                    return ((bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)?)null;
                }
                
                var result = _ipcCallerPenumbra.GetCollectionForObjectAsync(Logger, gameObjectHandler).GetAwaiter().GetResult();
                if (result.HasValue)
                {
                    Logger.LogDebug("Penumbra collection for user {user}: {collectionName} (ID: {collectionId}, Valid: {valid}, Individual: {individual})", 
                        userData.AliasOrUID, result.Value.EffectiveCollection.Name, result.Value.EffectiveCollection.Id, 
                        result.Value.ObjectValid, result.Value.IndividualSet);
                }
                else
                {
                    Logger.LogDebug("No Penumbra collection data available for user {user}", userData.AliasOrUID);
                }
                
                return result;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting Penumbra collection for user {user}", userData.AliasOrUID);
            return null;
        }
    }
    
    /// Gets the character data hash for a specific user by UID
    
    public string? GetUserCharacterHash(string userUID)
    {
        var user = _lastSentHashPerUser.Keys.FirstOrDefault(u => u.UID == userUID);
        return user != null ? _lastSentHashPerUser[user] : null;
    }
    
    /// Gets the last update time for a user's hash
    
    public DateTime? GetUserHashLastUpdate(string userUID)
    {
        return _userHashLastUpdate.TryGetValue(userUID, out var lastUpdate) ? lastUpdate : null;
    }
    

    
    /// Clears the hash cache for a specific user
    
    public void ClearUserHashCache(string userUID)
    {
        var user = _lastSentHashPerUser.Keys.FirstOrDefault(u => u.UID == userUID);
        if (user != null)
        {
            _lastSentHashPerUser.Remove(user);
            _lastSentTimePerUser.Remove(user);
            _lastReceivedHashPerUser.Remove(user);
            _lastReceivedTimePerUser.Remove(user);
            _userHashLastUpdate.Remove(userUID);
            _seenHashesPerUser.Remove(user);
            _pendingFirstHashReloads.Remove(user);
            Logger.LogDebug("Cleared hash cache for user {userUID} including seen hashes and pending reload confirmations", userUID);
        }
    }
    
    /// Clears all user hash caches
    
    public void ClearAllUserHashCaches()
    {
        _lastSentHashPerUser.Clear();
        _lastSentTimePerUser.Clear();
        _lastReceivedHashPerUser.Clear();
        _lastReceivedTimePerUser.Clear();
        _userHashLastUpdate.Clear();
        _seenHashesPerUser.Clear();
        _pendingFirstHashReloads.Clear();
        Logger.LogInformation("User hash cache cleared including seen hashes and pending reload confirmations");
    }
    
    /// Updates the received hash for a user and detects first-time reception
    
    public bool UpdateReceivedHashForUser(UserData user, string hash)
    {
        // Check if this is the first time we've seen this hash from this user
        var isFirstTime = !HasSeenHashBefore(user, hash);
        
        // Update tracking data
        _lastReceivedHashPerUser[user] = hash;
        _lastReceivedTimePerUser[user] = DateTime.UtcNow;
        _userHashLastUpdate[user.UID] = DateTime.UtcNow;
        
        // Add hash to seen hashes for this user
        if (!_seenHashesPerUser.TryGetValue(user, out var seenHashes))
        {
            seenHashes = new HashSet<string>();
            _seenHashesPerUser[user] = seenHashes;
        }
        seenHashes.Add(hash);
        
        if (isFirstTime)
        {
            Logger.LogInformation("First-time hash reception from user {user}: {hash} - character reload will be triggered", 
                user.AliasOrUID, hash[..Math.Min(8, hash.Length)]);
        }
        else
        {
            Logger.LogDebug("Updated received hash for user {user}: {hash} (seen before)", user.AliasOrUID, hash);
        }
        
        return isFirstTime;
    }
    
    /// Gets the last received hash for a user
    
    public string? GetLastReceivedHashForUser(UserData user)
    {
        return _lastReceivedHashPerUser.TryGetValue(user, out var hash) ? hash : null;
    }
    
    /// Gets all currently tracked received character data hashes for all users
    
    public Dictionary<UserData, string> GetAllTrackedReceivedCharacterHashes()
    {
        return new Dictionary<UserData, string>(_lastReceivedHashPerUser);
    }
    
    /// Gets the last received time for a user's hash
    
    public DateTime? GetLastReceivedTimeForUser(UserData user)
    {
        return _lastReceivedTimePerUser.TryGetValue(user, out var time) ? time : null;
    }
    
    /// Checks if a hash has been seen before for a user
    
    public bool HasSeenHashBefore(UserData user, string hash)
    {
        return _seenHashesPerUser.TryGetValue(user, out var seenHashes) && seenHashes.Contains(hash);
    }
    
    /// Marks a user as waiting for character reload confirmation after first hash reception
    
    public void MarkUserWaitingForReloadConfirmation(UserData user, string hash, string acknowledgmentId)
    {
        _pendingFirstHashReloads[user] = new PendingFirstHashInfo(hash, acknowledgmentId, DateTime.UtcNow);
        Logger.LogInformation("Marked user {user} as waiting for reload confirmation for first-time hash {hash} with AckId {ackId}", 
            user.AliasOrUID, hash[..Math.Min(8, hash.Length)], acknowledgmentId);
    }
    
    /// Checks if a user is waiting for reload confirmation and returns the pending info
    
    public PendingFirstHashInfo? GetPendingReloadConfirmation(UserData user)
    {
        return _pendingFirstHashReloads.TryGetValue(user, out var info) ? info : null;
    }
    
    /// Clears the pending reload confirmation for a user
    
    public void ClearPendingReloadConfirmation(UserData user)
    {
        if (_pendingFirstHashReloads.Remove(user))
        {
            Logger.LogInformation("Cleared pending reload confirmation for user {user}", user.AliasOrUID);
        }
    }
}

/// Information about a pending first hash reload

public class PendingFirstHashInfo
{
    public string Hash { get; }
    public string AcknowledgmentId { get; }
    public DateTime ReceivedAt { get; }
    
    public PendingFirstHashInfo(string hash, string acknowledgmentId, DateTime receivedAt)
    {
        Hash = hash;
        AcknowledgmentId = acknowledgmentId;
        ReceivedAt = receivedAt;
    }
}
