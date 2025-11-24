using System;

namespace Sphene.Services;

public class BatchStatistics
{
    public int PendingBatches { get; set; }
    public int TotalPendingUsers { get; set; }
    public TimeSpan OldestBatchAge { get; set; }
    public TimeSpan BatchWindow { get; set; }
    public int MaxBatchSize { get; set; }
    public int FailedBatches { get; set; }
    public int TotalFailedUsers { get; set; }
}
