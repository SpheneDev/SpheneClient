using Sphene.API.Dto.Group;

namespace Sphene.Services.Mediator;

public record OpenSyncshellCommunityMessage(SyncshellCommunityDto Community, GroupFullInfoDto? GroupFullInfo,
    bool JoinPopup, bool PreviewAsVisitor = false,
    /// <summary>Optional request identity - re-validated when the preview
    /// actually opens, so a queued open from a superseded request (e.g. a
    /// delayed Discover fetch across a profile switch) is dropped instead
    /// of surfacing under the new state.</summary>
    Func<bool>? IsStillValid = null) : MessageBase;
