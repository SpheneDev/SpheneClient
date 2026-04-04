using Sphene.API.Data;
using Sphene.API.Dto.User;
using Sphene.SpheneConfiguration;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.Files;
using Sphene.API.Data.Comparer;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Sphene.PlayerData.Pairs;

public class VisibleUserDataDistributor : DisposableMediatorSubscriberBase
{
    private const string SyncProgressTag = "[SyncProgress]";
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileUploadManager _fileTransferManager;
    private readonly PairManager _pairManager;
    private readonly SpheneConfigService _configService;
    private readonly SessionAcknowledgmentManager _sessionAcknowledgmentManager;
    private CharacterData? _lastCreatedData;
    private CharacterData? _uploadingCharacterData = null;
    private readonly HashSet<UserData> _previouslyVisiblePlayers = new(UserDataComparer.Instance);
    private Task<CharacterData>? _fileUploadTask = null;
    private readonly HashSet<UserData> _usersToPushDataTo = new(UserDataComparer.Instance);
    private readonly SemaphoreSlim _pushDataSemaphore = new(1, 1);
    private readonly CancellationTokenSource _runtimeCts = new();
    
    // Hash-based acknowledgment tracking
    private readonly ConcurrentDictionary<UserData, string> _lastSentHashPerUser = new(UserDataComparer.Instance);
    
    // Delayed push tracking for newly connected users
    private readonly Dictionary<UserData, DateTime> _delayedPushUsers = new(UserDataComparer.Instance);
    private readonly Timer _delayedPushTimer;
    private DateTime _nextOutgoingBatchPushUtc = DateTime.MinValue;
    private bool _hasPendingOutgoingBatchPush = false;
    private readonly ConcurrentDictionary<string, DateTime> _refreshRequestCooldownByUid = new(StringComparer.Ordinal);
    
    private const int DELAYED_PUSH_SECONDS = 3;
    private const int REFRESH_REQUEST_COOLDOWN_SECONDS = 15;


