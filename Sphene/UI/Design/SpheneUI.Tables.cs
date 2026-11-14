using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Themed data tables in the settings-card visual language: a rounded card
    /// frame (same radius/border as AccentCard), a nested tonal header cap,
    /// subtle striping and accent-aware row states. Scrollable tables hide the
    /// native scrollbar and use <see cref="Chrome.ScrollFades"/> like every
    /// other scroll region. Replaces raw BeginTable/TableHeadersRow plus
    /// hand-rolled row highlights. Tables without <see cref="HeaderRow"/> are
    /// treated as invisible layout plumbing and get no frame.
    /// </summary>
    public static class Tables
    {
        private static TableScope? ActiveScope { get; set; }

        internal static void CloseScope(TableScope scope) => ActiveScope = scope.Previous;

        /// <summary>
        /// Begins a themed table. Pushes a slim <see cref="ImGuiStyleVar.CellPadding"/>,
        /// a tonal header background and a subtle alternating row tint before
        /// opening the table; the inner scroll child gets a zero-width scrollbar
        /// so scrolling is signalled by edge fades instead. Check
        /// <see cref="TableScope.Success"/> before emitting cells.
        /// </summary>
        public static TableScope Begin(string id, int columns, ImGuiTableFlags flags,
            Vector2 size = default, Vector4? accent = null)
        {
            var scale = Scale;
            var acc = accent ?? Colors.Accent;
            var wantsScrollY = (flags & ImGuiTableFlags.ScrollY) != ImGuiTableFlags.None;
            // The card frame replaces ImGui's square outer borders · keep the
            // inner dividers callers asked for.
            flags &= ~(ImGuiTableFlags.BordersOuterH | ImGuiTableFlags.BordersOuterV);
            // CellPaddingX is snapshotted at BeginTable · this is the ONLY
            // place that controls the horizontal inset of header labels and
            // body cells alike. Keep it generous: content never hugs the
            // card frame's edge.
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(8f, 2.5f) * scale);
            // Only windows created while this is pushed inherit it, so it hides
            // exactly the table's inner scroll bar · mouse-wheel scrolling and
            // programmatic scroll keep working like the design's scroll areas.
            if (wantsScrollY)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 0f);
            }
            // The header band is drawn by HeaderRow as a rounded cap · keep the
            // native fill transparent so it does not shine through.
            ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, 0u);
            ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, ColorUtils.ToU32(
                ColorUtils.WithAlpha(Vector4.One, 0.035f)));
            // ScrollY tables switch the current window to the inner scroll
            // child · header chrome painted later must go to THIS (outer)
            // draw list or it scrolls away with the body.
            var outerDrawList = ImGui.GetWindowDrawList();
            var scope = new TableScope(ImGui.BeginTable(id, columns, flags, size));
            scope.OuterDrawList = outerDrawList;
            if (scope.Success)
            {
                // Right after BeginTable the item rect is the table's outer
                // rect · exact geometry for the header band and edge fades.
                scope.TableMin = ImGui.GetItemRectMin();
                scope.TableMax = ImGui.GetItemRectMax();
            }
            scope.WantsScrollY = wantsScrollY;
            scope.Accent = acc;
            scope.Previous = ActiveScope;
            ActiveScope = scope;
            return scope;
        }

        /// <summary>
        /// Registers a column. Header labels render uppercase · the same quiet
        /// caps treatment as the rail category labels · so pass plain title
        /// case here and let the design own the casing.
        /// </summary>
        public static void Column(string label,
            ImGuiTableColumnFlags flags = ImGuiTableColumnFlags.None, float width = 0f)
            => ImGui.TableSetupColumn(label.ToUpperInvariant(), flags, width);

        /// <summary>
        /// Column width that fits the (uppercased) header label plus room for
        /// the sort arrow · use for compact fixed-width columns next to a
        /// stretch column.
        /// </summary>
        public static float HeaderFitWidth(string label, float extra = 0f)
            => ImGui.CalcTextSize(label.ToUpperInvariant()).X
               + ImGui.GetFontSize() + 16f * Scale + extra;

        /// <summary>Read-only access to the current sort specs for sortable tables.</summary>
        public static ImGuiTableSortSpecsPtr SortSpecs() => ImGui.TableGetSortSpecs();

        /// <summary>
        /// Freezes the header row and draws the designed header chrome: a
        /// rounded tonal cap over the table width, quiet column ticks, an
        /// accent seam at the header/body boundary and muted caps labels.
        /// </summary>
        public static void HeaderRow()
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            var scale = Scale;
            // Header chrome belongs to the outer window · for ScrollY tables
            // the current draw list is the inner scroll child's and would
            // scroll the band away with the body.
            var dl = ActiveScope?.OuterDrawList ?? ImGui.GetWindowDrawList();
            var acc = ActiveScope?.Accent ?? Colors.Accent;
            // Extra vertical room for the header band · CellPaddingX was
            // snapshotted at BeginTable (labels already sit off the frame),
            // but the row height reads the style at call time, so this push
            // only buys the band its taller silhouette.
            using var headerPad = ImRaii.PushStyle(ImGuiStyleVar.CellPadding,
                new Vector2(8f, 4f) * scale);
            // GetCursorScreenPos inside the scroll child is scroll-shifted
            // (Pos + Cursor - Scroll), but the frozen header renders at the
            // UNSCROLLED position and the child's Scroll persists across
            // frames · without this the band floats above the table once the
            // user has scrolled, and HeaderBottomY drags the fade up into
            // the label row.
            var y0 = ImGui.GetCursorScreenPos().Y
                + (ActiveScope is { WantsScrollY: true } ? ImGui.GetScrollY() : 0f);
            var headerH = ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2f;
            var minX = ActiveScope?.TableMin.X ?? ImGui.GetCursorScreenPos().X;
            var maxX = ActiveScope?.TableMax.X ?? minX + ImGui.GetContentRegionAvail().X;
            // Inset by the frame border so the cap nests inside the card frame
            // the scope draws on dispose · concentric rounding like card chrome.
            var frameInset = Chrome.CardBorderThickness;
            var bandMin = new Vector2(minX + frameInset, y0);
            var bandMax = new Vector2(maxX - frameInset, y0 + headerH);

            // Rounded cap · top corners only, like a card header.
            dl.AddRectFilled(bandMin, bandMax,
                ColorUtils.ToU32(ColorUtils.LerpColor(Layout.NavRailBandColor(), acc, 0.12f)),
                MathF.Max(2f * scale, Layout.AccentCardRounding * scale - frameInset),
                ImDrawFlags.RoundCornersTop);
            // Accent seam closing the header · same language as card seams.
            var seamH = MathF.Max(1f, scale);
            dl.AddRectFilled(new Vector2(bandMin.X, bandMax.Y - seamH), bandMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(acc, 0.35f)));

            using var color = ImRaii.PushColor(ImGuiCol.Text, ColorUtils.ToU32(Colors.TextMuted));
            ApplyHeaderLabelInset(9f * scale);
            ImGui.TableHeadersRow();
            // Column positions are only final once the first row ran layout.
            DrawHeaderColumnSeparators(bandMin, bandMax);
            if (ActiveScope is { } scope)
            {
                scope.HeaderSeen = true;
                // The band's bottom edge is the authoritative boundary for the
                // top scroll fade · the scroll child's window pos (InnerTopY)
                // can sit above it, letting the fade paint through the header.
                scope.HeaderBottomY = bandMax.Y;
            }
            CaptureScroll();
        }

        /// <summary>
        /// Header labels lay out from the per-column WorkRect · refresh the
        /// cell padding ImGui snapshotted at BeginTable so labels sit at the
        /// design inset no matter which CellPadding was active then.
        /// </summary>
        private static unsafe void ApplyHeaderLabelInset(float padX)
        {
            var table = ImGui.GetCurrentContext().CurrentTable;
            if (table.Handle == null) return;
            table.Handle->CellPaddingX = padX;
        }

        /// <summary>
        /// Quiet vertical ticks between header columns · inset to the band's
        /// middle so they read as separators, not borders.
        /// </summary>
        private static unsafe void DrawHeaderColumnSeparators(Vector2 bandMin, Vector2 bandMax)
        {
            var table = ImGui.GetCurrentContext().CurrentTable;
            if (table.Handle == null) return;
            var dl = ActiveScope?.OuterDrawList ?? ImGui.GetWindowDrawList();
            var col = ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.TextMuted, 0.22f));
            var insetY = (bandMax.Y - bandMin.Y) * 0.30f;
            var y1 = bandMin.Y + insetY;
            var y2 = bandMax.Y - insetY;
            var thickness = MathF.Max(1f, Scale * 0.75f);
            for (var i = 1; i < table.ColumnsCount; i++)
            {
                var column = table.Columns.Data[i];
                var x = column.MinX;
                if (column.IsEnabled == 0 || x <= bandMin.X + 1f || x >= bandMax.X - 1f) continue;
                dl.AddLine(new Vector2(x, y1), new Vector2(x, y2), col, thickness);
            }
        }

        /// <summary>Advances to the next row without entering a column.</summary>
        public static void Row()
        {
            ImGui.TableNextRow();
            CaptureScroll();
        }

        /// <summary>Jumps to an explicit column index inside the current row.</summary>
        public static void SetColumn(int index)
        {
            ImGui.TableSetColumnIndex(index);
            CaptureScroll();
        }

        /// <summary>
        /// Advances to the next row, applies themed state tints and enters the
        /// first column. Use for non-interactive rows.
        /// </summary>
        public static void BeginRow(bool selected = false, bool flagged = false,
            Vector4? accent = null)
        {
            ImGui.TableNextRow();
            ApplyRowTint(selected, flagged, accent);
            ImGui.TableNextColumn();
            CaptureScroll();
        }

        /// <summary>
        /// Same as <see cref="BeginRow"/> but renders the row as a clickable
        /// span-all-columns selectable with an invisible label, then paints
        /// <paramref name="label"/> in the first column. Hover/selection render
        /// as an inset rounded chip in the accent color · the same language as
        /// <c>Lists.Item</c> · never a flat edge-to-edge fill. Returns true
        /// when clicked.
        /// </summary>
        public static bool SelectableRow(string id, string label,
            bool selected = false, bool flagged = false, Vector4? accent = null)
        {
            var clicked = SelectableRowCore(id, selected, flagged, accent);
            CellText(label);
            return clicked;
        }

        /// <summary>
        /// Same as <see cref="SelectableRow"/> but leaves the row unlabelled:
        /// the whole row is the click target while the caller paints the cells
        /// via <see cref="PathText"/>/<see cref="CellText"/>/<see cref="NextColumn"/>.
        /// Use for click-to-select rows (single/Ctrl/Shift selection).
        /// </summary>
        public static bool SelectableRowSpan(string id,
            bool selected = false, bool flagged = false, Vector4? accent = null)
            => SelectableRowCore(id, selected, flagged, accent);

        /// <summary>
        /// Row chrome shared by the selectable row helpers: an invisible
        /// span-all-columns hit area followed by the rounded selection chip  · 
        /// drawn BEFORE cell content so text sits on top of the fill.
        /// </summary>
        private static bool SelectableRowCore(string id, bool selected, bool flagged, Vector4? accent)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var acc = accent ?? Colors.Accent;
            var scale = Scale;
            // The invisible selectable occupies the full row; remember the cell
            // start so cell content can be drawn exactly on top of it.
            var cellPos = ImGui.GetCursorScreenPos();
            var scope = ActiveScope;
            var tableMinX = scope?.TableMin.X ?? cellPos.X;
            var tableMaxX = scope?.TableMax.X ?? cellPos.X + ImGui.GetContentRegionAvail().X;
            using var pad = ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
                new Vector2(4f, 1f) * scale);
            using var style = Controls.PushValueControlStyle();
            // Native selectable fills stay transparent · the chip below owns
            // the visual state.
            using var fills = ImRaii.PushColor(ImGuiCol.Header, 0u)
                .Push(ImGuiCol.HeaderHovered, 0u)
                .Push(ImGuiCol.HeaderActive, 0u);
            // Explicit width so the hit area covers the whole row · some
            // SpanAllColumns paths only stretch to the first column.
            var clicked = ImGui.Selectable($"##rowspan_{id}", false,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                new Vector2(MathF.Max(0f, tableMaxX - cellPos.X), 0f));
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();
            if (selected || flagged || hovered || active)
            {
                var rowMin = ImGui.GetItemRectMin();
                var rowMax = ImGui.GetItemRectMax();
                var chipMin = new Vector2(tableMinX, rowMin.Y) + new Vector2(3f, 0.5f) * scale;
                var chipMax = new Vector2(tableMaxX, rowMax.Y) - new Vector2(3f, 0.5f) * scale;
                var radius = Metrics.SwatchRadius(rowMax.Y - rowMin.Y);
                var col = flagged ? Colors.Danger : acc;
                // Cell draw commands are clipped to the column · widen the clip
                // to the table width so the chip can span every column, but keep
                // the current clip's Y extent: that is the scroll viewport, and
                // dropping it lets chips paint under the header / past the frame
                // while scrolling.
                var dl = ImGui.GetWindowDrawList();
                var clipMinY = dl.GetClipRectMin().Y;
                var clipMaxY = dl.GetClipRectMax().Y;
                ImGui.PushClipRect(
                    new Vector2(tableMinX, clipMinY),
                    new Vector2(tableMaxX, clipMaxY), false);
                dl.AddRectFilled(chipMin, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(col,
                        selected || flagged ? 0.13f : active ? 0.16f : 0.06f)), radius);
                dl.AddRect(chipMin, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(col,
                        selected || flagged ? 0.55f : active ? 0.45f : 0.28f)),
                    radius, ImDrawFlags.None, MathF.Max(1f, scale));
                ImGui.PopClipRect();
            }
            // Back to the first column AND the cell's content start · the
            // selectable's ItemSize advanced the cursor, so text would
            // otherwise paint on a phantom second line below the chip. The
            // extra nudge gives the chip real interior padding: its fill
            // starts ~1px off the table edge while cell text keeps the
            // column padding plus this offset, so content never starts
            // outside (or hugging) the chip frame.
            ImGui.TableSetColumnIndex(0);
            ImGui.SetCursorScreenPos(cellPos + new Vector2(5f * scale, 0f));
            Lint.CheckBounds(id);
            CaptureScroll();
            return clicked;
        }

        /// <summary>Moves to the next column · use before custom cell content.</summary>
        public static void NextColumn()
        {
            ImGui.TableNextColumn();
            CaptureScroll();
        }

        /// <summary>
        /// Next column + plain cell text. Text wider than the column is
        /// truncated with an ellipsis and shown in full on hover · never
        /// hard-clipped mid-glyph.
        /// </summary>
        public static void Cell(string text)
        {
            ImGui.TableNextColumn();
            Text.Plain(EllipsizeRight(text, ImGui.GetContentRegionAvail().X, out var cut));
            if (cut) AttachTooltip(text);
            CaptureScroll();
        }

        /// <summary>
        /// Next column + muted cell text for secondary data · same ellipsis
        /// and hover-tooltip guarantee as <see cref="Cell"/>.
        /// </summary>
        public static void MutedCell(string text)
        {
            ImGui.TableNextColumn();
            Text.Muted(EllipsizeRight(text, ImGui.GetContentRegionAvail().X, out var cut));
            if (cut) AttachTooltip(text);
            CaptureScroll();
        }

        /// <summary>
        /// Next column + path text truncated from the LEFT · file paths share
        /// the same leading directories, so the file name is the informative
        /// part. The full path stays available through the tooltip.
        /// </summary>
        public static void PathCell(string text, bool muted = true)
        {
            ImGui.TableNextColumn();
            PathText(text, muted);
        }

        /// <summary>
        /// Path text in the CURRENT column · same left-truncation and tooltip
        /// as <see cref="PathCell"/>, without advancing the column. Use inside
        /// <see cref="SelectableRowSpan"/> rows and other custom layouts.
        /// </summary>
        public static void PathText(string text, bool muted = true)
        {
            var avail = ImGui.GetContentRegionAvail().X;
            var shown = text;
            if (ImGui.CalcTextSize(shown).X > avail)
            {
                while (shown.Length > 4 && ImGui.CalcTextSize("…" + shown).X > avail)
                {
                    shown = shown[1..];
                }
                shown = "…" + shown;
            }
            if (muted)
            {
                Text.Muted(shown);
            }
            else
            {
                Text.Plain(shown);
            }
            if (!ReferenceEquals(shown, text))
            {
                AttachTooltip(text);
            }
            CaptureScroll();
        }

        /// <summary>
        /// Plain text in the CURRENT column with right-ellipsis and tooltip  · 
        /// <see cref="Cell"/> without the column advance, for custom layouts
        /// like <see cref="SelectableRowSpan"/> rows.
        /// </summary>
        public static void CellText(string text, bool muted = false)
        {
            var shown = EllipsizeRight(text, ImGui.GetContentRegionAvail().X, out var cut);
            if (muted)
            {
                Text.Muted(shown);
            }
            else
            {
                Text.Plain(shown);
            }
            if (cut) AttachTooltip(text);
            CaptureScroll();
        }

        private static string EllipsizeRight(string text, float avail, out bool cut)
        {
            cut = false;
            if (ImGui.CalcTextSize(text).X <= avail) return text;
            while (text.Length > 1 && ImGui.CalcTextSize(text + "…").X > avail)
            {
                text = text[..^1];
            }
            cut = true;
            return text + "…";
        }

        /// <summary>
        /// Emits a quiet empty-state note centered across the whole table  · 
        /// a regular cell would clip the text to its column width.
        /// </summary>
        public static void EmptyRow(string message, float height = 0f)
        {
            var scale = Scale;
            var lineH = ImGui.GetTextLineHeight();
            var rowH = height > 0f ? height : lineH + 16f * scale;
            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowH);
            ImGui.TableNextColumn();
            var pos = ImGui.GetCursorScreenPos();
            var scope = ActiveScope;
            var tableMinX = scope?.TableMin.X ?? ImGui.GetWindowPos().X;
            var tableW = scope is { } s && s.TableMax.X > s.TableMin.X
                ? s.TableMax.X - s.TableMin.X
                : ImGui.GetWindowSize().X;
            var textW = ImGui.CalcTextSize(message).X;
            var x = MathF.Max(pos.X, tableMinX + MathF.Max(0f, (tableW - textW) * 0.5f));
            // The cell clip only covers column 0 · widen X to the table while
            // keeping the viewport's Y clip so the note scrolls away correctly.
            var dl = ImGui.GetWindowDrawList();
            ImGui.PushClipRect(
                new Vector2(tableMinX, dl.GetClipRectMin().Y),
                new Vector2(tableMinX + tableW, dl.GetClipRectMax().Y), false);
            dl.AddText(
                new Vector2(x, pos.Y + MathF.Max(0f, (rowH - lineH) * 0.5f)),
                ColorUtils.ToU32(Colors.TextMuted), message);
            ImGui.PopClipRect();
            CaptureScroll();
        }

        /// <summary>
        /// Records the inner scroll child's position while inside a cell so the
        /// scope can paint edge fades over the table body on <see cref="TableScope.Dispose"/>.
        /// </summary>
        private static void CaptureScroll()
        {
            // Only ScrollY tables own an inner scroll window · for static
            // tables the cell cursor reports the PARENT window's scroll, which
            // would draw stray page fades across the table on dispose.
            if (ActiveScope is not { Success: true, WantsScrollY: true } scope) return;
            scope.ScrollY = ImGui.GetScrollY();
            scope.ScrollMaxY = ImGui.GetScrollMaxY();
            scope.InnerTopY = ImGui.GetWindowPos().Y;
        }

        private static void ApplyRowTint(bool selected, bool flagged, Vector4? accent)
        {
            if (!selected && !flagged) return;
            var tint = selected
                ? ColorUtils.WithAlpha(accent ?? Colors.Accent, 0.14f)
                : ColorUtils.WithAlpha(Colors.Danger, 0.10f);
            var col = ColorUtils.ToU32(tint);
            // Paint both parity slots so the tint does not depend on row index.
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, col);
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, col);
        }

    }

    /// <summary>RAII scope returned by <see cref="Tables.Begin"/>.</summary>
    public sealed class TableScope : IDisposable
    {
        internal TableScope(bool success)
        {
            Success = success;
        }

        /// <summary>Whether the underlying ImGui table was successfully begun.</summary>
        public bool Success { get; }

        internal TableScope? Previous { get; set; }
        internal bool WantsScrollY { get; set; }
        internal Vector4 Accent { get; set; }
        internal Vector2 TableMin { get; set; }
        internal Vector2 TableMax { get; set; }
        internal float ScrollY { get; set; }
        internal float ScrollMaxY { get; set; }
        internal float InnerTopY { get; set; }
        internal float HeaderBottomY { get; set; }
        internal bool HeaderSeen { get; set; }
        internal ImDrawListPtr OuterDrawList { get; set; }

        public static implicit operator bool(TableScope scope) => scope.Success;

        public void Dispose()
        {
            if (Success)
            {
                ImGui.EndTable();
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                if (WantsScrollY)
                {
                    var parentMin = ImGui.GetWindowPos();
                    var parentMax = parentMin + ImGui.GetWindowSize();
                    // Fades must start below the frozen header band · prefer the
                    // recorded band edge, fall back to the scroll child's pos.
                    var topInset = HeaderBottomY > 0f
                        ? HeaderBottomY - min.Y
                        : InnerTopY - min.Y;
                    Chrome.ScrollFades(min, max, ScrollY, ScrollMaxY, border: false,
                        MathF.Max(0f, topInset), parentMin, parentMax, null);
                }
                // Card frame · same rounding, thickness and accent border the
                // settings cards use. Drawn last so it layers cleanly over row
                // and cap edges; only data tables (with a header) get framed  · 
                // invisible layout tables stay chrome-free.
                if (HeaderSeen)
                {
                    ImGui.GetWindowDrawList().AddRect(min, max,
                        ColorUtils.ToU32(ColorUtils.WithAlpha(Accent, 0.38f)),
                        Layout.AccentCardRounding * SpheneUI.Scale,
                        ImDrawFlags.RoundCornersAll, Chrome.CardBorderThickness);
                }
            }
            Tables.CloseScope(this);
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(WantsScrollY ? 2 : 1);
        }
    }
}
