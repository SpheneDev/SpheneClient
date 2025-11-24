using System;
using Sphene.API.Data;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record AcknowledgmentTimeoutMessage(
    string AcknowledgmentId,
    UserData User,
    DateTime TimeoutAt
) : MessageBase;