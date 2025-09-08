using System;

namespace Sphene.PlayerData.Pairs;

/// <summary>
/// Configuration class for the acknowledgment system
/// </summary>
public class AcknowledgmentConfiguration
{
    /// <summary>
    /// Default timeout for acknowledgments in seconds
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Maximum number of retry attempts for failed acknowledgments
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;
    
    /// <summary>
    /// Base delay for exponential backoff in milliseconds
    /// </summary>
    public int BaseRetryDelayMs { get; set; } = 1000;
    
    /// <summary>
    /// Maximum delay for exponential backoff in milliseconds
    /// </summary>
    public int MaxRetryDelayMs { get; set; } = 30000;
    
    /// <summary>
    /// Maximum number of acknowledgments to batch together
    /// </summary>
    public int MaxBatchSize { get; set; } = 10;
    
    /// <summary>
    /// Maximum time to wait before sending a partial batch in milliseconds
    /// </summary>
    public int BatchTimeoutMs { get; set; } = 5000;
    
    /// <summary>
    /// Interval for silent acknowledgments in minutes
    /// </summary>
    public int SilentAcknowledgmentIntervalMinutes { get; set; } = 1;
    
    /// <summary>
    /// Maximum number of pending acknowledgments per user
    /// </summary>
    public int MaxPendingAcknowledgmentsPerUser { get; set; } = 100;
    
    /// <summary>
    /// Enable or disable acknowledgment batching
    /// </summary>
    public bool EnableBatching { get; set; } = true;
    
    /// <summary>
    /// Enable or disable automatic retry for failed acknowledgments
    /// </summary>
    public bool EnableAutoRetry { get; set; } = true;
    
    /// <summary>
    /// Enable or disable silent acknowledgments
    /// </summary>
    public bool EnableSilentAcknowledgments { get; set; } = true;
    
    /// <summary>
    /// Enable or disable priority-based acknowledgment processing
    /// </summary>
    public bool EnablePrioritySystem { get; set; } = true;
    
    /// <summary>
    /// Timeout for high priority acknowledgments in seconds
    /// </summary>
    public int HighPriorityTimeoutSeconds { get; set; } = 10;
    
    /// <summary>
    /// Timeout for medium priority acknowledgments in seconds
    /// </summary>
    public int MediumPriorityTimeoutSeconds { get; set; } = 20;
    
    /// <summary>
    /// Timeout for low priority acknowledgments in seconds
    /// </summary>
    public int LowPriorityTimeoutSeconds { get; set; } = 60;
    
    /// <summary>
    /// Maximum size of the acknowledgment cache
    /// </summary>
    public int MaxCacheSize { get; set; } = 1000;
    
    /// <summary>
    /// Cache expiration time in minutes
    /// </summary>
    public int CacheExpirationMinutes { get; set; } = 30;
    
    /// <summary>
    /// Enable or disable performance metrics collection
    /// </summary>
    public bool EnableMetrics { get; set; } = true;
    
    /// <summary>
    /// Validates the configuration and throws an exception if invalid
    /// </summary>
    public void Validate()
    {
        if (DefaultTimeoutSeconds <= 0)
            throw new ArgumentException("DefaultTimeoutSeconds must be greater than 0");
            
        if (MaxRetryAttempts < 0)
            throw new ArgumentException("MaxRetryAttempts must be greater than or equal to 0");
            
        if (BaseRetryDelayMs <= 0)
            throw new ArgumentException("BaseRetryDelayMs must be greater than 0");
            
        if (MaxRetryDelayMs < BaseRetryDelayMs)
            throw new ArgumentException("MaxRetryDelayMs must be greater than or equal to BaseRetryDelayMs");
            
        if (MaxBatchSize <= 0)
            throw new ArgumentException("MaxBatchSize must be greater than 0");
            
        if (BatchTimeoutMs <= 0)
            throw new ArgumentException("BatchTimeoutMs must be greater than 0");
            
        if (SilentAcknowledgmentIntervalMinutes <= 0)
            throw new ArgumentException("SilentAcknowledgmentIntervalMinutes must be greater than 0");
            
        if (MaxPendingAcknowledgmentsPerUser <= 0)
            throw new ArgumentException("MaxPendingAcknowledgmentsPerUser must be greater than 0");
            
        if (HighPriorityTimeoutSeconds <= 0)
            throw new ArgumentException("HighPriorityTimeoutSeconds must be greater than 0");
            
        if (MediumPriorityTimeoutSeconds <= 0)
            throw new ArgumentException("MediumPriorityTimeoutSeconds must be greater than 0");
            
        if (LowPriorityTimeoutSeconds <= 0)
            throw new ArgumentException("LowPriorityTimeoutSeconds must be greater than 0");
            
        if (MaxCacheSize <= 0)
            throw new ArgumentException("MaxCacheSize must be greater than 0");
            
        if (CacheExpirationMinutes <= 0)
            throw new ArgumentException("CacheExpirationMinutes must be greater than 0");
    }
    
    /// <summary>
    /// Gets the timeout for a specific priority level
    /// </summary>
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

/// <summary>
/// Priority levels for acknowledgments
/// </summary>
public enum AcknowledgmentPriority
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>
/// Error codes for acknowledgment failures
/// </summary>
public enum AcknowledgmentErrorCode
{
    None = 0,
    Timeout = 1,
    NetworkError = 2,
    InvalidData = 3,
    UserNotFound = 4,
    ServerError = 5,
    RateLimited = 6,
    AuthenticationFailed = 7,
    DataCorrupted = 8,
    InsufficientPermissions = 9,
    ServiceUnavailable = 10
}