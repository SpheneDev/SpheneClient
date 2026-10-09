using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using Sphene.UI.Components;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Panels;

public class EditProfileUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly FileDialogManager _fileDialogManager;
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly ImageCropUi _imageCropUi;
    private bool _adjustedForScollBarsLocalProfile = false;
    private bool _adjustedForScollBarsOnlineProfile = false;
    private string _descriptionText = string.Empty;
    private string _profileDescription = string.Empty;
    private string _statusText = string.Empty;
    private string _profileStatus = string.Empty;
    private string _tagsText = string.Empty;
    private string _profileTags = string.Empty;
    private string _profileTheme = string.Empty;
    private int _profileVisibility;
    private Vector4 _accentEdit = new(0.30f, 0.80f, 0.95f, 1f);
    private Vector4 _bannerTopEdit = new(0.30f, 0.80f, 0.95f, 1f);
    private Vector4 _bannerBottomEdit = new(0.40f, 0.30f, 0.80f, 1f);
    private bool _useAccent, _useBannerTop, _useBannerBottom;
    private byte[] _bannerImage = [];
    private bool _showBannerError = false;
    private byte[] _profileImage = [];
    private bool _showFileDialogError = false;
    private bool _wasOpen;
    private bool _pendingNsfwChange = false;
    private bool _nsfwEdit = false;

    public EditProfileUi(ILogger<EditProfileUi> logger, SpheneMediator mediator,
        ApiController apiController, FileDialogManager fileDialogManager,
        SpheneProfileManager spheneProfileManager, PerformanceCollectorService performanceCollectorService,
        ImageCropUi imageCropUi)
        : base(logger, mediator, "Sphene Edit Profile###SpheneEditProfileUI", performanceCollectorService)
    {
        IsOpen = false;
        this.SizeConstraints = new()
        {
            MinimumSize = new(768, 512),
            MaximumSize = new(768, 2000)
        };
        _apiController = apiController;
        _fileDialogManager = fileDialogManager;
        _spheneProfileManager = spheneProfileManager;
        _imageCropUi = imageCropUi;

        Mediator.Subscribe<GposeStartMessage>(this, (_) => { _wasOpen = IsOpen; IsOpen = false; });
        Mediator.Subscribe<GposeEndMessage>(this, (_) => IsOpen = _wasOpen);
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData == null || string.Equals(msg.UserData.UID, _apiController.UID, StringComparison.Ordinal))
            {
                SpheneUI.Media.Evict($"editprofile:{_apiController.UID}");
            }
        });
    }

    protected override void DrawInternal()
    {
        if (SpheneUI.Layout.BeginScrollArea("editprofile-content", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            DrawProfileContent(embedded: false);
        }
        SpheneUI.Layout.EndScrollArea();
    }

    /// <summary>Renders the profile editor inside another window (Sphene Hub); the host provides the scroll area.</summary>
    internal void DrawEmbedded() => DrawProfileContent(embedded: true);

    private void DrawProfileContent(bool embedded)
    {
        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));

        if (_pendingNsfwChange)
        {
            if (profile.IsNSFW == _nsfwEdit)
            {
                _pendingNsfwChange = false;
            }
        }
        else if (_nsfwEdit != profile.IsNSFW)
        {
            _nsfwEdit = profile.IsNSFW;
        }

        if (profile.IsFlagged)
        {
            SpheneUI.Layout.Callout(profile.Description, FontAwesomeIcon.ExclamationTriangle,
                SpheneUI.Colors.Danger, title: "Profile flagged");
            return;
        }

        IDalamudTextureWrap? pfpTextureWrap = null;
        if (!embedded)
        {
            var pfpKey = $"editprofile:{_apiController.UID}";
            if (!_profileImage.SequenceEqual(profile.ImageData.Value))
            {
                _profileImage = profile.ImageData.Value;
                SpheneUI.Media.Evict(pfpKey);
            }
            pfpTextureWrap = SpheneUI.Media.Texture(pfpKey, _profileImage);
        }

        if (!string.Equals(_profileDescription, profile.Description, StringComparison.OrdinalIgnoreCase))
        {
            _profileDescription = profile.Description;
            _descriptionText = _profileDescription;
        }
        if (!string.Equals(_profileStatus, profile.StatusMessage, StringComparison.Ordinal))
        {
            _profileStatus = profile.StatusMessage;
            _statusText = _profileStatus;
        }
        if (!string.Equals(_profileTags, profile.Tags, StringComparison.Ordinal))
        {
            _profileTags = profile.Tags;
            _tagsText = _profileTags;
        }
        if (!string.Equals(_profileTheme, profile.Theme, StringComparison.Ordinal))
        {
            _profileTheme = profile.Theme;
            var t = SpheneProfileTheme.Parse(_profileTheme);
            _useAccent = t.Accent.HasValue;
            _useBannerTop = t.BannerTop.HasValue;
            _useBannerBottom = t.BannerBottom.HasValue;
            if (t.Accent.HasValue) _accentEdit = SpheneProfileTheme.ToVector4(t.Accent.Value);
            if (t.BannerTop.HasValue) _bannerTopEdit = SpheneProfileTheme.ToVector4(t.BannerTop.Value);
            if (t.BannerBottom.HasValue) _bannerBottomEdit = SpheneProfileTheme.ToVector4(t.BannerBottom.Value);
        }
        _profileVisibility = profile.Visibility;

        if (!embedded)
        {
            DrawProfilePictureCard(profile, pfpTextureWrap);
            DrawStatusCard();
            DrawAppearanceCard();
            DrawDescriptionCard();
            DrawVisibilityCard();
        }
        DrawPrivacyCard();
        DrawProfileRules();
    }

    private void DrawProfilePictureCard(SpheneProfileData profile, IDalamudTextureWrap? pfpTextureWrap)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            if (ImGui.BeginTable("current_profile_table", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Image", ImGuiTableColumnFlags.WidthFixed,
                    128 + ImGui.GetStyle().WindowPadding.X);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (pfpTextureWrap != null)
                {
                    SpheneUI.Media.Image(pfpTextureWrap, ImGuiHelpers.ScaledVector2(128, 128),
                        ImageFit.Cover, rounding: SpheneUI.Spacing.X8, border: true);
                }
                else
                {
                    SpheneUI.Media.Placeholder(ImGuiHelpers.ScaledVector2(128, 128));
                }
                ImGui.TableNextColumn();
                using (SpheneUI.GameFont.Push())
                {
                    var descriptionTextSize = ImGui.CalcTextSize(profile.Description, wrapWidth: 256f);
                    var childFrame = ImGuiHelpers.ScaledVector2(256 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, 128);
                    if (descriptionTextSize.Y > childFrame.Y)
                    {
                        _adjustedForScollBarsOnlineProfile = true;
                    }
                    else
                    {
                        _adjustedForScollBarsOnlineProfile = false;
                    }
                    childFrame = childFrame with
                    {
                        X = childFrame.X + (_adjustedForScollBarsOnlineProfile ? ImGui.GetStyle().ScrollbarSize : 0),
                    };
                    if (ImGui.BeginChildFrame(101, childFrame))
                    {
                        SpheneUI.Text.Wrapped(profile.Description);
                    }
                    ImGui.EndChildFrame();
                }
                ImGui.EndTable();
            }

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FileUpload, "Upload new profile picture"))
            {
                PickAndCrop(ImageCropUi.CropKind.Avatar);
            }
            SpheneUI.AttachTooltip("Pick an image and choose the cropped region — only the crop is uploaded");
            ImGui.SameLine();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Clear uploaded profile picture"))
            {
                _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, "", Description: null));
            }
            SpheneUI.AttachTooltip("Clear your currently uploaded profile picture");
            if (_showFileDialogError)
            {
                SpheneUI.Layout.Callout("The picked file is not a readable image.", FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
            }
        }, title: "Profile Picture", accentColor: SpheneTheme.Current.AccentCyan,
            description: "PNG, max 256×256, 250 KiB — shown with your saved description.");
    }

    /// <summary>File-picks a PNG, then opens the interactive cropper; the cropped result is uploaded.</summary>
    private void PickAndCrop(ImageCropUi.CropKind kind)
    {
        var title = kind == ImageCropUi.CropKind.Avatar
            ? "Select new profile picture" : "Select banner image";
        _fileDialogManager.OpenFileDialog(title, ".png", (success, file) =>
        {
            if (!success) return;
            _ = Task.Run(() =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    var info = Image.Identify(bytes);
                    if (info == null)
                    {
                        if (kind == ImageCropUi.CropKind.Avatar) _showFileDialogError = true;
                        else _showBannerError = true;
                        return;
                    }
                    _showFileDialogError = false;
                    _showBannerError = false;
                    _imageCropUi.Open(kind, bytes, info.Width, info.Height, cropped =>
                    {
                        var base64 = Convert.ToBase64String(cropped);
                        _ = _apiController.UserSetProfile(new UserProfileDto(
                            new UserData(_apiController.UID), Disabled: false, IsNSFW: null,
                            ProfilePictureBase64: kind == ImageCropUi.CropKind.Avatar ? base64 : null,
                            Description: null,
                            BannerImageBase64: kind == ImageCropUi.CropKind.Banner ? base64 : null));
                    });
                }
                catch (Exception ex)
                {
                    if (kind == ImageCropUi.CropKind.Avatar) _showFileDialogError = true;
                    else _showBannerError = true;
                    _logger.LogWarning(ex, "Failed to read picked image {File}", file);
                }
            });
        });
    }

    private void DrawStatusCard()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Plain($"Status message {_statusText.Length}/100");
            if (SpheneUI.Controls.InputText("##profilestatus", ref _statusText, 100,
                    preferredWidth: ImGui.GetContentRegionAvail().X))
            {
                // local edit buffer only — saved via the button below
            }
            SpheneUI.Controls.HelpText("A short public status line shown under your name — keep it brief.");

            SpheneUI.Text.Plain($"Tags {_tagsText.Length}/150");
            if (SpheneUI.Controls.InputText("##profiletags", ref _tagsText, 150,
                    preferredWidth: ImGui.GetContentRegionAvail().X))
            {
                // local edit buffer only — saved via the button below
            }
            SpheneUI.Controls.HelpText("Comma-separated interests shown as chips on your profile, e.g. \"GPosing, Raiding, Venue RP\".");

            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Save, "Save Status & Tags"))
            {
                _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID),
                    Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
                    StatusMessage: _statusText, Tags: _tagsText));
            }
            SpheneUI.AttachTooltip("Saves your status message and tags");
        }, title: "Status & Tags", accentColor: SpheneUI.Palette.SpheneGold,
            description: "A short status line plus interest tags shown on your community profile.");
    }

    private void DrawDescriptionCard()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            if (ImGui.BeginTable("description_table", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Editor", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthFixed, 300);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var widthTextBox = Math.Max(400, (int)(ImGui.GetContentRegionAvail().X - 20));
                var posX = ImGui.GetCursorPosX();
                SpheneUI.Text.Plain($"Description {_descriptionText.Length}/1500");
                ImGui.SetCursorPosX(posX);
                using (SpheneUI.GameFont.Push())
                    SpheneUI.Controls.InputTextMultiline("##description", ref _descriptionText, 1500, ImGuiHelpers.ScaledVector2(widthTextBox, 200));

                ImGui.TableNextColumn();
                SpheneUI.Text.Plain("Preview");
                using (SpheneUI.GameFont.Push())
                {
                    var descriptionTextSizeLocal = ImGui.CalcTextSize(_descriptionText, wrapWidth: 256f);
                    var childFrameLocal = ImGuiHelpers.ScaledVector2(256 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, 200);
                    if (descriptionTextSizeLocal.Y > childFrameLocal.Y)
                    {
                        _adjustedForScollBarsLocalProfile = true;
                    }
                    else
                    {
                        _adjustedForScollBarsLocalProfile = false;
                    }
                    childFrameLocal = childFrameLocal with
                    {
                        X = childFrameLocal.X + (_adjustedForScollBarsLocalProfile ? ImGui.GetStyle().ScrollbarSize : 0),
                    };
                    if (ImGui.BeginChildFrame(102, childFrameLocal))
                    {
                        SpheneUI.Text.Wrapped(_descriptionText);
                    }
                    ImGui.EndChildFrame();
                }
                ImGui.EndTable();

                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Save, "Save Description"))
                {
                    _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, ProfilePictureBase64: null, _descriptionText));
                }
                SpheneUI.AttachTooltip("Sets your profile description text");
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Clear Description"))
                {
                    _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, ProfilePictureBase64: null, ""));
                }
                SpheneUI.AttachTooltip("Clears your profile description text");
            }
        }, title: "Description", accentColor: SpheneTheme.Current.AccentBlue,
            description: "Shown on your profile to everyone paired with you.");
    }

    private void DrawVisibilityCard()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            var isNsfw = _nsfwEdit;
            if (SpheneUI.Controls.Toggle("Profile is NSFW", ref isNsfw))
            {
                _nsfwEdit = isNsfw;
                _pendingNsfwChange = true;
                _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: isNsfw, ProfilePictureBase64: null, Description: null));
            }
            SpheneUI.Controls.HelpText("If your profile description or image can be considered NSFW, toggle this to ON");
        }, title: "Visibility", accentColor: SpheneUI.Palette.EtherealPurple);
    }

    private IDalamudTextureWrap? CurrentBannerTexture()
    {
        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));
        var bannerKey = $"editprofile-banner:{_apiController.UID}";
        if (!_bannerImage.SequenceEqual(profile.BannerImageData.Value))
        {
            _bannerImage = profile.BannerImageData.Value;
            SpheneUI.Media.Evict(bannerKey);
        }
        return SpheneUI.Media.Texture(bannerKey, _bannerImage);
    }

    private void DrawAppearanceCard()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            var bannerWrap = CurrentBannerTexture();
            var scale = SpheneUI.Scale;
            var previewW = MathF.Min(460f * scale, ImGui.GetContentRegionAvail().X);
            var previewH = previewW * (400f / 1200f);
            var pos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(previewW, previewH));
            var drawList = ImGui.GetWindowDrawList();
            if (bannerWrap != null)
            {
                SpheneUI.Media.ImageInRect(drawList, bannerWrap, pos,
                    pos + new Vector2(previewW, previewH), ImageFit.Cover, rounding: 8f * scale);
            }
            else
            {
                // Live preview of the chosen gradient colors (or defaults).
                var a = _useBannerTop ? _bannerTopEdit : SpheneTheme.Current.AccentCyan;
                var b = _useBannerBottom ? _bannerBottomEdit : SpheneTheme.Current.AccentBlue;
                drawList.AddRectFilledMultiColor(pos, pos + new Vector2(previewW, previewH),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(a, 0.45f)),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(b, 0.45f)),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(b, 0.14f)),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(a, 0.14f)));
            }
            drawList.AddRect(pos, pos + new Vector2(previewW, previewH),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.10f)),
                8f * scale);
            SpheneUI.Controls.HelpText("Banner shown on top of your profile page. PNG, up to 1200×400 — larger images are cropped to fit.");

            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FileUpload, "Upload banner"))
            {
                PickAndCrop(ImageCropUi.CropKind.Banner);
            }
            SpheneUI.AttachTooltip("Pick an image and choose the cropped banner region — only the crop is uploaded");
            ImGui.SameLine();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Clear banner"))
            {
                _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID),
                    Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
                    BannerImageBase64: ""));
            }
            SpheneUI.AttachTooltip("Remove your banner — the theme gradient is used instead");
            if (_showBannerError)
            {
                SpheneUI.Layout.Callout(
                    "The banner must be a readable PNG that still fits 400KiB after cropping.",
                    FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
            }

            SpheneUI.Layout.Separator();
            SpheneUI.Profile.ThemeColorRow("Custom accent color", ref _useAccent, ref _accentEdit);
            SpheneUI.Profile.ThemeColorRow("Custom banner top color", ref _useBannerTop, ref _bannerTopEdit);
            SpheneUI.Profile.ThemeColorRow("Custom banner bottom color", ref _useBannerBottom, ref _bannerBottomEdit);

            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Save, "Save colors"))
            {
                var theme = SpheneProfileTheme.Parse(_profileTheme) with
                {
                    Accent = _useAccent ? SpheneProfileTheme.FromVector4(_accentEdit) : null,
                    BannerTop = _useBannerTop ? SpheneProfileTheme.FromVector4(_bannerTopEdit) : null,
                    BannerBottom = _useBannerBottom ? SpheneProfileTheme.FromVector4(_bannerBottomEdit) : null,
                };
                _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID),
                    Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
                    Theme: theme.Serialize()));
            }
            SpheneUI.AttachTooltip("Saves your accent and banner gradient colors");
        }, title: "Appearance", accentColor: SpheneUI.Palette.SpheneGold,
            description: "Colors and banner your profile shows to you and paired users.");
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
