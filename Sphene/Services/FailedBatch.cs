using System;

namespace Sphene.Services;

public class FailedBatch
{
    public string BatchKey { get; set; } = string.Empty;
    public BatchedAcknowledgment Batch { get; set; } = null!;
    public int FailureCount { get; set; }
    public DateTime LastFailureTime { get; set; }
    public DateTime NextRetryTime { get; set; }
    public Exception? Exception { get; set; }
}
