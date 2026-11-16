using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Fully custom overlay scrollbar. Theme scopes push
    /// <see cref="ImGuiStyleVar.ScrollbarSize"/> as 0 so ImGui's native
    /// scrollbar is invisible and inert, then <see cref="Scrollbar.Resolve"/>
    /// collects the covered windows and queues the real thing for
    /// <see cref="Flush"/>: a thin rail spanning the whole content height
    /// plus a fixed-length pill (≈ two button rows) that only widens while
    /// hovered or dragged. Input is handled manually - pill dragging writes
    /// <c>ImGuiWindow.Scroll</c> directly, track clicks page the view.
    /// <para>
    /// Flush runs after the host's <c>ImGui.End</c> and appends the strips
    /// to each covered window's root draw list. That makes painting over
    /// foreign windows structurally impossible - the strip composites at
    /// the host's z-position, so any window above the host covers it, the
    /// same guarantee a native (window-draw-list) scrollbar has. An earlier
    /// revision drew on the viewport foreground list and had to guess
    /// occlusion from mid-frame sort state - foreign windows could slip
    /// past and get painted over.
    /// </para>
    /// </summary>
    internal static unsafe class Scrollbar
    {
        /// <summary>Idle width as a fraction of the themed (expanded) size.</summary>
        private const float IdleFraction = 0.34f;

        /// <summary>Absolute floor for the idle pill width, independent of theme.</summary>
        private const float MinIdleSize = 4f;

        /// <summary>Width of the background rail; never expands.</summary>
        private const float RailWidth = 3f;

        /// <summary>Ease speed of the expand animation (1/s).</summary>
        private const float ExpandSpeed = 10f;

        /// <summary>Shrink speed of the collapse animation (1/s).</summary>
        private const float CollapseSpeed = 8f;

        /// <summary>Fixed pill length expressed in button-row heights.</summary>
        private const float PillRowFactor = 2f;

        /// <summary>Maximum window-parent hops when resolving popup ownership.</summary>
        private const int MaxBeginStackDepth = 8;

        /// <summary>Page step as a fraction of the visible span on track clicks.</summary>
        private const float PageStepFraction = 0.85f;

        /// <summary>Per host window id → 0..1 eased expand state.</summary>
        private static readonly Dictionary<uint, float> ExpandT = new();

        /// <summary>Reusable scratch list of windows owned by the host.</summary>
        private static readonly List<ImGuiWindowPtr> Covered = new(64);

        /// <summary>Active drag: window id the drag belongs to (0 = none).</summary>
        private static uint _dragWindowId;

        /// <summary>Active drag: which axis (0 = Y, 1 = X).</summary>
        private static int _dragAxis;

        /// <summary>Active drag: pointer offset inside the pill at grab time.</summary>
        private static float _dragGrabOffset;

        /// <summary>Frame of the last pill grab, drag or track page. The strip
        /// writes <c>ImGuiWindow.Scroll</c> directly and bypasses
        /// <c>ScrollTarget</c>, so scroll-follow logic cannot see the user's
        /// steering through ImGui - it yields to this stamp instead.</summary>
        internal static int LastInteractionFrame { get; private set; } = -1;

        /// <summary>
        /// Called inside a theme scope while the host window is current (or
        /// resolvable through <paramref name="hostWindowName"/> for scopes
        /// created in PreDraw): eases the pill width, runs the manual
        /// drag/page input and queues the rail + pill rendering for every
        /// covered scrollbar - the actual painting happens in
        /// <see cref="Flush"/> once the host window has ended.
        /// Returns 0 - the native scrollbar stays fully suppressed.
        /// </summary>
        public static float Resolve(float expandedSize, string? hostWindowName = null)
        {
            var host = ImGuiP.GetCurrentWindowRead();
            if (host.IsNull && hostWindowName != null)
            {
                host = ImGuiP.FindWindowByName(hostWindowName);
            }
            if (host.IsNull)
            {
                return expandedSize;
            }
            var ctx = new ImGuiContextPtr(ImGuiNative.GetCurrentContext());
            if (ctx.IsNull)
            {
                return expandedSize;
            }

            CollectCovered(host, ctx);
            // The themed ScrollbarSize can be a wide gutter value - the grab
            // pill expands only to a button-like width, never to the raw
            // theme size.
            var expandedCap = MathF.Min(expandedSize, ImGui.GetFrameHeight() * 0.55f);
            var hot = UpdateInput(ctx, expandedSize);
            var t = ExpandT.GetValueOrDefault(host.ID);
            var speed = hot ? ExpandSpeed : CollapseSpeed;
            t += ((hot ? 1f : 0f) - t) * MathF.Min(1f, ImGui.GetIO().DeltaTime * speed);
            if (t < 0.002f)
            {
                ExpandT.Remove(host.ID);
                t = 0f;
            }
            else
            {
                ExpandT[host.ID] = t;
            }
            var size = MathF.Max(MinIdleSize, expandedSize * IdleFraction)
                + ((expandedCap - MathF.Max(MinIdleSize, expandedSize * IdleFraction)) * t);

            // Queue this host's strips - colors are captured now, while the
            // theme scope's style vars are still pushed; Flush runs in
            // PostDraw after they were popped.
            if (!Pending.TryGetValue(host.ID, out var p))
            {
                p = new PendingHost();
                Pending[host.ID] = p;
            }
            p.Host = host;
            p.Size = size;
            p.Normal = *ImGui.GetStyleColorVec4(ImGuiCol.ScrollbarGrab);
            p.Hovered = *ImGui.GetStyleColorVec4(ImGuiCol.ScrollbarGrabHovered);
            p.Active = *ImGui.GetStyleColorVec4(ImGuiCol.ScrollbarGrabActive);
            p.Windows.Clear();
            p.Windows.AddRange(Covered);
            PrunePending();
            return 0f;
        }

        /// <summary>
        /// Appends the host's queued strips to each covered window's root
        /// draw list. Must run after the host's <c>ImGui.End</c> - at that
        /// point every child draw list is merged, so appended primitives
        /// land on top of the window's own content while still compositing
        /// at the window's z-position. Foreign windows rendered above the
        /// host therefore always cover the strips.
        /// </summary>
        public static void Flush(string hostWindowName)
        {
            var host = ImGuiP.FindWindowByName(hostWindowName);
            if (host.IsNull || !Pending.Remove(host.ID, out var p))
            {
                return;
            }
            var ctx = new ImGuiContextPtr(ImGuiNative.GetCurrentContext());
            foreach (var w in p.Windows)
            {
                if (w.Handle == null || !w.WasActive || w.Hidden || w.SkipItems || w.Collapsed)
                {
                    continue;
                }
                var dl = w.RootWindow.DrawList;
                if (dl.Handle == null)
                {
                    continue;
                }
                var inner = w.InnerRect;
                dl.PushClipRectFullScreen();
                dl.PushClipRect(inner.Min, inner.Max, false);
                if (w.ScrollbarY)
                {
                    DrawAxis(dl, ctx, w, ImGuiAxis.Y, p.Size, p.Normal, p.Hovered, p.Active);
                }
                if (w.ScrollbarX)
                {
                    DrawAxis(dl, ctx, w, ImGuiAxis.X, p.Size, p.Normal, p.Hovered, p.Active);
                }
                dl.PopClipRect();
                dl.PopClipRect();
            }
        }

        /// <summary>One host's deferred strip render - queued by <see cref="Resolve"/>.</summary>
        private sealed class PendingHost
        {
            public ImGuiWindowPtr Host;
            public float Size;
            public Vector4 Normal;
            public Vector4 Hovered;
            public Vector4 Active;
            public readonly List<ImGuiWindowPtr> Windows = new(16);
        }

        /// <summary>Queued hosts awaiting their PostDraw flush.</summary>
        private static readonly Dictionary<uint, PendingHost> Pending = new();

        /// <summary>
        /// Drops queued hosts that stopped drawing - a closed or crashed
        /// window can leave a pending entry that would never flush.
        /// </summary>
        private static void PrunePending()
        {
            if ((ImGui.GetFrameCount() & 0x3F) != 0)
            {
                return;
            }
            _pruneScratch.Clear();
            foreach (var (id, p) in Pending)
            {
                if (p.Host.Handle == null || !p.Host.WasActive)
                {
                    _pruneScratch.Add(id);
                }
            }
            foreach (var id in _pruneScratch)
            {
                Pending.Remove(id);
            }
        }

        private static readonly List<uint> _pruneScratch = new(8);

        /// <summary>
        /// Fills <see cref="Covered"/> with every window that belongs to the
        /// host: the host itself, child descendants, and popups/tooltips
        /// whose begin-stack parent chain leads back into the host tree.
        /// </summary>
        private static void CollectCovered(ImGuiWindowPtr host, ImGuiContextPtr ctx)
        {
            Covered.Clear();
            ref var windows = ref ctx.Windows;
            var count = Math.Min(windows.Size, 512);
            for (var i = 0; i < count; i++)
            {
                var w = windows[i];
                if (w.Handle == null || (!w.Active && !w.WasActive))
                {
                    continue;
                }
                if (w.Handle == host.Handle
                    || w.RootWindow.Handle == host.Handle
                    || BelongsViaBeginStack(w, host))
                {
                    Covered.Add(w);
                }
            }
        }

        /// <summary>
        /// Popups and tooltips are root windows - their only link to the
        /// owning tree is the window that was in the begin stack when they
        /// were opened. Walk that chain; popup-of-popup nests keep working.
        /// </summary>
        private static bool BelongsViaBeginStack(ImGuiWindowPtr w, ImGuiWindowPtr host)
        {
            var p = w.ParentWindowInBeginStack;
            for (var depth = 0; depth < MaxBeginStackDepth && !p.IsNull; depth++)
            {
                if (p.Handle == host.Handle || p.RootWindow.Handle == host.Handle)
                {
                    return true;
                }
                p = p.ParentWindowInBeginStack;
            }
            return false;
        }

        /// <summary>
        /// Manual scrollbar input. Returns true while any covered strip is
        /// hovered or being dragged, so the host pill eases open.
        /// </summary>
        private static bool UpdateInput(ImGuiContextPtr ctx, float expandedSize)
        {
            var io = ImGui.GetIO();
            var mouse = io.MousePos;
            var hotZone = expandedSize + 2f;
            var hot = false;

            // Continue an active drag first - it wins over hover tests and
            // keeps working while the pointer has left the window.
            if (_dragWindowId != 0)
            {
                var dragWin = ImGuiP.FindWindowByID(_dragWindowId);
                if (!io.MouseDown[(int)ImGuiMouseButton.Left] || dragWin.IsNull)
                {
                    _dragWindowId = 0;
                }
                else
                {
                    ApplyDrag(dragWin, mouse);
                    LastInteractionFrame = ImGui.GetFrameCount();
                    hot = true;
                }
            }
            // The strip is not an ImGui item, so a click inside it also arms
            // the native window-move. Reclaim the pointer for our drag.
            if (_dragWindowId != 0 && !ctx.MovingWindow.IsNull)
            {
                ctx.MovingWindow = ImGuiWindowPtr.Null;
            }

            var clicked = io.MouseClicked[(int)ImGuiMouseButton.Left]
                && ctx.ActiveId == 0;
            foreach (var w in Covered)
            {
                if (w.Hidden || w.SkipItems)
                {
                    continue;
                }
                if (w.ScrollbarY
                    && ProcessAxis(ctx, w, ImGuiAxis.Y, mouse, hotZone, clicked))
                {
                    hot = true;
                }
                if (w.ScrollbarX
                    && ProcessAxis(ctx, w, ImGuiAxis.X, mouse, hotZone, clicked))
                {
                    hot = true;
                }
            }
            return hot;
        }

        /// <summary>
        /// Hit-test + click handling for one axis of one window. The strip
        /// counts as hot only when its own window is the hovered window -
        /// strips occluded by another window do not wake up.
        /// </summary>
        private static bool ProcessAxis(ImGuiContextPtr ctx, ImGuiWindowPtr w,
            ImGuiAxis axis, Vector2 mouse, float hotZone, bool clicked)
        {
            if (ctx.HoveredWindow.Handle != w.Handle)
            {
                return false;
            }
            var vert = axis == ImGuiAxis.Y;
            var inner = w.InnerRect;
            float zMin, zMax, cMin, cMax;
            if (vert)
            {
                zMin = inner.Max.X - hotZone;
                zMax = inner.Max.X;
                cMin = inner.Min.Y;
                cMax = inner.Max.Y;
            }
            else
            {
                zMin = inner.Max.Y - hotZone;
                zMax = inner.Max.Y;
                cMin = inner.Min.X;
                cMax = inner.Max.X;
            }
            var coord = vert ? mouse.Y : mouse.X;
            var edge = vert ? mouse.X : mouse.Y;
            if (edge < zMin || edge > zMax || coord < cMin || coord > cMax)
            {
                return false;
            }

            if (clicked)
            {
                LastInteractionFrame = ImGui.GetFrameCount();
                var trackLen = cMax - cMin;
                var pillLen = PillLength(trackLen);
                var (scroll, scrollMax) = AxisScroll(w, vert);
                var frac = scrollMax > 0.5f ? Math.Clamp(scroll / scrollMax, 0f, 1f) : 0f;
                var pillStart = cMin + (frac * (trackLen - pillLen));
                if (coord >= pillStart && coord <= pillStart + pillLen)
                {
                    _dragWindowId = w.ID;
                    _dragAxis = vert ? 0 : 1;
                    _dragGrabOffset = coord - pillStart;
                    ctx.MovingWindow = ImGuiWindowPtr.Null;
                }
                else
                {
                    // Track click: page toward the pointer, native style.
                    var avail = vert
                        ? w.InnerRect.Max.Y - w.InnerRect.Min.Y
                        : w.InnerRect.Max.X - w.InnerRect.Min.X;
                    var dir = coord < pillStart ? -1f : 1f;
                    SetAxisScroll(w, vert, scroll + (dir * avail * PageStepFraction));
                    ctx.MovingWindow = ImGuiWindowPtr.Null;
                }
            }
            return true;
        }

        /// <summary>Maps the live pointer position back onto the scroll range.</summary>
        private static void ApplyDrag(ImGuiWindowPtr w, Vector2 mouse)
        {
            var vert = _dragAxis == 0;
            var inner = w.InnerRect;
            float cMin, cMax;
            if (vert)
            {
                cMin = inner.Min.Y;
                cMax = inner.Max.Y;
            }
            else
            {
                cMin = inner.Min.X;
                cMax = inner.Max.X;
            }
            var trackLen = cMax - cMin;
            var pillLen = PillLength(trackLen);
            if (trackLen <= pillLen)
            {
                return;
            }
            var coord = vert ? mouse.Y : mouse.X;
            var frac = Math.Clamp((coord - _dragGrabOffset - cMin) / (trackLen - pillLen), 0f, 1f);
            var (_, scrollMax) = AxisScroll(w, vert);
            SetAxisScroll(w, vert, frac * scrollMax);
        }

        private static (float Scroll, float ScrollMax) AxisScroll(ImGuiWindowPtr w, bool vert)
            => vert ? (w.Scroll.Y, w.ScrollMax.Y) : (w.Scroll.X, w.ScrollMax.X);

        private static void SetAxisScroll(ImGuiWindowPtr w, bool vert, float value)
        {
            var scroll = w.Scroll;
            if (vert)
            {
                scroll.Y = Math.Clamp(value, 0f, w.ScrollMax.Y);
            }
            else
            {
                scroll.X = Math.Clamp(value, 0f, w.ScrollMax.X);
            }
            w.Scroll = scroll;
        }

        private static float PillLength(float trackLen)
            => MathF.Min(PillRowFactor * ImGui.GetFrameHeight(), trackLen);

        private static void DrawAxis(ImDrawListPtr drawList, ImGuiContextPtr ctx,
            ImGuiWindowPtr w, ImGuiAxis axis, float size,
            Vector4 normal, Vector4 hovered, Vector4 active)
        {
            var vert = axis == ImGuiAxis.Y;
            var inner = w.InnerRect;
            float rMin, rMax, cMin, cMax;
            if (vert)
            {
                rMax = inner.Max.X;
                rMin = rMax - RailWidth;
                cMin = inner.Min.Y;
                cMax = inner.Max.Y;
            }
            else
            {
                rMax = inner.Max.Y;
                rMin = rMax - RailWidth;
                cMin = inner.Min.X;
                cMax = inner.Max.X;
            }
            var trackLen = cMax - cMin;
            if (trackLen < 12f)
            {
                return;
            }

            var (scroll, scrollMax) = AxisScroll(w, vert);
            var frac = scrollMax > 0.5f ? Math.Clamp(scroll / scrollMax, 0f, 1f) : 0f;
            var pillLen = PillLength(trackLen);
            var start = cMin + (frac * (trackLen - pillLen));
            Vector2 pillMin, pillMax;
            if (vert)
            {
                pillMin = new Vector2(rMax - size, start);
                pillMax = new Vector2(rMax, start + pillLen);
            }
            else
            {
                pillMin = new Vector2(start, rMax - size);
                pillMax = new Vector2(start + pillLen, rMax);
            }

            var dragging = _dragWindowId == w.ID && _dragAxis == (vert ? 0 : 1);
            var color = dragging ? active
                : ctx.HoveredWindow.Handle == w.Handle && InStrip(w, vert) ? hovered
                : normal;

            var u32 = ImGui.GetColorU32(color);
            var rMinV = vert ? new Vector2(rMin, cMin) : new Vector2(cMin, rMin);
            var rMaxV = vert ? new Vector2(rMax, cMax) : new Vector2(cMax, rMax);
            // Rail and pill share one color - the strip reads as a single
            // line that swells into the grab, so the junction stays invisible.
            drawList.AddRectFilled(rMinV, rMaxV, u32, RailWidth * 0.5f);
            // Only the exposed side rounds - the pill stays flush against the
            // window edge it rides on.
            var flags = vert ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersTop;
            drawList.AddRectFilled(pillMin, pillMax, u32, size * 0.5f, flags);
            // Concave fillets where the widened pill peels out of the rail -
            // only while the pill is meaningfully wider than the rail: a
            // sub-pixel wedge degenerates into jaggies at idle width.
            var fillet = MathF.Min((size - RailWidth) * 0.5f, MathF.Min(pillLen * 0.5f, 8f));
            if (fillet > 1.25f)
            {
                if (vert)
                {
                    DrawFillet(drawList, new Vector2(rMin, start),
                        new Vector2(0f, -1f), new Vector2(-1f, 0f), fillet, u32);
                    DrawFillet(drawList, new Vector2(rMin, start + pillLen),
                        new Vector2(0f, 1f), new Vector2(-1f, 0f), fillet, u32);
                }
                else
                {
                    DrawFillet(drawList, new Vector2(start, rMin),
                        new Vector2(-1f, 0f), new Vector2(0f, -1f), fillet, u32);
                    DrawFillet(drawList, new Vector2(start + pillLen, rMin),
                        new Vector2(1f, 0f), new Vector2(0f, -1f), fillet, u32);
                }
            }
        }

        /// <summary>
        /// Fills a concave quarter-arc wedge between the rail's edge and the
        /// pill's edge at a junction: arc from <paramref name="railDir"/> *
        /// radius (on the rail edge, away from the pill) to
        /// <paramref name="outDir"/> * radius (on the pill edge). The region
        /// between the arc and the junction is emitted as a triangle fan -
        /// a single path is degenerate there (both arc endpoints are tangent
        /// cusps) and produces fill artifacts.
        /// </summary>
        private static void DrawFillet(ImDrawListPtr drawList, Vector2 junction,
            Vector2 railDir, Vector2 outDir, float radius, uint color)
        {
            const int segments = 8;
            var a = junction + (railDir * radius);
            var b = junction + (outDir * radius);
            var center = a + (outDir * radius);
            var startAngle = MathF.Atan2(a.Y - center.Y, a.X - center.X);
            var sweep = MathF.Atan2(b.Y - center.Y, b.X - center.X) - startAngle;
            while (sweep > MathF.PI) sweep -= MathF.PI * 2f;
            while (sweep < -MathF.PI) sweep += MathF.PI * 2f;
            var prev = a;
            for (var i = 1; i <= segments; i++)
            {
                var angle = startAngle + (sweep * i / segments);
                var p = center + new Vector2(
                    MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
                drawList.AddTriangleFilled(junction, prev, p, color);
                prev = p;
            }
        }

        /// <summary>Whether the pointer currently rests on this window's strip.</summary>
        private static bool InStrip(ImGuiWindowPtr w, bool vert)
        {
            var mouse = ImGui.GetIO().MousePos;
            var inner = w.InnerRect;
            var edge = vert ? mouse.X : mouse.Y;
            var edgeMax = vert ? inner.Max.X : inner.Max.Y;
            var coord = vert ? mouse.Y : mouse.X;
            var cMin = vert ? inner.Min.Y : inner.Min.X;
            var cMax = vert ? inner.Max.Y : inner.Max.X;
            return edge <= edgeMax && edge >= edgeMax - 16f && coord >= cMin && coord <= cMax;
        }
    }
}
