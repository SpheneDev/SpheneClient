using System;
using System.Numerics;
using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Every text primitive of the design system. All helpers render through
    /// <see cref="ImGui.TextUnformatted"/> so literal text never collides with
    /// ImGui formatting, and all colors resolve through theme/palette tokens.
    /// </summary>
    public static class Text
    {
        /// <summary>Plain text in the themed text color · replaces raw ImGui.Text/TextUnformatted.</summary>
        public static void Plain(string text) => ImGui.TextUnformatted(text);

        /// <summary>Wrapped text in the themed text color.</summary>
        public static void Wrapped(string text, float wrapPos = 0)
        {
            ImGui.PushTextWrapPos(wrapPos);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }

        /// <summary>
        /// Truncates <paramref name="text"/> to <paramref name="maxWidth"/>
        /// pixels with an ellipsis · first line only, binary-search measured.
        /// </summary>
        public static string Ellipsize(string text, float maxWidth)
        {
            if (maxWidth <= 0f || ImGui.CalcTextSize(text).X <= maxWidth)
            {
                return text;
            }
            var newline = text.IndexOf('\n', StringComparison.Ordinal);
            var line = newline >= 0 ? text[..newline] : text;
            if (line.Length > 256)
            {
                line = line[..256];
            }
            var ellipsisW = ImGui.CalcTextSize("…").X;
            var lo = 0;
            var hi = line.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (ImGui.CalcTextSize(line[..mid]).X + ellipsisW <= maxWidth)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return lo <= 0 ? "…" : line[..lo].TrimEnd() + "…";
        }

        /// <summary>Text in an explicit color; null keeps the themed text color.</summary>
        public static void Colored(string text, Vector4? color)
        {
            if (color.HasValue)
            {
                using var pushed = ImRaii.PushColor(ImGuiCol.Text, color.Value);
                ImGui.TextUnformatted(text);
            }
            else
            {
                ImGui.TextUnformatted(text);
            }
        }

        /// <summary>Colored wrapped text.</summary>
        public static void ColoredWrapped(string text, Vector4 color, float wrapPos = 0)
        {
            using var pushed = ImRaii.PushColor(ImGuiCol.Text, color);
            Wrapped(text, wrapPos);
        }

        /// <summary>Bullet-prefixed text line · replaces raw ImGui.BulletText.</summary>
        public static void Bullet(string text) => ImGui.BulletText(text);

        /// <summary>Secondary-tone wrapped text · the standard description style.</summary>
        public static void Muted(string text) => Colored(text, Colors.TextMuted);

        /// <summary>Secondary-tone wrapped text · the standard description style.</summary>
        public static void MutedWrapped(string text, float wrapPos = 0)
            => ColoredWrapped(text, Colors.TextMuted, wrapPos);

        /// <summary>
        /// Meta text for feed rows and card footers (timestamps, counts,
        /// separators)  ·  a touch dimmer than <see cref="Muted"/> so it never
        /// competes with the label above it.
        /// </summary>
        /// <summary>Word-wraps <paramref name="text"/> into at most
        /// <paramref name="maxLines"/> lines at <paramref name="maxWidth"/>;
        /// the last line is ellipsized when it still overflows.</summary>
        public static List<string> WrapLines(string text, float maxWidth,
            int maxLines)
        {
            var lines = new List<string>();
            var remaining = text.Trim();
            while (remaining.Length > 0 && lines.Count < maxLines)
            {
                if (ImGui.CalcTextSize(remaining).X <= maxWidth)
                {
                    lines.Add(remaining);
                    break;
                }
                var cut = 0;
                var sp = -1;
                for (var i = 0; i < remaining.Length; i++)
                {
                    if (remaining[i] == ' ') sp = i;
                    if (ImGui.CalcTextSize(remaining[..i]).X > maxWidth) break;
                    cut = i;
                }
                if (sp > 0 && sp < cut) cut = sp;
                if (lines.Count == maxLines - 1)
                {
                    lines.Add(Ellipsize(remaining, maxWidth));
                    break;
                }
                lines.Add(remaining[..cut].TrimEnd());
                remaining = remaining[cut..].TrimStart();
            }
            return lines;
        }

        /// <summary>Canonical date format for user-facing text:
        /// "Jun 12, 2026" · ISO strings pass through when unparseable.</summary>
        public static string FormatDate(string date)
            => DateTime.TryParse(date,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d)
                ? d.ToString("MMM d, yyyy",
                    System.Globalization.CultureInfo.InvariantCulture)
                : date;

        /// <summary>"Jun 12, 2026"</summary>
        public static string FormatDate(DateTime date)
            => date.ToString("MMM d, yyyy",
                System.Globalization.CultureInfo.InvariantCulture);

        public static string FormatDate(DateTimeOffset date)
            => FormatDate(date.DateTime);

        /// <summary>"Jun 12, 2026 14:30"</summary>
        public static string FormatDateTime(DateTime date, bool includeSeconds = false)
            => date.ToString(
                includeSeconds ? "MMM d, yyyy HH:mm:ss" : "MMM d, yyyy HH:mm",
                System.Globalization.CultureInfo.InvariantCulture);

        public static string FormatDateTime(DateTimeOffset date,
                bool includeSeconds = false)
            => FormatDateTime(date.DateTime, includeSeconds);

        /// <summary>"Jun 12" · short form for dense lists.</summary>
        public static string FormatShortDate(DateTime date)
            => date.ToString("MMM d",
                System.Globalization.CultureInfo.InvariantCulture);

        public static string FormatShortDate(DateTimeOffset date)
            => FormatShortDate(date.DateTime);

        public static void Timestamp(string text)
            => Colored(text, ColorUtils.WithAlpha(Colors.TextMuted,
                Colors.TextMuted.W * 0.82f));

        /// <summary>Right-aligned variant of <see cref="Timestamp"/> for
        /// meta text docked to the row's end.</summary>
        public static void TimestampRight(string text)
        {
            var w = ImGui.CalcTextSize(text).X;
            var avail = ImGui.GetContentRegionAvail().X;
            if (w < avail)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - w);
            }
            Timestamp(text);
        }

        /// <summary>
        /// One-line muted page blurb · ellipsized to the content width, the
        /// full text rides the hover tooltip. The standard description line
        /// on pages whose title already lives in the breadcrumb.
        /// </summary>
        public static void PageIntro(string text)
        {
            var shown = Ellipsize(text, ImGui.GetContentRegionAvail().X);
            Muted(shown);
            if (shown.Length != text.Length)
            {
                SpheneUI.AttachTooltip(text);
            }
        }

        /// <summary>
        /// Large display text in the shared UI font · renders in the display
        /// font (~26px) under <see cref="DensityProfile.Compact"/> so headings
        /// stay proportional on small windows instead of eating two rows.
        /// </summary>
        public static void Big(string text, Vector4? color = null)
        {
            if ((Density.IsCompact ? _displayFont : _uidFont) is { } handle)
            {
                Font(handle, text, color);
            }
            else
            {
                Colored(text, color);
            }
        }

        /// <summary>
        /// Display-size heading text in the dedicated display font (~26px)  · 
        /// the crisp way to render large text; never use
        /// <c>SetWindowFontScale</c> for headings, it raster-scales glyphs.
        /// </summary>
        public static void Display(string text, Vector4? color = null)
        {
            if (SpheneUI.DisplayFont is { } font)
            {
                Font(font, text, color);
            }
            else
            {
                Colored(text, color);
            }
        }

        /// <summary>Text rendered in one of the shared font handles.</summary>
        internal static void Font(IFontHandle handle, string text, Vector4? color = null)
            => Font(handle, text, color.HasValue ? ColorUtils.ToU32(color.Value) : ImGui.GetColorU32(ImGuiCol.Text));

        internal static void Font(IFontHandle handle, string text, uint color)
        {
            using var pushedFont = handle.Push();
            using var pushedColor = ImRaii.PushColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted(text);
        }

        /// <summary>
        /// Text with a same-size outline, drawn via 8 offset passes. Used where
        /// text must stay readable over arbitrary imagery.
        /// </summary>
        public static void Outlined(string text, Vector4 fontColor, Vector4 outlineColor, int thickness = 1)
        {
            var original = ImGui.GetCursorPos();
            using (ImRaii.PushColor(ImGuiCol.Text, outlineColor))
            {
                ImGui.SetCursorPos(original with { Y = original.Y - thickness });
                ImGui.TextUnformatted(text);
                ImGui.SetCursorPos(original with { X = original.X - thickness });
                ImGui.TextUnformatted(text);
                ImGui.SetCursorPos(original with { Y = original.Y + thickness });
                ImGui.TextUnformatted(text);
                ImGui.SetCursorPos(original with { X = original.X + thickness });
                ImGui.TextUnformatted(text);
                ImGui.SetCursorPos(original with { X = original.X - thickness, Y = original.Y - thickness });
                ImGui.TextUnformatted(text);
                ImGui.SetCursorPos(original with { X = original.X + thickness, Y = original.Y + thickness });
                ImGui.TextUnformatted(text);
                ImGui.SetCursorPos(original with { X = original.X - thickness, Y = original.Y + thickness });
                ImGui.TextUnformatted(text);
                ImGui.SetCursorPos(original with { X = original.X + thickness, Y = original.Y - thickness });
                ImGui.TextUnformatted(text);
            }
            using (ImRaii.PushColor(ImGuiCol.Text, fontColor))
            {
                ImGui.SetCursorPos(original);
                ImGui.TextUnformatted(text);
                ImGui.SetCursorPos(original);
                ImGui.TextUnformatted(text);
            }
        }

        /// <summary>Outlined text at the current cursor · alias of <see cref="Outlined"/>.</summary>
        public static void OutlinedOn(string text, Vector4 fontColor, Vector4 outlineColor,
            int thickness = 1)
            => Outlined(text, fontColor, outlineColor, thickness);

        /// <summary>Outlined text painted directly into a draw list.</summary>
        public static void OutlinedOn(ImDrawListPtr drawList, string text, Vector2 textPos,
            uint fontColor, uint outlineColor, int thickness = 1)
        {
            drawList.AddText(textPos with { Y = textPos.Y - thickness }, outlineColor, text);
            drawList.AddText(textPos with { X = textPos.X - thickness }, outlineColor, text);
            drawList.AddText(textPos with { Y = textPos.Y + thickness }, outlineColor, text);
            drawList.AddText(textPos with { X = textPos.X + thickness }, outlineColor, text);
            drawList.AddText(new Vector2(textPos.X - thickness, textPos.Y - thickness), outlineColor, text);
            drawList.AddText(new Vector2(textPos.X + thickness, textPos.Y + thickness), outlineColor, text);
            drawList.AddText(new Vector2(textPos.X - thickness, textPos.Y + thickness), outlineColor, text);
            drawList.AddText(new Vector2(textPos.X + thickness, textPos.Y - thickness), outlineColor, text);
            drawList.AddText(textPos, fontColor, text);
            drawList.AddText(textPos, fontColor, text);
        }

        /// <summary>
        /// Page-level header: bold accent title in the display font plus an
        /// optional muted description. Use once at the top of a page.
        /// </summary>
        public static void PageHeader(string title, string? description = null)
        {
            Big(title, Colors.TextPrimary);
            if (!string.IsNullOrWhiteSpace(description))
            {
                MutedWrapped(description);
            }
            Layout.VerticalSpace(Spacing.Section);
        }

        /// <summary>
        /// Section-level header inside a page or card: accent title plus an
        /// optional muted description and the standard section gap.
        /// </summary>
        public static void SectionHeader(string title, string? description = null,
            FontAwesomeIcon? icon = null, Vector4? accent = null)
        {
            var tone = accent ?? Palette.CrystalBlue;
            if (icon.HasValue)
            {
                Icons.Icon(icon.Value, tone);
                ImGui.SameLine();
            }
            Colored(title, tone);
            if (!string.IsNullOrWhiteSpace(description))
            {
                MutedWrapped(description);
            }
            Layout.VerticalSpace(Spacing.X8);
        }

        /// <summary>
        /// Accent header in the mono font with the double crystal underline  · 
        /// port of the former <c>DrawSpheneHeader</c> styling primitive.
        /// </summary>
        public static void Header(string text, bool underline = true)
        {
            using var font = ImRaii.PushFont(UiBuilder.MonoFont);
            using var color = ImRaii.PushColor(ImGuiCol.Text, ColorUtils.ToU32(Palette.SpheneGold));
            ImGui.TextUnformatted(text);
            if (underline)
            {
                var drawList = ImGui.GetWindowDrawList();
                var pos = ImGui.GetCursorScreenPos();
                var textWidth = ImGui.CalcTextSize(text).X;
                var startPos = new Vector2(pos.X, pos.Y - 2);
                var endPos = new Vector2(pos.X + textWidth, pos.Y - 2);
                drawList.AddLine(startPos, endPos, ColorUtils.ToU32(Palette.CrystalBlue), 2.0f);
                drawList.AddLine(new Vector2(startPos.X, startPos.Y + 1),
                    new Vector2(endPos.X, endPos.Y + 1),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Palette.EtherealPurple, 0.6f)), 1.0f);
            }
        }

        /// <summary>Centered, optionally width-capped colored text inside a drawn group frame.</summary>
        public static void GroupedCentered(string text, Vector4 color, float? maxWidth = null)
        {
            var availWidth = ImGui.GetContentRegionAvail().X;
            var textWidth = ImGui.CalcTextSize(text, wrapWidth: availWidth).X;
            if (maxWidth != null && textWidth > maxWidth * Scale)
            {
                textWidth = maxWidth.Value * Scale;
            }
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth / 2f) - (textWidth / 2f));
            Layout.Group(() =>
            {
                ColoredWrapped(text, color, ImGui.GetCursorPosX() + textWidth);
            }, expectedWidth: maxWidth == null ? null : maxWidth * Scale);
        }
    }

    /// <summary>
    /// Themed tooltip on the last item. Honors <c>--SEP--</c> segment
    /// separators and wraps at a readable width. Replaces raw
    /// ImGui tooltip sequences for design-system windows.
    /// </summary>
    public static void AttachTooltip(string text)
    {
        AttachTooltip(text, ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled));
    }

    /// <summary>
    /// Same themed tooltip as <see cref="AttachTooltip(string)"/> but driven by
    /// an explicit hover state · for drawlist-only hit zones that submit no
    /// ImGui item (rect-hovered chips, overlay controls).
    /// </summary>
    public static void AttachTooltip(string text, bool hovered)
    {
        const string separator = "--SEP--";
        if (!hovered)
        {
            return;
        }
        using var scope = Theme.TooltipScope();
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
        if (text.IndexOf(separator, StringComparison.Ordinal) >= 0)
        {
            var segments = _tooltipSegmentsCache.GetOrAdd(text,
                t => t.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            for (var i = 0; i < segments.Length; i++)
            {
                ImGui.TextUnformatted(segments[i]);
                if (i != segments.Length - 1)
                {
                    ImGui.Separator();
                }
            }
        }
        else
        {
            ImGui.TextUnformatted(text);
        }
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>
    /// Same themed tooltip as <see cref="AttachTooltip(string,bool)"/> but
    /// painted on the foreground drawlist - for overlays that already float
    /// above ImGui tooltip windows (e.g. the unsaved-changes toast). Style
    /// tokens and segment handling stay identical; only the layer differs.
    /// </summary>
    public static void AttachTooltipForeground(string text, bool hovered)
    {
        const string separator = "--SEP--";
        if (!hovered)
        {
            return;
        }
        var scale = SpheneUI.Scale;
        var theme = SpheneTheme.Current;
        var dl = ImGui.GetForegroundDrawList();
        var wrapW = ImGui.GetFontSize() * 35f;
        var pad = theme.WindowPadding;
        var rounding = theme.CompactTooltipRounding;
        var segments = _tooltipSegmentsCache.GetOrAdd(text,
            t => t.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        float w = 0, h = 0;
        foreach (var seg in segments)
        {
            var s = ImGui.CalcTextSize(seg, false, wrapW);
            w = MathF.Max(w, s.X);
            h += s.Y;
        }
        var box = new Vector2(w, h) + pad * 2f + new Vector2(0, (segments.Length - 1) * 8f * scale);
        var mouse = ImGui.GetMousePos();
        var pos = mouse + new Vector2(14f, 16f) * scale;
        var bounds = ImGui.GetIO().DisplaySize;
        if (pos.X + box.X > bounds.X - 4f) pos.X = mouse.X - box.X - 14f * scale;
        if (pos.Y + box.Y > bounds.Y - 4f) pos.Y = mouse.Y - box.Y - 16f * scale;
        dl.AddRectFilled(pos, pos + box, ImGui.GetColorU32(ImGuiCol.PopupBg), rounding);
        if (theme.CompactTooltipBorderSize > 0)
        {
            dl.AddRect(pos, pos + box, ImGui.GetColorU32(ImGuiCol.Border),
                rounding, ImDrawFlags.None, theme.CompactTooltipBorderSize);
        }
        var ty = pos.Y + pad.Y;
        var textCol = ImGui.GetColorU32(ImGuiCol.Text);
        for (var i = 0; i < segments.Length; i++)
        {
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(),
                new Vector2(pos.X + pad.X, ty), textCol, segments[i], wrapW);
            ty += ImGui.CalcTextSize(segments[i], false, wrapW).Y;
            if (i != segments.Length - 1)
            {
                ty += 4f * scale;
                dl.AddLine(new Vector2(pos.X + pad.X, ty), new Vector2(pos.X + box.X - pad.X, ty),
                    ImGui.GetColorU32(ImGuiCol.Separator));
                ty += 4f * scale;
            }
        }
    }

    private static readonly ConcurrentDictionary<string, string[]> _tooltipSegmentsCache = new(StringComparer.Ordinal);
}
