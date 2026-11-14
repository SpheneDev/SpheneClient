using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Application-wide layout density profiles. The active profile is
    /// resolved per window from its actual unscaled size (never the monitor
    /// resolution) unless the user pins one in settings · see
    /// <see cref="Density"/>.
    /// </summary>
    public enum DensityProfile
    {
        /// <summary>Tight rows and padding for small windows / short viewports.</summary>
        Compact,

        /// <summary>Default spacing for typical window dimensions.</summary>
        Balanced,

        /// <summary>Generous spacing for large windows.</summary>
        Comfortable,
    }

    /// <summary>
    /// The density scope stack and profile resolution. Pushed once per window
    /// by the window base class right after <see cref="Theme.WindowScope()"/>,
    /// so <see cref="Current"/> is valid inside every design-system draw.
    /// Semantic <see cref="Spacing"/> aliases and <see cref="Metrics"/>
    /// values remap to neighbor grid steps per profile - raw grid steps never
    /// move, so the lint stays honest.
    /// </summary>
    public static class Density
    {
        /// <summary>Style-var multiplier applied in <see cref="DensityProfile.Compact"/>.</summary>
        public const float CompactFactor = 0.75f;

        /// <summary>Style-var multiplier applied in <see cref="DensityProfile.Comfortable"/>.</summary>
        public const float ComfortableFactor = 1.15f;

        // Auto-resolution thresholds in unscaled window units. Values below
        // Compact collapse the profile; Comfortable needs BOTH dimensions
        // large so a wide-but-short window never inflates vertically.
        private const float CompactMaxHeight = 640f;
        private const float CompactMaxWidth = 760f;
        private const float ComfortableMinHeight = 880f;
        private const float ComfortableMinWidth = 1400f;

        private static readonly List<DensityProfile> Scopes = new();
        private static Func<DensityProfile?>? _overrideProvider;

        /// <summary>The active profile · <see cref="DensityProfile.Balanced"/> outside any scope.</summary>
        public static DensityProfile Current => Scopes.Count > 0 ? Scopes[^1] : DensityProfile.Balanced;

        /// <summary>The active profile's style-var multiplier (1 in Balanced).</summary>
        public static float Factor => Current switch
        {
            DensityProfile.Compact => CompactFactor,
            DensityProfile.Comfortable => ComfortableFactor,
            _ => 1f,
        };

        /// <summary>True in <see cref="DensityProfile.Compact"/>.</summary>
        public static bool IsCompact => Current == DensityProfile.Compact;

        /// <summary>True in <see cref="DensityProfile.Comfortable"/>.</summary>
        public static bool IsComfortable => Current == DensityProfile.Comfortable;

        /// <summary>
        /// Picks the profile for an unscaled window size. A manual override
        /// (when set via <see cref="SetOverrideProvider"/>) wins over the
        /// size-based result.
        /// </summary>
        public static DensityProfile Resolve(Vector2 windowSizeUnscaled)
        {
            var manual = _overrideProvider?.Invoke();
            if (manual.HasValue)
            {
                return manual.Value;
            }
            if (windowSizeUnscaled.Y <= CompactMaxHeight
                || windowSizeUnscaled.X <= CompactMaxWidth)
            {
                return DensityProfile.Compact;
            }
            if (windowSizeUnscaled.Y >= ComfortableMinHeight
                && windowSizeUnscaled.X >= ComfortableMinWidth)
            {
                return DensityProfile.Comfortable;
            }
            return DensityProfile.Balanced;
        }

        /// <summary>
        /// Resolves the profile for the window currently being drawn and
        /// pushes it together with its ImGui style-var pass · called by the
        /// window base class inside <c>Draw()</c>. The resolved profile is
        /// handed back through <paramref name="resolved"/> so the window can
        /// feed <see cref="PushFrame"/> next frame - PreDraw code (fold math)
        /// reads the previous frame's profile and stays consistent.
        /// </summary>
        public static IDisposable AutoWindowScope(out DensityProfile resolved)
        {
            resolved = Resolve(ImGui.GetWindowSize() / SpheneUI.Scale);
            return Scope(resolved);
        }

        /// <summary>Pushes an explicit profile (and its style vars) for nested surfaces.</summary>
        public static IDisposable Scope(DensityProfile profile) => new DensityScope(profile, applyVars: true);

        /// <summary>
        /// Pushes a stored profile for the PreDraw → Draw → PostDraw span
        /// without touching style vars · the Draw-time
        /// <see cref="AutoWindowScope"/> owns the style-var pass.
        /// Paired with <see cref="PopFrame"/> - no scope object so the window
        /// base can span it across its own override boundaries.
        /// </summary>
        public static void PushFrame(DensityProfile profile) => Scopes.Add(profile);

        /// <summary>Pops the innermost frame marker pushed by <see cref="PushFrame"/>.</summary>
        public static void PopFrame()
        {
            if (Scopes.Count > 0)
            {
                Scopes.RemoveAt(Scopes.Count - 1);
            }
        }

        /// <summary>
        /// Registers the settings override · wired once by
        /// <c>UiSharedService</c> so the design system stays config-free.
        /// </summary>
        internal static void SetOverrideProvider(Func<DensityProfile?> provider)
            => _overrideProvider = provider;

        private sealed class DensityScope : ThemeStyleScope
        {
            private bool _popped;

            public DensityScope(DensityProfile profile, bool applyVars)
            {
                Scopes.Add(profile);
                var factor = profile switch
                {
                    DensityProfile.Compact => CompactFactor,
                    DensityProfile.Comfortable => ComfortableFactor,
                    _ => 1f,
                };
                if (applyVars && MathF.Abs(factor - 1f) > 0.001f)
                {
                    ApplyVars(factor);
                }
            }

            private void ApplyVars(float factor)
            {
                var style = ImGui.GetStyle();
                static Vector2 Shrink(Vector2 v, float f)
                    => new(MathF.Max(1f, v.X * f), MathF.Max(1f, v.Y * f));
                PushStyleVar(ImGuiStyleVar.ItemSpacing, Shrink(style.ItemSpacing, factor));
                PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, Shrink(style.ItemInnerSpacing, factor));
                PushStyleVar(ImGuiStyleVar.FramePadding, Shrink(style.FramePadding, factor));
                PushStyleVar(ImGuiStyleVar.CellPadding, Shrink(style.CellPadding, factor));
                PushStyleVar(ImGuiStyleVar.IndentSpacing, MathF.Max(1f, style.IndentSpacing * factor));
            }

            protected override void Dispose(bool disposing)
            {
                if (_popped)
                {
                    return;
                }
                _popped = true;
                if (Scopes.Count > 0)
                {
                    Scopes.RemoveAt(Scopes.Count - 1);
                }
                base.Dispose(disposing);
            }
        }
    }
}
