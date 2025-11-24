using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Sphene.WebAPI.SignalR.Utils;


public class CircuitBreakerService : IDisposable
{
    private readonly ILogger<CircuitBreakerService> _logger;
    private readonly Lock _lockObject = new();
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
        _lockObject.Enter();
        try
        {
            if (_state == CircuitBreakerState.HalfOpen)
            {
                _logger.LogInformation("Circuit breaker test successful, closing circuit");
                _state = CircuitBreakerState.Closed;
                _halfOpenAttempts = 0;
            }
            _failureCount = 0;
        }
        finally
        {
            _lockObject.Exit();
        }
    }

    private void OnFailure(Exception exception, string operationName)
    {
        _lockObject.Enter();
        try
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
        finally
        {
            _lockObject.Exit();
        }
    }

    private void CheckForReset(object? state)
    {
        if (_disposed) return;
        
        _lockObject.Enter();
        try
        {
            if (_state == CircuitBreakerState.Open && 
                DateTime.UtcNow - _lastFailureTime > TimeSpan.FromSeconds(TimeoutSeconds))
            {
                _logger.LogInformation("Circuit breaker timeout elapsed, moving to half-open state");
                _state = CircuitBreakerState.HalfOpen;
                _halfOpenAttempts = 0;
            }
        }
        finally
        {
            _lockObject.Exit();
        }
    }

    public void Reset()
    {
        _lockObject.Enter();
        try
        {
            _logger.LogInformation("Circuit breaker manually reset");
            _state = CircuitBreakerState.Closed;
            _failureCount = 0;
            _halfOpenAttempts = 0;
        }
        finally
        {
            _lockObject.Exit();
        }
    }

    public CircuitBreakerStatus GetStatus()
    {
        _lockObject.Enter();
        try
        {
            return new CircuitBreakerStatus
            {
                State = _state,
                FailureCount = _failureCount,
                LastFailureTime = _lastFailureTime,
                HalfOpenAttempts = _halfOpenAttempts
            };
        }
        finally
        {
            _lockObject.Exit();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            _resetTimer?.Dispose();
        }
        _logger.LogDebug("Circuit breaker service disposed");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
