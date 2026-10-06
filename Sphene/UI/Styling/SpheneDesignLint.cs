using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Sphene.UI.Styling;

/// <summary>
/// Runtime design-system checker. When enabled (toggle on the Diagnostics
/// settings page), shared helpers validate the item they just drew against
/// the active card bounds; violations are collected per frame and painted as
/// red outlines over the offending control so layout regressions surface
/// immediately instead of after a screenshot roundtrip.
/// </summary>
public static class SpheneDesignLint
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
    public sealed record Violation(string Rule, string Context, Vector2 Min, Vector2 Max, string Detail);

    /// <summary>Clears per-frame state. Call once at the top of a window's draw.</summary>
    public static void BeginFrame()
    {
        _violations.Clear();
    }

    /// <summary>
    /// Validates the most recently drawn item against the active card bound —
    /// flags controls that overshoot the shared right edge, the classic
    /// "control clipped at the card border" regression.
    /// </summary>
    public static void CheckItemWithinBound(string context)
    {
        if (!Enabled)
        {
            return;
        }
        var bound = SpheneWindowChrome.CurrentCardContentRightBound;
        if (bound <= 0f)
        {
            return;
        }
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (max.X > bound + Tolerance)
        {
            _violations.Add(new Violation("overhang", context, min, max,
                $"extends {max.X - bound:0.#}px past the card edge"));
        }
    }

    /// <summary>
    /// Draws red outlines and rule labels over this frame's violations on the
    /// foreground draw list so they are visible above cards and popups.
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
            drawList.AddRect(violation.Min, violation.Max, _markerColor, 0f, ImDrawFlags.None, 2f);
            var labelPos = new Vector2(violation.Min.X, violation.Min.Y - textHeight - 2f);
            drawList.AddText(labelPos, _markerTextColor, $"{violation.Rule}: {violation.Detail}");
        }
    }
}
