using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Sphene.WebAPI.SignalR.Utils;

public enum CircuitBreakerState
{
    Closed,    // Normal operation
    Open,      // Circuit is open, calls fail fast
    HalfOpen   // Testing if service has recovered
}

public class CircuitBreakerService : IDisposable
{
    private readonly ILogger<CircuitBreakerService> _logger;
    private readonly object _lockObject = new();
    private readonly Timer _resetTimer;
    
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _failureCount = 0;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private bool _disposed = false;
    
    // Configuration
    private const int FailureThreshold = 5;
    private const int TimeoutSeconds = 60;
    private const int HalfOpenMaxAttempts = 3;
    
    private int _halfOpenAttempts = 0;

    public CircuitBreakerState State => _state;
    public int FailureCount => _failureCount;
    public bool IsOpen => _state == CircuitBreakerState.Open;
    public bool IsClosed => _state == CircuitBreakerState.Closed;

    public CircuitBreakerService(ILogger<CircuitBreakerService> logger)
    {
        _logger = logger;
        _resetTimer = new Timer(CheckForReset, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _logger.LogDebug("Circuit breaker service initialized");
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, string operationName = "Unknown")
    {
        if (IsOpen)
        {
            _logger.LogWarning("Circuit breaker is open, failing fast for operation: {operation}", operationName);
            throw new CircuitBreakerOpenException($"Circuit breaker is open for operation: {operationName}");
        }

        try
        {
            var result = await operation().ConfigureAwait(false);
            OnSuccess();
            return result;
        }
        catch (Exception ex)
        {
            OnFailure(ex, operationName);
            throw;
        }
    }

    public async Task ExecuteAsync(Func<Task> operation, string operationName = "Unknown")
    {
        if (IsOpen)
        {
            _logger.LogWarning("Circuit breaker is open, failing fast for operation: {operation}", operationName);
            throw new CircuitBreakerOpenException($"Circuit breaker is open for operation: {operationName}");
        }

        try
        {
            await operation().ConfigureAwait(false);
            OnSuccess();
        }
        catch (Exception ex)
        {
            OnFailure(ex, operationName);
            throw;
        }
    }

    private void OnSuccess()
    {
        lock (_lockObject)
        {
            if (_state == CircuitBreakerState.HalfOpen)
            {
                _logger.LogInformation("Circuit breaker test successful, closing circuit");
                _state = CircuitBreakerState.Closed;
                _halfOpenAttempts = 0;
            }
            
            _failureCount = 0;
        }
    }

    private void OnFailure(Exception exception, string operationName)
    {
        lock (_lockObject)
        {
            _failureCount++;
            _lastFailureTime = DateTime.UtcNow;
            
            _logger.LogWarning(exception, 
                "Circuit breaker recorded failure for operation: {operation}. Failure count: {count}", 
                operationName, _failureCount);

            if (_state == CircuitBreakerState.HalfOpen)
            {
                _halfOpenAttempts++;
                if (_halfOpenAttempts >= HalfOpenMaxAttempts)
                {
                    _logger.LogWarning("Half-open test failed, opening circuit again");
                    _state = CircuitBreakerState.Open;
                    _halfOpenAttempts = 0;
                }
            }
            else if (_state == CircuitBreakerState.Closed && _failureCount >= FailureThreshold)
            {
                _logger.LogError("Failure threshold exceeded, opening circuit breaker");
                _state = CircuitBreakerState.Open;
            }
        }
    }

    private void CheckForReset(object? state)
    {
        if (_disposed) return;
        
        lock (_lockObject)
        {
            if (_state == CircuitBreakerState.Open && 
                DateTime.UtcNow - _lastFailureTime > TimeSpan.FromSeconds(TimeoutSeconds))
            {
                _logger.LogInformation("Circuit breaker timeout elapsed, moving to half-open state");
                _state = CircuitBreakerState.HalfOpen;
                _halfOpenAttempts = 0;
            }
        }
    }

    public void Reset()
    {
        lock (_lockObject)
        {
            _logger.LogInformation("Circuit breaker manually reset");
            _state = CircuitBreakerState.Closed;
            _failureCount = 0;
            _halfOpenAttempts = 0;
        }
    }

    public CircuitBreakerStatus GetStatus()
    {
        lock (_lockObject)
        {
            return new CircuitBreakerStatus
            {
                State = _state,
                FailureCount = _failureCount,
                LastFailureTime = _lastFailureTime,
                HalfOpenAttempts = _halfOpenAttempts
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _resetTimer?.Dispose();
        _logger.LogDebug("Circuit breaker service disposed");
    }
}

public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
    public CircuitBreakerOpenException(string message, Exception innerException) : base(message, innerException) { }
}

public class CircuitBreakerStatus
{
    public CircuitBreakerState State { get; set; }
    public int FailureCount { get; set; }
    public DateTime LastFailureTime { get; set; }
    public int HalfOpenAttempts { get; set; }
}