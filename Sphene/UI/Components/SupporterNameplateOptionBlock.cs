using Dalamud.Bindings.ImGui;
using Sphene.Services;
using Sphene.SpheneConfiguration;

namespace Sphene.UI.Components;

public static class SupporterNameplateOptionBlock
{
    public static void DrawShowSupporterNameplateOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowSupporterNameplate")
    {
        var show = configService.Current.ShowSupporterNameplate;
        if (ImGui.Checkbox("Show Supporter Nameplate Indicators##" + blockId, ref show))
        {
            configService.Current.ShowSupporterNameplate = show;
            configService.Save();
        }

        uiShared.DrawHelpText("Display a symbol, text or the Sphene icon next to the nameplates of Sphene supporters.");
    }

    public static void DrawSupporterIconOverlayOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterIconOverlay")
    {
        var enabled = configService.Current.SupporterIconEnabled;
        if (ImGui.Checkbox("Show Sphene icon above supporter nameplates##" + blockId, ref enabled))
        {
            configService.Current.SupporterIconEnabled = enabled;
            configService.Save();
        }

        uiShared.DrawHelpText("Draws the Sphene icon above the nameplate of every supporter.");
    }

    public static void DrawSupporterIconLabelOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterIconLabel")
    {
        var enabled = configService.Current.SupporterIconLabelEnabled;
        if (ImGui.Checkbox("Show label text next to the Sphene icon##" + blockId, ref enabled))
        {
            configService.Current.SupporterIconLabelEnabled = enabled;
            configService.Save();
        }

        uiShared.DrawHelpText("Draws the configured Label Text as an overlay next to the Sphene icon.");
    }
}
