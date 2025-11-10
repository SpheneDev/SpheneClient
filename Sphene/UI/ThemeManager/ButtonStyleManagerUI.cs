using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Sphene.UI;

public static class ButtonStyleManagerUI
{
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
        (ButtonStyleKeys.Pair_Pin, "GroupPair: Pin/Unpin"),
        (ButtonStyleKeys.Pair_Remove, "GroupPair: Remove User"),
        (ButtonStyleKeys.Pair_Ban, "GroupPair: Ban User"),
        (ButtonStyleKeys.Pair_Mod, "GroupPair: Mod/Demod User"),
        (ButtonStyleKeys.Pair_ReloadLast, "GroupPair: Reload Last Data"),
        (ButtonStyleKeys.Pair_CyclePause, "GroupPair: Cycle Pause State"),
        (ButtonStyleKeys.Pair_OpenPermissions, "GroupPair: Open Permissions"),
        (ButtonStyleKeys.Pair_Transfer, "GroupPair: Transfer"),

        // Popup components
        (ButtonStyleKeys.Popup_Close, "Popup: Close"),

        // Syncshell group row
        (ButtonStyleKeys.GroupSyncshell_Menu, "Syncshell Group: Menu"),
        (ButtonStyleKeys.GroupSyncshell_Pause, "Syncshell Group: Pause"),

        // Pair tag row
        (ButtonStyleKeys.PairTag_Menu, "Pair Tag: Menu"),
        (ButtonStyleKeys.PairTag_Pause, "Pair Tag: Pause")
    };

    private static int _selectedIndex = 0; // 0 = All, >0 specific
    private static readonly string AllLabel = "All Buttons";

    public static void Draw()
    {
        var theme = SpheneCustomTheme.CurrentTheme;

        // Dropdown for selecting a button or All
        if (ImGui.BeginCombo("Button", _selectedIndex == 0 ? AllLabel : _keys[_selectedIndex - 1].Label))
        {
            if (ImGui.Selectable(AllLabel, _selectedIndex == 0))
                _selectedIndex = 0;
            for (int i = 0; i < _keys.Length; i++)
            {
                bool selected = _selectedIndex - 1 == i;
                if (ImGui.Selectable(_keys[i].Label, selected))
                    _selectedIndex = i + 1;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Separator();

        float widthDelta = 0f;
        float heightDelta = 0f;
        Vector2 iconOffset = Vector2.Zero;

        if (_selectedIndex == 0)
        {
            ImGui.Text("Global Adjustments (additive)");
            ImGui.DragFloat("Width Delta", ref widthDelta, 0.1f, -100f, 100f);
            ImGui.DragFloat("Height Delta", ref heightDelta, 0.1f, -50f, 50f);
            ImGui.DragFloat2("Icon Offset", ref iconOffset, 0.1f, -50f, 50f);

            if (ImGui.Button("Apply to All"))
            {
                foreach (var (key, _) in _keys)
                {
                    if (!theme.ButtonStyles.TryGetValue(key, out var ov))
                    {
                        ov = new ButtonStyleOverride();
                        theme.ButtonStyles[key] = ov;
                    }
                    ov.WidthDelta += widthDelta;
                    ov.HeightDelta += heightDelta;
                    ov.IconOffset += iconOffset;
                }
                theme.NotifyThemeChanged();
            }
        }
        else
        {
            var key = _keys[_selectedIndex - 1].Key;
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

            if (ImGui.Button("Reset Selected"))
            {
                ov.WidthDelta = 0f;
                ov.HeightDelta = 0f;
                ov.IconOffset = Vector2.Zero;
                theme.NotifyThemeChanged();
            }
        }
    }
}