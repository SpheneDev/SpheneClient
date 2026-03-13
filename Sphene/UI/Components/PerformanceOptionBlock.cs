using Dalamud.Bindings.ImGui;
using Sphene.SpheneConfiguration;

namespace Sphene.UI.Components;

public static class PerformanceOptionBlock
{
    public static bool DrawShowPerformanceIndicatorOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, string blockId = "ShowPerformanceIndicator")
    {
        ImGui.PushID(blockId);
        try
        {
            var showPerformanceIndicator = performanceConfigService.Current.ShowPerformanceIndicator;
            if (ImGui.Checkbox("Show performance indicator", ref showPerformanceIndicator))
            {
                performanceConfigService.Current.ShowPerformanceIndicator = showPerformanceIndicator;
                performanceConfigService.Save();
            }

            uiShared.DrawHelpText("Will show a performance indicator when players exceed defined thresholds in Sphenes UI." + Environment.NewLine + "Will use warning thresholds.");
            return showPerformanceIndicator;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static bool DrawWarnOnLoadingInPlayersExceedingThresholdsOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, string blockId = "WarnOnLoadingInPlayersExceedingThresholds")
    {
        ImGui.PushID(blockId);
        try
        {
            var warnOnExceedingThresholds = performanceConfigService.Current.WarnOnExceedingThresholds;
            if (ImGui.Checkbox("Warn on loading in players exceeding performance thresholds", ref warnOnExceedingThresholds))
            {
                performanceConfigService.Current.WarnOnExceedingThresholds = warnOnExceedingThresholds;
                performanceConfigService.Save();
            }

            uiShared.DrawHelpText("Sphene will print a warning in chat once per session of meeting those people. Will not warn on players with preferred permissions.");
            return warnOnExceedingThresholds;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawWarnIndicateAlsoOnPreferredPermissionsOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, string blockId = "WarnIndicateAlsoOnPreferredPermissions")
    {
        ImGui.PushID(blockId);
        try
        {
            var warnOnPref = performanceConfigService.Current.WarnOnPreferredPermissionsExceedingThresholds;
            if (ImGui.Checkbox("Warn/Indicate also on players with preferred permissions", ref warnOnPref))
            {
                performanceConfigService.Current.WarnOnPreferredPermissionsExceedingThresholds = warnOnPref;
                performanceConfigService.Save();
            }

            uiShared.DrawHelpText("Sphene will also print warnings and show performance indicator for players where you enabled preferred permissions. If warning in general is disabled, this will not produce any warnings.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawWarningVramThresholdOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, float itemWidth, string blockId = "WarningVramThreshold")
    {
        ImGui.PushID(blockId);
        try
        {
            var vram = performanceConfigService.Current.VRAMSizeWarningThresholdMiB;
            ImGui.SetNextItemWidth(itemWidth);
            if (ImGui.InputInt("Warning VRAM threshold", ref vram))
            {
                performanceConfigService.Current.VRAMSizeWarningThresholdMiB = vram;
                performanceConfigService.Save();
            }
            ImGui.SameLine();
            ImGui.Text("(MiB)");
            uiShared.DrawHelpText("Limit in MiB of approximate VRAM usage to trigger warning or performance indicator on UI." + UiSharedService.TooltipSeparator
                + "Default: 375 MiB");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawWarningTriangleThresholdOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, float itemWidth, string blockId = "WarningTriangleThreshold")
    {
        ImGui.PushID(blockId);
        try
        {
            var tris = performanceConfigService.Current.TrisWarningThresholdThousands;
            ImGui.SetNextItemWidth(itemWidth);
            if (ImGui.InputInt("Warning Triangle threshold", ref tris))
            {
                performanceConfigService.Current.TrisWarningThresholdThousands = tris;
                performanceConfigService.Save();
            }
            ImGui.SameLine();
            ImGui.Text("(thousand triangles)");
            uiShared.DrawHelpText("Limit in approximate used triangles from mods to trigger warning or performance indicator on UI." + UiSharedService.TooltipSeparator
                + "Default: 165 thousand");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static bool DrawAutomaticallyPausePlayersExceedingThresholdsOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, string blockId = "AutomaticallyPausePlayersExceedingThresholds")
    {
        ImGui.PushID(blockId);
        try
        {
            var autoPause = performanceConfigService.Current.AutoPausePlayersExceedingThresholds;
            if (ImGui.Checkbox("Automatically pause players exceeding thresholds", ref autoPause))
            {
                performanceConfigService.Current.AutoPausePlayersExceedingThresholds = autoPause;
                performanceConfigService.Save();
            }

            uiShared.DrawHelpText("When enabled, it will automatically pause all players without preferred permissions that exceed the thresholds defined below." + Environment.NewLine
                + "Will print a warning in chat when a player got paused automatically."
                + UiSharedService.TooltipSeparator + "Warning: this will not automatically unpause those people again, you will have to do this manually.");
            return autoPause;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawAutomaticallyPauseAlsoPreferredPermissionsOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, string blockId = "AutomaticallyPauseAlsoPreferredPermissions")
    {
        ImGui.PushID(blockId);
        try
        {
            var autoPauseEveryone = performanceConfigService.Current.AutoPausePlayersWithPreferredPermissionsExceedingThresholds;
            if (ImGui.Checkbox("Automatically pause also players with preferred permissions", ref autoPauseEveryone))
            {
                performanceConfigService.Current.AutoPausePlayersWithPreferredPermissionsExceedingThresholds = autoPauseEveryone;
                performanceConfigService.Save();
            }

            uiShared.DrawHelpText("When enabled, will automatically pause all players regardless of preferred permissions that exceed thresholds defined below." + UiSharedService.TooltipSeparator +
                "Warning: this will not automatically unpause those people again, you will have to do this manually.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawAutoPauseVramThresholdOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, float itemWidth, string blockId = "AutoPauseVramThreshold")
    {
        ImGui.PushID(blockId);
        try
        {
            var vramAuto = performanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB;
            ImGui.SetNextItemWidth(itemWidth);
            if (ImGui.InputInt("Auto Pause VRAM threshold", ref vramAuto))
            {
                performanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB = vramAuto;
                performanceConfigService.Save();
            }
            ImGui.SameLine();
            ImGui.Text("(MiB)");
            uiShared.DrawHelpText("When a loading in player and their VRAM usage exceeds this amount, automatically pauses the synced player." + UiSharedService.TooltipSeparator
                + "Default: 550 MiB");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawAutoPauseTriangleThresholdOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, float itemWidth, string blockId = "AutoPauseTriangleThreshold")
    {
        ImGui.PushID(blockId);
        try
        {
            var trisAuto = performanceConfigService.Current.TrisAutoPauseThresholdThousands;
            ImGui.SetNextItemWidth(itemWidth);
            if (ImGui.InputInt("Auto Pause Triangle threshold", ref trisAuto))
            {
                performanceConfigService.Current.TrisAutoPauseThresholdThousands = trisAuto;
                performanceConfigService.Save();
            }
            ImGui.SameLine();
            ImGui.Text("(thousand triangles)");
            uiShared.DrawHelpText("When a loading in player and their triangle count exceeds this amount, automatically pauses the synced player." + UiSharedService.TooltipSeparator
                + "Default: 250 thousand");
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
