using Sphene.SpheneConfiguration.Models;
using Sphene.Services.Mediator;
using Microsoft.AspNetCore.SignalR.Client;

namespace Sphene.WebAPI.SignalR.Utils;

public class ForeverRetryPolicy : IRetryPolicy
{
    private readonly SpheneMediator _mediator;
    private readonly Random _random = new();
    private bool _sentDisconnected = false;
    
    // Configuration constants for exponential backoff
    private const int BaseDelaySeconds = 2;
    private const int MaxDelaySeconds = 120; // 2 minutes max
    private const double JitterFactor = 0.1; // 10% jitter
    private const int NotificationThreshold = 3; // Show notification after 3 failed attempts

    public ForeverRetryPolicy(SpheneMediator mediator)
    {
        _mediator = mediator;
    }

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var retryCount = retryContext.PreviousRetryCount;
        
        // Reset disconnected flag on first retry
        if (retryCount == 0)
        {
            _sentDisconnected = false;
        }
        
        // Calculate exponential backoff delay
        var baseDelay = Math.Min(BaseDelaySeconds * Math.Pow(2, retryCount), MaxDelaySeconds);
        
        // Add jitter to prevent thundering herd problem
        var jitter = baseDelay * JitterFactor * (_random.NextDouble() * 2 - 1); // -10% to +10%
        var finalDelay = Math.Max(1, baseDelay + jitter); // Minimum 1 second
        
        // Send notification after threshold attempts
        if (retryCount >= NotificationThreshold && !_sentDisconnected)
        {
            _mediator.Publish(new NotificationMessage(
                "Connection Issues", 
                $"Connection lost to server. Retrying... (Attempt {retryCount + 1})", 
                NotificationType.Warning, 
                TimeSpan.FromSeconds(10)
            ));
            _mediator.Publish(new DisconnectedMessage());
            _sentDisconnected = true;
        }
        
        return TimeSpan.FromSeconds(finalDelay);
    }
}
