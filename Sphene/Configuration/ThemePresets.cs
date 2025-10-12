using System.Numerics;
using Sphene.UI;

namespace Sphene.Configuration;

public static class ThemePresets
{
    public static Dictionary<string, ThemeConfiguration> BuiltInThemes { get; } = new()
    {
        ["Default Sphene"] = CreateDefaultSpheneTheme(),
        ["Minimal"] = CreateMinimalTheme()
    };

    private static ThemeConfiguration CreateDefaultSpheneTheme()
    {
        return new ThemeConfiguration
        {
            WindowRounding = 3.6f,
            ChildRounding = 4.2f,
            PopupRounding = 4.0f,
            FrameRounding = 3.3f,
            ScrollbarRounding = 3.2f,
            GrabRounding = 2.1f,
            TabRounding = 4.6f,
            
            CompactWindowRounding = 15.0f,
            CompactChildRounding = 15.0f,
            CompactPopupRounding = 4.0f,
            CompactFrameRounding = 3.9f,
            CompactScrollbarRounding = 4.0f,
            CompactGrabRounding = 12.0f,
            CompactTabRounding = 12.0f,
            CompactHeaderRounding = 8.1f,
            
            WindowPadding = new Vector2(5.5f, 5.4f),
            FramePadding = new Vector2(3.6f, 1.4f),
            ItemSpacing = new Vector2(4.3f, 5.4f),
            ItemInnerSpacing = new Vector2(3.6f, 3.6f),
            IndentSpacing = 21.0f,
            
            CompactWindowPadding = new Vector2(10.6f, 8.9f),
            CompactFramePadding = new Vector2(6.8f, 3.5f),
            CompactItemSpacing = new Vector2(5.3f, 6.0f),
            CompactItemInnerSpacing = new Vector2(5.9f, 4.0f),
            CompactCellPadding = new Vector2(6.0f, 3.0f),
            CompactChildPadding = new Vector2(20.0f, 11.5f),
            CompactIndentSpacing = 23.4f,
            CompactScrollbarSize = 16.9f,
            CompactGrabMinSize = 14.0f,
            
            CompactButtonTextAlign = new Vector2(0.5f, 0.5f),
            CompactSelectableTextAlign = new Vector2(0.0f, 0.0f),
            
            CompactWindowBorderSize = 0.1f,
            CompactChildBorderSize = 0.0f,
            CompactPopupBorderSize = 0.0f,
            CompactFrameBorderSize = 0.1f,
            CompactTooltipRounding = 4.0f,
            CompactTooltipBorderSize = 0.1f,
            CompactContextMenuRounding = 4.0f,
            CompactContextMenuBorderSize = 0.1f,
            
            WindowBorderSize = 0.0f,
            ChildBorderSize = 0.1f,
            PopupBorderSize = 0.1f,
            FrameBorderSize = 0.1f,
            
            ScrollbarSize = 13.1f,
            GrabMinSize = 10.7f,
            
            PrimaryDark = new Vector4(0.05f, 0.10f, 0.20f, 0.90f),
            SecondaryDark = new Vector4(0.10f, 0.15f, 0.25f, 0.90f),
            AccentBlue = new Vector4(0.10f, 0.30f, 0.70f, 1.00f),
            AccentCyan = new Vector4(0.20f, 0.50f, 0.80f, 1.00f),
            TextPrimary = new Vector4(0.95f, 0.95f, 1.00f, 1.00f),
            TextSecondary = new Vector4(0.75f, 0.80f, 0.90f, 1.00f),
            Border = new Vector4(0.20f, 0.30f, 0.50f, 1.00f),
            Hover = new Vector4(0.15f, 0.35f, 0.75f, 1.00f),
            Active = new Vector4(0.20f, 0.40f, 0.80f, 1.00f),
            HeaderBg = new Vector4(0.10f, 0.30f, 0.70f, 0.40f),
            
            WindowBg = new Vector4(0.06f, 0.06f, 0.07f, 0.94f),
            ChildBg = new Vector4(0.00f, 0.00f, 0.00f, 0.00f),
            PopupBg = new Vector4(0.08f, 0.08f, 0.08f, 0.94f),
            BorderShadow = new Vector4(0.00f, 0.00f, 0.00f, 0.00f),
            TitleBg = new Vector4(0.04f, 0.04f, 0.04f, 1.00f),
            TitleBgActive = new Vector4(0.16f, 0.29f, 0.48f, 1.00f),
            TitleBgCollapsed = new Vector4(0.00f, 0.00f, 0.00f, 0.51f),
            ModalWindowDimBg = new Vector4(0.80f, 0.80f, 0.80f, 0.35f),
            
            FrameBg = new Vector4(0.16f, 0.29f, 0.48f, 0.54f),
            FrameBgHovered = new Vector4(0.26f, 0.59f, 0.98f, 0.40f),
            FrameBgActive = new Vector4(0.26f, 0.59f, 0.98f, 0.67f),
            CheckMark = new Vector4(0.26f, 0.59f, 0.98f, 1.00f),
            ResizeGrip = new Vector4(0.26f, 0.59f, 0.98f, 0.20f),
            ResizeGripHovered = new Vector4(0.26f, 0.59f, 0.98f, 0.67f),
            ResizeGripActive = new Vector4(0.26f, 0.59f, 0.98f, 0.95f),
            
            Button = new Vector4(0.26f, 0.59f, 0.98f, 0.40f),
            ButtonHovered = new Vector4(0.26f, 0.59f, 0.98f, 1.00f),
            ButtonActive = new Vector4(0.06f, 0.53f, 0.98f, 1.00f),
            Header = new Vector4(0.26f, 0.59f, 0.98f, 0.31f),
            HeaderHovered = new Vector4(0.26f, 0.59f, 0.98f, 0.80f),
            HeaderActive = new Vector4(0.26f, 0.59f, 0.98f, 1.00f),
            Separator = new Vector4(0.43f, 0.43f, 0.50f, 0.50f),
            SeparatorHovered = new Vector4(0.10f, 0.40f, 0.75f, 0.78f),
            SeparatorActive = new Vector4(0.10f, 0.40f, 0.75f, 1.00f),
            
            MenuBarBg = new Vector4(0.14f, 0.14f, 0.14f, 1.00f),
            NavHighlight = new Vector4(0.26f, 0.59f, 0.98f, 1.00f),
            NavWindowingHighlight = new Vector4(1.00f, 1.00f, 1.00f, 0.70f),
            NavWindowingDimBg = new Vector4(0.80f, 0.80f, 0.80f, 0.20f),
            DragDropTarget = new Vector4(1.00f, 1.00f, 0.00f, 0.90f),
            
            ScrollbarBg = new Vector4(0.02f, 0.02f, 0.02f, 0.0f),
            ScrollbarGrab = new Vector4(0.31f, 0.31f, 0.31f, 1.00f),
            ScrollbarGrabHovered = new Vector4(0.41f, 0.41f, 0.41f, 1.00f),
            ScrollbarGrabActive = new Vector4(0.51f, 0.51f, 0.51f, 1.00f),
            SliderGrab = new Vector4(0.24f, 0.52f, 0.88f, 1.00f),
            SliderGrabActive = new Vector4(0.26f, 0.59f, 0.98f, 1.00f),
            
            TableHeaderBg = new Vector4(0.19f, 0.19f, 0.20f, 1.00f),
            TableBorderStrong = new Vector4(0.31f, 0.31f, 0.35f, 1.00f),
            TableBorderLight = new Vector4(0.23f, 0.23f, 0.25f, 1.00f),
            TableRowBg = new Vector4(0.00f, 0.00f, 0.00f, 0.00f),
            TableRowBgAlt = new Vector4(1.00f, 1.00f, 1.00f, 0.06f),
            Tab = new Vector4(0.18f, 0.35f, 0.58f, 0.86f),
            TabHovered = new Vector4(0.26f, 0.59f, 0.98f, 0.80f),
            TabActive = new Vector4(0.20f, 0.41f, 0.68f, 1.00f),
            TabUnfocused = new Vector4(0.07f, 0.10f, 0.15f, 0.97f),
            TabUnfocusedActive = new Vector4(0.14f, 0.26f, 0.42f, 1.00f),
            
            TextDisabled = new Vector4(0.50f, 0.50f, 0.50f, 1.00f),
            TextSelectedBg = new Vector4(0.26f, 0.59f, 0.98f, 0.35f),
            
            CompactWindowBg = new Vector4(0.03f, 0.03f, 0.16f, 0.90f),
            CompactChildBg = new Vector4(0.00f, 0.00f, 0.00f, 0.00f),
            CompactPopupBg = new Vector4(0.10f, 0.10f, 0.15f, 0.95f),
            CompactTitleBg = new Vector4(0.06f, 0.06f, 0.10f, 1.00f),
            CompactTitleBgActive = new Vector4(0.10f, 0.20f, 0.40f, 1.00f),
            CompactFrameBg = new Vector4(0.12f, 0.20f, 0.35f, 0.60f),
            CompactButton = new Vector4(0.10f, 0.15f, 0.25f, 0.80f),
            CompactButtonHovered = new Vector4(0.15f, 0.25f, 0.40f, 0.90f),
            CompactButtonActive = new Vector4(0.20f, 0.35f, 0.55f, 1.00f),
            CompactHeaderBg = new Vector4(0.08f, 0.15f, 0.30f, 0.80f),
            CompactBorder = new Vector4(0.30f, 0.50f, 0.80f, 0.80f),
            CompactText = new Vector4(0.90f, 0.90f, 0.90f, 1.00f),
            CompactTextSecondary = new Vector4(0.70f, 0.70f, 0.70f, 1.00f),
            CompactAccent = new Vector4(0.20f, 0.40f, 0.80f, 1.00f),
            CompactHover = new Vector4(0.30f, 0.50f, 0.90f, 0.80f),
            CompactActive = new Vector4(0.40f, 0.60f, 1.00f, 0.90f),
            CompactHeaderText = new Vector4(0.80f, 0.60f, 0.20f, 1.00f),
            
            CompactUidColor = new Vector4(0.11f, 1.00f, 0.00f, 1.00f),
            CompactServerStatusConnected = new Vector4(0.33f, 0.76f, 0.47f, 1.00f),
            CompactServerStatusWarning = new Vector4(1.00f, 0.80f, 0.20f, 1.00f),
            CompactServerStatusError = new Vector4(0.80f, 0.20f, 0.20f, 1.00f),
            
            CompactActionButton = new Vector4(0.15f, 0.30f, 0.60f, 0.80f),
            CompactActionButtonHovered = new Vector4(0.20f, 0.40f, 0.70f, 0.90f),
            CompactActionButtonActive = new Vector4(0.25f, 0.50f, 0.80f, 1.00f),
            
            CompactSyncshellButton = new Vector4(0.30f, 0.50f, 0.90f, 0.40f),
            CompactSyncshellButtonHovered = new Vector4(0.40f, 0.60f, 1.00f, 0.60f),
            CompactSyncshellButtonActive = new Vector4(0.50f, 0.70f, 1.00f, 0.80f),
            
            CompactPanelTitleText = new Vector4(0.80f, 0.60f, 0.20f, 1.00f),
            CompactConnectedText = new Vector4(0.34f, 0.87f, 0.34f, 1.00f),
            CompactAllSyncshellsText = new Vector4(0.84f, 0.43f, 0.70f, 1.00f),
            CompactOfflinePausedText = new Vector4(0.80f, 0.40f, 0.40f, 1.00f),
            CompactOfflineSyncshellText = new Vector4(0.80f, 0.40f, 0.40f, 1.00f),
            CompactVisibleText = new Vector4(0.60f, 0.90f, 0.60f, 1.00f),
            CompactPairsText = new Vector4(0.55f, 0.55f, 1.00f, 1.00f),
            
            CompactShowImGuiHeader = false
        };
    }

