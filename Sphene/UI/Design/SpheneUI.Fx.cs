using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Reusable micro-animation primitives for interactive rows and cards.
    /// Animations are eased per widget id and driven by DeltaTime, so they stay
    /// smooth regardless of framerate and need no per-widget state on the
    /// caller side. Profile surfaces pick effects through
    /// <see cref="Profile.RowFx"/> using the owner's theme.
    /// </summary>
    public static class Fx
    {
        private static readonly Dictionary<uint, float> HoverStates = new();

        /// <summary>
        /// Smoothed 0..1 hover progress for the widget with <paramref name="id"/>
        /// (typically <see cref="ImGui.GetID"/> with the button's label).
        /// Ramps in while <paramref name="hovered"/>, eases back out otherwise.
        /// </summary>
        public static float HoverT(uint id, bool hovered, float speed = 9f)
        {
            var dt = ImGui.GetIO().DeltaTime;
            HoverStates.TryGetValue(id, out var t);
            t = Math.Clamp(t + (hovered ? dt : -dt) * speed, 0f, 1f);
            HoverStates[id] = t;
            // smoothstep — fast in, soft out
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Standard row/card hover treatment driven by <see cref="HoverT"/>:
        /// a light fill and accent border that fade in — personal accent
        /// colors make this read as "their" color when used on
        /// friend/character rows.
        /// </summary>
        public static void HoverRow(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            Vector4 accent, float t, float radius,
            ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
        {
            drawList.AddRectFilled(min, max,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.025f + 0.045f * t)), radius, corners);
            drawList.AddRect(min, max,
                ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.05f + 0.24f * t)), radius, corners);
        }

        /// <summary>
        /// Traveling light band clipped to <paramref name="min"/>..<paramref name="max"/>.
        /// <paramref name="phase"/> is the sweep position 0..1; values outside
        /// draw nothing, so callers can cycle `phase = (time * speed) % 1.5`
        /// to get a sweep followed by a rest interval. With a <paramref name="radius"/>
        /// and selective <paramref name="corners"/> the clip is split so the band
        /// never bleeds into rounded corner zones (clip rects are axis-aligned —
        /// corner squares are excluded instead of curved).
        /// </summary>
        public static void Sheen(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float phase, float bandWidth, Vector4 tint, float radius = 0f,
            ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
        {
            if (phase is < 0f or > 1f)
            {
                return;
            }
            var half = bandWidth * 0.5f;
            var x = min.X + (max.X - min.X + bandWidth * 2f) * phase - bandWidth;
            var center = ColorUtils.ToU32(tint);
            var edge = ColorUtils.ToU32(new Vector4(tint.X, tint.Y, tint.Z, 0f));
            void DrawBand()
            {
                drawList.AddRectFilledMultiColor(new Vector2(x, min.Y), new Vector2(x + half, max.Y),
                    edge, center, center, edge);
                drawList.AddRectFilledMultiColor(new Vector2(x + half, min.Y), new Vector2(x + bandWidth, max.Y),
                    center, edge, edge, center);
            }

            if (radius <= 0.5f || corners == ImDrawFlags.RoundCornersNone)
            {
                drawList.PushClipRect(min, max, true);
                DrawBand();
                drawList.PopClipRect();
                return;
            }

            var r = MathF.Min(radius, MathF.Min(max.X - min.X, max.Y - min.Y) * 0.5f);
            var insetL = corners.HasFlag(ImDrawFlags.RoundCornersTopLeft)
                || corners.HasFlag(ImDrawFlags.RoundCornersBottomLeft) ? r : 0f;
            var insetR = corners.HasFlag(ImDrawFlags.RoundCornersTopRight)
                || corners.HasFlag(ImDrawFlags.RoundCornersBottomRight) ? r : 0f;

            // center strip covers the full height between the corner zones
            drawList.PushClipRect(new Vector2(min.X + insetL, min.Y),
                new Vector2(max.X - insetR, max.Y), true);
            DrawBand();
            drawList.PopClipRect();

            // side strips skip the rounded corner squares
            if (insetL > 0f)
            {
                var topL = corners.HasFlag(ImDrawFlags.RoundCornersTopLeft) ? r : 0f;
                var botL = corners.HasFlag(ImDrawFlags.RoundCornersBottomLeft) ? r : 0f;
                drawList.PushClipRect(new Vector2(min.X, min.Y + topL),
                    new Vector2(min.X + insetL, max.Y - botL), true);
                DrawBand();
                drawList.PopClipRect();
            }
            if (insetR > 0f)
            {
                var topR = corners.HasFlag(ImDrawFlags.RoundCornersTopRight) ? r : 0f;
                var botR = corners.HasFlag(ImDrawFlags.RoundCornersBottomRight) ? r : 0f;
                drawList.PushClipRect(new Vector2(max.X - insetR, min.Y + topR),
                    new Vector2(max.X, max.Y - botR), true);
                DrawBand();
                drawList.PopClipRect();
            }
        }

        /// <summary>Clears cached animation state (e.g. on disconnect/window close).</summary>
        public static void Reset()
        {
            HoverStates.Clear();
        }
    }
}
