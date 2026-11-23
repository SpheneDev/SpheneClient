using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Ambient state of the innermost <see cref="Layout.RowGroup"/>: where
    /// separators span, the active row's frame and how many rows were drawn.
    /// <see cref="Rows"/> helpers and group-aware controls consult it so the
    /// same call renders standalone or grouped without call-site changes.
    /// Single UI thread · a plain static is sufficient.
    /// </summary>
    internal static class GroupState
    {
        private sealed class Group
        {
            public int RowsDrawn;
            public float SeparatorMinX;
            public float SeparatorMaxX;
            public float RowTop;
            public float RowMinX;
            public bool Pending;
            public bool SkipNextSeparator;
            public bool SuppressNextRowPad;
        }

        private static readonly Stack<Group> Groups = new();

        /// <summary>True while a <see cref="Layout.RowGroup"/> is being drawn.</summary>
        internal static bool InGroup => Groups.Count > 0;

        /// <summary>Right edge of the active group, or -1 outside one.</summary>
        internal static float CurrentRightBound
            => Groups.Count > 0 ? Groups.Peek().SeparatorMaxX : -1f;

        internal static void Push(float separatorMinX, float separatorMaxX)
            => Groups.Push(new Group { SeparatorMinX = separatorMinX, SeparatorMaxX = separatorMaxX });

        /// <summary>
        /// Headed containers (<see cref="Layout.HeadedRowGroup"/>) already add
        /// the standard header-to-body gap - which includes the visual row
        /// pad - before the first row. Marking the group lets the next
        /// <see cref="RowStart"/> skip its own centering pad so the offset is
        /// not applied twice.
        /// </summary>
        internal static void SuppressNextRowPadding()
        {
            if (Groups.Count > 0)
            {
                Groups.Peek().SuppressNextRowPad = true;
            }
        }

        internal static void Pop()
        {
            if (Groups.Count == 0)
            {
                return;
            }
            CompletePendingRow(Groups.Peek());
            Groups.Pop();
        }

        /// <summary>
        /// Begins the next row inside the active group: auto-completes the
        /// pending row, draws the inset hairline between rows (skipped for
        /// the first) and positions the cursor at the row's content band
        /// (vertically centered against <see cref="Metrics.RowHeight"/>).
        /// Returns false outside groups · callers then draw standalone at
        /// natural height.
        /// </summary>
        internal static bool RowStart()
        {
            if (Groups.Count == 0)
            {
                return false;
            }
            var group = Groups.Peek();
            CompletePendingRow(group);
            var drawList = ImGui.GetWindowDrawList();
            var cursor = ImGui.GetCursorScreenPos();
            if (group.RowsDrawn > 0 && !group.SkipNextSeparator)
            {
                var y = MathF.Round(cursor.Y) + 0.5f;
                drawList.AddLine(new Vector2(group.SeparatorMinX, y),
                    new Vector2(group.SeparatorMaxX, y),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.08f)));
                cursor.Y += 1f;
            }
            group.RowTop = cursor.Y;
            group.RowMinX = cursor.X;
            group.Pending = true;
            group.SkipNextSeparator = false;
            var pad = group.SuppressNextRowPad
                ? 0f
                : MathF.Max(0f, (Metrics.RowHeight - ImGui.GetFrameHeight()) * 0.5f);
            group.SuppressNextRowPad = false;
            ImGui.SetCursorScreenPos(cursor + new Vector2(0f, pad));
            return true;
        }

        /// <summary>
        /// Closes the row opened by <see cref="RowStart"/>. Rows that never
        /// get an explicit RowEnd auto-complete on the next RowStart/Pop  · 
        /// that is how group-aware controls (combos, sliders, inputs)
        /// participate without needing a tail hook.
        /// </summary>
        internal static void RowEnd()
        {
            if (Groups.Count == 0)
            {
                return;
            }
            CompletePendingRow(Groups.Peek());
        }

        /// <summary>
        /// Marks a deliberate divider (e.g. <c>DistanceSeparator</c>) drawn by
        /// the caller: completes the pending row and suppresses the hairline
        /// the next row would draw, so the two never stack as a double line.
        /// </summary>
        internal static void BreakRow()
        {
            if (Groups.Count == 0)
            {
                return;
            }
            var group = Groups.Peek();
            CompletePendingRow(group);
            group.SkipNextSeparator = true;
        }

        /// <summary>
        /// Lands the cursor on the next row's top edge. Content that already
        /// flowed below the row band (a help line under its control) keeps
        /// the lower position so the separator hugs it instead of
        /// overlapping it.
        /// </summary>
        private static void CompletePendingRow(Group group)
        {
            if (!group.Pending)
            {
                return;
            }
            group.Pending = false;
            group.RowsDrawn++;
            var bottom = group.RowTop + Metrics.RowHeight;
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(new Vector2(group.RowMinX,
                MathF.Max(cursor.Y, bottom)));
        }
    }

    public static partial class Layout
    {
        /// <summary>
        /// Depth of open draw-list channel splits (Surface / SurfaceFlat /
        /// RowGroup). ImGui channels cannot nest - an inner split collapses
        /// the outer merge and the card's buffered content vanishes, so a
        /// nested call degrades to <see cref="NestedSurfaceFallback"/> and
        /// flags a Lint violation instead of corrupting the draw list.
        /// Reset by <see cref="Lint.BeginFrame"/> so a throw mid-draw can
        /// never strand it.
        /// </summary>
        private static int _channelSplitDepth;

        /// <summary>True while inside a channel-splitting surface.</summary>
        internal static bool InChannelSplit => _channelSplitDepth > 0;

        internal static void ChannelSplitEnter() => _channelSplitDepth++;
        internal static void ChannelSplitExit() => _channelSplitDepth--;
        internal static void ChannelSplitReset() => _channelSplitDepth = 0;

        /// <summary>RAII channel split · the ctor splits + enters, Dispose
        /// merges + exits, so a throw inside the split region can never
        /// strand the depth counter or leave the draw list unmerged.</summary>
        internal readonly struct ChannelSplitGuard : IDisposable
        {
            private readonly ImDrawListPtr _drawList;

            public ChannelSplitGuard(ImDrawListPtr drawList)
            {
                _drawList = drawList;
                _drawList.ChannelsSplit(2);
                ChannelSplitEnter();
            }

            public void Dispose()
            {
                _drawList.ChannelsMerge();
                ChannelSplitExit();
            }
        }

        /// <summary>
        /// Channel-free degrade for a nested surface call: same padding,
        /// bounded content region and card bounds, but only a hollow outline
        /// (a fill needs the behind-content channel ordering). Content stays
        /// visible - the Lint overlay marks the offending card.
        /// </summary>
        private static void NestedSurfaceFallback(Action content, string? id,
            bool border, Vector4? accentColor, float? rounding, string caller)
        {
            var scale = SpheneUI.Scale;
            var padX = Spacing.CardPaddingX.Scaled;
            var padY = Spacing.CardPadding.Scaled;
            var radius = (rounding ?? AccentCardRounding) * scale;
            var cardMin = ImGui.GetCursorScreenPos();
            var cardRightX = SurfaceRightBound(cardMin, scale);
            var innerWidth = MathF.Max(60f * scale, cardRightX - cardMin.X - padX * 2f);
            ImGui.SetCursorScreenPos(cardMin + new Vector2(padX, padY));
            ContentRegion(innerWidth, id ?? "surface", content);
            var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padY);
            var drawList = ImGui.GetWindowDrawList();
            if (accentColor.HasValue)
            {
                drawList.AddRect(cardMin, cardMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accentColor.Value, 0.35f)),
                    radius, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
            }
            else if (border)
            {
                drawList.AddRect(cardMin, cardMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Border, 0.45f)),
                    radius, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
            }
            Lint.RecordNestedSurface($"{caller}:{id ?? "surface"}", cardMin, cardMax);
            ImGui.SetCursorScreenPos(cardMin + new Vector2(0, cardMax.Y - cardMin.Y));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>
        /// Minimal degrade for channel-splitting helpers without card chrome
        /// (AccentCard/SectionCard/Headed*/ImageBackground): renders the
        /// content plainly and flags the violation - never loses the draw.
        /// </summary>
        internal static void ChannelSplitFallback(string caller, string? id, Action content)
        {
            var cardMin = ImGui.GetCursorScreenPos();
            using (ImRaii.Group())
            {
                content();
            }
            Lint.RecordNestedSurface($"{caller}:{id ?? "region"}",
                cardMin, ImGui.GetItemRectMax());
        }

        /// <summary>Last frame's measured height per inner-card id - the
        /// fill is painted before content using this rect, so it needs no
        /// draw-list channel and stays legal inside surfaces.</summary>
        private static readonly Dictionary<string, float> _innerCardHeights =
            new(StringComparer.Ordinal);

        /// <summary>
        /// The visible card that is legal INSIDE channel-splitting surfaces
        /// (Surface/SectionCard/AccentCard - e.g. comments inside a widget).
        /// A fill must precede its content, but the card's height is only
        /// known after it - so the fill is painted at the live cursor using
        /// last frame's measured height: position tracks scrolling exactly,
        /// a changed height settles within one frame, and no draw-list
        /// channel split is needed. The fill lifts a step over the rail-band
        /// tone so the card reads as a raised panel; <paramref name="accentColor"/>
        /// adds the accent top seam and an accent-tinted edge.
        /// <paramref name="padding"/> overrides <see cref="Spacing.CardPadding"/>/
        /// <see cref="Spacing.CardPaddingX"/> (both axes) for tighter layouts.
        /// <paramref name="fill"/>/<paramref name="borderAlpha"/>
        /// restyle the panel (e.g. a darker inset for the comment body) -
        /// fill defaults to the lifted rail-band tone, border alpha to 0.45.
        /// </summary>
        public static void InnerCard(Action content, string id,
            Vector4? accentColor = null, Space? padding = null,
            Vector4? fill = null, float? borderAlpha = null)
        {
            var scale = SpheneUI.Scale;
            var padX = (padding ?? Spacing.CardPaddingX).Scaled;
            var padY = (padding ?? Spacing.CardPadding).Scaled;
            var radius = AccentCardRounding * scale;
            var w = MathF.Max(40f * scale, Controls.BoundedAvailX());
            var cardMin = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            var fillCol = ColorUtils.WithAlpha(fill
                ?? ColorUtils.LerpColor(NavRailBandColor(), Vector4.One, 0.05f), 1f);

            if (_innerCardHeights.TryGetValue(id, out var lastH) && lastH > 1f)
            {
                var lastMax = new Vector2(cardMin.X + w, cardMin.Y + lastH);
                var borderCol = accentColor.HasValue
                    ? ColorUtils.WithAlpha(accentColor.Value, 0.30f)
                    : ColorUtils.WithAlpha(Colors.Border, borderAlpha ?? 0.45f);
                dl.AddRectFilled(cardMin, lastMax, ColorUtils.ToU32(fillCol), radius);
                dl.AddRect(cardMin, lastMax, ColorUtils.ToU32(borderCol), radius,
                    ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
            }
            if (accentColor.HasValue)
            {
                // the accent seam rides the card's top edge - same identity
                // language as SectionCard's seam
                var seamColor = ColorUtils.WithAlpha(accentColor.Value, 0.35f);
                var seamCol = ColorUtils.ToU32(seamColor);
                var seamFade = ColorUtils.ToU32(ColorUtils.WithAlpha(seamColor, 0f));
                var seamH = MathF.Max(1f, 1.5f * scale);
                var seamInset = radius * 0.9f;
                var seamFadeW = 24f * scale;
                dl.AddRectFilledMultiColor(
                    new Vector2(cardMin.X + seamInset, cardMin.Y + 0.5f),
                    new Vector2(cardMin.X + seamInset + seamFadeW,
                        cardMin.Y + 0.5f + seamH),
                    seamFade, seamCol, seamCol, seamFade);
                dl.AddRectFilled(
                    new Vector2(cardMin.X + seamInset + seamFadeW, cardMin.Y + 0.5f),
                    new Vector2(cardMin.X + w - seamInset - seamFadeW,
                        cardMin.Y + 0.5f + seamH), seamCol);
                dl.AddRectFilledMultiColor(
                    new Vector2(cardMin.X + w - seamInset - seamFadeW,
                        cardMin.Y + 0.5f),
                    new Vector2(cardMin.X + w - seamInset, cardMin.Y + 0.5f + seamH),
                    seamCol, seamFade, seamFade, seamCol);
            }

            // a bounded ContentRegion (BeginTable) must NOT run here - a
            // nested table inside a host cell's open channel split kills
            // the draw-list splitter (TableBeginCell → SetCurrentChannel).
            // Bounds + wrap like SurfaceFlat: no table, no channels.
            ImGui.SetCursorScreenPos(cardMin + new Vector2(padX, padY));
            var innerRight = cardMin.X + w - padX;
            ContentBounds.Push(new Vector2(cardMin.X + padX, innerRight));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + w - padX * 2f);
            try
            {
                using (ImRaii.Group())
                {
                    content();
                }
            }
            finally
            {
                ImGui.PopTextWrapPos();
                ContentBounds.Pop();
            }
            var measured = ImGui.GetItemRectMax().Y - cardMin.Y + padY;
            _innerCardHeights[id] = measured;
            if (_innerCardHeights.Count > 1024)
            {
                _innerCardHeights.Clear();
            }
            ImGui.SetCursorScreenPos(new Vector2(cardMin.X, cardMin.Y + measured));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>
        /// The card that does not look like a card: a borderless tonal
        /// surface in the rail-band color family with generous padding and
        /// rounding. Identity comes from grouping, not outlines · headers
        /// stay on <c>RailHeader</c>/<c>Text.SectionHeader</c>.
        /// <paramref name="interactive"/> lifts the fill slightly while the
        /// pointer rests on the surface (for clickable cards; the caller owns
        /// the actual click handling).
        /// </summary>
        public static void Surface(Action content, string? id = null,
            bool interactive = false, Vector4? accentColor = null, float? rounding = null,
            bool border = false, bool sheen = false)
        {
            if (InChannelSplit)
            {
                NestedSurfaceFallback(content, id, border, accentColor, rounding, nameof(Surface));
                return;
            }
            var scale = SpheneUI.Scale;
            var padX = Spacing.CardPaddingX.Scaled;
            var padY = Spacing.CardPadding.Scaled;
            var radius = (rounding ?? AccentCardRounding) * scale;

            var drawList = ImGui.GetWindowDrawList();
            var cardMin = ImGui.GetCursorScreenPos();
            var cardRightX = SurfaceRightBound(cardMin, scale);
            var innerWidth = MathF.Max(60f * scale, cardRightX - cardMin.X - padX * 2f);

            using var splitScope = new ChannelSplitGuard(drawList);
            drawList.ChannelsSetCurrent(1);

            ImGui.SetCursorScreenPos(cardMin + new Vector2(padX, padY));
            using (ImRaii.Group())
            {
                ContentRegion(innerWidth, id ?? "surface", content);
            }

            var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padY);
            drawList.ChannelsSetCurrent(0);

            // An accent tint turns the quiet surface into the page's
            // spotlight block · same shape, a soft color wash + hairline
            // instead of the neutral rail fill.
            var fill = accentColor.HasValue
                ? ColorUtils.WithAlpha(accentColor.Value, 0.08f)
                : NavRailBandColor();
            drawList.AddRectFilled(cardMin, cardMax, ColorUtils.ToU32(fill), radius);
            if (sheen)
            {
                var sheenAccent = accentColor ?? Colors.Accent;
                var sheenH = MathF.Min(14f * scale, (cardMax.Y - cardMin.Y) * 0.3f);
                var sheenU32 = ColorUtils.ToU32(ColorUtils.WithAlpha(sheenAccent, 0.09f));
                // Inset past the corner arcs so the fade only covers the
                // straight top edge and never bleeds outside the rounding -
                // and eased in from the tangent so it can't cut hard
                // against the corner arc.
                var sheenIn = MathF.Min(12f * scale,
                    (cardMax.X - cardMin.X - radius * 2f) * 0.4f);
                drawList.AddRectFilledMultiColor(
                    new Vector2(cardMin.X + radius, cardMin.Y),
                    new Vector2(cardMin.X + radius + sheenIn, cardMin.Y + sheenH),
                    0u, sheenU32, 0u, 0u);
                drawList.AddRectFilledMultiColor(
                    new Vector2(cardMin.X + radius + sheenIn, cardMin.Y),
                    new Vector2(cardMax.X - radius, cardMin.Y + sheenH),
                    sheenU32, sheenU32, 0u, 0u);
            }
            if (accentColor.HasValue)
            {
                drawList.AddRect(cardMin, cardMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accentColor.Value, 0.18f)),
                    radius, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
            }
            else if (border)
            {
                drawList.AddRect(cardMin, cardMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Border, 0.30f)),
                    radius, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
            }
            if (interactive)
            {
                // eased lift instead of a fill snap · id derives from the
                // surface's screen position (stable per card while it sits
                // still; scroll-shifts just restart the ease)
                var hoverId = id != null
                    ? ImGui.GetID($"{id}##lift")
                    : unchecked((uint)cardMin.GetHashCode());
                Fx.HoverLift(drawList, cardMin, cardMax,
                    Fx.HoverLiftT(hoverId, cardMin, cardMax), radius, accentColor);
            }

            ImGui.SetCursorScreenPos(cardMin + new Vector2(0, cardMax.Y - cardMin.Y));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>
        /// Same tonal block as <see cref="Surface"/> but without the bounded
        /// table · for callers that place their own content (drawlist teasers,
        /// image rails) whose absolute-positioned text the table cell would
        /// clip. The content bounds are still pushed, so width-aware helpers
        /// dock against the card's inner edge.
        /// </summary>
        public static void SurfaceFlat(Action content, string? id = null,
            Vector4? accentColor = null, float? rounding = null)
        {
            if (InChannelSplit)
            {
                NestedSurfaceFallback(content, id, border: true, accentColor, rounding, nameof(SurfaceFlat));
                return;
            }
            var scale = SpheneUI.Scale;
            var padX = Spacing.CardPaddingX.Scaled;
            var padY = Spacing.CardPadding.Scaled;
            var radius = (rounding ?? AccentCardRounding) * scale;

            var drawList = ImGui.GetWindowDrawList();
            var cardMin = ImGui.GetCursorScreenPos();
            var cardRightX = SurfaceRightBound(cardMin, scale);
            var innerWidth = MathF.Max(60f * scale, cardRightX - cardMin.X - padX * 2f);

            using var splitScope = new ChannelSplitGuard(drawList);
            drawList.ChannelsSetCurrent(1);

            ImGui.SetCursorScreenPos(cardMin + new Vector2(padX, padY));
            ContentBounds.Push(new Vector2(cardMin.X + padX,
                cardMin.X + padX + innerWidth));
            try
            {
                using (ImRaii.Group())
                {
                    content();
                }
            }
            finally
            {
                ContentBounds.Pop();
            }

            var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padY);
            drawList.ChannelsSetCurrent(0);

            var fill = accentColor.HasValue
                ? ColorUtils.WithAlpha(accentColor.Value, 0.08f)
                : NavRailBandColor();
            drawList.AddRectFilled(cardMin, cardMax, ColorUtils.ToU32(fill), radius);
            if (accentColor.HasValue)
            {
                drawList.AddRect(cardMin, cardMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accentColor.Value, 0.18f)),
                    radius, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
            }

            ImGui.SetCursorScreenPos(cardMin + new Vector2(0, cardMax.Y - cardMin.Y));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>
        /// An iOS-style row group: a <see cref="Surface"/> whose rows draw
        /// hairline separators between themselves via <see cref="GroupState"/>.
        /// Rows come from <see cref="Rows"/> or group-aware controls like
        /// <see cref="Controls.Toggle"/>; mixed content renders unseparated.
        /// </summary>
        public static void RowGroup(Action content, string? id = null,
            float? rounding = null)
        {
            if (InChannelSplit)
            {
                NestedSurfaceFallback(content, id, border: true, accentColor: null, rounding, nameof(RowGroup));
                return;
            }
            var scale = SpheneUI.Scale;
            var padX = Spacing.CardPaddingX.Scaled;
            var padY = Spacing.X8.Scaled;
            var radius = (rounding ?? AccentCardRounding) * scale;

            var drawList = ImGui.GetWindowDrawList();
            var cardMin = ImGui.GetCursorScreenPos();
            var cardRightX = SurfaceRightBound(cardMin, scale);
            var innerWidth = MathF.Max(60f * scale, cardRightX - cardMin.X - padX * 2f);

            using var splitScope = new ChannelSplitGuard(drawList);
            drawList.ChannelsSetCurrent(1);

            ImGui.SetCursorScreenPos(cardMin + new Vector2(padX, padY));
            using (ImRaii.Group())
            {
                GroupState.Push(cardMin.X + padX, cardRightX - padX);
                try
                {
                    ContentRegion(innerWidth, id ?? "rowgroup", content);
                }
                finally
                {
                    GroupState.Pop();
                }
            }

            var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padY);
            drawList.ChannelsSetCurrent(0);
            Fx.SoftShadow(drawList, cardMin, cardMax, radius);
            drawList.AddRectFilled(cardMin, cardMax,
                ColorUtils.ToU32(NavRailBandColor()), radius);

            ImGui.SetCursorScreenPos(cardMin + new Vector2(0, cardMax.Y - cardMin.Y));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>
        /// Adaptive card grid: lays <paramref name="items"/> out in as many
        /// equal-width columns as fit at <paramref name="minCellWidth"/>,
        /// clamped to 1..<paramref name="maxCols"/>. Each cell is a bounded
        /// content region · cells draw their own <see cref="Surface"/>.
        /// </summary>
        public static void CardGrid<T>(IReadOnlyList<T> items, float minCellWidth,
            Action<T, int> drawCell, float? gap = null, int maxCols = 3)
        {
            if (items.Count == 0)
            {
                return;
            }
            var cellGap = gap ?? Spacing.X8.Scaled;
            var avail = MathF.Max(minCellWidth, Controls.BoundedAvailX());
            var cols = Math.Clamp((int)((avail + cellGap) / (minCellWidth + cellGap)), 1, maxCols);
            var cellW = (avail - (cols - 1) * cellGap) / cols;
            var startX = ImGui.GetCursorScreenPos().X;
            var startY = ImGui.GetCursorScreenPos().Y;
            var rowMaxY = startY;
            var riseId = ImGui.GetID("##cardgrid-rise");
            var riseLift = 6f * SpheneUI.Scale;

            for (var i = 0; i < items.Count; i++)
            {
                var col = i % cols;
                var row = i / cols;
                if (col == 0 && row > 0)
                {
                    startY = rowMaxY + cellGap;
                }
                // Staggered rise · the cell's slot stays put while its drawn
                // content settles upward; row tracking keeps the unshifted
                // edge so the layout never pops when the ease completes.
                var rise = Fx.RiseT(riseId, i);
                var cellDrop = (1f - rise) * riseLift;
                ImGui.SetCursorScreenPos(new Vector2(startX + col * (cellW + cellGap),
                    startY + cellDrop));
                using (ImRaii.Group())
                {
                    ContentRegion(cellW, $"grid-{i}", () => drawCell(items[i], i));
                }
                rowMaxY = MathF.Max(rowMaxY, ImGui.GetItemRectMax().Y - cellDrop);
            }
            ImGui.SetCursorScreenPos(new Vector2(startX, rowMaxY));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>Right edge shared by Surface/RowGroup: bounded, window- and scroll-aware.</summary>
        private static float SurfaceRightBound(Vector2 cardMin, float scale)
        {
            var right = MathF.Min(
                MathF.Min(cardMin.X + ImGui.GetContentRegionAvail().X,
                    ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - 1f * scale),
                ScrollAreaRightBound);
            var bound = ContentRightBound;
            if (bound > 0f)
            {
                right = MathF.Min(right, bound);
            }
            return right;
        }
    }
}
