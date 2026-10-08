using Dalamud.Bindings.ImGui;
using Sphene.Services;
using Sphene.SpheneConfiguration;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public static class SupporterNameplateOptionBlock
{
    public static void DrawShowSupporterNameplateOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowSupporterNameplate")
    {
        var show = configService.Current.ShowSupporterNameplate;
        if (SpheneUI.Controls.Toggle("Show Supporter Nameplate Indicators##" + blockId, ref show))
        {
            configService.Current.ShowSupporterNameplate = show;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("Display a symbol, text or the Sphene icon next to the nameplates of Sphene supporters.");
    }

    public static void DrawSupporterIconOverlayOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterIconOverlay")
    {
        var enabled = configService.Current.SupporterIconEnabled;
        if (SpheneUI.Controls.Toggle("Show Sphene icon above supporter nameplates##" + blockId, ref enabled))
        {
            configService.Current.SupporterIconEnabled = enabled;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("Draws the Sphene icon above the nameplate of every supporter.");
    }

    public static void DrawSupporterIconLabelOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterIconLabel")
    {
        var enabled = configService.Current.SupporterIconLabelEnabled;
        if (SpheneUI.Controls.Toggle("Show label text next to the Sphene icon##" + blockId, ref enabled))
        {
            configService.Current.SupporterIconLabelEnabled = enabled;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("Draws the configured icon label text as an overlay next to the Sphene icon.");
    }

    public static void DrawSupporterCustomizationInfo(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterCustomization")
    {
        SpheneUI.Text.Wrapped("Customize the supporter nameplate: preset or custom symbols, separate label texts for the nameplate and the Sphene icon, game palette colors for symbol and label, optional glow colors for both, and a free color picker for the icon label.");
        SpheneUI.Controls.HelpText("Open Settings > Supporter to configure all customization options.");
    }

    public static void DrawSupporterGlowColorsInfo(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterGlowColors")
    {
        SpheneUI.Text.Wrapped("Supporter nameplate symbol and label can now have an edge glow color — a colored outline that sits behind the glyph like a background halo.");
        SpheneUI.Controls.HelpText("Pick glow colors in Settings > Supporter, or choose None to keep the default.");
    }

    public static void DrawSupporterStyleSyncInfo(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterStyleSync")
    {
        SpheneUI.Text.Wrapped("Your supporter nameplate style is now shared with other Sphene users: they see your nameplate exactly the way you configured it.");
        SpheneUI.Controls.HelpText("Configure your personal supporter look in Settings > Supporter.");
    }

    public static void DrawSupporterServerIconsInfo(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterServerIcons")
    {
        SpheneUI.Text.Wrapped("Servers can now provide custom supporter icons, including animated ones. Pick your favorite in the supporter settings and other users will see it on your nameplate.");
        SpheneUI.Controls.HelpText("Open Settings > Supporter to choose an icon provided by your server.");
    }
}
