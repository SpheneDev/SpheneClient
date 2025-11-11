using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Data.Comparer;
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
    private readonly ConcurrentDictionary<string, LatestAcknowledgmentInfo> _userLatestAcknowledgments = new();
    
    // Helper class to store latest acknowledgment info per user
    private class LatestAcknowledgmentInfo
    {
        public string AcknowledgmentId { get; set; }
        public DateTime CreatedAt { get; set; }
        
        public LatestAcknowledgmentInfo(string acknowledgmentId)
        {
            AcknowledgmentId = acknowledgmentId;
            CreatedAt = DateTime.UtcNow;
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
        if (parts.Length >= 3 && parts[0] == "session")
        {
            return $"{parts[0]}_{parts[1]}_{parts[2]}";
        }
        
        return null;
    }
    
    // Check if acknowledgment ID is valid hash+version format
    public bool IsValidHashVersion(string hashVersionKey)
    {
        return !string.IsNullOrEmpty(hashVersionKey) && hashVersionKey.Contains('_');
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
        
        // Store only the latest acknowledgment for this user
        var latestInfo = new LatestAcknowledgmentInfo(hashVersionKey);
        
        var oldInfo = _userLatestAcknowledgments.AddOrUpdate(
            userKey,
            latestInfo,
            (key, existing) => latestInfo);
        
        if (oldInfo != null && oldInfo.AcknowledgmentId != hashVersionKey)
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
            hashVersionKey,
            recipient,
            DateTime.UtcNow
        ));
        
        // Publish UI refresh
        Mediator.Publish(new RefreshUiMessage());
    }
    


    
    // Extract timestamp from acknowledgment ID for comparison
    private static long ExtractTimestampFromAcknowledgmentId(string acknowledgmentId)
    {
        if (string.IsNullOrEmpty(acknowledgmentId)) return 0;
        
        var parts = acknowledgmentId.Split('_');
        if (parts.Length >= 2 && parts[0] == "session" && long.TryParse(parts[1], out var timestamp))
        {
            return timestamp;
        }
        
        return 0;
    }
    
    // Process received acknowledgment for hash-based system
    public bool ProcessReceivedAcknowledgment(string hashVersionKey, UserData acknowledgingUser)
    {
        if (string.IsNullOrEmpty(hashVersionKey))
        {
            _logger.LogWarning("Invalid hash version key: {hashVersionKey}", hashVersionKey);
            return false;
        }
        
        var userKey = acknowledgingUser.UID;
        
        // Check if this acknowledgment matches the latest one for this user
        if (!_userLatestAcknowledgments.TryGetValue(userKey, out var latestInfo) || 
            latestInfo.AcknowledgmentId != hashVersionKey)
        {
            _logger.LogDebug("Acknowledgment {hashVersionKey} from {user} is not the latest or not found", hashVersionKey, acknowledgingUser.AliasOrUID);
            return false;
        }
        
        // Remove the acknowledgment as it's been received
        _userLatestAcknowledgments.TryRemove(userKey, out _);
        
        // Update the pair's acknowledgment status to show success
        var pair = _getPairFunc(acknowledgingUser);
        if (pair != null)
        {
            pair.UpdateAcknowledgmentStatus(hashVersionKey, true, DateTimeOffset.UtcNow);
            _logger.LogDebug("Updated pair acknowledgment status for user {user} - HashVersion: {hashVersionKey}", acknowledgingUser.AliasOrUID, hashVersionKey);
            
            // Add success notification
            _messageService.AddTaggedMessage(
                $"ack_success_{hashVersionKey}_{acknowledgingUser.UID}",
                $"Acknowledgment received from {acknowledgingUser.AliasOrUID}",
                NotificationType.Success,
                "Acknowledgment Received",
                TimeSpan.FromSeconds(3)
            );
            
            // Publish acknowledgment received event
            Mediator.Publish(new AcknowledgmentReceivedMessage(
                hashVersionKey,
                acknowledgingUser,
                DateTime.UtcNow
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
            "Acknowledgment received successfully",
            NotificationType.Success,
            "Acknowledgment Complete",
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
            kvp => kvp.Value.AcknowledgmentId
        );
    }
    
    // Get total pending acknowledgment count
    public int GetPendingAcknowledgmentCount()
    {
        return _userLatestAcknowledgments.Count;
    }
    
    // Clear pending status from pair when acknowledgment is removed
    private async Task ClearPendingStatusFromPair(UserData user, string acknowledgmentId)
    {
        var pair = _getPairFunc(user);
        if (pair != null)
        {
            await pair.ClearPendingAcknowledgment(acknowledgmentId, _messageService);
            _logger.LogDebug("Cleared pending acknowledgment {ackId} from pair for user {user}", acknowledgmentId, user.AliasOrUID);
        }
        else
        {
            _logger.LogWarning("Could not find pair for user {user} to clear pending acknowledgment {ackId}", user.AliasOrUID, acknowledgmentId);
        }
    }

    // Clean up old pending acknowledgments based on age
    public async Task CleanupOldPendingAcknowledgments(TimeSpan maxAge)
    {
        var cutoffTime = DateTime.UtcNow.Subtract(maxAge);
        var toRemove = new List<(string userKey, string ackId, UserData user)>();
        
        foreach (var userKvp in _userLatestAcknowledgments)
        {
            var userKey = userKvp.Key;
            var latestInfo = userKvp.Value;
            
            if (latestInfo.CreatedAt < cutoffTime)
            {
                // Find the user data for this key - we need to get it from pairs
                var allPairs = new List<UserData>();
                // Since we don't have direct access to all users, we'll use the acknowledgment ID to find the user
                // This is a simplified approach - in practice you might need a different way to resolve users
                var dummyUser = new UserData(userKey);
                toRemove.Add((userKey, latestInfo.AcknowledgmentId, dummyUser));
                _logger.LogInformation("Marking old pending acknowledgment {ackId} for user {user} for removal (age: {age}s)", 
                    latestInfo.AcknowledgmentId, userKey, (DateTime.UtcNow - latestInfo.CreatedAt).TotalSeconds);
            }
        }
        
        // Remove old acknowledgments and clear pair status
        foreach (var (userKey, ackId, user) in toRemove)
        {
            if (_userLatestAcknowledgments.TryRemove(userKey, out _))
            {
                // Clear the pending acknowledgment from pair with timeout notification
                var pair = _getPairFunc(user);
                if (pair != null && pair.LastAcknowledgmentId == ackId)
                {
                    pair.ClearPendingAcknowledgmentForce(_messageService);
                }
                
                await ClearPendingStatusFromPair(user, ackId);
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
        await CleanupOldPendingAcknowledgments(maxAge);
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
    
    // Extract timestamp from session ID
    private static bool TryExtractTimestamp(string sessionId, out long timestamp)
    {
        timestamp = 0;
        if (string.IsNullOrEmpty(sessionId)) return false;
        
        var parts = sessionId.Split('_');
        if (parts.Length >= 2 && parts[0] == "session")
        {
            return long.TryParse(parts[1], out timestamp);
        }
        
        return false;
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
        var timedOutUsers = new List<string>();
        foreach (var kvp in _userLatestAcknowledgments)
        {
            if (kvp.Value.AcknowledgmentId == hashVersionKey)
            {
                timedOutUsers.Add(kvp.Key);
            }
        }
        
        foreach (var userKey in timedOutUsers)
        {
            if (_userLatestAcknowledgments.TryRemove(userKey, out var latestInfo))
            {
                // Try to find the user and update pair status
                var userData = new UserData(userKey, null);
                var pair = _getPairFunc(userData);
                if (pair != null)
                {
                    pair.UpdateAcknowledgmentStatus(hashVersionKey, false, DateTimeOffset.UtcNow);
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