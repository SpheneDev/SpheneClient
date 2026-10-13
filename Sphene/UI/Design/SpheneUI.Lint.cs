using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Runtime design-system checker. When enabled (toggle on the Diagnostics
    /// settings page), shared helpers validate what they just drew against the
    /// design rules; violations are collected per frame and painted as red
    /// outlines (when the item has a rect) plus a textual list over the
    /// offending control so layout regressions surface immediately.
    /// Rules:
    ///  - <c>overhang</c>: item extends past the active card/region bound.
    ///  - <c>off-grid</c>: a spacing/size value not on the Spacing scale.
    ///  - <c>no-theme-scope</c>: a design control drawn without an active
    ///    window/compact theme scope.
    /// </summary>
    public static class Lint
    {
        private const float Tolerance = 0.75f;

        private static readonly List<Violation> _violations = new(64);
        private static uint _markerColor;
        private static uint _markerTextColor;

        /// <summary>Master switch — session only, wired to the Diagnostics page.</summary>
        public static bool Enabled { get; set; }

        /// <summary>Violations recorded during the current frame.</summary>
        public static IReadOnlyList<Violation> Violations => _violations;

        /// <summary>A single design rule violation.</summary>
        public sealed record Violation(string Rule, string Context, Vector2? Min, Vector2? Max, string Detail);

        /// <summary>Clears per-frame state. Call once at the top of a window's draw.</summary>
        public static void BeginFrame()
        {
            _violations.Clear();
        }

        /// <summary>
        /// Validates the most recently drawn item against the active content
        /// bound — flags controls that overshoot the shared right edge.
        /// </summary>
        public static void CheckBounds(string context)
        {
            if (!Enabled)
            {
                return;
            }
            var bound = ContentBounds.CurrentRight;
            if (bound <= 0f)
            {
                return;
            }
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            if (max.X > bound + Tolerance)
            {
                _violations.Add(new Violation("overhang", context, min, max,
                    $"extends {max.X - bound:0.#}px past the bound"));
            }
        }

        /// <summary>
        /// Validates a fill-to-bottom layout right after its last element: the
        /// item's bottom edge should land on the caller's bound. A positive
        /// delta means scroll overflow, a negative one means dead space.
        /// </summary>
        public static void CheckFillBottom(float bound, string context)
        {
            if (!Enabled)
            {
                return;
            }
            var bottom = ImGui.GetItemRectMax().Y;
            var delta = bottom - bound;
            if (MathF.Abs(delta) <= Tolerance)
            {
                return;
            }
            var left = ContentBounds.CurrentLeft > 0f ? ContentBounds.CurrentLeft : ImGui.GetWindowPos().X;
            var right = ContentBounds.CurrentRight > 0f ? ContentBounds.CurrentRight
                : ImGui.GetWindowPos().X + ImGui.GetWindowSize().X;
            _violations.Add(new Violation(
                delta > 0f ? "fill-overflow" : "fill-gap", context,
                new Vector2(left, bound), new Vector2(right, bound + 2f),
                $"content bottom {(delta > 0f ? "overruns" : "falls short of")} the bound by {MathF.Abs(delta):0.#}px"));
        }

        /// <summary>
        /// Validates a caller-supplied spacing/size value against the grid.
        /// </summary>
        public static void CheckSpacing(float scaledPixels, string context)
        {
            if (!Enabled)
            {
                return;
            }
            if (!Space.IsOnGrid(scaledPixels))
            {
                _violations.Add(new Violation("off-grid", context, null, null,
                    $"value {scaledPixels / SpheneUI.Scale:0.##} (unscaled) is not on the spacing grid"));
            }
        }

        /// <summary>Records an off-grid hit without a context item.</summary>
        internal static void RecordOffGrid(float value, string context)
        {
            if (!Enabled)
            {
                return;
            }
            _violations.Add(new Violation("off-grid", context, null, null,
                $"spacing {value:0.##} is not on the grid"));
        }

        /// <summary>
        /// Flags design controls drawn without an active window/compact theme
        /// scope — usually a window bypassing the base-class theme path.
        /// </summary>
        public static void CheckThemeScope(string context)
        {
            if (!Enabled)
            {
                return;
            }
            if (ThemeScopeTracker.Depth == 0)
            {
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                _violations.Add(new Violation("no-theme-scope", context, min, max,
                    "drawn outside a design-system theme scope"));
            }
        }

        /// <summary>
        /// Draws red outlines and rule labels over this frame's violations on
        /// the foreground draw list so they are visible above cards and popups.
        /// </summary>
        public static void DrawOverlay()
        {
            if (!Enabled || _violations.Count == 0)
            {
                return;
            }
            if (_markerColor == 0)
            {
                _markerColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.2f, 0.2f, 1f));
                _markerTextColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.55f, 0.55f, 1f));
            }
            var drawList = ImGui.GetForegroundDrawList();
            var textHeight = ImGui.GetTextLineHeight();
            foreach (var violation in _violations)
            {
                if (violation.Min.HasValue && violation.Max.HasValue)
                {
                    drawList.AddRect(violation.Min.Value, violation.Max.Value,
                        _markerColor, 0f, ImDrawFlags.None, 2f);
                    var labelPos = new Vector2(violation.Min.Value.X,
                        violation.Min.Value.Y - textHeight - 2f);
                    drawList.AddText(labelPos, _markerTextColor,
                        $"{violation.Rule}: {violation.Detail}");
                }
            }
        }
    }

    /// <summary>
    /// Tracks the number of active window-level theme scopes (window + compact).
    /// Lint uses this to flag design controls rendered unthemed.
    /// </summary>
    internal static class ThemeScopeTracker
    {
        private static int _depth;
        private static int _compactDepth;

        public static int Depth => _depth;

        /// <summary>Whether a <c>CompactThemeScope</c> is currently active.</summary>
        public static bool InCompactScope => _compactDepth > 0;

        public static void Push() => _depth++;
        public static void Pop() => _depth--;

        public static void PushCompact() { _depth++; _compactDepth++; }
        public static void PopCompact() { _depth--; _compactDepth--; }
    }
}
