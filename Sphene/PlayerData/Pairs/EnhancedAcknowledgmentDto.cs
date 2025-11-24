using System;
using System.Collections.Generic;
using Sphene.API.Data;

namespace Sphene.PlayerData.Pairs;


/// Enhanced acknowledgment DTO with priority and error handling
public class EnhancedAcknowledgmentDto
{
    public string DataHash { get; set; } = string.Empty;
    public UserData User { get; set; } = null!;
    public bool Success { get; set; }
    public DateTime AcknowledgedAt { get; set; }
    public AcknowledgmentPriority Priority { get; set; } = AcknowledgmentPriority.Medium;
    public AcknowledgmentErrorCode ErrorCode { get; set; } = AcknowledgmentErrorCode.None;
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; } = 0;
    public DateTime? NextRetryAt { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    
    // Computed property for backward compatibility
    public string AcknowledgmentId => DataHash;
    
    
    /// Creates a new enhanced acknowledgment DTO
    
    public EnhancedAcknowledgmentDto() { }
    
    
    /// Creates a new enhanced acknowledgment DTO from existing data
    
    public EnhancedAcknowledgmentDto(UserData user, string dataHash, AcknowledgmentPriority priority = AcknowledgmentPriority.Medium)
    {
        User = user;
        DataHash = dataHash;
        Priority = priority;
        AcknowledgedAt = DateTime.UtcNow;
    }
    
    
    /// Marks the acknowledgment as failed with error details
    
    public void MarkAsFailed(AcknowledgmentErrorCode errorCode, string? errorMessage = null)
    {
        Success = false;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        AcknowledgedAt = DateTime.UtcNow;
    }
    
    
    /// Marks the acknowledgment as successful
    
    public void MarkAsSuccessful()
    {
        Success = true;
        ErrorCode = AcknowledgmentErrorCode.None;
        ErrorMessage = null;
        AcknowledgedAt = DateTime.UtcNow;
    }
    
    
    /// Increments retry count and sets next retry time
    
    public void IncrementRetry(TimeSpan nextRetryDelay)
    {
        RetryCount++;
        NextRetryAt = DateTime.UtcNow.Add(nextRetryDelay);
    }
    
    
    /// Checks if this acknowledgment is ready for retry
    
    public bool IsReadyForRetry()
    {
        return NextRetryAt.HasValue && DateTime.UtcNow >= NextRetryAt.Value;
    }
}


 
