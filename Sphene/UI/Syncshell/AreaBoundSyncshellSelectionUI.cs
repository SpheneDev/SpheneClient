using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Design;
using Sphene.WebAPI;
using System.Numerics;

namespace Sphene.UI.Syncshell;

public class AreaBoundSyncshellSelectionUI : WindowMediatorSubscriberBase
{
    private readonly AreaBoundSyncshellService _areaBoundService;
    private readonly ApiController _apiController;
    private List<AreaBoundSyncshellDto>? _availableSyncshells;
    private string _errorMessage = string.Empty;
    private DateTime _openedAt;


    public AreaBoundSyncshellSelectionUI(ILogger<AreaBoundSyncshellSelectionUI> logger, 
        SpheneMediator mediator, 
        PerformanceCollectorService performanceCollectorService,
        AreaBoundSyncshellService areaBoundService,
        ApiController apiController) 
        : base(logger, mediator, "Select Area Syncshell###AreaBoundSyncshellSelection", performanceCollectorService)
    {
        _areaBoundService = areaBoundService;
        _apiController = apiController;
        
        Size = new Vector2(460, 240);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;
        
        Mediator.Subscribe<AreaBoundSyncshellSelectionRequestMessage>(this, OnSelectionRequest);
        Mediator.Subscribe<AreaBoundLocationChangedMessage>(this, OnLocationChanged);
    }

    private void OnSelectionRequest(AreaBoundSyncshellSelectionRequestMessage message)
    {
        _availableSyncshells = message.AvailableSyncshells;
        _errorMessage = string.Empty;
        _openedAt = DateTime.UtcNow;
        IsOpen = true;
        _logger.LogDebug("Received selection request for {count} syncshells", message.AvailableSyncshells.Count);
    }

    protected override void DrawInternal()
    {
        SpheneUI.Chrome.TitleBar(nameof(AreaBoundSyncshellSelectionUI),
            "Syncshells here", FontAwesomeIcon.MapMarkerAlt,
            SpheneUI.Palette.CrystalBlue,
            rightButtons:
            [
                new SpheneUI.Chrome.TitleBarButton(FontAwesomeIcon.Times,
                    "Close", () => IsOpen = false),
            ]);
        if (_availableSyncshells == null || _availableSyncshells.Count == 0) return;

        SpheneUI.Layout.Callout(
            $"{_availableSyncshells.Count} syncshell{(_availableSyncshells.Count == 1 ? " is" : "s are")} bound to this area · browse their profiles and join from the hub.",
            FontAwesomeIcon.Users, SpheneUI.Palette.CrystalBlue,
            title: "Area-bound syncshells");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        foreach (var shell in _availableSyncshells)
        {
            var label = shell.IsLocked
                ? $"{shell.Group.AliasOrGID} · locked"
                : shell.Settings.RequireRulesAcceptance
                    && !string.IsNullOrEmpty(shell.Settings.JoinRules)
                    ? $"{shell.Group.AliasOrGID} · rules"
                    : shell.Group.AliasOrGID;
            SpheneUI.Text.Muted($"  {label}");
        }

        if (!string.IsNullOrEmpty(_errorMessage))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Callout(_errorMessage, FontAwesomeIcon.ExclamationTriangle,
                SpheneUI.Colors.Danger);
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        var bw = (SpheneUI.Controls.BoundedAvailX() - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        if (SpheneUI.Controls.PrimaryButton("Browse in Hub",
                FontAwesomeIcon.ArrowRight, size: new Vector2(bw, 0)))
        {
            Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Syncshells));
            _availableSyncshells = null;
            IsOpen = false;
        }
        SpheneUI.AttachTooltip("Opens the hub's syncshells page - profiles and join options are there.");
        ImGui.SameLine();
        var allLocked = _availableSyncshells!.All(s => s.IsLocked);
        if (SpheneUI.Controls.PrimaryButton("Join All",
                FontAwesomeIcon.Users, size: new Vector2(bw, 0), enabled: !allLocked))
        {
            _ = Task.Run(async () =>
            {
                try { await HandleJoinAll().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "Background error joining all area-bound syncshells"); }
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Mediator.UnsubscribeAll(this);
        }
        base.Dispose(disposing);
    }

    private async Task HandleJoinAll()
    {
        _logger.LogDebug("User chose to join all {count} syncshells", _availableSyncshells!.Count);
        
        try
        {
            foreach (var syncshell in _availableSyncshells)
            {
                if (syncshell.IsLocked)
                {
                    continue;
                }
                // Always check if user already has valid consent (for auto-rejoin)
                var hasValidConsent = await _apiController.GroupCheckAreaBoundConsent(syncshell.Group.GID).ConfigureAwait(false);
                
                if (hasValidConsent)
                {
                    // Auto-join without showing consent UI
                    _logger.LogDebug("User has valid consent for syncshell {SyncshellId}, auto-joining", syncshell.Group.GID);
                    await _areaBoundService.JoinAreaBoundSyncshell(syncshell.Group.GID, true, syncshell.Settings.RulesVersion).ConfigureAwait(false);
                }
                else
                {
                    // Check if user consent is required for new users
                    bool requiresRulesAcceptance = syncshell.Settings.RequireRulesAcceptance && 
                                                 !string.IsNullOrEmpty(syncshell.Settings.JoinRules);
                    
                    if (requiresRulesAcceptance)
                    {
                        // Show consent UI for each syncshell that requires it
                        var consentMessage = new AreaBoundSyncshellConsentRequestMessage(syncshell, requiresRulesAcceptance);
                        Mediator.Publish(consentMessage);
                    }
                    else
                    {
                        // Join directly without consent
                        await _areaBoundService.JoinAreaBoundSyncshell(syncshell.Group.GID, false, 0).ConfigureAwait(false);
                    }
                }
            }
            
            _availableSyncshells = null;
            IsOpen = false;
            _errorMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining all area-bound syncshells");
            _errorMessage = "Failed to join some syncshells. Please try again.";
        }
    }

    private void OnLocationChanged(AreaBoundLocationChangedMessage message)
    {
        // Automatically close the popup when the user leaves the area
        // But only if the UI has been open for at least 1 second to prevent immediate closure
        if (IsOpen)
        {
            var timeSinceOpened = DateTime.UtcNow - _openedAt;
            if (timeSinceOpened.TotalSeconds >= 1)
            {
                _logger.LogDebug("Location changed while selection UI was open, closing popup (open for {seconds}s)", timeSinceOpened.TotalSeconds);
                IsOpen = false;
                _availableSyncshells = null;
                        _errorMessage = string.Empty;
            }
            else
            {
                _logger.LogDebug("Location changed but selection UI was only open for {seconds}s, keeping open", timeSinceOpened.TotalSeconds);
            }
        }
    }
}
