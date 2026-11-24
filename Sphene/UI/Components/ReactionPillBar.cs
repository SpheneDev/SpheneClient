using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>
/// The one reactions capsule - a rounded pill holding emoji chips (sprite +
/// count, own reactions accent-lit) with a trailing ghost smiley+plus
/// add button (the Discord/Slack/Material add-reaction idiom). Shared by feed rows and comment-thread reaction bars so the
/// reaction surface looks and behaves identically everywhere. Rendering is
/// drawlist-based like the feed rows: hit zones come back as screen rects
/// and the host routes clicks (row buttons or an overlay invisible button).
/// Chips clamp to the available width; hidden ones collapse into a muted
/// overflow count before the add button.
/// </summary>
internal static class ReactionPillBar
{
    /// <summary>Vertical air inside the capsule, scaled - total height is
    /// lineH + 2×pad.</summary>
    public static float PadY(float scale) => 3.5f * scale;

    public static float Height(float lineH, float scale)
        => lineH + 2f * PadY(scale);

    /// <summary>Noto Sans cap-height ratio - digit ink spans baseline-capH
    /// to baseline, so optical centering needs the cap geometry, not the
    /// line box.</summary>
    private const float CapHeightRatio = 0.71f;

    /// <summary>AddText y position so digit/capital glyph ink lands
    /// optically centered on <paramref name="centerY"/> - plain line-box
    /// centering leaves digits floating high because the box carries
    /// ascender/descender air the digits never use.</summary>
    public static float OpticalTextY(float centerY, float lineH)
    {
        var ascent = ImGui.GetFont().Ascent;
        var capH = lineH * CapHeightRatio;
        return centerY - (ascent - capH * 0.5f);
    }

    /// <summary>Width of a FontAwesome icon rendered at pill glyph height.
    /// Measure and draw share this ratio so capsules wrap exactly.</summary>
    public static float CenteredIconWidth(FontAwesomeIcon icon, float lineH)
        => IconGlyphH(lineH) * IconWidthRatio(icon);

    /// <summary>Glyph height for icons inside pills - slightly under the
    /// line height so strokes keep air to the capsule edge.</summary>
    public static float IconGlyphH(float lineH) => lineH * 0.90f;

    /// <summary>Width-to-height ratio of the icon glyph measured in the
    /// icon font at natural size.</summary>
    private static float IconWidthRatio(FontAwesomeIcon icon)
    {
        using (SpheneUI.Icons.PushFont())
        {
            return ImGui.CalcTextSize(icon.ToIconString()).X
                / MathF.Max(1f, ImGui.GetTextLineHeight());
        }
    }

    /// <summary>Draws a FontAwesome icon with its em box centered on
    /// <paramref name="centerY"/> at x; returns the consumed width. Every
    /// icon shares the same geometric treatment, so per-glyph shapes
    /// (tall thumb, wide eye) stay mutually aligned.</summary>
    public static float DrawCenteredIcon(ImDrawListPtr drawList, FontAwesomeIcon icon,
        float x, float centerY, float lineH, uint col)
    {
        var glyphH = IconGlyphH(lineH);
        var w = glyphH * IconWidthRatio(icon);
        drawList.AddText(UiBuilder.IconFont, glyphH,
            new Vector2(x, centerY - glyphH * 0.5f), col, icon.ToIconString());
        return w;
    }

    /// <summary>The width the reactions capsule will occupy for these
    /// chips within <paramref name="maxWidth"/> - runs the same trailing-
    /// chip drop pass as Draw, so hit zones sized from it always match the
    /// rendered capsule. Hosts need this before submitting overlay buttons
    /// (the overlay must span the pill exactly, never the whole line -
    /// full-width overlays push same-line followers off the card edge).</summary>
    public static float MeasureWidth(float lineH, float scale,
        IReadOnlyList<(string Emoji, int Count, bool ByMe)> chips, bool clickable,
        float maxWidth)
        => Measure(lineH, scale, chips, clickable, maxWidth).Width;

