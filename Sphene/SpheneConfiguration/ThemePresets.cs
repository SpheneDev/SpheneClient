using System.Numerics;
using Sphene.UI.Theme;

namespace Sphene.Configuration;

public static class ThemePresets
{
    public static Dictionary<string, ThemeConfiguration> BuiltInThemes { get; } = new(StringComparer.Ordinal)
    {
        ["Default Sphene"] = CreateDefaultSpheneTheme(),
        ["Minimal"] = CreateMinimalTheme()
    };

    /// <summary>
    /// Default Sphene is defined by the <see cref="ThemeConfiguration"/> property initializers;
    /// this preset only adds the designed button-style overrides.
    /// </summary>
    private static ThemeConfiguration CreateDefaultSpheneTheme()
    {
        return new ThemeConfiguration
        {
            ButtonStyles = new Dictionary<string, ButtonStyleOverride>(StringComparer.Ordinal)
            {
                // TopTab buttons
                [ButtonStyleKeys.TopTab_User] = new ButtonStyleOverride
                {
                    Button = new Vector4(0.23930794f, 0.56535655f, 0.96862745f, 0.14901961f),
                    ButtonHovered = new Vector4(0.24705881f, 0.5836677f, 1.0f, 0.34901962f),
                    ButtonActive = new Vector4(0.24705881f, 0.5836677f, 1.0f, 0.54901963f),
                    BorderSize = 0f
                },
                [ButtonStyleKeys.TopTab_Users] = new ButtonStyleOverride
                {
                    Button = new Vector4(0.23921569f, 0.5647059f, 0.96862745f, 0.14901961f),
                    ButtonHovered = new Vector4(0.24696356f, 0.582996f, 1.0f, 0.34901962f),
                    ButtonActive = new Vector4(0.24696356f, 0.582996f, 1.0f, 0.54901963f),
                    BorderSize = 0f
                },
                [ButtonStyleKeys.TopTab_ModSharing] = new ButtonStyleOverride
                {
                    Button = new Vector4(0.23921569f, 0.5647059f, 0.96862745f, 0.14901961f),
                    ButtonHovered = new Vector4(0.24696356f, 0.582996f, 1.0f, 0.34901962f),
                    ButtonActive = new Vector4(0.24696356f, 0.582996f, 1.0f, 0.54901963f),
                    BorderSize = 0f
                },
                [ButtonStyleKeys.TopTab_Filter] = new ButtonStyleOverride
                {
                    Button = new Vector4(0.23921569f, 0.5647059f, 0.96862745f, 0.14901961f),
                    ButtonHovered = new Vector4(0.24696356f, 0.582996f, 1.0f, 0.34901962f),
                    ButtonActive = new Vector4(0.24696356f, 0.582996f, 1.0f, 0.54901963f),
                    BorderSize = 0f
                },
                [ButtonStyleKeys.TopTab_Settings] = new ButtonStyleOverride
                {
                    Button = new Vector4(0.23921569f, 0.5647059f, 0.96862745f, 0.14901961f),
                    ButtonHovered = new Vector4(0.24696356f, 0.582996f, 1.0f, 0.34901962f),
                    ButtonActive = new Vector4(0.24696356f, 0.582996f, 1.0f, 0.54901963f),
                    BorderSize = 0f
                },

                // TopTab global Individual controls
                [ButtonStyleKeys.TopTab_IndividualPause] = new ButtonStyleOverride(),
                [ButtonStyleKeys.TopTab_IndividualSound] = new ButtonStyleOverride(),
                [ButtonStyleKeys.TopTab_IndividualAnimations] = new ButtonStyleOverride(),
                [ButtonStyleKeys.TopTab_IndividualVFX] = new ButtonStyleOverride(),

                // TopTab global Syncshell controls
                [ButtonStyleKeys.TopTab_SyncshellPause] = new ButtonStyleOverride(),
                [ButtonStyleKeys.TopTab_SyncshellSound] = new ButtonStyleOverride(),
                [ButtonStyleKeys.TopTab_SyncshellAnimations] = new ButtonStyleOverride(),
                [ButtonStyleKeys.TopTab_SyncshellVFX] = new ButtonStyleOverride(),
                [ButtonStyleKeys.TopTab_SyncshellAlign] = new ButtonStyleOverride(),

                // Syncshell group row (values provided below)

                // Pair tag row
                [ButtonStyleKeys.PairTag_Menu] = new ButtonStyleOverride(),
                [ButtonStyleKeys.PairTag_Pause] = new ButtonStyleOverride(),

                // Unified context menu item style
                [ButtonStyleKeys.ContextMenu_Item] = new ButtonStyleOverride
                {
                    Button = new Vector4(0.0f, 0.5480428f, 1.0f, 0.4f),
                    ButtonHovered = new Vector4(0.0f, 0.5480428f, 1.0f, 0.6f),
                    ButtonActive = new Vector4(0.0f, 0.5480428f, 1.0f, 0.8f)
                },

                // CompactUI specific
                [ButtonStyleKeys.Compact_Connect] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(0.0f, 0.0f),
                    BorderSize = 0f
                },
                [ButtonStyleKeys.Compact_IncognitoOn] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(1.6f, 0.9f),
                    ButtonHovered = new Vector4(0.26530612f, 0.60204077f, 1.0f, 0.6f),
                    ButtonActive = new Vector4(0.26530612f, 0.60204077f, 1.0f, 0.8f),
                    Icon = new Vector4(1.0f, 0.27639383f, 0.27639383f, 1.0f)
                },
                [ButtonStyleKeys.Compact_IncognitoOff] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(1.4f, 0.0f),
                    ButtonHovered = new Vector4(0.26530612f, 0.60204077f, 1.0f, 0.6f),
                    ButtonActive = new Vector4(0.26530612f, 0.60204077f, 1.0f, 0.8f)
                },
                [ButtonStyleKeys.Compact_Conversion] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(1.0f, 0.0f)
                },
                [ButtonStyleKeys.Compact_AreaSelect] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(0.0f, 0.0f)
                },
                [ButtonStyleKeys.Compact_Settings] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 1.0f,
                    IconOffset = new Vector2(0.5f, 0.4f),
                    Button = new Vector4(0.16370112f, 0.16370112f, 0.16370112f, 0.6392157f),
                    ButtonHovered = new Vector4(0.2637011f, 0.2637011f, 0.2637011f, 0.8392157f),
                    ButtonActive = new Vector4(0.41370112f, 0.41370112f, 0.41370112f, 1.0f),
                    BorderSize = 0f
                },
                [ButtonStyleKeys.Compact_Close] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 1.0f,
                    IconOffset = new Vector2(0.4f, -0.9f),
                    Button = new Vector4(0.98f, 0.26f, 0.26f, 0.4f),
                    ButtonHovered = new Vector4(1.0f, 0.26530612f, 0.26530612f, 0.6f),
                    ButtonActive = new Vector4(1.0f, 0.26530612f, 0.26530612f, 0.8f),
                    BorderSize = 0f
                },

                // Group and pair controls
                [ButtonStyleKeys.Pair_Pause] = new ButtonStyleOverride
                {
                    WidthDelta = 0.6f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(0.8f, 0.0f)
                },
                [ButtonStyleKeys.Pair_Menu] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(0.5f, 0.0f)
                },
                ["GroupPair.Pause"] = new ButtonStyleOverride
                {
                    WidthDelta = 0.6f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(0.8f, 0.0f)
                },
                ["GroupPair.Menu"] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(0.5f, 0.0f)
                },
                [ButtonStyleKeys.GroupSyncshell_Menu] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(0.5f, 0.0f)
                },
                [ButtonStyleKeys.GroupSyncshell_Pause] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(1.0f, 0.0f)
                },
                [ButtonStyleKeys.Compact_Reconnect] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(0.0f, 0.5f),
                    Icon = new Vector4(0.3653618f, 1.0f, 0.5866945f, 1.0f)
                },
                [ButtonStyleKeys.Compact_Disconnect] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(0.0f, 0.0f),
                    BorderSize = 0f
                },
                [ButtonStyleKeys.Compact_TestServer] = new ButtonStyleOverride
                {
                    WidthDelta = 0.0f,
                    HeightDelta = 0.0f,
                    IconOffset = new Vector2(0.0f, 0.0f),
                    Button = new Vector4(0.98f, 0.26f, 0.26f, 0.45f),
                    ButtonHovered = new Vector4(1.0f, 0.26530612f, 0.26530612f, 0.65f),
                    ButtonActive = new Vector4(1.0f, 0.26530612f, 0.26530612f, 0.85f),
                    Icon = new Vector4(1.0f, 0.85f, 0.25f, 1.0f),
                    BorderSize = 0f
                }
            }
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
            
            // CompactUI Progress Bar Settings
            ProgressBarRounding = 4.0f,
            CompactProgressBarHeight = 20.0f,
            CompactProgressBarWidth = 200.0f,
            CompactProgressBarBackground = new Vector4(0.12f, 0.12f, 0.12f, 0.9f),
            CompactProgressBarForeground = new Vector4(0.3f, 0.3f, 0.3f, 1.0f),
            CompactProgressBarBorder = new Vector4(0.25f, 0.25f, 0.25f, 1.0f),
            ShowProgressBarPreview = false,
            ProgressBarPreviewFill = 75.0f,
            
            // CompactUI Transmission Progress Bar Settings
            TransmissionBarRounding = 2.0f,
            CompactTransmissionBarHeight = 8.0f,
            CompactTransmissionBarWidth = 120.0f,
            CompactTransmissionBarBackground = new Vector4(0.12f, 0.12f, 0.12f, 0.9f),
            CompactTransmissionBarForeground = new Vector4(0.4f, 0.4f, 0.4f, 1.0f),
            CompactTransmissionBarBorder = new Vector4(0.25f, 0.25f, 0.25f, 1.0f),
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
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
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
