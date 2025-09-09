using Sphene.API.Data;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

// Event message for acknowledgment status changes
public record AcknowledgmentStatusChangedMessage(
    string AcknowledgmentId,
    UserData User,
    AcknowledgmentStatus Status,
    DateTime Timestamp
) : MessageBase;

// Event message for acknowledgment received
public record AcknowledgmentReceivedMessage(
    string AcknowledgmentId,
    UserData User,
    DateTime ReceivedAt
) : MessageBase;

// Event message for acknowledgment timeout
public record AcknowledgmentTimeoutMessage(
    string AcknowledgmentId,
    UserData User,
    DateTime TimeoutAt
) : MessageBase;

// Event message for acknowledgment batch completed
public record AcknowledgmentBatchCompletedMessage(
    string BatchId,
    List<UserData> Recipients,
    DateTime CompletedAt
) : MessageBase;

// Event message for acknowledgment pending
public record AcknowledgmentPendingMessage(
    string AcknowledgmentId,
    UserData User,
    DateTime PendingAt
) : MessageBase;

// Acknowledgment status enumeration
public enum AcknowledgmentStatus
{
    Pending,
    Received,
    Timeout,
    Cancelled,
    Failed
}

// Event published when acknowledgment metrics are updated
public record AcknowledgmentMetricsUpdatedMessage(
    int TotalPending,
    int TotalCompleted,
    int TotalTimedOut,
    DateTime Timestamp
) : MessageBase;

// Event published when UI should refresh specific acknowledgment data (more granular than RefreshUiMessage)
public record AcknowledgmentUiRefreshMessage(
    string? AcknowledgmentId = null,
    UserData? User = null,
    bool RefreshAll = false
) : MessageBase;

// Event published when pair acknowledgment status changes (more specific than RefreshUiMessage)
public record PairAcknowledgmentStatusChangedMessage(
    UserData User,
    string? AcknowledgmentId,
    bool HasPendingAcknowledgment,
    bool? LastAcknowledgmentSuccess,
    DateTimeOffset? LastAcknowledgmentTime
) : MessageBase;