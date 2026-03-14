using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record AcknowledgmentReceivedMessage(
    AcknowledgmentEventDto Event
) : MessageBase;
