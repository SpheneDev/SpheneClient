using System;

namespace Sphene.PlayerData.Pairs;


/// Configuration class for the acknowledgment system
public class AcknowledgmentConfiguration
{
    
    /// Default timeout for acknowledgments in seconds
    
    public int DefaultTimeoutSeconds { get; set; } = 30;
    
    
    /// Maximum number of retry attempts for failed acknowledgments
    
    public int MaxRetryAttempts { get; set; } = 3;
    
    
    /// Base delay for exponential backoff in milliseconds
    
    public int BaseRetryDelayMs { get; set; } = 1000;
    
    
    /// Maximum delay for exponential backoff in milliseconds
    
    public int MaxRetryDelayMs { get; set; } = 30000;
    
    
    /// Maximum number of acknowledgments to batch together
    
    public int MaxBatchSize { get; set; } = 10;
    
    
    /// Maximum time to wait before sending a partial batch in milliseconds
    
    public int BatchTimeoutMs { get; set; } = 5000;
    
    
    /// Interval for silent acknowledgments in minutes
    
    public int SilentAcknowledgmentIntervalMinutes { get; set; } = 1;
    
    
    /// Maximum number of pending acknowledgments per user
    
    public int MaxPendingAcknowledgmentsPerUser { get; set; } = 100;
    
    
    /// Enable or disable acknowledgment batching
    
    public bool EnableBatching { get; set; } = true;
    
    
    /// Enable or disable automatic retry for failed acknowledgments
    
    public bool EnableAutoRetry { get; set; } = true;
    
    
    /// Enable or disable silent acknowledgments
    
    public bool EnableSilentAcknowledgments { get; set; } = true;
    
    
    /// Enable or disable priority-based acknowledgment processing
    
    public bool EnablePrioritySystem { get; set; } = true;
    
    
    /// Timeout for high priority acknowledgments in seconds
    
    public int HighPriorityTimeoutSeconds { get; set; } = 10;
    
    
    /// Timeout for medium priority acknowledgments in seconds
    
    public int MediumPriorityTimeoutSeconds { get; set; } = 20;
    
    
    /// Timeout for low priority acknowledgments in seconds
    
    public int LowPriorityTimeoutSeconds { get; set; } = 60;
    
    
    /// Maximum size of the acknowledgment cache
    
    public int MaxCacheSize { get; set; } = 1000;
    
    
    /// Cache expiration time in minutes
    
    public int CacheExpirationMinutes { get; set; } = 30;
    
    
    /// Enable or disable performance metrics collection
    
    public bool EnableMetrics { get; set; } = true;
    
    
    /// Validates the configuration and throws an exception if invalid
    
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


/// Priority levels for acknowledgments
public enum AcknowledgmentPriority
{
    Low = 0,
    Medium = 1,
    High = 2
}


/// Error codes for acknowledgment failures
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