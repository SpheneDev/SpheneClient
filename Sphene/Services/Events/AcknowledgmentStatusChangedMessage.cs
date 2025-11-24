using System;
using Sphene.API.Data;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record AcknowledgmentStatusChangedMessage(
    string AcknowledgmentId,
    UserData User,
    AcknowledgmentStatus Status,
    DateTime Timestamp
) : MessageBase;