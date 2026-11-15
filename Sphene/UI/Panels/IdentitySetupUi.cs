using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using Sphene.UI.Design;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Sphene.UI.Panels;

/// <summary>
/// Mandatory identity setup shown when the connected user has no alias yet.
/// Non-closable until an alias is set · all other hub calls are blocked server-side.
/// </summary>
public class IdentitySetupUi : WindowMediatorSubscriberBase
{
    private static readonly Regex _aliasFormatRegex = new(Constants.AliasValidationPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly LodestoneAvatarService _lodestoneAvatarService;

    private string _aliasInput = string.Empty;
    private string _displayNameInput = string.Empty;
    private byte[]? _avatarBytes;
    private string? _avatarBase64;
    private byte[]? _avatarTexSource;
    private string _avatarTexKey = "identity:avatar";
    private string? _statusMessage;
    private Vector4 _statusColor = SpheneUI.Colors.TextMuted;
    private AliasAvailabilityResult? _checkedResult;
    private string _checkedAlias = string.Empty;
    private bool _submitting;
    private bool _checking;
    private long _aliasEditedAt;

    public IdentitySetupUi(ILogger<IdentitySetupUi> logger, SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        ApiController apiController, DalamudUtilService dalamudUtilService,
        LodestoneAvatarService lodestoneAvatarService)
        : base(logger, mediator, "Sphene Identity Setup", performanceCollectorService)
    {
        _apiController = apiController;
        _dalamudUtilService = dalamudUtilService;
        _lodestoneAvatarService = lodestoneAvatarService;

        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(460, 340),
            MaximumSize = new Vector2(460, 400),
        };

        Mediator.Subscribe<SwitchToIdentitySetupUiMessage>(this, (msg) =>
        {
            if (_apiController.RequiresAliasSetup)
            {
                IsOpen = true;
                _lodestoneAvatarService.EnsurePrefetch();
                _ = PrefillDisplayNameAsync();
            }
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => IsOpen = false);
    }

    protected override void DrawInternal()
    {
        SpheneUI.Layout.PageHeader("Set up your identity", "identitysetup", () =>
        {
            SpheneUI.Text.MutedWrapped("Link your character to a Sphene secret key - required once before syncing.", ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });

        var formatOk = _aliasFormatRegex.IsMatch(_aliasInput.Trim());
        var displayNameOk = _displayNameInput.Trim().Length > 0;
        SpheneUI.Layout.RowGroup(() =>
        {
            SpheneUI.Text.Wrapped(
                "Sphene identifies you by a unique handle instead of a raw account ID. " +
                "Pick your handle once · it is used for pair requests and your profile. " +
                "Both handle and display name are required before continuing.");
            SpheneUI.Layout.VerticalSpace();

            DrawAvatarRow();
            SpheneUI.Layout.VerticalSpace();

            SpheneUI.Controls.InputTextWithHint("Display name",
                "required · shown instead of your alias", ref _displayNameInput, Constants.DisplayNameMaxLength);
            if (!displayNameOk)
            {
                SpheneUI.Text.Muted("Required");
            }

            SpheneUI.Layout.VerticalSpace();

            var aliasChanged = SpheneUI.Controls.InputText("Alias", ref _aliasInput, 15,
                ImGuiInputTextFlags.CharsNoBlank);
            DrawHandlePrefix();
            if (aliasChanged)
            {
                _checkedResult = null;
                _statusMessage = null;
                _aliasEditedAt = Environment.TickCount64;
            }

            if (_aliasInput.Length > 0 && !formatOk)
            {
                SpheneUI.Text.Colored("5-15 characters: letters, digits, . _ -", SpheneUI.Colors.Warning);
            }
            else
            {
                SpheneUI.Text.Muted("5-15 characters: letters, digits, . _ -");
            }

            // debounced availability check - fires while the user pauses typing
            var wantsCheck = formatOk && !_submitting
                && !string.Equals(_checkedAlias, _aliasInput.Trim(), StringComparison.OrdinalIgnoreCase);
            if (wantsCheck && !_checking && Environment.TickCount64 - _aliasEditedAt > 400)
            {
                _ = CheckAvailabilityAsync();
            }
            // status line always occupies one text row - the card must not
            // jump when the check result pops in
            if (formatOk)
            {
                var current = _aliasInput.Trim();
                if (_checking
                    || !string.Equals(_checkedAlias, current, StringComparison.OrdinalIgnoreCase))
                {
                    SpheneUI.Text.Muted("Checking…");
                }
                else
                {
                    DrawCheckResult();
                }
            }
            else
            {
                ImGui.Dummy(new Vector2(1f, ImGui.GetTextLineHeight()));
            }

            if (_statusMessage != null)
            {
                SpheneUI.Layout.VerticalSpace();
                SpheneUI.Text.ColoredWrapped(_statusMessage, _statusColor);
            }
        });

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace();

        var canConfirm = formatOk && displayNameOk && !_submitting;
        if (SpheneUI.Controls.PrimaryButton("Confirm and connect", FontAwesomeIcon.Check,
                size: new Vector2(-1, 32), enabled: canConfirm))
        {
            _ = SubmitAsync();
        }

        SpheneUI.Layout.VerticalSpace();
        if (SpheneUI.Controls.Button("Disconnect", new Vector2(-1, 0), danger: true))
        {
            IsOpen = false;
            Mediator.Publish(new DalamudLogoutMessage());
        }
    }

    /// <summary>Centered avatar preview - click opens the photo studio. Custom
    /// picks stage here and are committed with the identity on confirm; without
    /// a pick the Lodestone portrait (auto-seeded on connect) shows.</summary>
    private void DrawAvatarRow()
    {
        var preview = _avatarBytes ?? _lodestoneAvatarService.PrefetchedPortrait;
        if (!ReferenceEquals(preview, _avatarTexSource))
        {
            _avatarTexSource = preview;
            _avatarTexKey = $"identity:avatar:{Guid.NewGuid():N}";
        }
        var wrap = preview != null ? SpheneUI.Media.Texture(_avatarTexKey, preview) : null;

        var size = 56f * SpheneUI.Scale;
        var avail = ImGui.GetContentRegionAvail().X;
        var x = ImGui.GetCursorScreenPos().X + MathF.Max(0f, (avail - size) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(x, ImGui.GetCursorScreenPos().Y));
        var avatarPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        SpheneUI.Media.Avatar(wrap, size);
        if (wrap == null && !_lodestoneAvatarService.PrefetchSettled)
        {
            SpheneUI.Fx.RotatingSpinner(avatarPos + new Vector2(size * 0.5f),
                size * 0.32f, 2f * SpheneUI.Scale);
        }

        // click zone - same affordance as the profile hero avatar edit
        ImGui.SetCursorScreenPos(avatarPos);
        if (ImGui.InvisibleButton("##identity-avatar-edit", new Vector2(size)))
        {
            Mediator.Publish(new OpenPhotoStudioMessage(null, PhotoStudioTarget.ProfileAvatar, prepared =>
            {
                _avatarBytes = prepared.Bytes;
                _avatarBase64 = prepared.Base64;
            }));
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            drawList.AddRectFilled(avatarPos, avatarPos + new Vector2(size),
                SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.45f)), size * 0.5f);
            using (SpheneUI.Icons.PushFont())
            {
                var cam = FontAwesomeIcon.Camera.ToIconString();
                var camSize = ImGui.CalcTextSize(cam);
                drawList.AddText(avatarPos + (new Vector2(size) - camSize) * 0.5f,
                    SpheneUI.ColorUtils.ToU32(Vector4.One), cam);
            }
        }
        SpheneUI.AttachTooltip("Click to change avatar");
    }

