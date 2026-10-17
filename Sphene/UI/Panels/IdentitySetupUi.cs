using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
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
/// Non-closable until an alias is set — all other hub calls are blocked server-side.
/// </summary>
public class IdentitySetupUi : WindowMediatorSubscriberBase
{
    private static readonly Regex _aliasFormatRegex = new(Constants.AliasValidationPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private readonly ApiController _apiController;

    private string _aliasInput = string.Empty;
    private string _displayNameInput = string.Empty;
    private string? _statusMessage;
    private Vector4 _statusColor = SpheneUI.Colors.TextMuted;
    private AliasAvailabilityResult? _checkedResult;
    private string _checkedAlias = string.Empty;
    private bool _submitting;

    public IdentitySetupUi(ILogger<IdentitySetupUi> logger, SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        ApiController apiController)
        : base(logger, mediator, "Sphene Identity Setup", performanceCollectorService)
    {
        _apiController = apiController;

        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(460, 300),
            MaximumSize = new Vector2(460, 340),
        };

        Mediator.Subscribe<SwitchToIdentitySetupUiMessage>(this, (_) =>
        {
            if (_apiController.RequiresAliasSetup)
            {
                IsOpen = true;
            }
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => IsOpen = false);
    }

    protected override void DrawInternal()
    {
        SpheneUI.Text.PageHeader("Set up your identity");

        var formatOk = _aliasFormatRegex.IsMatch(_aliasInput.Trim());
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Wrapped(
                "Sphene identifies you by a unique handle instead of a raw account ID. " +
                "Pick your handle once — it is used for pair requests and your profile. " +
                "You cannot use Sphene until a handle is set.");
            SpheneUI.Layout.VerticalSpace();

            var aliasChanged = SpheneUI.Controls.InputText("Alias", ref _aliasInput, 15,
                ImGuiInputTextFlags.CharsNoBlank);
            if (aliasChanged)
            {
                _checkedResult = null;
                _statusMessage = null;
            }

            if (_aliasInput.Length > 0 && !formatOk)
            {
                SpheneUI.Text.Colored("5-15 characters: letters, digits, . _ -", SpheneUI.Colors.Warning);
            }
            else
            {
                SpheneUI.Text.Muted("5-15 characters: letters, digits, . _ -");
            }

            var canCheck = formatOk && !_submitting
                && !string.Equals(_checkedAlias, _aliasInput.Trim(), StringComparison.OrdinalIgnoreCase);
            using (ImRaii.Disabled(!canCheck))
            {
                if (SpheneUI.Controls.Button("Check availability"))
                {
                    _ = CheckAvailabilityAsync();
                }
            }
            ImGui.SameLine();
            DrawCheckResult();

            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Controls.InputTextWithHint("Display name", "optional — shown instead of your alias",
                ref _displayNameInput, Constants.DisplayNameMaxLength);

            if (_statusMessage != null)
            {
                SpheneUI.Layout.VerticalSpace();
                SpheneUI.Text.ColoredWrapped(_statusMessage, _statusColor);
            }
        });

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace();

        var canConfirm = formatOk && !_submitting;
        using (ImRaii.Disabled(!canConfirm))
        {
            if (SpheneUI.Controls.Button("Confirm and connect", new Vector2(-1, 32)))
            {
                _ = SubmitAsync();
            }
        }

        SpheneUI.Layout.VerticalSpace();
        if (SpheneUI.Controls.Button("Disconnect", new Vector2(-1, 0), danger: true))
        {
            IsOpen = false;
            Mediator.Publish(new DalamudLogoutMessage());
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
                SpheneUI.Text.Colored("Taken — pick another", SpheneUI.Colors.Danger);
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
        _checkedResult = await _apiController.UserCheckAliasAvailability(alias).ConfigureAwait(false);
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

            var displayName = _displayNameInput.Trim();
            if (displayName.Length > 0)
            {
                await _apiController.UserSetDisplayName(displayName).ConfigureAwait(false);
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
