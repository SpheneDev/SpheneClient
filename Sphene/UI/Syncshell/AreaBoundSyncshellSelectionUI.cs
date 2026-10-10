using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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
    private int _selectedIndex = -1;
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
        
        Size = new Vector2(600, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;
        
        Mediator.Subscribe<AreaBoundSyncshellSelectionRequestMessage>(this, OnSelectionRequest);
        Mediator.Subscribe<AreaBoundLocationChangedMessage>(this, OnLocationChanged);
    }

    private void OnSelectionRequest(AreaBoundSyncshellSelectionRequestMessage message)
    {
        _availableSyncshells = message.AvailableSyncshells;
        _selectedIndex = -1;
        _errorMessage = string.Empty;
        _openedAt = DateTime.UtcNow;
        IsOpen = true;
        _logger.LogDebug("Received selection request for {count} syncshells", message.AvailableSyncshells.Count);
    }

    protected override void DrawInternal()
    {
        if (_availableSyncshells == null || _availableSyncshells.Count == 0) return;

        // Header
        SpheneUI.Text.PageHeader($"Multiple Area Syncshells Available ({_availableSyncshells.Count})");

        SpheneUI.Text.MutedWrapped("Multiple area-bound syncshells are available in this location. Expand each group to see details and select which one you would like to join:");

        SpheneUI.Layout.VerticalSpace();

        // Syncshell list with collapsible groups
        if (SpheneUI.Layout.BeginScrollArea("SyncshellList", new Vector2(0, 300), border: true))
        {
            for (int i = 0; i < _availableSyncshells.Count; i++)
            {
                var syncshell = _availableSyncshells[i];
                
                // Create a collapsible tree node for each syncshell
                var treeNodeFlags = ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed;
                
                // Highlight selected syncshell
                if (_selectedIndex == i)
                {
                    ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.2f, 0.6f, 1.0f, 0.3f));
                    ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.2f, 0.6f, 1.0f, 0.4f));
                    ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.2f, 0.6f, 1.0f, 0.5f));
                }
                
                var headerLabel = syncshell.IsLocked ? $"{syncshell.Group.AliasOrGID} (Locked)" : $"{syncshell.Group.AliasOrGID}";
                bool nodeOpen = ImGui.TreeNodeEx($"##syncshell_tree_{i}", treeNodeFlags, headerLabel);
                
                if (_selectedIndex == i)
                {
                    ImGui.PopStyleColor(3);
                }
                
                // Handle selection when clicking on the tree node
                if (ImGui.IsItemClicked())
                {
                    _selectedIndex = i;
                }
                
                if (nodeOpen)
                {
                    ImGui.Indent();
                    
                    // Syncshell details
                    SpheneUI.Text.Muted($"ID: {syncshell.Group.GID}");

                    // Rules info
                    if (syncshell.Settings.RequireRulesAcceptance && !string.IsNullOrEmpty(syncshell.Settings.JoinRules))
                    {
                        SpheneUI.Text.Colored("⚠ Requires rules acceptance", ImGuiColors.DalamudYellow);
                    }

                    if (syncshell.IsLocked)
                    {
                        SpheneUI.Text.Colored("This syncshell is locked. Joining is disabled.", ImGuiColors.DalamudRed);
                    }

                    // Custom join message
                    if (!string.IsNullOrEmpty(syncshell.Settings.CustomJoinMessage))
                    {
                        SpheneUI.Text.MutedWrapped(syncshell.Settings.CustomJoinMessage);
                    }

                    // Selection button within the expanded node
                    if (_selectedIndex != i)
                    {
                        if (SpheneUI.Controls.Button($"Select##select_{i}"))
                        {
                            _selectedIndex = i;
                        }
                    }
                    else
                    {
                        SpheneUI.Text.Colored("✓ Selected", SpheneUI.Palette.CrystalBlue);
                    }
                    
                    ImGui.Unindent();
                    ImGui.TreePop();
                }
                
                SpheneUI.Layout.VerticalSpace();
            }
        }
        SpheneUI.Layout.EndScrollArea();

        // Error message
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            SpheneUI.Layout.Callout(_errorMessage, Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
        }

        SpheneUI.Layout.Separator();

        // Buttons
        var buttonWidth = (SpheneUI.Controls.BoundedAvailX() - ImGui.GetStyle().ItemSpacing.X * 2) / 3;
        var buttonSize = new Vector2(buttonWidth, 0);

        // Join button
        bool canJoin = _selectedIndex >= 0
                       && _selectedIndex < _availableSyncshells.Count
                       && !_availableSyncshells[_selectedIndex].IsLocked;
        using (ImRaii.Disabled(!canJoin))
        {
            if (SpheneUI.Controls.Button("Join Selected", buttonSize))
            {
                _ = Task.Run(async () =>
                {
                    try { await HandleJoin().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogError(ex, "Background error joining selected area-bound syncshell"); }
                });
            }
        }

        if (!canJoin)
        {
            SpheneUI.AttachTooltip(_selectedIndex >= 0 && _selectedIndex < _availableSyncshells.Count && _availableSyncshells[_selectedIndex].IsLocked
                ? "This syncshell is locked. Joining is disabled."
                : "Please select a syncshell to join");
        }

        ImGui.SameLine();

        // Join All button
        var allLocked = _availableSyncshells.All(s => s.IsLocked);
        using (ImRaii.Disabled(allLocked))
        {
            if (SpheneUI.Controls.Button("Join All", buttonSize))
            {
                _ = Task.Run(async () =>
                {
                    try { await HandleJoinAll().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogError(ex, "Background error joining all area-bound syncshells"); }
                });
            }
        }

        SpheneUI.AttachTooltip(allLocked
            ? "All syncshells in this area are locked. Joining is disabled."
            : "Join all available syncshells in this area");

        ImGui.SameLine();

        // Cancel button
        if (SpheneUI.Controls.Button("Cancel", buttonSize))
        {
            _logger.LogDebug("User cancelled syncshell selection");
            _availableSyncshells = null;
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

    private async Task HandleJoin()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _availableSyncshells!.Count) return;
        
        var selectedSyncshell = _availableSyncshells[_selectedIndex];
        _logger.LogDebug("User selected syncshell: {syncshellId}", selectedSyncshell.Group.GID);
        
        try
        {
            if (selectedSyncshell.IsLocked)
            {
                _errorMessage = "This syncshell is locked. Joining is currently disabled.";
                return;
            }

            // Always check if user already has valid consent (for auto-rejoin)
            var hasValidConsent = await _apiController.GroupCheckAreaBoundConsent(selectedSyncshell.Group.GID).ConfigureAwait(false);
            
            if (hasValidConsent)
            {
                // Auto-join without showing consent UI
                _logger.LogDebug("User has valid consent for syncshell {SyncshellId}, auto-joining", selectedSyncshell.Group.GID);
                await _areaBoundService.JoinAreaBoundSyncshell(selectedSyncshell.Group.GID, true, selectedSyncshell.Settings.RulesVersion).ConfigureAwait(false);
            }
            else
            {
                // Check if user consent is required for new users
                bool requiresRulesAcceptance = selectedSyncshell.Settings.RequireRulesAcceptance && 
                                             !string.IsNullOrEmpty(selectedSyncshell.Settings.JoinRules);
                
                if (requiresRulesAcceptance)
                {
                    // Show consent UI for the selected syncshell
                    var consentMessage = new AreaBoundSyncshellConsentRequestMessage(selectedSyncshell, requiresRulesAcceptance);
                    Mediator.Publish(consentMessage);
                }
                else
                {
                    // Join directly without consent
                    await _areaBoundService.JoinAreaBoundSyncshell(selectedSyncshell.Group.GID, false, 0).ConfigureAwait(false);
                }
            }
            
            _availableSyncshells = null;
            IsOpen = false;
            _errorMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining selected area-bound syncshell");
            _errorMessage = "Failed to join syncshell. Please try again.";
        }
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
                _selectedIndex = -1;
                _errorMessage = string.Empty;
            }
            else
            {
                _logger.LogDebug("Location changed but selection UI was only open for {seconds}s, keeping open", timeSinceOpened.TotalSeconds);
            }
        }
    }
}
