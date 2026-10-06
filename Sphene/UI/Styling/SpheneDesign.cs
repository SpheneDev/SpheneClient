using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Sphene.UI.Theme;
using System.Numerics;

namespace Sphene.UI.Styling;

/// <summary>
/// Sphene design tokens — the single source of truth for geometry, corner
/// radii, reserved space and semantic colors used by the shared UI helpers.
/// Helpers read these instead of hardcoding numbers or trusting ImGui's
/// global style (which the plugin never initializes), so a design tweak here
/// changes every control at once. Theme-facing values fall through to
/// <see cref="ThemeConfiguration"/> so user-configured values keep applying.
/// </summary>
public static class SpheneDesign
{
    // ------------------------------------------------------------------
    // Semantic colors — resolved against the active theme, never raw style.
    // ------------------------------------------------------------------

    /// <summary>Primary accent (links, active states, selected marks).</summary>
    public static Vector4 Accent => SpheneCustomTheme.Colors.AccentBlue;

    /// <summary>Standard readable text.</summary>
    public static Vector4 TextPrimary => SpheneCustomTheme.CurrentTheme.TextPrimary;

    /// <summary>Secondary/hint text.</summary>
    public static Vector4 TextSecondary => SpheneCustomTheme.CurrentTheme.TextSecondary;

    /// <summary>Muted text (disabled, meta info, help icons).</summary>
    public static Vector4 TextMuted => SpheneCustomTheme.CurrentTheme.TextDisabled;

    /// <summary>Frame fill of resting value controls.</summary>
    public static Vector4 FrameBg => SpheneCustomTheme.CurrentTheme.FrameBg;

    /// <summary>Frame fill while hovered.</summary>
    public static Vector4 FrameBgHover => SpheneCustomTheme.CurrentTheme.FrameBgHovered;

    /// <summary>Frame fill while pressed/active.</summary>
    public static Vector4 FrameBgActive => SpheneCustomTheme.CurrentTheme.FrameBgActive;

    /// <summary>Resting border of controls and cards.</summary>
    public static Vector4 Border => SpheneCustomTheme.CurrentTheme.Border;

    /// <summary>Popup surface fill.</summary>
    public static Vector4 Surface => SpheneCustomTheme.CurrentTheme.PopupBg;

    /// <summary>Button fill at rest.</summary>
    public static Vector4 ButtonBg => SpheneCustomTheme.CurrentTheme.Button;

    /// <summary>Button fill while hovered.</summary>
    public static Vector4 ButtonBgHover => SpheneCustomTheme.CurrentTheme.ButtonHovered;

    /// <summary>Button fill while pressed.</summary>
    public static Vector4 ButtonBgActive => SpheneCustomTheme.CurrentTheme.ButtonActive;

    /// <summary>Destructive/danger accent.</summary>
    public static Vector4 Danger => ImGuiColors.DalamudRed;

    /// <summary>Warning accent.</summary>
    public static Vector4 Warning => ImGuiColors.DalamudYellow;

    /// <summary>Success/ok accent.</summary>
    public static Vector4 Success => ImGuiColors.HealerGreen;

    // ------------------------------------------------------------------
    // Value controls
    // ------------------------------------------------------------------

    /// <summary>Standard height of value controls (sliders, inputs, swatches).</summary>
    public static float ControlHeight => ImGui.GetFrameHeight();

    /// <summary>Corner radius of framed value controls.</summary>
    public static float ControlRadius => ImGui.GetFrameHeight() * ControlRadiusFactor;

    /// <summary>Frame corner radius relative to the control height.</summary>
    public const float ControlRadiusFactor = 0.35f;

    /// <summary>Grab/knob minimum size relative to the control height.</summary>
    public const float GrabMinSizeFactor = 0.5f;

    /// <summary>Border thickness drawn around value-control frames.</summary>
    public const float ControlBorderSize = 1f;

    /// <summary>Square side length of swatch buttons (color/glyph pickers).</summary>
    public static float SwatchSize => ImGui.GetFrameHeight();

    /// <summary>Swatch corner radius for a given side length.</summary>
    public static float SwatchRadius(float size)
        => MathF.Min(size * SwatchRadiusFactor, SwatchMaxRadius * ImGuiHelpers.GlobalScale);

    /// <summary>Swatch corner radius relative to the swatch size.</summary>
    public const float SwatchRadiusFactor = 0.28f;

    /// <summary>Absolute cap for the swatch corner radius, before UI scale.</summary>
    public const float SwatchMaxRadius = 5f;

    /// <summary>Swatch border alpha in the resting state.</summary>
    public const float SwatchBorderAlpha = 0.18f;

    /// <summary>Swatch border alpha while hovered.</summary>
    public const float SwatchBorderAlphaHover = 0.55f;

    /// <summary>Swatch border alpha for the currently selected entry.</summary>
    public const float SwatchBorderAlphaSelected = 0.95f;

    /// <summary>Border thickness of the selected swatch, before UI scale.</summary>
    public const float SwatchSelectedThickness = 2f;

    /// <summary>Multiply color channels by this when a color swatch is hovered.</summary>
    public const float SwatchHoverBrighten = 1.18f;

    /// <summary>Added to color channels on top of <see cref="SwatchHoverBrighten"/>.</summary>
    public const float SwatchHoverBrightenAdd = 0.04f;

    // ------------------------------------------------------------------
    // Row layout
    // ------------------------------------------------------------------

    /// <summary>Horizontal gap between controls sharing one row.</summary>
    public static float Gap => ImGui.GetStyle().ItemSpacing.X;

    /// <summary>
    /// Width reserved at the right edge of a settings row for a trailing
    /// help/menu icon column.
    /// </summary>
    public static float TrailingIconReserve => ImGui.GetTextLineHeight() + Gap;

    /// <summary>
    /// Vertical inset from a framed control's top edge to its text/glyph —
    /// the same offset <c>AlignTextToFramePadding</c> applies, usable for
    /// centering trailing icons inside a <see cref="ControlHeight"/> row.
    /// </summary>
    public static float FrameTextInsetY => ImGui.GetStyle().FramePadding.Y;

    // ------------------------------------------------------------------
    // Popups & tooltips
    // ------------------------------------------------------------------

    /// <summary>Corner rounding for helper popups (palettes, kebab menus).</summary>
    public static float PopupRounding => SpheneCustomTheme.CurrentTheme.CompactContextMenuRounding;

    /// <summary>Border thickness of helper popups.</summary>
    public static float PopupBorderSize => SpheneCustomTheme.CurrentTheme.CompactContextMenuBorderSize;

    /// <summary>Inner padding of helper popups.</summary>
    public static Vector2 PopupPadding => SpheneCustomTheme.CurrentTheme.CompactChildPadding;

    /// <summary>Frame rounding for controls inside helper popups.</summary>
    public static float PopupFrameRounding => SpheneCustomTheme.CurrentTheme.CompactFrameRounding;

    /// <summary>Corner rounding of tooltips.</summary>
    public static float TooltipRounding => SpheneCustomTheme.CurrentTheme.CompactTooltipRounding;

    /// <summary>Tooltip border thickness.</summary>
    public static float TooltipBorderSize => SpheneCustomTheme.CurrentTheme.CompactTooltipBorderSize;
}
