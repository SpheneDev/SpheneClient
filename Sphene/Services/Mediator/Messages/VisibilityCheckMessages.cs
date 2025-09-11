using Sphene.Services.Mediator;

namespace Sphene.Services.Mediator.Messages;

public record VisibilityCheckRequestMessage(string FromPlayerIdentifier, string RequestId) : MessageBase;

public record VisibilityCheckResponseMessage(string RequestId, bool CanSeeUs) : MessageBase;