using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Sphene.API.Data;
using Sphene.API.Dto.User;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Diagnostics;
using Sphene.UI.Styling;

namespace Sphene.UI;

public class EditProfileUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly FileDialogManager _fileDialogManager;
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly UiSharedService _uiSharedService;
    private bool _adjustedForScollBarsLocalProfile = false;
    private bool _adjustedForScollBarsOnlineProfile = false;
    private string _descriptionText = string.Empty;
    private IDalamudTextureWrap? _pfpTextureWrap;
    private string _profileDescription = string.Empty;
    private byte[] _profileImage = [];
    private bool _showFileDialogError = false;
    private bool _wasOpen;
    private bool _openCropperPopupShown = false;
    private bool _pendingNsfwChange = false;
    private bool _nsfwEdit = false;

    public EditProfileUi(ILogger<EditProfileUi> logger, SpheneMediator mediator,
        ApiController apiController, UiSharedService uiSharedService, FileDialogManager fileDialogManager,
        SpheneProfileManager spheneProfileManager, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Sphene Edit Profile###SpheneEditProfileUI", performanceCollectorService)
    {
        IsOpen = false;
        this.SizeConstraints = new()
        {
            MinimumSize = new(768, 512),
            MaximumSize = new(768, 2000)
        };
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _fileDialogManager = fileDialogManager;
        _spheneProfileManager = spheneProfileManager;

        Mediator.Subscribe<GposeStartMessage>(this, (_) => { _wasOpen = IsOpen; IsOpen = false; });
        Mediator.Subscribe<GposeEndMessage>(this, (_) => IsOpen = _wasOpen);
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData == null || string.Equals(msg.UserData.UID, _apiController.UID, StringComparison.Ordinal))
            {
                _pfpTextureWrap?.Dispose();
                _pfpTextureWrap = null;
            }
        });
    }

    protected override void DrawInternal()
    {
        SpheneUIEnhancements.DrawSpheneHeader("Current Profile (as saved on server)");

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
            UiSharedService.ColorTextWrapped(profile.Description, ImGuiColors.DalamudRed);
            return;
        }

        if (!_profileImage.SequenceEqual(profile.ImageData.Value))
        {
            _profileImage = profile.ImageData.Value;
            _pfpTextureWrap?.Dispose();
            _pfpTextureWrap = _uiSharedService.LoadImage(_profileImage);
        }

        if (!string.Equals(_profileDescription, profile.Description, StringComparison.OrdinalIgnoreCase))
        {
            _profileDescription = profile.Description;
            _descriptionText = _profileDescription;
        }


        if (_uiSharedService.IconTextButton(FontAwesomeIcon.FileUpload, "Upload new profile picture"))
        {
            _fileDialogManager.OpenFileDialog("Select new Profile picture", ".png", (success, file) =>
            {
                if (!success) return;
                _ = Task.Run(async () =>
                {
                    var fileContent = File.ReadAllBytes(file);
                    using MemoryStream ms = new(fileContent);
                    var format = await Image.DetectFormatAsync(ms).ConfigureAwait(false);
                    if (!format.FileExtensions.Contains("png", StringComparer.OrdinalIgnoreCase))
                    {
                        _showFileDialogError = true;
                        return;
                    }
                    using var image = Image.Load<Rgba32>(fileContent);

                    if (image.Width > 256 || image.Height > 256 || (fileContent.Length > 250 * 1024))
                    {
                        _showFileDialogError = true;
                        return;
                    }

                    _showFileDialogError = false;
                    await _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, Convert.ToBase64String(fileContent), Description: null))
                        .ConfigureAwait(false);
                });
            });
        }
        UiSharedService.AttachToolTip("Select and upload a new profile picture");
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Link, "Open image cropper (256x256)"))
        {
            _openCropperPopupShown = true;
            ImGui.OpenPopup("Open Image Cropper?");
        }
        UiSharedService.AttachToolTip("Open online image cropper to create a 256x256 PNG in your browser (no image upload)");
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Clear uploaded profile picture"))
        {
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, "", Description: null));
        }
        UiSharedService.AttachToolTip("Clear your currently uploaded profile picture");
        if (_showFileDialogError)
        {
            UiSharedService.ColorTextWrapped("The profile picture must be a PNG file with a maximum height and width of 256px and 250KiB size", ImGuiColors.DalamudRed);
        }

        if (_openCropperPopupShown)
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(new System.Numerics.Vector2(viewport.WorkPos.X + viewport.WorkSize.X / 2f, viewport.WorkPos.Y + viewport.WorkSize.Y / 2f), ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(ImGuiHelpers.ScaledVector2(640, 0), ImGuiCond.Appearing);
        }
        if (ImGui.BeginPopupModal("Open Image Cropper?", ref _openCropperPopupShown, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped("Open your default browser to the Sphene online image cropper (256x256).");
            ImGui.Spacing();
            ImGui.BulletText("No image data is transmitted to Sphene servers.");
            ImGui.BulletText("All processing happens locally in your browser.");
            ImGui.BulletText("After cropping, download the PNG and upload it here.");
            ImGui.Spacing();
            UiSharedService.ColorTextWrapped("Security: processing is browser-side only. No uploads.", ImGuiColors.ParsedGreen);
            ImGui.Separator();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Check, "OK", null, true))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "https://sphene.online/cropper/",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                Process.Start(psi);
                ImGui.CloseCurrentPopup();
                _openCropperPopupShown = false;
            }
            ImGui.SameLine();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, "Cancel", null, true))
            {
                ImGui.CloseCurrentPopup();
                _openCropperPopupShown = false;
            }
            ImGui.EndPopup();
        }

        if (ImGui.BeginTable("current_profile_table", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Image", ImGuiTableColumnFlags.WidthFixed, 256 + ImGui.GetStyle().WindowPadding.X);
            ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (_pfpTextureWrap != null)
            {
                ImGui.Image(_pfpTextureWrap.Handle, ImGuiHelpers.ScaledVector2(_pfpTextureWrap.Width, _pfpTextureWrap.Height));
            }
            ImGui.TableNextColumn();
            using (_uiSharedService.GameFont.Push())
            {
                var descriptionTextSize = ImGui.CalcTextSize(profile.Description, wrapWidth: 256f);
                var childFrame = ImGuiHelpers.ScaledVector2(256 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, 256);
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
                    UiSharedService.TextWrapped(profile.Description);
                }
                ImGui.EndChildFrame();
            }
            ImGui.EndTable();
        }

        var isNsfw = _nsfwEdit;
        if (ImGui.Checkbox("Profile is NSFW", ref isNsfw))
        {
            _nsfwEdit = isNsfw;
            _pendingNsfwChange = true;
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: isNsfw, ProfilePictureBase64: null, Description: null));
        }
        _uiSharedService.DrawHelpText("If your profile description or image can be considered NSFW, toggle this to ON");

        if (ImGui.BeginTable("description_table", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Editor", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var widthTextBox = Math.Max(400, (int)(ImGui.GetContentRegionAvail().X - 20));
            var posX = ImGui.GetCursorPosX();
            ImGui.TextUnformatted($"Description {_descriptionText.Length}/1500");
            ImGui.SetCursorPosX(posX);
            using (_uiSharedService.GameFont.Push())
                ImGui.InputTextMultiline("##description", ref _descriptionText, 1500, ImGuiHelpers.ScaledVector2(widthTextBox, 200));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted("Preview");
            using (_uiSharedService.GameFont.Push())
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
                    UiSharedService.TextWrapped(_descriptionText);
                }
                ImGui.EndChildFrame();
            }
            ImGui.EndTable();

            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Save Description"))
            {
                _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, ProfilePictureBase64: null, _descriptionText));
            }
            UiSharedService.AttachToolTip("Sets your profile description text");
            ImGui.SameLine();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Clear Description"))
            {
                _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID), Disabled: false, IsNSFW: null, ProfilePictureBase64: null, ""));
            }
            UiSharedService.AttachToolTip("Clears your profile description text");
        }

        SpheneUIEnhancements.DrawSpheneSection("Notes and Rules for Profiles", () =>
        {
            ImGui.TextWrapped("- All users that are paired and unpaused with you will be able to see your profile picture and description." +
                Environment.NewLine + "- Other users have the possibility to report your profile for breaking the rules." +
                Environment.NewLine + "- !!! AVOID: anything as profile image that can be considered highly illegal or obscene (bestiality, anything that could be considered a sexual act with a minor (that includes Lalafells), etc.)" +
                Environment.NewLine + "- !!! AVOID: slurs of any kind in the description that can be considered highly offensive" +
                Environment.NewLine + "- In case of valid reports from other users this can lead to disabling your profile forever or terminating your Sphene account indefinitely." +
                Environment.NewLine + "- Judgement of your profile validity from reports through staff is not up to debate and the decisions to disable your profile/account permanent." +
                Environment.NewLine + "- If your profile picture or profile description could be considered NSFW, enable the toggle below.");
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _pfpTextureWrap?.Dispose();
    }
}
