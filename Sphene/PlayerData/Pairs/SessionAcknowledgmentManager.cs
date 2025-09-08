using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Data.Comparer;
using Sphene.Services.Mediator;
using System.Collections.Concurrent;

namespace Sphene.PlayerData.Pairs;

// Session-based acknowledgment manager for handling multiple concurrent users
public class SessionAcknowledgmentManager : DisposableMediatorSubscriberBase
{
    private readonly ILogger<SessionAcknowledgmentManager> _logger;
    private readonly Func<UserData, Pair?> _getPairFunc;
    
    // Thread-safe storage for pending acknowledgments per user session with timestamps
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PendingAcknowledgmentInfo>> _userSessionAcknowledgments = new();
    
    // Helper class to store acknowledgment info with timestamp
    private class PendingAcknowledgmentInfo
    {
        public HashSet<UserData> Recipients { get; set; }
        public DateTime CreatedAt { get; set; }
        
        public PendingAcknowledgmentInfo(HashSet<UserData> recipients)
        {
            Recipients = recipients;
            CreatedAt = DateTime.UtcNow;
        }
    }
    
    // Session counter for generating unique session IDs
    private static long _sessionCounter = 0;
    
    // Current session ID for this client instance
    private readonly string _currentSessionId;
    
    public SessionAcknowledgmentManager(ILogger<SessionAcknowledgmentManager> logger, SpheneMediator mediator, Func<UserData, Pair?> getPairFunc) : base(logger, mediator)
    {
        _logger = logger;
        _getPairFunc = getPairFunc;
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
    
    // Set pending acknowledgment for current session
    public void SetPendingAcknowledgmentForSession(List<UserData> recipients, string acknowledgmentId)
    {
        if (!IsCurrentSession(acknowledgmentId))
        {
            _logger.LogWarning("Attempted to set pending acknowledgment for different session. AckId: {ackId}, CurrentSession: {currentSession}", 
                acknowledgmentId, _currentSessionId);
            return;
        }
        
        var sessionAcks = _userSessionAcknowledgments.GetOrAdd(_currentSessionId, _ => new ConcurrentDictionary<string, PendingAcknowledgmentInfo>());
        
        // Remove ALL older acknowledgments
        RemoveOlderAcknowledgments(sessionAcks, acknowledgmentId);
        
        // Remove any pending acknowledgments for users in the new request
        RemovePendingAcknowledgmentsForUsers(sessionAcks, recipients);
        
        // Add new acknowledgment
        var recipientSet = new HashSet<UserData>(recipients, UserDataComparer.Instance);
        sessionAcks[acknowledgmentId] = new PendingAcknowledgmentInfo(recipientSet);
        
        _logger.LogInformation("Set pending acknowledgment for session {sessionId} with ID {ackId} waiting for {count} recipients: [{recipients}]", 
            _currentSessionId, acknowledgmentId, recipients.Count, string.Join(", ", recipients.Select(r => r.AliasOrUID)));
        
        // Publish UI refresh
        Mediator.Publish(new RefreshUiMessage());
    }
    
    // Remove any pending acknowledgments that contain the specified users
    private void RemovePendingAcknowledgmentsForUsers(ConcurrentDictionary<string, PendingAcknowledgmentInfo> sessionAcks, List<UserData> users)
    {
        var toRemove = new List<string>();
        
        foreach (var kvp in sessionAcks)
        {
            var existingAckId = kvp.Key;
            var existingInfo = kvp.Value;
            
            // Check if any of the specified users are in the pending acknowledgment
            var hasUserOverlap = existingInfo.Recipients.Any(existingUser => 
                users.Any(newUser => UserDataComparer.Instance.Equals(existingUser, newUser)));
            
            if (hasUserOverlap)
            {
                toRemove.Add(existingAckId);
                _logger.LogInformation("Removing pending acknowledgment {ackId} because user(s) have new request", existingAckId);
            }
        }
        
        // Remove acknowledgments with user overlap
        foreach (var ackIdToRemove in toRemove)
        {
            sessionAcks.TryRemove(ackIdToRemove, out _);
        }
    }
    
    // Remove ALL older acknowledgments when a newer one arrives
    private void RemoveOlderAcknowledgments(ConcurrentDictionary<string, PendingAcknowledgmentInfo> sessionAcks, string newAcknowledgmentId)
    {
        var newTimestamp = ExtractTimestampFromAcknowledgmentId(newAcknowledgmentId);
        var toRemove = new List<string>();
        
        foreach (var kvp in sessionAcks)
        {
            var existingAckId = kvp.Key;
            var existingInfo = kvp.Value;
            
            // Skip if this is the same acknowledgment ID
            if (existingAckId == newAcknowledgmentId) continue;
            
            var existingTimestamp = ExtractTimestampFromAcknowledgmentId(existingAckId);
            
            // Remove ALL older acknowledgments, regardless of user overlap
            if (newTimestamp > existingTimestamp)
            {
                toRemove.Add(existingAckId);
                _logger.LogInformation("Removing older acknowledgment {oldAckId} (timestamp: {oldTime}) in favor of newer {newAckId} (timestamp: {newTime})", 
                    existingAckId, existingTimestamp, newAcknowledgmentId, newTimestamp);
            }
        }
        
        // Remove older acknowledgments
        foreach (var ackIdToRemove in toRemove)
        {
            sessionAcks.TryRemove(ackIdToRemove, out _);
        }
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
        
        if (!_userSessionAcknowledgments.TryGetValue(sessionId, out var sessionAcks))
        {
            _logger.LogWarning("No session found for acknowledgment ID: {ackId}, SessionId: {sessionId}", acknowledgmentId, sessionId);
            return false;
        }
        
        if (!sessionAcks.TryGetValue(acknowledgmentId, out var pendingInfo))
        {
            _logger.LogWarning("No pending acknowledgment found for ID: {ackId} in session: {sessionId}", acknowledgmentId, sessionId);
            return false;
        }
        
        // Remove the acknowledging user from pending list
        var removed = pendingInfo.Recipients.Remove(acknowledgingUser);
        if (!removed)
        {
            _logger.LogWarning("User {user} was not in pending list for acknowledgment {ackId}", acknowledgingUser.AliasOrUID, acknowledgmentId);
            return false;
        }
        
        // Update the pair's acknowledgment status to show success
        var pair = _getPairFunc(acknowledgingUser);
        if (pair != null)
        {
            pair.UpdateAcknowledgmentStatus(acknowledgmentId, true, DateTimeOffset.UtcNow);
            _logger.LogInformation("Updated pair acknowledgment status for user {user} - AckId: {ackId}", acknowledgingUser.AliasOrUID, acknowledgmentId);
        }
        else
        {
            _logger.LogWarning("Could not find pair for user {user} to update acknowledgment status", acknowledgingUser.AliasOrUID);
        }
        
        _logger.LogInformation("Processed acknowledgment from {user} for ID {ackId}. Remaining: {remaining}", 
            acknowledgingUser.AliasOrUID, acknowledgmentId, pendingInfo.Recipients.Count);
        
        // If all acknowledgments received, remove from pending
        if (pendingInfo.Recipients.Count == 0)
        {
            sessionAcks.TryRemove(acknowledgmentId, out _);
            _logger.LogInformation("All acknowledgments received for ID {ackId}, removed from session {sessionId}", acknowledgmentId, sessionId);
            
            // Clean up empty session
            if (sessionAcks.IsEmpty)
            {
                _userSessionAcknowledgments.TryRemove(sessionId, out _);
                _logger.LogDebug("Cleaned up empty session: {sessionId}", sessionId);
            }
        }
        
        // Publish UI refresh
        Mediator.Publish(new RefreshUiMessage());
        return true;
    }
    
    // Get all pending acknowledgments for current session
    public Dictionary<string, HashSet<UserData>> GetPendingAcknowledgments()
    {
        if (_userSessionAcknowledgments.TryGetValue(_currentSessionId, out var sessionAcks))
        {
            return sessionAcks.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Recipients);
        }
        
        return new Dictionary<string, HashSet<UserData>>();
    }
    
