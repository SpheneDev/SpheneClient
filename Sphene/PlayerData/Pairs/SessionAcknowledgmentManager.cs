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
    
    // Generate session-aware acknowledgment ID
    public string GenerateAcknowledgmentId()
    {
        var baseId = Guid.NewGuid().ToString();
        var sessionAwareId = $"{_currentSessionId}_{baseId}";
        
        _logger.LogDebug("Generated session-aware acknowledgment ID: {ackId}", sessionAwareId);
        return sessionAwareId;
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
    
    // Check if acknowledgment ID belongs to current session
    public bool IsCurrentSession(string acknowledgmentId)
    {
        var sessionId = ExtractSessionId(acknowledgmentId);
        return sessionId == _currentSessionId;
    }
    
    // Set pending acknowledgment for current session - only latest per user
    public void SetPendingAcknowledgmentForSession(List<UserData> recipients, string acknowledgmentId)
    {
        if (!IsCurrentSession(acknowledgmentId))
        {
            _logger.LogWarning("Attempted to set pending acknowledgment for different session. AckId: {ackId}, CurrentSession: {currentSession}", 
                acknowledgmentId, _currentSessionId);
            return;
        }
        
        // Process each recipient individually - store only latest AckId per user
        foreach (var recipient in recipients)
        {
            ProcessLatestAcknowledgment(recipient, acknowledgmentId);
        }
    }
    
    // Process latest acknowledgment for a single recipient
    private void ProcessLatestAcknowledgment(UserData recipient, string acknowledgmentId)
    {
        var sessionId = ExtractSessionId(acknowledgmentId);
        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning("Invalid acknowledgment ID format: {ackId}", acknowledgmentId);
            return;
        }
        
        var userKey = recipient.UID;
        
        // Store only the latest acknowledgment for this user
        var latestInfo = new LatestAcknowledgmentInfo(acknowledgmentId);
        
        var oldInfo = _userLatestAcknowledgments.AddOrUpdate(
            userKey,
            latestInfo,
            (key, existing) => latestInfo);
        
        if (oldInfo != null && oldInfo.AcknowledgmentId != acknowledgmentId)
        {
            _logger.LogDebug("Replaced acknowledgment {oldAckId} with {newAckId} for user {user}", 
                oldInfo.AcknowledgmentId, acknowledgmentId, userKey);
        }
        else
        {
            _logger.LogDebug("Added pending acknowledgment {ackId} for user {user} in session {sessionId}", 
                acknowledgmentId, userKey, sessionId);
        }
        
        // Add notification for pending acknowledgment
        _messageService.AddTaggedMessage(
            $"ack_{acknowledgmentId}",
            $"Waiting for acknowledgment from {recipient.AliasOrUID}",
            NotificationType.Info,
            "Acknowledgment Pending",
            TimeSpan.FromSeconds(5)
        );
        
        // Publish acknowledgment pending event
        Mediator.Publish(new AcknowledgmentPendingMessage(
            acknowledgmentId,
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
    
    // Process received acknowledgment for any session
    public bool ProcessReceivedAcknowledgment(string acknowledgmentId, UserData acknowledgingUser)
    {
        var sessionId = ExtractSessionId(acknowledgmentId);
        if (sessionId == null)
        {
            _logger.LogWarning("Could not extract session ID from acknowledgment ID: {ackId}", acknowledgmentId);
            return false;
        }
        
        var userKey = acknowledgingUser.UID;
        
        // Check if this acknowledgment matches the latest one for this user
        if (!_userLatestAcknowledgments.TryGetValue(userKey, out var latestInfo) || 
            latestInfo.AcknowledgmentId != acknowledgmentId)
        {
            _logger.LogDebug("Acknowledgment {ackId} from {user} is not the latest or not found", acknowledgmentId, acknowledgingUser.AliasOrUID);
            return false;
        }
        
        // Remove the acknowledgment as it's been received
        _userLatestAcknowledgments.TryRemove(userKey, out _);
        
        // Update the pair's acknowledgment status to show success
        var pair = _getPairFunc(acknowledgingUser);
        if (pair != null)
        {
            pair.UpdateAcknowledgmentStatus(acknowledgmentId, true, DateTimeOffset.UtcNow);
            _logger.LogInformation("Updated pair acknowledgment status for user {user} - AckId: {ackId}", acknowledgingUser.AliasOrUID, acknowledgmentId);
            
            // Add success notification
            _messageService.AddTaggedMessage(
                $"ack_success_{acknowledgmentId}_{acknowledgingUser.UID}",
                $"Acknowledgment received from {acknowledgingUser.AliasOrUID}",
                NotificationType.Success,
                "Acknowledgment Received",
                TimeSpan.FromSeconds(3)
            );
            
            // Publish acknowledgment received event
            Mediator.Publish(new AcknowledgmentReceivedMessage(
                acknowledgmentId,
                acknowledgingUser,
                DateTime.UtcNow
            ));
        }
        else
        {
            _logger.LogWarning("Could not find pair for user {user} to update acknowledgment status", acknowledgingUser.AliasOrUID);
        }
        
        _logger.LogInformation("Processed acknowledgment from {user} for ID {ackId}", 
            acknowledgingUser.AliasOrUID, acknowledgmentId);
        
        // Clean up pending acknowledgment notification
        _messageService.CleanTaggedMessages($"ack_{acknowledgmentId}");
        
        // Add completion notification
        _messageService.AddTaggedMessage(
            $"ack_complete_{acknowledgmentId}",
            "Acknowledgment received successfully",
            NotificationType.Success,
            "Acknowledgment Complete",
            TimeSpan.FromSeconds(4)
        );
        
        // Publish batch completion event
        Mediator.Publish(new AcknowledgmentBatchCompletedMessage(
            acknowledgmentId,
            new List<UserData> { acknowledgingUser },
            DateTime.UtcNow
        ));
        
        // Publish granular UI refresh for this specific acknowledgment
        Mediator.Publish(new AcknowledgmentUiRefreshMessage(
            AcknowledgmentId: acknowledgmentId,
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
    private void ClearPendingStatusFromPair(UserData user, string acknowledgmentId)
    {
        var pair = _getPairFunc(user);
        if (pair != null)
        {
            pair.ClearPendingAcknowledgment(acknowledgmentId, _messageService);
            _logger.LogDebug("Cleared pending acknowledgment {ackId} from pair for user {user}", acknowledgmentId, user.AliasOrUID);
        }
        else
        {
            _logger.LogWarning("Could not find pair for user {user} to clear pending acknowledgment {ackId}", user.AliasOrUID, acknowledgmentId);
        }
    }

    // Clean up old pending acknowledgments based on age
    public void CleanupOldPendingAcknowledgments(TimeSpan maxAge)
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
                
                ClearPendingStatusFromPair(user, ackId);
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
    public void CleanupOldSessions(TimeSpan maxAge)
    {
        // In the new model, we don't have sessions to clean up since we only store latest acknowledgments
        // This method is kept for compatibility but delegates to CleanupOldPendingAcknowledgments
        CleanupOldPendingAcknowledgments(maxAge);
    }
    
    // Get acknowledgment status for UI display
    public List<string> GetAcknowledgmentStatuses()
    {
        var statuses = new List<string>();
        
        foreach (var userKvp in _userLatestAcknowledgments)
        {
            var userKey = userKvp.Key;
            var latestInfo = userKvp.Value;
            var sessionId = ExtractSessionId(latestInfo.AcknowledgmentId);
            
            var statusText = $"User: {userKey}, AckId: {latestInfo.AcknowledgmentId}, Session: {sessionId ?? "unknown"}, Created: {latestInfo.CreatedAt}";
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