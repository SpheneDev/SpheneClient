using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Configurations;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Syncshell;

public class AreaBoundSyncshellConsentUI : WindowMediatorSubscriberBase
{
    private readonly AreaBoundSyncshellService _areaBoundService;
    private readonly SpheneConfigService _configService;
    private AreaBoundSyncshellConsentRequestMessage? _currentRequest;
    private bool _rulesAccepted = false;
    private string _errorMessage = string.Empty;
    private DateTime _openedAt;

    public AreaBoundSyncshellConsentUI(ILogger<AreaBoundSyncshellConsentUI> logger, 
        SpheneMediator mediator, 
        PerformanceCollectorService performanceCollectorService,
        AreaBoundSyncshellService areaBoundService,
        SpheneConfigService configService) 
        : base(logger, mediator, "Area-Bound Syncshell Consent###AreaBoundSyncshellConsent", performanceCollectorService)
    {
        _areaBoundService = areaBoundService;
        _configService = configService;
        
        Size = new Vector2(600, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
        
        Mediator.Subscribe<AreaBoundSyncshellConsentRequestMessage>(this, OnConsentRequest);
        Mediator.Subscribe<AreaBoundLocationChangedMessage>(this, OnLocationChanged);
    }

    private void OnConsentRequest(AreaBoundSyncshellConsentRequestMessage message)
    {
        _currentRequest = message;
        _rulesAccepted = false;
        _errorMessage = string.Empty;
        _openedAt = DateTime.UtcNow;
        IsOpen = true;
        _logger.LogDebug("Received consent request for syncshell: {syncshellId}", message.Syncshell.Group.GID);
    }

    protected override void DrawInternal()
    {
        if (_currentRequest == null) return;

        var syncshell = _currentRequest.Syncshell;

        // Header
        SpheneUI.Text.PageHeader("Join Area-Bound Syncshell");

        // Display syncshell information
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Plain($"Syncshell: {_currentRequest.Syncshell.Group.AliasOrGID}");
            SpheneUI.Text.Muted($"ID: {_currentRequest.Syncshell.Group.GID}");
            if (syncshell.IsLocked)
            {
                SpheneUI.Text.Colored("This syncshell is locked. Joining is disabled.", ImGuiColors.DalamudRed);
            }
        });
        
        // Calculate fixed bottom section height
        var buttonHeight = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var separatorHeight = 1.0f;
        
        // Bottom section: separator + auto-show checkbox + rules checkbox (if needed) + error message + buttons + padding
        var bottomSectionHeight = separatorHeight + spacing + // separator
                                 buttonHeight + spacing + // auto-show checkbox
                                 buttonHeight + spacing + // buttons
                                 spacing * 2; // extra padding
        
        // Add rules checkbox if needed
        if (_currentRequest.RequiresRulesAcceptance && !string.IsNullOrEmpty(syncshell.Settings.JoinRules))
        {
            bottomSectionHeight += buttonHeight + spacing; // rules checkbox
        }
        
        // Add error message space if present
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            bottomSectionHeight += 40 + spacing; // error message height
        }
        
        // Rules section - use remaining space but leave room for bottom section
        if (_currentRequest.RequiresRulesAcceptance && !string.IsNullOrEmpty(syncshell.Settings.JoinRules))
        {
            SpheneUI.Text.SectionHeader("Rules (must be accepted to join)");

            // Calculate available height for rules, ensuring bottom section fits
            var availableHeight = ImGui.GetContentRegionAvail().Y;
            var rulesHeight = Math.Max(100, availableHeight - bottomSectionHeight);

            if (SpheneUI.Layout.BeginScrollArea("RulesText", new Vector2(0, rulesHeight), border: true))
            {
                SpheneUI.Text.Wrapped(syncshell.Settings.JoinRules);
            }
            SpheneUI.Layout.EndScrollArea();
        }
        else
        {
            _rulesAccepted = true; // No rules to accept
        }
        
        // Use remaining space to push bottom section down, but ensure it fits
        var remainingSpace = ImGui.GetContentRegionAvail().Y - bottomSectionHeight;
        if (remainingSpace > 0)
        {
            ImGui.Dummy(new Vector2(0, remainingSpace));
        }
        
        // Bottom section starts here
        SpheneUI.Layout.Separator();
        
        // Auto-show setting checkbox
        var autoShowConsent = _configService.Current.AutoShowAreaBoundSyncshellConsent;
        if (SpheneUI.Controls.Toggle("Automatically show area syncshell consent dialogs", ref autoShowConsent))
        {
            _configService.Current.AutoShowAreaBoundSyncshellConsent = autoShowConsent;
            _configService.Save();
        }
        
        // Rules acceptance checkbox (if rules present)
        if (_currentRequest.RequiresRulesAcceptance && !string.IsNullOrEmpty(syncshell.Settings.JoinRules))
        {
            SpheneUI.Controls.Toggle("I accept the rules and agree to follow them", ref _rulesAccepted);
        }
        
        // Error message
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            SpheneUI.Layout.Callout(_errorMessage, Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
        }

        // Buttons
        bool canAccept = (!syncshell.IsLocked) && (!_currentRequest.RequiresRulesAcceptance || _rulesAccepted);
        var buttonSize = new Vector2(
            (SpheneUI.Controls.BoundedAvailX() - ImGui.GetStyle().ItemSpacing.X) * 0.5f, 0f);
        using (ImRaii.Disabled(!canAccept))
        {
            if (SpheneUI.Controls.Button("Accept and Join", buttonSize))
            {
                _ = Task.Run(async () =>
                {
                    try { await HandleAccept().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogError(ex, "Background error during area-bound consent accept"); }
                });
            }
        }

        if (!canAccept)
        {
            SpheneUI.AttachTooltip(syncshell.IsLocked
                ? "This syncshell is locked. Joining is disabled."
                : "You must accept the rules to join this syncshell");
        }

        ImGui.SameLine();

        if (SpheneUI.Controls.Button("Decline", buttonSize))
        {
            _logger.LogDebug("User declined consent for syncshell: {syncshellId}", _currentRequest.Syncshell.Group.GID);
            _currentRequest = null;
            IsOpen = false;
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

    private async Task HandleAccept()
    {
        if (_currentRequest?.Syncshell?.Group == null)
        {
            _errorMessage = "No pending request";
            return;
        }
        _logger.LogDebug("User accepted consent for syncshell: {syncshellId}", _currentRequest.Syncshell.Group.GID);
        
        try
        {
            var rulesVersion = _currentRequest.Syncshell.Settings?.RulesVersion ?? 0;
            await _areaBoundService.JoinAreaBoundSyncshell(_currentRequest.Syncshell.Group.GID, _rulesAccepted, rulesVersion).ConfigureAwait(false);
            _currentRequest = null;
            IsOpen = false;
            _errorMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining area-bound syncshell");
            _errorMessage = "Failed to join syncshell. Please try again.";
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
                _logger.LogDebug("Location changed while consent UI was open, closing popup (open for {seconds}s)", timeSinceOpened.TotalSeconds);
                IsOpen = false;
                _currentRequest = null;
                _rulesAccepted = false;
                _errorMessage = string.Empty;
            }
            else
            {
                _logger.LogDebug("Location changed but consent UI was only open for {seconds}s, keeping open", timeSinceOpened.TotalSeconds);
            }
        }
    }
}
