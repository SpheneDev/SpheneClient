using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;
using Sphene.Services;
using Sphene.WebAPI;
using Sphene.UI.Components;
using Sphene.UI.Design;
using Sphene.UI.Syncshell;

namespace Sphene.UI.Panels;

/// <summary>
/// Profile options embedded in the hub's "My Profile" edit mode · identity
/// (handle/display name), avatar, banner, status, description and tags are
/// edited directly on the hero card; this component keeps the link editor,
/// the privacy toggles and the profile rules callout.
/// </summary>
public class EditProfileUi
{
    private readonly ApiController _apiController;
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly List<SyncshellCommunityView.CommunityLinkDraft> _linksBuffer = [];
    private int _profileVisibility;
    private bool _publicProfile;
    private bool _linksSeeded;

    /// <summary>When set, profile patches route here instead of
    /// <c>UserSetProfile</c> - the host stages them for a later apply.</summary>
    public Action<UserProfileDto>? CommitOverride { get; set; }

    public EditProfileUi(ApiController apiController, SpheneProfileManager spheneProfileManager)
    {
        _apiController = apiController;
        _spheneProfileManager = spheneProfileManager;
    }

    /// <summary>Renders the profile options inside the hub; the host provides the scroll area.</summary>
    internal void DrawEmbedded()
    {
        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));

        if (profile.IsFlagged)
        {
            SpheneUI.Layout.Callout(profile.Description, FontAwesomeIcon.ExclamationTriangle,
                SpheneUI.Colors.Danger, title: "Profile flagged");
            return;
        }

        _profileVisibility = profile.Visibility;
        _publicProfile = profile.PublicProfile;
        if (!_linksSeeded)
        {
            SeedLinks(profile);
        }
        DrawProfileRules();
        DrawLinksCard();
        DrawPrivacyCard();
    }

    private void SeedLinks(SpheneProfileData profile)
    {
        _linksBuffer.Clear();
        if (profile.Links != null)
        {
            foreach (var l in profile.Links)
            {
                _linksBuffer.Add(new SyncshellCommunityView.CommunityLinkDraft
                {
                    Label = l.Label, Url = l.Url, Icon = l.Icon,
                });
            }
        }
        _linksSeeded = true;
    }

    private void DrawLinksCard()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            var scale = SpheneUI.Scale;
            for (var i = 0; i < _linksBuffer.Count; i++)
            {
                var link = _linksBuffer[i];
                using (ImRaii.PushId($"profilelink{i}"))
                {
                    var iconW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Trash).X;
                    var preset = LinkBrandPresets.Find(link.Icon) ?? LinkBrandPresets.Custom;
                    SpheneUI.Controls.Combo("##preset", LinkBrandPresets.All, p => p?.Label ?? "",
                        selected =>
                        {
                            if (selected == null)
                            {
                                return;
                            }
                            link.Icon = string.Equals(selected.Key, LinkBrandPresets.Custom.Key,
                                StringComparison.Ordinal) ? null : selected.Key;
                            if (string.IsNullOrWhiteSpace(link.Label))
                            {
                                link.Label = selected.Label;
                            }
                        }, preset, preferredWidth: 140f * scale);
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    var labelW = MathF.Max(140f * scale,
                        SpheneUI.Controls.BoundedAvailX() - iconW - SpheneUI.Spacing.X4.Scaled);
                    _ = SpheneUI.Controls.InputTextWithHint("##label", "Label…", ref link.Label, 60,
                        trailingWidth: iconW, preferredWidth: labelW);
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                    {
                        _linksBuffer.RemoveAt(i);
                        CommitLinks();
                        break;
                    }
                    SpheneUI.AttachTooltip("Remove link");
                    if (SpheneUI.Controls.InputTextWithHint("##url", preset.UrlHint, ref link.Url, 300))
                    {
                        CommitLinks();
                    }
                }
                if (i < _linksBuffer.Count - 1)
                {
                    SpheneUI.Layout.Separator();
                }
            }
            using (ImRaii.Group())
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Add link"))
                {
                    _linksBuffer.Add(new SyncshellCommunityView.CommunityLinkDraft());
                }
                SpheneUI.AttachTooltip("Discord, website, venue listing · rendered as clickable chips");
            }
        }, "Links", icon: FontAwesomeIcon.Link, accentColor: SpheneUI.Colors.Accent,
            description: "Discord, website, venue listing · shown as clickable chips on your profile.");
    }

    private void CommitLinks()
    {
        var committed = _linksBuffer
            .Where(l => !string.IsNullOrWhiteSpace(l.Label) || !string.IsNullOrWhiteSpace(l.Url))
            .Select(l => new UserProfileLinkDto(l.Label.Trim(), l.Url.Trim(),
                string.IsNullOrWhiteSpace(l.Icon) ? null : l.Icon))
            .ToList();
        var dto = new UserProfileDto(new UserData(_apiController.UID),
            Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
            Links: committed);
        if (CommitOverride != null)
        {
            CommitOverride(dto);
        }
        else
        {
            _ = _apiController.UserSetProfile(dto);
        }
    }

    private void DrawPrivacyCard()
    {
        SpheneUI.Layout.HeadedRowGroup("Privacy", () =>
        {
            // Public sharing is a server capability - on an old server the
            // toggle stays hidden rather than writing a flag nothing reads.
            if (_apiController.ServerInfo.PublicProfilesEnabled)
            {
                PublicProfileToggle();
                SpheneUI.GroupState.RowEnd();
                SpheneUI.Rows.Note(
                    "Signed-in Sphene users can find this profile in the Community Hub and follow it. The section switches below still apply to what they see.");
            }
            VisibilityToggle(ProfileVisibility.HideProfilePicture, "Show profile picture");
            VisibilityToggle(ProfileVisibility.HideBanner, "Show banner");
            VisibilityToggle(ProfileVisibility.HideDescription, "Show description");
            VisibilityToggle(ProfileVisibility.HideStatusMessage, "Show status message");
            VisibilityToggle(ProfileVisibility.HideTags, "Show tags");
            VisibilityToggle(ProfileVisibility.HideGallery, "Show gallery");
            VisibilityToggle(ProfileVisibility.HideBlog, "Show Chronicles");
            VisibilityToggle(ProfileVisibility.HideLinks, "Show links");
            SpheneUI.GroupState.RowEnd();
            SpheneUI.Rows.Note(
                "Hidden sections are stripped server-side before your profile is sent to paired users.");
        }, FontAwesomeIcon.UserShield,
            "Choose which profile sections paired users can see.",
            SpheneUI.Colors.Warning,
            id: "profile-privacy");
    }

    /// <summary>Opt-in Community Hub visibility - a dedicated flag, separate
    /// from the field mask below. Stages through the same draft pipeline.</summary>
    private void PublicProfileToggle()
    {
        var show = _publicProfile;
        if (SpheneUI.Controls.Toggle("Show profile in Community Hub", ref show))
        {
            _publicProfile = show;
            var dto = new UserProfileDto(new UserData(_apiController.UID),
                Disabled: false, IsNSFW: null, ProfilePictureBase64: null,
                Description: null, PublicProfile: show);
            if (CommitOverride != null)
            {
                CommitOverride(dto);
            }
            else
            {
                _ = _apiController.UserSetProfile(dto);
            }
        }
    }

    private void VisibilityToggle(ProfileVisibility flag, string label)
    {
        var show = !((ProfileVisibility)_profileVisibility).HasFlag(flag);
        if (SpheneUI.Controls.Toggle(label, ref show))
        {
            _profileVisibility = show
                ? _profileVisibility & ~(int)flag
                : _profileVisibility | (int)flag;
            var dto = new UserProfileDto(new UserData(_apiController.UID),
                Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
                Visibility: _profileVisibility);
            if (CommitOverride != null)
            {
                CommitOverride(dto);
            }
            else
            {
                _ = _apiController.UserSetProfile(dto);
            }
        }
    }

    private static void DrawProfileRules()
    {
        SpheneUI.Layout.Callout(
            "All paired, unpaused users can see your profile picture and description. Profiles can be reported for breaking the rules · avoid anything highly illegal or obscene and slurs of any kind. Valid reports can lead to your profile being disabled or your account terminated.",
            FontAwesomeIcon.InfoCircle, SpheneUI.Palette.CrystalBlue, title: "Profile rules");
    }
}
