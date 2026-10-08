using Dalamud.Bindings.ImGui;
using Sphene.SpheneConfiguration;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public static class PerformanceOptionBlock
{
    public static bool DrawShowPerformanceIndicatorOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, string blockId = "ShowPerformanceIndicator")
    {
        ImGui.PushID(blockId);
        try
        {
            var showPerformanceIndicator = performanceConfigService.Current.ShowPerformanceIndicator;
            if (SpheneUI.Controls.Toggle("Show performance indicator", ref showPerformanceIndicator))
            {
                performanceConfigService.Current.ShowPerformanceIndicator = showPerformanceIndicator;
                performanceConfigService.Save();
            }

            SpheneUI.Controls.HelpText("Will show a performance indicator when players exceed defined thresholds in Sphenes UI." + Environment.NewLine + "Will use warning thresholds.");
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
            if (SpheneUI.Controls.Toggle("Warn on loading in players exceeding performance thresholds", ref warnOnExceedingThresholds))
            {
                performanceConfigService.Current.WarnOnExceedingThresholds = warnOnExceedingThresholds;
                performanceConfigService.Save();
            }

            SpheneUI.Controls.HelpText("Sphene will print a warning in chat once per session of meeting those people. Will not warn on players with preferred permissions.");
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
            if (SpheneUI.Controls.Toggle("Warn/Indicate also on players with preferred permissions", ref warnOnPref))
            {
                performanceConfigService.Current.WarnOnPreferredPermissionsExceedingThresholds = warnOnPref;
                performanceConfigService.Save();
            }

            SpheneUI.Controls.HelpText("Sphene will also print warnings and show performance indicator for players where you enabled preferred permissions. If warning in general is disabled, this will not produce any warnings.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawWarningVramThresholdOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, string blockId = "WarningVramThreshold")
    {
        ImGui.PushID(blockId);
        try
        {
            var vram = performanceConfigService.Current.VRAMSizeWarningThresholdMiB;
            if (SpheneUI.Controls.InputInt("Warning VRAM threshold", ref vram))
            {
                performanceConfigService.Current.VRAMSizeWarningThresholdMiB = vram;
                performanceConfigService.Save();
            }
            ImGui.SameLine();
            SpheneUI.Text.Plain("(MiB)");
            SpheneUI.Controls.HelpText("Limit in MiB of approximate VRAM usage to trigger warning or performance indicator on UI." + SpheneUI.TooltipSeparator
                + "Default: 375 MiB");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawWarningTriangleThresholdOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, string blockId = "WarningTriangleThreshold")
    {
        ImGui.PushID(blockId);
        try
        {
            var tris = performanceConfigService.Current.TrisWarningThresholdThousands;
            if (SpheneUI.Controls.InputInt("Warning Triangle threshold", ref tris))
            {
                performanceConfigService.Current.TrisWarningThresholdThousands = tris;
                performanceConfigService.Save();
            }
            ImGui.SameLine();
            SpheneUI.Text.Plain("(thousand triangles)");
            SpheneUI.Controls.HelpText("Limit in approximate used triangles from mods to trigger warning or performance indicator on UI." + SpheneUI.TooltipSeparator
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
            if (SpheneUI.Controls.Toggle("Automatically pause players exceeding thresholds", ref autoPause))
            {
                performanceConfigService.Current.AutoPausePlayersExceedingThresholds = autoPause;
                performanceConfigService.Save();
            }

            SpheneUI.Controls.HelpText("When enabled, it will automatically pause all players without preferred permissions that exceed the thresholds defined below." + Environment.NewLine
                + "Will print a warning in chat when a player got paused automatically."
                + SpheneUI.TooltipSeparator + "Warning: this will not automatically unpause those people again, you will have to do this manually.");
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
            if (SpheneUI.Controls.Toggle("Automatically pause also players with preferred permissions", ref autoPauseEveryone))
            {
                performanceConfigService.Current.AutoPausePlayersWithPreferredPermissionsExceedingThresholds = autoPauseEveryone;
                performanceConfigService.Save();
            }

            SpheneUI.Controls.HelpText("When enabled, will automatically pause all players regardless of preferred permissions that exceed thresholds defined below." + SpheneUI.TooltipSeparator +
                "Warning: this will not automatically unpause those people again, you will have to do this manually.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawAutoPauseVramThresholdOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, string blockId = "AutoPauseVramThreshold")
    {
        ImGui.PushID(blockId);
        try
        {
            var vramAuto = performanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB;
            if (SpheneUI.Controls.InputInt("Auto Pause VRAM threshold", ref vramAuto))
            {
                performanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB = vramAuto;
                performanceConfigService.Save();
            }
            ImGui.SameLine();
            SpheneUI.Text.Plain("(MiB)");
            SpheneUI.Controls.HelpText("When a loading in player and their VRAM usage exceeds this amount, automatically pauses the synced player." + SpheneUI.TooltipSeparator
                + "Default: 550 MiB");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawAutoPauseTriangleThresholdOption(PlayerPerformanceConfigService performanceConfigService, UiSharedService uiShared, string blockId = "AutoPauseTriangleThreshold")
    {
        ImGui.PushID(blockId);
        try
        {
            var trisAuto = performanceConfigService.Current.TrisAutoPauseThresholdThousands;
            if (SpheneUI.Controls.InputInt("Auto Pause Triangle threshold", ref trisAuto))
            {
                performanceConfigService.Current.TrisAutoPauseThresholdThousands = trisAuto;
                performanceConfigService.Save();
            }
            ImGui.SameLine();
            SpheneUI.Text.Plain("(thousand triangles)");
            SpheneUI.Controls.HelpText("When a loading in player and their triangle count exceeds this amount, automatically pauses the synced player." + SpheneUI.TooltipSeparator
                + "Default: 250 thousand");
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
