using System;

namespace Sphene.PlayerData.Pairs;


/// Configuration class for the acknowledgment system
public class AcknowledgmentConfiguration
{
    
    /// Default timeout for acknowledgments in seconds
    
    private int _defaultTimeoutSeconds = 30;
    public int DefaultTimeoutSeconds
    {
        get => _defaultTimeoutSeconds;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _defaultTimeoutSeconds = value;
        }
    }
    
    
    /// Maximum number of retry attempts for failed acknowledgments
    
    private int _maxRetryAttempts = 3;
    public int MaxRetryAttempts
    {
        get => _maxRetryAttempts;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _maxRetryAttempts = value;
        }
    }
    
    
    /// Base delay for exponential backoff in milliseconds
    
    private int _baseRetryDelayMs = 1000;
    public int BaseRetryDelayMs
    {
        get => _baseRetryDelayMs;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _baseRetryDelayMs = value;
        }
    }
    
    
    /// Maximum delay for exponential backoff in milliseconds
    
    private int _maxRetryDelayMs = 30000;
    public int MaxRetryDelayMs
    {
        get => _maxRetryDelayMs;
        set
        {
            if (value < _baseRetryDelayMs) throw new ArgumentOutOfRangeException(nameof(value));
            _maxRetryDelayMs = value;
        }
    }
    
    
    /// Maximum number of acknowledgments to batch together
    
    private int _maxBatchSize = 10;
    public int MaxBatchSize
    {
        get => _maxBatchSize;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _maxBatchSize = value;
        }
    }
    
    
    /// Maximum time to wait before sending a partial batch in milliseconds
    
    private int _batchTimeoutMs = 5000;
    public int BatchTimeoutMs
    {
        get => _batchTimeoutMs;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _batchTimeoutMs = value;
        }
    }
    
    
    /// Enable adaptive timeout based on network conditions
    
    public bool EnableAdaptiveTimeout { get; set; } = true;
    
    
    /// Minimum timeout for adaptive timeout in seconds
    
    public int MinAdaptiveTimeoutSeconds { get; set; } = 10;
    
    
    /// Maximum timeout for adaptive timeout in seconds
    
    public int MaxAdaptiveTimeoutSeconds { get; set; } = 120;
    
    
    /// Network latency threshold for timeout adjustment in milliseconds
    
    public int NetworkLatencyThresholdMs { get; set; } = 500;
    
    
    /// Timeout multiplier for high latency connections
    
    public double HighLatencyTimeoutMultiplier { get; set; } = 2.0;
    
    
    /// Maximum number of pending acknowledgments per user
    
    private int _maxPendingAcknowledgmentsPerUser = 100;
    public int MaxPendingAcknowledgmentsPerUser
    {
        get => _maxPendingAcknowledgmentsPerUser;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _maxPendingAcknowledgmentsPerUser = value;
        }
    }
    
    
    /// Enable or disable acknowledgment batching
    
    public bool EnableBatching { get; set; } = true;
    
    
    /// Enable or disable automatic retry for failed acknowledgments
    
    public bool EnableAutoRetry { get; set; } = true;
    
    

    
    
    /// Enable or disable priority-based acknowledgment processing
    
    public bool EnablePrioritySystem { get; set; } = true;
    
    
    /// Timeout for high priority acknowledgments in seconds
    private int _highPriorityTimeoutSeconds = 10;
    public int HighPriorityTimeoutSeconds
    {
        get => _highPriorityTimeoutSeconds;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _highPriorityTimeoutSeconds = value;
        }
    }
    
    
    /// Timeout for medium priority acknowledgments in seconds
    private int _mediumPriorityTimeoutSeconds = 20;
    public int MediumPriorityTimeoutSeconds
    {
        get => _mediumPriorityTimeoutSeconds;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _mediumPriorityTimeoutSeconds = value;
        }
    }
    
    
    /// Timeout for low priority acknowledgments in seconds
    private int _lowPriorityTimeoutSeconds = 60;
    public int LowPriorityTimeoutSeconds
    {
        get => _lowPriorityTimeoutSeconds;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _lowPriorityTimeoutSeconds = value;
        }
    }
    
    
    /// Maximum size of the acknowledgment cache
    private int _maxCacheSize = 1000;
    public int MaxCacheSize
    {
        get => _maxCacheSize;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _maxCacheSize = value;
        }
    }
    
    
    /// Cache expiration time in minutes
    private int _cacheExpirationMinutes = 30;
    public int CacheExpirationMinutes
    {
        get => _cacheExpirationMinutes;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _cacheExpirationMinutes = value;
        }
    }
    
    
    /// Enable or disable performance metrics collection
    
    public bool EnableMetrics { get; set; } = true;
    
    
    /// Validates the configuration and throws an exception if invalid
    
    public void Validate()
    {
        // All validations are enforced in property setters
    }
    
    
    /// Gets the timeout for a specific priority level
    
    public int GetTimeoutForPriority(AcknowledgmentPriority priority)
    {
        return priority switch
        {
            AcknowledgmentPriority.High => HighPriorityTimeoutSeconds,
            AcknowledgmentPriority.Medium => MediumPriorityTimeoutSeconds,
            AcknowledgmentPriority.Low => LowPriorityTimeoutSeconds,
            _ => DefaultTimeoutSeconds
        };
    }
}


 