    private static ThemeConfiguration CreateMinimalTheme()
    {
        return new ThemeConfiguration
        {
            WindowRounding = 4.0f,
            ChildRounding = 4.0f,
            PopupRounding = 4.0f,
            FrameRounding = 2.0f,
            ScrollbarRounding = 2.0f,
            GrabRounding = 2.0f,
            TabRounding = 2.0f,
            CompactWindowRounding = 6.0f,
            CompactChildRounding = 6.0f,
            CompactPopupRounding = 6.0f,
            CompactFrameRounding = 4.0f,
            CompactScrollbarRounding = 4.0f,
            CompactGrabRounding = 4.0f,
            CompactTabRounding = 4.0f,
            CompactHeaderRounding = 2.0f,
            CompactWindowPadding = new Vector2(6.0f, 4.0f),
            CompactFramePadding = new Vector2(3.0f, 1.5f),
            CompactItemSpacing = new Vector2(4.0f, 2.0f),
            CompactItemInnerSpacing = new Vector2(2.0f, 1.0f),
            CompactCellPadding = new Vector2(2.0f, 1.0f),
            CompactChildPadding = new Vector2(6.0f, 4.0f),
            CompactIndentSpacing = 16.0f,
            CompactScrollbarSize = 12.0f,
            CompactGrabMinSize = 8.0f,
            CompactButtonTextAlign = new Vector2(0.5f, 0.5f),
            CompactSelectableTextAlign = new Vector2(0.0f, 0.0f),
            CompactWindowBorderSize = 0.5f,
            CompactChildBorderSize = 0.5f,
            CompactPopupBorderSize = 0.5f,
            CompactFrameBorderSize = 0.0f,
            CompactTooltipRounding = 4.0f,
            CompactTooltipBorderSize = 0.5f,
            CompactContextMenuRounding = 4.0f,
            CompactContextMenuBorderSize = 0.5f,
            WindowPadding = new Vector2(12.0f, 8.0f),
            FramePadding = new Vector2(4.0f, 2.0f),
            ItemSpacing = new Vector2(4.0f, 2.0f),
            ItemInnerSpacing = new Vector2(2.0f, 1.0f),
            IndentSpacing = 16.0f,
            WindowBorderSize = 0.5f,
            ChildBorderSize = 0.5f,
            PopupBorderSize = 0.5f,
            FrameBorderSize = 0.0f,
            ScrollbarSize = 12.0f,
            GrabMinSize = 8.0f,
            PrimaryDark = new Vector4(0.12f, 0.12f, 0.12f, 0.9f),
            SecondaryDark = new Vector4(0.16f, 0.16f, 0.16f, 0.9f),
            AccentBlue = new Vector4(0.3f, 0.3f, 0.3f, 1.0f),
            AccentCyan = new Vector4(0.4f, 0.4f, 0.4f, 1.0f),
            TextPrimary = new Vector4(0.85f, 0.85f, 0.85f, 1.0f),
            TextSecondary = new Vector4(0.6f, 0.6f, 0.6f, 1.0f),
            Border = new Vector4(0.25f, 0.25f, 0.25f, 1.0f),
            Hover = new Vector4(0.35f, 0.35f, 0.35f, 1.0f),
            Active = new Vector4(0.45f, 0.45f, 0.45f, 1.0f),
            HeaderBg = new Vector4(0.3f, 0.3f, 0.3f, 0.3f),
            CompactShowImGuiHeader = true
        };
    }

