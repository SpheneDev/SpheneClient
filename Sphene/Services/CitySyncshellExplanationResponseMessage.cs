using Sphene.Services.Mediator;

namespace Sphene.Services;

public record CitySyncshellExplanationResponseMessage(string CityName, bool ShouldJoin) : MessageBase;
