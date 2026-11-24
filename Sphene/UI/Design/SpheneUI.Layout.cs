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
    public static partial class Layout
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
        /// color · shared so banners can fade into the exact same tone.</summary>
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
        // inset). Nested child windows · expanders, embedded panels · have a
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
        /// Suppresses the active content bound until disposed · use while
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
            // Window-space Y stays constant while scrolling · screen Y would
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
                // Wrap at the cell's right edge · PushTextWrapPos takes a
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
        /// animated shimmer and a gently pulsing accent border · the prominent
        /// promo/status card of the design system. Optional
        /// <paramref name="fxHover"/>/<paramref name="fxIdle"/> stack the
        /// owner's combinable <see cref="ProfileRowFx"/> layers on the card
        /// chrome (under the content, above the card background).
        /// </summary>
        /// <param name="topBleed">Optional chrome-layer draw callback invoked
        /// with the card's exact outer rect (drawList, cardMin, cardMax,
        /// rounding). It renders above the card background and below the
        /// border/effect layers · full-bleed media like hero banners belong
        /// here since content coordinates are not reliable for edge-aligned
        /// drawing.</param>
        public static void AccentCard(Action content, Vector4? accentColor = null,
            bool animatedBackdrop = true, string? id = null,
            ProfileRowFx fxHover = ProfileRowFx.None, ProfileRowFx fxIdle = ProfileRowFx.None,
            Action<ImDrawListPtr, Vector2, Vector2, float>? topBleed = null)
        {
            if (InChannelSplit)
            {
                ChannelSplitFallback(nameof(AccentCard), id, content);
                return;
            }

            var scale = SpheneUI.Scale;
            var padX = Spacing.CardPaddingX.Scaled;
            var padY = Spacing.CardPadding.Scaled;
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
            // the card to it · narrower than the plain window edge
            var bound = ContentRightBound;
            if (bound > 0f)
            {
                cardRightX = MathF.Min(cardRightX, bound);
            }
            var innerWidth = MathF.Max(60f * scale, cardRightX - cardMin.X - padX * 2f);
            using var splitScope = new ChannelSplitGuard(drawList);
            drawList.ChannelsSetCurrent(1);

            ImGui.SetCursorScreenPos(cardMin + new Vector2(padX, padY));
            using (ImRaii.Group())
            {
                ContentRegion(innerWidth, id, content);
            }

            var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padY);
            drawList.ChannelsSetCurrent(0);

            var background = AccentCardTint(accent);
            drawList.AddRectFilled(cardMin, cardMax, ColorUtils.ToU32(background), rounding);
            topBleed?.Invoke(drawList, cardMin, cardMax, rounding);

            if (animatedBackdrop)
            {
                Fx.SoftShadow(drawList, cardMin, cardMax, rounding);
                Chrome.AccentCardBackdrop(drawList, cardMin, cardMax, rounding, accent);
            }

            // Gradient-ramp edge: the deep stop rings the card, the bright
            // stop kisses the top edge - the mockup's lit promo rim without
            // a pulsing border.
            var (eDeep, eHi) = Colors.AccentRamp(accent);
            drawList.AddRect(cardMin, cardMax, ColorUtils.ToU32(
                ColorUtils.WithAlpha(eDeep, 0.55f)),
                rounding, ImDrawFlags.RoundCornersAll, Chrome.CardBorderThickness);
            var kissW = (cardMax.X - cardMin.X) * 0.55f;
            var kissHi = ColorUtils.ToU32(ColorUtils.WithAlpha(eHi, 0.55f));
            // Fade in from the corner tangent so the lit edge never cuts
            // hard against the dimmer ring, then fade out toward the middle.
            var kissIn = MathF.Min(16f * scale, kissW * 0.5f);
            drawList.AddRectFilledMultiColor(
                new Vector2(cardMin.X + rounding, cardMin.Y + 0.5f),
                new Vector2(cardMin.X + rounding + kissIn, cardMin.Y + 0.5f + 1.5f * scale),
                0u, kissHi, kissHi, 0u);
            drawList.AddRectFilledMultiColor(
                new Vector2(cardMin.X + rounding + kissIn, cardMin.Y + 0.5f),
                new Vector2(cardMin.X + rounding + kissW, cardMin.Y + 0.5f + 1.5f * scale),
                kissHi, 0u, 0u, kissHi);
            if (fxHover != ProfileRowFx.None || fxIdle != ProfileRowFx.None)
            {
                var fxHovered = ImGui.IsMouseHoveringRect(cardMin, cardMax);
                var fxT = Fx.HoverT(ImGui.GetID(id ?? "##accentcardfx"), fxHovered);
                Profile.RowFx(drawList, cardMin, cardMax, accent,
                    fxHovered, fxT, fxHover, fxIdle, rounding);
            }

            ImGui.SetCursorScreenPos(cardMin + new Vector2(0, cardMax.Y - cardMin.Y));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>
        /// Calm full-width section panel: a borderless tonal surface in the
        /// rail-band color family. With a <paramref name="title"/> it renders
        /// the integrated header band shared with <see cref="HeadedSurface"/> -
        /// icon slot + heading + optional count/description + right-docked
        /// actions on a lifted tonal strip closed by a divider. Untitled
        /// cards stay plain surfaces; an explicit accent still draws the
        /// hairline top seam. Use <see cref="AccentCard"/> for prominent cards.
        /// </summary>
        public static void SectionCard(Action content, string? title = null,
            Vector4? accentColor = null, string? description = null, bool animatedBackdrop = true,
            string? id = null, bool border = false, FontAwesomeIcon? icon = null,
            int? count = null,
            params (FontAwesomeIcon Icon, string Tooltip, Action OnClick)?[] actions)
        {
            if (InChannelSplit)
            {
                ChannelSplitFallback(nameof(SectionCard), id ?? title, content);
                return;
            }

            var scale = SpheneUI.Scale;
            var padX = Spacing.CardPaddingX.Scaled;
            var padY = Spacing.CardPadding.Scaled;
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
            var innerWidth = MathF.Max(60f * scale, cardRightX - cardMin.X - padX * 2f);
            var innerRight = cardRightX - padX;
            using var splitScope = new ChannelSplitGuard(drawList);
            drawList.ChannelsSetCurrent(1);

            var bandTopPad = Spacing.X8.Scaled;
            ImGui.SetCursorScreenPos(cardMin + new Vector2(padX,
                string.IsNullOrEmpty(title) ? padY : bandTopPad));
            var bandBottom = -1f;
            if (!string.IsNullOrEmpty(title))
            {
                var headerBottom = CardHeader(title, icon, description, accent,
                    count, innerRight, actions);
                bandBottom = headerBottom + bandTopPad;
                ImGui.SetCursorScreenPos(new Vector2(cardMin.X + padX,
                    bandBottom + HeaderBodyGap));
            }
            using (ImRaii.Group())
            {
                ContentRegion(innerWidth, id ?? title, content);
            }

            var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padY);
            drawList.ChannelsSetCurrent(0);

            // Borderless tonal panel in the rail-band color family; the group
            // identity lives on a hairline accent seam along the top edge.
            Fx.SoftShadow(drawList, cardMin, cardMax, rounding);
            var background = NavRailBandColor();
            drawList.AddRectFilled(cardMin, cardMax, ColorUtils.ToU32(background), rounding);
            if (border)
            {
                drawList.AddRect(cardMin, cardMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Border, 0.30f)),
                    rounding, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
            }

            if (animatedBackdrop)
            {
                Chrome.SectionCardBackdrop(drawList, cardMin, cardMax, rounding, accent);
            }

            // Titled cards carry the same header band as HeadedSurface: a
            // lifted tonal strip spanning the card edge-to-edge, top-rounded
            // and closed by a divider. The band is the card identity, so the
            // accent seam is only drawn for untitled explicitly-accented cards.
            if (bandBottom > 0f)
            {
                drawList.AddRectFilled(cardMin, new Vector2(cardRightX, bandBottom),
                    ColorUtils.ToU32(ColorUtils.LerpColor(background, accent, 0.07f)),
                    rounding, ImDrawFlags.RoundCornersTop);
                drawList.AddLine(new Vector2(cardMin.X, bandBottom),
                    new Vector2(cardRightX, bandBottom),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Border, 0.5f)));
            }
            else if (accentColor.HasValue)
            {
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
            }

            ImGui.SetCursorScreenPos(cardMin + new Vector2(0, cardMax.Y - cardMin.Y));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>
        /// Integrated card header row · optional semantic accent icon in a
        /// uniform invisible slot (fixed width - <see cref="CardHeaderIconSlot"/>,
        /// no plate), heading title, optional count, optional muted
        /// description under the title and right-aligned icon actions -
        /// icon, title block and actions all vertically centered against
        /// each other from real font metrics. The title always begins at
        /// left + slot + gap regardless of the glyph's rendered width, so
        /// stacked card headers share one title column. A null icon leaves
        /// the slot empty so the title column still aligns.
        /// Returns the screen-space Y at the bottom of the header block and
        /// leaves the flow cursor there; callers add their own spacing so
        /// chrome variants can size the header band exactly. Inside a
        /// <see cref="RowGroup"/> the header never calls
        /// <see cref="GroupState.RowStart"/>, so it draws no row hairline and
        /// the first real row still skips its separator.
        /// </summary>
        public static float CardHeader(string title, FontAwesomeIcon? icon = null,
            string? description = null, Vector4? accent = null, int? count = null,
            float rightEdge = -1f,
            params (FontAwesomeIcon Icon, string Tooltip, Action OnClick)?[] actions)
        {
            var scale = SpheneUI.Scale;
            var drawList = ImGui.GetWindowDrawList();
            var tone = accent ?? Colors.Accent;
            var headerMin = ImGui.GetCursorScreenPos();

            var lineH = ImGui.GetTextLineHeight();
            var iconSize = icon.HasValue ? Icons.Size(icon.Value) : Vector2.Zero;
            // Heading metrics · measured inside the pushed scope, used to
            // center the title/desc block against the leading icon glyph.
            var titleH = lineH;
            var titleW = ImGui.CalcTextSize(title).X;
            if (HeadingFont is { } hf)
            {
                using (hf.Push())
                {
                    titleH = ImGui.GetFontSize();
                    titleW = ImGui.CalcTextSize(title).X;
                }
            }
            var hasDesc = !string.IsNullOrWhiteSpace(description);
            var blockH = titleH + (hasDesc ? 2f * scale + lineH : 0f);
            var rowH = MathF.Max(iconSize.Y, blockH);
            // Uniform invisible icon slot · every integrated card header
            // reserves the same horizontal area, so the title's X offset
            // never depends on the glyph's rendered width:
            // TitleX = left + IconSlot + gap. The slot itself is plain
            // space - no plate, background or border; only the glyph shows.
            var iconSlotW = CardHeaderIconSlot * scale;
            var textX = headerMin.X + iconSlotW + Spacing.X8.Scaled;
            var boundRight = rightEdge > 0f ? rightEdge
                : headerMin.X + Controls.BoundedAvailX();

            // Right-edge icon actions · measured first, docked at the bound
            // and vertically centered on the header row.
            var actionCount = 0;
            foreach (var a in actions)
            {
                if (a.HasValue)
                {
                    actionCount++;
                }
            }
            var actionsLeft = boundRight;
            if (actionCount > 0)
            {
                var gap = Spacing.X4.Scaled;
                var total = -gap;
                var btnH = 0f;
                foreach (var a in actions)
                {
                    if (a.HasValue)
                    {
                        var sz = Icons.ButtonSize(a.Value.Icon);
                        total += sz.X + gap;
                        btnH = MathF.Max(btnH, sz.Y);
                    }
                }
                actionsLeft = boundRight - total;
                var btnY = headerMin.Y + (rowH - btnH) * 0.5f;
                var x = actionsLeft;
                foreach (var a in actions)
                {
                    if (!a.HasValue)
                    {
                        continue;
                    }
                    ImGui.SetCursorScreenPos(new Vector2(x, btnY));
                    if (Icons.Button(a.Value.Icon))
                    {
                        a.Value.OnClick();
                    }
                    AttachTooltip(a.Value.Tooltip);
                    x += Icons.ButtonSize(a.Value.Icon).X + gap;
                }
            }
            var textRight = actionsLeft - Spacing.X8.Scaled;

            // Leading glyph · bare accent icon centered inside the fixed
            // slot both axes (horizontally against the slot, vertically on
            // the title line) - no plate, no tile, it reads as part of the
            // heading itself.
            var glyphX = headerMin.X + MathF.Max(0f, (iconSlotW - iconSize.X) * 0.5f);
                var glyphY = headerMin.Y + (rowH - blockH) * 0.5f
                    + (titleH - iconSize.Y) * 0.5f;
                if (icon.HasValue)
                {
                    using (Icons.PushFont())
                    {
                        drawList.AddText(new Vector2(glyphX, glyphY),
                            ColorUtils.ToU32(tone), icon.Value.ToIconString());
                    }
                }

            // Title/description block · clipped left of the action dock so a
            // long title never runs under the buttons.
            var titleY = headerMin.Y + (rowH - blockH) * 0.5f;
            drawList.PushClipRect(new Vector2(textX, headerMin.Y - 1f),
                new Vector2(MathF.Max(textX + 4f, textRight),
                    headerMin.Y + rowH + 1f), true);
            var titleU32 = ColorUtils.ToU32(Colors.TextPrimary);
            if (HeadingFont is { } hf2)
            {
                using (hf2.Push())
                {
                    drawList.AddText(new Vector2(textX, titleY), titleU32, title);
                }
            }
            else
            {
                drawList.AddText(new Vector2(textX, titleY), titleU32, title);
            }
            if (count.HasValue)
            {
                drawList.AddText(new Vector2(textX + titleW + 6f * scale,
                        titleY + (titleH - lineH) * 0.5f),
                    ColorUtils.ToU32(Colors.TextMuted), $"· {count.Value}");
            }
            if (hasDesc)
            {
                drawList.AddText(new Vector2(textX,
                        titleY + titleH + 2f * scale),
                    ColorUtils.ToU32(Colors.TextMuted),
                    Text.Ellipsize(description!,
                        MathF.Max(20f, textRight - textX)));
            }
            drawList.PopClipRect();

            ImGui.SetCursorScreenPos(new Vector2(headerMin.X,
                headerMin.Y + rowH));
            return headerMin.Y + rowH;
        }

        /// <summary>Unscaled width of the invisible icon slot every
        /// integrated card header reserves (≈ glyph + breathing room);
        /// titles always begin at left + slot + <see cref="Spacing.X8"/>.</summary>
        private const float CardHeaderIconSlot = 20f;

        /// <summary>
        /// The authoritative header-to-body gap for integrated cards: the
        /// small breathing gap plus the row-band pad a grouped control would
        /// add itself - so free-flow content and row controls start at the
        /// same visual offset below the header divider. Headed containers
        /// own this spacing contract: card helpers draw header, divider and
        /// this gap, while callers only own spacing BETWEEN body elements
        /// and must not add leading Space/Dummy/cursor offsets before their
        /// first body element.
        /// </summary>
        internal static float HeaderBodyGap
            => Spacing.X4.Scaled + MathF.Max(0f,
                (Metrics.RowHeight - ImGui.GetFrameHeight()) * 0.5f);

        /// <summary>
        /// A <see cref="RowGroup"/> whose first row is an integrated
        /// <see cref="CardHeader"/>: the title row sits on a subtly lifted
        /// tonal band that spans the card edge-to-edge, top-rounded with the
        /// card and closed by a full-width divider - then the regular
        /// hairline-separated rows. The body starts <see cref="HeaderBodyGap"/>
        /// below the divider and the first row's own centering pad is
        /// suppressed, so every card's first element lands at the same offset.
        /// </summary>
        public static void HeadedRowGroup(string title, Action content,
            FontAwesomeIcon icon, string? description = null,
            Vector4? accent = null, int? count = null, string? id = null,
            params (FontAwesomeIcon Icon, string Tooltip, Action OnClick)?[] actions)
        {
            if (InChannelSplit)
            {
                ChannelSplitFallback(nameof(HeadedRowGroup), id ?? title, content);
                return;
            }

            var scale = SpheneUI.Scale;
            var padX = Spacing.CardPaddingX.Scaled;
            var padY = Spacing.X8.Scaled;
            var radius = AccentCardRounding * scale;

            var drawList = ImGui.GetWindowDrawList();
            var cardMin = ImGui.GetCursorScreenPos();
            var cardRightX = SurfaceRightBound(cardMin, scale);
            var innerWidth = MathF.Max(60f * scale,
                cardRightX - cardMin.X - padX * 2f);
            var innerRight = cardRightX - padX;

            using var splitScope = new ChannelSplitGuard(drawList);
            drawList.ChannelsSetCurrent(1);

            ImGui.SetCursorScreenPos(cardMin + new Vector2(padX, padY));
            var headerBottom = CardHeader(title, icon, description, accent,
                count, innerRight, actions);
            var bandBottom = headerBottom + padY;
            // Divider sits at the band edge · the shared gap below it is
            // owned by this helper (see HeaderBodyGap).
            ImGui.SetCursorScreenPos(new Vector2(cardMin.X + padX,
                bandBottom + HeaderBodyGap));

            using (ImRaii.Group())
            {
                GroupState.Push(cardMin.X + padX, innerRight);
                GroupState.SuppressNextRowPadding();
                try
                {
                    ContentRegion(innerWidth, id ?? $"headed-{title}", content);
                }
                finally
                {
                    GroupState.Pop();
                }
            }

            var cardMax = new Vector2(cardRightX,
                ImGui.GetItemRectMax().Y + padY);
            drawList.ChannelsSetCurrent(0);
            Fx.SoftShadow(drawList, cardMin, cardMax, radius);
            var fill = NavRailBandColor();
            drawList.AddRectFilled(cardMin, cardMax, ColorUtils.ToU32(fill), radius);
            // Header band · a touch of accent over the card fill so the
            // header reads as its own zone, top-rounded with the card and
            // closed by a full-width divider line.
            var tone = accent ?? Colors.Accent;
            drawList.AddRectFilled(cardMin, new Vector2(cardRightX, bandBottom),
                ColorUtils.ToU32(ColorUtils.LerpColor(fill, tone, 0.07f)),
                radius, ImDrawFlags.RoundCornersTop);
            drawList.AddLine(new Vector2(cardMin.X, bandBottom),
                new Vector2(cardRightX, bandBottom),
                ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Border, 0.5f)));

            ImGui.SetCursorScreenPos(cardMin + new Vector2(0,
                cardMax.Y - cardMin.Y));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>
        /// A <see cref="Surface"/> with the same integrated header band as
        /// <see cref="HeadedRowGroup"/> - the free-content counterpart for
        /// cards that are not row groups.
        /// </summary>
        public static void HeadedSurface(string title, Action content,
            FontAwesomeIcon icon, string? description = null,
            Vector4? accent = null, int? count = null, string? id = null,
            params (FontAwesomeIcon Icon, string Tooltip, Action OnClick)?[] actions)
        {
            if (InChannelSplit)
            {
                ChannelSplitFallback(nameof(HeadedSurface), id ?? title, content);
                return;
            }

            var scale = SpheneUI.Scale;
            var padX = Spacing.CardPaddingX.Scaled;
            var padY = Spacing.CardPadding.Scaled;
            var radius = AccentCardRounding * scale;

            var drawList = ImGui.GetWindowDrawList();
            var cardMin = ImGui.GetCursorScreenPos();
            var cardRightX = SurfaceRightBound(cardMin, scale);
            var innerWidth = MathF.Max(60f * scale,
                cardRightX - cardMin.X - padX * 2f);
            var innerRight = cardRightX - padX;

            using var splitScope = new ChannelSplitGuard(drawList);
            drawList.ChannelsSetCurrent(1);

            ImGui.SetCursorScreenPos(cardMin + new Vector2(padX,
                Spacing.X8.Scaled));
            var headerBottom = CardHeader(title, icon, description, accent,
                count, innerRight, actions);
            var bandBottom = headerBottom + Spacing.X8.Scaled;
            ImGui.SetCursorScreenPos(new Vector2(cardMin.X + padX,
                bandBottom + HeaderBodyGap));

            using (ImRaii.Group())
            {
                ContentBounds.Push(new Vector2(cardMin.X + padX, innerRight));
                try
                {
                    ContentRegion(innerWidth, id ?? $"headed-{title}", content);
                }
                finally
                {
                    ContentBounds.Pop();
                }
            }

            var cardMax = new Vector2(cardRightX,
                ImGui.GetItemRectMax().Y + padY);
            drawList.ChannelsSetCurrent(0);
            var fill = NavRailBandColor();
            drawList.AddRectFilled(cardMin, cardMax, ColorUtils.ToU32(fill), radius);
            var tone = accent ?? Colors.Accent;
            drawList.AddRectFilled(cardMin, new Vector2(cardRightX, bandBottom),
                ColorUtils.ToU32(ColorUtils.LerpColor(fill, tone, 0.07f)),
                radius, ImDrawFlags.RoundCornersTop);
            drawList.AddLine(new Vector2(cardMin.X, bandBottom),
                new Vector2(cardRightX, bandBottom),
                ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Border, 0.5f)));

            ImGui.SetCursorScreenPos(cardMin + new Vector2(0,
                cardMax.Y - cardMin.Y));
            ImGui.Dummy(Vector2.Zero);
        }

        private sealed class ScrollAreaState
        {
            public string Id = string.Empty;
            public bool Begun;
            public bool ClipPushed;
            public bool Border;
            public bool ChainWheel = true;
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

        // Last measured scrollMax per scroll-area id · the bottom spacer is
        // gated on it so near-fit pages never gain a phantom scroll range.
        private static readonly Dictionary<string, float> ScrollAreaLastMax = new(StringComparer.Ordinal);

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
        /// noPadding behavior. <paramref name="edgeToEdge"/> widens the
        /// horizontal clip to the full parent window rect · for surfaces that
        /// must cover the window's side borders like the title bar does.
        /// <paramref name="chainWheel"/> controls whether wheel input past
        /// the child's scroll edges is forwarded to the parent window -
        /// disable it for self-managed scroll views (like the chat history)
        /// whose own snap logic would fight the resulting page slide.
        /// </summary>
        public static bool BeginScrollArea(string id, Vector2 size, bool border = false,
            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None, bool noPadding = false,
            bool hideTopBorder = false, float topInset = 0f, float contentInset = -1f,
            Vector2? contentPadding = null, bool edgeToEdge = false, bool chainWheel = true)
        {
            var state = new ScrollAreaState
            {
                Id = id,
                Border = border,
                ChainWheel = chainWheel,
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
            // in · without it the padded variant would sit flush at the child
            // edge despite the pushed style var.
            if (contentPadding.HasValue)
            {
                windowFlags |= ImGuiWindowFlags.AlwaysUseWindowPadding;
            }
            if (edgeToEdge && size.X > 0f && size.Y > 0f)
            {
                // A child's own background fill is clipped to the parent's
                // inner clip rect · the border ring would keep showing the
                // translucent window surface (and the blur behind it) even
                // though the child spans the full width. Paint the surface
                // color opaque over the whole window on the parent draw list  · 
                // the same backing BeginNavContent gives its content region.
                // RedrawWindowBorder re-strokes the frame on top later.
                var bg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
                ImGui.PushClipRect(state.ParentMin, state.ParentMax, false);
                ImGui.GetWindowDrawList().AddRectFilled(state.ParentMin, state.ParentMax,
                    ColorUtils.ToU32(new Vector4(bg.X, bg.Y, bg.Z, 1f)),
                    ImGui.GetStyle().WindowRounding);
                ImGui.PopClipRect();
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
                    // edgeToEdge surfaces paint over the border ring · the same
                    // full-bleed treatment the chrome title bar gets.
                    // Vertically the theoretical child bounds are NOT safe: a
                    // child extending past the parent viewport (short host
                    // window, area placed low) would let rows paint below the
                    // window entirely. Clamp the pushed rect to the clip that
                    // is active now - ImGui already intersects every child
                    // with its parent chain, so this keeps the wider-than-child
                    // horizontal reach while restoring the viewport's bound.
                    var borderSize = MathF.Max(ImGui.GetStyle().WindowBorderSize, 0f);
                    var clipInsetX = edgeToEdge ? 0f : borderSize;
                    var clipMin = ImGui.GetWindowDrawList().GetClipRectMin();
                    var clipMax = ImGui.GetWindowDrawList().GetClipRectMax();
                    var lo = new Vector2(state.ParentMin.X + clipInsetX,
                        MathF.Max(min.Y + state.TopInset, clipMin.Y));
                    var hi = new Vector2(state.ParentMax.X - clipInsetX,
                        MathF.Min(max.Y, clipMax.Y));
                    if (hi.X > lo.X && hi.Y > lo.Y)
                    {
                        ImGui.PushClipRect(lo, hi, false);
                        state.ClipPushed = true;
                    }
                }
                // The child's own content edge is a bound too: labeled controls
                // and toggle rows inside a scroll area dock to its padded inner
                // edge, not to a wider enclosing card/region bound. noPadding
                // children have no right padding of their own · there the
                // content inset must close the right edge just like Indent
                // does on the left, or full-width rows glue to the window edge.
                var contentX = ImGui.GetCursorScreenPos().X;
                var rightEdge = MathF.Min(
                    contentX + ImGui.GetContentRegionAvail().X - state.ContentInset,
                    state.ChildRight - 1f * SpheneUI.Scale - state.ContentInset);
                ContentBounds.Push(new Vector2(contentX, rightEdge));
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
        /// bottom padding, a margin for the bottom inset EndScrollArea may
        /// append (only emitted once the child actually scrolls) and the
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
            // GetContentRegionAvail is inflated by the window's scroll offset  · 
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

        /// <summary>
        /// Invisible multi-column layout for side-by-side card surfaces: the
        /// table's <see cref="ImGuiStyleVar.CellPadding"/> is halved so
        /// neighbouring cards sit exactly <see cref="Spacing.CardGap"/> apart
        /// while <see cref="ImGuiTableFlags.NoPadOuterX"/> keeps the outer
        /// column edges flush with the page. Columns size by
        /// <see cref="ImGuiTableFlags.SizingStretchProp"/> unless
        /// <paramref name="flags"/> carries another sizing policy. Set columns
        /// up with <see cref="ImGui.TableSetupColumn"/> / weight, then emit one
        /// row and draw a card per <see cref="ImGui.TableSetColumnIndex"/>.
        /// </summary>
        public static CardColumnsScope BeginCardColumns(string id, int columns,
            ImGuiTableFlags flags = ImGuiTableFlags.SizingStretchProp)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding,
                new Vector2(Spacing.CardGap.Scaled * 0.5f, 0f));
            return new CardColumnsScope(ImGui.BeginTable(id, columns,
                flags | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.NoPadOuterX));
        }

        /// <summary>
        /// Titled bordered pane: muted header label above a bordered box in the
        /// pick-list language. <paramref name="size"/> of (0,0) fills the
        /// cell/region; <paramref name="scrollable"/> keeps the scroll-fade
        /// treatment for panes whose content can overflow (default: the pane
        /// hosts a fixed layout and does not scroll). <paramref name="padding"/>
        /// is the inner inset around the content · the pane never draws flush.
        /// <paramref name="border"/> toggles the framed box look off for panes
        /// that sit next to their own separator or chrome.
        /// </summary>
        public static PaneScope BeginPane(string id, string? title = null,
            Vector2 size = default, bool scrollable = false, Vector2? padding = null,
            bool border = true)
        {
            if (!string.IsNullOrEmpty(title))
            {
                Text.Muted(title);
                VerticalSpace(Spacing.X4);
            }
            var flags = scrollable ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar;
            var begun = BeginScrollArea(id, size, border: border, windowFlags: flags,
                contentPadding: (padding ?? new Vector2(5f, 4f) * Scale));
            return new PaneScope(begun);
        }

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

        /// <summary>Sets an expander's open state explicitly · use for external close paths.</summary>
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

        /// <summary>Drops all expander state · call on window/page resets like <see cref="Fx.Reset"/>.</summary>
        public static void ResetExpanders() => _expanders.Clear();

        /// <summary>
        /// In-flow expanding panel: renders its content inside the current
        /// window (not an overlay), grows/collapses with a height + alpha
        /// animation and clips content while animating. Once fully open the
        /// region is exactly content-sized · no inner scrolling, the host
        /// page scrolls instead. Being part of the host window it also
        /// disappears with it · no focus or z-order handling needed at all.
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
            // child exists · ChildBg stays transparent because child windows
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
            // NoScrollWithMouse · the child is exactly content-sized at rest,
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
            // feed a jittering content height back into BeginExpander's size  · 
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
        /// <paramref name="fadeStrength"/> scales the edge-fade visibility ·
        /// 0 hides the hints entirely while still-unrevealed content is
        /// loading, 1 is the default behavior.
        /// </summary>
        public static void EndScrollArea(Vector4? fadeBaseColor = null, float edgePaddingX = -1f,
            Vector2? fadeSpanX = null, float? fadeMinY = null, float bottomInset = 0f,
            float fadeStrength = 1f, bool scrollHints = true)
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
                // The bottom spacer only exists so genuinely scrolling
                // content clears the bottom fade. Inside a child whose
                // content (nearly) fits it IS the overflow · a ~inset-sized
                // phantom scroll range users can wheel into. Emit it only
                // when the child already scrolled past the spacer last
                // frame; content heights are stable frame-to-frame, so the
                // gate converges in one pass and never oscillates.
                var spacerThreshold = ScrollAreaContentInset * SpheneUI.Scale;
                if (ScrollAreaLastMax.TryGetValue(state.Id, out var prevScrollMax)
                    && prevScrollMax > spacerThreshold)
                {
                    ImGuiHelpers.ScaledDummy(0, ScrollAreaContentInset);
                }
                if (bottomInset > 0f)
                {
                    ImGui.Dummy(new Vector2(0f, bottomInset));
                }
                var scrollY = ImGui.GetScrollY();
                var scrollMaxY = ImGui.GetScrollMaxY();
                ScrollAreaLastMax[state.Id] = scrollMaxY;
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
                // forward must clamp to the parent's own range · an uncapped
                // SetScrollY overscrolls non-scrollable hosts and slides their
                // nested children past the window edges. It must also never
                // reach a nav-rail host: its rail+content children are
                // edge-to-edge by design, so any scroll range it reports is
                // incidental overflow, and programmatic SetScrollY would slide
                // the whole embedded layout out of the window even though the
                // host is NoScrollWithMouse. chainWheel=false opts the child
                // out entirely (self-managed scroll like the chat history).
                if (childHovered && state.ChainWheel)
                {
                    var wheel = ImGui.GetIO().MouseWheel;
                    var atTop = scrollY <= 0.5f;
                    var atBottom = scrollY >= scrollMaxY - 0.5f;
                    if (MathF.Abs(wheel) > 0.001f && ((wheel < 0f && atBottom) || (wheel > 0f && atTop))
                        && !IsNavRailHost(ImGui.GetWindowPos(), ImGui.GetWindowSize()))
                    {
                        // Only chain into a parent with a meaningful scroll
                        // range · a fit or near-fit host (fixed clip regions,
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
                    state.ParentMin, state.ParentMax, fadeBaseColor, edgePaddingX, fadeSpanX, fadeMinY,
                    fadeStrength, scrollHints);
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
        /// storage keyed by <paramref name="id"/> · pass
        /// <paramref name="preferredIndex"/> ≥ 0 to force-select a tab once.
        /// Unlike a real ImGui tab bar the strip never reacts to the mouse
        /// wheel and never emits overflow scroll buttons; buttons that do not
        /// fit wrap to the next line. Returns the active tab index · the
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
        /// Nothing is persisted · the caller owns the active state and applies
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
        /// in the design border color · replaces raw ImGui.Separator() inside
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

        /// <summary>
        /// Hairline between stacked free-form entries (feed rows, list items
        /// living outside a <see cref="RowGroup"/>) - the same white-α0.08
        /// line <see cref="GroupState.RowStart"/> draws between settings rows,
        /// spanning the full bounded width so the siblings read as one list.
        /// Replaces a bare <see cref="VerticalSpace(Space)"/> between entries;
        /// emits <see cref="Spacing.X2"/> of air around the line, about the
        /// total height <see cref="Spacing.X4"/> reserved before.
        /// </summary>
        public static void ItemSeparator()
        {
            var scale = SpheneUI.Scale;
            var thickness = MathF.Max(1f, scale);
            var pad = Spacing.X2.Scaled;
            var cursor = ImGui.GetCursorScreenPos();
            var width = Controls.BoundedAvailX();
            var y = MathF.Round(cursor.Y + pad) + thickness * 0.5f;
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(cursor.X, y), new Vector2(cursor.X + width, y),
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.08f)),
                thickness);
            ImGui.Dummy(new Vector2(0f, pad * 2f + thickness));
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

        /// <summary>
        /// Secondary group label inside a card body · a medium-weight label
        /// with an optional bare accent icon, separated from the previous
        /// group by plain vertical whitespace (<paramref name="firstGroup"/>:
        /// true for the first group - the card divider already separates it,
        /// so no pre-heading gap is emitted). No horizontal line is drawn
        /// above a heading: inside a <see cref="RowGroup"/> the helper
        /// completes the pending row and suppresses the next row hairline,
        /// while row separators between individual settings stay intact.
        /// The helper owns heading typography and all group-transition
        /// spacing; the card helper still owns <see cref="HeaderBodyGap"/>
        /// and body padding - do not add leading space before the first
        /// group.
        /// </summary>
        public static void SettingsGroup(string label,
            FontAwesomeIcon? icon = null, bool firstGroup = false)
        {
            var scale = SpheneUI.Scale;
            var drawList = ImGui.GetWindowDrawList();
            GroupState.BreakRow();
            if (!firstGroup)
            {
                VerticalSpace(Spacing.Section);
            }
            var labelPos = ImGui.GetCursorScreenPos();
            var lineH = ImGui.GetTextLineHeight();
            var textX = labelPos.X;
            if (icon.HasValue)
            {
                var iconSize = Icons.Size(icon.Value);
                using (Icons.PushFont())
                {
                    drawList.AddText(new Vector2(textX,
                            labelPos.Y + (lineH - iconSize.Y) * 0.5f),
                        ColorUtils.ToU32(Colors.Accent),
                        icon.Value.ToIconString());
                }
                textX += iconSize.X + Spacing.X4.Scaled;
            }
            // Medium-weight emphasis at body size: double-drawn with a 1px
            // offset (the same faux-bold the markdown engine uses - no
            // semibold face is loaded). Secondary emphasis that stays
            // clearly subordinate to the card header's HeadingFont title.
            var labelU32 = ColorUtils.ToU32(Colors.TextSecondary);
            drawList.AddText(new Vector2(textX, labelPos.Y), labelU32, label);
            drawList.AddText(
                new Vector2(textX + MathF.Max(1f, scale), labelPos.Y),
                labelU32, label);
            ImGui.SetCursorScreenPos(new Vector2(labelPos.X,
                labelPos.Y + lineH + Spacing.X4.Scaled));
        }

        /// <summary>Separator padded with <see cref="Spacing.X8"/>-level breathing room on both sides.</summary>
        public static void DistanceSeparator()
        {
            // Inside a row group this is a deliberate sub-section break  · 
            // complete the pending row and suppress the next hairline so
            // the divider never stacks on top of it.
            GroupState.BreakRow();
            ImGuiHelpers.ScaledDummy(0, 5);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(0, 5);
        }

        /// <summary>
        /// Section header (crystal accent underline) wrapping indented content
        /// · the general-purpose "subsection with title" primitive.
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
        /// Info callout · accent icon + wrapped text inside a tonal rounded
        /// box, spanning the content width. Pass <paramref name="title"/> for
        /// a colored title line above neutral body text
        /// (info/warning/error boxes). The universal "hint/note" element of
        /// the design system · borderless like the cards.
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
            // content region · raw avail reaches further right than cards do
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
        public static float BadgeHeight => ImGui.GetTextLineHeight() + Density.Current switch
        {
            DensityProfile.Compact => 2f,
            DensityProfile.Comfortable => 6f,
            _ => 4f,
        } * SpheneUI.Scale;

        /// <summary>
        /// Inline tag pill: tinted rounded chip drawn at the cursor (soft
        /// accent underlay + hairline border) · e.g. "NEW"/"BETA" markers on
        /// list rows. <paramref name="solid"/> renders the loud variant - an
        /// accent-gradient fill with contrast text - for state markers that
        /// should read at a glance ("Latest", "SUPPORTER", live kind chips).
        /// Advances the cursor by the chip width and returns it.
        /// </summary>
        public static float Badge(string text, Vector4 color, bool solid = false)
        {
            var scale = SpheneUI.Scale;
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var chipW = BadgeWidth(text);
            var chipH = BadgeHeight;
            var padX = 7f * scale;
            var padY = 2f * scale;
            var chipMax = pos + new Vector2(chipW, chipH);
            if (solid)
            {
                var (gDeep, gHi) = Colors.AccentRamp(color);
                Chrome.RoundedHorizontalGradient(drawList, pos, chipMax,
                    chipH * 0.5f, gDeep, gHi);
                drawList.AddRect(pos, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(
                        ColorUtils.LerpColor(color, Vector4.One, 0.4f), 0.55f)),
                    chipH * 0.5f);
                drawList.AddText(pos + new Vector2(padX, padY),
                    ColorUtils.ToU32(ColorUtils.ContrastTextColor(color)), text);
            }
            else
            {
                drawList.AddRectFilled(pos, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(color, 0.14f)), chipH * 0.5f);
                drawList.AddRect(pos, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(color, 0.35f)), chipH * 0.5f);
                drawList.AddText(pos + new Vector2(padX, padY),
                    ColorUtils.ToU32(Colors.TextPrimary), text);
            }
            ImGui.SetCursorScreenPos(pos + new Vector2(chipW, 0f));
            return chipW;
        }

        /// <summary>Draws <paramref name="imguiDrawAction"/> inside a group with a rounded frame.</summary>
        /// <summary>
        /// Channel-free tonal item container · the same quiet surface treatment
        /// as <see cref="SurfaceFlat"/> but without a channel split, so it stays
        /// legal inside any surface or card. Use for item rows and inline bands
        /// that need their own edge inside a larger block.
        /// </summary>
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

            var scale = SpheneUI.Scale;
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetItemRectMin() - ImGui.GetStyle().ItemInnerSpacing;
            var max = ImGui.GetItemRectMax() + ImGui.GetStyle().ItemInnerSpacing;
            dl.AddRectFilled(min, max, ColorUtils.ToU32(NavRailBandColor()), rounding * scale);
            dl.AddRect(min, max,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Border, 0.6f)),
                rounding * scale, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
        }

        /// <summary>
        /// Re-anchors the layout's line origin - the x every line break snaps
        /// back to (<c>Pos.x + DC.Indent.x</c>) - to <paramref name="x"/> in
        /// screen space. <c>SetCursorScreenPos</c> alone moves only the
        /// cursor: the first item lands right, then every wrapped line snaps
        /// back to the window's original origin - inside the hub's fixed
        /// chrome zone that origin sits under the nav rail. Returns the
        /// applied delta; pass it to <see cref="FlowUnindent"/> to restore.
        /// The cursor position is preserved.
        /// </summary>
        public static float FlowIndentTo(float x)
        {
            var resume = ImGui.GetCursorScreenPos();
            // Indent() snaps CursorPos.x onto the line origin - an epsilon
            // probe reads the current origin without disturbing the layout.
            ImGui.Indent(0.0001f);
            var lineOriginX = ImGui.GetCursorScreenPos().X;
            ImGui.Unindent(0.0001f);
            var delta = x - lineOriginX;
            if (MathF.Abs(delta) > 0.01f)
            {
                ImGui.Indent(delta);
            }
            ImGui.SetCursorScreenPos(resume);
            return delta;
        }

        /// <summary>Reverses <see cref="FlowIndentTo"/>, preserving the cursor.</summary>
        public static void FlowUnindent(float delta)
        {
            if (MathF.Abs(delta) <= 0.01f)
            {
                return;
            }
            var resume = ImGui.GetCursorScreenPos();
            ImGui.Unindent(delta);
            ImGui.SetCursorScreenPos(resume);
        }

        /// <summary>Vertical gap on the spacing grid.</summary>
        public static void VerticalSpace(Space space) => ImGui.Dummy(new Vector2(0f, space.Scaled));

        /// <summary>One themed vertical gap · the standard row break.</summary>
        public static void VerticalSpace() => ImGui.Spacing();

        /// <summary>Square gap reserving X and Y on the spacing grid.</summary>
        public static void Space(Space size) => ImGui.Dummy(new Vector2(size.Scaled));

        /// <summary>Gap reserving X and Y on the spacing grid independently.</summary>
        public static void Space(Space x, Space y) => ImGui.Dummy(new Vector2(x.Scaled, y.Scaled));

        /// <summary>Horizontal gap on the spacing grid.</summary>
        public static void HorizontalSpace(Space space) => ImGui.Dummy(new Vector2(space.Scaled, 0f));

        /// <summary>Indented content scope with a grid spacing width.</summary>
        public static IDisposable IndentScope(Space space) => ImRaii.PushIndent(space.Scaled);

        /// <summary>
        /// Page-level help affordance · a muted "?" button that opens an
        /// anchored flyout with the page's explanation. This replaces
        /// per-page prose: pages should self-explain through their layout,
        /// and the longer detail lives behind this icon.
        /// </summary>
        public static void PageHelp(string id, Action drawHelp)
        {
            var flyoutId = "pagehelp:" + id;
            if (Controls.IconButton("##" + flyoutId, FontAwesomeIcon.Question))
            {
                Popups.ToggleFlyout(flyoutId);
            }
            AttachTooltip("About this page");
            if (Popups.BeginFlyout(flyoutId))
            {
                drawHelp();
                Popups.EndFlyout();
            }
        }

        /// <summary>
        /// Page header row: the section title on the left and · when
        /// <paramref name="helpId"/> is set · the page's "?" help button
        /// docked at the far right edge. This is THE consistent spot for
        /// page help across the app; every page that has help content
        /// starts with this row.
        /// </summary>
        public static void PageHeader(string title, string helpId, Action drawHelp)
        {
            var rowH = ImGui.GetFrameHeight();
            var lineMin = ImGui.GetCursorScreenPos();
            Controls.DockToCardEdge(Metrics.ControlHeight);
            var dockMin = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(lineMin);
            Text.SectionHeader(title);
            ImGui.SetCursorScreenPos(dockMin);
            RegisterPageHelp(helpId, drawHelp);
            PageHelp(helpId, _helpDrawers[helpId]);
            ImGui.SetCursorScreenPos(new Vector2(lineMin.X,
                MathF.Max(ImGui.GetCursorScreenPos().Y, lineMin.Y + rowH)));
        }

        /// <summary>
        /// Registers the active page's "?" flyout content without drawing
        /// anything · the page-level sibling of <see cref="PageHeader"/>
        /// when the help affordance lives in the nav rail's "?" dock. Call
        /// once per frame while the page draws so every nav entry that
        /// points at this id can resolve its flyout.
        /// </summary>
        public static void RegisterPageHelp(string helpId, Action drawHelp)
        {
            _currentHelpId = helpId;
            if (!string.Equals(_lastHelpId, helpId, StringComparison.Ordinal))
            {
                _helpExtras.Remove(_lastHelpId ?? "");
                _lastHelpId = helpId;
            }
            _helpDrawers[helpId] = ComposeHelp(helpId, drawHelp);
        }

        /// <summary>
        /// Registers a "?" flyout's content without claiming the current
        /// page context - nav rail items call it so their entry's help
        /// exists before the page itself is ever opened. A null drawer only
        /// leaves whatever the active page registered on its own.
        /// </summary>
        public static void EnsurePageHelp(string helpId, Action? drawer)
        {
            if (drawer != null)
            {
                _helpDrawers[helpId] = ComposeHelp(helpId, drawer);
            }
        }

        /// <summary>The composed flyout body · the page's own help text plus
        /// any <see cref="HelpExtra"/> blocks the active page merged in.</summary>
        private static Action ComposeHelp(string helpId, Action drawHelp) => () =>
        {
            drawHelp();
            if (_helpExtras.TryGetValue(helpId, out var extra))
            {
                VerticalSpace(Spacing.Row);
                extra();
            }
        };

        /// <summary>
        /// The subtle "?" affordance docked at a nav row's right edge ·
        /// returns true while the corner click is being consumed so the
        /// caller can suppress the row's own navigation result. Hit-testing
        /// is manual: the row's invisible button already claimed
        /// <c>HoveredId</c> for the whole pill this frame, so a second
        /// button submitted after it could never see the corner. The
        /// invisible button stays purely as a keyboard-focus stop; the
        /// glyph itself appears only while the row (or the dock) is
        /// hovered/focused or while its flyout is open, so rows never
        /// resize and the icon stays out of idle view.
        /// </summary>
        private static bool NavHelpDock(Vector2 min, Vector2 max, bool rowHovered,
            string helpId, ImDrawListPtr drawList, Action? drawer = null)
        {
            EnsurePageHelp(helpId, drawer);
            if (!_helpDrawers.TryGetValue(helpId, out var helpDraw))
            {
                return false;
            }
            var scale = SpheneUI.Scale;
            var side = NavRailItemHeight * scale;
            var iconMin = new Vector2(max.X - side, min.Y);
            var flyoutId = "pagehelp:" + helpId;
            // The row's own button already advanced the cursor past the row
            // (+ ItemSpacing) · capturing that resume point and restoring it
            // after the focus stop keeps the next rail item's gap intact.
            var resume = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(iconMin);
            ImGui.InvisibleButton("##navhelp:" + helpId, new Vector2(side, side));
            var helpFocused = ImGui.IsItemFocused();
            ImGui.SetCursorScreenPos(resume);
            var helpHover = ImGui.IsMouseHoveringRect(iconMin, max);
            var helpClicked = helpHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            if (rowHovered || helpHover || helpFocused
                || Popups.IsFlyoutOpen(flyoutId))
            {
                var qColor = helpHover
                    ? Colors.Accent
                    : ColorUtils.WithAlpha(Colors.TextMuted, 0.85f);
                using (Icons.PushFont())
                {
                    var q = FontAwesomeIcon.Question.ToIconString();
                    var qSize = ImGui.CalcTextSize(q);
                    drawList.AddText(new Vector2(
                            iconMin.X + (side - qSize.X) * 0.5f,
                            iconMin.Y + (max.Y - iconMin.Y - qSize.Y) * 0.5f),
                        ColorUtils.ToU32(qColor), q);
                }
            }
            if (helpHover)
            {
                AttachTooltip("About this page", true);
            }
            if (helpClicked
                || (helpFocused
                    && (ImGui.IsKeyPressed(ImGuiKey.Enter)
                        || ImGui.IsKeyPressed(ImGuiKey.Space))))
            {
                Popups.ToggleFlyout(flyoutId);
            }
            if (Popups.BeginFlyout(flyoutId))
            {
                helpDraw();
                Popups.EndFlyout();
            }
            return helpClicked;
        }

        private static readonly Dictionary<string, Action> _helpExtras = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Action> _helpDrawers = new(StringComparer.Ordinal);
        private static string? _currentHelpId;
        private static string? _lastHelpId;

        /// <summary>
        /// Registers extra explanation content for the current page's "?"
        /// flyout - the replacement for on-page "What is this?" foldouts.
        /// Call anywhere below the page's <see cref="PageHeader"/>.
        /// </summary>
        public static void HelpExtra(Action draw)
        {
            if (_currentHelpId != null)
            {
                _helpExtras[_currentHelpId] = draw;
            }
        }

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
        /// The page-anatomy action bar: one row, pinned below the scroll
        /// region. <paramref name="drawRight"/> renders the cluster docked
        /// flush at the card edge (primary action last/rightmost) inside
        /// <paramref name="rightWidth"/> pixels; the optional
        /// <paramref name="drawLeft"/> slot flows from the left edge in the
        /// space that remains · status text, hints or secondary actions.
        /// Left-slot items manage their own vertical alignment (call
        /// <see cref="ImGui.AlignTextToFramePadding"/> for text). Reserve
        /// the bar's height from the scroll region so it never scrolls.
        /// </summary>
        public static void ActionBar(float rightWidth, Action drawRight,
            Action<float>? drawLeft = null)
        {
            var lineMin = ImGui.GetCursorScreenPos();
            var rowH = ImGui.GetFrameHeight();
            var rowMaxY = lineMin.Y + rowH;

            Controls.DockToCardEdge(rightWidth);
            var dockMin = ImGui.GetCursorScreenPos();
            drawRight();
            rowMaxY = MathF.Max(rowMaxY, ImGui.GetItemRectMax().Y);

            if (drawLeft != null)
            {
                var leftAvail = dockMin.X - lineMin.X - Spacing.Inline.Scaled;
                if (leftAvail > 0f)
                {
                    ImGui.SetCursorScreenPos(lineMin);
                    // Left content owns only leftAvail · right-docking
                    // controls (Toggle, HelpText) must see that bound or a
                    // switch lands on top of the docked button and its late
                    // hit box swallows the button's clicks.
                    ContentBounds.Push(new Vector2(lineMin.X,
                        dockMin.X - Spacing.Inline.Scaled));
                    try
                    {
                        drawLeft(leftAvail);
                    }
                    finally
                    {
                        ContentBounds.Pop();
                    }
                    rowMaxY = MathF.Max(rowMaxY, ImGui.GetItemRectMax().Y);
                }
            }
            ImGui.SetCursorScreenPos(new Vector2(lineMin.X, rowMaxY));
        }

        /// <summary>
        /// Collapsible section in the segmented-pill language: a tonal header
        /// row (square chevron zone + seam + label, optional icon and a muted
        /// right-aligned hint) that toggles a bordered body sharing the
        /// header's bottom hairline. State persists per window via
        /// <see cref="ImGui.GetStateStorage"/> keyed on <paramref name="label"/>.
        /// <paramref name="setOpen"/> force-opens the section for the frame  · 
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

            // Chevron zone + seam · the same left-zone language as SegCore.
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
            // its real height · fills stay header-only so nothing overpaints.
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
            // 0..1 while the host swaps rail contents · the sliding
            // indicator holds its position instead of gliding under cover.
            public float Transition;
        }

        private static readonly Stack<NavRailState> NavRailStack = new();

        // Windows hosting a rail+content layout are fixed surfaces: their
        // children already reach the frame edges, so any scroll range they
        // report is incidental padding overflow · never real scrollable
        // content. BeginNavRail registers the host's rect every frame  · 
        // entries expire after a frame so rails that stop being drawn do
        // not linger · and EndScrollArea refuses to chain wheel input into
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

        /// <summary>Nav item pill height · matches the header nav buttons. Density-aware.</summary>
        private static float NavRailItemHeight => Density.Current switch
        {
            DensityProfile.Compact => 26f,
            DensityProfile.Comfortable => 34f,
            _ => 30f,
        };

        /// <summary>Inner horizontal margin of a nav rail band (unscaled)  · 
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
        /// widest icon glyph plus item padding · icon rails show a tooltip
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

        /// <summary>Scaled vertical footprint of <see cref="NavRailDivider"/> · pad, line, pad.</summary>
        public static float NavRailDividerHeight => Density.Current switch
        {
            DensityProfile.Compact => 9f,
            DensityProfile.Comfortable => 13f,
            _ => 11.5f,
        } * SpheneUI.Scale;

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
        /// instead of the parent window's title bar · used when a rail layout
        /// is rendered inside another window's content (e.g. Sphene Hub).
        /// <paramref name="topInset"/> reserves a pinned strip at the rail top  · 
        /// scrolled content is clipped below it (see <see cref="BeginScrollArea"/>).
        /// <paramref name="backdropPattern"/> draws the star-chart backdrop
        /// over the content region's backing gradient · that solid fill
        /// would otherwise hide the window-level pattern entirely.
        /// </summary>
        /// <param name="transition">0..1 progress of an ongoing rail content
        /// swap · while mid-transition the active indicator parks so its
        /// glide to the new item happens visibly after the swap settles.
        /// </param>
        public static float BeginNavRail(string id, float width, bool embedded = false,
            float topInset = 0f, bool backdropPattern = false, float transition = 0f,
            float bottomInset = 0f)
        {
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            RegisterNavRailHost(windowPos, windowSize);
            // Fresh frame, fresh claim · items report themselves active
            // below; a rail with no active item draws no stale indicator.
            if (!string.IsNullOrEmpty(id)
                && RailIndicators.TryGetValue(id, out var claimed))
            {
                claimed.Reported = false;
                claimed.ReportKey = null;
            }
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
                Transition = transition,
            });
            // The nav child is inset by NavRailPadX on both sides so pills
            // keep a visible margin to the rail edges · enforced by the
            // child rect itself, not by padding semantics.
            var railPad = NavRailPadX * SpheneUI.Scale;
            ImGui.SetCursorScreenPos(new Vector2(railMin.X + railPad, contentTop.Y));
            BeginScrollArea(id + "##nav", new Vector2(
                    MathF.Max(0f, railMax.X - railMin.X - railPad * 2f),
                    MathF.Max(0f, windowPos.Y + windowSize.Y - contentTop.Y - bottomInset)),
                contentPadding: new Vector2(0f, 8f), topInset: topInset);
            return width;
        }

        /// <summary>
        /// Closes the rail opened by <see cref="BeginNavRail"/>: ends the nav
        /// scroll area with fades tinted to the rail color. The rail state
        /// stays on the stack so <see cref="BeginNavContent"/> can read it.
        /// <paramref name="fadeMinY"/> keeps scroll fades below a pinned strip.
        /// </summary>
        /// <summary>Per-rail sliding active indicator · the eased Y position
        /// glides toward whichever <see cref="NavRailSegmentItem"/> reported
        /// itself active, Discord-server-rail style. The tracked item key
        /// separates a navigation (new item → glide) from the same item
        /// physically moving under the bar (scroll/drag/pan → stay glued).
        /// </summary>
        private sealed class RailIndicator
        {
            public float Y;
            public float Target;
            public float PrevTarget;
            public Vector4 Accent;
            public string? TrackKey;
            public string? ReportKey;
            public double Born;
            public bool Reported;
        }

        private static readonly Dictionary<string, RailIndicator> RailIndicators = new(StringComparer.Ordinal);

        public static void EndNavRail(float? fadeMinY = null)
        {
            var state = NavRailStack.Count > 0 ? NavRailStack.Peek() : new NavRailState();
            // Child scroll while still inside it · the indicator below fades
            // with the same ramp the scroll fades apply to the items, so the
            // bar never rides past the dissolve zone when the active row is
            // scrolled under the rail's edge.
            var railScrollY = ImGui.GetScrollY();
            var railScrollMaxY = ImGui.GetScrollMaxY();
            EndScrollArea(fadeBaseColor: state.RailBg,
                fadeSpanX: new Vector2(state.RailMin.X, state.RailMax.X), fadeMinY: fadeMinY);

            if (!string.IsNullOrEmpty(state.Id)
                && RailIndicators.TryGetValue(state.Id, out var ind)
                && ind.Reported)
            {
                var dt = ImGui.GetIO().DeltaTime;
                // A target that is itself travelling (section pan sliding the
                // items in) must not be chased · the bar holds its position
                // and only glides once the destination is stable, so the
                // travel happens after the veil clears and stays visible.
                var targetMoving = MathF.Abs(ind.Target - ind.PrevTarget) > 0.1f;
                ind.PrevTarget = ind.Target;
                var railSwapping = state.Transition is > 0.01f and < 0.99f;
                if (!string.Equals(ind.TrackKey, ind.ReportKey, StringComparison.Ordinal))
                {
                    // A different item claimed active · glide toward it and
                    // adopt its key on arrival. While the rail swaps contents
                    // the bar parks: gliding under the dissolve veils would
                    // hide the travel entirely. The first report on a rail
                    // has nothing to glide from; the grow-in ramp below
                    // covers the materialization instead of a pop.
                    if (ind.Y <= 0f)
                    {
                        ind.Y = ind.Target;
                    }
                    else if (!targetMoving && !railSwapping)
                    {
                        ind.Y += (ind.Target - ind.Y) * MathF.Min(1f, dt * 13f);
                        if (MathF.Abs(ind.Y - ind.Target) < 0.5f)
                        {
                            ind.Y = ind.Target;
                            ind.TrackKey = ind.ReportKey;
                        }
                    }
                }
                else
                {
                    // Same item, physically moved (rail scroll, window drag,
                    // section pan) · glued to it, never a delayed chase.
                    ind.Y = ind.Target;
                }

                var scale = SpheneUI.Scale;
                var grow = Math.Clamp((float)((ImGui.GetTime() - ind.Born) / 0.18f), 0f, 1f);
                var y = ind.Y;
                // Match the scroll fades: the bar dissolves over the same
                // band instead of jutting past them when the row scrolls out.
                var fadeH = Chrome.ScrollFadeHeight * scale;
                var fadeEdge = 1f;
                if (railScrollY > Chrome.ScrollFadeDeadZone && fadeH > 0f)
                {
                    var top = fadeMinY ?? state.ContentTop.Y;
                    fadeEdge = Math.Min(fadeEdge, Math.Clamp((y - top) / fadeH, 0f, 1f));
                }
                if (railScrollMaxY - railScrollY > Chrome.ScrollFadeDeadZone && fadeH > 0f)
                {
                    fadeEdge = Math.Min(fadeEdge,
                        Math.Clamp((state.RailMax.Y - y) / fadeH, 0f, 1f));
                }
                grow *= fadeEdge * fadeEdge * (3f - 2f * fadeEdge);
                var barH = NavRailItemHeight * scale * 0.55f * (0.65f + 0.35f * grow);
                var x = state.RailMin.X + NavRailPadX * scale * 0.45f;
                var dl = ImGui.GetWindowDrawList();
                dl.AddRectFilled(new Vector2(x - 2f * scale, y - barH * 0.5f - 2f * scale),
                    new Vector2(x + 4.5f * scale, y + barH * 0.5f + 2f * scale),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(ind.Accent, 0.16f * grow)), 3f * scale);
                dl.AddRectFilled(new Vector2(x, y - barH * 0.5f),
                    new Vector2(x + 2.5f * scale, y + barH * 0.5f),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(ind.Accent, 0.9f * grow)), 2f * scale);
            }
        }

        // Remembers per nested nav-content how it was opened so
        // EndNavContent can pair BeginChild/EndScrollArea correctly, tint
        // the fades toward the same surface and · for the non-scrollable
        // variant · re-stamp the window frame chrome the solid backing
        // covered. A plain BeginChild otherwise buries the resize grip at
        // the host corner: only scroll areas restore it via GripCorner.
        private sealed class NavContentState
        {
            public bool Scrollable;
            public bool ScrollHints;
            public Vector4? BaseColor;
            public bool HasHost;
            public bool Begun;
            public int Depth;
            public Vector2 HostMin;
            public Vector2 HostMax;
        }

        private static readonly Stack<NavContentState> NavContentStack = new();

        /// <summary>
        /// Moves the cursor right of the last opened rail and begins the
        /// content scroll area (no padding, no own background so the window
        /// surface shows through). Pops the rail state · call after
        /// <see cref="EndNavRail"/> and close with <see cref="EndNavContent"/>.
        /// Pass <paramref name="scrollable"/> = false for a plain child when
        /// the page hosts content that manages its own scrolling (e.g. an
        /// embedded nav-rail layout). <paramref name="padded"/> insets the
        /// content uniformly (<see cref="Spacing.PagePadding"/> on
        /// all sides) · pages then start flush at the content origin without
        /// their own page margins. The scroll fades still render across the
        /// full child bounds, so the padding never clips them. Set
        /// <paramref name="padded"/> = false only for content that draws its
        /// own edge-to-edge chrome.
        /// <paramref name="fixedContent"/> draws fixed page chrome (banner,
        /// top tabs) in the PARENT window at the content origin - above the
        /// scroll child, so it never scrolls - and returns the consumed
        /// height. The child then opens below it, reduced by that amount
        /// (clamped so a usable body always remains).
        /// <paramref name="scrollHints"/> toggles the centered scroll pill
        /// the page viewport draws at its edges; the edge fades always stay.
        /// </summary>
        /// </summary>
        public static bool BeginNavContent(string id, bool scrollable = true, bool padded = true,
            float contentInset = -1f, Func<Vector2, float>? fixedContent = null,
            bool scrollHints = true)
        {
            var hadRail = NavRailStack.Count > 0;
            var state = hadRail ? NavRailStack.Pop() : new NavRailState();
            var contentMin = state.ContentTop + new Vector2(state.Width, 0f);
            // The content child reaches the parent window's right and bottom
            // edges, not the content region's end (which stops windowPadding
            // short of both). Otherwise the right card margin ends up padding
            // + inset wider than the left one and embedded rails leave a bare
            // window-background strip under their band.
            var size = new Vector2(
                MathF.Max(0f, state.WindowRight - contentMin.X),
                MathF.Max(0f, state.WindowBottom - contentMin.Y));
            // The page padding inside the scroll child would expose the
            // translucent window surface · and the blur behind it · along the
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
            // frame arc · a square fill would blunt the frame's rounding.
            var style = ImGui.GetStyle();
            var cornerR = MathF.Max(0f, style.WindowRounding);
            var topRightR = contentMin.Y <= state.WindowMin.Y + 0.5f ? cornerR : 0f;
            var bottomRightR = contentMin.Y + size.Y >= parentMax.Y - 0.5f ? cornerR : 0f;
            ImGui.PushClipRect(state.WindowMin, parentMax, false);
            Chrome.VerticalFade(ImGui.GetWindowDrawList(), contentMin, contentMin + size,
                bgTop, bgBottom, 0f, topRightR, 0f, bottomRightR);
            // The solid backing above hides the window-level backdrop · the
            // rail host asks for the pattern to be repeated on top of it.
            if (state.Pattern)
            {
                Fx.Constellation(ImGui.GetWindowDrawList(), contentMin, contentMin + size);
            }
            ImGui.PopClipRect();
            // Fixed page chrome (banner / top tabs) · drawn as ordinary
            // parent-window widgets at contentMin so they never scroll with
            // the body. The same bound the scroll child would push is
            // mirrored so bound-aware helpers dock identically in the zone.
            var fixedH = 0f;
            if (fixedContent != null)
            {
                var insetX = padded
                    ? Spacing.PagePadding.Scaled
                    : (contentInset < 0f ? ScrollAreaContentInset * SpheneUI.Scale : contentInset);
                var insetRight = padded
                    ? insetX
                    : insetX + 1f * SpheneUI.Scale;
                ContentBounds.Push(new Vector2(contentMin.X + insetX,
                    contentMin.X + size.X - insetRight));
                try
                {
                    fixedH = MathF.Max(0f, fixedContent(contentMin));
                }
                finally
                {
                    ContentBounds.Pop();
                }
                // Never let the fixed zone consume the body: the scroll
                // child keeps a usable minimum height.
                fixedH = MathF.Min(fixedH,
                    MathF.Max(0f, size.Y - 48f * SpheneUI.Scale));
            }
            var navContent = new NavContentState
            {
                Scrollable = scrollable,
                ScrollHints = scrollHints,
                BaseColor = baseColor,
                HasHost = hadRail,
                Depth = ScrollAreaStack.Count,
                HostMin = state.WindowMin,
                HostMax = parentMax,
            };
            NavContentStack.Push(navContent);
            ImGui.SetCursorScreenPos(contentMin + new Vector2(0f, fixedH));
            var bodySize = size - new Vector2(0f, fixedH);
            if (scrollable)
            {
                if (padded)
                {
                    return BeginScrollArea(id + "##content", bodySize,
                        windowFlags: ImGuiWindowFlags.NoBackground,
                        contentPadding: new Vector2(Spacing.PagePadding.Value));
                }
                return BeginScrollArea(id + "##content", bodySize,
                    windowFlags: ImGuiWindowFlags.NoBackground, noPadding: true,
                    contentInset: contentInset);
            }
            // Non-scrollable nav content must offer the same geometry as the
            // scroll path. With padded, the fixed child carries the same
            // PagePadding inset the scrollable variant applies via
            // contentPadding · AlwaysUseWindowPadding so the borderless
            // child actually honors it. Unpadded stays edge-to-edge for
            // embedded layouts that manage their own rails. NoScrollbar:
            // inner scroll areas push the cursor a hair past the child
            // bottom (trailing item spacing), which would surface a
            // permanent bar that eats into the embedded layout's width.
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
                padded ? new Vector2(Spacing.PagePadding.Value * SpheneUI.Scale) : Vector2.Zero);
            var contentBegun = ImGui.BeginChild(id + "##content", bodySize, false,
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse
                | (padded ? ImGuiWindowFlags.AlwaysUseWindowPadding : ImGuiWindowFlags.None));
            ImGui.PopStyleVar();
            navContent.Begun = contentBegun;
            return contentBegun;
        }

        /// <summary>Ends the content area opened by <see cref="BeginNavContent"/>.</summary>
        public static void EndNavContent()
        {
            var state = NavContentStack.Count > 0 ? NavContentStack.Pop() : null;
            if (state?.Scrollable ?? true)
            {
                EndScrollArea(fadeBaseColor: state?.BaseColor,
                    scrollHints: state?.ScrollHints ?? true);
                return;
            }
            var childMin = ImGui.GetWindowPos();
            var childMax = childMin + ImGui.GetWindowSize();
            ImGui.EndChild();
            // Only a top-level nav content may own frame chrome: nested ones
            // live inside a child, whose "corner" sits mid-window and is
            // neither a resize affordance nor a real window border.
            var isTopLevel = NavContentStack.Count == 0 && state.Depth == 0;
            if (state is not { Begun: true, HasHost: true } || !isTopLevel)
            {
                return;
            }
            // The fixed content child never passes through EndScrollArea,
            // which is where scrollable pages get their frame chrome back:
            // the solid backing painted up to the window edges covers the
            // border and the native resize grip. Re-stamp both through the
            // same sibling-overlay child EndScrollArea uses, so it draws
            // above the content subtree.
            var styleVars = ImGui.GetStyle();
            var cornerReach = MathF.Max(styleVars.WindowBorderSize,
                    MathF.Max(styleVars.WindowPadding.X, styleVars.WindowPadding.Y))
                + 4f * SpheneUI.Scale;
            var ownsCorner = state.HostMax.X - childMax.X <= cornerReach
                && state.HostMax.Y - childMax.Y <= cornerReach;
            var resumeCursor = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(childMin);
            ImGui.BeginChild("##navcontent-chrome", childMax - childMin, false,
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings);
            Chrome.RedrawWindowBorder(childMin, childMax, state.HostMin, state.HostMax, cornerReach);
            if (ownsCorner)
            {
                Chrome.GripCorner(state.HostMax, false, 0f, out _, out _);
            }
            ImGui.EndChild();
            ImGui.SetCursorScreenPos(resumeCursor);
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
        /// Square icon button for <see cref="NavRailItem"/> in icon mode  · 
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
        /// the label by a thin seam. The zone carries the accent color · idle
        /// as a faint tint, fully lit when the item is active · while the
        /// label area only picks up a soft fill. Size the rail with
        /// <see cref="NavRailSegmentedWidth"/>.
        /// <paramref name="special"/> tints the idle label in the accent color
        /// for highlighted entries (e.g. Supporter).
        /// </summary>
        /// <summary>Per-item engagement blend driving the nav item transition
        /// (idle → hover → active) · keyed by label so each row animates
        /// independently like a regulator gauge easing into its target.</summary>
        private static readonly Dictionary<string, float> NavItemBlend = new(StringComparer.Ordinal);

        public static bool NavRailSegmentItem(string label, FontAwesomeIcon icon, bool active, Vector4 accent,
            bool special = false, string? helpId = null, Action? helpDrawer = null)
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

            // Ease toward the engagement target · active snaps the gauge fully
            // lit, hover settles mid-way, held punches slightly past hover.
            var target = active ? 1f : held ? 0.62f : hovered ? 0.45f : 0f;
            var blend = NavItemBlend.GetValueOrDefault(label);
            blend += (target - blend) * MathF.Min(1f, ImGui.GetIO().DeltaTime * 12f);
            if (MathF.Abs(blend - target) < 0.005f) blend = target;
            NavItemBlend[label] = blend;

            // Feed the rail's sliding indicator · the active row's center is
            // this frame's target; the label is the item identity EndNavRail
            // compares against the eased position's tracked key. While the
            // tracked item is still being drawn it keeps the claim · during a
            // section pan the bar rides out with the old item and only glides
            // to the new one once the pan settles, so the travel stays
            // visible instead of happening under the midpoint veil.
            if (active && NavRailStack.Count > 0)
            {
                NavRailReportActive(NavRailStack.Peek().Id, label, (min.Y + max.Y) * 0.5f, accent);
            }

            var drawList = ImGui.GetWindowDrawList();
            var zoneW = NavRailItemHeight * scale;
            var zoneMax = new Vector2(min.X + zoneW, max.Y);

            // Vivid active state · once the ease crosses ~85% the whole pill
            // lights up in the accent gradient (the mockup's lit sidebar
            // pill) instead of staying a faint tint under a lit zone.
            var activeT = Math.Clamp((blend - 0.85f) / 0.15f, 0f, 1f);
            if (activeT > 0f)
            {
                var (gDeep, gHi) = Colors.AccentRamp(accent);
                gDeep.W = (0.40f + 0.50f * activeT) * ImGui.GetStyle().Alpha;
                gHi.W = (0.40f + 0.50f * activeT) * ImGui.GetStyle().Alpha;
                Fx.AmbientGlow(drawList, min, max, radius, accent, 0.55f * activeT);
                Chrome.RoundedHorizontalGradient(drawList, min, max, radius, gDeep, gHi);
            }
            else
            {
                var pillTint = ColorUtils.LerpColor(Vector4.One, accent, MathF.Min(1f, blend * 2f));
                var pillFill = ColorUtils.WithAlpha(pillTint, 0.045f + 0.115f * blend);
                drawList.AddRectFilled(min, max, ColorUtils.ToU32(pillFill), radius);
            }

            var zoneFill = blend > 0.85f
                ? ColorUtils.WithAlpha(accent, 0.08f + 0.84f * blend)
                : ColorUtils.WithAlpha(
                    ColorUtils.LerpColor(Vector4.One, accent, MathF.Min(1f, blend * 2.4f)),
                    0.08f + 0.26f * blend);
            drawList.AddRectFilled(min, zoneMax, ColorUtils.ToU32(zoneFill),
                radius, ImDrawFlags.RoundCornersLeft);

            var seamColor = blend > 0.85f
                ? ColorUtils.WithAlpha(ColorUtils.LerpColor(accent, Vector4.One, 0.5f), 0.55f)
                : ColorUtils.WithAlpha(Vector4.One, 0.09f + 0.2f * blend);
            var seamInset = 6f * scale;
            drawList.AddLine(new Vector2(zoneMax.X - 0.5f, min.Y + seamInset),
                new Vector2(zoneMax.X - 0.5f, max.Y - seamInset), ColorUtils.ToU32(seamColor));

            var glyphColor = blend > 0.85f ? ColorUtils.ContrastTextColor(accent)
                : ColorUtils.LerpColor(accent, Vector4.One, 0.1f + 0.2f * blend);
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
            var textColor = activeT > 0f
                ? ColorUtils.ToU32(ColorUtils.LerpColor(
                    ColorUtils.LerpColor(accent, Vector4.One, 0.25f),
                    ColorUtils.ContrastTextColor(accent), activeT))
                : special ? ColorUtils.ToU32(accent) : ImGui.GetColorU32(ImGuiCol.Text);
            ImGui.PushClipRect(new Vector2(textX, min.Y),
                new Vector2(max.X - textPadX * 0.5f, max.Y), true);
            drawList.AddText(new Vector2(textX,
                    min.Y + (max.Y - min.Y - labelSize.Y) * 0.5f), textColor, label);
            ImGui.PopClipRect();
            var helpConsumed = helpId != null
                && NavHelpDock(min, max, hovered, helpId, drawList, helpDrawer);
            return clicked && !helpConsumed;
        }

        /// <summary>
        /// Fixed first row of a nav rail that morphs between two identities in
        /// place · a "recompile" sweep: the outgoing glyph decays into ghost
        /// fragments ahead of an accent scanline while the incoming glyph
        /// materializes behind it over a soft bloom, and the labels crossfade
        /// with a short drift. The row itself never pans or scrolls · only
        /// its contents morph with <paramref name="morph"/> (0 = state A,
        /// 1 = state B). It reports no indicator claim itself; the caller
        /// feeds <see cref="NavRailReportActive"/> since the slot lives in an
        /// overlay child outside the scroll rail's stack.
        /// </summary>
        public static bool NavRailTopSlotItem(string labelA, FontAwesomeIcon iconA, bool activeA,
            string labelB, FontAwesomeIcon iconB, bool activeB, float morph, Vector4 accent,
            string? helpId = null, Action? helpDrawer = null)
        {
            var scale = SpheneUI.Scale;
            var radius = Metrics.ButtonRadius;
            var clicked = ImGui.InvisibleButton("##navrail-top", new Vector2(-1f, NavRailItemHeight * scale));
            var hovered = ImGui.IsItemHovered();
            var held = ImGui.IsItemActive();
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            Lint.CheckBounds("navrail-top");

            var active = morph < 0.5f ? activeA : activeB;
            var target = active ? 1f : held ? 0.62f : hovered ? 0.45f : 0f;
            var blend = NavItemBlend.GetValueOrDefault("rail-top-slot");
            blend += (target - blend) * MathF.Min(1f, ImGui.GetIO().DeltaTime * 12f);
            if (MathF.Abs(blend - target) < 0.005f) blend = target;
            NavItemBlend["rail-top-slot"] = blend;

            var drawList = ImGui.GetWindowDrawList();
            var zoneW = NavRailItemHeight * scale;
            var zoneMax = new Vector2(min.X + zoneW, max.Y);
            var charge = MathF.Sin(morph * MathF.PI);

            var activeT = Math.Clamp((blend - 0.85f) / 0.15f, 0f, 1f);
            if (activeT > 0f)
            {
                var (gDeep, gHi) = Colors.AccentRamp(accent);
                gDeep.W = (0.40f + 0.50f * activeT) * ImGui.GetStyle().Alpha;
                gHi.W = (0.40f + 0.50f * activeT) * ImGui.GetStyle().Alpha;
                Fx.AmbientGlow(drawList, min, max, radius, accent, 0.55f * activeT);
                Chrome.RoundedHorizontalGradient(drawList, min, max, radius, gDeep, gHi);
            }
            else
            {
                var pillTint = ColorUtils.LerpColor(Vector4.One, accent, MathF.Min(1f, blend * 2f));
                var pillFill = ColorUtils.WithAlpha(pillTint, 0.045f + 0.115f * blend + 0.05f * charge);
                drawList.AddRectFilled(min, max, ColorUtils.ToU32(pillFill), radius);
            }

            var zoneFill = blend > 0.85f
                ? ColorUtils.WithAlpha(accent, 0.08f + 0.84f * blend)
                : ColorUtils.WithAlpha(
                    ColorUtils.LerpColor(Vector4.One, accent, MathF.Min(1f, blend * 2.4f)),
                    0.08f + 0.26f * blend);
            drawList.AddRectFilled(min, zoneMax, ColorUtils.ToU32(zoneFill),
                radius, ImDrawFlags.RoundCornersLeft);

            var seamColor = blend > 0.85f
                ? ColorUtils.WithAlpha(ColorUtils.LerpColor(accent, Vector4.One, 0.5f), 0.55f)
                : ColorUtils.WithAlpha(Vector4.One, 0.09f + 0.2f * blend);
            var seamInset = 6f * scale;
            drawList.AddLine(new Vector2(zoneMax.X - 0.5f, min.Y + seamInset),
                new Vector2(zoneMax.X - 0.5f, max.Y - seamInset), ColorUtils.ToU32(seamColor));

            var sweepX = min.X + zoneW * morph;
            var glyphColor = blend > 0.85f
                ? ColorUtils.ContrastTextColor(accent)
                : ColorUtils.LerpColor(accent, Vector4.One, 0.1f + 0.2f * blend);
            using (Icons.PushFont())
            {
                var glyphA = iconA.ToIconString();
                var glyphB = iconB.ToIconString();
                var glyphSizeA = ImGui.CalcTextSize(glyphA);
                var glyphSizeB = ImGui.CalcTextSize(glyphB);
                var posA = new Vector2(min.X + (zoneW - glyphSizeA.X) * 0.5f,
                    min.Y + (max.Y - min.Y - glyphSizeA.Y) * 0.5f);
                var posB = new Vector2(min.X + (zoneW - glyphSizeB.X) * 0.5f,
                    min.Y + (max.Y - min.Y - glyphSizeB.Y) * 0.5f);
                if (morph < 0.999f)
                {
                    drawList.PushClipRect(new Vector2(sweepX, min.Y), zoneMax, true);
                    // Ghost fragments · the memory shedding data as it decays.
                    var ghost = ColorUtils.WithAlpha(accent, 0.5f * charge);
                    drawList.AddText(posA + new Vector2(morph * 2f * scale, -morph * 2f * scale),
                        ColorUtils.ToU32(ghost), glyphA);
                    drawList.AddText(posA + new Vector2(-morph * 1.5f * scale, morph * 1.5f * scale),
                        ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.3f * charge)), glyphA);
                    drawList.AddText(posA, ColorUtils.ToU32(
                        ColorUtils.WithAlpha(glyphColor, glyphColor.W * (1f - morph))), glyphA);
                    drawList.PopClipRect();
                }
                if (morph > 0.001f)
                {
                    drawList.PushClipRect(min, new Vector2(sweepX, max.Y), true);
                    drawList.AddCircleFilled(
                        new Vector2(min.X + zoneW * 0.5f, (min.Y + max.Y) * 0.5f),
                        zoneW * 0.32f, ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.3f * charge)));
                    drawList.AddText(posB, ColorUtils.ToU32(
                        ColorUtils.WithAlpha(glyphColor, glyphColor.W * morph)), glyphB);
                    drawList.PopClipRect();
                }
            }
            if (morph is > 0.001f and < 0.999f)
            {
                var lineMin = new Vector2(sweepX - 0.75f, min.Y + 4f * scale);
                var lineMax = new Vector2(sweepX + 0.75f, max.Y - 4f * scale);
                drawList.AddRectFilled(new Vector2(sweepX - 3f * scale, lineMin.Y),
                    new Vector2(sweepX + 3f * scale, lineMax.Y),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.35f * charge)));
                drawList.AddRectFilled(lineMin, lineMax, ColorUtils.ToU32(
                    ColorUtils.WithAlpha(ColorUtils.LerpColor(accent, Vector4.One, 0.5f), charge)));
            }

            var textPadX = NavRailItemPadX * scale;
            var textX = zoneMax.X + textPadX;
            var textColor = activeT > 0f
                ? ColorUtils.LerpColor(
                    ColorUtils.LerpColor(accent, Vector4.One, 0.25f),
                    ColorUtils.ContrastTextColor(accent), activeT)
                : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            var textY = min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) * 0.5f;
            var drift = 5f * scale;
            drawList.PushClipRect(new Vector2(textX, min.Y),
                new Vector2(max.X - textPadX * 0.5f, max.Y), true);
            if (morph < 0.999f)
            {
                drawList.AddText(new Vector2(textX - drift * morph, textY),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(textColor, textColor.W * (1f - morph))), labelA);
            }
            if (morph > 0.001f)
            {
                drawList.AddText(new Vector2(textX + drift * (1f - morph), textY),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(textColor, textColor.W * morph)), labelB);
            }
            drawList.PopClipRect();
            // The "?" dock belongs to the A identity · while the row reads
            // "Back to Hub" the slot is a back button, not a page entry.
            var helpConsumed = helpId != null && morph < 0.5f
                && NavHelpDock(min, max, hovered, helpId, drawList, helpDrawer);
            return clicked && !helpConsumed;
        }

        /// <summary>
        /// Feeds the rail's sliding indicator from outside the scroll child  · 
        /// used by fixed overlay items like <see cref="NavRailTopSlotItem"/>.
        /// </summary>
        public static void NavRailReportActive(string railId, string key, float centerY, Vector4 accent)
        {
            if (!RailIndicators.TryGetValue(railId, out var ind))
            {
                ind = new RailIndicator { Born = ImGui.GetTime() };
                RailIndicators[railId] = ind;
            }
            if (!ind.Reported
                || !string.Equals(ind.ReportKey, ind.TrackKey, StringComparison.Ordinal))
            {
                ind.ReportKey = key;
                ind.Target = centerY;
                ind.Accent = accent;
            }
            ind.Reported = true;
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
            // The banded first label carries no top spacer of its own · the
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

    /// <summary>RAII scope returned by <see cref="Layout.BeginPane"/>.</summary>
    public sealed class PaneScope : IDisposable
    {
        internal PaneScope(bool success)
        {
            Success = success;
        }

        /// <summary>Whether the inner child accepted content this frame.</summary>
        public bool Success { get; }

        public static implicit operator bool(PaneScope scope) => scope.Success;

        public void Dispose() => Layout.EndScrollArea();
    }

    /// <summary>RAII scope returned by <see cref="Layout.BeginCardColumns"/>.</summary>
    public sealed class CardColumnsScope : IDisposable
    {
        internal CardColumnsScope(bool success)
        {
            Success = success;
        }

        /// <summary>Whether the underlying ImGui table was successfully begun.</summary>
        public bool Success { get; }

        public static implicit operator bool(CardColumnsScope scope) => scope.Success;

        public void Dispose()
        {
            if (Success)
            {
                ImGui.EndTable();
            }
            ImGui.PopStyleVar();
        }
    }
}
