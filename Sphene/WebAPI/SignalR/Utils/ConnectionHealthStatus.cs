using System;

namespace Sphene.WebAPI.SignalR.Utils;

public class ConnectionHealthStatus
{
    public bool IsHealthy { get; set; }
    public int ConsecutiveFailures { get; set; }
    public TimeSpan TimeSinceLastSuccess { get; set; }
    public DateTime LastHealthCheck { get; set; }
}
