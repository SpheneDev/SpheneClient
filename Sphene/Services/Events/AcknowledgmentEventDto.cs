using System;
using Sphene.API.Data;

namespace Sphene.Services.Events;

public sealed record AcknowledgmentEventDto(
    string AcknowledgmentId,
    UserData User,
    AcknowledgmentStatus Status,
    DateTime Timestamp
);
