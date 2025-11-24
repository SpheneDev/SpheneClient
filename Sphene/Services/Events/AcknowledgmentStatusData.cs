namespace Sphene.Services.Events;

public record AcknowledgmentStatusData(bool HasPending, bool? LastSuccess, DateTimeOffset? LastTime);
