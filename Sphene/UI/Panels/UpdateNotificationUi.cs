using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Sphene.Services;
using Sphene.Services.Mediator;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Diagnostics;

namespace Sphene.UI.Panels;

public class UpdateNotificationUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiShared;
    private readonly ILogger<UpdateNotificationUi> _logger;
    private readonly ICommandManager _commandManager;
    private UpdateInfo? _updateInfo;
    
    public UpdateNotificationUi(ILogger<UpdateNotificationUi> logger, UiSharedService uiShared, 
        SpheneMediator mediator, PerformanceCollectorService performanceCollectorService,
        ICommandManager commandManager) 
        : base(logger, mediator, "Sphene Update Available", performanceCollectorService)
    {
        _logger = logger;
        _uiShared = uiShared;
        _commandManager = commandManager;
        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(500, 300),
            MaximumSize = new Vector2(600, 800),
        };
        
        Mediator.Subscribe<ShowUpdateNotificationMessage>(this, (msg) =>
        {
            _updateInfo = msg.UpdateInfo;
        });
    }
    
    protected override void DrawInternal()
    {
        if (_updateInfo == null || !_updateInfo.IsUpdateAvailable)
        {
            IsOpen = false;
            return;
        }
        
        if (_uiShared.IsInGpose) return;
        
        // Header
        _uiShared.BigText("Update Available!");
        ImGui.Separator();
        
        // Version info
        UiSharedService.TextWrapped($"A new version of Sphene is available for download.");
        ImGui.Spacing();
        
        using (var table = ImRaii.Table("VersionTable", 2, ImGuiTableFlags.None))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthStretch);
                
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text("Current Version:");
                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.DalamudYellow, _updateInfo.CurrentVersion.ToString());
                
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text("Latest Version:");
                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.HealerGreen, _updateInfo.LatestVersion.ToString());
            }
        }
        
        ImGui.Spacing();
        
        
        ImGui.Spacing();
        
        // Instructions
        UiSharedService.ColorTextWrapped(
            "To update Sphene, please use the Dalamud Plugin Installer. " +
            "Go to System → Dalamud Plugins → Installed Plugins and look for available updates.", 
            ImGuiColors.DalamudWhite);
        
        ImGui.Spacing();
        
        // Delay warning
        UiSharedService.ColorTextWrapped(
            "Note: Dalamud may take 2-3 minutes to show new updates in its repository. " +
            "If you don't see the update immediately, please wait a moment and check again.", 
            ImGuiColors.DalamudYellow);
        
        ImGui.Spacing();
        ImGui.Separator();
        
        // Buttons
        var buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        
        if (!string.IsNullOrEmpty(_updateInfo.DownloadUrl))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Download, "Open Plugin Installer"))
            {
                try
                {
                    // Open Dalamud Plugin Installer
                    _commandManager.ProcessCommand("/xlplugins");
                    _logger.LogInformation("Opened Dalamud Plugin Installer");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to open Dalamud Plugin Installer");
                }
            }
            
            ImGui.SameLine();
        }
        
        if (_uiShared.IconTextButton(FontAwesomeIcon.Clock, "Remind Me Later"))
        {
            IsOpen = false;
        }
        
        ImGui.Spacing();
        
        // Footer note
        using (var font = ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, FontAwesomeIcon.InfoCircle.ToIconString());
        }
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "This notification will appear again on next login if the update is still available.");
    }
}