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
                ImGui.PushTextWrapPos(width);
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

            // backdrop off = the whole card chrome goes still (no shimmer
            // band, no border pulse) — the static frame stays
            var pulse = animatedBackdrop
                ? 0.30f + 0.16f * MathF.Sin((float)ImGui.GetTime() * Chrome.CardBorderPulseSpeed)
                : 0.30f;
            var borderColor = new Vector4(accent.X, accent.Y, accent.Z, pulse);
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
        /// Calm full-width section card: a softly tinted background, a thin
        /// accent border with a gentle pulse and an optional accent title with
        /// a fading underline. The standard grouping container for settings
        /// blocks; use <see cref="AccentCard"/> for prominent cards.
        /// </summary>
        public static void SectionCard(Action content, string? title = null,
            Vector4? accentColor = null, string? description = null, bool animatedBackdrop = false,
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
                        var lineMin = ImGui.GetCursorScreenPos() + new Vector2(0, 2f * scale);
                        var lineMax = new Vector2(lineMin.X + innerWidth, lineMin.Y + 1f * scale);
                        var lineCol = ColorUtils.ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.4f));
                        drawList.AddRectFilledMultiColor(lineMin, lineMax, lineCol, 0, 0, lineCol);
                        ImGuiHelpers.ScaledDummy(0, 7);
                    }
                    content();
                });
            }

            var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padding);
            drawList.ChannelsSetCurrent(0);

            var accentTint = new Vector4(accent.X * 0.10f, accent.Y * 0.09f, accent.Z * 0.05f, 0.55f);
            var secondary = Theme.Current.SecondaryDark;
            var background = Vector4.Lerp(accentTint,
                new Vector4(secondary.X, secondary.Y, secondary.Z, 0.55f), secondary.W * 0.5f);
            drawList.AddRectFilled(cardMin, cardMax, ColorUtils.ToU32(background), rounding);

            if (animatedBackdrop)
            {
                Chrome.SectionCardBackdrop(drawList, cardMin, cardMax, rounding, accent);
            }

            var pulse = 0.22f + 0.08f * MathF.Sin((float)ImGui.GetTime() * Chrome.CardBorderPulseSpeed);
            var borderColor = new Vector4(accent.X, accent.Y, accent.Z, pulse);
            drawList.AddRect(cardMin, cardMax, ColorUtils.ToU32(borderColor),
                rounding, ImDrawFlags.RoundCornersAll, Chrome.CardBorderThickness);
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
            return MathF.Max(minHeight, ImGui.GetContentRegionAvail().Y - bottomReserve);
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
            st.ContentH = bottom - st.TopY + pad;
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
                // the delta to the parent window so scrolling continues.
                if (childHovered)
                {
                    var wheel = ImGui.GetIO().MouseWheel;
                    var atTop = scrollY <= 0.5f;
                    var atBottom = scrollY >= scrollMaxY - 0.5f;
                    if (MathF.Abs(wheel) > 0.001f && ((wheel < 0f && atBottom) || (wheel > 0f && atTop)))
                    {
                        ImGui.SetScrollY(ImGui.GetScrollY()
                            - wheel * ImGui.GetTextLineHeightWithSpacing() * 4f);
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
        /// Info callout — icon + wrapped text inside a tinted rounded box
        /// spanning the content width. Pass <paramref name="title"/> for a
        /// colored title line above neutral body text (info/warning/error
        /// boxes). The universal "hint/note" element of the design system.
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
            var wrapWidth = MathF.Max(1f, boxWidth - padX * 2f);

            var textSize = ImGui.CalcTextSize(text, wrapWidth: wrapWidth);
            var boxHeight = padY * 2f + textSize.Y
                + (hasTitle ? ImGui.GetTextLineHeightWithSpacing() : 0f);
            var boxMax = new Vector2(startPos.X + boxWidth, startPos.Y + boxHeight);

            drawList.AddRectFilled(startPos, boxMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.1f)), 8.0f * scale);
            drawList.AddRect(startPos, boxMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.4f)), 8.0f * scale,
                ImDrawFlags.RoundCornersAll, 1.0f);

            var innerMin = startPos + new Vector2(padX, padY);
            ImGui.SetCursorScreenPos(innerMin);
            using (ImRaii.PushColor(ImGuiCol.Text, accent))
            {
                ImGui.TextUnformatted(icon.ToIconString());
                ImGui.SameLine();
                if (hasTitle)
                {
                    ImGui.TextUnformatted(title);
                    ImGui.SetCursorScreenPos(new Vector2(innerMin.X, ImGui.GetCursorScreenPos().Y));
                }
            }

            using (ImRaii.PushColor(ImGuiCol.Text, hasTitle ? Colors.TextPrimary : accent))
            {
                ImGui.PushTextWrapPos(startPos.X + boxWidth - padX);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
            }

            ImGui.SetCursorScreenPos(new Vector2(startPos.X, boxMax.Y + Spacing.X4.Scaled));
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
            public float WindowRight;
        }

        private static readonly Stack<NavRailState> NavRailStack = new();

        /// <summary>
        /// Rail width that fits every <paramref name="labels"/> entry at the
        /// current font: widest label plus item padding, the child's window
        /// padding and a small breathing margin. Pass all item labels so the
        /// rail never clips one.
        /// </summary>
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
            var padX = MathF.Max(4f * SpheneUI.Scale, style.FramePadding.X * 0.65f);
            return maxText + padX * 2f + style.WindowPadding.X * 2f + 2f * SpheneUI.Scale;
        }

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
        /// </summary>
        public static float BeginNavRail(string id, float width, bool embedded = false)
        {
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
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
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.06f)));
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
                WindowRight = windowPos.X + windowSize.X,
            });
            // Center the nav child inside the rail: the rail reaches the
            // window's left edge while the child keeps a symmetric inset.
            var childMin = new Vector2(
                railMin.X + MathF.Max(0f, railMax.X - railMin.X - width) * 0.5f,
                contentTop.Y);
            ImGui.SetCursorScreenPos(childMin);
            BeginScrollArea(id + "##nav", new Vector2(width, MathF.Max(0f, available.Y)));
            return width;
        }

        /// <summary>
        /// Closes the rail opened by <see cref="BeginNavRail"/>: ends the nav
        /// scroll area with fades tinted to the rail color. The rail state
        /// stays on the stack so <see cref="BeginNavContent"/> can read it.
        /// </summary>
        public static void EndNavRail()
        {
            var state = NavRailStack.Count > 0 ? NavRailStack.Peek() : new NavRailState();
            EndScrollArea(fadeBaseColor: state.RailBg,
                fadeSpanX: new Vector2(state.RailMin.X, state.RailMax.X));
        }

        // Remembers per nested nav-content whether it scrolls, so
        /// EndNavContent can pair BeginChild/EndScrollArea correctly even
        /// when an embedded page opens its own rail+content inside ours.
        private static readonly Stack<bool> NavContentScrollable = new();

        /// <summary>
        /// Moves the cursor right of the last opened rail and begins the
        /// content scroll area (no padding, no own background so the window
        /// surface shows through). Pops the rail state — call after
        /// <see cref="EndNavRail"/> and close with <see cref="EndNavContent"/>.
        /// Pass <paramref name="scrollable"/> = false for a plain child when
        /// the page hosts content that manages its own scrolling (e.g. an
        /// embedded nav-rail layout).
        /// </summary>
        public static bool BeginNavContent(string id, bool scrollable = true)
        {
            var state = NavRailStack.Count > 0 ? NavRailStack.Pop() : new NavRailState();
            ImGui.SetCursorScreenPos(state.ContentTop + new Vector2(state.Width, 0f));
            // The content child reaches the parent window's right edge, not
            // the content region's end (which stops windowPadding short of
            // it). Otherwise the right card margin ends up padding + inset
            // wider than the left one.
            var size = new Vector2(
                MathF.Max(0f, state.WindowRight - state.ContentTop.X - state.Width),
                state.Available.Y);
            NavContentScrollable.Push(scrollable);
            if (scrollable)
            {
                return BeginScrollArea(id + "##content", size,
                    windowFlags: ImGuiWindowFlags.NoBackground, noPadding: true);
            }
            // Non-scrollable nav content must offer the same geometry as the
            // scroll path — no child padding, so embedded layouts (their own
            // rail + scroll areas) land on the same content edges.
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            var contentBegun = ImGui.BeginChild(id + "##content", size, false,
                ImGuiWindowFlags.NoBackground);
            ImGui.PopStyleVar();
            return contentBegun;
        }

        /// <summary>Ends the content area opened by <see cref="BeginNavContent"/>.</summary>
        public static void EndNavContent()
        {
            var scrollable = NavContentScrollable.Count > 0 && NavContentScrollable.Pop();
            if (scrollable)
            {
                EndScrollArea();
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
        public static bool NavRailItem(string label, bool active, Vector4 accent, bool special = false)
        {
            var scale = SpheneUI.Scale;
            var style = ImGui.GetStyle();
            var padX = MathF.Max(4f * scale, style.FramePadding.X * 0.65f);
            var buttonSize = new Vector2(-1f, 24f * scale);
            using var buttonPadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
                new Vector2(padX, style.FramePadding.Y));
            using var buttonRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f * scale);
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
            var clicked = Controls.Button(label, buttonSize);
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
                var buttonRadius = 5f * scale;
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
    }
}
