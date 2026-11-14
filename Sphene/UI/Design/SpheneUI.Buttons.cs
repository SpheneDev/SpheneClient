using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Action rows that never clip: <see cref="Row"/> lays out icon+label
    /// buttons left-to-right and keeps the whole row inside the bounded width  · 
    /// when space runs out the rightmost buttons degrade to icon-only (their
    /// label moves into the tooltip), and whatever still does not fit wraps to
    /// a new line instead of being cut off.
    /// </summary>
    public static class Buttons
    {
        /// <summary>One action in a <see cref="Row"/>.</summary>
        /// <param name="Icon">Glyph shown on the button.</param>
        /// <param name="Label">Visible label · keep it short; long descriptions
        /// belong in <paramref name="Tooltip"/>.</param>
        /// <param name="Tooltip">Hover explanation, may be null.</param>
        /// <param name="OnClick">Invoked on click.</param>
        /// <param name="Enabled">Greys out when false.</param>
        /// <param name="Danger">Tints the button in the danger color for
        /// destructive actions.</param>
        public readonly record struct Spec(
            FontAwesomeIcon Icon,
            string Label,
            string? Tooltip,
            Action OnClick,
            bool Enabled = true,
            bool Danger = false);

        /// <summary>
        /// Renders <paramref name="specs"/> as a left-aligned toolbar row.
        /// Rightmost buttons are converted to icon-only until the row fits the
        /// bounded width; if even icon-only would overflow, the row wraps.
        /// </summary>
        public static void Row(string id, params Spec[] specs)
        {
            if (specs.Length == 0) return;
            var avail = Controls.BoundedAvailX();
            var gap = Metrics.Gap;
            var rowH = ImGui.GetFrameHeight();
            var count = specs.Length;
            Span<float> fullW = stackalloc float[count];
            Span<float> iconW = stackalloc float[count];
            Span<bool> iconOnly = stackalloc bool[count];
            var need = 0f;
            for (var i = 0; i < count; i++)
            {
                fullW[i] = Icons.TextButtonWidth(specs[i].Icon, specs[i].Label);
                iconW[i] = MathF.Max(Icons.ButtonSize(specs[i].Icon).X, rowH);
                need += (i == 0 ? 0f : gap) + fullW[i];
            }
            if (need > avail)
            {
                // Shrink right-to-left so the primary (leftmost) action keeps
                // its label as long as possible.
                for (var i = count - 1; i >= 0 && need > avail; i--)
                {
                    iconOnly[i] = true;
                    need -= fullW[i] - iconW[i];
                }
            }

            var right = ImGui.GetCursorScreenPos().X + avail;
            for (var i = 0; i < count; i++)
            {
                var spec = specs[i];
                var w = iconOnly[i] ? iconW[i] : fullW[i];
                // Stay on the line only while the button still fits inside the
                // bound · otherwise let it flow to a fresh line.
                if (i > 0 && ImGui.GetItemRectMax().X + gap + w <= right + 0.5f)
                {
                    ImGui.SameLine(0f, gap);
                }
                var label = iconOnly[i] ? null : spec.Label;
                if (Controls.SegCore($"{spec.Label}##{id}fit{i}", label, spec.Icon,
                    new Vector2(w, rowH), spec.Danger ? Colors.Danger : Colors.Accent,
                    enabled: spec.Enabled))
                {
                    spec.OnClick();
                }
                var tip = iconOnly[i] && !string.IsNullOrEmpty(spec.Tooltip)
                    ? spec.Label + TooltipSeparator + spec.Tooltip
                    : (iconOnly[i] ? spec.Label : spec.Tooltip);
                if (!string.IsNullOrEmpty(tip))
                {
                    AttachTooltip(tip);
                }
            }
        }
    }
}
