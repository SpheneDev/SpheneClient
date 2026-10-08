using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// An immutable spacing token on the design grid. Implicitly converts to
    /// the UI-scaled pixel value — helpers receive tokens, never raw floats,
    /// so off-grid spacing cannot sneak in through the public API.
    /// </summary>
    public readonly struct Space : IEquatable<Space>
    {
        /// <summary>Unscaled base value.</summary>
        public float Value { get; }

        internal Space(float value, bool custom = false)
        {
            Value = value;
            if (custom)
            {
                Lint.RecordOffGrid(value, "Spacing.Custom");
            }
        }

        /// <summary>UI-scaled pixel value.</summary>
        public float Scaled => Value * SpheneUI.Scale;

        public static implicit operator float(Space space) => space.Scaled;
        public static implicit operator Vector2(Space space) => new(space.Scaled, space.Scaled);
        public static bool operator ==(Space a, Space b) => a.Value.Equals(b.Value);
        public static bool operator !=(Space a, Space b) => !a.Value.Equals(b.Value);
        public bool Equals(Space other) => Value.Equals(other.Value);
        public override bool Equals(object? obj) => obj is Space other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Space({Value})";

        /// <summary>True when <paramref name="scaledPixels"/> lands on the grid (±0.75 tolerance).</summary>
        public static bool IsOnGrid(float scaledPixels)
        {
            var unscaled = scaledPixels / SpheneUI.Scale;
            foreach (var step in Spacing.Steps)
            {
                if (MathF.Abs(unscaled - step) <= 0.75f)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Fixed spacing scale — the only legal gaps, paddings and indents in the
    /// design system. Base values are unscaled; <see cref="Space"/> applies
    /// <see cref="ImGuiHelpers.GlobalScale"/> automatically. Use the semantic
    /// aliases for layout code and the raw steps only when no alias fits.
    /// </summary>
    public static class Spacing
    {
        internal static readonly float[] Steps = [1f, 2f, 4f, 8f, 10f, 12f, 16f, 20f, 24f, 32f];

        public static Space Hairline => new(1f);
        public static Space X2 => new(2f);
        public static Space X4 => new(4f);
        public static Space X8 => new(8f);
        public static Space X10 => new(10f);
        public static Space X12 => new(12f);
        public static Space X16 => new(16f);
        public static Space X20 => new(20f);
        public static Space X24 => new(24f);
        public static Space X32 => new(32f);

        /// <summary>Horizontal gap between elements sharing one row (SameLine).</summary>
        public static Space Inline => X4;

        /// <summary>Vertical gap between consecutive rows/controls.</summary>
        public static Space Row => X8;

        /// <summary>Vertical gap between logical groups inside a card.</summary>
        public static Space Section => X12;

        /// <summary>Gap between sibling cards on a page.</summary>
        public static Space CardGap => X8;

        /// <summary>Inner padding of cards.</summary>
        public static Space CardPadding => X16;

        /// <summary>Outer page padding inside the content scroll area.</summary>
        public static Space PagePadding => X16;

        /// <summary>Default horizontal inset of scroll-area content.</summary>
        public static Space ScrollInset => X10;

        /// <summary>Title bar horizontal padding.</summary>
        public static Space TitlePadX => X12;

        /// <summary>Title bar vertical padding.</summary>
        public static Space TitlePadY => X8;

        /// <summary>Gap between title bar buttons.</summary>
        public static Space TitleButtonGap => X4;

        /// <summary>
        /// Escape hatch for a non-grid value — always records a lint hit.
        /// Prefer adding a new step or semantic alias instead.
        /// </summary>
        public static Space Custom(float value) => new(value, custom: true);
    }

    /// <summary>
    /// The static Sphene palette — crystalline blues, ethereal purples and
    /// golden accents plus network/status colors. These are constants, not
    /// theme-resolved; prefer <see cref="Colors"/> in controls.
    /// </summary>
    public static class Palette
    {
        // Primary Sphene Colors - Crystalline Blues and Purples
        public static readonly Vector4 CrystalBlue = new(0.4f, 0.7f, 1.0f, 1.0f);          // #66B3FF
        public static readonly Vector4 DeepCrystal = new(0.2f, 0.4f, 0.8f, 1.0f);          // #3366CC
        public static readonly Vector4 EtherealPurple = new(0.6f, 0.4f, 0.9f, 1.0f);       // #9966E6
        public static readonly Vector4 VoidPurple = new(0.4f, 0.2f, 0.7f, 1.0f);           // #6633B3

        // Accent Colors - Golden and Light Tones
        public static readonly Vector4 SpheneGold = new(1.0f, 0.8f, 0.3f, 1.0f);           // #FFCC4D
        public static readonly Vector4 LuminousGold = new(1.0f, 0.9f, 0.6f, 1.0f);         // #FFE699
        public static readonly Vector4 CrystalWhite = new(0.95f, 0.98f, 1.0f, 1.0f);       // #F2FAFF
        public static readonly Vector4 EtherGlow = new(0.8f, 0.9f, 1.0f, 1.0f);            // #CCE6FF

        // Status Colors - Network themed
        public static readonly Vector4 NetworkActive = new(0.3f, 0.8f, 0.5f, 1.0f);        // #4DCC80
        public static readonly Vector4 NetworkConnected = NetworkActive;
        public static readonly Vector4 NetworkDisconnected = new(0.9f, 0.3f, 0.4f, 1.0f);  // #E64D66
        public static readonly Vector4 NetworkWarning = new(1.0f, 0.7f, 0.2f, 1.0f);       // #FFB333
        public static readonly Vector4 NetworkError = NetworkDisconnected;
        public static readonly Vector4 NetworkInactive = new(0.6f, 0.6f, 0.7f, 1.0f);      // #9999B3
        public static readonly Vector4 TransmissionActive = CrystalBlue;
        public static readonly Vector4 ReceptionActive = EtherealPurple;

        // UI Element Colors
        public static readonly Vector4 BackgroundDark = new(0.1f, 0.12f, 0.18f, 0.0f);     // rgb(26, 31, 46)
        public static readonly Vector4 BackgroundMid = new(0.04f, 0.0f, 0.12f, 0.8f);      // rgba(50, 47, 87, 0.81)
        public static readonly Vector4 BorderColor = new(0.3f, 0.4f, 0.6f, 1.0f);          // #4D6699
        public static readonly Vector4 TextPrimary = new(0.9f, 0.95f, 1.0f, 1.0f);         // #E6F2FF
        public static readonly Vector4 TextSecondary = new(0.7f, 0.8f, 0.9f, 1.0f);        // #B3CCE6
        public static readonly Vector4 UITextSecondary = TextSecondary;

        // Hover and Selection States
        public static readonly Vector4 HoverBlue = new(0.5f, 0.7f, 0.9f, 0.3f);
        public static readonly Vector4 SelectionBlue = new(0.4f, 0.6f, 0.8f, 0.5f);
        public static readonly Vector4 ActiveTab = new(0.3f, 0.5f, 0.8f, 1.0f);

        // Aliases historically exposed as SpheneUI.Palette
        public static readonly Vector4 PrimaryDark = BackgroundMid;
        public static readonly Vector4 SecondaryDark = BackgroundMid;
        public static readonly Vector4 AccentBlue = DeepCrystal;
        public static readonly Vector4 AccentCyan = CrystalBlue;
        public static readonly Vector4 Border = BorderColor;
        public static readonly Vector4 Hover = HoverBlue;
        public static readonly Vector4 Active = SelectionBlue;
        public static readonly Vector4 Success = NetworkActive;
        public static readonly Vector4 Warning = NetworkWarning;
        public static readonly Vector4 Error = NetworkError;
        public static readonly Vector4 HeaderBg = ColorUtils.WithAlpha(DeepCrystal, 0.3f);
        public static readonly Vector4 EtherealGlow = EtherGlow;

        /// <summary>Status color for a boolean active state.</summary>
        public static Vector4 StatusColor(bool isActive) => isActive ? NetworkActive : NetworkInactive;

        /// <summary>Connection status color with warning/error overrides.</summary>
        public static Vector4 ConnectionStatusColor(bool isConnected, bool hasWarning = false, bool hasError = false)
        {
            if (hasError) return NetworkError;
            if (hasWarning) return NetworkWarning;
            return isConnected ? NetworkActive : NetworkInactive;
        }
    }

    /// <summary>
    /// Pure color utilities used across the design system.
    /// </summary>
    public static partial class ColorUtils
    {
        /// <summary>Packs a color into ImGui's U32 format.</summary>
        public static uint ToU32(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

        /// <summary>Returns <paramref name="color"/> with a replaced alpha.</summary>
        public static Vector4 WithAlpha(Vector4 color, float alpha)
            => new(color.X, color.Y, color.Z, alpha);

        /// <summary>Linear interpolation between two colors, t clamped to [0,1].</summary>
        public static Vector4 LerpColor(Vector4 from, Vector4 to, float t)
            => Vector4.Lerp(from, to, Math.Clamp(t, 0f, 1f));

        /// <summary>
        /// Picks the more readable text color for a colored background by WCAG
        /// contrast ratio: white or a darkened shade of the background itself
        /// (keeps the text hue in harmony with the accent).
        /// </summary>
        public static Vector4 ContrastTextColor(Vector4 background)
        {
            var dark = LerpColor(background, new Vector4(0.04f, 0.05f, 0.09f, 1f), 0.85f);
            return ContrastRatio(background, Vector4.One) >= ContrastRatio(background, dark)
                ? new Vector4(0.96f, 0.98f, 1f, 1f)
                : dark;
        }

        /// <summary>WCAG 2.x contrast ratio between two colors (1..21).</summary>
        public static float ContrastRatio(Vector4 a, Vector4 b)
        {
            var la = RelativeLuminance(a) + 0.05f;
            var lb = RelativeLuminance(b) + 0.05f;
            return la > lb ? la / lb : lb / la;
        }

        /// <summary>sRGB relative luminance with linearization per WCAG 2.x.</summary>
        private static float RelativeLuminance(Vector4 color)
        {
            static float Linearize(float c)
                => c <= 0.03928f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
            return 0.2126f * Linearize(color.X) + 0.7152f * Linearize(color.Y)
                + 0.0722f * Linearize(color.Z);
        }
    }

    /// <summary>
    /// Semantic colors resolved against <see cref="SpheneTheme.Current"/> —
    /// never raw ImGui style. Controls and helpers use these; a theme tweak
    /// re-themes every control at once.
    /// </summary>
    public static partial class Colors
    {
        /// <summary>Primary accent (links, active states, selected marks).</summary>
        public static Vector4 Accent => Palette.AccentBlue;

        /// <summary>Standard readable text.</summary>
        public static Vector4 TextPrimary => Theme.Current.TextPrimary;

        /// <summary>Secondary/hint text.</summary>
        public static Vector4 TextSecondary => Theme.Current.TextSecondary;

        /// <summary>Muted text (disabled, meta info, help icons).</summary>
        public static Vector4 TextMuted => Theme.Current.TextDisabled;

        /// <summary>Frame fill of resting value controls.</summary>
        public static Vector4 FrameBg => Theme.Current.FrameBg;

        /// <summary>Frame fill while hovered.</summary>
        public static Vector4 FrameBgHover => Theme.Current.FrameBgHovered;

        /// <summary>Frame fill while pressed/active.</summary>
        public static Vector4 FrameBgActive => Theme.Current.FrameBgActive;

        /// <summary>Resting border of controls and cards.</summary>
        public static Vector4 Border => Theme.Current.Border;

        /// <summary>Popup surface fill.</summary>
        public static Vector4 Surface => Theme.Current.PopupBg;

        /// <summary>Button fill at rest.</summary>
        public static Vector4 ButtonBg => Theme.Current.Button;

        /// <summary>Button fill while hovered.</summary>
        public static Vector4 ButtonBgHover => Theme.Current.ButtonHovered;

        /// <summary>Button fill while pressed.</summary>
        public static Vector4 ButtonBgActive => Theme.Current.ButtonActive;

        /// <summary>Destructive/danger accent.</summary>
        public static Vector4 Danger => ImGuiColors.DalamudRed;

        /// <summary>Warning accent.</summary>
        public static Vector4 Warning => ImGuiColors.DalamudYellow;

        /// <summary>Success/ok accent.</summary>
        public static Vector4 Success => ImGuiColors.HealerGreen;
    }

    /// <summary>
    /// Geometry tokens — control heights, corner radii and reserved space.
    /// Helpers read these instead of hardcoding numbers or trusting ImGui's
    /// global style.
    /// </summary>
    public static class Metrics
    {
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
            => MathF.Min(size * SwatchRadiusFactor, SwatchMaxRadius * SpheneUI.Scale);

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

        /// <summary>Horizontal gap between controls sharing one row.</summary>
        public static float Gap => ImGui.GetStyle().ItemSpacing.X;

        /// <summary>
        /// Width reserved at the right edge of a settings row for a trailing
        /// help/menu icon column.
        /// </summary>
        public static float TrailingIconReserve => ImGui.GetTextLineHeight() + Gap;

        /// <summary>
        /// Vertical inset from a framed control's top edge to its text/glyph —
        /// the same offset <c>AlignTextToFramePadding</c> applies.
        /// </summary>
        public static float FrameTextInsetY => ImGui.GetStyle().FramePadding.Y;

        /// <summary>Corner rounding for helper popups (palettes, kebab menus).</summary>
        public static float PopupRounding => Theme.Current.CompactContextMenuRounding;

        /// <summary>Border thickness of helper popups.</summary>
        public static float PopupBorderSize => Theme.Current.CompactContextMenuBorderSize;

        /// <summary>Inner padding of helper popups.</summary>
        public static Vector2 PopupPadding => Theme.Current.CompactChildPadding;

        /// <summary>Frame rounding for controls inside helper popups.</summary>
        public static float PopupFrameRounding => Theme.Current.CompactFrameRounding;

        /// <summary>Corner rounding of tooltips.</summary>
        public static float TooltipRounding => Theme.Current.CompactTooltipRounding;

        /// <summary>Tooltip border thickness.</summary>
        public static float TooltipBorderSize => Theme.Current.CompactTooltipBorderSize;
    }
}

public static partial class SpheneUI
{
    public static partial class Colors
    {
        /// <summary>Green for true, red for false — the bool-state pair.</summary>
        public static Vector4 Bool(bool input) => input ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed;

        /// <summary>Upload progress color: grey when empty, green when complete, yellow in between.</summary>
        public static Vector4 UploadProgress((long, long) data) => data.Item1 == 0 ? ImGuiColors.DalamudGrey :
            data.Item1 == data.Item2 ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudYellow;
    }

    public static partial class ColorUtils
    {
        /// <summary>Packs RGBA bytes into ImGui's U32 format.</summary>
        public static uint PackRgba(byte r, byte g, byte b, byte a)
        {
            uint ret = a;
            ret <<= 8;
            ret += b;
            ret <<= 8;
            ret += g;
            ret <<= 8;
            ret += r;
            return ret;
        }

        /// <summary>U32 from a Vector4 color.</summary>
        public static uint U32(Vector4 color) => ToU32(color);

        /// <summary>U32 from RGBA bytes.</summary>
        public static uint U32(byte r, byte g, byte b, byte a) => PackRgba(r, g, b, a);
    }
}
