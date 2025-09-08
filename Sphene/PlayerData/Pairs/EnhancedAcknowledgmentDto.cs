using System;
using System.Collections.Generic;
using Sphene.API.Data;

namespace Sphene.PlayerData.Pairs;

/// <summary>
/// Enhanced acknowledgment DTO with priority and error handling
/// </summary>
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
    
    /// <summary>
    /// Creates a new enhanced acknowledgment DTO
    /// </summary>
    public EnhancedAcknowledgmentDto() { }
    
    /// <summary>
    /// Creates a new enhanced acknowledgment DTO from existing data
    /// </summary>
    public EnhancedAcknowledgmentDto(UserData user, string acknowledgmentId, AcknowledgmentPriority priority = AcknowledgmentPriority.Medium)
    {
        User = user;
        AcknowledgmentId = acknowledgmentId;
        Priority = priority;
        AcknowledgedAt = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Marks the acknowledgment as failed with error details
    /// </summary>
    public void MarkAsFailed(AcknowledgmentErrorCode errorCode, string? errorMessage = null)
    {
        Success = false;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        AcknowledgedAt = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Marks the acknowledgment as successful
    /// </summary>
    public void MarkAsSuccessful()
    {
        Success = true;
        ErrorCode = AcknowledgmentErrorCode.None;
        ErrorMessage = null;
        AcknowledgedAt = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Increments retry count and sets next retry time
    /// </summary>
    public void IncrementRetry(TimeSpan nextRetryDelay)
    {
        RetryCount++;
        NextRetryAt = DateTime.UtcNow.Add(nextRetryDelay);
    }
    
    /// <summary>
    /// Checks if this acknowledgment is ready for retry
    /// </summary>
    public bool IsReadyForRetry()
    {
        return NextRetryAt.HasValue && DateTime.UtcNow >= NextRetryAt.Value;
    }
}

/// <summary>
/// Batch acknowledgment DTO for processing multiple acknowledgments together
/// </summary>
public class BatchAcknowledgmentDto
{
    public string BatchId { get; set; } = Guid.NewGuid().ToString();
    public List<EnhancedAcknowledgmentDto> Acknowledgments { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public bool IsProcessed { get; set; } = false;
    public AcknowledgmentPriority Priority { get; set; } = AcknowledgmentPriority.Medium;
    
    /// <summary>
    /// Adds an acknowledgment to the batch
    /// </summary>
    public void AddAcknowledgment(EnhancedAcknowledgmentDto acknowledgment)
    {
        Acknowledgments.Add(acknowledgment);
        // Set batch priority to highest priority of contained acknowledgments
        if (acknowledgment.Priority > Priority)
        {
            Priority = acknowledgment.Priority;
        }
    }
    
    /// <summary>
    /// Marks the batch as processed
    /// </summary>
    public void MarkAsProcessed()
    {
        IsProcessed = true;
        ProcessedAt = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Gets the number of acknowledgments in the batch
    /// </summary>
    public int Count => Acknowledgments.Count;
    
    /// <summary>
    /// Checks if the batch is ready to be sent based on size or timeout
    /// </summary>
    public bool IsReadyToSend(int maxBatchSize, TimeSpan batchTimeout)
    {
        return Count >= maxBatchSize || DateTime.UtcNow - CreatedAt >= batchTimeout;
    }
}

/// <summary>
/// Acknowledgment metrics for monitoring and diagnostics
/// </summary>
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
    
    /// <summary>
    /// Calculates the success rate as a percentage
    /// </summary>
    public double SuccessRate => TotalReceived > 0 ? (double)TotalSuccessful / TotalReceived * 100 : 0;
    
    /// <summary>
    /// Calculates the failure rate as a percentage
    /// </summary>
    public double FailureRate => TotalReceived > 0 ? (double)TotalFailed / TotalReceived * 100 : 0;
    
    /// <summary>
    /// Records a successful acknowledgment
    /// </summary>
    public void RecordSuccess(AcknowledgmentPriority priority, double responseTimeMs)
    {
        TotalSuccessful++;
        TotalReceived++;
        UpdateAverageResponseTime(responseTimeMs);
        IncrementPriorityCount(priority);
        LastUpdated = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Records a failed acknowledgment
    /// </summary>
    public void RecordFailure(AcknowledgmentPriority priority, AcknowledgmentErrorCode errorCode)
    {
        TotalFailed++;
        TotalReceived++;
        IncrementErrorCount(errorCode);
        IncrementPriorityCount(priority);
        LastUpdated = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Records a sent acknowledgment
    /// </summary>
    public void RecordSent()
    {
        TotalSent++;
        LastUpdated = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Records a retry attempt
    /// </summary>
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