using System;
using Sphene.API.Data;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record PairAcknowledgmentStatusChangedMessage(
    UserData User,
    string? AcknowledgmentId,
    bool HasPendingAcknowledgment,
    bool? LastAcknowledgmentSuccess,
    DateTimeOffset? LastAcknowledgmentTime
) : MessageBase;
