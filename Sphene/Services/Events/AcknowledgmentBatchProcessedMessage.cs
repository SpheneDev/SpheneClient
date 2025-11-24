using Sphene.API.Data;
using Sphene.Services.Mediator;
using System;
using System.Collections.Generic;

namespace Sphene.Services.Events;

public record AcknowledgmentBatchProcessedMessage(
    string BatchKey,
    List<UserData> Users,
    string AcknowledgmentId,
    DateTime Timestamp
) : MessageBase;
