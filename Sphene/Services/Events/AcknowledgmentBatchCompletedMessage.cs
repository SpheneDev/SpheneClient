using Sphene.API.Data;
using Sphene.Services.Mediator;
using System;
using System.Collections.Generic;

namespace Sphene.Services.Events;

public record AcknowledgmentBatchCompletedMessage(
    string BatchId,
    List<UserData> Recipients,
    DateTime CompletedAt
) : MessageBase;

