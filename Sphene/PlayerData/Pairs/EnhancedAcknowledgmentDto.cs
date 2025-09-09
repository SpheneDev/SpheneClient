using System;
using System.Collections.Generic;
using Sphene.API.Data;

namespace Sphene.PlayerData.Pairs;


/// Enhanced acknowledgment DTO with priority and error handling
public class EnhancedAcknowledgmentDto
{
    public string AcknowledgmentId { get; set; } = string.Empty;
    public UserData User { get; set; } = null!;
    public bool Success { get; set; }
    public DateTime AcknowledgedAt { get; set; }
    public AcknowledgmentPriority Priority { get; set; } = AcknowledgmentPriority.Medium;
    public AcknowledgmentErrorCode ErrorCode { get; set; } = AcknowledgmentErrorCode.None;
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; } = 0;
    public DateTime? NextRetryAt { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    
    
    /// Creates a new enhanced acknowledgment DTO
    
    public EnhancedAcknowledgmentDto() { }
    
    
    /// Creates a new enhanced acknowledgment DTO from existing data
    
    public EnhancedAcknowledgmentDto(UserData user, string acknowledgmentId, AcknowledgmentPriority priority = AcknowledgmentPriority.Medium)
    {
        User = user;
        AcknowledgmentId = acknowledgmentId;
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


/// Batch acknowledgment DTO for processing multiple acknowledgments together
public class BatchAcknowledgmentDto
{
    public string BatchId { get; set; } = Guid.NewGuid().ToString();
    public List<EnhancedAcknowledgmentDto> Acknowledgments { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public bool IsProcessed { get; set; } = false;
    public AcknowledgmentPriority Priority { get; set; } = AcknowledgmentPriority.Medium;
    
    
    /// Adds an acknowledgment to the batch
    
    public void AddAcknowledgment(EnhancedAcknowledgmentDto acknowledgment)
    {
        Acknowledgments.Add(acknowledgment);
        // Set batch priority to highest priority of contained acknowledgments
        if (acknowledgment.Priority > Priority)
        {
            Priority = acknowledgment.Priority;
        }
    }
    
    
    /// Marks the batch as processed
    
    public void MarkAsProcessed()
    {
        IsProcessed = true;
        ProcessedAt = DateTime.UtcNow;
    }
    
    
    /// Gets the number of acknowledgments in the batch
    
    public int Count => Acknowledgments.Count;
    
    
    /// Checks if the batch is ready to be sent based on size or timeout
    
    public bool IsReadyToSend(int maxBatchSize, TimeSpan batchTimeout)
    {
        return Count >= maxBatchSize || DateTime.UtcNow - CreatedAt >= batchTimeout;
    }
}


/// Acknowledgment metrics for monitoring and diagnostics
public class AcknowledgmentMetrics
{
    public int TotalSent { get; set; }
    public int TotalReceived { get; set; }
    public int TotalSuccessful { get; set; }
    public int TotalFailed { get; set; }
    public int TotalRetries { get; set; }
    public double AverageResponseTimeMs { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public Dictionary<AcknowledgmentErrorCode, int> ErrorCounts { get; set; } = new();
    public Dictionary<AcknowledgmentPriority, int> PriorityCounts { get; set; } = new();
    
    
    /// Calculates the success rate as a percentage
    
    public double SuccessRate => TotalReceived > 0 ? (double)TotalSuccessful / TotalReceived * 100 : 0;
    
    
    /// Calculates the failure rate as a percentage
    
    public double FailureRate => TotalReceived > 0 ? (double)TotalFailed / TotalReceived * 100 : 0;
    
    
    /// Records a successful acknowledgment
    
    public void RecordSuccess(AcknowledgmentPriority priority, double responseTimeMs)
    {
        TotalSuccessful++;
        TotalReceived++;
        UpdateAverageResponseTime(responseTimeMs);
        IncrementPriorityCount(priority);
        LastUpdated = DateTime.UtcNow;
    }
    
    
    /// Records a failed acknowledgment
    
    public void RecordFailure(AcknowledgmentPriority priority, AcknowledgmentErrorCode errorCode)
    {
        TotalFailed++;
        TotalReceived++;
        IncrementErrorCount(errorCode);
        IncrementPriorityCount(priority);
        LastUpdated = DateTime.UtcNow;
    }
    
    
    /// Records a sent acknowledgment
    
    public void RecordSent()
    {
        TotalSent++;
        LastUpdated = DateTime.UtcNow;
    }
    
    
    /// Records a retry attempt
    
    public void RecordRetry()
    {
        TotalRetries++;
        LastUpdated = DateTime.UtcNow;
    }
    
    private void UpdateAverageResponseTime(double responseTimeMs)
    {
        if (TotalSuccessful == 1)
        {
            AverageResponseTimeMs = responseTimeMs;
        }
        else
        {
            AverageResponseTimeMs = (AverageResponseTimeMs * (TotalSuccessful - 1) + responseTimeMs) / TotalSuccessful;
        }
    }
    
    private void IncrementErrorCount(AcknowledgmentErrorCode errorCode)
    {
        ErrorCounts.TryGetValue(errorCode, out var count);
        ErrorCounts[errorCode] = count + 1;
    }
    
    private void IncrementPriorityCount(AcknowledgmentPriority priority)
    {
        PriorityCounts.TryGetValue(priority, out var count);
        PriorityCounts[priority] = count + 1;
    }
}