using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.UI;
using Sphene.UI.Panels;

namespace Sphene.UI.Components;

public static class DebugOptionBlock
{
    public static void DrawLogLevelOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "LogLevel")
    {
        uiShared.DrawCombo("Log Level##" + blockId, Enum.GetValues<LogLevel>(), l => l.ToString(), l =>
        {
            configService.Current.LogLevel = l;
            configService.Save();
        }, configService.Current.LogLevel);
        uiShared.DrawHelpText("Controls verbosity of logs written to /xllog and plugin console.");
    }

    public static bool DrawLogNetworkPerformanceMetricsOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "LogNetworkPerformanceMetrics")
    {
        var logPerformance = configService.Current.LogPerformance;
        if (ImGui.Checkbox("Log Network Performance Metrics##" + blockId, ref logPerformance))
        {
            configService.Current.LogPerformance = logPerformance;
            configService.Save();
        }
        uiShared.DrawHelpText("Enabling this can incur a slight performance impact. Extended monitoring is not recommended.");
        return logPerformance;
    }

    public static void DrawPrintNetworkMetricsActions(UiSharedService uiShared, bool logPerformance, Action printMetrics, Action printMetricsLast60, string blockId = "PrintNetworkMetricsActions")
    {
        if (!logPerformance) ImGui.BeginDisabled();
        if (uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Print Network Metrics to /xllog"))
        {
            printMetrics.Invoke();
        }

        if (uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Print Network Metrics (last 60s) to /xllog"))
        {
            printMetricsLast60.Invoke();
        }
        if (!logPerformance) ImGui.EndDisabled();
    }

    public static void DrawDoNotNotifyForModifiedGameFilesOrEnabledLodOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "DoNotNotifyForModifiedGameFilesOrEnabledLod")
    {
        var stopWhining = configService.Current.DebugStopWhining;
        if (ImGui.Checkbox("Do not notify for modified game files or enabled LOD##" + blockId, ref stopWhining))
        {
            configService.Current.DebugStopWhining = stopWhining;
            configService.Save();
        }
        uiShared.DrawHelpText("Having modified game files will still mark your logs with UNSUPPORTED and you will not receive Network support, message shown or not." + UiSharedService.TooltipSeparator
            + "Keeping LOD enabled can lead to more crashes. Use at your own risk.");
    }

    public static void DrawActiveMismatchTrackerFilterOptions(SpheneConfigService configService, UiSharedService uiShared, string blockId = "MismatchTrackerFilters")
    {
        ImGui.PushID(blockId);
        try
        {
            var trackEquipmentPaths = configService.Current.MismatchTrackerTrackEquipmentPaths;
            if (ImGui.Checkbox("Track equipment paths (chara/weapon, chara/equipment, chara/accessory)", ref trackEquipmentPaths))
            {
                configService.Current.MismatchTrackerTrackEquipmentPaths = trackEquipmentPaths;
                configService.Save();
            }

            var trackCompanions = configService.Current.MismatchTrackerTrackMinionMountAndPetPaths;
            if (ImGui.Checkbox("Track minion/mount and pet paths", ref trackCompanions))
            {
                configService.Current.MismatchTrackerTrackMinionMountAndPetPaths = trackCompanions;
                configService.Save();
            }

            var trackPhyb = configService.Current.MismatchTrackerTrackPhybFiles;
            if (ImGui.Checkbox("Track .phyb files", ref trackPhyb))
            {
                configService.Current.MismatchTrackerTrackPhybFiles = trackPhyb;
                configService.Save();
            }

            var trackSkp = configService.Current.MismatchTrackerTrackSkpFiles;
            if (ImGui.Checkbox("Track .skp files", ref trackSkp))
            {
                configService.Current.MismatchTrackerTrackSkpFiles = trackSkp;
                configService.Save();
            }

            var trackPbd = configService.Current.MismatchTrackerTrackPbdFiles;
            if (ImGui.Checkbox("Track .pbd files", ref trackPbd))
            {
                configService.Current.MismatchTrackerTrackPbdFiles = trackPbd;
                configService.Save();
            }

            uiShared.DrawHelpText("These filters affect what is recorded and displayed in the Active Mismatch Tracker.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawOpenAcknowledgmentMonitorAction(UiSharedService uiShared, SpheneMediator mediator, string blockId = "OpenAcknowledgmentMonitor")
    {
        if (uiShared.IconTextButton(FontAwesomeIcon.Desktop, "Open Acknowledgment Monitor"))
        {
            mediator.Publish(new UiToggleMessage(typeof(AcknowledgmentMonitorUI)));
        }
        UiSharedService.AttachToolTip("Opens the Acknowledgment Monitor window for monitoring acknowledgment system status and metrics.");
    }

    public static void DrawOpenStatusDebugAction(UiSharedService uiShared, SpheneMediator mediator, string blockId = "OpenStatusDebug")
    {
        if (uiShared.IconTextButton(FontAwesomeIcon.Bug, "Open Status Debug"))
        {
            mediator.Publish(new UiToggleMessage(typeof(StatusDebugUi)));
        }
        UiSharedService.AttachToolTip("Opens the Status Debug window for connection status monitoring and debugging.");
    }
}
