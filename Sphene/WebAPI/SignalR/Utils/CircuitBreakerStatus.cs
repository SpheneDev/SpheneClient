using System;

namespace Sphene.WebAPI.SignalR.Utils;

public class CircuitBreakerStatus
{
    public CircuitBreakerState State { get; set; }
    public int FailureCount { get; set; }
    public DateTime LastFailureTime { get; set; }
    public int HalfOpenAttempts { get; set; }
}
