using Sphene.API.Dto.Group;

namespace Sphene.Services.Mediator;

public record OpenSyncshellCommunityMessage(SyncshellCommunityDto Community, GroupFullInfoDto? GroupFullInfo,
    bool JoinPopup, bool PreviewAsVisitor = false) : MessageBase;
