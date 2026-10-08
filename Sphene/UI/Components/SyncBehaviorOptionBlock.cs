using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public static class SyncBehaviorOptionBlock
{
    public static void DrawIncomingSyncWithoutRedraw(SpheneConfigService configService, UiSharedService uiShared, string blockId = "IncomingSyncWithoutRedraw")
    {
        ImGui.PushID(blockId);
        try
        {
            var dutyCombatNoRedraw = configService.Current.EnableDutyCombatSyncWithoutRedraw;
            if (SpheneUI.Controls.Toggle("Allow sync in duties/combat without redraw", ref dutyCombatNoRedraw))
            {
                configService.Current.EnableDutyCombatSyncWithoutRedraw = dutyCombatNoRedraw;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("When enabled, incoming sync changes are applied during duties or combat/performance without triggering redraw. This helps avoid temporary invisibility caused by redraw. If you notice performance issues, disable this option again.");
            if (dutyCombatNoRedraw)
            {
                SpheneUI.Text.ColoredWrapped("Tip: This option is active. If you notice performance issues or crashes during combat, disable this option again.", ImGuiColors.ParsedGreen);
            }
            else
            {
                SpheneUI.Text.ColoredWrapped("Tip: This option is currently disabled. Enable it only if you want duty/combat sync without redraw.", ImGuiColors.DalamudGrey);
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawOutgoingSyncBatching(SpheneConfigService configService, UiSharedService uiShared, string blockId = "OutgoingSyncBatching")
    {
        ImGui.PushID(blockId);
        try
        {
            var outgoingBatching = configService.Current.EnableDutyCombatOutgoingSyncBatching;
            if (SpheneUI.Controls.Toggle("Batch outgoing sync updates in duties/combat", ref outgoingBatching))
            {
                configService.Current.EnableDutyCombatOutgoingSyncBatching = outgoingBatching;
                configService.Save();
            }
            SpheneUI.Controls.HelpText("When enabled, outgoing sync updates are collected for a configurable window during duty/combat, then sent as one update burst.");

            ImGui.Indent();
            var batchSeconds = configService.Current.DutyCombatOutgoingSyncBatchSeconds;
            using (ImRaii.Disabled(!outgoingBatching))
            {
                if (SpheneUI.Controls.SliderInt("Outgoing batch window (seconds)", ref batchSeconds, 1, 60))
                {
                    configService.Current.DutyCombatOutgoingSyncBatchSeconds = batchSeconds;
                    configService.Save();
                }
            }
            SpheneUI.Controls.HelpText("Recommended default is 10 seconds. Higher values reduce update frequency but increase sync latency.");
            ImGui.Unindent();

            if (outgoingBatching)
            {
                SpheneUI.Text.ColoredWrapped("Tip: Outgoing batching is active. If you notice performance issues or crashes during combat, disable this option.", ImGuiColors.ParsedGreen);
            }
            else
            {
                SpheneUI.Text.ColoredWrapped("Tip: Outgoing batching is currently disabled. Enable it if you want fewer sync sends during combat to reduce performance impact.", ImGuiColors.DalamudGrey);
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
            if (SpheneUI.Controls.Toggle("Filter characterlegacy.shpk in sync data", ref enabled))
            {
                configService.Current.FilterCharacterLegacyShpkInOutgoingCharacterData = enabled;
                configService.Save();
                mediator.Publish(new PenumbraModSettingChangedMessage());
            }

            SpheneUI.Controls.HelpText("When enabled, incoming and outgoing character sync filters paths that reference characterlegacy.shpk. This file may be related to shadow bugs. If you notice rendering issues while using this feature, disable this option.");
            if (enabled)
            {
                SpheneUI.Text.ColoredWrapped("Filter is active. characterlegacy.shpk entries are excluded from outgoing push and incoming apply/download paths.", ImGuiColors.ParsedGreen);
            }
            else
            {
                SpheneUI.Text.ColoredWrapped("Filter is disabled. characterlegacy.shpk entries are no longer filtered.", ImGuiColors.DalamudGrey);
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
