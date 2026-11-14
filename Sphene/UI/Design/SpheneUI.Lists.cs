using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Bordered pick lists: a slim titled box with scroll fades and padded rows
    /// in the shared selection language · the current row reads as a framed
    /// button chip (rounded accent outline + soft fill), never an edge-to-edge
    /// block. Replaces raw ImGui.ListBox; the box stays bounded to its own
    /// width so it composes inside SameLine groups and table cells.
    /// </summary>
    public static class Lists
    {
        /// <summary>
        /// Opens a pick list box. <paramref name="title"/> draws a muted label
        /// above the box. <paramref name="width"/> defaults to the remaining
        /// cell/region width; <paramref name="height"/> defaults to
        /// <see cref="Layout.FillToViewBottomHeight"/>. <paramref name="border"/>
        /// draws the framed box · false keeps a slim padded scroll region for
        /// rails that should float on the page. Emit rows with
        /// <see cref="Item"/> or <see cref="EmptyHint"/>.
        /// </summary>
        public static ListScope Begin(string id, float width = -1f, float height = -1f,
            string? title = null, bool border = true)
        {
            if (!string.IsNullOrEmpty(title))
            {
                Text.Muted(title);
                Layout.VerticalSpace(Spacing.X4);
            }
            var h = height >= 0f ? height : Layout.FillToViewBottomHeight();
            var begun = Layout.BeginScrollArea(id, new Vector2(width, h), border: border,
                contentPadding: (border ? new Vector2(4f, 3f) : new Vector2(2f, 1f)) * Scale);
            return new ListScope(begun);
        }

        /// <summary>
        /// Padded full-width row. Returns true when clicked. Hover and
        /// selection render as an inset rounded frame in the accent color  · 
        /// button-like, not an edge-to-edge fill. Overlong labels ellipsize and
        /// keep the full text in a tooltip; <paramref name="icon"/> draws a
        /// glyph before the label, <paramref name="tooltip"/> overrides the
        /// hover text.
        /// </summary>
        public static bool Item(string id, string label, bool selected = false,
            Vector4? accent = null, bool muted = false,
            FontAwesomeIcon? icon = null, string? tooltip = null, uint? gameIcon = null)
        {
            var acc = accent ?? Colors.Accent;
            var scale = Scale;
            var padX = 8f * scale;
            var lineH = ImGui.GetTextLineHeight();
            var rowH = lineH + 6f * scale;
            var min = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var max = min + new Vector2(width, rowH);

            var clicked = ImGui.InvisibleButton($"##listrow_{id}", new Vector2(width, rowH));
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();
            var drawList = ImGui.GetWindowDrawList();
            var radius = Metrics.SwatchRadius(rowH);
            var chipMin = min + new Vector2(2f, 0.5f) * scale;
            var chipMax = max - new Vector2(2f, 0.5f) * scale;
            if (selected || hovered || active)
            {
                var fill = selected ? 0.13f : active ? 0.16f : 0.06f;
                var edge = selected ? 0.55f : active ? 0.45f : 0.28f;
                drawList.AddRectFilled(chipMin, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(acc, fill)), radius);
                drawList.AddRect(chipMin, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(acc, edge)), radius, ImDrawFlags.None, 1f * scale);
            }

            var textX = padX;
            if (gameIcon.HasValue)
            {
                var iconSize = rowH - 4f * scale;
                var iconMin = min + new Vector2(padX - 1f * scale, (rowH - iconSize) * 0.5f);
                if (Media.GameIcon(gameIcon.Value) is { } wrap)
                {
                    drawList.AddImage(wrap.Handle, iconMin, iconMin + new Vector2(iconSize));
                }
                textX += iconSize + 4f * scale;
            }
            else if (icon.HasValue)
            {
                using (SpheneUI.IconFont.Push())
                {
                    drawList.AddText(min + new Vector2(padX, (rowH - lineH) * 0.5f),
                        ColorUtils.ToU32(selected ? acc : Colors.TextMuted),
                        icon.Value.ToIconString());
                }
                textX += ImGui.GetTextLineHeight() + 6f * scale;
            }

            var text = label;
            var textAvail = width - textX - padX;
            if (ImGui.CalcTextSize(text).X > textAvail)
            {
                while (text.Length > 1 && ImGui.CalcTextSize(text + "…").X > textAvail)
                {
                    text = text[..^1];
                }
                text += "…";
            }
            drawList.AddText(min + new Vector2(textX, (rowH - lineH) * 0.5f),
                ColorUtils.ToU32(muted ? Colors.TextMuted : Colors.TextPrimary), text);
            var tip = tooltip ?? (ReferenceEquals(text, label) ? null : label);
            if (!string.IsNullOrEmpty(tip))
            {
                AttachTooltip(tip);
            }
            Lint.CheckBounds(id);
            return clicked;
        }

        /// <summary>Muted placeholder row for empty lists.</summary>
        public static void EmptyHint(string text)
        {
            Layout.VerticalSpace(Spacing.X4);
            ImGui.Indent(8f * Scale);
            Text.Muted(text);
            ImGui.Unindent(8f * Scale);
        }
    }

    /// <summary>RAII scope returned by <see cref="Lists.Begin"/>.</summary>
    public sealed class ListScope : IDisposable
    {
        internal ListScope(bool success)
        {
            Success = success;
        }

        /// <summary>Whether the inner scroll child accepted content this frame.</summary>
        public bool Success { get; }

        public static implicit operator bool(ListScope scope) => scope.Success;

        public void Dispose() => Layout.EndScrollArea();
    }
}
