using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;
using Sphene.PlayerData.Pairs;
using Sphene.WebAPI;
using System.Collections.Concurrent;
using AckStatus = Sphene.Services.Events.AcknowledgmentStatus;

namespace Sphene.Services;

public sealed class AcknowledgmentRequestSystem : DisposableMediatorSubscriberBase
{
    private readonly ILogger<AcknowledgmentRequestSystem> _logger;
    private readonly PairManager _pairManager;
    private readonly CharacterHashTracker _hashTracker;
    private readonly ApiController _apiController;
    private readonly VisibilityCheckService _visibilityCheckService;
    private readonly ConcurrentDictionary<string, AcknowledgmentRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, AcknowledgmentStatus> _playerAckStatus = new();
    private readonly ConcurrentDictionary<string, PendingVisibilityRequest> _pendingVisibilityRequests = new();
    private readonly Timer _requestTimeoutTimer;

    public AcknowledgmentRequestSystem(ILogger<AcknowledgmentRequestSystem> logger, 
        SpheneMediator mediator, 
        PairManager pairManager, 
        CharacterHashTracker hashTracker,
        ApiController apiController,
        VisibilityCheckService visibilityCheckService) : base(logger, mediator)
    {
        _logger = logger;
        _pairManager = pairManager;
        _hashTracker = hashTracker;
        _apiController = apiController;
        _visibilityCheckService = visibilityCheckService;
        _requestTimeoutTimer = new Timer(ProcessTimeouts, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        
        Mediator.Subscribe<TriggerAcknowledgmentRequestMessage>(this, OnTriggerAcknowledgmentRequest);
        Mediator.Subscribe<PlayerHashChangedMessage>(this, OnPlayerHashChanged);
        Mediator.Subscribe<RefreshUiMessage>(this, async (_) => await ProcessPendingVisibilityRequests());
    }
    
    public AcknowledgmentStatus GetPlayerAcknowledgmentStatus(string playerIdentifier)
    {
        return _playerAckStatus.TryGetValue(playerIdentifier, out var status) ? status : AcknowledgmentStatus.Unknown;
    }
    
    public async Task SendAcknowledgmentRequestAsync(string targetPlayerIdentifier, string currentHash)
    {

        
        // First check mutual visibility before sending hash request
        _playerAckStatus[targetPlayerIdentifier] = AcknowledgmentStatus.Pending;
        
        try
        {
            var hasMutualVisibility = await _visibilityCheckService.CheckMutualVisibilityAsync(targetPlayerIdentifier);
            
            if (!hasMutualVisibility)
            {

                
                // Create pending visibility request
                var visibilityRequestId = Guid.NewGuid().ToString();
                var pendingRequest = new PendingVisibilityRequest
                {
                    RequestId = visibilityRequestId,
                    TargetPlayer = targetPlayerIdentifier,
                    PendingHash = currentHash,
                    Timestamp = DateTime.UtcNow
                };
                
                _pendingVisibilityRequests[visibilityRequestId] = pendingRequest;
                _playerAckStatus[targetPlayerIdentifier] = AcknowledgmentStatus.PendingVisibility;
                

                return;
            }
            
            // Mutual visibility confirmed, proceed with hash request
            await SendHashRequestWithVisibilityConfirmed(targetPlayerIdentifier, currentHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during visibility check for {player}", targetPlayerIdentifier);
            _playerAckStatus[targetPlayerIdentifier] = AcknowledgmentStatus.Failed;
        }
    }
    
    private async Task SendHashRequestWithVisibilityConfirmed(string targetPlayerIdentifier, string currentHash)
    {
        var requestId = Guid.NewGuid().ToString();
        var request = new AcknowledgmentRequest
        {
            RequestId = requestId,
            TargetPlayer = targetPlayerIdentifier,
            RequestedHash = currentHash,
            Timestamp = DateTime.UtcNow,
            Status = RequestStatus.Pending
        };
        
        _pendingRequests[requestId] = request;

        
        try
        {
            var success = await SendHashAcknowledgmentRequestViaApi(targetPlayerIdentifier, currentHash, requestId);
            if (!success)
            {
                _logger.LogWarning("Failed to send acknowledgment request to {player}", targetPlayerIdentifier);
                _playerAckStatus[targetPlayerIdentifier] = AcknowledgmentStatus.Failed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending acknowledgment request to {player}", targetPlayerIdentifier);
            _playerAckStatus[targetPlayerIdentifier] = AcknowledgmentStatus.Failed;
        }
    }
    
    public void HandleAcknowledgmentResponse(string requestId, string playerIdentifier, bool hasMatchingHash)
    {
        if (_pendingRequests.TryGetValue(requestId, out var request))
        {
            request.Status = RequestStatus.Completed;
            request.Response = hasMatchingHash;
            
            var status = hasMatchingHash ? AcknowledgmentStatus.Acknowledged : AcknowledgmentStatus.OutOfSync;
            _playerAckStatus[playerIdentifier] = status;
            

            
            // Publish status update for UI
            Mediator.Publish(new AcknowledgmentStatusUpdatedMessage(playerIdentifier, status));
        }
    }
    
    private async void OnTriggerAcknowledgmentRequest(TriggerAcknowledgmentRequestMessage message)
    {
        if (_hashTracker.CurrentPlayerHash == null) return;
        
        var visibleUsers = _pairManager.GetVisibleUsers();
        var currentHash = _hashTracker.CurrentPlayerHash;
        
        foreach (var user in visibleUsers)
        {
            if (message.PlayerIdentifiers.Contains("self") || message.PlayerIdentifiers.Contains(user.UID))
            {
                await SendAcknowledgmentRequestAsync(user.UID, currentHash);
            }
        }
    }
    
    private async void OnPlayerHashChanged(PlayerHashChangedMessage message)
    {
        // Reset all acknowledgment statuses when our hash changes
        var visibleUsers = _pairManager.GetVisibleUsers();
        foreach (var user in visibleUsers)
        {
            _playerAckStatus[user.UID] = AcknowledgmentStatus.Pending;
        }
        
        // Publish status update for UI
        Mediator.Publish(new AllAcknowledgmentStatusResetMessage());
        
        // Trigger acknowledgment requests for all visible users
        if (message.NewHash != null)
        {
            foreach (var user in visibleUsers)
            {
                await SendAcknowledgmentRequestAsync(user.UID, message.NewHash);
            }
        }
    }
    
    private void ProcessTimeouts(object? state)
    {
        var now = DateTime.UtcNow;
        var timeoutThreshold = TimeSpan.FromSeconds(30);
        
        var timedOutRequests = _pendingRequests.Values
            .Where(r => r.Status == RequestStatus.Pending && now - r.Timestamp > timeoutThreshold)
            .ToList();
            
        foreach (var request in timedOutRequests)
        {
            request.Status = RequestStatus.TimedOut;
            _playerAckStatus[request.TargetPlayer] = AcknowledgmentStatus.Timeout;
            

            
            // Publish timeout status for UI
            Mediator.Publish(new AcknowledgmentStatusUpdatedMessage(request.TargetPlayer, AcknowledgmentStatus.Timeout));
        }
    }
    
    private async Task<bool> SendHashAcknowledgmentRequestViaApi(string targetPlayer, string hash, string requestId)
    {
        // This would need to be implemented in the API controller
        // For now, return true as placeholder
        await Task.Delay(1); // Placeholder
        return true;
    }
    
    // Gets the count of pending acknowledgment requests
    public int GetPendingRequestCount()
    {
        return _pendingRequests.Count;
    }
    
    // Gets the status of a specific acknowledgment request
    public HashAcknowledgmentRequestStatus GetRequestStatus(string userUid)
    {
        if (_pendingRequests.Values.Any(r => r.TargetPlayer == userUid))
        {
            var request = _pendingRequests.Values.First(r => r.TargetPlayer == userUid);
            var status = request.Status == RequestStatus.TimedOut ? "Timeout" : "Pending";
            return new HashAcknowledgmentRequestStatus
            {
                Status = status,
                RequestSentAt = request.Timestamp,
                ResponseReceivedAt = null
            };
        }
        
        if (_playerAckStatus.TryGetValue(userUid, out var ackStatus) && ackStatus == AcknowledgmentStatus.Acknowledged)
        {
            return new HashAcknowledgmentRequestStatus
            {
                Status = "Confirmed",
                RequestSentAt = null,
                ResponseReceivedAt = DateTime.UtcNow // Approximation
            };
        }
        
        return new HashAcknowledgmentRequestStatus
        {
            Status = "Unknown",
            RequestSentAt = null,
            ResponseReceivedAt = null
        };
    }
    
    // Process pending visibility requests when visibility changes
    public async Task ProcessPendingVisibilityRequests()
    {
        var visibleUsers = _pairManager.GetVisibleUsers();
        var requestsToProcess = new List<PendingVisibilityRequest>();
        
        foreach (var kvp in _pendingVisibilityRequests)
        {
            var request = kvp.Value;
            var targetUser = visibleUsers.FirstOrDefault(u => u.UID == request.TargetPlayer);
            
            if (targetUser != null)
            {
                // User is now visible, check mutual visibility
                try
                {
                    var hasMutualVisibility = await _visibilityCheckService.CheckMutualVisibilityAsync(request.TargetPlayer);
                    if (hasMutualVisibility)
                    {
                        requestsToProcess.Add(request);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error checking mutual visibility for pending request {requestId}", request.RequestId);
                }
            }
        }
        
        // Process confirmed visibility requests
        foreach (var request in requestsToProcess)
        {

            
            // Remove from pending visibility requests
            _pendingVisibilityRequests.TryRemove(request.RequestId, out _);
            
            // Send the hash request now that visibility is confirmed
            await SendHashRequestWithVisibilityConfirmed(request.TargetPlayer, request.PendingHash);
        }
    }
    
    // Gets the count of pending visibility requests
    public int GetPendingVisibilityRequestCount()
    {
        return _pendingVisibilityRequests.Count;
    }
    
    // Clears all pending requests
    public void ClearAllRequests()
    {
        _pendingRequests.Clear();
        _pendingVisibilityRequests.Clear();
        _playerAckStatus.Clear();
        _logger.LogInformation("Cleared all acknowledgment requests");
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _requestTimeoutTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}

// Data classes
public class AcknowledgmentRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string TargetPlayer { get; set; } = string.Empty;
    public string RequestedHash { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public RequestStatus Status { get; set; }
    public bool? Response { get; set; }
}

public enum RequestStatus
{
    Pending,
    Completed,
    TimedOut,
    Failed
}

public enum AcknowledgmentStatus
{
    Unknown,
    Pending,
    PendingVisibility,
    Acknowledged,
    OutOfSync,
    Timeout,
    Failed
}

// Represents the status of an acknowledgment request
public class HashAcknowledgmentRequestStatus
{
    public string Status { get; set; } = "Unknown";
    public DateTime? RequestSentAt { get; set; }
    public DateTime? ResponseReceivedAt { get; set; }
}

// Pending visibility request data class
public class PendingVisibilityRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string TargetPlayer { get; set; } = string.Empty;
    public string PendingHash { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

// Message classes
public record AcknowledgmentStatusUpdatedMessage(string PlayerIdentifier, AcknowledgmentStatus Status) : MessageBase;
public record AllAcknowledgmentStatusResetMessage() : MessageBase;