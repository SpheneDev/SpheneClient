using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Data.Comparer;
using Sphene.API.Dto.User;
using Sphene.Services.Mediator;
using Sphene.Services;
using Sphene.Services.Events;
using Sphene.SpheneConfiguration.Models;
using System.Collections.Concurrent;
using Dalamud.Interface.ImGuiNotification;
using NotificationType = Sphene.SpheneConfiguration.Models.NotificationType;

namespace Sphene.PlayerData.Pairs;

// Session-based acknowledgment manager for handling multiple concurrent users
public class SessionAcknowledgmentManager : DisposableMediatorSubscriberBase
{
    private readonly ILogger<SessionAcknowledgmentManager> _logger;
    private readonly Func<UserData, Pair?> _getPairFunc;
    private readonly MessageService _messageService;
    private readonly AcknowledgmentBatchingService _batchingService;
    
    // Thread-safe storage for latest acknowledgment per user pair with timestamps
    private readonly ConcurrentDictionary<string, LatestAcknowledgmentInfo> _userLatestAcknowledgments = new(StringComparer.Ordinal);
    
    // Thread-safe storage for recent acknowledgment history per user (max 5 per user) to handle out-of-order acknowledgments
    private readonly ConcurrentDictionary<string, List<string>> _userAcknowledgmentHistory = new(StringComparer.Ordinal);
    private const int MaxHistoryEntriesPerUser = 5;

    private long _resultTotal = 0;
    private long _resultSuccess = 0;
    private long _resultFail = 0;
    private long _resultResponseTotalMs = 0;
    private long _resultResponseCount = 0;
    private readonly ConcurrentDictionary<Sphene.API.Dto.User.AcknowledgmentErrorCode, long> _resultErrorCounts = new();

    public readonly record struct AckResultMetrics(long Total, long Success, long Fail, double AverageResponseTimeMs,
        IReadOnlyDictionary<Sphene.API.Dto.User.AcknowledgmentErrorCode, long> ErrorCounts);

    public AckResultMetrics GetResultMetrics()
    {
        var total = Interlocked.Read(ref _resultTotal);
        var success = Interlocked.Read(ref _resultSuccess);
        var fail = Interlocked.Read(ref _resultFail);
        var totalMs = Interlocked.Read(ref _resultResponseTotalMs);
        var count = Interlocked.Read(ref _resultResponseCount);
        var avg = count > 0 ? (double)totalMs / count : 0d;

        return new AckResultMetrics(total, success, fail, avg, new Dictionary<Sphene.API.Dto.User.AcknowledgmentErrorCode, long>(_resultErrorCounts));
    }
    
    // Helper class to store latest acknowledgment info per user
    private sealed class LatestAcknowledgmentInfo
    {
        public string AcknowledgmentId { get; set; }
        public DateTime CreatedAt { get; set; }
        
        public LatestAcknowledgmentInfo(string acknowledgmentId)
        {
            AcknowledgmentId = acknowledgmentId;
            CreatedAt = DateTime.UtcNow;
        }
    }
    
    // Add acknowledgment to user history (max 5 entries per user)
    private void AddToAcknowledgmentHistory(string userKey, string hashVersionKey)
    {
        _userAcknowledgmentHistory.AddOrUpdate(userKey,
            addKey => new List<string> { hashVersionKey },
            (key, existingList) =>
            {
                var newList = new List<string>(existingList);
                // Remove if already exists to move to end
                newList.Remove(hashVersionKey);
                // Add to end (most recent)
                newList.Add(hashVersionKey);
                // Keep only max entries
                if (newList.Count > MaxHistoryEntriesPerUser)
                {
                    newList.RemoveAt(0);
                }
                return newList;
            });
    }
    
    // Check if hash exists in user's acknowledgment history
    private bool IsHashInHistory(string userKey, string hashVersionKey)
    {
        if (_userAcknowledgmentHistory.TryGetValue(userKey, out var history))
        {
            return history.Contains(hashVersionKey, StringComparer.Ordinal);
        }
        return false;
    }
    
