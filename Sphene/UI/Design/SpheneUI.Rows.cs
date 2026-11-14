using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// iOS-style rows for <see cref="Layout.RowGroup"/>: label left,
    /// control/value right on a shared <see cref="Metrics.RowHeight"/> frame,
    /// hairline-separated by the ambient <see cref="GroupState"/>. Every
    /// helper also renders standalone (outside a group) · the separator
    /// logic is then a no-op.
    /// </summary>
    public static class Rows
    {
        /// <summary>Screen X where row content starts (cursor at row start).</summary>
        private static float RowMinX => ImGui.GetCursorScreenPos().X;

        /// <summary>Right edge a row docks its control/value to.</summary>
        private static float RowRightBound(float trailing = 0f)
        {
            var bound = Layout.ContentRightBound;
            if (bound <= 0f)
            {
                bound = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            }
            return bound - trailing;
        }

        private static float RowTopY()
        {
            var pad = MathF.Max(0f, (Metrics.RowHeight - ImGui.GetFrameHeight()) * 0.5f);
            return ImGui.GetCursorScreenPos().Y - pad;
        }

        /// <summary>Muted row label drawn at the current (padded) cursor.</summary>
        private static void Label(string text, FontAwesomeIcon? icon = null)
        {
            var split = text.IndexOf("##", StringComparison.Ordinal);
            var show = split < 0 ? text : text[..split];
            ImGui.AlignTextToFramePadding();
            if (icon.HasValue)
            {
                Icons.Icon(icon.Value, ColorUtils.ToU32(ImGuiColors.DalamudGrey));
                ImGui.SameLine(0f, 6f * SpheneUI.Scale);
            }
            if (show.Length > 0)
            {
                SpheneUI.Text.Colored(show, ImGuiColors.DalamudGrey);
            }
        }

        /// <summary>
        /// Switch row: muted label left, animated slide switch right, optional
        /// status pill between them. The whole row is the hit area.
        /// </summary>
        public static bool Toggle(string label, ref bool value, FontAwesomeIcon? icon = null,
            string? statusPill = null, Vector4? statusColor = null)
        {
            var grouped = GroupState.RowStart();
            var rowTop = GroupState.InGroup ? RowTopY() : ImGui.GetCursorScreenPos().Y;
            var cursor = ImGui.GetCursorScreenPos();
            var frameH = ImGui.GetFrameHeight();
            var scale = SpheneUI.Scale;
            var right = RowRightBound(Metrics.TrailingIconReserve * 0.5f);
            var rowH = GroupState.InGroup ? Metrics.RowHeight : frameH;
            var trackH = MathF.Round(frameH * 0.7f);
            var trackW = trackH * 1.8f;

            var trackMin = new Vector2(right - trackW, rowTop + (rowH - trackH) * 0.5f);
            var drawList = ImGui.GetWindowDrawList();
            ImGui.SetCursorScreenPos(new Vector2(cursor.X, rowTop));
            var changed = ImGui.InvisibleButton(label.Contains("##", StringComparison.Ordinal)
                    ? label : label + "##rows-toggle",
                new Vector2(MathF.Max(60f * scale, right - cursor.X), rowH));
            if (changed)
            {
                value = !value;
            }
            if (ImGui.IsItemHovered())
            {
                var tint = Colors.Accent with { W = 0.07f * ImGui.GetStyle().Alpha };
                drawList.AddRectFilled(new Vector2(cursor.X - 2f * scale, rowTop),
                    new Vector2(right, rowTop + rowH), ColorUtils.ToU32(tint),
                    4f * scale, ImDrawFlags.RoundCornersAll);
            }

            var t = Controls.ToggleAnimValue(ImGui.GetID(label), value);
            Controls.SwitchVisual(drawList, trackMin,
                trackMin + new Vector2(trackW, trackH), t, ImGui.GetStyle().Alpha);

            if (!string.IsNullOrEmpty(statusPill))
            {
                var pillW = Layout.BadgeWidth(statusPill!);
                ImGui.SetCursorScreenPos(new Vector2(
                    trackMin.X - 8f * scale - pillW,
                    rowTop + (rowH - Layout.BadgeHeight) * 0.5f));
                Layout.Badge(statusPill!, statusColor ?? Colors.Accent);
            }

            ImGui.SetCursorScreenPos(new Vector2(cursor.X,
                rowTop + (rowH - frameH) * 0.5f));
            Label(label, icon);
            if (grouped)
            {
                GroupState.RowEnd();
            }
            else
            {
                ImGui.SetCursorScreenPos(new Vector2(cursor.X, rowTop + rowH));
            }
            return changed;
        }

        /// <summary>Same as <see cref="Toggle"/> without the status pill slot.</summary>
        public static bool Switch(string label, ref bool value, FontAwesomeIcon? icon = null)
            => Toggle(label, ref value, icon);

        /// <summary>Segmented row: muted label left, segment bar right.</summary>
        public static bool Segmented(string label, ref int index, string[] items,
            float? barWidth = null)
        {
            var grouped = GroupState.RowStart();
            var cursor = ImGui.GetCursorScreenPos();
            var barW = barWidth ?? Controls.SegmentedWidth(items);
            var right = RowRightBound(Metrics.TrailingIconReserve * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(right - barW, cursor.Y));
            var changed = Controls.Segmented(label + "##rows-seg", ref index, items, barW);
            ImGui.SetCursorScreenPos(cursor);
            Label(label);
            if (grouped)
            {
                GroupState.RowEnd();
            }
            return changed;
        }

        /// <summary>Combo row: muted label left, fixed-width combo right.</summary>
        public static bool Combo(string label, ref int index, string[] items,
            float? width = null)
        {
            var grouped = GroupState.RowStart();
            var cursor = ImGui.GetCursorScreenPos();
            var comboW = width ?? 160f * SpheneUI.Scale;
            var right = RowRightBound(Metrics.TrailingIconReserve * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(right - comboW, cursor.Y));
            var changed = Controls.LabeledCombo("##" + label + "-rows", ref index, items,
                items.Length, preferredWidth: comboW);
            ImGui.SetCursorScreenPos(cursor);
            Label(label);
            if (grouped)
            {
                GroupState.RowEnd();
            }
            return changed;
        }

        /// <summary>Slider row: muted label left, slider right.</summary>
        public static bool SliderInt(string label, ref int value, int min, int max,
            float? width = null)
        {
            var grouped = GroupState.RowStart();
            var cursor = ImGui.GetCursorScreenPos();
            var sliderW = width ?? 160f * SpheneUI.Scale;
            var right = RowRightBound(Metrics.TrailingIconReserve * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(right - sliderW, cursor.Y));
            var changed = Controls.SliderInt("##" + label + "-rows", ref value, min, max,
                preferredWidth: sliderW);
            ImGui.SetCursorScreenPos(cursor);
            Label(label);
            if (grouped)
            {
                GroupState.RowEnd();
            }
            return changed;
        }

        /// <summary>Slider row (float): muted label left, slider right.</summary>
        public static bool SliderFloat(string label, ref float value, float min, float max,
            string? format = null, float? width = null)
        {
            var grouped = GroupState.RowStart();
            var cursor = ImGui.GetCursorScreenPos();
            var sliderW = width ?? 160f * SpheneUI.Scale;
            var right = RowRightBound(Metrics.TrailingIconReserve * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(right - sliderW, cursor.Y));
            var changed = Controls.SliderFloat("##" + label + "-rows", ref value, min, max,
                format, preferredWidth: sliderW);
            ImGui.SetCursorScreenPos(cursor);
            Label(label);
            if (grouped)
            {
                GroupState.RowEnd();
            }
            return changed;
        }

        /// <summary>Text-input row: muted label left, input right.</summary>
        public static bool Input(string label, ref string value, int maxLength,
            string? hint = null, float? width = null)
        {
            var grouped = GroupState.RowStart();
            var cursor = ImGui.GetCursorScreenPos();
            var inputW = width ?? 180f * SpheneUI.Scale;
            var right = RowRightBound(Metrics.TrailingIconReserve * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(right - inputW, cursor.Y));
            var changed = hint != null
                ? Controls.InputTextWithHint("##" + label + "-rows", hint, ref value, maxLength,
                    preferredWidth: inputW)
                : Controls.InputText("##" + label + "-rows", ref value, maxLength,
                    preferredWidth: inputW);
            ImGui.SetCursorScreenPos(cursor);
            Label(label);
            if (grouped)
            {
                GroupState.RowEnd();
            }
            return changed;
        }

        /// <summary>
        /// Navigation row: full-row hit target, muted value and a chevron on
        /// the right · the iOS "tap to open" affordance.
        /// </summary>
        public static bool Nav(string label, string? value, Action onClick,
            FontAwesomeIcon? icon = null)
        {
            var grouped = GroupState.RowStart();
            var rowTop = GroupState.InGroup ? RowTopY() : ImGui.GetCursorScreenPos().Y;
            var cursor = ImGui.GetCursorScreenPos();
            var minX = RowMinX;
            var right = RowRightBound();
            var rowH = GroupState.InGroup ? Metrics.RowHeight : ImGui.GetFrameHeight();

            var drawList = ImGui.GetWindowDrawList();
            ImGui.SetCursorScreenPos(new Vector2(minX, rowTop));
            var clicked = ImGui.InvisibleButton(label + "##rows-nav",
                new Vector2(MathF.Max(60f * SpheneUI.Scale, right - minX), rowH));
            var hovered = ImGui.IsItemHovered();
            if (hovered)
            {
                var tint = Colors.Accent with { W = 0.07f * ImGui.GetStyle().Alpha };
                drawList.AddRectFilled(new Vector2(minX - 2f * SpheneUI.Scale, rowTop),
                    new Vector2(right, rowTop + rowH), ColorUtils.ToU32(tint),
                    4f * SpheneUI.Scale, ImDrawFlags.RoundCornersAll);
            }

            var chevronW = ImGui.GetTextLineHeight();
            ImGui.SetCursorScreenPos(new Vector2(right - chevronW,
                rowTop + (rowH - ImGui.GetTextLineHeight()) * 0.5f));
            Icons.Icon(FontAwesomeIcon.ChevronRight, ColorUtils.ToU32(ImGuiColors.DalamudGrey3));

            if (!string.IsNullOrEmpty(value))
            {
                var valueW = ImGui.CalcTextSize(value).X;
                var valueRight = right - chevronW - 6f * SpheneUI.Scale;
                var valueLeft = MathF.Max(valueRight - valueW,
                    cursor.X + 48f * SpheneUI.Scale);
                ImGui.PushClipRect(new Vector2(valueLeft, rowTop),
                    new Vector2(valueRight, rowTop + rowH), true);
                ImGui.SetCursorScreenPos(new Vector2(
                    valueRight - MathF.Min(valueW, valueRight - valueLeft),
                    rowTop + (rowH - ImGui.GetTextLineHeight()) * 0.5f));
                SpheneUI.Text.Muted(value!);
                ImGui.PopClipRect();
            }

            ImGui.SetCursorScreenPos(new Vector2(cursor.X,
                rowTop + (rowH - ImGui.GetFrameHeight()) * 0.5f));
            Label(label, icon);
            if (clicked)
            {
                onClick();
            }
            if (grouped)
            {
                GroupState.RowEnd();
            }
            else
            {
                ImGui.SetCursorScreenPos(new Vector2(minX, rowTop + rowH));
            }
            return clicked;
        }

        /// <summary>Read-only value row: muted label left, value right.</summary>
        public static void Value(string label, string value, FontAwesomeIcon? icon = null,
            Vector4? valueColor = null)
        {
            var grouped = GroupState.RowStart();
            var cursor = ImGui.GetCursorScreenPos();
            var right = RowRightBound(Metrics.TrailingIconReserve * 0.5f);
            var rowH = GroupState.InGroup ? Metrics.RowHeight : ImGui.GetFrameHeight();
            var rowTop = GroupState.InGroup ? RowTopY() : cursor.Y;
            var valueW = ImGui.CalcTextSize(value).X;
            var valueLeft = MathF.Max(right - valueW, cursor.X + 48f * SpheneUI.Scale);
            ImGui.PushClipRect(new Vector2(valueLeft, rowTop),
                new Vector2(right, rowTop + rowH), true);
            ImGui.SetCursorScreenPos(new Vector2(
                right - MathF.Min(valueW, right - valueLeft), cursor.Y));
            SpheneUI.Text.Colored(value, valueColor ?? ImGuiColors.DalamudGrey3);
            ImGui.PopClipRect();
            ImGui.SetCursorScreenPos(cursor);
            Label(label, icon);
            if (grouped)
            {
                GroupState.RowEnd();
            }
        }

        /// <summary>
        /// Action row: full-row hit target with icon + label (danger variants
        /// tint the label). No chevron · performs, does not navigate.
        /// </summary>
        public static bool Action(string label, FontAwesomeIcon icon, Action onClick,
            bool danger = false)
        {
            var grouped = GroupState.RowStart();
            var rowTop = GroupState.InGroup ? RowTopY() : ImGui.GetCursorScreenPos().Y;
            var minX = RowMinX;
            var right = RowRightBound();
            var rowH = GroupState.InGroup ? Metrics.RowHeight : ImGui.GetFrameHeight();

            var drawList = ImGui.GetWindowDrawList();
            ImGui.SetCursorScreenPos(new Vector2(minX, rowTop));
            var clicked = ImGui.InvisibleButton(label + "##rows-action",
                new Vector2(MathF.Max(60f * SpheneUI.Scale, right - minX), rowH));
            if (ImGui.IsItemHovered())
            {
                var tint = (danger ? SpheneUI.Colors.Danger : Colors.Accent)
                    with { W = 0.08f * ImGui.GetStyle().Alpha };
                drawList.AddRectFilled(new Vector2(minX - 2f * SpheneUI.Scale, rowTop),
                    new Vector2(right, rowTop + rowH), ColorUtils.ToU32(tint),
                    4f * SpheneUI.Scale, ImDrawFlags.RoundCornersAll);
            }

            ImGui.SetCursorScreenPos(new Vector2(minX,
                rowTop + (rowH - ImGui.GetFrameHeight()) * 0.5f));
            var labelColor = danger ? Colors.Danger : ImGuiColors.DalamudGrey;
            Icons.Icon(icon, ColorUtils.ToU32(labelColor));
            ImGui.SameLine(0f, 6f * SpheneUI.Scale);
            SpheneUI.Text.Colored(label, labelColor);
            if (clicked)
            {
                onClick();
            }
            if (grouped)
            {
                GroupState.RowEnd();
            }
            else
            {
                ImGui.SetCursorScreenPos(new Vector2(minX, rowTop + rowH));
            }
            return clicked;
        }

        /// <summary>
        /// Subtext under the row above · muted, wrapped, no separator. Use
        /// for the one-line help a complex option still deserves.
        /// </summary>
        public static void Note(string text)
        {
            SpheneUI.Text.ColoredWrapped(text, ImGuiColors.DalamudGrey3);
        }

        /// <summary>Inline status badge · pill in the accent or given tone.</summary>
        public static void StatusPill(string text, Vector4? color = null)
            => Layout.Badge(text, color ?? Colors.Accent);
    }
}
