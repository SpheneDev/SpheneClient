using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Sphene.UI.Styling;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Provides the Discord and Ko-fi brand logos as embedded white-silhouette
/// textures (alpha channel carries the glyph) so they can be tinted to any
/// color, plus ready-made brand-colored buttons.
/// </summary>
public static class SpheneBrandIcons
{
    /// <summary>Brand icon identifier.</summary>
    public enum Brand
    {
        Discord,
        Kofi
    }

    /// <summary>Discord blurple brand color.</summary>
    public static readonly Vector4 DiscordColor = new(0.345f, 0.396f, 0.949f, 1f);
    /// <summary>Ko-fi coral brand color.</summary>
    public static readonly Vector4 KofiColor = new(1.0f, 0.392f, 0.2f, 1f);
    /// <summary>Slightly brightened variants for hover/active states.</summary>
    public static readonly Vector4 DiscordColorHover = new(0.42f, 0.47f, 1f, 1f);
    public static readonly Vector4 KofiColorHover = new(1f, 0.52f, 0.32f, 1f);

    private const string DiscordPngBase64 =
        "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" +
        "A47VLX66y4wtAAAAAElFTkSuQmCC";

    private const string KofiPngBase64 =
        "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";

    private static readonly IDalamudTextureWrap?[] Textures = new IDalamudTextureWrap?[2];
    private static bool _loadAttempted;

    /// <summary>Returns the embedded texture for <paramref name="brand"/>, loading it lazily.</summary>
    public static IDalamudTextureWrap? GetTexture(UiSharedService uiShared, Brand brand)
    {
        if (!_loadAttempted)
        {
            _loadAttempted = true;
            try
            {
                Textures[0] = uiShared.LoadImage(Convert.FromBase64String(DiscordPngBase64));
                Textures[1] = uiShared.LoadImage(Convert.FromBase64String(KofiPngBase64));
            }
            catch
            {
                // Brand icons are cosmetic; stay null so callers skip drawing.
            }
        }
        return Textures[(int)brand];
    }

    /// <summary>Returns the brand color for <paramref name="brand"/>.</summary>
    public static Vector4 GetBrandColor(Brand brand)
        => brand == Brand.Discord ? DiscordColor : KofiColor;

    /// <summary>Returns the hover brand color for <paramref name="brand"/>.</summary>
    public static Vector4 GetBrandHoverColor(Brand brand)
        => brand == Brand.Discord ? DiscordColorHover : KofiColorHover;

    /// <summary>Draws a tinted brand glyph into <paramref name="drawList"/> inside the given rect.</summary>
    public static void DrawIcon(UiSharedService uiShared, ImDrawListPtr drawList, Brand brand,
        Vector2 min, Vector2 max, Vector4? tint = null)
    {
        var texture = GetTexture(uiShared, brand);
        if (texture == null)
            return;
        var color = tint ?? GetBrandColor(brand);
        drawList.AddImage(texture.Handle, min, max, Vector2.Zero, Vector2.One,
            ImGui.ColorConvertFloat4ToU32(color));
    }

    /// <summary>Draws a centered tinted brand glyph button of the given square size.</summary>
    /// <returns>True when the button was clicked.</returns>
    public static bool IconButton(UiSharedService uiShared, Brand brand, string id,
        float size, Vector4? tint = null, string? tooltip = null)
    {
        ImGui.PushID(id);
        var pos = ImGui.GetCursorScreenPos();
        var result = ImGui.InvisibleButton($"##brandBtn{id}", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var color = tint ?? (hovered ? GetBrandHoverColor(brand) : GetBrandColor(brand));
        var inset = size * 0.15f;
        DrawIcon(uiShared, ImGui.GetWindowDrawList(), brand,
            pos + new Vector2(inset, inset), pos + new Vector2(size - inset, size - inset), color);
        if (tooltip != null && hovered)
            UiSharedService.AttachToolTip(tooltip);
        ImGui.PopID();
        return result;
    }

    /// <summary>Computes the width of a brand icon + text button.</summary>
    public static float GetIconTextButtonSize(string text)
    {
        var iconSize = ImGui.GetTextLineHeight();
        var gap = 3f * ImGuiHelpers.GlobalScale;
        return iconSize + gap + ImGui.CalcTextSize(text).X + ImGui.GetStyle().FramePadding.X * 2f;
    }

    /// <summary>Draws a brand-colored button with the brand glyph and a text label.</summary>
    /// <returns>True when the button was clicked.</returns>
    public static bool IconTextButton(UiSharedService uiShared, Brand brand, string text,
        string id, float? width = null, string? tooltip = null)
    {
        ImGui.PushID(id);
        var iconSize = ImGui.GetTextLineHeight();
        var gap = 3f * ImGuiHelpers.GlobalScale;
        var framePad = ImGui.GetStyle().FramePadding;
        var buttonWidth = width ?? (iconSize + gap + ImGui.CalcTextSize(text).X + framePad.X * 2f);
        var buttonHeight = ImGui.GetFrameHeight();

        ImGui.PushStyleColor(ImGuiCol.Button, GetBrandColor(brand));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, GetBrandHoverColor(brand));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, GetBrandHoverColor(brand));
        var result = ImGui.Button($"##brandTextBtn{id}", new Vector2(buttonWidth, buttonHeight));
        ImGui.PopStyleColor(3);

