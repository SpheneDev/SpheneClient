using System;
using Sphene.API.Data;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record AcknowledgmentPendingMessage(
    string AcknowledgmentId,
    UserData User,
    DateTime PendingAt
) : MessageBase;

