using System;
using System.Collections.Generic;

namespace Sphene.PlayerData.Pairs;

public class BatchAcknowledgmentDto
{
    public string BatchId { get; set; } = Guid.NewGuid().ToString();
    public List<EnhancedAcknowledgmentDto> Acknowledgments { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public bool IsProcessed { get; set; } = false;
    public AcknowledgmentPriority Priority { get; set; } = AcknowledgmentPriority.Medium;

    public void AddAcknowledgment(EnhancedAcknowledgmentDto acknowledgment)
    {
        Acknowledgments.Add(acknowledgment);
        if (acknowledgment.Priority > Priority)
        {
            Priority = acknowledgment.Priority;
        }
    }

    public void MarkAsProcessed()
    {
        IsProcessed = true;
        ProcessedAt = DateTime.UtcNow;
    }

    public int Count => Acknowledgments.Count;

    public bool IsReadyToSend(int maxBatchSize, TimeSpan batchTimeout)
    {
        return Count >= maxBatchSize || DateTime.UtcNow - CreatedAt >= batchTimeout;
    }
}
