using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Sphene.Helpers.Emoji;
using Sphene.SpheneConfiguration;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>
/// The one shared emoji picker. A host draws <see cref="TriggerButton"/> next
/// to its composer and calls <see cref="Draw"/> every frame; the flyout
/// anchors to the trigger, offers search, categories, recents and a skin-tone
/// selector, and reports picks through the callback registered by the
/// trigger. Selection NEVER submits - it only hands back the UTF-16 emoji
/// sequence for caret insertion.
/// </summary>
public sealed class EmojiPicker
{
    private const int MaxSearchLength = 64;
    private const int MaxRecents = 24;
    private const float PanelWidthLogical = 304f;
    private const float GridHeightLogical = 224f;
    private const float CellLogical = 30f;
    private const float SpriteLogical = 24f;

    private static readonly (FontAwesomeIcon Icon, string Name)[] CategoryIcons =
    {
        (FontAwesomeIcon.Smile, "Smileys"),
        (FontAwesomeIcon.User, "People & body"),
        (FontAwesomeIcon.Paw, "Animals & nature"),
        (FontAwesomeIcon.Utensils, "Food & drink"),
        (FontAwesomeIcon.Plane, "Travel & places"),
        (FontAwesomeIcon.Futbol, "Activities"),
        (FontAwesomeIcon.Lightbulb, "Objects"),
        (FontAwesomeIcon.Heart, "Symbols"),
        (FontAwesomeIcon.Flag, "Flags"),
    };

    private readonly SpheneConfigService _config;
    private string _search = string.Empty;
    private int _category = -1;            // -1 recent-or-all, 0..cats-1 specific
    private bool _wantSearchFocus;
    private Action<string>? _onPick;
    private List<SpheneUI.Emoji.Entry>? _filtered;
    private string _filterKey = "\0";
    private string? _hoveredName;

    public EmojiPicker(SpheneConfigService config) => _config = config;

    /// <summary>Small smiley trigger; call where the button should render.
    /// <paramref name="ghost"/> renders the borderless in-field style.</summary>
    public void TriggerButton(string flyoutId, Action<string> onPick, string tooltip = "Insert emoji",
        bool ghost = false)
    {
        var clicked = ghost
            ? SpheneUI.Icons.GhostButton(FontAwesomeIcon.Smile)
            : SpheneUI.Icons.Button(FontAwesomeIcon.Smile);
        if (clicked)
        {
            _onPick = onPick;
            if (!SpheneUI.Popups.IsFlyoutOpen(flyoutId))
            {
                _wantSearchFocus = true;
                _hoveredName = null;
                _category = _config.Current.RecentEmojis.Count > 0 ? -1 : 0;
            }
            SpheneUI.Popups.ToggleFlyout(flyoutId, preferAbove: true);
        }
        SpheneUI.AttachTooltip(tooltip);
    }

    /// <summary>Opens the flyout without rendering the trigger button -
    /// for hosts that draw their own affordance (feed reaction pills) and
    /// only need the picker's selection surface.</summary>
    public void OpenFlyout(string flyoutId, Action<string> onPick)
    {
        _onPick = onPick;
        if (!SpheneUI.Popups.IsFlyoutOpen(flyoutId))
        {
            _wantSearchFocus = true;
            _hoveredName = null;
            _category = _config.Current.RecentEmojis.Count > 0 ? -1 : 0;
        }
        SpheneUI.Popups.ToggleFlyout(flyoutId, preferAbove: true);
    }

    /// <summary>Renders the flyout while open; a no-op otherwise.</summary>
    public void Draw(string flyoutId)
    {
        if (!SpheneUI.Popups.BeginFlyout(flyoutId))
        {
            return;
        }
        var scale = SpheneUI.Scale;
        var panelW = PanelWidthLogical * scale;
        _hoveredName = null;

        if (!SpheneUI.Emoji.IsReady)
        {
            SpheneUI.Text.MutedWrapped("Emoji are still loading...",
                ImGui.GetCursorPosX() + panelW);
            ImGui.Dummy(new Vector2(panelW, 1f));
            SpheneUI.Popups.EndFlyout();
            return;
        }

        if (_wantSearchFocus)
        {
            ImGui.SetKeyboardFocusHere();
            _wantSearchFocus = false;
        }
        SpheneUI.Controls.InputTextWithHint("##emoji-search", "Search emoji",
            ref _search, MaxSearchLength, preferredWidth: panelW);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        DrawCategoryRow(panelW);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        DrawGrid(panelW, GridHeightLogical * scale);
        DrawFooter(panelW);

        SpheneUI.Popups.EndFlyout();
    }

