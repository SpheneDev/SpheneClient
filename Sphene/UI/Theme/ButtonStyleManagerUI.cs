using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using System.Text.Json;
using Sphene.Configuration;

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
        if (key.StartsWith("CompactUI.", StringComparison.Ordinal))
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
    private static readonly (string Key, string Label, FontAwesomeIcon Icon, string Category)[] _keys =
    {
        // Control Panel (CompactUI)
        (ButtonStyleKeys.Compact_Connect, "Connect", FontAwesomeIcon.Link, "Control Panel"),
        (ButtonStyleKeys.Compact_Disconnect, "Disconnect", FontAwesomeIcon.Unlink, "Control Panel"),
        (ButtonStyleKeys.Compact_Reconnect, "Reconnect", FontAwesomeIcon.Redo, "Control Panel"),
        (ButtonStyleKeys.Compact_Conversion, "Conversion", FontAwesomeIcon.ArrowsToEye, "Control Panel"),
        (ButtonStyleKeys.Compact_IncognitoOn, "Incognito Disabled", FontAwesomeIcon.Heart, "Control Panel"),
        (ButtonStyleKeys.Compact_IncognitoOff, "Incognito Enabled", FontAwesomeIcon.Play, "Control Panel"),
        (ButtonStyleKeys.Compact_AreaSelect, "Area Select", FontAwesomeIcon.MapMarkerAlt, "Control Panel"),
        (ButtonStyleKeys.Compact_Settings, "Settings", FontAwesomeIcon.Cog, "Control Panel"),
        (ButtonStyleKeys.Compact_Close, "Close", FontAwesomeIcon.Times, "Control Panel"),

        // Navigation (TopTab)
        // Navigation - Individual Controls
        (ButtonStyleKeys.TopTab_User, "Individual Pair Menu", FontAwesomeIcon.User, "Navigation: Individual Pair"),
        (ButtonStyleKeys.TopTab_IndividualPause, "Individual Pause", FontAwesomeIcon.Pause, "Navigation: Individual Pair"),
        (ButtonStyleKeys.TopTab_IndividualSound, "Individual Sound", FontAwesomeIcon.VolumeUp, "Navigation: Individual Pair"),
        (ButtonStyleKeys.TopTab_IndividualAnimations, "Individual Animations", FontAwesomeIcon.Running, "Navigation: Individual Pair"),
        (ButtonStyleKeys.TopTab_IndividualVFX, "Individual VFX", FontAwesomeIcon.Sun, "Navigation: Individual Pair"),

        // Navigation - Syncshell Controls
        (ButtonStyleKeys.TopTab_Users, "Syncshell Menu", FontAwesomeIcon.Users, "Navigation: Syncshell"),
        (ButtonStyleKeys.TopTab_SyncshellPause, "Syncshell Pause", FontAwesomeIcon.PauseCircle, "Navigation: Syncshell"),
        (ButtonStyleKeys.TopTab_SyncshellSound, "Syncshell Sound", FontAwesomeIcon.VolumeUp, "Navigation: Syncshell"),
        (ButtonStyleKeys.TopTab_SyncshellAnimations, "Syncshell Animations", FontAwesomeIcon.Running, "Navigation: Syncshell"),
        (ButtonStyleKeys.TopTab_SyncshellVFX, "Syncshell VFX", FontAwesomeIcon.Sun, "Navigation: Syncshell"),
        (ButtonStyleKeys.TopTab_SyncshellAlign, "Syncshell Align", FontAwesomeIcon.Check, "Navigation: Syncshell"),

        // Navigation - Filter
        (ButtonStyleKeys.TopTab_Filter, "Filter", FontAwesomeIcon.Filter, "Navigation: Filter"),

        // Navigation - Your User Menu
        (ButtonStyleKeys.TopTab_Settings, "Your User Menu", FontAwesomeIcon.UserCog, "Navigation: Your User Menu"),



        // Group & Pair
        // (ButtonStyleKeys.Pair_Sync, "Pair Sync", FontAwesomeIcon.Sync, "Pairs"), // Temporarily disabled until functionality is implemented
        (ButtonStyleKeys.Pair_Reload, "Pair Reload", FontAwesomeIcon.Sync, "Pairs"),
        (ButtonStyleKeys.Pair_Pause, "Pair Pause/Play", FontAwesomeIcon.Pause, "Pairs"),
        (ButtonStyleKeys.Pair_Menu, "Pair Menu", FontAwesomeIcon.EllipsisV, "Pairs"),
        (ButtonStyleKeys.GroupSyncshell_Menu, "Syncshell Menu", FontAwesomeIcon.EllipsisV, "Synchsell"),
        (ButtonStyleKeys.GroupSyncshell_Pause, "Syncshell Pause", FontAwesomeIcon.Pause, "Synchsell"),
        (ButtonStyleKeys.PairTag_Menu, "Group Menu", FontAwesomeIcon.EllipsisV, "Group" ),
        (ButtonStyleKeys.PairTag_Pause, "Group Pause", FontAwesomeIcon.Pause, "Group"),
    };

    private static int _selectedIndex = 0;

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
        s = MathF.Abs(max) < 1e-6f ? 0 : d / max;
        if (MathF.Abs(d) < 1e-6f)
        {
            h = 0;
        }
        else
        {
            if (MathF.Abs(max - r) < 1e-6f)
                h = (g - b) / d + (g < b ? 6 : 0);
            else if (MathF.Abs(max - g) < 1e-6f)
                h = (b - r) / d + 2;
            else
                h = (r - g) / d + 4;
            h /= 6f;
        }
    }

    private static Vector4 RgbaFromHsv(float h, float s, float v, float a)
    {
        if (MathF.Abs(s) < 1e-6f)
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

        ImGui.PushFont(UiBuilder.IconFont);
        var iconWidth = ImGui.CalcTextSize(_keys[_selectedIndex].Icon.ToIconString()).X;
        ImGui.PopFont();
        var spaceWidth = ImGui.CalcTextSize(" ").X;
        var padX = ImGui.GetStyle().FramePadding.X + ImGui.GetStyle().ItemSpacing.X;
        var extraPad = ImGui.GetStyle().ItemSpacing.X;
        var desiredPad = iconWidth + padX + extraPad + spaceWidth * 2.0f;
        var spaces = Math.Max(4, (int)MathF.Ceiling(desiredPad / MathF.Max(1e-3f, spaceWidth)));
        var selectedDisplay = new string(' ', spaces) + _keys[_selectedIndex].Label;
        if (ImGui.BeginCombo("Button", selectedDisplay))
        {
            string? currentCategory = null;
            for (int i = 0; i < _keys.Length; i++)
            {
                var item = _keys[i];
                if (!string.Equals(item.Category, currentCategory, StringComparison.Ordinal))
                {
                    if (currentCategory != null) ImGui.Separator();
                    ImGui.TextDisabled(item.Category);
                    currentCategory = item.Category;
                }
                bool selected = _selectedIndex == i;
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.TextUnformatted(item.Icon.ToIconString());
                ImGui.PopFont();
                ImGui.SameLine();
                if (ImGui.Selectable(item.Label, selected))
                    _selectedIndex = i;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        var rectMin = ImGui.GetItemRectMin();
        var rectMax = ImGui.GetItemRectMax();
        var framePad = ImGui.GetStyle().FramePadding;
        var iconY = rectMin.Y + (rectMax.Y - rectMin.Y - ImGui.GetTextLineHeight()) * 0.5f;
        var iconPos = new Vector2(rectMin.X + framePad.X, iconY);
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.GetWindowDrawList().AddText(iconPos, ImGui.GetColorU32(ImGuiCol.Text), _keys[_selectedIndex].Icon.ToIconString());
        ImGui.PopFont();

        ImGui.Separator();

        DrawSelectedButtonOverrides(theme);
    }

    public static (Vector4 Hover, Vector4 Active) DeriveHoverActive(Vector4 baseColor)
    {
        var hover = DeriveHoverColor(baseColor);
        var active = DeriveActiveColor(baseColor);
        return (hover, active);
    }

    private static void DrawSelectedButtonOverrides(ThemeConfiguration theme)
    {
        float widthDelta = 0f;
        float heightDelta = 0f;
        Vector2 iconOffset;

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
        if (ImGui.DragFloat("Width", ref widthDelta, 0.1f, -100f, 200f))
        {
            ov.WidthDelta = widthDelta;
            theme.NotifyThemeChanged();
        }
        if (ImGui.DragFloat("Height", ref heightDelta, 0.1f, -50f, 100f))
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
        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        var genClicked = ImGui.SmallButton(FontAwesomeIcon.Magic.ToIconString());
        ImGui.PopFont();
        if (genClicked)
        {
            var hover = DeriveHoverColor(effBtn);
            var active = DeriveActiveColor(effBtn);
            ov.ButtonHovered = hover;
            ov.ButtonActive = active;
            theme.NotifyThemeChanged();
        }
        if (ImGui.IsItemHovered())
            Sphene.UI.UiSharedService.AttachToolTip("Generate Hover/Active colors from current Button color.");
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

        if (ImGui.Button("Reset Changes"))
        {
            var selectedThemeName = ThemeManager.GetSelectedTheme();
            ThemeConfiguration? baseline = null;
            if (ThemePresets.BuiltInThemes.ContainsKey(selectedThemeName))
                baseline = ThemePresets.BuiltInThemes[selectedThemeName];
            else
                baseline = ThemeManager.LoadTheme(selectedThemeName);

            if (baseline != null && baseline.ButtonStyles.TryGetValue(key, out var baseOv))
            {
                ov.WidthDelta = baseOv.WidthDelta;
                ov.HeightDelta = baseOv.HeightDelta;
                ov.IconOffset = baseOv.IconOffset;
                ov.Button = baseOv.Button;
                ov.ButtonHovered = baseOv.ButtonHovered;
                ov.ButtonActive = baseOv.ButtonActive;
                ov.Text = baseOv.Text;
                ov.Icon = baseOv.Icon;
                ov.Border = baseOv.Border;
                ov.BorderSize = baseOv.BorderSize;
            }
            else
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
            }
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
            if (clip != null && clip.StartsWith("SPHENE_BTN_STYLE:colors:", StringComparison.Ordinal))
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
            if (clip != null && clip.StartsWith("SPHENE_BTN_STYLE:all:", StringComparison.Ordinal))
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
