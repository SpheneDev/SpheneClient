using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Layout containers and geometry: content bounds, cards, scroll areas,
    /// tab strips, pinned section labels, separators, groups, indents and
    /// spacing primitives. All bounds flow through
    /// <see cref="ContentBounds"/> so nested helpers dock correctly.
    /// </summary>
    public static class Layout
    {
        /// <summary>
        /// Standard horizontal inner spacing for scroll areas created with
        /// noPadding. Applied automatically as a left indent; edge-bleed
        /// elements must respect the same value on the right.
        /// </summary>
        public const float ScrollAreaContentInset = 10f;

        /// <summary>Screen-space right bound of the innermost content region, or -1.</summary>
        public static float ContentRightBound => ContentBounds.CurrentRight;

        /// <summary>Screen-space left bound of the innermost content region, or -1.</summary>
        public static float ContentLeftBound => ContentBounds.CurrentLeft;

        internal const float AccentCardRounding = 8f;

        /// <summary>The AccentCard's semi-transparent accent-tinted body
        /// color — shared so banners can fade into the exact same tone.</summary>
        internal static Vector4 AccentCardTint(Vector4 accent)
            => new(accent.X * 0.16f, accent.Y * 0.14f, accent.Z * 0.07f, 0.82f);

        /// <summary>
        /// Content width that aligns with the section cards of the current
        /// page: remaining width clamped to the window edge minus the active
        /// scroll-area inset and border. For elements outside cards (e.g. a
        /// tab bar) that must share the cards' edges.
        /// </summary>
        public static float CardAlignedAvailX()
        {
            var scale = SpheneUI.Scale;
            var cursorX = ImGui.GetCursorScreenPos().X;
            var rightEdge = MathF.Min(
                MathF.Min(cursorX + ImGui.GetContentRegionAvail().X,
                    ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - 1f * scale),
                ScrollAreaRightBound);
            return MathF.Max(60f * scale, rightEdge - cursorX);
        }

        // The scroll area's right content edge (child edge minus the content
        // inset). Nested child windows — expanders, embedded panels — have a
        // smaller right edge of their own that wins the Min(), so their cards
        // are never double-inset; windows outside the scroll area (popups)
        // are excluded explicitly.
        private static float ScrollAreaRightBound
        {
            get
            {
                if (ScrollAreaStack.Count == 0 || !ScrollAreaStack.Peek().Begun)
                {
                    return float.MaxValue;
                }
                var state = ScrollAreaStack.Peek();
                if (ImGui.GetWindowPos().X + ImGui.GetWindowSize().X > state.ChildRight + 1f)
                {
                    return float.MaxValue;
                }
                return state.ChildRight - 1f * SpheneUI.Scale - state.ContentInset;
            }
        }

        /// <summary>
        /// Suppresses the active content bound until disposed — use while
        /// drawing modal/popup windows so right-docked helpers measure against
        /// the popup window rather than the card rendered underneath it.
        /// </summary>
        public static IDisposable SuppressBound() => ContentBounds.PushNone();

        /// <summary>
        /// Runs <paramref name="content"/> inside a borderless, fixed-width
        /// single-column table so every widget respects the given content
        /// width: wrapped text, right-aligned SameLine rows, stretched tables
        /// and full-width items all stop at the right bound. Reusable anywhere
        /// a bounded content region is needed.
        /// </summary>
        public static void ContentRegion(float width, string? id, Action content)
        {
            // Window-space Y stays constant while scrolling — screen Y would
            // mint a new table instance per scroll position and thrash its
            // measurement.
            var tableId = $"##card-content-{(id ?? "region")}-{(int)ImGui.GetCursorPos().Y}";
            var cursorX = ImGui.GetCursorScreenPos().X;
            ContentBounds.Push(new Vector2(cursorX, cursorX + width));
            try
            {
                using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero))
                {
                    if (!ImGui.BeginTable(tableId, 1, ImGuiTableFlags.None, new Vector2(width, 0f)))
                    {
                        return;
                    }
                }
                ImGui.TableNextColumn();
                // Wrap at the cell's right edge — PushTextWrapPos takes a
                // window-local X, not a width, so starting mid-row (side-by-
                // side regions) must add the column's own offset.
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                content();
                ImGui.PopTextWrapPos();
                ImGui.EndTable();
            }
            finally
            {
                ContentBounds.Pop();
            }
        }

        /// <summary>
        /// Full-width card with padded content, a tinted background, a subtle
        /// animated shimmer and a gently pulsing accent border — the prominent
        /// promo/status card of the design system. Optional
        /// <paramref name="fxHover"/>/<paramref name="fxIdle"/> stack the
        /// owner's combinable <see cref="ProfileRowFx"/> layers on the card
        /// chrome (under the content, above the card background).
        /// </summary>
        /// <param name="topBleed">Optional chrome-layer draw callback invoked
        /// with the card's exact outer rect (drawList, cardMin, cardMax,
        /// rounding). It renders above the card background and below the
        /// border/effect layers — full-bleed media like hero banners belong
        /// here since content coordinates are not reliable for edge-aligned
        /// drawing.</param>
        public static void AccentCard(Action content, Vector4? accentColor = null,
            bool animatedBackdrop = true, string? id = null,
            ProfileRowFx fxHover = ProfileRowFx.None, ProfileRowFx fxIdle = ProfileRowFx.None,
            Action<ImDrawListPtr, Vector2, Vector2, float>? topBleed = null)
        {
            var scale = SpheneUI.Scale;
            var padding = Spacing.CardPadding.Scaled;
            var rounding = AccentCardRounding * scale;
            var availWidth = ImGui.GetContentRegionAvail().X;
            var accent = accentColor ?? Palette.SpheneGold;

            var drawList = ImGui.GetWindowDrawList();
            var cardMin = ImGui.GetCursorScreenPos();
            var cardRightX = MathF.Min(
                MathF.Min(cardMin.X + availWidth,
                    ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - 1f * scale),
                ScrollAreaRightBound);
            // an explicitly pushed bound (e.g. side-by-side columns) clamps
            // the card to it — narrower than the plain window edge
            var bound = ContentRightBound;
            if (bound > 0f)
            {
                cardRightX = MathF.Min(cardRightX, bound);
            }
            var innerWidth = MathF.Max(60f * scale, cardRightX - cardMin.X - padding * 2f);
            drawList.ChannelsSplit(2);
            drawList.ChannelsSetCurrent(1);

            ImGui.SetCursorScreenPos(cardMin + new Vector2(padding, padding));
            using (ImRaii.Group())
            {
                ContentRegion(innerWidth, id, content);
            }

            var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padding);
            drawList.ChannelsSetCurrent(0);

            var background = AccentCardTint(accent);
            drawList.AddRectFilled(cardMin, cardMax, ColorUtils.ToU32(background), rounding);
            topBleed?.Invoke(drawList, cardMin, cardMax, rounding);

            if (animatedBackdrop)
            {
                Chrome.AccentCardBackdrop(drawList, cardMin, cardMax, rounding, accent);
            }

            // Static accent hairline instead of a pulsing border — keeps the
            // hero card defined without the plastic chrome look.
            var borderColor = ColorUtils.WithAlpha(accent, 0.38f);
            drawList.AddRect(cardMin, cardMax, ColorUtils.ToU32(borderColor),
                rounding, ImDrawFlags.RoundCornersAll, Chrome.CardBorderThickness);
            if (fxHover != ProfileRowFx.None || fxIdle != ProfileRowFx.None)
            {
                var fxHovered = ImGui.IsMouseHoveringRect(cardMin, cardMax);
                var fxT = Fx.HoverT(ImGui.GetID(id ?? "##accentcardfx"), fxHovered);
                Profile.RowFx(drawList, cardMin, cardMax, accent,
                    fxHovered, fxT, fxHover, fxIdle, rounding);
            }
            drawList.ChannelsMerge();

            ImGui.SetCursorScreenPos(cardMin + new Vector2(0, cardMax.Y - cardMin.Y));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>
        /// Calm full-width section panel: a borderless tonal surface in the
        /// rail-band color family, identified by a hairline accent seam along
        /// its top edge and an optional accent title. The standard grouping
        /// container for settings blocks; use <see cref="AccentCard"/> for
        /// prominent cards.
        /// </summary>
        public static void SectionCard(Action content, string? title = null,
            Vector4? accentColor = null, string? description = null, bool animatedBackdrop = true,
            string? id = null)
        {
            var scale = SpheneUI.Scale;
            var padding = Spacing.CardPadding.Scaled;
            var rounding = AccentCardRounding * scale;
            var availWidth = ImGui.GetContentRegionAvail().X;
            var accent = accentColor ?? Theme.Current.AccentCyan;

            var drawList = ImGui.GetWindowDrawList();
            var cardMin = ImGui.GetCursorScreenPos();
            var cardRightX = MathF.Min(
                MathF.Min(cardMin.X + availWidth,
                    ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - 1f * scale),
                ScrollAreaRightBound);
            var bound = ContentRightBound;
            if (bound > 0f)
            {
                cardRightX = MathF.Min(cardRightX, bound);
            }
            var innerWidth = MathF.Max(60f * scale, cardRightX - cardMin.X - padding * 2f);
            drawList.ChannelsSplit(2);
            drawList.ChannelsSetCurrent(1);

            ImGui.SetCursorScreenPos(cardMin + new Vector2(padding, padding));
            using (ImRaii.Group())
            {
                ContentRegion(innerWidth, id ?? title, () =>
                {
                    if (!string.IsNullOrEmpty(title))
                    {
                        Text.Colored(title, accent);
                        if (!string.IsNullOrEmpty(description))
                        {
                            Text.ColoredWrapped(description, Dalamud.Interface.Colors.ImGuiColors.DalamudGrey);
                        }
                        ImGuiHelpers.ScaledDummy(0, 7);
                    }
                    content();
                });
            }

            var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padding);
            drawList.ChannelsSetCurrent(0);

            // Borderless tonal panel in the rail-band color family; the group
            // identity lives on a hairline accent seam along the top edge.
            var background = NavRailBandColor();
            drawList.AddRectFilled(cardMin, cardMax, ColorUtils.ToU32(background), rounding);

            if (animatedBackdrop)
            {
                Chrome.SectionCardBackdrop(drawList, cardMin, cardMax, rounding, accent);
            }

            var seamColor = ColorUtils.WithAlpha(accent, 0.32f);
            var seamCol = ImGui.ColorConvertFloat4ToU32(seamColor);
            var seamFade = ImGui.ColorConvertFloat4ToU32(ColorUtils.WithAlpha(seamColor, 0f));
            var seamH = MathF.Max(1f, 1.5f * scale);
            var seamInset = rounding * 0.9f;
            var seamMin = new Vector2(cardMin.X + seamInset, cardMin.Y + 0.5f);
            var seamFadeW = 24f * scale;
            drawList.AddRectFilledMultiColor(seamMin,
                new Vector2(seamMin.X + seamFadeW, seamMin.Y + seamH),
                seamFade, seamCol, seamCol, seamFade);
            drawList.AddRectFilled(new Vector2(seamMin.X + seamFadeW, seamMin.Y),
                new Vector2(cardMax.X - seamInset - seamFadeW, seamMin.Y + seamH), seamCol);
            drawList.AddRectFilledMultiColor(
                new Vector2(cardMax.X - seamInset - seamFadeW, seamMin.Y),
                new Vector2(cardMax.X - seamInset, seamMin.Y + seamH),
                seamCol, seamFade, seamFade, seamCol);
            drawList.ChannelsMerge();

            ImGui.SetCursorScreenPos(cardMin + new Vector2(0, cardMax.Y - cardMin.Y));
            ImGui.Dummy(Vector2.Zero);
        }

        private sealed class ScrollAreaState
        {
            public string Id = string.Empty;
            public bool Begun;
            public bool ClipPushed;
            public bool Border;
            public bool GripHeld;
            public float GripAnim;
            public Vector2 ParentMin;
            public Vector2 ParentMax;
            public float ContentInset;
            public bool HideTopBorder;
            public float TopInset;
            public bool BoundsPushed;
            public int Depth;
            public float ChildRight;
        }

        private static readonly Stack<ScrollAreaState> ScrollAreaStack = new();

        /// <summary>
        /// Begins a child region without a visible scrollbar. Wheel scrolling
        /// still works. Must be closed with <see cref="EndScrollArea"/>, which
        /// draws fade hints at the top/bottom edges whenever more content
        /// exists in that direction.
        /// <paramref name="noPadding"/> removes the child window padding so
        /// content can reach the edges; inner spacing must then be managed
        /// manually. <paramref name="contentInset"/> indents content inside a
        /// noPadding child; negative values use the default
        /// <see cref="ScrollAreaContentInset"/>. <paramref name="contentPadding"/>
        /// overrides the child's WindowPadding (unscaled) without the
        /// noPadding behavior.
        /// </summary>
        public static bool BeginScrollArea(string id, Vector2 size, bool border = false,
            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None, bool noPadding = false,
            bool hideTopBorder = false, float topInset = 0f, float contentInset = -1f,
            Vector2? contentPadding = null)
        {
            var state = new ScrollAreaState
            {
                Id = id,
                Border = border,
                HideTopBorder = hideTopBorder,
                TopInset = topInset,
                ParentMin = ImGui.GetWindowPos(),
                ParentMax = ImGui.GetWindowPos() + ImGui.GetWindowSize(),
                Depth = ScrollAreaStack.Count,
            };
            if (noPadding || contentPadding.HasValue)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
                    noPadding ? Vector2.Zero : contentPadding!.Value * SpheneUI.Scale);
            }
            // Borderless children ignore WindowPadding unless the flag opts
            // in — without it the padded variant would sit flush at the child
            // edge despite the pushed style var.
            if (contentPadding.HasValue)
            {
                windowFlags |= ImGuiWindowFlags.AlwaysUseWindowPadding;
            }
            var begun = ImGui.BeginChild(id, size, false, windowFlags | ImGuiWindowFlags.NoScrollbar);
            if (noPadding || contentPadding.HasValue)
            {
                ImGui.PopStyleVar();
            }
            if (noPadding)
            {
                state.ContentInset = contentInset < 0f
                    ? ScrollAreaContentInset * SpheneUI.Scale
                    : contentInset;
                if (state.ContentInset > 0f)
                {
                    ImGui.Indent(state.ContentInset);
                }
            }
            state.Begun = begun;
            if (begun)
            {
                state.ChildRight = ImGui.GetWindowPos().X + ImGui.GetWindowSize().X;
                if (state.TopInset > 0f && ImGui.GetScrollY() <= Chrome.ScrollFadeDeadZone)
                {
                    state.TopInset = 0f;
                }
                var min = ImGui.GetWindowPos();
                var max = min + ImGui.GetWindowSize();
                GetScrollInsets(border, out _, out var insetY);
                if (border)
                {
                    var clipX = 1f * SpheneUI.Scale;
                    var inner = min + new Vector2(clipX, insetY + state.TopInset);
                    var innerMax = max - new Vector2(clipX, insetY);
                    if (innerMax.X > inner.X && innerMax.Y > inner.Y)
                    {
                        ImGui.PushClipRect(inner, innerMax, true);
                        state.ClipPushed = true;
                    }
                }
                else
                {
                    // Horizontally content may reach the window's inner edge so
                    // separators/fades can span wider than a padded child. A
                    // non-intersecting rect is required: the child's own clip
                    // is limited to its bounds and would cut anything wider off.
                    var borderSize = MathF.Max(ImGui.GetStyle().WindowBorderSize, 0f);
                    ImGui.PushClipRect(
                        new Vector2(state.ParentMin.X + borderSize, min.Y + state.TopInset),
                        new Vector2(state.ParentMax.X - borderSize, max.Y), false);
                    state.ClipPushed = true;
                }
                // The child's own content edge is a bound too: labeled controls
                // and toggle rows inside a scroll area dock to its padded inner
                // edge, not to a wider enclosing card/region bound.
                var contentX = ImGui.GetCursorScreenPos().X;
                ContentBounds.Push(new Vector2(contentX, contentX + ImGui.GetContentRegionAvail().X));
                state.BoundsPushed = true;
            }
            ScrollAreaStack.Push(state);
            return begun;
        }

        /// <summary>
        /// Remaining height for a scroll child that must reach the bottom edge
        /// of the visible region without overflowing into page scroll.
        /// Measures the remaining content height and subtracts the trailing
        /// chrome that still lands below the child: the enclosing card's
        /// bottom padding, the bottom spacer EndScrollArea appends and the
        /// inter-item spacing around it. <paramref name="bottomReserve"/>
        /// overrides the computed reserve for callers outside cards or with
        /// additional trailing elements.
        /// </summary>
        public static float FillToViewBottomHeight(float bottomReserve = -1f)
        {
            var scale = SpheneUI.Scale;
            if (bottomReserve < 0f)
            {
                bottomReserve = Spacing.CardPadding.Scaled
                    + ScrollAreaContentInset * scale
                    + ImGui.GetStyle().ItemSpacing.Y * 2f
                    + 1f * scale;
            }
            var minHeight = ImGui.GetTextLineHeightWithSpacing() * 2f;
            // GetContentRegionAvail is inflated by the window's scroll offset —
            // subtract it or fill-height elements grow with the scroll and keep
            // a few pixels of residual scrollMax alive forever.
            return MathF.Max(minHeight,
                ImGui.GetContentRegionAvail().Y - ImGui.GetScrollY() - bottomReserve);
        }

        /// <summary>
        /// <see cref="BeginScrollArea"/> variant whose height fills the rest of
        /// the view: the enclosing card closes flush at the page bottom
        /// instead of poking past it and forcing a few pixels of outer scroll.
        /// </summary>
        public static bool BeginFillToBottomScrollArea(string id, float bottomReserve = -1f,
            bool border = false, Vector2? contentPadding = null,
            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None)
            => BeginScrollArea(id, new Vector2(0f, FillToViewBottomHeight(bottomReserve)),
                border, windowFlags, contentPadding: contentPadding);

        private sealed class ExpanderState
        {
            public bool Open;
            public float T;
            public float ContentH;
            public float TopY;
        }

        private static readonly Dictionary<string, ExpanderState> _expanders =
            new(StringComparer.Ordinal);
        private static ExpanderState? _activeExpander;

        /// <summary>Toggles the inline expander <paramref name="id"/> (plays the grow animation).</summary>
        public static void ToggleExpander(string id)
        {
            if (!_expanders.TryGetValue(id, out var st))
            {
                st = new ExpanderState();
                _expanders[id] = st;
            }
            st.Open = !st.Open;
        }

        /// <summary>Sets an expander's open state explicitly — use for external close paths.</summary>
        public static void SetExpanderOpen(string id, bool open)
        {
            if (!_expanders.TryGetValue(id, out var st))
            {
                if (!open)
                {
                    return;
                }
                st = new ExpanderState();
                _expanders[id] = st;
            }
            st.Open = open;
        }

        /// <summary>Whether the expander is logically open (regardless of the animation phase).</summary>
        public static bool IsExpanderOpen(string id)
            => _expanders.TryGetValue(id, out var st) && st.Open;

        /// <summary>Drops all expander state — call on window/page resets like <see cref="Fx.Reset"/>.</summary>
        public static void ResetExpanders() => _expanders.Clear();

        /// <summary>
        /// In-flow expanding panel: renders its content inside the current
        /// window (not an overlay), grows/collapses with a height + alpha
        /// animation and clips content while animating. Once fully open the
        /// region is exactly content-sized — no inner scrolling, the host
        /// page scrolls instead. Being part of the host window it also
        /// disappears with it — no focus or z-order handling needed at all.
        /// Returns true while visible; render content after it and always
        /// pair with <see cref="EndExpander"/>.
        /// </summary>
        public static bool BeginExpander(string id)
        {
            if (!_expanders.TryGetValue(id, out var st))
            {
                return false;
            }
            var dt = ImGui.GetIO().DeltaTime;
            st.T = Math.Clamp(st.T + (st.Open ? dt : -dt) * 6f, 0f, 1f);
            var eased = st.T * st.T * (3f - 2f * st.T);
            if (eased <= 0.004f && !st.Open)
            {
                _expanders.Remove(id);
                return false;
            }
            var scale = SpheneUI.Scale;
            var height = MathF.Max(2f, st.ContentH * eased);
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, eased);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10f * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f * scale);
            // The panel paints itself on the PARENT draw list before the
            // child exists — ChildBg stays transparent because child windows
            // paint it as their own background, and nested children (e.g.
            // the hero's text block) would overlay an opaque rect that hides
            // accent-tinted card surfaces beneath them.
            var wbg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
            var panelBg = Vector4.Lerp(wbg, Vector4.One, 0.04f);
            panelBg.W = MathF.Max(wbg.W, 0.96f) * eased;
            var panelMin = ImGui.GetCursorScreenPos();
            var panelW = ImGui.GetContentRegionAvail().X;
            var panelBound = MathF.Min(
                ContentRightBound > 0f ? ContentRightBound : float.MaxValue,
                ScrollAreaRightBound);
            if (panelBound < float.MaxValue)
            {
                panelW = MathF.Min(panelW, panelBound - panelMin.X);
            }
            ImGui.GetWindowDrawList().AddRectFilled(panelMin,
                panelMin + new Vector2(panelW, height), ColorUtils.ToU32(panelBg),
                10f * scale);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, 0u);
            ImGui.PushStyleColor(ImGuiCol.Border, ColorUtils.ToU32(Colors.Border));
            // NoScrollWithMouse — the child is exactly content-sized at rest,
            // so an inner scroll offset would only exist mid-animation and
            // would fight the height measurement
            ImGui.BeginChild($"##expander_{id}", new Vector2(0f, height), true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings);
            st.TopY = ImGui.GetWindowPos().Y;
            _activeExpander = st;
            return true;
        }

        /// <summary>Ends a region opened by <see cref="BeginExpander"/>.</summary>
        public static void EndExpander()
        {
            if (_activeExpander == null)
            {
                return;
            }
            var st = _activeExpander;
            _activeExpander = null;
            var bottom = ImGui.GetCursorScreenPos().Y;
            var pad = ImGui.GetStyle().WindowPadding.Y;
            // Measure only while animating (or until first content arrives):
            // re-measuring a fully-open, possibly clipped child each frame can
            // feed a jittering content height back into BeginExpander's size —
            // the enclosing scroll area then oscillates and clamps ScrollY,
            // which is the "scroll snaps back to top" symptom.
            if (st.T < 1f || st.ContentH <= 0f)
            {
                st.ContentH = bottom - st.TopY + pad;
            }
            ImGui.EndChild();
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(4);
        }

        /// <summary>
        /// Ends a child opened by <see cref="BeginScrollArea"/> and overlays
        /// the scroll fades. <paramref name="fadeBaseColor"/> should match the
        /// background behind the child; defaults to the window background.
        /// <paramref name="edgePaddingX"/>, when non-negative, spans the fades
        /// from the parent window's inner edge inset by that amount instead of
        /// the child bounds. <paramref name="fadeSpanX"/> sets an explicit
        /// horizontal fade span (min.X, max.X); takes precedence over
        /// <paramref name="edgePaddingX"/>. <paramref name="fadeMinY"/> lets the
        /// top fade start above the child bounds.
        /// <paramref name="bottomInset"/> adds extra spacing below the last item.
        /// </summary>
        public static void EndScrollArea(Vector4? fadeBaseColor = null, float edgePaddingX = -1f,
            Vector2? fadeSpanX = null, float? fadeMinY = null, float bottomInset = 0f)
        {
            var state = ScrollAreaStack.Count > 0 ? ScrollAreaStack.Pop() : new ScrollAreaState();

            if (state.Begun)
            {
                if (state.BoundsPushed)
                {
                    ContentBounds.Pop();
                }
                if (state.ContentInset > 0f)
                {
                    ImGui.Unindent(state.ContentInset);
                }
                ImGuiHelpers.ScaledDummy(0, ScrollAreaContentInset);
                if (bottomInset > 0f)
                {
                    ImGui.Dummy(new Vector2(0f, bottomInset));
                }
                var scrollY = ImGui.GetScrollY();
                var scrollMaxY = ImGui.GetScrollMaxY();
                var childHovered = ImGui.IsWindowHovered();
                var min = ImGui.GetWindowPos();
                var max = min + ImGui.GetWindowSize();
                if (state.ClipPushed)
                {
                    ImGui.PopClipRect();
                }
                ImGui.EndChild();

                // Nested scroll chaining: once this child cannot scroll further
                // in the wheel direction ImGui still consumes the input. Forward
                // the delta to the parent window so scrolling continues. The
                // forward must clamp to the parent's own range — an uncapped
                // SetScrollY overscrolls non-scrollable hosts and slides their
                // nested children past the window edges. It must also never
                // reach a nav-rail host: its rail+content children are
                // edge-to-edge by design, so any scroll range it reports is
                // incidental overflow, and programmatic SetScrollY would slide
                // the whole embedded layout out of the window even though the
                // host is NoScrollWithMouse.
                if (childHovered)
                {
                    var wheel = ImGui.GetIO().MouseWheel;
                    var atTop = scrollY <= 0.5f;
                    var atBottom = scrollY >= scrollMaxY - 0.5f;
                    if (MathF.Abs(wheel) > 0.001f && ((wheel < 0f && atBottom) || (wheel > 0f && atTop))
                        && !IsNavRailHost(ImGui.GetWindowPos(), ImGui.GetWindowSize()))
                    {
                        // Only chain into a parent with a meaningful scroll
                        // range — a fit or near-fit host (fixed clip regions,
                        // cards at the fill bound) would otherwise receive
                        // micro SetScrollY writes that jitter it by a few px.
                        var parentMaxY = ImGui.GetScrollMaxY();
                        if (parentMaxY > ImGui.GetTextLineHeightWithSpacing())
                        {
                            var parentTarget = ImGui.GetScrollY()
                                - wheel * ImGui.GetTextLineHeightWithSpacing() * 4f;
                            ImGui.SetScrollY(Math.Clamp(parentTarget, 0f, parentMaxY));
                        }
                    }
                }

                // Every ImGui window (including child windows) owns a draw list,
                // and lists composite parent-first, then children in creation
                // order. Overlays drawn inside the scroll child would render
                // under any nested child content, so a sibling overlay child
                // created afterwards carries all chrome drawing: it composites
                // above the whole subtree while staying inside the window
                // z-order.
                var resumeCursor = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(min);
                ImGui.BeginChild(state.Id + "##scroll-overlay", max - min, false,
                    ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs
                    | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                    | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings);
                if (state.Border)
                {
                    Chrome.CornerSpillMasks(min, max, state.ParentMin, state.ParentMax);
                    Chrome.RoundedFrame(min, max, ImGui.GetStyle().ChildRounding,
                        MathF.Max(ImGui.GetStyle().ChildBorderSize, 1f), openTop: state.HideTopBorder);
                }
                Chrome.ScrollFades(min, max, scrollY, scrollMaxY, state.Border, state.TopInset,
                    state.ParentMin, state.ParentMax, fadeBaseColor, edgePaddingX, fadeSpanX, fadeMinY);
                var styleVars = ImGui.GetStyle();
                var cornerReach = MathF.Max(styleVars.WindowBorderSize,
                        MathF.Max(styleVars.WindowPadding.X, styleVars.WindowPadding.Y))
                    + 4f * SpheneUI.Scale;
                // Only the outermost scroll area (a direct window child) may own
                // the frame corner: nested areas reach their parent child's
                // edge, whose "corner" sits mid-window and is not a resize
                // affordance.
                var ownsCorner = state.Depth == 0
                    && state.ParentMax.X - max.X <= cornerReach
                    && state.ParentMax.Y - max.Y <= cornerReach;
                Chrome.RedrawWindowBorder(min, max, state.ParentMin, state.ParentMax, cornerReach);
                // The grip stamp goes last so its corner triangle covers the
                // re-stroked border instead of the border cutting through it.
                if (!state.Border && ownsCorner)
                {
                    Chrome.GripCorner(state.ParentMax, state.GripHeld, state.GripAnim,
                        out var held, out var anim);
                    state.GripHeld = held;
                    state.GripAnim = anim;
                }
                ImGui.EndChild();
                ImGui.SetCursorScreenPos(resumeCursor);
            }
            else
            {
                ImGui.EndChild();
            }
        }

        private static void GetScrollInsets(bool border, out float insetX, out float insetY)
        {
            var scale = SpheneUI.Scale;
            var style = ImGui.GetStyle();
            if (border)
            {
                insetX = MathF.Max(style.ChildRounding, 3f * scale);
                insetY = MathF.Max(style.ChildBorderSize + 1f * scale, 2f * scale);
            }
            else
            {
                insetX = 0f;
                insetY = 0f;
            }
        }

        /// <summary>
        /// Draws a separator across the current scroll area spanning from the
        /// parent window's inner edge, inset by <paramref name="edgePadding"/>
        /// per side. Falls back to a regular separator outside of a scroll area.
        /// </summary>
        public static void ScrollAreaSeparator(float edgePadding)
        {
            if (ScrollAreaStack.Count == 0)
            {
                ImGui.Separator();
                return;
            }
            var state = ScrollAreaStack.Peek();
            var style = ImGui.GetStyle();
            var border = MathF.Max(style.WindowBorderSize, 0f);
            var spacing = MathF.Max(style.ItemSpacing.Y, 1f);
            var y = ImGui.GetCursorScreenPos().Y + spacing * 0.5f;
            var minX = state.ParentMin.X + border + edgePadding;
            var maxX = state.ParentMax.X - border - edgePadding;
            if (maxX > minX)
            {
                ImGui.GetWindowDrawList().AddLine(new Vector2(minX, y),
                    new Vector2(maxX, y), ImGui.GetColorU32(ImGuiCol.Separator));
            }
            ImGui.Dummy(new Vector2(0f, spacing));
        }

        /// <summary>
        /// Segmented tab strip: a row of rounded pill buttons in the accent
        /// language of the cards. The selection persists in the window's state
        /// storage keyed by <paramref name="id"/> — pass
        /// <paramref name="preferredIndex"/> ≥ 0 to force-select a tab once.
        /// Unlike a real ImGui tab bar the strip never reacts to the mouse
        /// wheel and never emits overflow scroll buttons; buttons that do not
        /// fit wrap to the next line. Returns the active tab index — the
        /// caller renders that tab's content. With
        /// <paramref name="scrollIntoViewOnChange"/> a tab switch scrolls the
        /// strip to the top of the view.
        /// </summary>
        public static int TabStrip(string id, IReadOnlyList<string> labels,
            int preferredIndex = -1, bool scrollIntoViewOnChange = false)
        {
            if (labels.Count == 0)
            {
                return -1;
            }

            var scale = SpheneUI.Scale;
            var style = ImGui.GetStyle();
            var selected = TabStripSelection(id, labels.Count, preferredIndex);
            var previous = selected;
            var padX = style.FramePadding.X + 8f * scale;

            using var frameStyle = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding,
                    ImGui.GetFrameHeight() * 0.5f)
                .Push(ImGuiStyleVar.FrameBorderSize, 1f * scale);

            for (var i = 0; i < labels.Count; i++)
            {
                var width = ImGui.CalcTextSize(labels[i]).X + padX * 2f;
                if (i > 0)
                {
                    Controls.SameLineIfFits(width);
                }
                if (TabStripButton(id, i, labels[i], i == selected, new Vector2(width, 0f)))
                {
                    selected = i;
                }
            }

            if (scrollIntoViewOnChange && selected != previous)
            {
                ImGui.SetScrollHereY(0f);
            }
            ImGui.GetStateStorage().SetInt(ImGui.GetID(id), selected);
            return selected;
        }

        /// <summary>
        /// <see cref="TabStrip"/> variant for many or long labels: the buttons
        /// stack as a fixed-width left column (sized to the longest label).
        /// Call <see cref="ImGui.SameLine"/> after it and render the tab
        /// content to the right of the column.
        /// </summary>
        public static int VerticalTabStrip(string id, IReadOnlyList<string> labels,
            int preferredIndex = -1)
        {
            if (labels.Count == 0)
            {
                return -1;
            }

            var selected = TabStripSelection(id, labels.Count, preferredIndex);
            var clicked = DrawVerticalNavColumn(id, labels, selected);
            if (clicked >= 0)
            {
                selected = clicked;
            }

            ImGui.GetStateStorage().SetInt(ImGui.GetID(id), selected);
            return selected;
        }

        /// <summary>
        /// Controlled <see cref="VerticalTabStrip"/> variant for scrollspy-style
        /// navigation: <paramref name="activeIndex"/> selects the highlighted
        /// entry (driven externally, e.g. the scroll position over a stacked
        /// section list) and the return value is the clicked index or -1.
        /// Nothing is persisted — the caller owns the active state and applies
        /// the scroll.
        /// </summary>
        public static int VerticalTabNav(string id, IReadOnlyList<string> labels, int activeIndex)
        {
            if (labels.Count == 0)
            {
                return -1;
            }

            return DrawVerticalNavColumn(id, labels,
                Math.Clamp(activeIndex, 0, labels.Count - 1));
        }

        private static int DrawVerticalNavColumn(string id, IReadOnlyList<string> labels,
            int activeIndex)
        {
            var scale = SpheneUI.Scale;
            var padX = ImGui.GetStyle().FramePadding.X + 8f * scale;
            var width = 0f;
            for (var i = 0; i < labels.Count; i++)
            {
                width = MathF.Max(width, ImGui.CalcTextSize(labels[i]).X);
            }
            width += padX * 2f;

            var clicked = -1;
            using var frameStyle = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding,
                    AccentCardRounding * scale)
                .Push(ImGuiStyleVar.FrameBorderSize, 1f * scale);

            for (var i = 0; i < labels.Count; i++)
            {
                if (TabStripButton(id, i, labels[i], i == activeIndex, new Vector2(width, 0f)))
                {
                    clicked = i;
                }
            }

            return clicked;
        }

        private static int TabStripSelection(string id, int count, int preferredIndex)
            => Math.Clamp(
                preferredIndex >= 0 && preferredIndex < count
                    ? preferredIndex
                    : ImGui.GetStateStorage().GetInt(ImGui.GetID(id), 0),
                0, count - 1);

        private static bool TabStripButton(string id, int index, string label, bool isActive,
            Vector2 size)
        {
            var accent = Theme.Current.AccentCyan;
            var textDim = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
            var textActive = new Vector4(
                accent.X + (1f - accent.X) * 0.55f,
                accent.Y + (1f - accent.Y) * 0.55f,
                accent.Z + (1f - accent.Z) * 0.55f, 1f);

            using var colors = ImRaii.PushColor(ImGuiCol.Button, isActive
                    ? new Vector4(accent.X, accent.Y, accent.Z, 0.30f)
                    : new Vector4(1f, 1f, 1f, 0.05f))
                .Push(ImGuiCol.ButtonHovered, isActive
                    ? new Vector4(accent.X, accent.Y, accent.Z, 0.42f)
                    : new Vector4(1f, 1f, 1f, 0.12f))
                .Push(ImGuiCol.ButtonActive, isActive
                    ? new Vector4(accent.X, accent.Y, accent.Z, 0.52f)
                    : new Vector4(1f, 1f, 1f, 0.18f))
                .Push(ImGuiCol.Border, isActive
                    ? new Vector4(accent.X, accent.Y, accent.Z, 0.80f)
                    : new Vector4(1f, 1f, 1f, 0.14f))
                .Push(ImGuiCol.Text, isActive ? textActive : textDim);

            return ImGui.Button($"{label}##{id}{index}", size);
        }

        /// <summary>
        /// Draws a sticky section header pinned inside the frame's top edge.
        /// The first label is pinned from the start; when the next section's
        /// own label scrolls up it pushes the pinned band out of the way and
        /// takes over. <paramref name="labels"/> must contain each label's
        /// current screen-space center Y plus its accent color, recorded while
        /// drawing the scrolled content. <paramref name="pinCenterY"/>
        /// optionally sets the band's resting center on screen.
        /// </summary>
        public static void PinnedSectionLabel(Vector2 frameMin, Vector2 frameMax,
            IReadOnlyList<(string Label, float CenterY, Vector4 Accent)> labels,
            Vector2 surfaceMin, Vector2 surfaceMax, Vector4? baseColor = null,
            float? pinCenterY = null)
        {
            if (labels.Count == 0)
            {
                return;
            }

            var scale = SpheneUI.Scale;
            var lineHeight = ImGui.GetTextLineHeight();
            var bandHeight = lineHeight + 8f * scale;
            var pinCenter = pinCenterY ?? (frameMin.Y + bandHeight * 0.5f);

            var current = -1;
            var next = -1;
            for (var i = 0; i < labels.Count; i++)
            {
                if (labels[i].CenterY <= pinCenter)
                {
                    current = i;
                }
                else
                {
                    next = i;
                    break;
                }
            }
            if (current < 0)
            {
                // The first label is pinned even before it has scrolled past.
                current = 0;
                next = labels.Count > 1 ? 1 : -1;
            }

            var centerY = pinCenter;
            if (next >= 0)
            {
                centerY = MathF.Min(centerY, labels[next].CenterY - (bandHeight + lineHeight) * 0.5f);
            }

            var drawList = ImGui.GetWindowDrawList();
            var rounding = MathF.Max(ImGui.GetStyle().ChildRounding, 2f);
            var label = labels[current].Label;
            var textWidth = ImGui.CalcTextSize(label).X;
            var stripMin = new Vector2(frameMin.X + rounding * 0.5f, centerY - bandHeight * 0.5f);
            var stripMax = new Vector2(frameMax.X - rounding * 0.5f, centerY + bandHeight * 0.5f);
            var stripBase = baseColor ?? Chrome.WindowSurfaceColorAt(surfaceMin, surfaceMax, centerY);
            var stripColor = baseColor.HasValue
                ? ColorUtils.WithAlpha(baseColor.Value, 1f)
                : Vector4.Lerp(stripBase, Theme.Current.HeaderBg, 0.35f);
            stripColor.W = MathF.Max(stripColor.W, 0.9f);

            // Clip to the frame so the band slides out cleanly when pushed away
            drawList.PushClipRect(new Vector2(frameMin.X, frameMin.Y), frameMax, true);
            drawList.AddRectFilled(stripMin, stripMax, ColorUtils.ToU32(stripColor),
                MathF.Min(rounding * 0.5f, bandHeight * 0.5f));
            drawList.AddText(new Vector2(
                frameMin.X + (frameMax.X - frameMin.X - textWidth) * 0.5f,
                centerY - lineHeight * 0.5f),
                ColorUtils.ToU32(labels[current].Accent), label);
            drawList.PopClipRect();
        }

        /// <summary>
        /// Thin horizontal divider spanning the bounded content width, drawn
        /// in the design border color — replaces raw ImGui.Separator() inside
        /// settings content so dividers follow the same token everywhere.
        /// </summary>
        public static void Separator()
        {
            var scale = SpheneUI.Scale;
            var cursor = ImGui.GetCursorScreenPos();
            var width = Controls.BoundedAvailX();
            var thickness = MathF.Max(1f, scale);
            var gap = Metrics.Gap * 0.5f;
            var y = cursor.Y + gap;
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(cursor.X, y), new Vector2(cursor.X + width, y),
                ColorUtils.ToU32(Colors.Border with { W = 0.35f }), thickness);
            ImGui.Dummy(new Vector2(width, gap * 2f + thickness));
        }

        /// <summary>Horizontal divider with a centered label flanked by line segments.</summary>
        public static void SectionSeparator(string text)
        {
            var drawList = ImGui.GetWindowDrawList();
            var cursor = ImGui.GetCursorScreenPos();
            var availWidth = ImGui.GetContentRegionAvail().X;
            var height = ImGui.GetTextLineHeightWithSpacing();

            var textSize = ImGui.CalcTextSize(text);
            var lineY = cursor.Y + height / 2;
            var lineColor = ImGui.GetColorU32(ImGuiCol.Separator);

            var leftLineEnd = cursor.X + (availWidth - textSize.X) / 2 - 5;
            if (leftLineEnd > cursor.X)
            {
                drawList.AddLine(new Vector2(cursor.X, lineY), new Vector2(leftLineEnd, lineY), lineColor);
            }

            ImGui.SetCursorScreenPos(new Vector2(cursor.X + (availWidth - textSize.X) / 2, cursor.Y));
            ImGui.TextUnformatted(text);

            var rightLineStart = cursor.X + (availWidth + textSize.X) / 2 + 5;
            if (rightLineStart < cursor.X + availWidth)
            {
                drawList.AddLine(new Vector2(rightLineStart, lineY), new Vector2(cursor.X + availWidth, lineY), lineColor);
            }

            ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + height));
        }

        /// <summary>Separator padded with <see cref="Spacing.X8"/>-level breathing room on both sides.</summary>
        public static void DistanceSeparator()
        {
            ImGuiHelpers.ScaledDummy(0, 5);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(0, 5);
        }

        /// <summary>
        /// Section header (crystal accent underline) wrapping indented content
        /// — the general-purpose "subsection with title" primitive.
        /// </summary>
        public static void Section(string title, Action content, bool spacing = true)
        {
            if (spacing)
            {
                ImGui.Spacing();
            }

            Text.Colored(title, Palette.CrystalBlue);

            var drawList = ImGui.GetWindowDrawList();
            var titleSize = ImGui.CalcTextSize(title);
            var lineStart = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + 2.0f);
            var lineEnd = new Vector2(lineStart.X + titleSize.X, lineStart.Y);
            var lineColor = ColorUtils.ToU32(ColorUtils.WithAlpha(Palette.CrystalBlue, 0.6f));
            drawList.AddLine(lineStart, lineEnd, lineColor, 2.0f);

            ImGui.Spacing();

            using (ImRaii.PushIndent(12.0f))
            {
                content();
            }

            if (spacing)
            {
                ImGui.Spacing();
            }
        }

        /// <summary>
        /// Info callout — accent icon + wrapped text inside a tonal rounded
        /// box, spanning the content width. Pass <paramref name="title"/> for
        /// a colored title line above neutral body text
        /// (info/warning/error boxes). The universal "hint/note" element of
        /// the design system — borderless like the cards.
        /// </summary>
        public static void Callout(string text, FontAwesomeIcon icon = FontAwesomeIcon.InfoCircle,
            Vector4? color = null, string? title = null)
        {
            var scale = SpheneUI.Scale;
            var accent = color ?? Palette.CrystalBlue;
            var hasTitle = !string.IsNullOrEmpty(title);

            var drawList = ImGui.GetWindowDrawList();
            var startPos = ImGui.GetCursorScreenPos();
            // align with the card edge (scroll-area bound), not the raw
            // content region — raw avail reaches further right than cards do
            var boxWidth = CardAlignedAvailX();
            var padX = 12.0f * scale;
            var padY = 8.0f * scale;
            var radius = AccentCardRounding * scale;
            var wrapWidth = MathF.Max(1f, boxWidth - padX * 2f);

            var textSize = ImGui.CalcTextSize(text, wrapWidth: wrapWidth);
            var boxHeight = padY * 2f + textSize.Y
                + (hasTitle ? ImGui.GetTextLineHeightWithSpacing() : 0f);
            var boxMax = new Vector2(startPos.X + boxWidth, startPos.Y + boxHeight);

            drawList.AddRectFilled(startPos, boxMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.07f)), radius);

            var innerMin = startPos + new Vector2(padX, padY);
            ImGui.SetCursorScreenPos(innerMin);
            SpheneUI.Icons.Icon(icon, accent);
            ImGui.SameLine();
            if (hasTitle)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, accent))
                {
                    ImGui.TextUnformatted(title);
                }
                ImGui.SetCursorScreenPos(new Vector2(innerMin.X, ImGui.GetCursorScreenPos().Y));
            }

            using (ImRaii.PushColor(ImGuiCol.Text, hasTitle ? Colors.TextPrimary : accent))
            {
                // PushTextWrapPos is window-local, not screen space.
                var wrapLocalX = startPos.X - ImGui.GetWindowPos().X + boxWidth - padX;
                ImGui.PushTextWrapPos(wrapLocalX);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
            }

            ImGui.SetCursorScreenPos(new Vector2(startPos.X, boxMax.Y + Spacing.X4.Scaled));
        }

        /// <summary>Width <see cref="Badge"/> occupies for <paramref name="text"/>.</summary>
        public static float BadgeWidth(string text)
            => ImGui.CalcTextSize(text).X + 14f * SpheneUI.Scale;

        /// <summary>Height <see cref="Badge"/> occupies on the row.</summary>
        public static float BadgeHeight => ImGui.GetTextLineHeight() + 4f * SpheneUI.Scale;

        /// <summary>
        /// Inline tag pill: tinted rounded chip drawn at the cursor (soft
        /// accent underlay + hairline border) — e.g. "NEW"/"BETA" markers on
        /// list rows. Advances the cursor by the chip width and returns it.
        /// </summary>
        public static float Badge(string text, Vector4 color)
        {
            var scale = SpheneUI.Scale;
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var chipW = BadgeWidth(text);
            var chipH = BadgeHeight;
            var padX = 7f * scale;
            var padY = 2f * scale;
            var chipMax = pos + new Vector2(chipW, chipH);
            drawList.AddRectFilled(pos, chipMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(color, 0.14f)), chipH * 0.5f);
            drawList.AddRect(pos, chipMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(color, 0.35f)), chipH * 0.5f);
            drawList.AddText(pos + new Vector2(padX, padY),
                ColorUtils.ToU32(Colors.TextPrimary), text);
            ImGui.SetCursorScreenPos(pos + new Vector2(chipW, 0f));
            return chipW;
        }

        /// <summary>Draws <paramref name="imguiDrawAction"/> inside a group with a rounded frame.</summary>
        public static void Group(Action imguiDrawAction, float rounding = 5f, float? expectedWidth = null)
        {
            var cursorPos = ImGui.GetCursorPos();
            using (ImRaii.Group())
            {
                if (expectedWidth != null)
                {
                    ImGui.Dummy(new(expectedWidth.Value, 0));
                    ImGui.SetCursorPos(cursorPos);
                }

                imguiDrawAction.Invoke();
            }

            ImGui.GetWindowDrawList().AddRect(
                ImGui.GetItemRectMin() - ImGui.GetStyle().ItemInnerSpacing,
                ImGui.GetItemRectMax() + ImGui.GetStyle().ItemInnerSpacing,
                ColorUtils.ToU32(Dalamud.Interface.Colors.ImGuiColors.DalamudGrey2), rounding);
        }

        /// <summary>Vertical gap on the spacing grid.</summary>
        public static void VerticalSpace(Space space) => ImGui.Dummy(new Vector2(0f, space.Scaled));

        /// <summary>One themed vertical gap — the standard row break.</summary>
        public static void VerticalSpace() => ImGui.Spacing();

        /// <summary>Square gap reserving X and Y on the spacing grid.</summary>
        public static void Space(Space size) => ImGui.Dummy(new Vector2(size.Scaled));

        /// <summary>Gap reserving X and Y on the spacing grid independently.</summary>
        public static void Space(Space x, Space y) => ImGui.Dummy(new Vector2(x.Scaled, y.Scaled));

        /// <summary>Horizontal gap on the spacing grid.</summary>
        public static void HorizontalSpace(Space space) => ImGui.Dummy(new Vector2(space.Scaled, 0f));

        /// <summary>Indented content scope with a grid spacing width.</summary>
        public static IDisposable IndentScope(Space space) => ImRaii.PushIndent(space.Scaled);

        /// <summary>Tree node that runs <paramref name="drawOnOpened"/> while open.</summary>
        public static void Tree(string leafName, Action drawOnOpened,
            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
        {
            using var tree = ImRaii.TreeNode(leafName, flags);
            if (tree)
            {
                drawOnOpened();
            }
        }

        /// <summary>
        /// Collapsible section in the segmented-pill language: a tonal header
        /// row (square chevron zone + seam + label, optional icon and a muted
        /// right-aligned hint) that toggles a bordered body sharing the
        /// header's bottom hairline. State persists per window via
        /// <see cref="ImGui.GetStateStorage"/> keyed on <paramref name="label"/>.
        /// <paramref name="setOpen"/> force-opens the section for the frame —
        /// the deep-link equivalent of SetNextItemOpen; the user can still
        /// close it afterwards. An "##id" suffix on the label is hidden.
        /// </summary>
        public static bool Collapsible(string label, Action drawContent,
            FontAwesomeIcon? icon = null, string? hint = null,
            Vector4? accent = null, bool defaultOpen = false, bool setOpen = false)
        {
            var acc = accent ?? Colors.Accent;
            var scale = SpheneUI.Scale;
            var w = Controls.BoundedAvailX();
            var h = ImGui.GetFrameHeight();
            var radius = Metrics.ButtonRadius;
            var padX = ImGui.GetStyle().ItemInnerSpacing.X;
            var displayIdx = label.IndexOf("##", StringComparison.Ordinal);
            var display = displayIdx >= 0 ? label[..displayIdx] : label;

            var storage = ImGui.GetStateStorage();
            var key = ImGui.GetID($"collapsible:{label}");
            var open = storage.GetBool(key, defaultOpen);
            if (setOpen)
            {
                open = true;
                storage.SetBool(key, true);
            }

            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(w, h);
            var drawList = ImGui.GetWindowDrawList();

            ImGui.PushID(label);
            var clicked = ImGui.InvisibleButton("##collapsible", new Vector2(w, h));
            ImGui.PopID();
            if (clicked)
            {
                open = !open;
                storage.SetBool(key, open);
            }
            var hovered = ImGui.IsItemHovered();

            var corners = open ? ImDrawFlags.RoundCornersTop : ImDrawFlags.RoundCornersAll;
            var scrim = ColorUtils.LerpColor(Colors.FrameBg,
                new Vector4(0.02f, 0.02f, 0.035f, 1f), 0.5f);
            scrim.W = MathF.Max(scrim.W, 0.66f);
            var fill = hovered
                ? ColorUtils.WithAlpha(acc, 0.11f)
                : ColorUtils.WithAlpha(Vector4.One, open ? 0.085f : 0.055f);
            drawList.AddRectFilled(min, max, ColorUtils.ToU32(scrim), radius, corners);
            drawList.AddRectFilled(min, max, ColorUtils.ToU32(fill), radius, corners);
            drawList.AddRect(min, max, ColorUtils.ToU32(
                ColorUtils.WithAlpha(Colors.Border, hovered ? 0.5f : 0.3f)), radius, corners);

            // Chevron zone + seam — the same left-zone language as SegCore.
            var zoneW = h;
            using (SpheneUI.IconFont.Push())
            {
                var glyph = (open ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight).ToIconString();
                var glyphSize = ImGui.CalcTextSize(glyph);
                var glyphCol = open || hovered
                    ? ColorUtils.WithAlpha(acc, 0.95f)
                    : ColorUtils.WithAlpha(Colors.TextMuted, 0.9f);
                drawList.AddText(min + new Vector2((zoneW - glyphSize.X) * 0.5f,
                        (h - glyphSize.Y) * 0.5f), ColorUtils.ToU32(glyphCol), glyph);
            }
            var seamInset = h * 0.22f;
            drawList.AddLine(new Vector2(min.X + zoneW - 0.5f, min.Y + seamInset),
                new Vector2(min.X + zoneW - 0.5f, max.Y - seamInset),
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, open ? 0.18f : 0.13f)));

            var textX = min.X + zoneW + 10f * scale;
            if (icon.HasValue)
            {
                using (SpheneUI.IconFont.Push())
                {
                    var iconGlyph = icon.Value.ToIconString();
                    var iconSize = ImGui.CalcTextSize(iconGlyph);
                    drawList.AddText(new Vector2(textX, min.Y + (h - iconSize.Y) * 0.5f),
                        ColorUtils.ToU32(ColorUtils.WithAlpha(acc, 0.9f)), iconGlyph);
                    textX += iconSize.X + 6f * scale;
                }
            }

            var labelSize = ImGui.CalcTextSize(display);
            var textY = min.Y + (h - labelSize.Y) * 0.5f;
            drawList.AddText(new Vector2(textX, textY),
                ColorUtils.ToU32(open ? ColorUtils.LerpColor(acc, Vector4.One, 0.25f) : Colors.TextPrimary), display);

            if (!string.IsNullOrEmpty(hint))
            {
                var hintW = ImGui.CalcTextSize(hint).X;
                drawList.AddText(new Vector2(max.X - padX * 2f - hintW, textY),
                    ColorUtils.ToU32(Colors.TextMuted), hint);
            }

            if (!open)
            {
                return false;
            }

            // Body: bordered region sharing the header's bottom hairline. The
            // stroke is deferred until after the content so the group reports
            // its real height — fills stay header-only so nothing overpaints.
            var bodyTop = max.Y;
            var padY = ImGui.GetStyle().ItemInnerSpacing.Y;
            ImGui.SetCursorScreenPos(new Vector2(min.X + padX, bodyTop + padY));
            ContentBounds.Push(new Vector2(min.X + padX, max.X - padX));
            using (ImRaii.Group())
            {
                drawContent();
            }
            ContentBounds.Pop();
            var bodyMax = new Vector2(max.X, ImGui.GetItemRectMax().Y + padY);
            drawList.AddRect(new Vector2(min.X, bodyTop), bodyMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Border, 0.3f)),
                radius, ImDrawFlags.RoundCornersBottom);
            ImGui.SetCursorScreenPos(new Vector2(min.X, bodyMax.Y));
            return true;
        }

        /// <summary>SetNextItemWidth in unscaled units (multiplied by the UI scale).</summary>
        public static void ScaledNextItemWidth(float width)
            => ImGui.SetNextItemWidth(width * SpheneUI.Scale);

        /// <summary>SameLine with an unscaled offset (multiplied by the UI scale).</summary>
        public static void ScaledSameLine(float offset)
            => ImGui.SameLine(offset * SpheneUI.Scale);

        /// <summary>Window content region width.</summary>
        public static float WindowContentWidth()
            => ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;

        /// <summary>Centers the next window on the main viewport.</summary>
        public static void CenterNextWindow(float width, float height, ImGuiCond cond = ImGuiCond.None)
        {
            var center = ImGui.GetMainViewport().GetCenter();
            ImGui.SetNextWindowPos(new Vector2(center.X - width / 2, center.Y - height / 2), cond);
        }

        /// <summary>Resizes the current window to a scaled width, auto-computed height.</summary>
        public static void SetScaledWindowSize(float width, bool centerWindow = true)
        {
            var newLineHeight = ImGui.GetCursorPosY();
            ImGui.NewLine();
            newLineHeight = ImGui.GetCursorPosY() - newLineHeight;
            var y = ImGui.GetCursorPos().Y + ImGui.GetWindowContentRegionMin().Y
                - newLineHeight * 2 - ImGui.GetStyle().ItemSpacing.Y;

            SetScaledWindowSize(width, y, centerWindow, scaledHeight: true);
        }

        /// <summary>Resizes the current window to scaled (or explicit scaled) dimensions.</summary>
        public static void SetScaledWindowSize(float width, float height, bool centerWindow = true,
            bool scaledHeight = false)
        {
            ImGui.SameLine();
            var x = width * SpheneUI.Scale;
            var y = scaledHeight ? height : height * SpheneUI.Scale;

            if (centerWindow)
            {
                CenterWindow(x, y);
            }

            ImGui.SetWindowSize(new Vector2(x, y));
        }

        private static void CenterWindow(float width, float height, ImGuiCond cond = ImGuiCond.None)
        {
            var center = ImGui.GetMainViewport().GetCenter();
            ImGui.SetWindowPos(new Vector2(center.X - width / 2, center.Y - height / 2), cond);
        }

        private sealed class NavRailState
        {
            public string Id = string.Empty;
            public Vector2 RailMin;
            public Vector2 RailMax;
            public Vector4 RailBg;
            public Vector2 ContentTop;
            public Vector2 Available;
            public float Width;
            public Vector2 WindowMin;
            public float WindowRight;
            public float WindowBottom;
            public bool Pattern;
        }

        private static readonly Stack<NavRailState> NavRailStack = new();

        // Windows hosting a rail+content layout are fixed surfaces: their
        // children already reach the frame edges, so any scroll range they
        // report is incidental padding overflow — never real scrollable
        // content. BeginNavRail registers the host's rect every frame —
        // entries expire after a frame so rails that stop being drawn do
        // not linger — and EndScrollArea refuses to chain wheel input into
        // a registered host.
        private static readonly List<(Vector4 Rect, int Frame)> _navRailHostRects = new();

        private static bool IsNavRailHost(Vector2 pos, Vector2 size)
        {
            var frame = ImGui.GetFrameCount();
            for (var i = _navRailHostRects.Count - 1; i >= 0; i--)
            {
                var (rect, registered) = _navRailHostRects[i];
                if (registered < frame - 1)
                {
                    _navRailHostRects.RemoveAt(i);
                    continue;
                }
                if (MathF.Abs(rect.X - pos.X) < 0.5f && MathF.Abs(rect.Y - pos.Y) < 0.5f
                    && MathF.Abs(rect.Z - size.X) < 0.5f && MathF.Abs(rect.W - size.Y) < 0.5f)
                {
                    return true;
                }
            }
            return false;
        }

        private static void RegisterNavRailHost(Vector2 pos, Vector2 size)
            => _navRailHostRects.Add((new Vector4(pos.X, pos.Y, size.X, size.Y), ImGui.GetFrameCount()));

        /// <summary>
        /// Rail width that fits every <paramref name="labels"/> entry at the
        /// current font: widest label plus item padding, the child's window
        /// padding and a small breathing margin. Pass all item labels so the
        /// rail never clips one.
        /// </summary>
        /// <summary>Label padding inside <see cref="NavRailItem"/> pills.</summary>
        private const float NavRailItemPadX = 10f;

        /// <summary>Nav item pill height — matches the header nav buttons.</summary>
        private const float NavRailItemHeight = 30f;

        /// <summary>Inner horizontal margin of a nav rail band (unscaled) —
        /// pills float inside the band instead of touching its edges.</summary>
        public const float NavRailPadX = 10f;

        public static float NavRailWidth(IReadOnlyList<string> labels)
        {
            var style = ImGui.GetStyle();
            var maxText = 0f;
            foreach (var label in labels)
            {
                var size = ImGui.CalcTextSize(label);
                if (size.X > maxText)
                {
                    maxText = size.X;
                }
            }
            var padX = MathF.Max(NavRailItemPadX * SpheneUI.Scale, style.FramePadding.X * 0.65f);
            return maxText + padX * 2f + NavRailPadX * 2f * SpheneUI.Scale + 2f * SpheneUI.Scale;
        }

        /// <summary>
        /// <see cref="NavRailWidth(IReadOnlyList{string})"/> for icon items:
        /// widest icon glyph plus item padding — icon rails show a tooltip
        /// instead of a label, so the rail stays narrow.
        /// </summary>
        public static float NavRailWidth(IReadOnlyList<(FontAwesomeIcon Icon, string Label)> items)
        {
            var style = ImGui.GetStyle();
            var iconW = 0f;
            using (Icons.PushFont())
            {
                foreach (var (icon, _) in items)
                {
                    var w = ImGui.CalcTextSize(icon.ToIconString()).X;
                    if (w > iconW)
                    {
                        iconW = w;
                    }
                }
            }
            var padX = MathF.Max(NavRailItemPadX * SpheneUI.Scale, style.FramePadding.X * 0.65f);
            return iconW + padX * 2f + NavRailPadX * 2f * SpheneUI.Scale + 2f * SpheneUI.Scale;
        }

        /// <summary>
        /// Rail width for <see cref="NavRailSegmentItem"/> rows: square icon
        /// zone plus the widest label with its padding, so the segmented pills
        /// never clip text.
        /// </summary>
        public static float NavRailSegmentedWidth(IReadOnlyList<(FontAwesomeIcon Icon, string Label)> items)
        {
            var scale = SpheneUI.Scale;
            var maxText = 0f;
            foreach (var (_, label) in items)
            {
                var w = ImGui.CalcTextSize(label).X;
                if (w > maxText)
                {
                    maxText = w;
                }
            }
            return NavRailPadX * 2f * scale
                + NavRailItemHeight * scale
                + NavRailItemPadX * scale
                + maxText
                + 14f * scale;
        }

        /// <summary>Scaled height of one <see cref="NavRailSegmentItem"/> row.</summary>
        public static float NavRailItemHeightPx => NavRailItemHeight * SpheneUI.Scale;

        /// <summary>Scaled vertical footprint of <see cref="NavRailDivider"/> — pad, line, pad.</summary>
        public static float NavRailDividerHeight => 11.5f * SpheneUI.Scale;

        /// <summary>
        /// The solid color painted behind the nav rail. Hosts that draw a
        /// full-width header band across the window should paint it in this
        /// color so the rail and the header merge seamlessly.
        /// </summary>
        public static Vector4 NavRailBandColor()
        {
            var theme = SpheneTheme.Current;
            var railBg = ColorUtils.LerpColor(theme.WindowBg, Vector4.One, 0.07f);
            return ColorUtils.WithAlpha(railBg, MathF.Min(1f, theme.WindowBg.W + 0.08f));
        }

        /// <summary>
        /// Opens a navigation rail: a slightly lighter band from below the
        /// title bar to the window's bottom edge, holding nav items drawn with
        /// <see cref="NavRailItem"/>. Returns the rail width for
        /// <see cref="BeginNavContent"/>. Close with <see cref="EndNavRail"/>
        /// before opening the content area. When <paramref name="embedded"/>
        /// is set, the rail anchors to the current child window's top edge
        /// instead of the parent window's title bar — used when a rail layout
        /// is rendered inside another window's content (e.g. Sphene Hub).
        /// <paramref name="topInset"/> reserves a pinned strip at the rail top —
        /// scrolled content is clipped below it (see <see cref="BeginScrollArea"/>).
        /// <paramref name="backdropPattern"/> draws the star-chart backdrop
        /// over the content region's backing gradient — that solid fill
        /// would otherwise hide the window-level pattern entirely.
        /// </summary>
        public static float BeginNavRail(string id, float width, bool embedded = false,
            float topInset = 0f, bool backdropPattern = false)
        {
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            RegisterNavRailHost(windowPos, windowSize);
            // Hard-reset residual scroll: a host that still carries an offset
            // from before it held a rail would draw its children shifted.
            ImGui.SetScrollY(0f);
            var contentTop = ImGui.GetCursorScreenPos();
            var available = ImGui.GetContentRegionAvail();
            var railMin = new Vector2(windowPos.X, windowPos.Y + (embedded ? 0f : Chrome.TitleBarHeight));
            var railMax = new Vector2(contentTop.X + width, windowPos.Y + windowSize.Y);
            var railBg = NavRailBandColor();
            var style = ImGui.GetStyle();
            var railRadius = embedded ? 0f : MathF.Max(0f, style.WindowRounding - MathF.Max(style.WindowBorderSize, 0f));
            var drawList = ImGui.GetWindowDrawList();
            // Escape the content clip so the rail hugs the window edges like
            // the title bar does; the bottom-left corner follows the window arc.
            ImGui.PushClipRect(windowPos, windowPos + windowSize, false);
            drawList.AddRectFilled(railMin, railMax, ColorUtils.ToU32(railBg),
                railRadius, embedded ? ImDrawFlags.None : ImDrawFlags.RoundCornersBottomLeft);
            drawList.AddLine(new Vector2(railMax.X - 0.5f, railMin.Y),
                new Vector2(railMax.X - 0.5f, railMax.Y),
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.09f)));
            Fx.RailMotes(drawList, railMin, railMax);
            ImGui.PopClipRect();

            NavRailStack.Push(new NavRailState
            {
                Id = id,
                RailMin = railMin,
                RailMax = railMax,
                RailBg = railBg,
                ContentTop = contentTop,
                Available = available,
                Width = width,
                WindowMin = windowPos,
                WindowRight = windowPos.X + windowSize.X,
                WindowBottom = windowPos.Y + windowSize.Y,
                Pattern = backdropPattern,
            });
            // The nav child is inset by NavRailPadX on both sides so pills
            // keep a visible margin to the rail edges — enforced by the
            // child rect itself, not by padding semantics.
            var railPad = NavRailPadX * SpheneUI.Scale;
            ImGui.SetCursorScreenPos(new Vector2(railMin.X + railPad, contentTop.Y));
            BeginScrollArea(id + "##nav", new Vector2(
                    MathF.Max(0f, railMax.X - railMin.X - railPad * 2f),
                    MathF.Max(0f, windowPos.Y + windowSize.Y - contentTop.Y)),
                contentPadding: new Vector2(0f, 8f), topInset: topInset);
            return width;
        }

        /// <summary>
        /// Closes the rail opened by <see cref="BeginNavRail"/>: ends the nav
        /// scroll area with fades tinted to the rail color. The rail state
        /// stays on the stack so <see cref="BeginNavContent"/> can read it.
        /// <paramref name="fadeMinY"/> keeps scroll fades below a pinned strip.
        /// </summary>
        public static void EndNavRail(float? fadeMinY = null)
        {
            var state = NavRailStack.Count > 0 ? NavRailStack.Peek() : new NavRailState();
            EndScrollArea(fadeBaseColor: state.RailBg,
                fadeSpanX: new Vector2(state.RailMin.X, state.RailMax.X), fadeMinY: fadeMinY);
        }

        // Remembers per nested nav-content whether it scrolls plus the
        // backing color under it, so EndNavContent can pair
        // BeginChild/EndScrollArea correctly and tint the fades toward the
        // same surface even when an embedded page opens its own
        // rail+content inside ours.
        private static readonly Stack<(bool Scrollable, Vector4? BaseColor)> NavContentScrollable = new();

        /// <summary>
        /// Moves the cursor right of the last opened rail and begins the
        /// content scroll area (no padding, no own background so the window
        /// surface shows through). Pops the rail state — call after
        /// <see cref="EndNavRail"/> and close with <see cref="EndNavContent"/>.
        /// Pass <paramref name="scrollable"/> = false for a plain child when
        /// the page hosts content that manages its own scrolling (e.g. an
        /// embedded nav-rail layout). <paramref name="padded"/> insets the
        /// scrollable content uniformly (<see cref="Spacing.PagePadding"/> on
        /// all sides) — pages then start flush at the content origin without
        /// their own page margins. The scroll fades still render across the
        /// full child bounds, so the padding never clips them. Set
        /// <paramref name="padded"/> = false only for content that draws its
        /// own edge-to-edge chrome.
        /// </summary>
        public static bool BeginNavContent(string id, bool scrollable = true, bool padded = true)
        {
            var state = NavRailStack.Count > 0 ? NavRailStack.Pop() : new NavRailState();
            var contentMin = state.ContentTop + new Vector2(state.Width, 0f);
            ImGui.SetCursorScreenPos(contentMin);
            // The content child reaches the parent window's right and bottom
            // edges, not the content region's end (which stops windowPadding
            // short of both). Otherwise the right card margin ends up padding
            // + inset wider than the left one and embedded rails leave a bare
            // window-background strip under their band.
            var size = new Vector2(
                MathF.Max(0f, state.WindowRight - state.ContentTop.X - state.Width),
                MathF.Max(0f, state.WindowBottom - state.ContentTop.Y));
            // The page padding inside the scroll child would expose the
            // translucent window surface — and the blur behind it — along the
            // right and bottom edges. Paint a solid continuation of the
            // surface gradient under the whole content region so those margins
            // read as window chrome; the fades blend into the same tone.
            var parentMax = new Vector2(state.WindowRight, state.WindowBottom);
            var bgTop = Chrome.WindowSurfaceColorAt(state.WindowMin, parentMax, contentMin.Y);
            var bgBottom = Chrome.WindowSurfaceColorAt(state.WindowMin, parentMax,
                contentMin.Y + size.Y);
            var baseColor = Vector4.Lerp(bgTop, bgBottom, 0.5f);
            baseColor.W = 1f;
            bgTop.W = 1f;
            bgBottom.W = 1f;
            // Corners the backing shares with the window silhouette keep the
            // frame arc — a square fill would blunt the frame's rounding.
            var style = ImGui.GetStyle();
            var cornerR = MathF.Max(0f, style.WindowRounding);
            var topRightR = contentMin.Y <= state.WindowMin.Y + 0.5f ? cornerR : 0f;
            var bottomRightR = contentMin.Y + size.Y >= parentMax.Y - 0.5f ? cornerR : 0f;
            ImGui.PushClipRect(state.WindowMin, parentMax, false);
            Chrome.VerticalFade(ImGui.GetWindowDrawList(), contentMin, contentMin + size,
                bgTop, bgBottom, 0f, topRightR, 0f, bottomRightR);
            // The solid backing above hides the window-level backdrop — the
            // rail host asks for the pattern to be repeated on top of it.
            if (state.Pattern)
            {
                Fx.Constellation(ImGui.GetWindowDrawList(), contentMin, contentMin + size);
            }
            ImGui.PopClipRect();
            NavContentScrollable.Push((scrollable, baseColor));
            if (scrollable)
            {
                if (padded)
                {
                    return BeginScrollArea(id + "##content", size,
                        windowFlags: ImGuiWindowFlags.NoBackground,
                        contentPadding: new Vector2(Spacing.PagePadding.Value));
                }
                return BeginScrollArea(id + "##content", size,
                    windowFlags: ImGuiWindowFlags.NoBackground, noPadding: true);
            }
            // Non-scrollable nav content must offer the same geometry as the
            // scroll path — no child padding, so embedded layouts (their own
            // rail + scroll areas) land on the same content edges. NoScrollbar:
            // inner scroll areas push the cursor a hair past the child bottom
            // (trailing item spacing), which would surface a permanent bar that
            // eats into the embedded layout's width.
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            var contentBegun = ImGui.BeginChild(id + "##content", size, false,
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse);
            ImGui.PopStyleVar();
            return contentBegun;
        }

        /// <summary>Ends the content area opened by <see cref="BeginNavContent"/>.</summary>
        public static void EndNavContent()
        {
            var (scrollable, baseColor) = NavContentScrollable.Count > 0
                ? NavContentScrollable.Pop()
                : (true, (Vector4?)null);
            if (scrollable)
            {
                EndScrollArea(fadeBaseColor: baseColor);
            }
            else
            {
                ImGui.EndChild();
            }
        }

        /// <summary>
        /// Navigation item inside a rail: a full-width pill button. The active
        /// item fills with <paramref name="accent"/>, gains a bright left edge
        /// bar and a soft top sheen; inactive items tint on hover.
        /// <paramref name="special"/> paints the label in the accent color for
        /// highlighted entries. Returns true when clicked.
        /// </summary>
        public static bool NavRailItem(string label, bool active, Vector4 accent, bool special = false,
            FontAwesomeIcon? icon = null)
        {
            if (icon.HasValue)
            {
                return NavRailIconItem(label, icon.Value, active, accent);
            }
            var scale = SpheneUI.Scale;
            var style = ImGui.GetStyle();
            var padX = MathF.Max(NavRailItemPadX * scale, style.FramePadding.X * 0.65f);
            var buttonSize = new Vector2(-1f, NavRailItemHeight * scale);
            using var buttonPadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
                new Vector2(padX, style.FramePadding.Y));
            using var buttonStyle = Controls.PushButtonStyle();
            var pushedColors = 0;
            if (active)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, accent);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorUtils.LerpColor(accent, Vector4.One, 0.15f));
                ImGui.PushStyleColor(ImGuiCol.Text, ColorUtils.ContrastTextColor(accent));
                pushedColors += 3;
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorUtils.WithAlpha(accent, 0.22f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorUtils.WithAlpha(accent, 0.32f));
                pushedColors += 2;
                if (special)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, accent);
                    pushedColors++;
                }
            }
            var clicked = ImGui.Button(label, buttonSize);
            if (active)
            {
                // Bright left accent bar + a soft top sheen give the active
                // pill depth.
                var itemMin = ImGui.GetItemRectMin();
                var itemMax = ImGui.GetItemRectMax();
                var drawList = ImGui.GetWindowDrawList();
                var sheenTop = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.13f));
                var sheenBottom = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0f));
                drawList.AddRectFilledMultiColor(itemMin, itemMax,
                    sheenTop, sheenTop, sheenBottom, sheenBottom);
                var accentEdge = ColorUtils.LerpColor(accent, Vector4.One, 0.35f);
                var edgeCol = ImGui.ColorConvertFloat4ToU32(accentEdge);
                var fadeCol = ImGui.ColorConvertFloat4ToU32(ColorUtils.WithAlpha(accentEdge, 0f));
                var buttonRadius = Metrics.ButtonRadius;
                var fadeWidth = (itemMax.X - itemMin.X) * 0.35f;
                drawList.AddRectFilled(itemMin, new Vector2(itemMin.X + buttonRadius, itemMax.Y),
                    edgeCol, buttonRadius, ImDrawFlags.RoundCornersLeft);
                drawList.AddRectFilledMultiColor(
                    new Vector2(itemMin.X + buttonRadius, itemMin.Y),
                    new Vector2(itemMin.X + buttonRadius + fadeWidth, itemMax.Y),
                    edgeCol, fadeCol, fadeCol, edgeCol);
            }
            if (pushedColors > 0)
            {
                ImGui.PopStyleColor(pushedColors);
            }
            return clicked;
        }

        /// <summary>
        /// Square icon button for <see cref="NavRailItem"/> in icon mode —
        /// the standalone version of <see cref="NavRailSegmentItem"/>'s icon
        /// zone: a gray rounded square idle with the glyph in accent color;
        /// on hover it tints toward the accent and when active the whole
        /// square lights up solid accent with a contrast glyph. The label is
        /// shown as a tooltip so the rail can stay narrow.
        /// </summary>
        private static bool NavRailIconItem(string label, FontAwesomeIcon icon, bool active, Vector4 accent)
        {
            var availW = ImGui.GetContentRegionAvail().X;
            ImGui.PushID(label);
            var clicked = ImGui.InvisibleButton("##navrail-icon", new Vector2(availW, availW));
            var hovered = ImGui.IsItemHovered();
            var held = ImGui.IsItemActive();
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.PopID();
            Lint.CheckBounds(label);

            var drawList = ImGui.GetWindowDrawList();
            var fill = active ? ColorUtils.WithAlpha(accent, 0.92f)
                : held ? ColorUtils.WithAlpha(accent, 0.34f)
                : hovered ? ColorUtils.WithAlpha(accent, 0.24f)
                : ColorUtils.WithAlpha(Vector4.One, 0.08f);
            drawList.AddRectFilled(min, max, ColorUtils.ToU32(fill), Metrics.ButtonRadius);

            var glyphColor = active ? ColorUtils.ContrastTextColor(accent)
                : hovered ? ColorUtils.LerpColor(accent, Vector4.One, 0.3f)
                : ColorUtils.LerpColor(accent, Vector4.One, 0.1f);
            using (Icons.PushFont())
            {
                var glyph = icon.ToIconString();
                var glyphSize = ImGui.CalcTextSize(glyph);
                drawList.AddText(min + (max - min - glyphSize) * 0.5f,
                    ColorUtils.ToU32(glyphColor), glyph);
            }
            SpheneUI.AttachTooltip(label);
            return clicked;
        }

        /// <summary>
        /// Segmented nav item: a square icon zone on the left separated from
        /// the label by a thin seam. The zone carries the accent color — idle
        /// as a faint tint, fully lit when the item is active — while the
        /// label area only picks up a soft fill. Size the rail with
        /// <see cref="NavRailSegmentedWidth"/>.
        /// <paramref name="special"/> tints the idle label in the accent color
        /// for highlighted entries (e.g. Supporter).
        /// </summary>
        public static bool NavRailSegmentItem(string label, FontAwesomeIcon icon, bool active, Vector4 accent,
            bool special = false)
        {
            var scale = SpheneUI.Scale;
            var radius = Metrics.ButtonRadius;
            ImGui.PushID(label);
            var clicked = ImGui.InvisibleButton("##navrail-seg",
                new Vector2(-1f, NavRailItemHeight * scale));
            var hovered = ImGui.IsItemHovered();
            var held = ImGui.IsItemActive();
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.PopID();
            Lint.CheckBounds(label);

            var drawList = ImGui.GetWindowDrawList();
            var zoneW = NavRailItemHeight * scale;
            var zoneMax = new Vector2(min.X + zoneW, max.Y);

            var pillFill = active ? ColorUtils.WithAlpha(accent, 0.16f)
                : held ? ColorUtils.WithAlpha(accent, 0.14f)
                : hovered ? ColorUtils.WithAlpha(accent, 0.10f)
                : ColorUtils.WithAlpha(Vector4.One, 0.045f);
            drawList.AddRectFilled(min, max, ColorUtils.ToU32(pillFill), radius);

            var zoneFill = active ? ColorUtils.WithAlpha(accent, 0.92f)
                : held ? ColorUtils.WithAlpha(accent, 0.34f)
                : hovered ? ColorUtils.WithAlpha(accent, 0.24f)
                : ColorUtils.WithAlpha(Vector4.One, 0.08f);
            drawList.AddRectFilled(min, zoneMax, ColorUtils.ToU32(zoneFill),
                radius, ImDrawFlags.RoundCornersLeft);

            var seamColor = active
                ? ColorUtils.WithAlpha(ColorUtils.LerpColor(accent, Vector4.One, 0.5f), 0.55f)
                : ColorUtils.WithAlpha(Vector4.One, 0.09f);
            var seamInset = 6f * scale;
            drawList.AddLine(new Vector2(zoneMax.X - 0.5f, min.Y + seamInset),
                new Vector2(zoneMax.X - 0.5f, max.Y - seamInset), ColorUtils.ToU32(seamColor));

            var glyphColor = active ? ColorUtils.ContrastTextColor(accent)
                : hovered ? ColorUtils.LerpColor(accent, Vector4.One, 0.3f)
                : ColorUtils.LerpColor(accent, Vector4.One, 0.1f);
            using (Icons.PushFont())
            {
                var glyph = icon.ToIconString();
                var glyphSize = ImGui.CalcTextSize(glyph);
                drawList.AddText(new Vector2(min.X + (zoneW - glyphSize.X) * 0.5f,
                    min.Y + (max.Y - min.Y - glyphSize.Y) * 0.5f),
                    ColorUtils.ToU32(glyphColor), glyph);
            }

            var textPadX = NavRailItemPadX * scale;
            var textX = zoneMax.X + textPadX;
            var labelSize = ImGui.CalcTextSize(label);
            var textColor = active
                ? ColorUtils.ToU32(ColorUtils.LerpColor(accent, Vector4.One, 0.25f))
                : special ? ColorUtils.ToU32(accent) : ImGui.GetColorU32(ImGuiCol.Text);
            ImGui.PushClipRect(new Vector2(textX, min.Y),
                new Vector2(max.X - textPadX * 0.5f, max.Y), true);
            drawList.AddText(new Vector2(textX,
                    min.Y + (max.Y - min.Y - labelSize.Y) * 0.5f), textColor, label);
            ImGui.PopClipRect();
            return clicked;
        }

        /// <summary>Uppercase centered category label inside a nav rail.</summary>
        public static void NavRailCategory(string label, Vector4 accent)
        {
            ImGuiHelpers.ScaledDummy(0, 5);
            var text = label.ToUpperInvariant();
            var availWidth = ImGui.GetContentRegionAvail().X;
            var textWidth = ImGui.CalcTextSize(text).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (availWidth - textWidth) * 0.5f));
            Text.Colored(text, accent);
            ImGuiHelpers.ScaledDummy(0, 3);
        }

        /// <summary>Thin divider inside a nav rail blending two accent colors.</summary>
        public static void NavRailDivider(Vector4 from, Vector4 to)
        {
            ImGuiHelpers.ScaledDummy(0, 5);
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var lineHeight = MathF.Max(1f, 1.5f * SpheneUI.Scale);
            var span = pos.X + ImGui.GetContentRegionAvail().X;
            var fromCol = ImGui.ColorConvertFloat4ToU32(ColorUtils.WithAlpha(from, 0.4f));
            var toCol = ImGui.ColorConvertFloat4ToU32(ColorUtils.WithAlpha(to, 0.4f));
            drawList.AddRectFilledMultiColor(new Vector2(pos.X, pos.Y),
                new Vector2(span, pos.Y + lineHeight), fromCol, toCol, toCol, fromCol);
            ImGuiHelpers.ScaledDummy(0, 5);
        }

        /// <summary>
        /// Uppercase centered category label inside a nav rail that cooperates
        /// with the pinned band: when <paramref name="pinnedCategories"/> is
        /// supplied the label's center is recorded and the label either draws
        /// in-flow or reserves its slot once the pinned band covers it. Pass
        /// null to always draw in-flow.
        /// </summary>
        public static void NavRailCategoryLabel(string label, Vector4 accent,
            List<(string Label, float CenterY, Vector4 Accent)>? pinnedCategories, float pinnedCenterY)
        {
            // The banded first label carries no top spacer of its own — the
            // pinned band's padding already covers that gap. Keeping one
            // would drop the first button an extra ItemSpacing+space below
            // the drawn label compared to every later category.
            if (pinnedCategories == null || pinnedCategories.Count > 0)
            {
                VerticalSpace(Spacing.X4);
            }
            var text = label.ToUpperInvariant();
            var drawnInBand = false;
            if (pinnedCategories != null)
            {
                var labelTop = ImGui.GetCursorScreenPos().Y;
                var labelCenter = labelTop + ImGui.GetTextLineHeight() * 0.5f;
                pinnedCategories.Add((text, labelCenter, accent));
                // The band owns the first label plus every label whose
                // center has reached the pin; an approaching label is drawn
                // in-flow so the band can ride on its top edge while it
                // pushes in.
                drawnInBand = pinnedCategories.Count == 1 || labelCenter <= pinnedCenterY;
            }
            if (drawnInBand)
            {
                ImGui.Dummy(new Vector2(1f, ImGui.GetTextLineHeight()));
            }
            else
            {
                var availWidth = ImGui.GetContentRegionAvail().X;
                var textWidth = ImGui.CalcTextSize(text).X;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (availWidth - textWidth) * 0.5f));
                Text.Colored(text, accent);
            }
            VerticalSpace(Spacing.X4);
        }
    }
}
