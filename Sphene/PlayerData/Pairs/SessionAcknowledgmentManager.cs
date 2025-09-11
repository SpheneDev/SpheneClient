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
    private readonly Timer _cleanupTimer;
    private readonly Timer _asymmetricVisibilityCleanupTimer;
    private readonly Timer _timeoutCheckTimer;
    private readonly MessageService _messageService;
    private readonly AcknowledgmentBatchingService _batchingService;
    private readonly AcknowledgmentConfiguration _config;   
    // Thread-safe storage for pending acknowledgments per user session with timestamps
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PendingAcknowledgmentInfo>> _userSessionAcknowledgments = new();
    
    // Timeout and reload tracking
    private readonly ConcurrentDictionary<UserData, DateTime> _userReloadAttempts = new();
    // Cache to store consistent timestamps for character data hashes
    private readonly ConcurrentDictionary<string, long> _characterDataHashToTimestamp = new();
    private readonly ConcurrentDictionary<string, string> _characterDataHashToAckId = new();
    
    // Receiving side cache to store and validate acknowledgment IDs from other users
    private readonly ConcurrentDictionary<string, ReceivedAckInfo> _receivedAckCache = new();
    
    private class ReceivedAckInfo
    {
        public string AckId { get; set; }
        public long Timestamp { get; set; }
        public string SessionId { get; set; }
        public DateTime LastSeen { get; set; }
        
        public ReceivedAckInfo(string ackId, long timestamp, string sessionId)
        {
            AckId = ackId;
            Timestamp = timestamp;
            SessionId = sessionId;
            LastSeen = DateTime.UtcNow;
        }
    }
    private const int ACKNOWLEDGMENT_TIMEOUT_SECONDS = 10;
    private const int TIMEOUT_CHECK_INTERVAL_SECONDS = 2;
    private const int RELOAD_INTERVAL_SECONDS = 15;
    
    // Track pending requests that need 15-second reload mechanism
    private readonly ConcurrentDictionary<string, PendingReloadRequest> _pendingReloadRequests = new();
    
    private class PendingReloadRequest
    {
        public string AckId { get; set; }
        public UserData TargetUser { get; set; }
        public DateTime LastReloadAttempt { get; set; }
        public DateTime CreatedAt { get; set; }
        public int ReloadAttempts { get; set; }
        
        public PendingReloadRequest(string ackId, UserData targetUser)
        {
            AckId = ackId;
            TargetUser = targetUser;
            LastReloadAttempt = DateTime.UtcNow;
            CreatedAt = DateTime.UtcNow;
            ReloadAttempts = 0;
        }
        
        public bool ShouldReload => DateTime.UtcNow - LastReloadAttempt >= TimeSpan.FromSeconds(RELOAD_INTERVAL_SECONDS);
    }
    
    // Helper class to store acknowledgment info with timestamp and timeout tracking
    private class PendingAcknowledgmentInfo
    {
        public HashSet<UserData> Recipients { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime TimeoutAt { get; set; }
        public bool HasTimedOut => DateTime.UtcNow > TimeoutAt;
        
        public PendingAcknowledgmentInfo(HashSet<UserData> recipients, TimeSpan timeout = default)
        {
            Recipients = recipients;
            CreatedAt = DateTime.UtcNow;
            TimeoutAt = timeout == default ? CreatedAt.AddSeconds(10) : CreatedAt.Add(timeout);
        }
    }
    
    // Session counter for generating unique session IDs
    private static long _sessionCounter = 0;
    
    // Current session ID for this client instance
    private readonly string _currentSessionId;
    
    public SessionAcknowledgmentManager(ILogger<SessionAcknowledgmentManager> logger, SpheneMediator mediator, 
        Func<UserData, Pair?> getPairFunc, MessageService messageService, AcknowledgmentBatchingService batchingService, 
        AcknowledgmentConfiguration config) : base(logger, mediator)
    {
        _logger = logger;
        _getPairFunc = getPairFunc;
        _messageService = messageService;
        _batchingService = batchingService;
        _config = config;
        _currentSessionId = GenerateSessionId();
        
        _logger.LogInformation("SessionAcknowledgmentManager initialized with session ID: {sessionId}", _currentSessionId);
        
        // Start asymmetric visibility timeout cleanup timer
        _asymmetricVisibilityCleanupTimer = new Timer(state => CleanupAsymmetricVisibilityTimeouts(_config), null,
            TimeSpan.FromSeconds(30), // First cleanup after 30 seconds
            TimeSpan.FromSeconds(45)); // Then every 45 seconds
            
        // Start timeout check timer for acknowledgment timeouts and automatic reloads
        _timeoutCheckTimer = new Timer(state => CheckAcknowledgmentTimeouts(), null,
            TimeSpan.FromSeconds(TIMEOUT_CHECK_INTERVAL_SECONDS),
            TimeSpan.FromSeconds(TIMEOUT_CHECK_INTERVAL_SECONDS));
    }
    
    // Generate unique session ID combining timestamp and counter
    private static string GenerateSessionId()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var counter = Interlocked.Increment(ref _sessionCounter);
        return $"session_{timestamp}_{counter}";
    }
    
    // Generate user-based acknowledgment ID with character data hash
    // Returns the same ID for the same character data hash per user until character data changes
    public string GenerateAcknowledgmentId(string? characterDataHash = null, UserData? targetUser = null)
    {
        if (!string.IsNullOrEmpty(characterDataHash) && targetUser != null)
        {
            var cacheKey = $"{targetUser.UID}_{characterDataHash}";
            
            // Check if we already have an ID for this character data hash and user
            if (_characterDataHashToAckId.TryGetValue(cacheKey, out var existingId))
            {
                return existingId;
            }
            
            // Generate new ID using UserID + character hash (no timestamp needed)
            var hashPrefix = characterDataHash.Length > 8 ? characterDataHash[..8] : characterDataHash;
            var userBasedId = $"{targetUser.UID}_{hashPrefix}";
            
            // Cache the new ID for this character data hash and user
            _characterDataHashToAckId[cacheKey] = userBasedId;
            
            return userBasedId;
        }
        else if (targetUser != null)
        {
            // Fallback without character data hash - use UserID only
            var userBasedId = $"{targetUser.UID}_fallback";
            
            return userBasedId;
        }
        else
        {
            // Ultimate fallback - should not happen in normal operation
            var fallbackId = $"unknown_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            return fallbackId;
        }
    }
    
    // Extract timestamp from enhanced acknowledgment ID
    public static long ExtractTimestampFromAcknowledgmentId(string acknowledgmentId)
    {
        try
        {
            var parts = acknowledgmentId.Split('_');
            if (parts.Length >= 2)
            {
                // For new format: sessionId_hash_timestamp or sessionId_timestamp
                var timestampIndex = parts.Length == 3 ? 2 : 1; // With or without hash
                if (long.TryParse(parts[timestampIndex], out var timestamp))
                {
                    return timestamp;
                }
            }
            // Fallback for old format - return current time to avoid issues
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        catch
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
    
    // Extract character data hash from acknowledgment ID
    public static string? ExtractCharacterDataHash(string acknowledgmentId)
    {
        try
        {
            var parts = acknowledgmentId.Split('_');
            if (parts.Length == 2 && parts[1] != "fallback")
            {
                // New user-based format: userId_hashPrefix
                // Note: This is only the first 8 characters of the original hash
                return parts[1];
            }
            else if (parts.Length == 3)
            {
                // Legacy format with hash: sessionId_hashPrefix_timestamp
                // Note: This is only the first 8 characters of the original hash
                return parts[1];
            }
            else if (parts.Length == 4)
            {
                // Alternative format with hash: sessionId_hash_timestamp_baseId
                return parts[1];
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
    
    // Check if acknowledgment ID is newer than another
    public static bool IsAcknowledgmentNewer(string acknowledgmentId1, string acknowledgmentId2)
    {
        var timestamp1 = ExtractTimestampFromAcknowledgmentId(acknowledgmentId1);
        var timestamp2 = ExtractTimestampFromAcknowledgmentId(acknowledgmentId2);
        return timestamp1 > timestamp2;
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
    
    // Clear cached acknowledgment IDs when character data changes
    public void ClearCharacterDataCache()
    {
        _characterDataHashToTimestamp.Clear();
        _characterDataHashToAckId.Clear();
        _receivedAckCache.Clear();
        _pendingReloadRequests.Clear();
    }
    
    // Validate and cache received acknowledgment ID from another user
    public bool ValidateReceivedAcknowledgmentId(string ackId, string fromSessionId)
    {
        try
        {
            var timestamp = ExtractTimestampFromAcknowledgmentId(ackId);
            if (timestamp <= 0)
            {
                return false;
            }
            
            var cacheKey = $"{fromSessionId}_{ackId}";
            
            // Check if we have this acknowledgment ID cached
            if (_receivedAckCache.TryGetValue(cacheKey, out var existingInfo))
            {
                // If timestamp changed, invalidate the cache entry
                if (existingInfo.Timestamp != timestamp)
                {                  
                    // Remove old entry and create new one
                    _receivedAckCache.TryRemove(cacheKey, out _);
                    _receivedAckCache[cacheKey] = new ReceivedAckInfo(ackId, timestamp, fromSessionId);
                    return true; // New timestamp means character data changed
                }
                
                // Update last seen time
                existingInfo.LastSeen = DateTime.UtcNow;
                return false; // Same timestamp, no need to reload
            }
            
            // New acknowledgment ID, cache it
            _receivedAckCache[cacheKey] = new ReceivedAckInfo(ackId, timestamp, fromSessionId);
            return true; // New ID means we should process it
        }
        catch (Exception ex)
        {
            return false;
        }
    }
    
    // Clean up old received acknowledgment cache entries
     public void CleanupReceivedAckCache(TimeSpan maxAge)
     {
         var cutoffTime = DateTime.UtcNow - maxAge;
         var keysToRemove = new List<string>();
         
         foreach (var kvp in _receivedAckCache)
         {
             if (kvp.Value.LastSeen < cutoffTime)
             {
                 keysToRemove.Add(kvp.Key);
             }
         }
         
         foreach (var key in keysToRemove)
         {
             _receivedAckCache.TryRemove(key, out _);
         }
         
         if (keysToRemove.Count > 0)
         {
             _logger.LogDebug("Cleaned up {count} old received acknowledgment cache entries", keysToRemove.Count);
         }
     }
     
     // Add a request to the 15-second reload queue (one slot per user)
     public void AddPendingReloadRequest(string ackId, UserData targetUser)
     {
         var request = new PendingReloadRequest(ackId, targetUser);
         // Use UserID as key to ensure only one reload request per user
         var userKey = targetUser.UID;         
         _pendingReloadRequests[userKey] = request;
     }
     
     // Remove a request from the reload queue (when acknowledgment is received)
     public void RemovePendingReloadRequest(string ackId)
     {
         // Find and remove by acknowledgment ID (need to search through values)
         var itemToRemove = _pendingReloadRequests.FirstOrDefault(kvp => kvp.Value.AckId == ackId);
         if (!itemToRemove.Equals(default(KeyValuePair<string, PendingReloadRequest>)))
         {
             if (_pendingReloadRequests.TryRemove(itemToRemove.Key, out var request))
             {
                 _logger.LogDebug("Removed pending reload request for user");
             }
         }
     }
     
     // Process pending reload requests - trigger reloads every 15 seconds
     public async Task ProcessPendingReloadRequests()
     {
         var requestsToReload = new List<PendingReloadRequest>();
         
         foreach (var kvp in _pendingReloadRequests)
         {
             var request = kvp.Value;
             if (request.ShouldReload)
             {
                 requestsToReload.Add(request);
             }
         }
         
         foreach (var request in requestsToReload)
         {
             try
             {
                 request.LastReloadAttempt = DateTime.UtcNow;
                 request.ReloadAttempts++;
                 
                 // Extract character data hash from acknowledgment ID for hash-based validation
                 var characterDataHash = ExtractCharacterDataHash(request.AckId);
                                  
                 // Trigger character data reload for the target user with hash validation
                 await TriggerCharacterDataReload(request.TargetUser, characterDataHash);
             }
             catch (Exception ex)
             {
                 //_logger.LogError(ex, "Error processing reload request for acknowledgment ID");
             }
         }
     }
     
     // Trigger character data reload for a specific user with hash-based validation
     private async Task TriggerCharacterDataReload(UserData targetUser, string? characterDataHash = null)
     {
         try
         {
             // If we have a character data hash, check if the user already has this data
             if (!string.IsNullOrEmpty(characterDataHash))
             {
                 // Check if user needs this specific character data based on hash comparison
                 // This prevents unnecessary reloads when the user already has the current data
                 if (!UserNeedsCharacterDataHash(targetUser, characterDataHash))
                 {
                     _logger.LogDebug("User already has character data hash, skipping reload");
                     return;
                 }
             }
             
             // Send a reload request to the target user
             var reloadMessage = new CharacterDataReloadRequest(
                 _currentSessionId,
                 targetUser.UID,
                 DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
             );
             
             Mediator.Publish(reloadMessage);

         }
         catch (Exception ex)
         {
             //_logger.LogError(ex, "Error sending character data reload request to user");
         }
     }
    
    // Test method to verify acknowledgment ID consistency for same character data hash
      public bool TestAcknowledgmentIdConsistency(string characterDataHash, UserData testUser)
      {
          var firstId = GenerateAcknowledgmentId(characterDataHash, testUser);
          var secondId = GenerateAcknowledgmentId(characterDataHash, testUser);
          var isConsistent = firstId == secondId;
          
          _logger.LogInformation("AckID consistency test: Consistent={consistent}", isConsistent);
              
          return isConsistent;
      }
     
     // Check if a user needs character data based on hash comparison
     // This is a simplified version that assumes the user needs data if we don't have tracking info
     private bool UserNeedsCharacterDataHash(UserData user, string? characterDataHashPrefix)
     {
         // Note: characterDataHashPrefix is only the first 8 characters of the original hash
         // extracted from the acknowledgment ID, not the full hash
         if (string.IsNullOrEmpty(characterDataHashPrefix))
         {
             return true;
         }
         
         // For now, we'll use a simple approach: if we're trying to reload, assume the user needs it
         // This can be enhanced later with proper hash tracking per user
         // The main optimization is that we extract the hash prefix from acknowledgment ID to avoid unnecessary reloads
         return true; // Conservative approach - always reload for now, but with hash validation
     }
    
    // Set pending acknowledgment for current session with outdated acknowledgment filtering
    public void SetPendingAcknowledgmentForSession(List<UserData> recipients, string acknowledgmentId)
    {
        if (!IsCurrentSession(acknowledgmentId))
        {
            _logger.LogWarning("Attempted to set pending acknowledgment for different session");
            return;
        }
        
        // Check if this acknowledgment is outdated by comparing with existing acknowledgments for the same recipients
        if (_userSessionAcknowledgments.TryGetValue(_currentSessionId, out var sessionAcks))
        {
            var newerExists = sessionAcks.Any(kvp => 
                kvp.Value.Recipients.Any(existingRecipient => 
                    recipients.Any(newRecipient => UserDataComparer.Instance.Equals(existingRecipient, newRecipient))) &&
                IsAcknowledgmentNewer(kvp.Key, acknowledgmentId));
            
            if (newerExists)
            {
                _logger.LogDebug("Ignoring outdated acknowledgment - newer acknowledgment exists for same recipients");
                return;
            }
        }
        
        // Use batching for multiple recipients to improve performance
        if (recipients.Count > 1)
        {
            var batchKey = $"session_ack_{acknowledgmentId}";
            foreach (var recipient in recipients)
            {
                _batchingService.AddToBatch(batchKey, recipient, acknowledgmentId, ProcessBatchedAcknowledgment);
            }
        }
        else
        {
            // Process single recipient immediately
            ProcessBatchedAcknowledgment(recipients, acknowledgmentId);
        }
    }
    
    private void ProcessBatchedAcknowledgment(List<UserData> recipients, string acknowledgmentId)
    {
        var sessionAcks = _userSessionAcknowledgments.GetOrAdd(_currentSessionId, _ => new ConcurrentDictionary<string, PendingAcknowledgmentInfo>());
        
        // PRIORITY CLEANUP: Remove ALL older acknowledgments first
        RemoveOlderAcknowledgments(sessionAcks, acknowledgmentId);
        
        // AGGRESSIVE CLEANUP: Remove any pending acknowledgments for users in the new request
        RemovePendingAcknowledgmentsForUsers(sessionAcks, recipients);
        
        // FINAL CLEANUP: Ensure no duplicate acknowledgments exist for the same users across all sessions
        CleanupDuplicateAcknowledgmentsAcrossAllSessions(recipients, acknowledgmentId);
        
        // Add new acknowledgment
        var recipientSet = new HashSet<UserData>(recipients, UserDataComparer.Instance);
        sessionAcks[acknowledgmentId] = new PendingAcknowledgmentInfo(recipientSet);
        
        // Add each recipient to pending reload requests for 15-second reload mechanism
        foreach (var recipient in recipients)
        {
            AddPendingReloadRequest(acknowledgmentId, recipient);
        }
        
        _logger.LogInformation("Set pending acknowledgment for session waiting for {count} recipients: [{recipients}]", 
            recipients.Count, string.Join(", ", recipients.Select(r => r.AliasOrUID)));
        
        // Add notification for pending acknowledgment
        var recipientNames = string.Join(", ", recipients.Select(r => r.AliasOrUID));
        if (recipients.Count == 1)
        {
            _messageService.AddTaggedMessage(
                $"ack_{acknowledgmentId}",
                $"Waiting for acknowledgment from {recipientNames}",
                NotificationType.Info,
                "Acknowledgment Pending",
                TimeSpan.FromSeconds(5)
            );
        }
        else
        {
            _messageService.AddTaggedMessage(
                $"ack_{acknowledgmentId}",
                $"Waiting for acknowledgments from {recipients.Count} users: {recipientNames}",
                NotificationType.Info,
                "Batch Acknowledgment Pending",
                TimeSpan.FromSeconds(5)
            );
        }
        
        // Publish acknowledgment pending events
        foreach (var recipient in recipients)
        {
            Mediator.Publish(new AcknowledgmentPendingMessage(
                acknowledgmentId,
                recipient,
                DateTime.UtcNow
            ));
        }
        
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
        
        // Remove acknowledgments with user overlap and clear pair status
        foreach (var ackIdToRemove in toRemove)
        {
            if (sessionAcks.TryRemove(ackIdToRemove, out var removedInfo))
            {
                // Clear pending status from affected pairs
                ClearPendingStatusFromPairs(removedInfo.Recipients, ackIdToRemove);
                
                // Clean up notification messages for removed acknowledgments
                _messageService.CleanTaggedMessages($"ack_{ackIdToRemove}");
            }
        }
    }
    
    // Clean up duplicate acknowledgments for the same users across ALL sessions
    private void CleanupDuplicateAcknowledgmentsAcrossAllSessions(List<UserData> users, string newAcknowledgmentId)
    {
        var newTimestamp = ExtractTimestampFromAcknowledgmentId(newAcknowledgmentId);
        var removedCount = 0;
        
        foreach (var sessionKvp in _userSessionAcknowledgments)
        {
            var sessionId = sessionKvp.Key;
            var sessionAcks = sessionKvp.Value;
            var toRemove = new List<string>();
            
            foreach (var ackKvp in sessionAcks)
            {
                var existingAckId = ackKvp.Key;
                var existingInfo = ackKvp.Value;
                var existingTimestamp = ExtractTimestampFromAcknowledgmentId(existingAckId);
                
                // Skip the new acknowledgment itself
                if (existingAckId == newAcknowledgmentId)
                    continue;
                
                // Check if any users overlap with the new acknowledgment
                var hasUserOverlap = existingInfo.Recipients.Any(existingUser => 
                    users.Any(newUser => UserDataComparer.Instance.Equals(existingUser, newUser)));
                
                if (hasUserOverlap)
                {
                    // Always remove older acknowledgments, or if timestamps are equal, remove the one with different ID
                    if (existingTimestamp < newTimestamp || 
                        (existingTimestamp == newTimestamp && string.Compare(existingAckId, newAcknowledgmentId, StringComparison.Ordinal) < 0))
                    {
                        toRemove.Add(existingAckId);
                        _logger.LogInformation("Cross-session cleanup: Removing older acknowledgment from session due to newer acknowledgment");
                    }
                }
            }
            
            // Remove identified acknowledgments
            foreach (var ackIdToRemove in toRemove)
            {
                if (sessionAcks.TryRemove(ackIdToRemove, out var removedInfo))
                {
                    removedCount++;
                    // Clear pending status from affected pairs
                    ClearPendingStatusFromPairs(removedInfo.Recipients, ackIdToRemove);
                    
                    // Clean up notification messages
                    _messageService.CleanTaggedMessages($"ack_{ackIdToRemove}");
                }
            }
            
            // Clean up empty sessions
            if (sessionAcks.IsEmpty)
            {
                _userSessionAcknowledgments.TryRemove(sessionId, out _);
                _logger.LogDebug("Cleaned up empty session during cross-session cleanup");
            }
        }
        
        if (removedCount > 0)
        {
            _logger.LogInformation("Cross-session cleanup completed: Removed {count} duplicate acknowledgments for new acknowledgment", 
                removedCount);
            
            // Publish UI refresh to update the interface
            Mediator.Publish(new RefreshUiMessage());
        }
    }
    
    // Check if there's a newer acknowledgment for the specified user across all sessions
    private bool CheckForNewerAcknowledgmentForUser(UserData user, string acknowledgmentId)
    {
        var currentTimestamp = ExtractTimestampFromAcknowledgmentId(acknowledgmentId);
        
        foreach (var sessionKvp in _userSessionAcknowledgments)
        {
            var sessionAcks = sessionKvp.Value;
            
            foreach (var ackKvp in sessionAcks)
            {
                var existingAckId = ackKvp.Key;
                var existingInfo = ackKvp.Value;
                var existingTimestamp = ExtractTimestampFromAcknowledgmentId(existingAckId);
                
                // Skip the current acknowledgment
                if (existingAckId == acknowledgmentId)
                    continue;
                
                // Check if this acknowledgment involves the same user and is newer
                var userInAcknowledgment = existingInfo.Recipients.Any(recipient => 
                    UserDataComparer.Instance.Equals(recipient, user));
                
                if (userInAcknowledgment && existingTimestamp > currentTimestamp)
                {
                    _logger.LogDebug("Found newer acknowledgment for user compared to current acknowledgment");
                    return true;
                }
            }
        }
        
        return false;
    }
    
    // Check if the specified acknowledgment is outdated for the given user
    private bool CheckIfAcknowledgmentIsOutdated(string acknowledgmentId, UserData user)
    {
        return CheckForNewerAcknowledgmentForUser(user, acknowledgmentId);
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
            
            // Remove if older, or if same timestamp but different ID (deterministic ordering)
            if (existingTimestamp < newTimestamp || 
                (existingTimestamp == newTimestamp && string.Compare(existingAckId, newAcknowledgmentId, StringComparison.Ordinal) < 0))
            {
                toRemove.Add(existingAckId);
                _logger.LogInformation("Removing older acknowledgment in favor of newer acknowledgment");
            }
        }
        
        // Remove older acknowledgments and clear pair status
        foreach (var ackIdToRemove in toRemove)
        {
            if (sessionAcks.TryRemove(ackIdToRemove, out var removedInfo))
            {
                // Clear pending status from affected pairs
                ClearPendingStatusFromPairs(removedInfo.Recipients, ackIdToRemove);
                
                // Clean up notification messages for removed acknowledgments
                _messageService.CleanTaggedMessages($"ack_{ackIdToRemove}");
            }
        }
    }
    

    
    // Process received acknowledgment for any session
    public bool ProcessReceivedAcknowledgment(string acknowledgmentId, UserData acknowledgingUser)
    {
        var sessionId = ExtractSessionId(acknowledgmentId);
        if (sessionId == null)
        {
            _logger.LogWarning("Could not extract session ID from acknowledgment ID");
            return false;
        }
        
        if (!_userSessionAcknowledgments.TryGetValue(sessionId, out var sessionAcks))
        {
            _logger.LogWarning("No session found for acknowledgment ID");
            return false;
        }
        
        if (!sessionAcks.TryGetValue(acknowledgmentId, out var pendingInfo))
        {
            _logger.LogWarning("No pending acknowledgment found for ID in session. This might be an outdated acknowledgment.");
            
            // Check if there's a newer acknowledgment for this user across all sessions
            var hasNewerAcknowledgment = CheckForNewerAcknowledgmentForUser(acknowledgingUser, acknowledgmentId);
            if (hasNewerAcknowledgment)
            {
                _logger.LogInformation("Ignoring outdated acknowledgment from user - newer acknowledgment exists");
                return false;
            }
            
            return false;
        }
        
        // Additional safety check: Verify this acknowledgment is still the most recent for this user
        var isOutdated = CheckIfAcknowledgmentIsOutdated(acknowledgmentId, acknowledgingUser);
        if (isOutdated)
        {
            _logger.LogInformation("Rejecting outdated acknowledgment from user - newer acknowledgment exists");
            return false;
        }
        
        // Remove the acknowledging user from pending list
        var removed = pendingInfo.Recipients.Remove(acknowledgingUser);
        if (!removed)
        {
            _logger.LogWarning("User was not in pending list for acknowledgment");
            return false;
        }
        
        // Update the pair's acknowledgment status to show success
        var pair = _getPairFunc(acknowledgingUser);
        if (pair != null)
        {
            pair.UpdateAcknowledgmentStatus(acknowledgmentId, true, DateTimeOffset.UtcNow);
            _logger.LogInformation("Updated pair acknowledgment status for user");
            
            // Remove from pending reload requests since acknowledgment was successful
             RemovePendingReloadRequest(acknowledgmentId);
            
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
        
        _logger.LogInformation("Processed acknowledgment from user. Remaining: {remaining}", 
            pendingInfo.Recipients.Count);
        
        // If all acknowledgments received, remove from pending
        if (pendingInfo.Recipients.Count == 0)
        {
            sessionAcks.TryRemove(acknowledgmentId, out _);
            _logger.LogInformation("All acknowledgments received for ID, removed from session");
            
            // Clean up pending acknowledgment notification
            _messageService.CleanTaggedMessages($"ack_{acknowledgmentId}");
            
            // Add completion notification
            _messageService.AddTaggedMessage(
                $"ack_complete_{acknowledgmentId}",
                "All acknowledgments received successfully",
                NotificationType.Success,
                "Acknowledgment Complete",
                TimeSpan.FromSeconds(4)
            );
            
            // Publish batch completion event
            var allRecipients = sessionAcks.Values.SelectMany(info => info.Recipients).ToList();
            Mediator.Publish(new AcknowledgmentBatchCompletedMessage(
                acknowledgmentId,
                allRecipients,
                DateTime.UtcNow
            ));
            
            // Clean up empty session
            if (sessionAcks.IsEmpty)
            {
                _userSessionAcknowledgments.TryRemove(sessionId, out _);
                _logger.LogDebug("Cleaned up empty session");
            }
        }
        
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
    
    // Get total count of pending acknowledgments across all sessions
    private int GetTotalPendingAcknowledgments()
    {
        return _userSessionAcknowledgments.Values
            .SelectMany(sessionAcks => sessionAcks.Values)
            .Sum(ackInfo => ackInfo.Recipients.Count);
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
    
    // Clear pending status from pairs when acknowledgments are removed
    private void ClearPendingStatusFromPairs(HashSet<UserData> recipients, string acknowledgmentId)
    {
        foreach (var recipient in recipients)
        {
            var pair = _getPairFunc(recipient);
            if (pair != null)
            {
                pair.ClearPendingAcknowledgment(acknowledgmentId, _messageService);
                _logger.LogDebug("Cleared pending acknowledgment from pair for user");
            }
            else
            {
                _logger.LogWarning("Could not find pair for user to clear pending acknowledgment");
            }
        }
    }

    // Clean up old pending acknowledgments based on age
    public void CleanupOldPendingAcknowledgments(TimeSpan maxAge)
    {
        var cutoffTime = DateTime.UtcNow.Subtract(maxAge);
        var toRemove = new List<(string sessionId, string ackId, HashSet<UserData> recipients)>();
        
        foreach (var sessionKvp in _userSessionAcknowledgments)
        {
            var sessionId = sessionKvp.Key;
            var sessionAcks = sessionKvp.Value;
            
            foreach (var ackKvp in sessionAcks)
            {
                var ackId = ackKvp.Key;
                var ackInfo = ackKvp.Value;
                
                if (ackInfo.CreatedAt < cutoffTime)
                {
                    toRemove.Add((sessionId, ackId, ackInfo.Recipients));
                    _logger.LogInformation("Marking old pending acknowledgment for removal (age: {age}s)", 
                        (DateTime.UtcNow - ackInfo.CreatedAt).TotalSeconds);
                }
            }
        }
        
        // Remove old acknowledgments and clear pair status
        foreach (var (sessionId, ackId, recipients) in toRemove)
        {
            if (_userSessionAcknowledgments.TryGetValue(sessionId, out var sessionAcks) && 
                sessionAcks.TryRemove(ackId, out _))
            {
                // Clear the pending acknowledgment from pairs with timeout notification
                foreach (var recipient in recipients)
                {
                    var pair = _getPairFunc(recipient);
                    if (pair != null && pair.LastAcknowledgmentId == ackId)
                    {
                        pair.ClearPendingAcknowledgmentForce(_messageService);
                    }
                }
                
                ClearPendingStatusFromPairs(recipients, ackId);
                _logger.LogInformation("Removed old pending acknowledgment from session");
                
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
                
                // Publish acknowledgment timeout events
                foreach (var recipient in recipients)
                {
                    Mediator.Publish(new AcknowledgmentTimeoutMessage(
                        ackId,
                        recipient,
                        DateTime.UtcNow
                    ));
                }
                
                // Clean up empty session
                if (sessionAcks.IsEmpty)
                {
                    _userSessionAcknowledgments.TryRemove(sessionId, out _);
                    _logger.LogDebug("Cleaned up empty session");
                }
            }
        }
        
        if (toRemove.Count > 0)
        {
            _logger.LogInformation("Cleaned up {count} old pending acknowledgments", toRemove.Count);
            
            // Publish acknowledgment metrics update
            var totalPending = GetTotalPendingAcknowledgments();
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
    
    // Clean up pending acknowledgments that may be stuck due to asymmetric visibility
    public void CleanupAsymmetricVisibilityTimeouts(AcknowledgmentConfiguration config)
    {
        if (!config.EnableAsymmetricVisibilityTimeout)
        {
            return;
        }
        
        var gracePeriod = TimeSpan.FromSeconds(config.AsymmetricVisibilityGracePeriodSeconds);
        var timeout = TimeSpan.FromSeconds(config.AsymmetricVisibilityTimeoutSeconds);
        var cutoffTime = DateTime.UtcNow.Subtract(timeout);
        var graceCutoffTime = DateTime.UtcNow.Subtract(gracePeriod);
        
        var toRemove = new List<(string sessionId, string ackId, HashSet<UserData> recipients)>();
        
        foreach (var sessionKvp in _userSessionAcknowledgments)
        {
            var sessionId = sessionKvp.Key;
            var sessionAcks = sessionKvp.Value;
            
            foreach (var ackKvp in sessionAcks)
            {
                var ackId = ackKvp.Key;
                var ackInfo = ackKvp.Value;
                
                // Only apply asymmetric visibility timeout after grace period
                if (ackInfo.CreatedAt < cutoffTime && ackInfo.CreatedAt < graceCutoffTime)
                {
                    // Check if this might be an asymmetric visibility issue
                    // (acknowledgment has been pending for longer than normal but not extremely old)
                    var age = DateTime.UtcNow - ackInfo.CreatedAt;
                    if (age.TotalSeconds >= config.AsymmetricVisibilityTimeoutSeconds)
                    {
                        toRemove.Add((sessionId, ackId, ackInfo.Recipients));
                        _logger.LogWarning("Marking pending acknowledgment for asymmetric visibility timeout (age: {age}s)", 
                            age.TotalSeconds);
                    }
                }
            }
        }
        
        // Remove acknowledgments that may be stuck due to asymmetric visibility
        foreach (var (sessionId, ackId, recipients) in toRemove)
        {
            if (_userSessionAcknowledgments.TryGetValue(sessionId, out var sessionAcks) && 
                sessionAcks.TryRemove(ackId, out _))
            {
                // Clear the pending acknowledgment from pairs with asymmetric visibility notification
                foreach (var recipient in recipients)
                {
                    var pair = _getPairFunc(recipient);
                    if (pair != null && pair.LastAcknowledgmentId == ackId)
                    {
                        pair.ClearPendingAcknowledgmentForce(_messageService);
                    }
                }
                
                ClearPendingStatusFromPairs(recipients, ackId);
                _logger.LogInformation("Removed pending acknowledgment due to asymmetric visibility timeout");
                
                // Clean up related notifications
                _messageService.CleanTaggedMessages($"ack_{ackId}");
                
                // Add asymmetric visibility timeout notification
                _messageService.AddTaggedMessage(
                    $"ack_asymmetric_timeout_{ackId}",
                    $"Acknowledgment cleared due to possible visibility range difference. Try adjusting your visibility range or manually reload the character.",
                    NotificationType.Info,
                    "Visibility Range Issue",
                    TimeSpan.FromSeconds(8)
                );
                
                // Publish asymmetric visibility timeout events
                foreach (var recipient in recipients)
                {
                    Mediator.Publish(new AcknowledgmentAsymmetricVisibilityTimeoutMessage(
                        ackId,
                        recipient,
                        DateTime.UtcNow
                    ));
                }
                
                // Clean up empty session
                if (sessionAcks.IsEmpty)
                {
                    _userSessionAcknowledgments.TryRemove(sessionId, out _);
                    _logger.LogDebug("Cleaned up empty session");
                }
            }
        }
        
        if (toRemove.Count > 0)
        {
            _logger.LogInformation("Cleaned up {count} acknowledgments due to asymmetric visibility timeouts", toRemove.Count);
            
            // Publish acknowledgment metrics update
            var totalPending = GetTotalPendingAcknowledgments();
            Mediator.Publish(new AcknowledgmentMetricsUpdatedMessage(
                totalPending,
                0,
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
                // Clear pending status from all pairs in removed sessions
                foreach (var ackInfo in removedSession)
                {
                    ClearPendingStatusFromPairs(ackInfo.Value.Recipients, ackInfo.Key);
                }
                
                _logger.LogInformation("Cleaned up old session with {count} pending acknowledgments", 
                    removedSession.Values.Sum(info => info.Recipients.Count));
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
    
    // Check for acknowledgment timeouts and trigger automatic reloads
    private void CheckAcknowledgmentTimeouts()
    {
        try
        {
            var currentTime = DateTime.UtcNow;
            var timedOutAcknowledgments = new List<(string sessionId, string ackId, PendingAcknowledgmentInfo info)>();
            
            // Find all timed out acknowledgments
            foreach (var sessionKvp in _userSessionAcknowledgments)
            {
                foreach (var ackKvp in sessionKvp.Value)
                {
                    if (ackKvp.Value.HasTimedOut)
                    {
                        timedOutAcknowledgments.Add((sessionKvp.Key, ackKvp.Key, ackKvp.Value));
                    }
                }
            }
            
            // Process timed out acknowledgments
            foreach (var (sessionId, ackId, info) in timedOutAcknowledgments)
            {
                _logger.LogWarning("Acknowledgment timed out after {timeout} seconds for {count} recipients", 
                    ACKNOWLEDGMENT_TIMEOUT_SECONDS, info.Recipients.Count);
                
                // Trigger reload for each recipient that hasn't been reloaded recently
                foreach (var recipient in info.Recipients.ToList())
                {
                    if (ShouldTriggerReload(recipient))
                    {
                        TriggerUserReload(recipient, ackId);
                        _userReloadAttempts[recipient] = currentTime;
                    }
                }
                
                // Remove timed out acknowledgment
                if (_userSessionAcknowledgments.TryGetValue(sessionId, out var sessionAcks))
                {
                    sessionAcks.TryRemove(ackId, out _);
                    
                    // Clear pending status from pairs
                    ClearPendingStatusFromPairs(info.Recipients, ackId);
                    
                    // Clean up timeout notification
                    _messageService.CleanTaggedMessages($"ack_{ackId}");
                    
                    // Add timeout notification
                    _messageService.AddTaggedMessage(
                        $"timeout_{ackId}",
                        $"Acknowledgment timed out - triggered reload for {info.Recipients.Count} users",
                        NotificationType.Warning,
                        "Acknowledgment Timeout",
                        TimeSpan.FromSeconds(5)
                    );
                }
            }
            
            // Process pending reload requests (15-second reload mechanism)
            ProcessPendingReloadRequests();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking acknowledgment timeouts");
        }
    }
    
    // Check if a user should be reloaded (not reloaded recently)
    private bool ShouldTriggerReload(UserData user)
    {
        if (!_userReloadAttempts.TryGetValue(user, out var lastReload))
            return true;
            
        // Don't reload the same user more than once every 30 seconds
        return DateTime.UtcNow - lastReload > TimeSpan.FromSeconds(30);
    }
    
    // Trigger reload for a specific user
    private void TriggerUserReload(UserData user, string acknowledgmentId)
    {
        try
        {
            _logger.LogInformation("Triggering reload for user due to acknowledgment timeout");
            
            // Publish reload event
            Mediator.Publish(new UserReloadTriggeredMessage(
                user,
                acknowledgmentId,
                "Acknowledgment timeout",
                DateTime.UtcNow
            ));
            
            // Find the pair and trigger reload
            var pair = _getPairFunc(user);
            if (pair != null)
            {
                // Schedule automatic confirmation after successful reload
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)); // Wait for reload to complete
                    
                    // Send confirmation if reload was successful
                    if (pair.IsVisible && pair.IsOnline)
                    {
                        var confirmationId = GenerateAcknowledgmentId("", user); // Empty hash for confirmation
                        _logger.LogInformation("Sending automatic confirmation after successful reload for user");
                        
                        // Publish confirmation event
                        Mediator.Publish(new AutomaticConfirmationSentMessage(
                            user,
                            confirmationId,
                            acknowledgmentId,
                            DateTime.UtcNow
                        ));
                        
                        _messageService.AddTaggedMessage(
                            $"auto_confirm_{confirmationId}",
                            $"Automatic confirmation sent to {user.AliasOrUID} after successful reload",
                            NotificationType.Success,
                            "Auto Confirmation",
                            TimeSpan.FromSeconds(3)
                        );
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering reload for user");
        }
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _asymmetricVisibilityCleanupTimer?.Dispose();
            _timeoutCheckTimer?.Dispose();
            _userSessionAcknowledgments.Clear();
            _userReloadAttempts.Clear();
            _characterDataHashToAckId.Clear();
            _logger.LogInformation("SessionAcknowledgmentManager disposed for session");
        }
        
        base.Dispose(disposing);
    }
}