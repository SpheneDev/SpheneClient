using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Application-wide layout density profile. Sphene runs a single
    /// profile - <see cref="Compact"/> - in every window · see
    /// <see cref="Density"/>.
    /// </summary>
    public enum DensityProfile
    {
        /// <summary>The one active profile: tight rows and padding everywhere.</summary>
        Compact,

        /// <summary>Retired - kept so legacy switch arms stay valid; never resolved.</summary>
        Balanced,

        /// <summary>Retired - kept so legacy switch arms stay valid; never resolved.</summary>
        Comfortable,
    }

    /// <summary>
    /// The density scope stack. Pushed once per window by the window base
    /// class right after <see cref="Theme.WindowScope()"/>, so
    /// <see cref="Current"/> is valid inside every design-system draw.
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

        private static readonly List<DensityProfile> Scopes = new();

        /// <summary>The active profile · <see cref="DensityProfile.Compact"/>.</summary>
        public static DensityProfile Current => Scopes.Count > 0 ? Scopes[^1] : DensityProfile.Compact;

        /// <summary>The active profile's style-var multiplier.</summary>
        public static float Factor => Current switch
        {
            DensityProfile.Compact => CompactFactor,
            DensityProfile.Comfortable => ComfortableFactor,
            _ => 1f,
        };

        /// <summary>True in <see cref="DensityProfile.Compact"/> - always.</summary>
        public static bool IsCompact => Current == DensityProfile.Compact;

        /// <summary>True in <see cref="DensityProfile.Comfortable"/> - never.</summary>
        public static bool IsComfortable => Current == DensityProfile.Comfortable;

        /// <summary>
        /// Resolves the profile for a window. Sphene only uses
        /// <see cref="DensityProfile.Compact"/>, regardless of window size.
        /// </summary>
        public static DensityProfile Resolve(Vector2 windowSizeUnscaled)
            => DensityProfile.Compact;

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
