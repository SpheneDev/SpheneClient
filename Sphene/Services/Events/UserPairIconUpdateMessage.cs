using Sphene.API.Data;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public record UserPairIconUpdateMessage(UserData User, IconUpdateType UpdateType, object? UpdateData) : MessageBase;