    // Remove hash from user's acknowledgment history
    private void RemoveFromHistory(string userKey, string hashVersionKey)
    {
        if (_userAcknowledgmentHistory.TryGetValue(userKey, out var history))
        {
            history.Remove(hashVersionKey);
        }
    }
    
    // Session counter for generating unique session IDs
    private static long _sessionCounter = 0;
    
    // Current session ID for this client instance
    private readonly string _currentSessionId;
    
    public SessionAcknowledgmentManager(ILogger<SessionAcknowledgmentManager> logger, SpheneMediator mediator, 
        Func<UserData, Pair?> getPairFunc, MessageService messageService, AcknowledgmentBatchingService batchingService) : base(logger, mediator)
    {
        _logger = logger;
        _getPairFunc = getPairFunc;
        _messageService = messageService;
        _batchingService = batchingService;
        _currentSessionId = GenerateSessionId();
        
        _logger.LogInformation("SessionAcknowledgmentManager initialized with session ID: {sessionId}", _currentSessionId);
    }
    
    // Generate unique session ID combining timestamp and counter
    private static string GenerateSessionId()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var counter = Interlocked.Increment(ref _sessionCounter);
        return $"session_{timestamp}_{counter}";
    }
    

    
    // Extract session ID from acknowledgment ID
    public static string? ExtractSessionId(string acknowledgmentId)
    {
        if (string.IsNullOrEmpty(acknowledgmentId)) return null;
        
        var parts = acknowledgmentId.Split('_');
        if (parts.Length >= 3 && string.Equals(parts[0], "session", StringComparison.Ordinal))
        {
            return $"{parts[0]}_{parts[1]}_{parts[2]}";
        }
        
        return null;
    }
    
    // Check if acknowledgment ID is valid hash+version format
    public static bool IsValidHashVersion(string hashVersionKey)
    {
        return !string.IsNullOrEmpty(hashVersionKey) && hashVersionKey.Contains("_", StringComparison.Ordinal);
    }
    
    // Set pending acknowledgment for hash-based system - only latest per user
    public void SetPendingAcknowledgmentForHashVersion(List<UserData> recipients, string hashVersionKey)
    {
        if (string.IsNullOrEmpty(hashVersionKey))
        {
            _logger.LogWarning("Invalid hash version key: {hashVersionKey}", hashVersionKey);
            return;
        }
        
        // Process each recipient individually - store only latest hash+version per user
        foreach (var recipient in recipients)
        {
            ProcessLatestAcknowledgment(recipient, hashVersionKey);
        }
    }
    
    // Process latest acknowledgment for a single recipient
    private void ProcessLatestAcknowledgment(UserData recipient, string hashVersionKey)
    {
        if (string.IsNullOrEmpty(hashVersionKey))
        {
            _logger.LogWarning("Invalid hash version key: {hashVersionKey}", hashVersionKey);
            return;
        }
        
        var userKey = recipient.UID;
        
        // If an older pending acknowledgment exists for this user, clear it before setting the new one
        if (_userLatestAcknowledgments.TryGetValue(userKey, out var existing) &&
            !string.Equals(existing.AcknowledgmentId, hashVersionKey, StringComparison.Ordinal))
        {
            try
            {
                var pair = _getPairFunc(recipient);
                if (pair != null && string.Equals(pair.LastAcknowledgmentId, existing.AcknowledgmentId, StringComparison.Ordinal))
                {
                    // Clear previous pending state on the pair
                    pair.ClearPendingAcknowledgmentForce(_messageService);
                    _logger.LogDebug("Cleared previous pending ack {oldAck} on pair for user {user}", existing.AcknowledgmentId, recipient.AliasOrUID);
                }

                // Clean up notifications tied to the previous acknowledgment
                _messageService.CleanTaggedMessages($"ack_{existing.AcknowledgmentId}");

                // Publish granular UI refresh for the old acknowledgment being cleared
                Mediator.Publish(new AcknowledgmentUiRefreshMessage(
                    AcknowledgmentId: existing.AcknowledgmentId,
                    User: recipient
                ));

                // Also publish legacy UI refresh for broader components
                Mediator.Publish(new RefreshUiMessage());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed clearing previous pending ack {oldAck} for user {user}", existing.AcknowledgmentId, recipient.AliasOrUID);
            }
        }
        
        // Store only the latest acknowledgment for this user
        var latestInfo = new LatestAcknowledgmentInfo(hashVersionKey);
        
        var oldInfo = _userLatestAcknowledgments.AddOrUpdate(
            userKey,
            latestInfo,
            (key, existing) => latestInfo);
        
        // Add to history before clearing previous to handle out-of-order acknowledgments
        AddToAcknowledgmentHistory(userKey, hashVersionKey);
        
        if (oldInfo != null && !string.Equals(oldInfo.AcknowledgmentId, hashVersionKey, StringComparison.Ordinal))
        {
            _logger.LogDebug("Replaced acknowledgment {oldHashVersion} with {newHashVersion} for user {user}", 
                oldInfo.AcknowledgmentId, hashVersionKey, userKey);
        }
        else
        {
            _logger.LogDebug("Added pending acknowledgment {hashVersionKey} for user {user}", 
                hashVersionKey, userKey);
        }
        
        // Add notification for pending acknowledgment
        _messageService.AddTaggedMessage(
            $"ack_{hashVersionKey}",
            $"Waiting for acknowledgment from {recipient.AliasOrUID}",
            NotificationType.Info,
            "Acknowledgment Pending",
            TimeSpan.FromSeconds(5)
        );
        
        // Publish acknowledgment pending event
        Mediator.Publish(new AcknowledgmentPendingMessage(
            new AcknowledgmentEventDto(
                hashVersionKey,
                recipient,
                AcknowledgmentStatus.Pending,
                DateTime.UtcNow)
        ));
        
        // Publish UI refresh
        Mediator.Publish(new RefreshUiMessage());
    }
    


    
    
    
    // Process received acknowledgment for hash-based system
    public bool ProcessReceivedAcknowledgment(CharacterDataAcknowledgmentDto acknowledgmentDto)
    {
        var hashVersionKey = acknowledgmentDto.DataHash;
        var acknowledgingUser = acknowledgmentDto.User;

        if (string.IsNullOrEmpty(hashVersionKey))
        {
            _logger.LogWarning("Invalid hash version key: {hashVersionKey}", hashVersionKey);
            return false;
        }
        
        var userKey = acknowledgingUser.UID;
        
        // Check if this acknowledgment matches the latest one for this user
        // OR if it's in the acknowledgment history (to handle out-of-order acknowledgments)
        bool matchesLatest = _userLatestAcknowledgments.TryGetValue(userKey, out var latestInfo) && 
            string.Equals(latestInfo.AcknowledgmentId, hashVersionKey, StringComparison.Ordinal);
        
        bool matchesHistory = !matchesLatest && IsHashInHistory(userKey, hashVersionKey);
        
        if (!matchesLatest && !matchesHistory)
        {
            _logger.LogDebug("Acknowledgment {hashVersionKey} from {user} is not the latest, not found, and not in history", hashVersionKey, acknowledgingUser.AliasOrUID);
            return false;
        }
        
        // If it matches history but not latest, use the latest info for timing metrics
        if (matchesHistory && !matchesLatest)
        {
            _logger.LogDebug("Acknowledgment {hashVersionKey} from {user} matched history (out-of-order)", hashVersionKey, acknowledgingUser.AliasOrUID);
            if (!_userLatestAcknowledgments.TryGetValue(userKey, out latestInfo))
            {
                latestInfo = new LatestAcknowledgmentInfo(hashVersionKey);
            }
        }
        
        latestInfo ??= new LatestAcknowledgmentInfo(hashVersionKey);
        var now = DateTime.UtcNow;
        var responseMs = Math.Max(0, (long)(now - latestInfo.CreatedAt).TotalMilliseconds);
        Interlocked.Increment(ref _resultTotal);
        Interlocked.Add(ref _resultResponseTotalMs, responseMs);
        Interlocked.Increment(ref _resultResponseCount);
        if (acknowledgmentDto.Success)
        {
            Interlocked.Increment(ref _resultSuccess);
        }
        else
        {
            Interlocked.Increment(ref _resultFail);
            _resultErrorCounts.AddOrUpdate(acknowledgmentDto.ErrorCode, 1, (_, old) => old + 1);
        }

        // Only remove the latest acknowledgment tracking if this response was for the latest one.
        // A historical (out-of-order) response must not discard the current latest entry.
        if (matchesLatest)
        {
            _userLatestAcknowledgments.TryRemove(userKey, out _);
        }

        // Remove from history to prevent duplicate processing
        RemoveFromHistory(userKey, hashVersionKey);
        
        // Update the pair's acknowledgment status
        var pair = _getPairFunc(acknowledgingUser);
        if (pair != null)
        {
            pair.UpdateAcknowledgmentStatus(hashVersionKey, acknowledgmentDto.Success, DateTimeOffset.UtcNow,
                    acknowledgmentDto.ErrorCode, acknowledgmentDto.ErrorMessage)
                .GetAwaiter().GetResult();
            pair.SetOutgoingAcknowledgmentContext(hashVersionKey, acknowledgmentDto.SessionId);
            _logger.LogDebug("Updated pair acknowledgment status for user {user} - HashVersion: {hashVersionKey} success={success} errorCode={errorCode}",
                acknowledgingUser.AliasOrUID, hashVersionKey, acknowledgmentDto.Success, acknowledgmentDto.ErrorCode);
            Mediator.Publish(new DebugLogEventMessage(
                acknowledgmentDto.Success ? LogLevel.Information : LogLevel.Warning,
                "ACK",
                acknowledgmentDto.Success ? "Ack received" : "Ack received (fail)",
                Uid: acknowledgingUser.UID,
                Details: $"hash={hashVersionKey[..Math.Min(8, hashVersionKey.Length)]} code={acknowledgmentDto.ErrorCode} msg={acknowledgmentDto.ErrorMessage ?? "-"} session={acknowledgmentDto.SessionId ?? "-"}"));
            
            // Add success notification
            _messageService.AddTaggedMessage(
                $"ack_result_{hashVersionKey}_{acknowledgingUser.UID}",
                acknowledgmentDto.Success
                    ? $"Acknowledgment received from {acknowledgingUser.AliasOrUID}"
                    : $"Acknowledgment failed from {acknowledgingUser.AliasOrUID}",
                acknowledgmentDto.Success ? NotificationType.Success : NotificationType.Warning,
                acknowledgmentDto.Success ? "Acknowledgment Received" : "Acknowledgment Failed",
                TimeSpan.FromSeconds(3)
            );
            
            // Publish acknowledgment received event
            Mediator.Publish(new AcknowledgmentReceivedMessage(
                new AcknowledgmentEventDto(
                    hashVersionKey,
                    acknowledgingUser,
                    AcknowledgmentStatus.Received,
                    DateTime.UtcNow)
            ));
        }
        else
        {
            _logger.LogWarning("Could not find pair for user {user} to update acknowledgment status", acknowledgingUser.AliasOrUID);
        }
        
        _logger.LogDebug("Processed acknowledgment from {user} for HashVersion {hashVersionKey}", 
            acknowledgingUser.AliasOrUID, hashVersionKey);
        
        // Clean up pending acknowledgment notification
        _messageService.CleanTaggedMessages($"ack_{hashVersionKey}");
        
        // Add completion notification
        _messageService.AddTaggedMessage(
            $"ack_complete_{hashVersionKey}",
            acknowledgmentDto.Success ? "Acknowledgment received successfully" : "Acknowledgment failed",
            acknowledgmentDto.Success ? NotificationType.Success : NotificationType.Warning,
            acknowledgmentDto.Success ? "Acknowledgment Complete" : "Acknowledgment Failed",
            TimeSpan.FromSeconds(4)
        );
        
        // Publish batch completion event
        Mediator.Publish(new AcknowledgmentBatchCompletedMessage(
            hashVersionKey,
            new List<UserData> { acknowledgingUser },
            DateTime.UtcNow
        ));
        
        // Publish granular UI refresh for this specific acknowledgment
        Mediator.Publish(new AcknowledgmentUiRefreshMessage(
            AcknowledgmentId: hashVersionKey,
            User: acknowledgingUser
        ));
        
        // Publish acknowledgment metrics update
        var totalPending = GetTotalPendingAcknowledgments();
        Mediator.Publish(new AcknowledgmentMetricsUpdatedMessage(
            totalPending,
            1, // One acknowledgment was completed
            0,
            DateTime.UtcNow
        ));
        var stats = _batchingService.GetStatistics();
        Logger.LogDebug("Batch stats - Pending: {pending}, Users: {users}", stats.PendingBatches, stats.TotalPendingUsers);
        
        // Keep legacy RefreshUiMessage for backward compatibility
        Mediator.Publish(new RefreshUiMessage());
        return true;
    }
    
    // Get total count of pending acknowledgments
    private int GetTotalPendingAcknowledgments()
    {
        return _userLatestAcknowledgments.Count;
    }
    
    // Get all pending acknowledgments
    public Dictionary<string, string> GetPendingAcknowledgments()
    {
        return _userLatestAcknowledgments.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.AcknowledgmentId,
            StringComparer.Ordinal
        );
    }
    
    // Get total pending acknowledgment count
    public int GetPendingAcknowledgmentCount()
    {
        return _userLatestAcknowledgments.Count;
    }

    // Remove a pending acknowledgment for a user and clear pair state
    public bool RemovePendingAcknowledgment(UserData user, string? acknowledgmentId = null)
    {
        try
        {
            var userKey = user.UID;
            if (string.IsNullOrEmpty(userKey))
            {
                Logger.LogWarning("RemovePendingAcknowledgment called with empty user UID");
                return false;
            }

            if (!_userLatestAcknowledgments.TryGetValue(userKey, out var latestInfo))
            {
                Logger.LogDebug("No pending acknowledgment found for user {user}", user.AliasOrUID);
                return false;
            }

            // If a specific acknowledgmentId was provided, ensure it matches the latest
            if (!string.IsNullOrEmpty(acknowledgmentId) && !string.Equals(latestInfo.AcknowledgmentId, acknowledgmentId, StringComparison.Ordinal))
            {
                Logger.LogDebug("Provided acknowledgmentId {ackId} does not match latest {latest} for user {user}", acknowledgmentId, latestInfo.AcknowledgmentId, user.AliasOrUID);
                return false;
            }

            // Remove pending entry
            if (_userLatestAcknowledgments.TryRemove(userKey, out var removedInfo))
            {
                // Remove from history
                RemoveFromHistory(userKey, removedInfo.AcknowledgmentId);
                
                // Clear pair pending state and related notifications
                var pair = _getPairFunc(user);
                if (pair != null)
                {
                    pair.ClearPendingAcknowledgmentForce(_messageService);
                    Logger.LogDebug("Cleared pending acknowledgment for user {user}", user.AliasOrUID);
                }

                // Clean up notifications tied to this acknowledgment
                _messageService.CleanTaggedMessages($"ack_{removedInfo.AcknowledgmentId}");

                // Publish UI refresh
                Mediator.Publish(new AcknowledgmentUiRefreshMessage(
                    AcknowledgmentId: removedInfo.AcknowledgmentId,
                    User: user
                ));
                Mediator.Publish(new RefreshUiMessage());
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to remove pending acknowledgment for user {user}", user.AliasOrUID);
            return false;
        }
    }

    // Clean up old pending acknowledgments based on age
    public async Task CleanupOldPendingAcknowledgments(TimeSpan maxAge)
    {
        var cutoffTime = DateTime.UtcNow.Subtract(maxAge);
        var toRemove = new List<(string userKey, string ackId, UserData user, DateTime createdAt)>();
        
        foreach (var userKvp in _userLatestAcknowledgments)
        {
            var userKey = userKvp.Key;
            var latestInfo = userKvp.Value;
            
            if (latestInfo.CreatedAt < cutoffTime)
            {
                // Find the user data for this key - we need to get it from pairs
                
                // Since we don't have direct access to all users, we'll use the acknowledgment ID to find the user
                // This is a simplified approach - in practice you might need a different way to resolve users
                var dummyUser = new UserData(userKey);
                toRemove.Add((userKey, latestInfo.AcknowledgmentId, dummyUser, latestInfo.CreatedAt));
                _logger.LogInformation("Marking old pending acknowledgment {ackId} for user {user} for removal (age: {age}s)", 
                    latestInfo.AcknowledgmentId, userKey, (DateTime.UtcNow - latestInfo.CreatedAt).TotalSeconds);
            }
        }
        
        // Remove old acknowledgments and clear pair status
        foreach (var (userKey, ackId, user, createdAt) in toRemove)
        {
            if (_userLatestAcknowledgments.TryRemove(userKey, out _))
            {
                // Remove from history
                RemoveFromHistory(userKey, ackId);
                
                var now = DateTime.UtcNow;
                var responseMs = Math.Max(0, (long)(now - createdAt).TotalMilliseconds);
                Interlocked.Increment(ref _resultTotal);
                Interlocked.Increment(ref _resultFail);
                Interlocked.Add(ref _resultResponseTotalMs, responseMs);
                Interlocked.Increment(ref _resultResponseCount);
                _resultErrorCounts.AddOrUpdate(Sphene.API.Dto.User.AcknowledgmentErrorCode.NotArrivedTimeout, 1, (_, old) => old + 1);

                // Clear the pending acknowledgment from pair with timeout notification
                var pair = _getPairFunc(user);
                if (pair != null && string.Equals(pair.LastAcknowledgmentId, ackId, StringComparison.Ordinal))
                {
                    pair.UpdateAcknowledgmentStatus(ackId, false, DateTimeOffset.UtcNow,
                            Sphene.API.Dto.User.AcknowledgmentErrorCode.NotArrivedTimeout,
                            "Acknowledgment timed out")
                        .GetAwaiter().GetResult();
                }
                _logger.LogInformation("Removed old pending acknowledgment {ackId} for user {user}", ackId, userKey);
                
                // Clean up related notifications
                _messageService.CleanTaggedMessages($"ack_{ackId}");
                
                // Add timeout notification
                _messageService.AddTaggedMessage(
                    $"ack_timeout_{ackId}",
                    $"Acknowledgment {ackId} timed out and was removed",
                    NotificationType.Warning,
                    "Acknowledgment Timeout",
                    TimeSpan.FromSeconds(5)
                );
                
                // Publish acknowledgment timeout event
                Mediator.Publish(new AcknowledgmentTimeoutMessage(
                    ackId,
                    user,
                    DateTime.UtcNow
                ));
            }
        }
        
        if (toRemove.Count > 0)
        {
            _logger.LogInformation("Cleaned up {count} old pending acknowledgments", toRemove.Count);
            
            // Publish acknowledgment metrics update
            var totalPending = _userLatestAcknowledgments.Count;
            Mediator.Publish(new AcknowledgmentMetricsUpdatedMessage(
                totalPending,
                0, // We don't track completed count here
                toRemove.Count,
                DateTime.UtcNow
            ));
            
            // Publish granular UI refresh for cleanup
            Mediator.Publish(new AcknowledgmentUiRefreshMessage(
                RefreshAll: true
            ));
            
            // Keep legacy RefreshUiMessage for backward compatibility
            Mediator.Publish(new RefreshUiMessage());
        }
    }

    // Check if there are any pending acknowledgments
    public bool HasPendingAcknowledgments()
    {
        return !_userLatestAcknowledgments.IsEmpty;
    }
    
    // Clean up old sessions (simplified for single acknowledgment per user model)
    public async Task CleanupOldSessions(TimeSpan maxAge)
    {
        // In the new model, we don't have sessions to clean up since we only store latest acknowledgments
        // This method is kept for compatibility but delegates to CleanupOldPendingAcknowledgments
        await CleanupOldPendingAcknowledgments(maxAge).ConfigureAwait(false);
    }
    
    // Get acknowledgment status for UI display
    public List<string> GetAcknowledgmentStatuses()
    {
        var statuses = new List<string>();
        
        foreach (var userKvp in _userLatestAcknowledgments)
        {
            var userKey = userKvp.Key;
            var latestInfo = userKvp.Value;
            var statusText = $"User: {userKey}, HashVersion: {latestInfo.AcknowledgmentId}, Created: {latestInfo.CreatedAt}";
            statuses.Add(statusText);
        }
        
        return statuses;
    }
    
    
    
    public string CurrentSessionId => _currentSessionId;
    // Process timeout acknowledgment - mark as failed and update pair status
    public void ProcessTimeoutAcknowledgment(string hashVersionKey)
    {
        if (string.IsNullOrEmpty(hashVersionKey))
        {
            _logger.LogWarning("Invalid hash version key for timeout: {hashVersionKey}", hashVersionKey);
            return;
        }
        
        // Find and remove any pending acknowledgments with this hash+version
        var timedOutUsers = new List<(string UserKey, DateTime CreatedAt)>();
        foreach (var kvp in _userLatestAcknowledgments)
        {
            if (string.Equals(kvp.Value.AcknowledgmentId, hashVersionKey, StringComparison.Ordinal))
            {
                timedOutUsers.Add((kvp.Key, kvp.Value.CreatedAt));
            }
        }
        
        foreach (var (userKey, createdAt) in timedOutUsers)
        {
            if (_userLatestAcknowledgments.TryRemove(userKey, out _))
            {
                // Remove from history
                RemoveFromHistory(userKey, hashVersionKey);
                
                var now = DateTime.UtcNow;
                var responseMs = Math.Max(0, (long)(now - createdAt).TotalMilliseconds);
                Interlocked.Increment(ref _resultTotal);
                Interlocked.Increment(ref _resultFail);
                Interlocked.Add(ref _resultResponseTotalMs, responseMs);
                Interlocked.Increment(ref _resultResponseCount);
                _resultErrorCounts.AddOrUpdate(Sphene.API.Dto.User.AcknowledgmentErrorCode.NotArrivedTimeout, 1, (_, old) => old + 1);

                // Try to find the user and update pair status
                var userData = new UserData(userKey, null);
                var pair = _getPairFunc(userData);
                if (pair != null)
                {
                    pair.UpdateAcknowledgmentStatus(hashVersionKey, false, DateTimeOffset.UtcNow,
                            Sphene.API.Dto.User.AcknowledgmentErrorCode.NotArrivedTimeout,
                            "Acknowledgment timed out")
                        .GetAwaiter().GetResult();
                    _logger.LogWarning("Updated pair acknowledgment status for timeout - User: {user}, HashVersion: {hashVersionKey}", userKey, hashVersionKey);
                    
                    // Add timeout notification
                    _messageService.AddTaggedMessage(
                        $"ack_timeout_{hashVersionKey}_{userKey}",
                        $"Acknowledgment timed out for user {userKey}",
                        NotificationType.Warning,
                        "Acknowledgment Timeout",
                        TimeSpan.FromSeconds(5)
                    );
                    
                    // Publish timeout event
                    Mediator.Publish(new AcknowledgmentTimeoutMessage(
                        hashVersionKey,
                        userData,
                        DateTime.UtcNow
                    ));
                }
            }
        }
        
        // Publish UI refresh
        Mediator.Publish(new RefreshUiMessage());
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _userLatestAcknowledgments.Clear();
            _logger.LogInformation("SessionAcknowledgmentManager disposed for session: {sessionId}", _currentSessionId);
        }
        
        base.Dispose(disposing);
    }
}
