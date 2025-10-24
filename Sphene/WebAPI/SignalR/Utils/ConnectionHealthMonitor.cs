using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration.Models;

namespace Sphene.WebAPI.SignalR.Utils;

public class ConnectionHealthMonitor : IDisposable
{
    private readonly ILogger<ConnectionHealthMonitor> _logger;
    private readonly SpheneMediator _mediator;
    private readonly Timer _healthCheckTimer;
    private readonly object _lockObject = new();
    
    private bool _disposed = false;
    private int _consecutiveFailures = 0;
    private DateTime _lastSuccessfulConnection = DateTime.UtcNow;
    private DateTime _lastHealthCheck = DateTime.UtcNow;
    
    // Configuration
    private const int HealthCheckIntervalSeconds = 30;
    private const int MaxConsecutiveFailures = 5;
    private const int UnhealthyThresholdMinutes = 5;
    
    public bool IsHealthy { get; private set; } = true;
    public TimeSpan TimeSinceLastSuccess => DateTime.UtcNow - _lastSuccessfulConnection;
    public int ConsecutiveFailures => _consecutiveFailures;

    public ConnectionHealthMonitor(ILogger<ConnectionHealthMonitor> logger, SpheneMediator mediator)
    {
        _logger = logger;
        _mediator = mediator;
        
        _healthCheckTimer = new Timer(
            PerformHealthCheck, 
            null, 
            TimeSpan.FromSeconds(HealthCheckIntervalSeconds), 
            TimeSpan.FromSeconds(HealthCheckIntervalSeconds)
        );
        
        _logger.LogDebug("Connection health monitor initialized");
    }

    public void RecordSuccessfulConnection()
    {
        lock (_lockObject)
        {
            _consecutiveFailures = 0;
            _lastSuccessfulConnection = DateTime.UtcNow;
            
            if (!IsHealthy)
            {
                IsHealthy = true;
                _logger.LogInformation("Connection health restored after {failures} consecutive failures", _consecutiveFailures);
                
                _mediator.Publish(new NotificationMessage(
                    "Connection Restored",
                    "Connection to server has been restored",
                    NotificationType.Success,
                    TimeSpan.FromSeconds(5)
                ));
            }
        }
    }

    public void RecordConnectionFailure(Exception? exception = null)
    {
        lock (_lockObject)
        {
            _consecutiveFailures++;
            
            _logger.LogWarning(exception, 
                "Connection failure recorded. Consecutive failures: {failures}", 
                _consecutiveFailures);
            
            // Mark as unhealthy if we exceed threshold
            if (_consecutiveFailures >= MaxConsecutiveFailures && IsHealthy)
            {
                IsHealthy = false;
                _logger.LogError(
                    "Connection marked as unhealthy after {failures} consecutive failures", 
                    _consecutiveFailures);
                
                _mediator.Publish(new NotificationMessage(
                    "Connection Unstable",
                    $"Connection has failed {_consecutiveFailures} times consecutively",
                    NotificationType.Error,
                    TimeSpan.FromSeconds(15)
                ));
            }
        }
    }

    public ConnectionHealthStatus GetHealthStatus()
    {
        lock (_lockObject)
        {
            return new ConnectionHealthStatus
            {
                IsHealthy = IsHealthy,
                ConsecutiveFailures = _consecutiveFailures,
                TimeSinceLastSuccess = TimeSinceLastSuccess,
                LastHealthCheck = _lastHealthCheck
            };
        }
    }

    private void PerformHealthCheck(object? state)
    {
        if (_disposed) return;
        
        try
        {
            lock (_lockObject)
            {
                _lastHealthCheck = DateTime.UtcNow;
                
                // Check if we've been disconnected for too long
                if (TimeSinceLastSuccess.TotalMinutes > UnhealthyThresholdMinutes && IsHealthy)
                {
                    IsHealthy = false;
                    _logger.LogWarning(
                        "Connection marked as unhealthy due to prolonged disconnection ({minutes} minutes)",
                        TimeSinceLastSuccess.TotalMinutes);
                }
                
                _logger.LogDebug(
                    "Health check completed. Healthy: {healthy}, Consecutive failures: {failures}, Time since last success: {time}",
                    IsHealthy, _consecutiveFailures, TimeSinceLastSuccess);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during health check");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _healthCheckTimer?.Dispose();
        _logger.LogDebug("Connection health monitor disposed");
    }
}

public class ConnectionHealthStatus
{
    public bool IsHealthy { get; set; }
    public int ConsecutiveFailures { get; set; }
    public TimeSpan TimeSinceLastSuccess { get; set; }
    public DateTime LastHealthCheck { get; set; }
}