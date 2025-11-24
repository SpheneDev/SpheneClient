using System;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record AcknowledgmentMetricsUpdatedMessage(
    int TotalPending,
    int TotalCompleted,
    int TotalTimedOut,
    DateTime Timestamp
) : MessageBase;
