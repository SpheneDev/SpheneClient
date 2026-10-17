using Dalamud.Bindings.ImGui;
using System.Numerics;
using System.Runtime.InteropServices;

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

        // ---- Star-chart backdrop & rail motes -----------------------------

        private static readonly Vector4 StarColor = new(0.82f, 0.88f, 1f, 1f);

        [StructLayout(LayoutKind.Auto)]
        private readonly record struct Star(Vector2 Pos, float Radius, float Alpha,
            float Phase, bool Accent, bool Bright);
        [StructLayout(LayoutKind.Auto)]
        private readonly record struct StarLink(Vector2 A, Vector2 B, float Alpha);
        [StructLayout(LayoutKind.Auto)]
        private readonly record struct Mote(float X, float Size, float Speed,
            float Phase, float Alpha, bool Accent);

        private static Star[]? _stars;
        private static StarLink[]? _starLinks;
        private static Mote[]? _railMotes;

        /// <summary>
        /// Static "star chart" window backdrop: a sparse field of twinkling
        /// stars — a few tinted with the theme accent — plus faint chart
        /// lines between close neighbours and a handful of brighter stars
        /// carrying a small cross glint. Geometry is generated once from a
        /// fixed seed in normalized space and scaled to the window rect.
        /// Draw after the window surface, before any content.
        /// </summary>
        public static void Constellation(ImDrawListPtr drawList, Vector2 min, Vector2 max)
        {
            if (_stars == null || _starLinks == null)
            {
                (_stars, _starLinks) = BuildConstellation();
            }
            var t = (float)ImGui.GetTime();
            var size = max - min;
            var lineW = MathF.Max(1f, Scale * 0.75f);
            var accent = Theme.Current.AccentCyan;

            drawList.PushClipRect(min, max, true);
            foreach (var l in _starLinks)
            {
                drawList.AddLine(min + l.A * size, min + l.B * size,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(StarColor, l.Alpha)), lineW);
            }
            foreach (var s in _stars)
            {
                var a = s.Alpha * (0.55f + 0.45f * MathF.Sin(t * 0.5f + s.Phase));
                var p = min + s.Pos * size;
                var tint = s.Accent ? accent : StarColor;
                var r = MathF.Max(1f, s.Radius * Scale);
                drawList.AddCircleFilled(p, r,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(tint, a)), 8);
                if (s.Bright)
                {
                    var g = r * 3.2f;
                    var glint = ColorUtils.ToU32(ColorUtils.WithAlpha(tint, a * 0.7f));
                    drawList.AddLine(p + new Vector2(-g, 0f), p + new Vector2(g, 0f), glint, lineW);
                    drawList.AddLine(p + new Vector2(0f, -g), p + new Vector2(0f, g), glint, lineW);
                }
            }
            drawList.PopClipRect();
        }

        /// <summary>
        /// Slow aether motes drifting upward inside a nav rail band: a dozen
        /// tiny dots on individual cycles that fade in at the bottom edge and
        /// out at the top, with a gentle horizontal sway. Drawn by
        /// <see cref="Layout.BeginNavRail"/> so every sidebar shares it.
        /// </summary>
        public static void RailMotes(ImDrawListPtr drawList, Vector2 min, Vector2 max)
        {
            _railMotes ??= BuildRailMotes();
            var span = max.Y - min.Y;
            var w = max.X - min.X;
            if (span < 40f || w < 20f)
            {
                return;
            }
            var t = (float)ImGui.GetTime();
            var accent = Theme.Current.AccentCyan;
            var edge = 14f * Scale;

            drawList.PushClipRect(min, max, true);
            foreach (var m in _railMotes)
            {
                var k = (t + m.Phase) / m.Speed % 1f;
                var y = max.Y - k * (span + edge * 0.5f);
                var edgeFade = MathF.Min(1f, MathF.Min(max.Y - y, y - min.Y) / edge);
                if (edgeFade <= 0f)
                {
                    continue;
                }
                var x = min.X + w * (m.X + 0.05f * MathF.Sin(t * 0.45f + m.Phase));
                var tint = m.Accent ? accent : StarColor;
                drawList.AddCircleFilled(new Vector2(x, y), MathF.Max(0.8f, m.Size * Scale),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(tint, m.Alpha * edgeFade)), 8);
            }
            drawList.PopClipRect();
        }

        /// <summary>
        /// Builds the star field once: a uniform sparse layer plus extra
        /// weight toward the edges (the window center is content-dense
        /// anyway), a few bright chart stars, and short links between close
        /// neighbours — capped so the sky never turns into a mesh.
        /// </summary>
        private static (Star[] Stars, StarLink[] Links) BuildConstellation()
        {
            var rng = new Random(0xA37E);
            var stars = new List<Star>(64);
            for (var i = 0; i < 46; i++)
            {
                var x = rng.NextSingle();
                var y = rng.NextSingle();
                if (rng.NextSingle() < 0.4f)
                {
                    if (rng.NextSingle() < 0.5f)
                    {
                        x = x < 0.5f ? x * 0.30f : 1f - (1f - x) * 0.30f;
                    }
                    else
                    {
                        y = y < 0.5f ? y * 0.30f : 1f - (1f - y) * 0.30f;
                    }
                }
                stars.Add(new Star(new Vector2(x, y),
                    0.9f + 1.0f * rng.NextSingle(),
                    0.10f + 0.14f * rng.NextSingle(),
                    rng.NextSingle() * MathF.Tau,
                    rng.NextSingle() < 0.15f, Bright: false));
            }
            for (var i = 0; i < 5; i++)
            {
                stars.Add(new Star(
                    new Vector2(0.08f + 0.84f * rng.NextSingle(), 0.08f + 0.84f * rng.NextSingle()),
                    1.7f + 0.6f * rng.NextSingle(),
                    0.28f + 0.10f * rng.NextSingle(),
                    rng.NextSingle() * MathF.Tau,
                    rng.NextSingle() < 0.5f, Bright: true));
            }

            var links = new List<StarLink>(20);
            for (var i = 0; i < stars.Count; i++)
            {
                for (var j = i + 1; j < stars.Count; j++)
                {
                    if (links.Count >= 20)
                    {
                        break;
                    }
                    if (Vector2.Distance(stars[i].Pos, stars[j].Pos) < 0.11f
                        && rng.NextSingle() < 0.45f)
                    {
                        links.Add(new StarLink(stars[i].Pos, stars[j].Pos,
                            0.04f + 0.035f * rng.NextSingle()));
                    }
                }
            }
            return ([.. stars], [.. links]);
        }

        /// <summary>Twelve motes on individual 9–20s cycles, a third tinted accent.</summary>
        private static Mote[] BuildRailMotes()
        {
            var rng = new Random(0xC0FF);
            var motes = new Mote[12];
            for (var i = 0; i < motes.Length; i++)
            {
                motes[i] = new Mote(
                    0.10f + 0.80f * rng.NextSingle(),
                    0.9f + 0.9f * rng.NextSingle(),
                    9f + 11f * rng.NextSingle(),
                    rng.NextSingle() * 20f,
                    0.10f + 0.10f * rng.NextSingle(),
                    rng.NextSingle() < 0.33f);
            }
            return motes;
        }
    }
}