    public VisibleUserDataDistributor(ILogger<VisibleUserDataDistributor> logger, ApiController apiController, DalamudUtilService dalamudUtil,
        PairManager pairManager, SpheneMediator mediator, FileUploadManager fileTransferManager, SessionAcknowledgmentManager sessionAcknowledgmentManager,
        SpheneConfigService configService) : base(logger, mediator)
    {
        _apiController = apiController;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _fileTransferManager = fileTransferManager;
        _sessionAcknowledgmentManager = sessionAcknowledgmentManager;
        _configService = configService;
        

        
        // Initialize delayed push timer for newly connected users
        _delayedPushTimer = new Timer(ProcessDelayedPushes, null, 
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        
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
                EnsureServerWarmUpload();
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
            _refreshRequestCooldownByUid.Clear();
            _delayedPushUsers.Clear();
            _usersToPushDataTo.Clear();
        });
        Mediator.Subscribe<CharacterDataRefreshRequestedMessage>(this, msg =>
        {
            RequestImmediatePushToUser(msg.Requester);
        });
    }

    private bool IsDutyCombatOutgoingBatchingActive()
        => _configService.Current.EnableDutyCombatOutgoingSyncBatching
           && (_dalamudUtil.IsInCombatOrPerforming || _dalamudUtil.IsInDuty);

    private int GetDutyCombatOutgoingBatchSeconds()
    {
        var seconds = _configService.Current.DutyCombatOutgoingSyncBatchSeconds;
        return Math.Clamp(seconds, 1, 60);
    }

    private void EnsureServerWarmUpload()
    {
        if (!_apiController.IsConnected || _lastCreatedData == null)
            return;

        var currentHash = _lastCreatedData.DataHash?.Value ?? string.Empty;
        if (string.IsNullOrEmpty(currentHash))
            return;

        if (string.Equals(_uploadingCharacterData?.DataHash?.Value, currentHash, StringComparison.Ordinal)
            && _fileUploadTask != null && !_fileUploadTask.IsCompleted)
            return;

        _uploadingCharacterData = _lastCreatedData.DeepClone();
        Logger.LogDebug("{tag} Warm upload start: hash={hash}", SyncProgressTag, currentHash);
        _fileUploadTask = _fileTransferManager.UploadFiles(_uploadingCharacterData, []);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runtimeCts.Cancel();
            _runtimeCts.Dispose();
            _delayedPushTimer?.Dispose();
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
            // Only add users who haven't received this hash yet, if forced, or if we're still waiting for an ack
            _lastSentHashPerUser.TryGetValue(user, out var lastHash);
            var hasPendingAck = _pairManager.HasPendingAcknowledgmentForUser(user);
            if (forced || hasPendingAck || lastHash == null || !string.Equals(lastHash, currentHash, StringComparison.Ordinal))
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
        var newVisibleUsers = allVisibleUsers.Except(_previouslyVisiblePlayers, UserDataComparer.Instance).ToList();
        _previouslyVisiblePlayers.Clear();
        foreach (var u in allVisibleUsers) _previouslyVisiblePlayers.Add(u);
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
            RequestRemoteCharacterDataRefreshIfNeeded(user);
        }
    }

    private void RequestRemoteCharacterDataRefreshIfNeeded(UserData user)
    {
        if (!_apiController.IsConnected) return;
        if (string.IsNullOrWhiteSpace(user.UID)) return;

        var now = DateTime.UtcNow;
        if (_refreshRequestCooldownByUid.TryGetValue(user.UID, out var lastRequest)
            && (now - lastRequest).TotalSeconds < REFRESH_REQUEST_COOLDOWN_SECONDS)
        {
            return;
        }

        var pair = _pairManager.GetPairByUID(user.UID);
        if (pair?.LastReceivedCharacterDataTime != null && (now - pair.LastReceivedCharacterDataTime.Value.UtcDateTime).TotalSeconds < 10)
        {
            return;
        }

        _refreshRequestCooldownByUid[user.UID] = now;
        Logger.LogDebug("{tag} Refresh request: requester=self target={user}", SyncProgressTag, user.AliasOrUID);
        Mediator.Publish(new DebugLogEventMessage(LogLevel.Debug, "APPLY", "Requested remote character data refresh", Uid: user.UID));
        _ = _apiController.UserRequestCharacterDataRefresh(new UserDto(user));
    }

    private void PushCharacterData(bool forced = false, bool bypassBatching = false)
    {
        if (_lastCreatedData == null || _usersToPushDataTo.Count == 0) return;

        if (!bypassBatching && IsDutyCombatOutgoingBatchingActive())
        {
            var batchSeconds = GetDutyCombatOutgoingBatchSeconds();
            _hasPendingOutgoingBatchPush = true;
            _nextOutgoingBatchPushUtc = DateTime.UtcNow.AddSeconds(batchSeconds);
            Logger.LogDebug("{tag} Outgoing batch queued: hash={hash} users={count} flushIn={seconds}s",
                SyncProgressTag, _lastCreatedData.DataHash?.Value ?? "null", _usersToPushDataTo.Count, batchSeconds);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                forced |= _uploadingCharacterData?.DataHash != _lastCreatedData.DataHash;

                if (_fileUploadTask == null || (_fileUploadTask?.IsCompleted ?? false) || forced)
                {
                    _uploadingCharacterData = _lastCreatedData.DeepClone();
                    Logger.LogDebug("{tag} Upload start: hash={hash} taskNull={task} taskCompleted={taskCpl} forced={frc}",
                        SyncProgressTag, _lastCreatedData.DataHash, _fileUploadTask == null, _fileUploadTask?.IsCompleted ?? false, forced);
                    _fileUploadTask = _fileTransferManager.UploadFiles(_uploadingCharacterData, [.. _usersToPushDataTo]);
                }

                if (_fileUploadTask == null)
                {
                    return;
                }

                var dataToSend = await _fileUploadTask.ConfigureAwait(false);
                Logger.LogDebug("{tag} Upload complete: hash={hash} users={users}", SyncProgressTag, dataToSend.DataHash.Value, string.Join(", ", _usersToPushDataTo.Select(u => u.AliasOrUID)));
                await _pushDataSemaphore.WaitAsync(_runtimeCts.Token).ConfigureAwait(false);
                try
                {
                    if (_usersToPushDataTo.Count == 0) return;

                    var currentVisible = new HashSet<UserData>(_pairManager.GetVisibleUsers(), UserDataComparer.Instance);
                    var usersToSend = new List<UserData>();
                    var currentHash = dataToSend.DataHash.Value;

                    foreach (var user in _usersToPushDataTo.ToList())
                    {
                        if (!currentVisible.Contains(user))
                        {
                            _usersToPushDataTo.Remove(user);
                            continue;
                        }

                        var hasPendingAck = _pairManager.HasPendingAcknowledgmentForUser(user);
                        if (!forced && !hasPendingAck && _lastSentHashPerUser.TryGetValue(user, out var lastSentHash)
                            && string.Equals(lastSentHash, currentHash, StringComparison.Ordinal))
                        {
                            _usersToPushDataTo.Remove(user);
                            continue;
                        }

                        usersToSend.Add(user);
                    }

                    if (usersToSend.Count == 0)
                    {
                        Logger.LogDebug("{tag} Push skip: no users need data", SyncProgressTag);
                        return;
                    }

                    var hashKey = currentHash;
                    var requiresAck = usersToSend.Any(u =>
                        forced
                        || _pairManager.HasPendingAcknowledgmentForUser(u)
                        || !_lastSentHashPerUser.TryGetValue(u, out var lastSentHash)
                        || !string.Equals(lastSentHash, currentHash, StringComparison.Ordinal));

                    if (requiresAck)
                    {
                        Logger.LogDebug("{tag} Ack pending before push: hash={hash}", SyncProgressTag, hashKey);
                        _pairManager.SetPendingAcknowledgmentForSender([.. usersToSend], hashKey);
                        _sessionAcknowledgmentManager.SetPendingAcknowledgmentForHashVersion([.. usersToSend], hashKey);
                    }
                    Mediator.Publish(new DebugLogEventMessage(
                        LogLevel.Debug,
                        "ACK",
                        "Ack outgoing push",
                        Details: $"hash={hashKey[..Math.Min(8, hashKey.Length)]} requiresAck={requiresAck} recipients={usersToSend.Count}"));

                    Logger.LogDebug("{tag} Push send: hash={hash} ackKey={hashKey} requiresAck={requiresAck} users={users}", SyncProgressTag, dataToSend.DataHash, hashKey, requiresAck, string.Join(", ", usersToSend.Select(k => k.AliasOrUID)));
                    await _apiController.PushCharacterData(dataToSend, [.. usersToSend], hashKey, requiresAck).ConfigureAwait(false);
                    Logger.LogDebug("{tag} Push complete: hash={hash} users={users}", SyncProgressTag, dataToSend.DataHash, string.Join(", ", usersToSend.Select(k => k.AliasOrUID)));

                    foreach (var user in usersToSend)
                    {
                        _lastSentHashPerUser[user] = currentHash;
                        _usersToPushDataTo.Remove(user);
                    }
                }
                finally
                {
                    _pushDataSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{tag} Push failed: keeping queue for retry", SyncProgressTag);
                Mediator.Publish(new DebugLogEventMessage(LogLevel.Warning, "APPLY", "Outgoing push failed (will retry)", Details: ex.ToString()));
                _hasPendingOutgoingBatchPush = true;
                _nextOutgoingBatchPushUtc = DateTime.UtcNow.AddSeconds(3);
            }
        });
    }

    private void RequestImmediatePushToUser(UserData user)
    {
        if (!_apiController.IsConnected)
        {
            return;
        }

        if (_lastCreatedData == null)
        {
            Logger.LogDebug("{tag} Refresh push ignored: no character data for requester={user}", SyncProgressTag, user.AliasOrUID);
            return;
        }

        _delayedPushUsers.Remove(user);
        _usersToPushDataTo.Add(user);
        Logger.LogDebug("{tag} Refresh push requested: requester={user} hash={hash}", SyncProgressTag, user.AliasOrUID, _lastCreatedData.DataHash?.Value ?? "null");
        EnsureServerWarmUpload();
        PushCharacterData(forced: true, bypassBatching: true);
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

        if (_hasPendingOutgoingBatchPush && _usersToPushDataTo.Count > 0 && DateTime.UtcNow >= _nextOutgoingBatchPushUtc)
        {
            _hasPendingOutgoingBatchPush = false;
            Logger.LogDebug("{tag} Outgoing batch flush: hash={hash} users={count}",
                SyncProgressTag, _lastCreatedData?.DataHash?.Value ?? "null", _usersToPushDataTo.Count);
            PushCharacterData(forced: true, bypassBatching: true);
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
