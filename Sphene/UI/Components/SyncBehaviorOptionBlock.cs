using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;

namespace Sphene.UI.Components;

public static class SyncBehaviorOptionBlock
{
    public static void DrawDisableRedraws(SpheneConfigService configService, UiSharedService uiShared, string blockId = "DisableRedraws")
    {
        ImGui.PushID(blockId);
        try
        {
            var disableRedraws = configService.Current.DisableRedraws;
            if (ImGui.Checkbox("Disable redraws globally", ref disableRedraws))
            {
                configService.Current.DisableRedraws = disableRedraws;
                configService.Save();
            }

            uiShared.DrawHelpText("When enabled, Sphene suppresses most Penumbra redraw calls. Exceptions: minions and pets are still redrawn, and each character is force-redrawn once when you first encounter them after plugin start.");
            if (disableRedraws)
            {
                UiSharedService.ColorTextWrapped("Warning: (Experimental) Most redraws are disabled. Visual updates can be delayed until natural game refreshes happen, except minion/pet redraws and first-encounter redraw.", ImGuiColors.DalamudYellow);
            }
            else
            {
                UiSharedService.ColorTextWrapped("Redraws are active. Sphene will trigger a redraw after visual changes.", ImGuiColors.DalamudGrey);
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawIncomingSyncWithoutRedraw(SpheneConfigService configService, UiSharedService uiShared, string blockId = "IncomingSyncWithoutRedraw")
    {
        ImGui.PushID(blockId);
        try
        {
            var dutyCombatNoRedraw = configService.Current.EnableDutyCombatSyncWithoutRedraw;
            if (ImGui.Checkbox("Allow sync in duties/combat without redraw", ref dutyCombatNoRedraw))
            {
                configService.Current.EnableDutyCombatSyncWithoutRedraw = dutyCombatNoRedraw;
                configService.Save();
            }

            uiShared.DrawHelpText("When enabled, incoming sync changes are applied during duties or combat/performance without triggering redraw. This helps avoid temporary invisibility caused by redraw. If you notice performance issues, disable this option again.");
            if (dutyCombatNoRedraw)
            {
                UiSharedService.ColorTextWrapped("Tip: This option is active. If you notice performance issues or crashes during combat, disable this option again.", ImGuiColors.ParsedGreen);
            }
            else
            {
                UiSharedService.ColorTextWrapped("Tip: This option is currently disabled. Enable it only if you want duty/combat sync without redraw.", ImGuiColors.DalamudGrey);
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawOutgoingSyncBatching(SpheneConfigService configService, UiSharedService uiShared, float sliderWidth = 240f, string blockId = "OutgoingSyncBatching")
    {
        ImGui.PushID(blockId);
        try
        {
            var outgoingBatching = configService.Current.EnableDutyCombatOutgoingSyncBatching;
            if (ImGui.Checkbox("Batch outgoing sync updates in duties/combat", ref outgoingBatching))
            {
                configService.Current.EnableDutyCombatOutgoingSyncBatching = outgoingBatching;
                configService.Save();
            }
            uiShared.DrawHelpText("When enabled, outgoing sync updates are collected for a configurable window during duty/combat, then sent as one update burst.");

            ImGui.Indent();
            var batchSeconds = configService.Current.DutyCombatOutgoingSyncBatchSeconds;
            using (ImRaii.Disabled(!outgoingBatching))
            {
                ImGui.SetNextItemWidth(sliderWidth);
                if (ImGui.SliderInt("Outgoing batch window (seconds)", ref batchSeconds, 1, 60))
                {
                    configService.Current.DutyCombatOutgoingSyncBatchSeconds = batchSeconds;
                    configService.Save();
                }
            }
            uiShared.DrawHelpText("Recommended default is 10 seconds. Higher values reduce update frequency but increase sync latency.");
            ImGui.Unindent();

            if (outgoingBatching)
            {
                UiSharedService.ColorTextWrapped("Tip: Outgoing batching is active. If you notice performance issues or crashes during combat, disable this option.", ImGuiColors.ParsedGreen);
            }
            else
            {
                UiSharedService.ColorTextWrapped("Tip: Outgoing batching is currently disabled. Enable it if you want fewer sync sends during combat to reduce performance impact.", ImGuiColors.DalamudGrey);
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawFilterCharacterLegacyShpkInOutgoingCharacterData(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "FilterCharacterLegacyShpk")
    {
        ImGui.PushID(blockId);
        try
        {
            var enabled = configService.Current.FilterCharacterLegacyShpkInOutgoingCharacterData;
            if (ImGui.Checkbox("Filter characterlegacy.shpk in sync data (experimental)", ref enabled))
            {
                configService.Current.FilterCharacterLegacyShpkInOutgoingCharacterData = enabled;
                configService.Save();
                mediator.Publish(new PenumbraModSettingChangedMessage());
            }

            uiShared.DrawHelpText("When enabled, incoming and outgoing character sync filters paths that reference characterlegacy.shpk. This file may be related to shadow bugs. If you notice rendering issues while testing this experimental behavior, disable this option.");
            if (enabled)
            {
                UiSharedService.ColorTextWrapped("Experimental filter is active. characterlegacy.shpk entries are excluded from outgoing push and incoming apply/download paths.", ImGuiColors.ParsedGreen);
            }
            else
            {
                UiSharedService.ColorTextWrapped("Experimental filter is disabled. characterlegacy.shpk entries are no longer filtered.", ImGuiColors.DalamudGrey);
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
