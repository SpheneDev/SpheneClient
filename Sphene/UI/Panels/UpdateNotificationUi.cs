using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Sphene.Services;
using Sphene.Services.Mediator;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Diagnostics;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public class UpdateNotificationUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiShared;
    private new readonly ILogger<UpdateNotificationUi> _logger;
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

        if (SpheneUI.Layout.BeginScrollArea("update-content", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            DrawUpdateContent();
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawUpdateContent()
    {
        var updateKind = _updateInfo!.IsTestBuildUpdate ? "Testbuild Update" : "Update";
        var latestVersionLabel = _updateInfo.IsTestBuildUpdate ? "Latest Testbuild Version:" : "Latest Version:";

        // Header
        SpheneUI.Text.PageHeader($"{updateKind} Available!",
            _updateInfo.IsTestBuildUpdate
                ? "A newer testbuild version of Sphene is available for download."
                : "A new version of Sphene is available for download.");

        // Version info
        SpheneUI.Layout.SectionCard(() =>
        {
            using (var table = ImRaii.Table("VersionTable", 2, ImGuiTableFlags.None))
            {
                if (table)
                {
                    ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 120);
                    ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthStretch);

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    SpheneUI.Text.Muted("Current Version:");
                    ImGui.TableNextColumn();
                    SpheneUI.Text.Colored(_updateInfo.CurrentVersion.ToString(), ImGuiColors.DalamudYellow);

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    SpheneUI.Text.Muted(latestVersionLabel);
                    ImGui.TableNextColumn();
                    SpheneUI.Text.Colored(_updateInfo.LatestVersion.ToString(), ImGuiColors.HealerGreen);
                }
            }
        });

        // Instructions
        SpheneUI.Text.Wrapped(
            "To update Sphene, please use the Dalamud Plugin Installer. " +
            "Go to System → Dalamud Plugins → Installed Plugins and look for available updates.");

        // Delay warning
        SpheneUI.Layout.Callout(
            "Dalamud may take 2-3 minutes to show new updates in its repository. " +
            "If you don't see the update immediately, please wait a moment and check again.",
            FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudYellow);

        SpheneUI.Layout.Separator();
        
        // Buttons
        
        if (!string.IsNullOrEmpty(_updateInfo.DownloadUrl))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Download, "Open Plugin Installer"))
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
        
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Clock, "Remind Me Later"))
        {
            IsOpen = false;
        }
        
        ImGui.Spacing();

        // Footer note
        SpheneUI.Icons.Icon(FontAwesomeIcon.InfoCircle, SpheneUI.Colors.TextMuted);
        ImGui.SameLine();
        SpheneUI.Text.Muted("This notification will appear again on next login if the update is still available.");
    }
}