    /// <summary>Capsule width plus the leading-chip count surviving the
    /// width clamp - the single layout computation both Draw and overlay
    /// sizing use, so hit zones can never diverge from the rendering.</summary>
    private static (float Width, int Visible) Measure(float lineH, float scale,
        IReadOnlyList<(string Emoji, int Count, bool ByMe)> chips, bool clickable,
        float maxWidth)
    {
        var iconGap = 4f * scale;
        var segGap = 6f * scale;
        var padX = 8f * scale;
        var addW = clickable ? AddWidth(lineH, scale) : 0f;
        var widths = new List<float>(chips.Count);
        foreach (var chip in chips)
        {
            var countText = chip.Count > 1 ? chip.Count.ToString() : null;
            widths.Add(lineH + (countText != null
                ? iconGap + ImGui.CalcTextSize(countText).X : 0f));
        }
        float chipsW = 0f;
        for (var i = 0; i < widths.Count; i++)
        {
            chipsW += widths[i] + (i > 0 ? segGap : 0f);
        }
        float PillWidth()
        {
            var inner = chipsW + (addW > 0f && chipsW > 0f ? segGap : 0f) + addW;
            return padX * 2f + inner;
        }
        while (widths.Count > 0 && PillWidth() > maxWidth)
        {
            chipsW -= widths[^1] + (widths.Count > 1 ? segGap : 0f);
            widths.RemoveAt(widths.Count - 1);
        }
        if (widths.Count == 0 && addW <= 0f) return (0f, 0);
        var pillW = MathF.Min(PillWidth(), maxWidth);
        if (addW > 0f && padX * 2f + addW > maxWidth && widths.Count == 0)
        {
            pillW = padX * 2f + addW;
        }
        return (pillW, widths.Count);
    }

    /// <summary>Add-affordance width - the smiley+plus composite plus its
    /// margins. Shared by measure and draw.</summary>
    private static float AddWidth(float lineH, float scale)
    {
        var smileyGlyph = FontAwesomeIcon.Smile.ToIconString();
        var smileyH = lineH * 0.80f;
        var smileyW = smileyH * (ImGui.CalcTextSize(smileyGlyph).X
            / MathF.Max(1f, ImGui.GetTextLineHeight()));
        var plusH = smileyH * 0.72f;
        var plusW = plusH * (ImGui.CalcTextSize("+").X
            / MathF.Max(1f, ImGui.GetTextLineHeight()));
        return smileyW + plusW * 0.75f + 5f * scale * 2f;
    }

