using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    public static partial class Layout
    {
        /// <summary>
        /// Bare icon glyph centered in an invisible <paramref name="side"/>-sized
        /// slot - the standard icon treatment: no fill, border or plate, the
        /// slot only guarantees a shared title column. Draws into
        /// <paramref name="drawList"/> at <paramref name="min"/>; no cursor
        /// advancement. For missing-media/avatar fallback slots use
        /// <see cref="IconPlaceholder"/> instead.
        /// </summary>
        public static void SlotIcon(ImDrawListPtr drawList, Vector2 min, float side,
            FontAwesomeIcon icon, Vector4 color)
        {
            var glyph = icon.ToIconString();
            var glyphH = MathF.Max(ImGui.GetTextLineHeight(), side * 0.6f);
            var glyphW = glyphH * (ImGui.CalcTextSize(glyph).X
                / MathF.Max(1f, ImGui.GetTextLineHeight()));
            var pos = min + (new Vector2(side, side)
                - new Vector2(glyphW, glyphH)) * 0.5f;
            drawList.AddText(UiBuilder.IconFont, glyphH, pos,
                ColorUtils.ToU32(color), glyph);
        }

        /// <summary>
        /// Neutral media/avatar fallback placeholder: a tonal
        /// <see cref="NavRailBandColor"/> rounded rect with a hairline border
        /// and a faint centered glyph - marks "no image", never a decorative
        /// tile. Draws into <paramref name="drawList"/> at
        /// <paramref name="min"/>; no cursor advancement.
        /// </summary>
        public static void IconPlaceholder(ImDrawListPtr drawList, Vector2 min, float side,
            FontAwesomeIcon icon, Vector4 tint, float? radius = null)
        {
            var scale = SpheneUI.Scale;
            var max = min + new Vector2(side, side);
            var r = radius ?? MathF.Min(Metrics.ControlRadius, side * 0.3f);
            drawList.AddRectFilled(min, max,
                ColorUtils.ToU32(NavRailBandColor()), r);
            drawList.AddRect(min, max,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Border, 0.30f)), r,
                ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
            var glyph = icon.ToIconString();
            var glyphH = MathF.Max(ImGui.GetTextLineHeight(), side * 0.55f);
            var glyphW = glyphH * (ImGui.CalcTextSize(glyph).X
                / MathF.Max(1f, ImGui.GetTextLineHeight()));
            drawList.AddText(UiBuilder.IconFont, glyphH,
                min + (new Vector2(side, side)
                    - new Vector2(glyphW, glyphH)) * 0.5f,
                ColorUtils.ToU32(ColorUtils.WithAlpha(tint, 0.45f)), glyph);
        }

        /// <summary>
        /// Feature tile · a clickable card in the dashboard language: a bare
        /// icon glyph on the left, title plus an optional muted subtitle
        /// beside it and a trailing chevron on the right. The glyph idles in
        /// <see cref="Colors.TextSecondary"/> and eases toward the tile tint
        /// on hover; the card edge carries the affordance. The whole tile is
        /// one hit area with an eased hover lift.
        /// <paramref name="width"/> ≤ 0 stretches to the bounded content
        /// width; pass an explicit width for side-by-side tiles.
        /// Returns true when clicked.
        /// </summary>
        public static bool FeatureTile(string id, FontAwesomeIcon icon, Vector4 tint,
            string title, string? sub = null, float width = -1f, bool showChevron = true)
        {
            var scale = SpheneUI.Scale;
            var drawList = ImGui.GetWindowDrawList();
            var pad = Spacing.CardPadding.Scaled * 0.75f;
            var slot = 28f * scale;
            var lineH = ImGui.GetTextLineHeight();
            var h = pad * 2f + slot + (sub != null ? 3f * scale + lineH : 0f);
            var w = width > 0f ? width : Controls.BoundedAvailX();
            var radius = Metrics.ControlRadius;

            ImGui.PushID(id);
            var clicked = ImGui.InvisibleButton("##ftile", new Vector2(w, h));
            var hovered = ImGui.IsItemHovered();
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.PopID();
            Lint.CheckBounds(id);

            drawList.AddRectFilled(min, max,
                ColorUtils.ToU32(NavRailBandColor()), radius);
            drawList.AddRect(min, max,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Border, hovered ? 0.45f : 0.28f)),
                radius);
            var lift = Fx.HoverLiftT(ImGui.GetID($"{id}##lift"), min, max);
            Fx.HoverLift(drawList, min, max, lift, radius, tint);

            var slotMin = new Vector2(min.X + pad, min.Y + pad);
            var slotMax = slotMin + new Vector2(slot, slot);
            var glyphCol = ColorUtils.LerpColor(Colors.TextSecondary, tint, 0.3f + 0.7f * lift);
            SlotIcon(drawList, slotMin, slot, icon, glyphCol);

            var chev = FontAwesomeIcon.ChevronRight.ToIconString();
            float chevW;
            using (Icons.PushFont())
            {
                chevW = ImGui.CalcTextSize(chev).X;
            }
            var textX = slotMax.X + Spacing.Row.Scaled;
            var textRight = max.X - pad - (showChevron ? chevW + Spacing.Row.Scaled : 0f);
            var titleY = slotMin.Y + (slot - lineH) * 0.5f;
            drawList.PushClipRect(new Vector2(textX, min.Y), new Vector2(textRight, max.Y), true);
            drawList.AddText(new Vector2(textX, titleY),
                ColorUtils.ToU32(Colors.TextPrimary), title);
            if (sub != null)
            {
                drawList.AddText(new Vector2(textX, slotMax.Y + 3f * scale),
                    ColorUtils.ToU32(Colors.TextMuted),
                    Text.Ellipsize(sub, textRight - textX));
            }
            drawList.PopClipRect();

            if (showChevron)
            {
                using (Icons.PushFont())
                {
                    var chevCol = hovered
                        ? ColorUtils.LerpColor(tint, Vector4.One, 0.3f)
                        : Colors.TextMuted;
                    drawList.AddText(new Vector2(max.X - pad - chevW,
                            min.Y + (h - lineH) * 0.5f),
                        ColorUtils.ToU32(chevCol), chev);
                }
            }
            return clicked;
        }
    }
}
