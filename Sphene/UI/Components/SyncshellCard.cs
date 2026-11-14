using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using ImGui = Dalamud.Bindings.ImGui.ImGui;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>
/// Venue-style card primitives for syncshell surfaces · cover cards with
/// bottom scrim and status pills, row cards with thumbs, and equal-width
/// action tiles. Composes the Aetherphone-flavoured presentation of the
/// syncshell list and community pages.
/// </summary>
internal static class SyncshellCard
{
    private static readonly Dictionary<string, float> _hoverEase = new(StringComparer.Ordinal);

    /// <summary>Rounded status pill · optional live dot, tinted text on dark fill.</summary>
    /// <returns>The pill's drawn width in pixels.</returns>
    public static float StatusPill(ImDrawListPtr dl, Vector2 topLeft, string label, Vector4 tint,
        bool dot, float scale)
    {
        var padX = 8f * scale;
        var h = ImGui.GetTextLineHeight() + 6f * scale;
        var textW = ImGui.CalcTextSize(label).X;
        var dotW = dot ? 14f * scale : 0f;
        var w = padX + dotW + textW + padX;

        var min = topLeft;
        var max = topLeft + new Vector2(w, h);
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.55f)), h * 0.5f);

        var x = topLeft.X + padX;
        if (dot)
        {
            var c = new Vector2(x + 4f * scale, topLeft.Y + h * 0.5f);
            dl.AddCircleFilled(c, 4.5f * scale, SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(tint, 0.35f)));
            dl.AddCircleFilled(c, 3f * scale, SpheneUI.ColorUtils.ToU32(tint));
            x += dotW;
        }
        dl.AddText(new Vector2(x, topLeft.Y + (h - ImGui.GetTextLineHeight()) * 0.5f),
            SpheneUI.ColorUtils.ToU32(tint), label);
        return w;
    }

    /// <summary>Bottom scrim · the dark gradient that makes overlay text readable
    /// on covers. <paramref name="rounding"/> + <paramref name="corners"/> must
    /// match the silhouette of the surface underneath so the scrim cannot
    /// square off rounded card corners.</summary>
    public static void BottomScrim(ImDrawListPtr dl, Vector2 min, Vector2 max, float share,
        float rounding = 0f, ImDrawFlags corners = ImDrawFlags.RoundCornersBottom)
    {
        var top = new Vector2(min.X, max.Y - (max.Y - min.Y) * share);
        var clear = new Vector4(0f, 0f, 0f, 0f);
        var dark = new Vector4(0f, 0f, 0f, 0.82f);
        SpheneUI.Media.FillRoundedGradient(dl, top, max, clear, clear, dark, dark,
            rounding, corners);
    }

    /// <summary>
    /// Cover card for rails: image fills the card, bottom scrim, status pill
    /// top-left, title + stat line bottom-left, hover lift + accent rim.
    /// Caller positions it via cursor; returns true when the body was clicked.
    /// </summary>
    public static bool Cover(string id, Vector2 size, Func<IDalamudTextureWrap?> cover,
        string? pill, Vector4 pillTint, string title, string? stat, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var rounding = 14f * scale;
        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##cover", size);
        var hovered = ImGui.IsItemHovered();
        var restMin = ImGui.GetItemRectMin();
        var restMax = ImGui.GetItemRectMax();
        ImGui.PopID();

        // Lift: eased cover zoom inside the fixed frame + rim highlight  · 
        // the card rect never leaves the item bounds, so nothing clips.
        var ease = Ease(id, hovered);
        var min = restMin;
        var max = restMax;

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Surface), rounding);

        dl.PushClipRect(min, max, true);
        var wrap = cover();
        if (wrap != null)
        {
            SpheneUI.Media.ImageInRect(dl, wrap, min, max, ImageFit.Cover,
                rounding: rounding, zoom: 1f + 0.06f * ease);
        }
        else
        {
            // Fallback cover: accent-tinted gradient + centered icon.
            var fadeTop = SpheneUI.ColorUtils.WithAlpha(accent, 0.10f);
            var fadeBot = SpheneUI.ColorUtils.WithAlpha(accent, 0.30f);
            SpheneUI.Media.FillRoundedGradient(dl, min, max, fadeTop, fadeTop,
                fadeBot, fadeBot, rounding);
            using (SpheneUI.Icons.PushFont())
            {
                var glyph = FontAwesomeIcon.Users.ToIconString();
                var gSize = ImGui.CalcTextSize(glyph);
                dl.AddText(min + (max - min - gSize) * 0.45f,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.6f)), glyph);
            }
        }
        BottomScrim(dl, min, max, 0.66f, rounding);
        dl.PopClipRect();

        if (ease > 0.01f)
        {
            dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.30f * ease)), rounding, ImDrawFlags.None, 1.5f);
        }

        var pad = 10f * scale;
        if (!string.IsNullOrEmpty(pill))
        {
            StatusPill(dl, min + new Vector2(pad, pad), pill, pillTint,
                dot: pillTint == SpheneUI.Colors.Success, scale);
        }

        var padBottom = min.Y + (max.Y - min.Y) - pad;
        var statH = stat != null ? ImGui.GetTextLineHeight() : 0f;
        var titleY = padBottom - statH - 2f * scale - ImGui.GetTextLineHeight();
        ClipText(dl, new Vector2(min.X + pad, titleY),
            new Vector2(max.X - pad, titleY + ImGui.GetTextLineHeight()),
            title, SpheneUI.ColorUtils.ToU32(Vector4.One));
        if (stat != null)
        {
            ClipText(dl, new Vector2(min.X + pad, titleY + ImGui.GetTextLineHeight() + 2f * scale),
                new Vector2(max.X - pad, padBottom),
                stat, SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.75f)));
        }
        return clicked;
    }

    /// <summary>
    /// Venue row card: thumb left (logo/avatar or icon fallback), title + two
    /// sub-lines, optional right chevron · the grouped-list variant.
    /// </summary>
    public static bool Row(string id, float width, IDalamudTextureWrap? thumb,
        FontAwesomeIcon fallbackIcon, Vector4 accent, string title, string? sub,
        string? third, Vector4 thirdTint, bool chevron = true)
    {
        var scale = SpheneUI.Scale;
        var pad = 10f * scale;
        var thumbSide = 38f * scale;
        var lineH = ImGui.GetTextLineHeight();
        var rowH = MathF.Max(thumbSide + pad * 1.6f,
            lineH + (sub != null ? lineH : 0f) + (third != null ? lineH * 0.9f : 0f) + pad * 1.4f);

        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##row", new Vector2(width, rowH));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.PopID();

        var dl = ImGui.GetWindowDrawList();
        var radius = SpheneUI.Metrics.ButtonRadius;
        if (hovered)
        {
            dl.AddRectFilled(min, max,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.07f)), radius);
        }

        var cy = min.Y + rowH * 0.5f;
        var thumbMin = new Vector2(min.X + pad, cy - thumbSide * 0.5f);
        var thumbMax = thumbMin + new Vector2(thumbSide, thumbSide);
        var thumbR = thumbSide * 0.3f;
        if (thumb != null)
        {
            SpheneUI.Media.ImageInRect(dl, thumb, thumbMin, thumbMax, ImageFit.Cover,
                rounding: thumbR);
            dl.AddRect(thumbMin, thumbMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Border with { W = 0.3f }), thumbR);
        }
        else
        {
            SpheneUI.Layout.IconPlaceholder(dl, thumbMin, thumbSide, fallbackIcon,
                SpheneUI.Colors.TextMuted, thumbR);
        }

        var textX = thumbMax.X + 10f * scale;
        var chevW = chevron ? 18f * scale : 0f;
        var textW = max.X - pad - chevW - textX;
        var thirdH = third != null ? lineH * 0.9f : 0f;
        var subH = sub != null ? lineH : 0f;
        var blockTop = cy - (lineH + subH + thirdH + 2f * scale) * 0.5f;

        ClipText(dl, new Vector2(textX, blockTop), new Vector2(textX + textW, blockTop + lineH),
            title, SpheneUI.ColorUtils.ToU32(Vector4.One));
        if (sub != null)
        {
            ClipText(dl, new Vector2(textX, blockTop + lineH + 1f * scale),
                new Vector2(textX + textW, blockTop + lineH + 1f * scale + subH),
                sub, SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted));
        }
        if (third != null)
        {
            ClipText(dl, new Vector2(textX, blockTop + lineH + subH + 2f * scale),
                new Vector2(textX + textW, blockTop + lineH + subH + 2f * scale + thirdH),
                third, SpheneUI.ColorUtils.ToU32(thirdTint));
        }

        if (chevron)
        {
            using (SpheneUI.Icons.PushFont())
            {
                var g = FontAwesomeIcon.ChevronRight.ToIconString();
                dl.AddText(new Vector2(max.X - pad - ImGui.CalcTextSize(g).X,
                        cy - ImGui.CalcTextSize(g).Y * 0.5f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), g);
            }
        }
        return clicked;
    }

    /// <summary>
    /// Equal-width action tile row (the venue detail "Go There" band) · first
    /// tile is the prominent accent fill, the rest are ghosted. Returns the
    /// index of the tile clicked or -1.
    /// </summary>
    public static int ActionTiles(string id, (FontAwesomeIcon Icon, string Label)[] tiles, Vector4 accent)
    {
        if (tiles.Length == 0)
        {
            return -1;
        }
        var scale = SpheneUI.Scale;
        var gap = 8f * scale;
        var tileH = ImGui.GetTextLineHeight() * 1.9f + 18f * scale;
        var avail = SpheneUI.Layout.CardAlignedAvailX();
        var tileW = (avail - gap * (tiles.Length - 1)) / tiles.Length;
        var hit = -1;
        var dl = ImGui.GetWindowDrawList();

        for (var i = 0; i < tiles.Length; i++)
        {
            var min = ImGui.GetCursorScreenPos();
            ImGui.PushID(id + i);
            if (ImGui.InvisibleButton("##tile", new Vector2(tileW, tileH)))
            {
                hit = i;
            }
            var hovered = ImGui.IsItemHovered();
            var max = ImGui.GetItemRectMax();
            ImGui.PopID();

            var radius = 10f * scale;
            var primary = i == 0;
            if (primary)
            {
                dl.AddRectFilled(min, max,
                    SpheneUI.ColorUtils.ToU32(hovered
                        ? SpheneUI.ColorUtils.LerpColor(accent, Vector4.One, 0.12f)
                        : accent), radius);
            }
            else
            {
                dl.AddRectFilled(min, max,
                    SpheneUI.ColorUtils.ToU32(hovered
                        ? SpheneUI.ColorUtils.WithAlpha(accent, 0.16f)
                        : SpheneUI.ColorUtils.WithAlpha(accent, 0.08f)), radius);
            }

            var labelH = ImGui.GetTextLineHeight();
            var glyphH = ImGui.GetTextLineHeight() + 4f * scale;
            var blockH = glyphH + 2f * scale + labelH;
            var top = min.Y + (tileH - blockH) * 0.5f;
            var centerX = (min.X + max.X) * 0.5f;
            var ink = primary ? Vector4.One : SpheneUI.ColorUtils.LerpColor(accent, Vector4.One, 0.2f);
            using (SpheneUI.Icons.PushFont())
            {
                var g = tiles[i].Icon.ToIconString();
                dl.AddText(new Vector2(centerX - ImGui.CalcTextSize(g).X * 0.5f, top),
                    SpheneUI.ColorUtils.ToU32(ink), g);
            }
            var lw = ImGui.CalcTextSize(tiles[i].Label).X;
            dl.AddText(new Vector2(centerX - lw * 0.5f, top + glyphH + 2f * scale),
                SpheneUI.ColorUtils.ToU32(primary ? Vector4.One : ink), tiles[i].Label);
            if (i < tiles.Length - 1)
            {
                ImGui.SameLine(0f, gap);
            }
        }
        ImGui.NewLine();
        return hit;
    }

    /// <summary>Single-line clipped text · PushClipRect + AddText.</summary>
    private static void ClipText(ImDrawListPtr dl, Vector2 min, Vector2 max, string text, uint col)
    {
        dl.PushClipRect(min, max, true);
        dl.AddText(min, col, text);
        dl.PopClipRect();
    }

    private static float Ease(string id, bool hovered)
    {
        _hoverEase.TryGetValue(id, out var t);
        var dt = MathF.Min(ImGui.GetIO().DeltaTime, 0.1f) * 9f;
        t = hovered ? MathF.Min(1f, t + dt) : MathF.Max(0f, t - dt);
        _hoverEase[id] = t;
        return t;
    }
}
