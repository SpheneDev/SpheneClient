using System.Text.Json.Serialization;
using System;
using System.Collections.Generic;

namespace Sphene.Services;

public sealed class ActiveMismatchRecord
{
    public string Uid { get; init; } = string.Empty;
    public string GamePath { get; init; } = string.Empty;
    public int MismatchCount { get; set; }
    public int TotalCheckCount { get; set; }
    public long GlobalTotalCheckCount { get; set; }
    public double MismatchPercentage => TotalCheckCount > 0 ? (double)MismatchCount / TotalCheckCount * 100.0 : 0.0;
    public double GlobalMismatchPercentage => GlobalTotalCheckCount > 0 ? (double)MismatchCount / GlobalTotalCheckCount * 100.0 : 0.0;
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public HashSet<string> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ObjectKinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
