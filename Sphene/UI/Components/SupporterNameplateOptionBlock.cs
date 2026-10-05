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

        uiShared.DrawHelpText("Draws the configured icon label text as an overlay next to the Sphene icon.");
    }

    public static void DrawSupporterCustomizationInfo(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterCustomization")
    {
        ImGui.TextWrapped("Customize the supporter nameplate: preset or custom symbols, separate label texts for the nameplate and the Sphene icon, game palette colors for symbol and label, and a free color picker for the icon label.");
        uiShared.DrawHelpText("Open Settings > Supporter to configure all customization options.");
    }

    public static void DrawSupporterStyleSyncInfo(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterStyleSync")
    {
        ImGui.TextWrapped("Your supporter nameplate style is now shared with other Sphene users: they see your nameplate exactly the way you configured it.");
        uiShared.DrawHelpText("Configure your personal supporter look in Settings > Supporter.");
    }
}
