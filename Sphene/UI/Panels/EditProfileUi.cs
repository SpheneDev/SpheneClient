using Dalamud.Interface;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;
using Sphene.Services;
using Sphene.WebAPI;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

/// <summary>
/// Profile options embedded in the hub's "My Profile" edit mode — identity
/// (handle/display name), avatar, banner, status, description and tags are
/// edited directly on the hero card; this component only keeps the privacy
/// toggles and the profile rules callout.
/// </summary>
public class EditProfileUi
{
    private readonly ApiController _apiController;
    private readonly SpheneProfileManager _spheneProfileManager;
    private int _profileVisibility;

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
        DrawPrivacyCard();
        DrawProfileRules();
    }

    private void DrawPrivacyCard()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            VisibilityToggle(ProfileVisibility.HideProfilePicture, "Show profile picture");
            VisibilityToggle(ProfileVisibility.HideBanner, "Show banner");
            VisibilityToggle(ProfileVisibility.HideDescription, "Show description");
            VisibilityToggle(ProfileVisibility.HideStatusMessage, "Show status message");
            VisibilityToggle(ProfileVisibility.HideTags, "Show tags");
            SpheneUI.Controls.HelpText("Hidden sections are stripped server-side before your profile is sent to paired users.");
        }, title: "Privacy", accentColor: SpheneUI.Colors.Warning,
            description: "Choose which profile sections paired users can see.");
    }

    private void VisibilityToggle(ProfileVisibility flag, string label)
    {
        var show = !((ProfileVisibility)_profileVisibility).HasFlag(flag);
        if (SpheneUI.Controls.Toggle(label, ref show))
        {
            _profileVisibility = show
                ? _profileVisibility & ~(int)flag
                : _profileVisibility | (int)flag;
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID),
                Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
                Visibility: _profileVisibility));
        }
    }

    private static void DrawProfileRules()
    {
        SpheneUI.Layout.Callout(
            "All paired, unpaused users can see your profile picture and description. Profiles can be reported for breaking the rules — avoid anything highly illegal or obscene and slurs of any kind. Valid reports can lead to your profile being disabled or your account terminated.",
            FontAwesomeIcon.InfoCircle, SpheneUI.Palette.CrystalBlue, title: "Profile rules");
    }
}
