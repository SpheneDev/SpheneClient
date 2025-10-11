using Sphene.API.Dto.Group;

namespace Sphene.Services.Mediator;

public record OpenWelcomePageMessage(SyncshellWelcomePageDto WelcomePage, GroupFullInfoDto GroupFullInfo) : MessageBase;