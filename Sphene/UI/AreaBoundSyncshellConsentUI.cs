using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.Services.Mediator;
using System.Numerics;

namespace Sphene.UI;

public class AreaBoundSyncshellConsentUI : WindowMediatorSubscriberBase
{
    private readonly AreaBoundSyncshellService _areaBoundService;
    private AreaBoundSyncshellConsentRequestMessage? _currentRequest;
    private bool _rulesAccepted = false;
    private string _errorMessage = string.Empty;
    private DateTime _openedAt;

    public AreaBoundSyncshellConsentUI(ILogger<AreaBoundSyncshellConsentUI> logger, 
        SpheneMediator mediator, 
        PerformanceCollectorService performanceCollectorService,
        AreaBoundSyncshellService areaBoundService) 
        : base(logger, mediator, "Area-Bound Syncshell Consent###AreaBoundSyncshellConsent", performanceCollectorService)
    {
        _areaBoundService = areaBoundService;
        
        Size = new Vector2(500, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;
        
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
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.2f, 0.8f, 1.0f, 1.0f));
        ImGui.Text($"Join Area-Bound Syncshell");
        ImGui.PopStyleColor();
        
        ImGui.Separator();
        
        // Display syncshell information
        ImGui.Text($"Syncshell: {_currentRequest.Syncshell.Group.AliasOrGID}");
        ImGui.Text($"ID: {_currentRequest.Syncshell.Group.GID}");
        
        // Calculate space needed for bottom elements
        var buttonHeight = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var bottomSpace = buttonHeight + spacing * 3; // Buttons + separators + spacing
        
        // Add checkbox height if rules are present
        if (_currentRequest.RequiresRulesAcceptance && !string.IsNullOrEmpty(syncshell.Settings.JoinRules))
        {
            bottomSpace += buttonHeight + spacing; // Checkbox
        }
        
        // Error message space if present
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            bottomSpace += 30; // Approximate error message height
        }
        
        // Rules section - use all remaining space
        if (_currentRequest.RequiresRulesAcceptance && !string.IsNullOrEmpty(syncshell.Settings.JoinRules))
        {
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.8f, 0.2f, 1.0f));
            ImGui.Text("Rules (must be accepted to join):");
            ImGui.PopStyleColor();
            
            // Use all remaining space for rules text
            var rulesHeight = ImGui.GetContentRegionAvail().Y - bottomSpace;
            if (rulesHeight < 100) rulesHeight = 100;
            
            using var child = ImRaii.Child("RulesText", new Vector2(0, rulesHeight), true);
            if (child)
            {
                ImGui.TextWrapped(syncshell.Settings.JoinRules);
            }
        }
        else
        {
            _rulesAccepted = true; // No rules to accept
        }
        
        // Push everything to bottom
        var remainingHeight = ImGui.GetContentRegionAvail().Y - (buttonHeight + spacing * 2);
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            remainingHeight -= 30;
        }
        if (_currentRequest.RequiresRulesAcceptance && !string.IsNullOrEmpty(syncshell.Settings.JoinRules))
        {
            remainingHeight -= (buttonHeight + spacing);
        }
        
        if (remainingHeight > 0)
        {
            ImGui.Dummy(new Vector2(0, remainingHeight));
        }
        
        // Checkbox at bottom (if rules present)
        if (_currentRequest.RequiresRulesAcceptance && !string.IsNullOrEmpty(syncshell.Settings.JoinRules))
        {
            ImGui.Checkbox("I accept the rules and agree to follow them", ref _rulesAccepted);
        }
        
        // Error message
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.3f, 0.3f, 1.0f));
            ImGui.TextWrapped(_errorMessage);
            ImGui.PopStyleColor();
        }
        
        ImGui.Separator();
        
        // Buttons at very bottom
        var buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        
        // Accept button
        bool canAccept = !_currentRequest.RequiresRulesAcceptance || _rulesAccepted;
        using (ImRaii.Disabled(!canAccept))
        {
            if (ImGui.Button("Accept and Join", new Vector2(buttonWidth, 0)))
            {
                HandleAccept();
            }
        }
        
        if (!canAccept && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("You must accept the rules to join this syncshell");
        }
        
        ImGui.SameLine();
        
        // Decline button
        if (ImGui.Button("Decline", new Vector2(buttonWidth, 0)))
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

    private async void HandleAccept()
    {
        _logger.LogDebug("User accepted consent for syncshell: {syncshellId}", _currentRequest.Syncshell.Group.GID);
        
        try
        {
            await _areaBoundService.JoinAreaBoundSyncshell(_currentRequest.Syncshell.Group.GID, _rulesAccepted, _currentRequest.Syncshell.Settings.RulesVersion);
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