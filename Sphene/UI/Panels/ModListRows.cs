using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

/// <summary>
/// Shared rich list-row renderer for the Mod Sharing surfaces: hover/accent fx,
/// a rounded thumbnail (image or themed icon placeholder), an ellipsized title
/// line and a muted meta line below it. Prepared so mod artwork, authors and
/// versions can flow in as soon as they are known — callers only pass what they
/// have and the row degrades gracefully.
/// </summary>
internal static class ModListRows
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    public readonly record struct Result(bool Clicked, bool Hovered, Vector2 TrailingPos, Vector2 RowEnd);

    /// <summary>Full row height including padding.</summary>
    public static float Height => 48f * SpheneUI.Scale;

    /// <summary>Square thumbnail size inside the row.</summary>
    public static float ThumbSize => 38f * SpheneUI.Scale;

    /// <summary>
    /// Draws one list row at the cursor and advances past it. Trailing widgets
    /// (checkboxes, badges, buttons) are drawn by the caller afterwards at
    /// <see cref="Result.TrailingPos"/> — widgets submitted later win input over
    /// the row's invisible button inside their own rect. After trailing content,
    /// restore the cursor with <see cref="Result.RowEnd"/>.
    /// <paramref name="trailingReserve"/> shrinks the text zone so ellipsized
    /// text never collides with the trailing content.
    /// <paramref name="checkIndicator"/> draws a selection square at the
    /// trailing edge — use for whole-row toggle semantics (click anywhere).
    /// </summary>
    public static Result Draw(string id, string title, string? meta,
        IDalamudTextureWrap? thumb, FontAwesomeIcon placeholderIcon,
        bool selected, Vector4 accent, float trailingReserve = 0f,
        bool checkIndicator = false, bool enabled = true)
    {
        var scale = SpheneUI.Scale;
        var rowH = Height;
        var width = SpheneUI.Controls.BoundedAvailX();
        var pos = ImGui.GetCursorScreenPos();
        var max = pos + new Vector2(width, rowH);
        var drawList = ImGui.GetWindowDrawList();

        var clicked = false;
        var hovered = false;
        var rowId = ImGui.GetID($"##modrow{id}");
        if (enabled)
        {
            ImGui.InvisibleButton($"##modrow{id}", new Vector2(width, rowH));
            clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
            hovered = ImGui.IsItemHovered();
        }
        else
        {
            ImGui.Dummy(new Vector2(width, rowH));
        }

        var hoverT = SpheneUI.Fx.HoverT(rowId, hovered);
        SpheneUI.Fx.HoverRow(drawList, pos, max, accent,
            MathF.Max(hoverT, selected ? 1f : 0f), 6f * scale);

        var thumbSize = ThumbSize;
        var thumbMin = pos + new Vector2(5f * scale, (rowH - thumbSize) * 0.5f);
        var thumbMax = thumbMin + new Vector2(thumbSize);
        var rounding = thumbSize * 0.18f;
        if (thumb != null)
        {
            SpheneUI.Media.ImageInRect(drawList, thumb, thumbMin, thumbMax,
                ImageFit.Cover, tint: null, rounding: rounding, border: true);
        }
        else
        {
            SpheneUI.Media.PlaceholderInRect(drawList, thumbMin, thumbMax,
                rounding, border: true, icon: placeholderIcon);
        }

        var checkW = checkIndicator ? 20f * scale : 0f;
        var textX = thumbMax.X + 8f * scale;
        var rightX = max.X - 8f * scale - MathF.Max(trailingReserve, checkW);
        var lineH = ImGui.GetTextLineHeight();
        var titleCol = enabled ? SpheneUI.Colors.TextPrimary : SpheneUI.Colors.TextMuted;
        var metaCol = SpheneUI.Colors.TextMuted;

        if (string.IsNullOrEmpty(meta))
        {
            drawList.AddText(new Vector2(textX, pos.Y + (rowH - lineH) * 0.5f),
                SpheneUI.ColorUtils.ToU32(titleCol), SpheneUI.Text.Ellipsize(title, MathF.Max(0f, rightX - textX)));
        }
        else
        {
            var titleY = pos.Y + (rowH - lineH * 2f - 2f * scale) * 0.5f;
            drawList.AddText(new Vector2(textX, titleY),
                SpheneUI.ColorUtils.ToU32(titleCol), SpheneUI.Text.Ellipsize(title, MathF.Max(0f, rightX - textX)));
            drawList.AddText(new Vector2(textX, titleY + lineH + 2f * scale),
                SpheneUI.ColorUtils.ToU32(metaCol), SpheneUI.Text.Ellipsize(meta, MathF.Max(0f, rightX - textX)));
        }

        if (checkIndicator)
        {
            var box = 14f * scale;
            var boxMin = new Vector2(max.X - 8f * scale - box, pos.Y + (rowH - box) * 0.5f);
            var boxMax = boxMin + new Vector2(box);
            if (selected)
            {
                drawList.AddRectFilled(boxMin, boxMax, SpheneUI.ColorUtils.ToU32(accent), 3f * scale);
                using (SpheneUI.Icons.PushFont())
                {
                    var glyph = FontAwesomeIcon.Check.ToIconString();
                    var glyphSize = ImGui.CalcTextSize(glyph);
                    drawList.AddText(boxMin + (new Vector2(box) - glyphSize) * 0.5f,
                        SpheneUI.ColorUtils.ToU32(Vector4.One), glyph);
                }
            }
            else
            {
                drawList.AddRect(boxMin, boxMax,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Border with { W = 0.5f }), 3f * scale);
            }
        }

        var trailingPos = new Vector2(max.X - 8f * scale - trailingReserve,
            pos.Y + (rowH - ImGui.GetFrameHeight()) * 0.5f);
        return new Result(clicked, hovered, trailingPos, pos + new Vector2(0f, rowH));
    }

    /// <summary>Joins non-empty parts into a "A · B · C" meta line.</summary>
    public static string Meta(params string?[] parts)
    {
        var span = parts.AsSpan();
        var count = 0;
        foreach (var p in span)
        {
            if (!string.IsNullOrWhiteSpace(p)) count++;
        }
        if (count == 0) return string.Empty;
        var partsArr = new string[count];
        var i = 0;
        foreach (var p in span)
        {
            if (!string.IsNullOrWhiteSpace(p)) partsArr[i++] = p!.Trim();
        }
        return string.Join(" · ", partsArr);
    }
}
