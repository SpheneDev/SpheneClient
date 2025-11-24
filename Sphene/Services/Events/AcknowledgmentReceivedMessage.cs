using System;
using Sphene.API.Data;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record AcknowledgmentReceivedMessage(
    string AcknowledgmentId,
    UserData User,
    DateTime ReceivedAt
) : MessageBase;
