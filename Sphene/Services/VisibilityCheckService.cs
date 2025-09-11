using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;
using Sphene.Services.Mediator.Messages;
using Sphene.PlayerData.Pairs;
using System.Collections.Concurrent;
using Sphene.API.Data;

namespace Sphene.Services;

public sealed class VisibilityCheckService : DisposableMediatorSubscriberBase
{
    private readonly ILogger<VisibilityCheckService> _logger;
    private readonly PairManager _pairManager;
    private readonly ConcurrentDictionary<string, VisibilityCheckRequest> _pendingVisibilityChecks = new();
    private readonly Timer _cleanupTimer;

    public VisibilityCheckService(ILogger<VisibilityCheckService> logger,
        SpheneMediator mediator,
        PairManager pairManager) : base(logger, mediator)
    {
        _logger = logger;
        _pairManager = pairManager;
        _cleanupTimer = new Timer(CleanupExpiredRequests, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        
        // Subscribe to mediator messages
        Mediator.Subscribe<VisibilityCheckRequestMessage>(this, async (msg) => await HandleVisibilityCheckRequestAsync(msg.FromPlayerIdentifier, msg.RequestId));
        Mediator.Subscribe<VisibilityCheckResponseMessage>(this, (msg) => HandleVisibilityCheckResponse(msg.RequestId, msg.CanSeeUs));
    }

    // Check if both parties can see each other before allowing hash comparison
    // For now, this is a simplified implementation that only checks local visibility
    // TODO: Implement full mutual visibility check when server-side support is available
    public async Task<bool> CheckMutualVisibilityAsync(string targetPlayerIdentifier)
    {
        var visibleUsers = _pairManager.GetVisibleUsers();
        var targetUser = visibleUsers.FirstOrDefault(u => u.UID == targetPlayerIdentifier);
        
        if (targetUser == null)
        {
            _logger.LogDebug("Target player {player} is not visible to us", targetPlayerIdentifier);
            return false;
        }

        // For now, assume mutual visibility if we can see them
        // This is a temporary solution until server-side visibility checks are implemented
        _logger.LogDebug("Local visibility confirmed for {player}, assuming mutual visibility", targetPlayerIdentifier);
        
        // Simulate a small delay to mimic network communication
        await Task.Delay(50);
        
        return true;
    }

    // Handle incoming visibility check request from another player
    public async Task<bool> HandleVisibilityCheckRequestAsync(string fromPlayerIdentifier, string requestId)
    {
        _logger.LogDebug("Received visibility check request from {player}", fromPlayerIdentifier);
        
        var visibleUsers = _pairManager.GetVisibleUsers();
        var fromUser = visibleUsers.FirstOrDefault(u => u.UID == fromPlayerIdentifier);
        
        bool canSeePlayer = fromUser != null;
        
        _logger.LogDebug("Visibility check result for {player}: {canSee}", fromPlayerIdentifier, canSeePlayer);
        
        try
        {
            // Send response back via API
            await SendVisibilityCheckResponseViaApi(fromPlayerIdentifier, requestId, canSeePlayer);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending visibility check response to {player}", fromPlayerIdentifier);
            return false;
        }
    }

    // Handle incoming visibility check response
    public void HandleVisibilityCheckResponse(string requestId, bool canSeeUs)
    {
        if (_pendingVisibilityChecks.TryGetValue(requestId, out var request))
        {
            request.Status = canSeeUs ? VisibilityCheckStatus.Confirmed : VisibilityCheckStatus.Failed;
            request.Response = canSeeUs;
            
            _logger.LogDebug("Received visibility check response for request {requestId}: {canSee}", 
                requestId, canSeeUs);
        }
        else
        {
            _logger.LogWarning("Received visibility check response for unknown request {requestId}", requestId);
        }
    }

    private async Task<bool> SendVisibilityCheckRequestViaApi(string targetPlayer, string requestId)
    {
        // API communication will be handled by mediator messages in the future
        // For now, return true to avoid blocking
        await Task.Delay(1);
        return true;
    }

    private async Task SendVisibilityCheckResponseViaApi(string targetPlayer, string requestId, bool canSeePlayer)
    {
        // API communication will be handled by mediator messages in the future
        await Task.Delay(1);
    }



    private void CleanupExpiredRequests(object? state)
    {
        var expiredRequests = _pendingVisibilityChecks
            .Where(kvp => DateTime.UtcNow - kvp.Value.Timestamp > TimeSpan.FromMinutes(5))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var requestId in expiredRequests)
        {
            _pendingVisibilityChecks.TryRemove(requestId, out _);
        }

        if (expiredRequests.Count > 0)
        {
            _logger.LogDebug("Cleaned up {count} expired visibility check requests", expiredRequests.Count);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cleanupTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}

// Data classes
public class VisibilityCheckRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string TargetPlayer { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public VisibilityCheckStatus Status { get; set; }
    public bool? Response { get; set; }
}

public enum VisibilityCheckStatus
{
    Pending,
    Confirmed,
    Failed,
    Timeout
}