        var pos = ImGui.GetItemRectMin();
        var iconPos = new Vector2(pos.X + framePad.X, pos.Y + (buttonHeight - iconSize) * 0.5f);
        DrawIcon(uiShared, ImGui.GetWindowDrawList(), brand,
            iconPos, iconPos + new Vector2(iconSize, iconSize), Vector4.One);

        var textPos = new Vector2(iconPos.X + iconSize + gap,
            pos.Y + (buttonHeight - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.GetWindowDrawList().AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), text);

        if (tooltip != null && ImGui.IsItemHovered())
            UiSharedService.AttachToolTip(tooltip);
        ImGui.PopID();
        return result;
    }
    /// <summary>
    /// Draws a square button with the brand color as its background and the
    /// brand glyph in white. The background brightens on hover.
    /// </summary>
    /// <returns>True when the button was clicked.</returns>
    public static bool FramedIconButton(UiSharedService uiShared, Brand brand, string id,
        float size, string? tooltip = null)
    {
        ImGui.PushID(id);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.PushStyleColor(ImGuiCol.Button, GetBrandColor(brand));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, GetBrandHoverColor(brand));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, GetBrandHoverColor(brand));
        var result = ImGui.Button($"##brandFrameBtn{id}", new Vector2(size, size));
        ImGui.PopStyleColor(3);
        var inset = size * 0.17f;
        DrawIcon(uiShared, ImGui.GetWindowDrawList(), brand,
            pos + new Vector2(inset, inset), pos + new Vector2(size - inset, size - inset), Vector4.One);
        if (tooltip != null && ImGui.IsItemHovered())
            UiSharedService.AttachToolTip(tooltip);
        ImGui.PopID();
        return result;
    }

    /// <summary>One clickable segment of a <see cref="BrandStrip"/>.</summary>
    public readonly record struct StripSegment(
        Brand Brand, string? Label = null, string? Tooltip = null, Action? OnClick = null);

    /// <summary>
    /// Draws a joined brand button strip at the cursor: contiguous segments blend
    /// their brand colors at the seams and the whole strip fades to real
    /// transparency on every edge (alpha gradient like the scroll fade).
    /// Segments may carry a white text label next to the white glyph.
    /// </summary>
    /// <param name="width">Total strip width; null auto-sizes each segment to its content.</param>
    /// <returns>The strip size so callers can align following content.</returns>
    public static Vector2 BrandStrip(UiSharedService uiShared, string id,
        IReadOnlyList<StripSegment> segments, float height, float? width = null)
    {
        var count = segments.Count;
        var scale = ImGuiHelpers.GlobalScale;
        var widths = new float[count];
        var autoTotal = 0f;
        for (var k = 0; k < count; k++)
        {
            widths[k] = SegmentWidth(segments[k], height, scale);
            autoTotal += widths[k];
        }

        var totalWidth = width ?? autoTotal;
        if (autoTotal > 0f)
        {
            var factor = totalWidth / autoTotal;
            for (var k = 0; k < count; k++)
                widths[k] *= factor;
        }

        var pos = ImGui.GetCursorScreenPos();
        var colors = new Vector4[count];
        var x = pos.X;
        for (var k = 0; k < count; k++)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, pos.Y));
            var clicked = ImGui.InvisibleButton($"##brandStrip{id}{k}", new Vector2(widths[k], height));
            var hovered = ImGui.IsItemHovered();
            colors[k] = hovered ? GetBrandHoverColor(segments[k].Brand)
                : GetBrandColor(segments[k].Brand);
            if (clicked)
            {
                segments[k].OnClick?.Invoke();
            }
            if (!string.IsNullOrEmpty(segments[k].Tooltip) && hovered)
            {
                UiSharedService.AttachToolTip(segments[k].Tooltip!);
            }
            x += widths[k];
        }
        ImGui.SetCursorScreenPos(pos + new Vector2(totalWidth, height));

        DrawStripSurface(uiShared, ImGui.GetWindowDrawList(), pos, widths, colors, segments, height);
        return new Vector2(totalWidth, height);
    }

    /// <summary>Natural width of one strip segment (icon-only or icon + label).</summary>
    private static float SegmentWidth(StripSegment segment, float height, float scale)
    {
        var iconSize = height * 0.55f;
        if (segment.Label == null)
            return height * 1.15f;
        var pad = height * 0.5f;
        return pad * 2f + iconSize + 5f * scale + ImGui.CalcTextSize(segment.Label).X;
    }

    /// <summary>
    /// Renders the strip surface as a single grid sampled from a continuous
    /// gradient: segment colors blend at seams, every edge fades to alpha zero.
    /// </summary>
    private static void DrawStripSurface(UiSharedService uiShared, ImDrawListPtr drawList,
        Vector2 pos, float[] widths, Vector4[] colors,
        IReadOnlyList<StripSegment> segments, float height)
    {
        const float edgeFadeFractionY = 0.32f;
        const float edgeFadeWidth = 10f;
        const float blendHalfWidth = 10f;
        const int fadeSteps = 3;
        var count = widths.Length;
        var totalWidth = 0f;
        var bounds = new float[count + 1];
        for (var k = 0; k < count; k++)
        {
            bounds[k] = totalWidth;
            totalWidth += widths[k];
        }
        bounds[count] = totalWidth;

        var stripMin = pos;
        var stripMax = pos + new Vector2(totalWidth, height);
        var fadeW = MathF.Min(edgeFadeWidth, totalWidth * 0.08f);
        var fadeH = height * edgeFadeFractionY;
        var blendHalf = MathF.Min(blendHalfWidth, totalWidth * 0.04f);

        // Subdivided stop lists: fade zones get several samples so the alpha
        // curve can bend (harder ramp) and corners interpolate round instead
        // of producing a diagonal cut.
        var xs = new List<float> { stripMin.X };
        for (var s = 1; s <= fadeSteps; s++)
            xs.Add(stripMin.X + fadeW * s / fadeSteps);
        for (var k = 1; k < count; k++)
        {
            xs.Add(stripMin.X + bounds[k] - blendHalf);
            xs.Add(stripMin.X + bounds[k] + blendHalf);
        }
        for (var s = fadeSteps - 1; s >= 0; s--)
            xs.Add(stripMax.X - fadeW * s / fadeSteps);
        xs.Sort();
        for (var i = xs.Count - 1; i > 0; i--)
            if (xs[i] - xs[i - 1] < 0.5f)
                xs.RemoveAt(i);

        var ys = new List<float> { stripMin.Y };
        for (var s = 1; s <= fadeSteps; s++)
            ys.Add(stripMin.Y + fadeH * s / fadeSteps);
        for (var s = fadeSteps; s >= 0; s--)
            ys.Add(stripMax.Y - fadeH * s / fadeSteps);

        for (var r = 0; r < ys.Count - 1; r++)
        {
            var rowMinY = ys[r];
            var rowMaxY = ys[r + 1];
            if (rowMaxY - rowMinY <= 0f)
                continue;
            var vT = StripVerticalAlpha(rowMinY, stripMin.Y, stripMax.Y, fadeH);
            var vB = StripVerticalAlpha(rowMaxY, stripMin.Y, stripMax.Y, fadeH);

            for (var c = 0; c < xs.Count - 1; c++)
            {
                var colMin = new Vector2(xs[c], rowMinY);
                var colMax = new Vector2(xs[c + 1], rowMaxY);
                if (colMax.X - colMin.X <= 0f)
                    continue;

                var cl = StripColorAt(colMin.X - stripMin.X, bounds, colors, blendHalf);
                var cr = StripColorAt(colMax.X - stripMin.X, bounds, colors, blendHalf);
                var aL = StripEdgeAlpha(colMin.X, stripMin.X, stripMax.X, fadeW);
                var aR = StripEdgeAlpha(colMax.X, stripMin.X, stripMax.X, fadeW);

                drawList.AddRectFilledMultiColor(colMin, colMax,
                    FadedColor(cl, aL * vT),
                    FadedColor(cr, aR * vT),
                    FadedColor(cr, aR * vB),
                    FadedColor(cl, aL * vB));
            }
        }

        // White glyphs and labels on top
        var scale = ImGuiHelpers.GlobalScale;
        var iconSize = height * 0.55f;
        var gap = 5f * scale;
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        for (var k = 0; k < count; k++)
        {
            var segMin = pos + new Vector2(bounds[k], 0f);
            var segW = widths[k];
            var label = segments[k].Label;
            var contentW = label == null ? iconSize
                : iconSize + gap + ImGui.CalcTextSize(label).X;
            var contentX = segMin.X + (segW - contentW) * 0.5f;
            var iconMin = new Vector2(contentX, segMin.Y + (height - iconSize) * 0.5f);
            DrawIcon(uiShared, drawList, segments[k].Brand,
                iconMin, iconMin + new Vector2(iconSize, iconSize), Vector4.One);
            if (label != null)
            {
                var textPos = new Vector2(contentX + iconSize + gap,
                    segMin.Y + (height - ImGui.GetTextLineHeight()) * 0.5f);
                drawList.AddText(textPos, textColor, label);
            }
        }
    }

    /// <summary>Continuous horizontal strip color: brand color per segment, lerped in seam zones.</summary>
    private static Vector4 StripColorAt(float localX, float[] bounds, Vector4[] colors, float blendHalf)
    {
        var count = colors.Length;
        var seg = count - 1;
        for (var k = 0; k < count; k++)
        {
            if (localX < bounds[k + 1])
            {
                seg = k;
                break;
            }
        }
        for (var k = 1; k < count; k++)
        {
            var seam = bounds[k];
            if (localX > seam - blendHalf && localX < seam + blendHalf)
            {
                var t = Math.Clamp((localX - seam + blendHalf) / (2f * blendHalf), 0f, 1f);
                return Vector4.Lerp(colors[k - 1], colors[k], t);
            }
        }
        return colors[seg];
    }

    /// <summary>Horizontal alpha ramp at the outer strip edges, curved for a harder fade.</summary>
    private static float StripEdgeAlpha(float x, float stripMinX, float stripMaxX, float fadeW)
    {
        if (fadeW <= 0f)
            return 1f;
        var t = x < stripMinX + fadeW ? (x - stripMinX) / fadeW
            : x > stripMaxX - fadeW ? (stripMaxX - x) / fadeW
            : 1f;
        return FadeCurve(t);
    }

    /// <summary>Vertical alpha ramp at the top/bottom strip edges, curved for a harder fade.</summary>
    private static float StripVerticalAlpha(float y, float stripMinY, float stripMaxY, float fadeH)
    {
        if (fadeH <= 0f)
            return 1f;
        var t = y < stripMinY + fadeH ? (y - stripMinY) / fadeH
            : y > stripMaxY - fadeH ? (stripMaxY - y) / fadeH
            : 1f;
        return FadeCurve(t);
    }

    /// <summary>Exponent &lt; 1 pushes opacity toward the edge so fades start hard and ease out.</summary>
    private static float FadeCurve(float t)
        => MathF.Pow(Math.Clamp(t, 0f, 1f), 0.6f);

    private static uint FadedColor(Vector4 color, float alphaFactor)
        => ImGui.ColorConvertFloat4ToU32(SpheneColors.WithAlpha(color, color.W * alphaFactor));
}
