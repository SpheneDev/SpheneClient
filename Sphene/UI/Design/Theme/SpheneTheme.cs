using System;

namespace Sphene.UI.Design;

/// <summary>
/// Holds the currently active <see cref="ThemeConfiguration"/> for the Sphene
/// design system. Every token, scope and helper resolves against
/// <see cref="Current"/> — this is the single source of truth for theme state.
/// Replaces the former <c>SpheneTheme.Current</c> static.
/// </summary>
public static class SpheneTheme
{
    /// <summary>The active theme. Assigned on startup load and by the theme editor.</summary>
    public static ThemeConfiguration Current { get; set; } = new ThemeConfiguration();
}