    private void DrawCategoryRow(float panelW)
    {
        var scale = SpheneUI.Scale;
        var cats = SpheneUI.Emoji.CategoryIds;
        var count = cats == null ? 0 : Math.Min(cats.Count, CategoryIcons.Length);
        var btnW = MathF.Floor((panelW - (count) * 2f * scale) / (count + 1));
        // recents slot first
        DrawCatButton(-1, FontAwesomeIcon.Clock, "Recently used", btnW);
        for (var i = 0; i < count; i++)
        {
            ImGui.SameLine(0f, 2f * scale);
            DrawCatButton(i, CategoryIcons[i].Icon, CategoryIcons[i].Name, btnW);
        }
    }

    private void DrawCatButton(int cat, FontAwesomeIcon icon, string name, float width)
    {
        var active = _category == cat;
        var pos = ImGui.GetCursorScreenPos();
        if (SpheneUI.Icons.Button(icon, width: width))
        {
            _category = active ? -1 : cat;
            _filtered = null;
        }
        if (active)
        {
            var h = MathF.Max(1f, SpheneUI.Scale);
            ImGui.GetWindowDrawList().AddRectFilled(
                new Vector2(pos.X + 4f * SpheneUI.Scale, pos.Y + ImGui.GetFrameHeight() - 3f * SpheneUI.Scale),
                new Vector2(pos.X + width - 4f * SpheneUI.Scale, pos.Y + ImGui.GetFrameHeight() - 3f * SpheneUI.Scale + h),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Accent), h * 0.5f);
        }
        SpheneUI.AttachTooltip(name);
    }

    private void DrawGrid(float panelW, float gridH)
    {
        var entries = Filtered();
        var scale = SpheneUI.Scale;
        var cell = CellLogical * scale;
        var sprite = SpriteLogical * scale;
        if (!SpheneUI.Layout.BeginScrollArea("##emoji-grid",
            new Vector2(panelW, gridH), border: true))
        {
            return;
        }
        var cols = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + 2f * scale) / (cell + 2f * scale)));
        var rows = (entries.Count + cols - 1) / cols;
        // manual virtualization: skip fully off-screen rows
        var rowH = cell + 2f * scale;
        var scrollY = ImGui.GetScrollY();
        var firstRow = Math.Max(0, (int)(scrollY / rowH) - 1);
        var lastRow = Math.Min(rows - 1, (int)((scrollY + ImGui.GetWindowHeight()) / rowH) + 1);
        if (firstRow > 0)
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + firstRow * rowH);
        }
        for (var r = firstRow; r <= lastRow; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var idx = r * cols + c;
                if (c > 0)
                {
                    ImGui.SameLine(0f, 2f * scale);
                }
                if (idx >= entries.Count)
                {
                    ImGui.Dummy(new Vector2(cell, cell));
                    continue;
                }
                DrawCell(entries[idx], cell, sprite);
            }
            if (r == lastRow && rows > lastRow + 1)
            {
                ImGui.Dummy(new Vector2(1f, (rows - lastRow - 1) * rowH));
            }
        }
        if (entries.Count == 0)
        {
            SpheneUI.Text.MutedWrapped(_search.Length > 0
                ? "No emoji matches the search."
                : "No recent emoji yet - pick a category above.");
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawCell(SpheneUI.Emoji.Entry entry, float cell, float sprite)
    {
        var scale = SpheneUI.Scale;
        var tone = _config.Current.EmojiSkinTone;
        var pos = ImGui.GetCursorScreenPos();
        var id = "##em" + entry.Seq;
        var hovered = false;
        ImGui.InvisibleButton(id, new Vector2(cell, cell));
        if (ImGui.IsItemHovered())
        {
            hovered = true;
            _hoveredName = entry.Name;
            ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(cell),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.FrameBgHover), 4f * scale);
        }
        if (SpheneUI.Emoji.TrySpriteForKey(entry.Seq, out var uv0, out var uv1))
        {
            var inset = (cell - sprite) * 0.5f;
            var smin = pos + new Vector2(inset);
            SpheneUI.Media.SpriteInRect(ImGui.GetWindowDrawList(), SpheneUI.Emoji.AtlasHandle,
                smin, smin + new Vector2(sprite), uv0, uv1);
        }
        if (hovered)
        {
            SpheneUI.AttachTooltip(":" + entry.Name.Replace(' ', '_') + ":");
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            Pick(entry);
        }
        // tone-variant sprite: draw over the base when a tone is active
        if (tone is >= 0 and <= 4 && entry.ToneCells != null
            && SpheneUI.Emoji.TrySpriteForKey(ToneKey(entry, tone), out var tuv0, out var tuv1))
        {
            var inset = (cell - sprite) * 0.5f;
            var smin = pos + new Vector2(inset);
            SpheneUI.Media.SpriteInRect(ImGui.GetWindowDrawList(), SpheneUI.Emoji.AtlasHandle,
                smin, smin + new Vector2(sprite), tuv0, tuv1);
        }
    }

    private static string ToneKey(SpheneUI.Emoji.Entry entry, int tone)
        => entry.Seq + "_" + (EmojiCore.SkinToneBase + tone).ToString("x");

    private void DrawFooter(float panelW)
    {
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        var scale = SpheneUI.Scale;
        var swatch = 20f * scale;
        // tone swatches: default + five skin tones rendered via the 🖐 cells
        DrawToneSwatch(-1, "Default skin tone", swatch);
        for (var t = 0; t < 5; t++)
        {
            ImGui.SameLine(0f, 3f * scale);
            DrawToneSwatch(t, "Skin tone " + (t + 1), swatch);
        }
        ImGui.SameLine(0f, 8f * scale);
        var name = _hoveredName ?? string.Empty;
        var maxName = panelW - (6 * swatch + 8 * 3f * scale + 8f * scale);
        SpheneUI.Text.Colored(SpheneUI.Text.Ellipsize(name, Math.Max(60f * scale, maxName)),
            SpheneUI.Colors.TextMuted);
    }

    private void DrawToneSwatch(int tone, string label, float swatch)
    {
        var scale = SpheneUI.Scale;
        var pos = ImGui.GetCursorScreenPos();
        var selected = _config.Current.EmojiSkinTone == tone;
        ImGui.InvisibleButton("##tone" + tone, new Vector2(swatch, swatch));
        if (ImGui.IsItemHovered())
        {
            ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(swatch),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.FrameBgHover), 4f * scale);
        }
        var drawn = false;
        // wave-hand base 1f44b has all five tone cells
        var key = tone < 0 ? "1f44b" : "1f44b_" + (EmojiCore.SkinToneBase + tone).ToString("x");
        if (SpheneUI.Emoji.TrySpriteForKey(key, out var uv0, out var uv1))
        {
            var inset = 1.5f * scale;
            SpheneUI.Media.SpriteInRect(ImGui.GetWindowDrawList(), SpheneUI.Emoji.AtlasHandle,
                pos + new Vector2(inset), pos + new Vector2(swatch - inset), uv0, uv1);
            drawn = true;
        }
        if (!drawn)
        {
            ImGui.GetWindowDrawList().AddRectFilled(pos + new Vector2(2f * scale),
                pos + new Vector2(swatch - 2f * scale), SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.FrameBg), 3f * scale);
        }
        if (selected)
        {
            ImGui.GetWindowDrawList().AddRect(pos, pos + new Vector2(swatch),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Accent), 4f * scale,
                ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _config.Current.EmojiSkinTone = tone;
            _config.Save();
        }
        SpheneUI.AttachTooltip(label);
    }

    private void Pick(SpheneUI.Emoji.Entry entry)
    {
        var tone = _config.Current.EmojiSkinTone;
        var text = tone is >= 0 and <= 4 && entry.ToneTexts != null
            ? entry.ToneTexts[tone]
            : entry.Glyph;
        var recents = _config.Current.RecentEmojis;
        recents.Remove(text);
        recents.Insert(0, text);
        while (recents.Count > MaxRecents)
        {
            recents.RemoveAt(recents.Count - 1);
        }
        _config.Save();
        _onPick?.Invoke(text);
        _filtered = null;
        // keep the picker open for rapid multi-pick - matches common emoji UX
    }

    private List<SpheneUI.Emoji.Entry> Filtered()
    {
        var key = _search + "\u0001" + _category;
        if (_filtered != null && string.Equals(_filterKey, key, StringComparison.Ordinal))
        {
            return _filtered;
        }
        _filterKey = key;
        var catalog = SpheneUI.Emoji.Catalog;
        var result = new List<SpheneUI.Emoji.Entry>();
        if (catalog == null)
        {
            _filtered = result;
            return result;
        }
        var query = _search.Trim();
        if (_category < 0 && query.Length == 0)
        {
            // recents view
            foreach (var text in _config.Current.RecentEmojis)
            {
                var entry = FindByText(catalog, text);
                if (entry != null)
                {
                    result.Add(entry);
                }
            }
            _filtered = result;
            return result;
        }
        var tokens = query.Length == 0
            ? null
            : query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var e in catalog)
        {
            if (_category >= 0 && e.Category != _category)
            {
                continue;
            }
            if (tokens != null && !Matches(e, tokens))
            {
                continue;
            }
            result.Add(e);
        }
        _filtered = result;
        return result;
    }

    private static bool Matches(SpheneUI.Emoji.Entry e, string[] tokens)
    {
        foreach (var t in tokens)
        {
            if (!e.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                && !e.Keywords.Contains(t, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static SpheneUI.Emoji.Entry? FindByText(IReadOnlyList<SpheneUI.Emoji.Entry> catalog, string text)
    {
        foreach (var e in catalog)
        {
            if (string.Equals(e.Glyph, text, StringComparison.Ordinal))
            {
                return e;
            }
        }
        return null;
    }
}
