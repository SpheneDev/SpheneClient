using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    public static partial class Layout
    {
        /// <summary>
        /// Standard empty state: a large muted glyph over a title and an
        /// optional one-line hint, all centered in the available width, plus
        /// an optional call to action. Replaces bare
        /// <c>Text.Muted("No …")</c> dead ends  ·  every list, tab and panel
        /// that can be empty should explain itself and offer the next step.
        /// </summary>
        /// <param name="icon">Glyph shown above the title.</param>
        /// <param name="title">Short headline, e.g. "No events yet".</param>
        /// <param name="hint">Optional muted explanation / guidance line.</param>
        /// <param name="actionLabel">Optional button label.</param>
        /// <param name="onAction">Invoked when the action button is clicked.</param>
        /// <param name="actionIcon">Optional icon for the action button.</param>
        /// <param name="accent">Optional accent for glyph + action.</param>
        public static void EmptyState(FontAwesomeIcon icon, string title,
            string? hint = null, string? actionLabel = null, Action? onAction = null,
            FontAwesomeIcon? actionIcon = null, Vector4? accent = null)
        {
            var scale = SpheneUI.Scale;
            var availX = ImGui.GetContentRegionAvail().X;
            var tint = accent ?? Colors.Accent;
            var iconSize = 28f * scale;
            var titleH = ImGui.GetTextLineHeight();
            var hintH = string.IsNullOrEmpty(hint)
                ? 0f : ImGui.CalcTextSize(hint, false, MathF.Min(availX, 260f * scale)).Y;
            var gap = 6f * scale;
            var actionH = (actionLabel != null && onAction != null)
                ? ImGui.GetFrameHeight() + gap : 0f;
            var totalH = iconSize + gap + titleH + (hintH > 0 ? gap * 0.5f + hintH : 0f) + actionH;

            VerticalSpace(Spacing.Row);
            var blockMin = ImGui.GetCursorScreenPos();
            var cx = blockMin.X + availX * 0.5f;

            // glyph · muted, faintly tinted so empty panels keep the owner's color
            using (Icons.PushFont())
            {
                var glyph = icon.ToIconString();
                var gs = ImGui.CalcTextSize(glyph);
                var drawList = ImGui.GetWindowDrawList();
                var glyphScale = iconSize / MathF.Max(gs.X, gs.Y);
                var drawSize = gs * glyphScale;
                var glyphPos = new Vector2(cx - drawSize.X * 0.5f, blockMin.Y);
                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize() * glyphScale,
                    glyphPos, ColorUtils.ToU32(ColorUtils.WithAlpha(tint, 0.38f)), glyph);
            }
            ImGui.SetCursorScreenPos(blockMin + new Vector2(0f, iconSize + gap));

            CenteredText(title, Colors.TextSecondary, availX);
            ImGui.SetCursorScreenPos(new Vector2(blockMin.X,
                ImGui.GetCursorScreenPos().Y + (hintH > 0 ? gap * 0.5f : 0f)));

            if (hintH > 0)
            {
                var hintW = MathF.Min(availX, 260f * scale);
                var hintPos = new Vector2(cx - hintW * 0.5f, ImGui.GetCursorScreenPos().Y);
                ImGui.SetCursorScreenPos(hintPos);
                ImGui.PushTextWrapPos(hintPos.X + hintW);
                ColoredCenteredWrapped(hint!, Colors.TextMuted);
                ImGui.PopTextWrapPos();
                ImGui.SetCursorScreenPos(new Vector2(blockMin.X, hintPos.Y + hintH));
            }

            if (actionLabel != null && onAction != null)
            {
                ImGui.SetCursorScreenPos(new Vector2(blockMin.X,
                    blockMin.Y + iconSize + gap + titleH + (hintH > 0 ? gap * 0.5f + hintH : 0f) + gap));
                var bw = Controls.SegAutoWidth(actionLabel, actionIcon, ImGui.GetFrameHeight());
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (availX - bw) * 0.5f));
                if (Controls.SegmentButton(actionLabel, actionIcon, accent: tint))
                {
                    onAction();
                }
            }

            // reserve the block height so following content never overlaps
            var used = ImGui.GetCursorScreenPos().Y - blockMin.Y;
            if (used < totalH)
            {
                ImGui.Dummy(new Vector2(0f, totalH - used));
            }
            VerticalSpace(Spacing.Row);
        }

        private static void CenteredText(string text, Vector4 color, float availX)
        {
            var shown = Text.Ellipsize(text, availX);
            var w = ImGui.CalcTextSize(shown).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (availX - w) * 0.5f));
            Text.Colored(shown, color);
            if (shown.Length != text.Length)
            {
                SpheneUI.AttachTooltip(text);
            }
        }

        private static void ColoredCenteredWrapped(string text, Vector4 color)
        {
            using var pushed = ImRaii.PushColor(ImGuiCol.Text, color);
            var lines = text.Split('\n');
            foreach (var line in lines)
            {
                var w = ImGui.CalcTextSize(line, false, ImGui.GetContentRegionAvail().X).X;
                var avail = ImGui.GetContentRegionAvail().X;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (avail - w) * 0.5f));
                ImGui.TextWrapped(line);
            }
        }
    }
}
