using Sphene.API.Data;
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
    private readonly Dictionary<UserData, DateTime> _lastSilentAckPerUser = new();
    private readonly Timer _silentAcknowledgmentTimer;
    private const int SILENT_ACK_INTERVAL_MINUTES = 1;


    public VisibleUserDataDistributor(ILogger<VisibleUserDataDistributor> logger, ApiController apiController, DalamudUtilService dalamudUtil,
        PairManager pairManager, SpheneMediator mediator, FileUploadManager fileTransferManager, SessionAcknowledgmentManager sessionAcknowledgmentManager) : base(logger, mediator)
    {
        _apiController = apiController;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _fileTransferManager = fileTransferManager;
        _sessionAcknowledgmentManager = sessionAcknowledgmentManager;
        
        // Initialize silent acknowledgment timer
        _silentAcknowledgmentTimer = new Timer(SendSilentAcknowledgments, null, 
            TimeSpan.FromMinutes(SILENT_ACK_INTERVAL_MINUTES), 
            TimeSpan.FromMinutes(SILENT_ACK_INTERVAL_MINUTES));
        
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => FrameworkOnUpdate());
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            var previousHash = _lastCreatedData?.DataHash?.Value;
            var newHash = msg.CharacterData.DataHash?.Value;
            
            _lastCreatedData = msg.CharacterData;
            
            // Only push if hash actually changed or if we have users waiting for data
            if (previousHash != newHash || _usersToPushDataTo.Count > 0)
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
            _lastSilentAckPerUser.Clear();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runtimeCts.Cancel();
            _runtimeCts.Dispose();
            _silentAcknowledgmentTimer?.Dispose();
        }

        base.Dispose(disposing);
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
                currentHash, _usersToPushDataTo.Count, _pairManager.GetVisibleUsers().Count());
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

        Logger.LogDebug("New users entered view: {users}. Scheduling data push check.",
            string.Join(", ", newVisibleUsers.Select(k => k.AliasOrUID)));
        
        // Add new users to push queue - they will get data when CharacterDataCreatedMessage fires
        // or immediately if we have current data
        foreach (var user in newVisibleUsers)
        {
            _usersToPushDataTo.Add(user);
        }
        
        // Only push immediately if we have data to push
        if (_lastCreatedData != null)
        {
            Logger.LogDebug("Pushing existing character data {hash} to new users",
                _lastCreatedData.DataHash?.Value ?? "null");
            PushCharacterData();
        }
        else
        {
            Logger.LogDebug("No character data available yet, users will receive data when it's created");
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
                    
                    // Generate session-aware acknowledgment ID and set pending status for all recipients from sender's perspective
                    var acknowledgmentId = _sessionAcknowledgmentManager.GenerateAcknowledgmentId();
                    _pairManager.SetPendingAcknowledgmentForSender([.. _usersToPushDataTo], acknowledgmentId);
                    
                    // Track the hash sent to each user
                    var currentHash = dataToSend.DataHash.Value;
                    foreach (var user in _usersToPushDataTo)
                    {
                        _lastSentHashPerUser[user] = currentHash;
                        _lastSilentAckPerUser[user] = DateTime.UtcNow;
                        Logger.LogTrace("Updated hash tracking for user {user}: {hash}", user.AliasOrUID, currentHash);
                    }
                    
                    Logger.LogDebug("Pushing {data} to {users} with acknowledgment ID {ackId}", dataToSend.DataHash, string.Join(", ", _usersToPushDataTo.Select(k => k.AliasOrUID)), acknowledgmentId);
                    await _apiController.PushCharacterData(dataToSend, [.. _usersToPushDataTo], acknowledgmentId).ConfigureAwait(false);
                    _usersToPushDataTo.Clear();
                }
                finally
                {
                    _pushDataSemaphore.Release();
                }
            }
        });
    }
    
    
    /// Sends silent acknowledgments for users who have the same hash for more than 1 minute
    /// This maintains connection health without unnecessary data transfers
    
    private void SendSilentAcknowledgments(object? state)
    {
        // Schedule the actual work on the framework thread to avoid threading issues
        _ = Task.Run(() =>
        {
            try
            {
                _dalamudUtil.RunOnFrameworkThread(() =>
                {
                    ProcessSilentAcknowledgments();
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to schedule silent acknowledgments on framework thread");
            }
        });
    }
    
    
    /// Processes silent acknowledgments on the framework thread
    
    private void ProcessSilentAcknowledgments()
    {
        if (!_dalamudUtil.GetIsPlayerPresent() || !_apiController.IsConnected || _lastCreatedData == null)
            return;
            
        var currentTime = DateTime.UtcNow;
        var currentHash = _lastCreatedData.DataHash.Value;
        var usersForSilentAck = new List<UserData>();
        
        foreach (var user in _pairManager.GetVisibleUsers())
        {
            // Check if user has the current hash and hasn't received a silent ack recently
            if (_lastSentHashPerUser.TryGetValue(user, out var userHash) && 
                string.Equals(userHash, currentHash, StringComparison.Ordinal))
            {
                if (_lastSilentAckPerUser.TryGetValue(user, out var lastSilentAck))
                {
                    var timeSinceLastAck = currentTime - lastSilentAck;
                    if (timeSinceLastAck.TotalMinutes >= SILENT_ACK_INTERVAL_MINUTES)
                    {
                        usersForSilentAck.Add(user);
                    }
                }
            }
        }
        
        if (usersForSilentAck.Count > 0)
        {
            Logger.LogTrace("Sending silent acknowledgment to {count} users with hash {hash}", 
                usersForSilentAck.Count, currentHash);
                
            _ = Task.Run(async () =>
            {
                try
                {
                    // Send silent acknowledgment without requiring response
                    await _apiController.PushCharacterData(_lastCreatedData, usersForSilentAck, null).ConfigureAwait(false);
                    
                    // Update silent ack timestamps
                    foreach (var user in usersForSilentAck)
                    {
                        _lastSilentAckPerUser[user] = currentTime;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to send silent acknowledgments");
                }
            });
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
