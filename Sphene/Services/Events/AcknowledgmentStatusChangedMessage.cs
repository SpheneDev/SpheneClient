using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record AcknowledgmentStatusChangedMessage(
    AcknowledgmentEventDto Event
) : MessageBase;