    // Get total pending acknowledgment count for current session
    public int GetPendingAcknowledgmentCount()
    {
        if (_userSessionAcknowledgments.TryGetValue(_currentSessionId, out var sessionAcks))
        {
            return sessionAcks.Values.Sum(info => info.Recipients.Count);
        }
        
        return 0;
    }
    
    // Clean up old sessions (call periodically)
    public void CleanupOldSessions(TimeSpan maxAge)
    {
        var cutoffTime = DateTimeOffset.UtcNow.Subtract(maxAge).ToUnixTimeMilliseconds();
        var sessionsToRemove = new List<string>();
        
        foreach (var sessionId in _userSessionAcknowledgments.Keys)
        {
            if (TryExtractTimestamp(sessionId, out var timestamp) && timestamp < cutoffTime)
            {
                sessionsToRemove.Add(sessionId);
            }
        }
        
        foreach (var sessionId in sessionsToRemove)
        {
            if (_userSessionAcknowledgments.TryRemove(sessionId, out var removedSession))
            {
                _logger.LogInformation("Cleaned up old session {sessionId} with {count} pending acknowledgments", 
                    sessionId, removedSession.Values.Sum(info => info.Recipients.Count));
            }
        }
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
            _userSessionAcknowledgments.Clear();
            _logger.LogInformation("SessionAcknowledgmentManager disposed for session: {sessionId}", _currentSessionId);
        }
        
        base.Dispose(disposing);
    }
}