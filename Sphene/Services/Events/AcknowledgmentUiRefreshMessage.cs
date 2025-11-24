using Sphene.API.Data;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record AcknowledgmentUiRefreshMessage(
    string? AcknowledgmentId = null,
    UserData? User = null,
    bool RefreshAll = false
) : MessageBase;
