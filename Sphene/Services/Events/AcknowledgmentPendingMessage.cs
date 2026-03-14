using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record AcknowledgmentPendingMessage(
    AcknowledgmentEventDto Event
) : MessageBase;

