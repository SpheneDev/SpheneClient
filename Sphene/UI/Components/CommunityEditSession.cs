using Sphene.API.Dto.Group;

namespace Sphene.UI.Components;

/// <summary>
/// Staged community-aggregate edits for the syncshell pages. Every
/// <c>CommunityManageOps.Patch</c> call and inline identity commit lands here
/// as a composed patch instead of hitting <c>GroupSetCommunity</c> right away,
/// so the page can preview the pending state and the host commits everything
/// with a single save on Apply. The patch chain always re-applies on top of
/// the freshest community DTO, so news/events/gallery refreshes are never
/// frozen into the draft.
/// </summary>
internal sealed class CommunityEditSession
{
    private Func<SyncshellCommunityDto, SyncshellCommunityDto>? _patch;
    private string? _alias;

    /// <summary>True while any aggregate field or the alias is staged.</summary>
    public bool IsDirty => _patch != null || _alias != null;

    /// <summary>Staged shell alias · renames ride <c>GroupSetAlias</c>, not the aggregate.</summary>
    public string? Alias => _alias;

    /// <summary>Community with all staged patches applied · feed this to the view for preview.</summary>
    public SyncshellCommunityDto Effective(SyncshellCommunityDto community)
        => _patch?.Invoke(community) ?? community;

    /// <summary>Composes a community-aggregate patch onto the staged state.
    /// The previous chain is captured in a local - reading the field inside
    /// the lambda would make the chain call itself and overflow the stack.</summary>
    public void Stage(Func<SyncshellCommunityDto, SyncshellCommunityDto> patch)
    {
        var prev = _patch;
        _patch = prev == null ? patch : c => patch(prev(c));
    }

    /// <summary>Stages only the banner/avatar image fields produced by the
    /// photo-studio picker · every other staged field stays untouched.</summary>
    public void StageImage(SyncshellCommunityDto imaged, bool banner)
    {
        var src = imaged.Profile;
        if (banner)
        {
            Stage(c => c with
            {
                Profile = c.Profile with
                {
                    BannerImageBase64 = src.BannerImageBase64,
                    BannerMediaHash = src.BannerMediaHash,
                    BannerFileName = src.BannerFileName,
                    BannerContentType = src.BannerContentType,
                    BannerSize = src.BannerSize,
                }
            });
        }
        else
        {
            Stage(c => c with
            {
                Profile = c.Profile with
                {
                    AvatarImageBase64 = src.AvatarImageBase64,
                    AvatarMediaHash = src.AvatarMediaHash,
                    AvatarFileName = src.AvatarFileName,
                    AvatarContentType = src.AvatarContentType,
                    AvatarSize = src.AvatarSize,
                }
            });
        }
    }

    /// <summary>Stages a hero alias rename; committed via <c>GroupSetAlias</c>.</summary>
    public void StageAlias(string alias) => _alias = alias;

    /// <summary>Drops every staged change without saving.</summary>
    public void Reset()
    {
        _patch = null;
        _alias = null;
    }
}