    /// <summary>Draws the capsule at <paramref name="pos"/> and reports the
    /// add-button hit rect plus one rect per visible chip. Returns the
    /// capsule width actually used (clamped to <paramref name="maxWidth"/>).</summary>
    public static float Draw(ImDrawListPtr drawList, Vector2 pos, float maxWidth,
        float lineH, float scale, Vector4 accent,
        IReadOnlyList<(string Emoji, int Count, bool ByMe)> chips, bool clickable,
        out Vector4 addRect, List<(string Emoji, Vector4 Rect)>? chipRects)
    {
        var mutedU = SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted);
        var accentU = SpheneUI.ColorUtils.ToU32(accent);
        var surfaceU = SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Surface);
        var borderU = SpheneUI.ColorUtils.ToU32(new Vector4(
            SpheneUI.Colors.Border.X, SpheneUI.Colors.Border.Y,
            SpheneUI.Colors.Border.Z, 0.35f));
        var padY = PadY(scale);
        var pillH = lineH + 2f * padY;
        var contentY = pos.Y + padY;
        var centerY = pos.Y + pillH * 0.5f;
        var iconGap = 4f * scale;
        var segGap = 6f * scale;
        var padX = 8f * scale;
        var chipH = lineH;
        addRect = Vector4.Zero;

        // smiley+plus composite (Material add_reaction idiom): quiet and
        // compact - the smiley reads "emoji", the small plus reads "add"
        var smileyGlyph = FontAwesomeIcon.Smile.ToIconString();
        var smileyH = chipH * 0.80f;
        var smileyW = smileyH * (ImGui.CalcTextSize(smileyGlyph).X
            / MathF.Max(1f, ImGui.GetTextLineHeight()));
        var plusH = smileyH * 0.72f;
        var plusW = plusH * (ImGui.CalcTextSize("+").X
            / MathF.Max(1f, ImGui.GetTextLineHeight()));
        var addMargin = 5f * scale;
        var addW = clickable ? smileyW + plusW * 0.75f + addMargin * 2f : 0f;
        // the one layout computation - overlay sizing and drawing share it,
        // so hit zones can never diverge from the rendered capsule
        var measured = Measure(lineH, scale, chips, clickable, maxWidth);
        var pillW = measured.Width;
        if (pillW <= 0f) return 0f;
        // the surviving leading chips, sized for the layout pass
        var widths = new List<float>(measured.Visible);
        for (var c = 0; c < measured.Visible; c++)
        {
            var ct = chips[c].Count > 1 ? chips[c].Count.ToString() : null;
            widths.Add(chipH + (ct != null
                ? iconGap + ImGui.CalcTextSize(ct).X : 0f));
        }

        drawList.AddRectFilled(pos, pos + new Vector2(pillW, pillH), surfaceU, pillH * 0.5f);
        drawList.AddRect(pos, pos + new Vector2(pillW, pillH), borderU,
            pillH * 0.5f, ImDrawFlags.None, 1f);

        var rx = pos.X + padX;
        for (var c = 0; c < widths.Count; c++)
        {
            var chip = chips[c];
            var cw = widths[c];
            var countText = chip.Count > 1 ? chip.Count.ToString() : null;
            var chipHovered = clickable && ImGui.IsMouseHoveringRect(
                new Vector2(rx, pos.Y), new Vector2(rx + cw, pos.Y + pillH));
            if (chipHovered || chip.ByMe)
            {
                drawList.AddRectFilled(new Vector2(rx - 2f * scale, pos.Y + 2f),
                    new Vector2(rx + cw + 2f * scale, pos.Y + pillH - 2f),
                    SpheneUI.ColorUtils.ToU32(new Vector4(accent.X, accent.Y,
                        accent.Z, accent.W * (chipHovered ? 0.28f : 0.14f))),
                    3f * scale);
            }
            if (chipHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            SpheneUI.Emoji.SpriteAt(drawList, new Vector2(rx, contentY), chipH, chip.Emoji);
            if (countText != null)
            {
                drawList.AddText(new Vector2(rx + chipH + iconGap,
                    OpticalTextY(centerY, lineH)),
                    chipHovered || chip.ByMe ? accentU : mutedU, countText);
            }
            if (clickable)
            {
                chipRects?.Add((chip.Emoji, new Vector4(rx, pos.Y, rx + cw, pos.Y + pillH)));
            }
            rx += cw + segGap;
        }
        var hidden = chips.Count - widths.Count;
        if (hidden > 0)
        {
            var more = hidden + "...";
            drawList.AddText(new Vector2(rx, OpticalTextY(centerY, lineH)), mutedU, more);
            rx += ImGui.CalcTextSize(more).X + segGap;
        }
        if (clickable)
        {
            // ghost add-reaction button: muted smiley+plus at rest, a faint
            // tonal plate and accent glyphs on hover - quiet in the pill but
            // unmistakably interactive once approached
            var addX = rx;
            var addHovered = ImGui.IsMouseHoveringRect(
                new Vector2(addX, pos.Y + 1f), new Vector2(addX + addW, pos.Y + pillH - 1f));
            var glyphCol = addHovered ? accentU : mutedU;
            if (addHovered)
            {
                drawList.AddRectFilled(new Vector2(addX, pos.Y + 2f),
                    new Vector2(addX + addW, pos.Y + pillH - 2f),
                    SpheneUI.ColorUtils.ToU32(new Vector4(accent.X, accent.Y,
                        accent.Z, accent.W * 0.16f)), (pillH - 4f) * 0.5f);
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            var smileyX = addX + addMargin;
            var smileyY = centerY - smileyH * 0.5f;
            drawList.AddText(UiBuilder.IconFont, smileyH,
                new Vector2(smileyX, smileyY), glyphCol, smileyGlyph);
            // plus badge overlapping the smiley's lower right (the Material
            // add_reaction composition) - large enough to read as a plus
            drawList.AddText(ImGui.GetFont(), plusH,
                new Vector2(smileyX + smileyW - plusW * 0.55f,
                    smileyY + smileyH - plusH * 0.78f),
                glyphCol, "+");
            addRect = new Vector4(addX, pos.Y, addX + addW, pos.Y + pillH);
        }
        return pillW;
    }

    /// <summary>One stats-capsule segment: icon with optional count. Active
    /// renders accent (the viewer's like); clickable segments report a hit
    /// rect and tint on hover, passive ones stay muted.</summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Auto)]
    public readonly record struct StatsSegment(
        FontAwesomeIcon Icon, int? Count, bool Active, bool Clickable);

    /// <summary>Width the stats capsule will occupy - measure and draw
    /// share this so overlay hit zones match the rendering exactly.</summary>
    public static float MeasureStatsWidth(float lineH, float scale,
        IReadOnlyList<StatsSegment> segments)
    {
        var iconGap = 4f * scale;
        var segGap = 10f * scale;
        var padX = 8f * scale;
        var w = padX * 2f;
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var num = s.Count is > 0 ? s.Count.Value.ToString() : null;
            w += CenteredIconWidth(s.Icon, lineH)
                + (num != null ? iconGap + ImGui.CalcTextSize(num).X : 0f)
                + (i > 0 ? segGap : 0f);
        }
        return w;
    }

    /// <summary>Draws the stats capsule (like/comment/views idiom) at
    /// <paramref name="pos"/>; clickable segments report hit rects as
    /// (slot index, screen rect) for the host's click routing. Returns the
    /// capsule width. Shares the pill geometry, centerline and optical
    /// centering of the reactions capsule - the two read as one family.</summary>
    public static float DrawStats(ImDrawListPtr drawList, Vector2 pos,
        float lineH, float scale, Vector4 accent,
        IReadOnlyList<StatsSegment> segments,
        List<(int Slot, Vector4 Rect)>? hitRects)
    {
        var mutedU = SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted);
        var accentU = SpheneUI.ColorUtils.ToU32(accent);
        var surfaceU = SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Surface);
        var borderU = SpheneUI.ColorUtils.ToU32(new Vector4(
            SpheneUI.Colors.Border.X, SpheneUI.Colors.Border.Y,
            SpheneUI.Colors.Border.Z, 0.35f));
        var iconGap = 4f * scale;
        var segGap = 10f * scale;
        var padX = 8f * scale;
        var pillH = Height(lineH, scale);
        var centerY = pos.Y + pillH * 0.5f;
        var pillW = MeasureStatsWidth(lineH, scale, segments);

        drawList.AddRectFilled(pos, pos + new Vector2(pillW, pillH),
            surfaceU, pillH * 0.5f);
        drawList.AddRect(pos, pos + new Vector2(pillW, pillH), borderU,
            pillH * 0.5f, ImDrawFlags.None, 1f);

        var cx = pos.X + padX;
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var num = s.Count is > 0 ? s.Count.Value.ToString() : null;
            var segW = CenteredIconWidth(s.Icon, lineH)
                + (num != null ? iconGap + ImGui.CalcTextSize(num).X : 0f);
            var hovered = s.Clickable && ImGui.IsMouseHoveringRect(
                new Vector2(cx, pos.Y), new Vector2(cx + segW, pos.Y + pillH));
            var col = hovered || s.Active ? accentU : mutedU;
            var gw = DrawCenteredIcon(drawList, s.Icon, cx, centerY, lineH, col);
            if (num != null)
            {
                drawList.AddText(new Vector2(cx + gw + iconGap,
                    OpticalTextY(centerY, lineH)), col, num);
            }
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            if (s.Clickable)
            {
                hitRects?.Add((i, new Vector4(cx, pos.Y, cx + segW, pos.Y + pillH)));
            }
            cx += segW + segGap;
        }
        return pillW;
    }
}