    /// <summary>@ floats in the gap between the left label and the right-docked
    /// field - marks the handle without touching layout.</summary>
    private static void DrawHandlePrefix()
    {
        var fieldMin = ImGui.GetItemRectMin();
        var atW = ImGui.CalcTextSize("@").X;
        var restore = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(fieldMin.X - atW - 6f * SpheneUI.Scale,
            fieldMin.Y + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f));
        SpheneUI.Text.Muted("@");
        ImGui.SetCursorScreenPos(restore);
    }

    private async Task PrefillDisplayNameAsync()
    {
        if (_displayNameInput.Length > 0) return;
        var name = await _dalamudUtilService.GetPlayerNameAsync().ConfigureAwait(false);
        if (!string.Equals(name, "--", StringComparison.Ordinal) && _displayNameInput.Length == 0)
        {
            _displayNameInput = name;
        }
    }

    private void DrawCheckResult()
    {
        if (_checkedResult == null)
        {
            SpheneUI.Text.Muted("Not checked");
            return;
        }

        switch (_checkedResult.Value)
        {
            case AliasAvailabilityResult.Available:
                SpheneUI.Text.Colored("Available", SpheneUI.Colors.Success);
                break;
            case AliasAvailabilityResult.Taken:
                SpheneUI.Text.Colored("Taken · pick another", SpheneUI.Colors.Danger);
                break;
            default:
                SpheneUI.Text.Colored("Invalid", SpheneUI.Colors.Danger);
                break;
        }
    }

    private async Task CheckAvailabilityAsync()
    {
        var alias = _aliasInput.Trim();
        _checkedAlias = alias;
        _checkedResult = null;
        _checking = true;
        try
        {
            _checkedResult = await _apiController.UserCheckAliasAvailability(alias).ConfigureAwait(false);
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task SubmitAsync()
    {
        _submitting = true;
        _statusMessage = null;
        try
        {
            var result = await _apiController.UserSetAlias(_aliasInput.Trim()).ConfigureAwait(false);
            if (result != SetAliasResult.Success)
            {
                _statusColor = SpheneUI.Colors.Danger;
                _statusMessage = result switch
                {
                    SetAliasResult.Taken => "This alias is already taken. Pick another.",
                    SetAliasResult.AlreadySet => "An alias is already set for this account.",
                    _ => "Invalid alias. Check the format rules above.",
                };
                return;
            }

            await _apiController.UserSetDisplayName(_displayNameInput.Trim()).ConfigureAwait(false);
            if (_avatarBase64 != null)
            {
                try
                {
                    await _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID),
                        Disabled: false, IsNSFW: null, ProfilePictureBase64: _avatarBase64,
                        Description: null)).ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to apply staged avatar"); }
            }

            IsOpen = false;
            await _apiController.CompleteIdentitySetupAsync().ConfigureAwait(false);
        }
        finally
        {
            _submitting = false;
        }
    }
}