    // Method to apply theme values from user input
    public static void ApplyThemeFromValues(ThemeConfiguration theme, Dictionary<string, object> values)
    {
        foreach (var kvp in values)
        {
            var property = typeof(ThemeConfiguration).GetProperty(kvp.Key);
            if (property != null && property.CanWrite)
            {
                try
                {
                    if (property.PropertyType == typeof(float))
                    {
                        property.SetValue(theme, Convert.ToSingle(kvp.Value));
                    }
                    else if (property.PropertyType == typeof(bool))
                    {
                        property.SetValue(theme, Convert.ToBoolean(kvp.Value));
                    }
                    else if (property.PropertyType == typeof(Vector2))
                    {
                        if (kvp.Value is Vector2 vec2)
                            property.SetValue(theme, vec2);
                        else if (kvp.Value is float[] arr && arr.Length >= 2)
                            property.SetValue(theme, new Vector2(arr[0], arr[1]));
                    }
                    else if (property.PropertyType == typeof(Vector4))
                    {
                        if (kvp.Value is Vector4 vec4)
                            property.SetValue(theme, vec4);
                        else if (kvp.Value is float[] arr && arr.Length >= 4)
                            property.SetValue(theme, new Vector4(arr[0], arr[1], arr[2], arr[3]));
                    }
                }
                catch
                {
                    // Ignore invalid values
                }
            }
        }
        
        theme.NotifyThemeChanged();
    }

    // Method to get theme values as dictionary for easy sharing
    public static Dictionary<string, object> GetThemeValues(ThemeConfiguration theme)
    {
        var values = new Dictionary<string, object>();
        var properties = typeof(ThemeConfiguration).GetProperties()
            .Where(p => p.CanRead && (p.PropertyType == typeof(float) || 
                                     p.PropertyType == typeof(Vector2) || 
                                     p.PropertyType == typeof(Vector4) ||
                                     p.PropertyType == typeof(bool)));

        foreach (var property in properties)
        {
            var value = property.GetValue(theme);
            if (value != null)
                values[property.Name] = value;
        }

        return values;
    }
}