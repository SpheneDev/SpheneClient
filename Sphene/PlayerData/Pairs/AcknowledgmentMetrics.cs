using System;
using System.Collections.Generic;

namespace Sphene.PlayerData.Pairs;

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

    public double SuccessRate => TotalReceived > 0 ? (double)TotalSuccessful / TotalReceived * 100 : 0;
    public double FailureRate => TotalReceived > 0 ? (double)TotalFailed / TotalReceived * 100 : 0;

    public void RecordSuccess(AcknowledgmentPriority priority, double responseTimeMs)
    {
        TotalSuccessful++;
        TotalReceived++;
        UpdateAverageResponseTime(responseTimeMs);
        IncrementPriorityCount(priority);
        LastUpdated = DateTime.UtcNow;
    }

    public void RecordFailure(AcknowledgmentPriority priority, AcknowledgmentErrorCode errorCode)
    {
        TotalFailed++;
        TotalReceived++;
        IncrementErrorCount(errorCode);
        IncrementPriorityCount(priority);
        LastUpdated = DateTime.UtcNow;
    }

    public void RecordSent()
    {
        TotalSent++;
        LastUpdated = DateTime.UtcNow;
    }

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
