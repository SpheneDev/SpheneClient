using Sphene.API.Data;
using System;
using System.Collections.Generic;

namespace Sphene.Services;

public class BatchedAcknowledgment
{
    public string BatchKey { get; set; } = string.Empty;
    public List<UserData> Users { get; set; } = new();
    public string AcknowledgmentId { get; set; } = string.Empty;
    public Action<List<UserData>, string> ProcessBatch { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime LastUpdated { get; set; }
}
