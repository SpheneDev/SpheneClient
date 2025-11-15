using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using System.Text.Json;

namespace Sphene.UI.Theme;

public static class ButtonStyleManagerUI
{
    private static readonly JsonSerializerOptions ClipboardJsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new Vector4JsonConverter(), new Vector2JsonConverter() }
    };

    private static (Vector4 Button, Vector4 Hovered, Vector4 Active, Vector4 Text, Vector4 Icon, Vector4 Border, float BorderSize) GetDefaultsForKey(string key, ThemeConfiguration theme)
    {
        if (key.StartsWith("CompactUI."))
        {
            return (
                theme.CompactActionButton,
                theme.CompactActionButtonHovered,
                theme.CompactActionButtonActive,
                theme.TextPrimary,
                theme.TextPrimary,
                theme.CompactBorder,
                theme.FrameBorderSize
            );
        }
        return (
            theme.Button,
            theme.ButtonHovered,
            theme.ButtonActive,
            theme.TextPrimary,
            theme.TextPrimary,
            theme.Border,
            theme.FrameBorderSize
        );
    }
    private static bool _pickerEnabled = false;
    private static readonly (string Key, string Label)[] _keys =
    {
        // CompactUI controls
        (ButtonStyleKeys.Compact_Connect, "CompactUI: Connect"),
        (ButtonStyleKeys.Compact_Disconnect, "CompactUI: Disconnect"),
        (ButtonStyleKeys.Compact_Reconnect, "CompactUI: Reconnect"),
        (ButtonStyleKeys.Compact_Conversion, "CompactUI: Conversion"),
        (ButtonStyleKeys.Compact_IncognitoOn, "CompactUI: Incognito On"),
        (ButtonStyleKeys.Compact_IncognitoOff, "CompactUI: Incognito Off"),
        (ButtonStyleKeys.Compact_AreaSelect, "CompactUI: Area Select"),
        (ButtonStyleKeys.Compact_Settings, "CompactUI: Settings"),
        (ButtonStyleKeys.Compact_Close, "CompactUI: Close"),

        // Top tabs
        (ButtonStyleKeys.TopTab_User, "TopTab: User"),
        (ButtonStyleKeys.TopTab_Users, "TopTab: Users"),
        (ButtonStyleKeys.TopTab_Filter, "TopTab: Filter"),
        (ButtonStyleKeys.TopTab_Settings, "TopTab: Settings"),

        // TopTab global Individual controls
        (ButtonStyleKeys.TopTab_IndividualPause, "TopTab: Individual Pause"),
        (ButtonStyleKeys.TopTab_IndividualSound, "TopTab: Individual Sound"),
        (ButtonStyleKeys.TopTab_IndividualAnimations, "TopTab: Individual Animations"),
        (ButtonStyleKeys.TopTab_IndividualVFX, "TopTab: Individual VFX"),

        // TopTab global Syncshell controls
        (ButtonStyleKeys.TopTab_SyncshellPause, "TopTab: Syncshell Pause"),
        (ButtonStyleKeys.TopTab_SyncshellSound, "TopTab: Syncshell Sound"),
        (ButtonStyleKeys.TopTab_SyncshellAnimations, "TopTab: Syncshell Animations"),
        (ButtonStyleKeys.TopTab_SyncshellVFX, "TopTab: Syncshell VFX"),
        (ButtonStyleKeys.TopTab_SyncshellAlign, "TopTab: Syncshell Align"),

        // Group Pair component
        (ButtonStyleKeys.Pair_Sync, "GroupPair: Sync"),
        (ButtonStyleKeys.Pair_Reload, "GroupPair: Reload"),
        (ButtonStyleKeys.Pair_Pause, "GroupPair: Pause/Play"),
        (ButtonStyleKeys.Pair_Menu, "GroupPair: Menu"),

        // Popup components
        (ButtonStyleKeys.Popup_Close, "Popup: Close"),

        

        // Syncshell group row
        (ButtonStyleKeys.GroupSyncshell_Menu, "Syncshell Group: Menu"),
        (ButtonStyleKeys.GroupSyncshell_Pause, "Syncshell Group: Pause"),

        // Pair tag row
        (ButtonStyleKeys.PairTag_Menu, "Pair Tag: Menu"),
        (ButtonStyleKeys.PairTag_Pause, "Pair Tag: Pause")
    };

    private static int _selectedIndex = 0;
    private static int _copyTargetIndex = 0;

    public static bool IsPickerEnabled => _pickerEnabled;
    public static void DisablePicker()
    {
        _pickerEnabled = false;
    }

    public static void SelectButtonKey(string key)
    {
        for (int i = 0; i < _keys.Length; i++)
        {
            if (string.Equals(_keys[i].Key, key, StringComparison.Ordinal))
            {
                _selectedIndex = i;
                break;
            }
        }
    }

    private static Vector4 DeriveHoverColor(Vector4 baseColor)
    {
        HsvFromRgba(baseColor, out var h, out var s, out var v);
        v = MathF.Min(1f, v + 0.1f);
        var a = MathF.Min(1f, baseColor.W + 0.2f);
        return RgbaFromHsv(h, s, v, a);
    }

    private static Vector4 DeriveActiveColor(Vector4 baseColor)
    {
        HsvFromRgba(baseColor, out var h, out var s, out var v);
        v = MathF.Min(1f, v + 0.25f);
        var a = MathF.Min(1f, baseColor.W + 0.4f);
        return RgbaFromHsv(h, s, v, a);
    }

    private static void HsvFromRgba(Vector4 rgba, out float h, out float s, out float v)
    {
        float r = rgba.X, g = rgba.Y, b = rgba.Z;
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        v = max;
        float d = max - min;
        s = max == 0 ? 0 : d / max;
        if (d == 0)
        {
            h = 0;
        }
        else
        {
            if (max == r)
                h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g)
                h = (b - r) / d + 2;
            else
                h = (r - g) / d + 4;
            h /= 6f;
        }
    }

    private static Vector4 RgbaFromHsv(float h, float s, float v, float a)
    {
        if (s == 0)
            return new Vector4(v, v, v, a);
        h *= 6f;
        int i = (int)MathF.Floor(h);
        float f = h - i;
        float p = v * (1 - s);
        float q = v * (1 - s * f);
        float t = v * (1 - s * (1 - f));
        switch (i % 6)
        {
            case 0: return new Vector4(v, t, p, a);
            case 1: return new Vector4(q, v, p, a);
            case 2: return new Vector4(p, v, t, a);
            case 3: return new Vector4(p, q, v, a);
            case 4: return new Vector4(t, p, v, a);
            default: return new Vector4(v, p, q, a);
        }
    }
    

    public static void Draw()
    {
        var theme = SpheneCustomTheme.CurrentTheme;

        ImGui.Checkbox("Button Picker", ref _pickerEnabled);
        if (ImGui.IsItemHovered())
            Sphene.UI.UiSharedService.AttachToolTip("When enabled you can Click on Active Control Panel UI Buttons to navigate to their Button Style settings.");
        ImGui.Separator();

        if (ImGui.BeginCombo("Button", _keys[_selectedIndex].Label))
        {
            for (int i = 0; i < _keys.Length; i++)
            {
                bool selected = _selectedIndex == i;
                if (ImGui.Selectable(_keys[i].Label, selected))
                    _selectedIndex = i;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Separator();

        float widthDelta = 0f;
        float heightDelta = 0f;
        Vector2 iconOffset = Vector2.Zero;

        {
            var key = _keys[_selectedIndex].Key;
            var defaults = GetDefaultsForKey(key, theme);
            if (!theme.ButtonStyles.TryGetValue(key, out var ov))
            {
                ov = new ButtonStyleOverride();
                theme.ButtonStyles[key] = ov;
            }

            widthDelta = ov.WidthDelta;
            heightDelta = ov.HeightDelta;
            iconOffset = ov.IconOffset;

            ImGui.Text("Selected Button Overrides");
            if (ImGui.DragFloat("Width Delta", ref widthDelta, 0.1f, -100f, 200f))
            {
                ov.WidthDelta = widthDelta;
                theme.NotifyThemeChanged();
            }
            if (ImGui.DragFloat("Height Delta", ref heightDelta, 0.1f, -50f, 100f))
            {
                ov.HeightDelta = heightDelta;
                theme.NotifyThemeChanged();
            }
            if (ImGui.DragFloat2("Icon Offset", ref iconOffset, 0.1f, -50f, 50f))
            {
                ov.IconOffset = iconOffset;
                theme.NotifyThemeChanged();
            }

            ImGui.Separator();
            ImGui.Text("Selected Button Colors");
            var effBtn = ov.Button ?? defaults.Button;
            if (ImGui.ColorEdit4("Button", ref effBtn))
            {
                ov.Button = effBtn;
                theme.NotifyThemeChanged();
            }
            if (ImGui.Button("Derive Hover/Active from Button"))
            {
                var hover = DeriveHoverColor(effBtn);
                var active = DeriveActiveColor(effBtn);
                ov.ButtonHovered = hover;
                ov.ButtonActive = active;
                theme.NotifyThemeChanged();
            }
            var effBtnH = ov.ButtonHovered ?? defaults.Hovered;
            if (ImGui.ColorEdit4("Button Hovered", ref effBtnH))
            {
                ov.ButtonHovered = effBtnH;
                theme.NotifyThemeChanged();
            }
            var effBtnA = ov.ButtonActive ?? defaults.Active;
            if (ImGui.ColorEdit4("Button Active", ref effBtnA))
            {
                ov.ButtonActive = effBtnA;
                theme.NotifyThemeChanged();
            }
            var effIcon = ov.Icon ?? defaults.Icon;
            if (ImGui.ColorEdit4("Icon", ref effIcon))
            {
                ov.Icon = effIcon;
                theme.NotifyThemeChanged();
            }
            var effBorder = ov.Border ?? defaults.Border;
            if (ImGui.ColorEdit4("Border", ref effBorder))
            {
                ov.Border = effBorder;
                theme.NotifyThemeChanged();
            }
            var effBorderSize = ov.BorderSize ?? defaults.BorderSize;
            if (ImGui.SliderFloat("Border Width", ref effBorderSize, 0f, 5f, "%.1f"))
            {
                ov.BorderSize = effBorderSize;
                theme.NotifyThemeChanged();
            }
            var effTxt = ov.Text ?? defaults.Text;
            if (ImGui.ColorEdit4("Text", ref effTxt))
            {
                ov.Text = effTxt;
                theme.NotifyThemeChanged();
            }

            if (ImGui.Button("Reset Selected"))
            {
                ov.WidthDelta = 0f;
                ov.HeightDelta = 0f;
                ov.IconOffset = Vector2.Zero;
                ov.Button = null;
                ov.ButtonHovered = null;
                ov.ButtonActive = null;
                ov.Text = null;
                ov.Icon = null;
                ov.Border = null;
                ov.BorderSize = null;
                theme.NotifyThemeChanged();
            }

            ImGui.Separator();
            var srcKey = _keys[_selectedIndex].Key;
            if (ImGui.Button("Copy Colors"))
            {
                if (!theme.ButtonStyles.TryGetValue(srcKey, out var src))
                {
                    src = new ButtonStyleOverride();
                    theme.ButtonStyles[srcKey] = src;
                }
                var srcDefaults = GetDefaultsForKey(srcKey, theme);
                var effSrcBtn = src.Button ?? srcDefaults.Button;
                var effSrcBtnH = src.ButtonHovered ?? srcDefaults.Hovered;
                var effSrcBtnA = src.ButtonActive ?? srcDefaults.Active;
                var effSrcIcon = src.Icon ?? srcDefaults.Icon;
                var effSrcBorder = src.Border ?? srcDefaults.Border;
                var effSrcText = src.Text ?? srcDefaults.Text;
                var effSrcBorderSize = src.BorderSize ?? srcDefaults.BorderSize;
                var payload = JsonSerializer.Serialize(new ButtonStyleOverride
                {
                    Button = effSrcBtn,
                    ButtonHovered = effSrcBtnH,
                    ButtonActive = effSrcBtnA,
                    Text = effSrcText,
                    Icon = effSrcIcon,
                    Border = effSrcBorder,
                    BorderSize = effSrcBorderSize
                }, ClipboardJsonOptions);
                ImGui.SetClipboardText("SPHENE_BTN_STYLE:colors:" + payload);
            }
            ImGui.SameLine();
            if (ImGui.Button("Copy All"))
            {
                if (!theme.ButtonStyles.TryGetValue(srcKey, out var src))
                {
                    src = new ButtonStyleOverride();
                    theme.ButtonStyles[srcKey] = src;
                }
                var payload = JsonSerializer.Serialize(src, ClipboardJsonOptions);
                ImGui.SetClipboardText("SPHENE_BTN_STYLE:all:" + payload);
            }

            if (ImGui.Button("Paste Colors"))
            {
                var clip = ImGui.GetClipboardText();
                if (clip != null && clip.StartsWith("SPHENE_BTN_STYLE:colors:"))
                {
                    var json = clip.Substring("SPHENE_BTN_STYLE:colors:".Length);
                    var src = JsonSerializer.Deserialize<ButtonStyleOverride>(json, ClipboardJsonOptions);
                    if (src != null)
                    {
                        var dstKey = _keys[_selectedIndex].Key;
                        if (!theme.ButtonStyles.TryGetValue(dstKey, out var dst))
                        {
                            dst = new ButtonStyleOverride();
                            theme.ButtonStyles[dstKey] = dst;
                        }
                        dst.Button = src.Button;
                        dst.ButtonHovered = src.ButtonHovered;
                        dst.ButtonActive = src.ButtonActive;
                        dst.Text = src.Text;
                        dst.Icon = src.Icon;
                        dst.Border = src.Border;
                        dst.BorderSize = src.BorderSize;
                        theme.NotifyThemeChanged();
                    }
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Paste All"))
            {
                var clip = ImGui.GetClipboardText();
                if (clip != null && clip.StartsWith("SPHENE_BTN_STYLE:all:"))
                {
                    var json = clip.Substring("SPHENE_BTN_STYLE:all:".Length);
                    var src = JsonSerializer.Deserialize<ButtonStyleOverride>(json, ClipboardJsonOptions);
                    if (src != null)
                    {
                        var dstKey = _keys[_selectedIndex].Key;
                        if (!theme.ButtonStyles.TryGetValue(dstKey, out var dst))
                        {
                            dst = new ButtonStyleOverride();
                            theme.ButtonStyles[dstKey] = dst;
                        }
                        dst.WidthDelta = src.WidthDelta;
                        dst.HeightDelta = src.HeightDelta;
                        dst.IconOffset = src.IconOffset;
                        dst.Button = src.Button;
                        dst.ButtonHovered = src.ButtonHovered;
                        dst.ButtonActive = src.ButtonActive;
                        dst.Text = src.Text;
                        dst.Icon = src.Icon;
                        dst.Border = src.Border;
                        dst.BorderSize = src.BorderSize;
                        theme.NotifyThemeChanged();
                    }
                }
            }
        }
    }

    public static (Vector4 Hover, Vector4 Active) DeriveHoverActive(Vector4 baseColor)
    {
        var hover = DeriveHoverColor(baseColor);
        var active = DeriveActiveColor(baseColor);
        return (hover, active);
    }
}