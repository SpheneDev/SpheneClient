namespace Sphene.Services.Events;

public enum AcknowledgmentStatus
{
    Pending,
    Received,
    Timeout,
    Cancelled,
    Failed
}
