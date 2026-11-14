using System;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;

namespace Sphene.UI.Design;

/// <summary>
/// The single entry point of the Sphene design system. All shared UI drawing
/// goes through this facade: <see cref="Spacing"/>/<see cref="Palette"/>/
/// <see cref="Colors"/>/<see cref="Metrics"/> tokens, text and icon helpers,
/// value controls, layout containers (cards, scroll areas, tab strips),
/// window chrome (title bars, blur, collapse), popups, media and
/// <see cref="Theme"/> scopes. Implementation lives in partials grouped by
/// module · see DESIGN.md for the rules and the extension guide.
/// </summary>
public static partial class SpheneUI
{
    private static IFontHandle? _iconFont;
    private static IFontHandle? _uidFont;
    private static IFontHandle? _displayFont;
    private static IFontHandle? _headingFont;
    private static IFontHandle? _gameFont;
    private static ILogger? _logger;
    private static SpheneMediator? _mediator;

    /// <summary>Shared icon font (FontAwesome). Throws until <see cref="Initialize"/> runs.</summary>
    public static IFontHandle IconFont => _iconFont
        ?? throw new InvalidOperationException("SpheneUI.Initialize has not run yet.");

    /// <summary>UID/display font. Throws until <see cref="Initialize"/> runs.</summary>
    public static IFontHandle UidFont => _uidFont
        ?? throw new InvalidOperationException("SpheneUI.Initialize has not run yet.");

    /// <summary>Display/heading font (~26px) · crisp large text without bitmap
    /// scaling. Throws until <see cref="Initialize"/> runs.</summary>
    public static IFontHandle DisplayFont => _displayFont
        ?? throw new InvalidOperationException("SpheneUI.Initialize has not run yet.");

    /// <summary>Mid heading font (~20px) for markdown h2/h3 · crisp like
    /// <see cref="DisplayFont"/> but smaller. Throws until <see cref="Initialize"/> runs.</summary>
    public static IFontHandle HeadingFont => _headingFont
        ?? throw new InvalidOperationException("SpheneUI.Initialize has not run yet.");

    /// <summary>Game font handle. Throws until <see cref="Initialize"/> runs.</summary>
    public static IFontHandle GameFont => _gameFont
        ?? throw new InvalidOperationException("SpheneUI.Initialize has not run yet.");

    /// <summary>
    /// Provides the shared font handles to the design system. Called once by
    /// <c>UiSharedService</c> during plugin initialization · the service keeps
    /// owning and disposing the handles.
    /// </summary>
    internal static void Initialize(ILogger logger, SpheneMediator mediator, IFontHandle iconFont,
        IFontHandle uidFont, IFontHandle displayFont, IFontHandle headingFont, IFontHandle gameFont)
    {
        _logger = logger;
        _mediator = mediator;
        _iconFont = iconFont;
        _uidFont = uidFont;
        _displayFont = displayFont;
        _headingFont = headingFont;
        _gameFont = gameFont;
    }

    internal static SpheneMediator? Mediator => _mediator;

    /// <summary>Resolver wired by <c>UiSharedService</c> - lazy crisp font
    /// handles for markdown &lt;size=N&gt; tags; null until initialization.</summary>
    internal static Func<int, IFontHandle?>? MarkdownFontResolver { get; set; }

    /// <summary>Real font handle for a &lt;size=N&gt; tag, or null when the
    /// resolver has not been wired (renderer falls back to FontScale).</summary>
    public static IFontHandle? ResolveMarkdownFont(int pt) => MarkdownFontResolver?.Invoke(pt);

    internal static void LogError(Exception ex, string message)
        => _logger?.LogError(ex, "{Message}", message);

    internal static void LogDebug(string message, params object?[] args)
        => _logger?.LogDebug(message, args);

    /// <summary>Global UI scale · every token multiplies this.</summary>
    public static float Scale => ImGuiHelpers.GlobalScale;

    /// <summary>Segment separator inside tooltip text; each segment renders with a divider between.</summary>
    public const string TooltipSeparator = "--SEP--";
}

public static partial class SpheneUI
{
    /// <summary>
    /// Theme scopes and the active <see cref="ThemeConfiguration"/>.
    /// </summary>
    public static class Theme
    {
        /// <summary>The active theme · same object as <see cref="SpheneTheme.Current"/>.</summary>
        public static ThemeConfiguration Current => SpheneTheme.Current;

        /// <summary>
        /// Pushes the standard window theme (all colors + style vars) for the
        /// current window. Applied once per draw by the window base class for
        /// every design-system window (all except CompactUI).
        /// </summary>
        public static IDisposable WindowScope() => new WindowThemeScope();

        /// <summary>
        /// Pushes the CompactUI theme (<c>Compact*</c> values). Owned by
        /// CompactUI until it receives its own design system.
        /// </summary>
        public static IDisposable CompactScope(string? hostWindowName = null) => new CompactThemeScope(hostWindowName);

        /// <summary>Tooltip rounding/border scope.</summary>
        public static IDisposable TooltipScope() => new TooltipThemeScope();

        /// <summary>Context menu / helper popup rounding/border/padding scope.</summary>
        public static IDisposable ContextMenuScope() => new ContextMenuThemeScope();

        /// <summary>
        /// Overrides <see cref="Colors.Accent"/> inside the scope  ·  community
        /// surfaces (syncshell pages, friend views, feed items) tint every
        /// accent-driven control with the owner's accent without a full theme
        /// push. Nested scopes resolve LIFO.
        /// </summary>
        public static IDisposable AccentScope(Vector4 accent)
        {
            Colors.PushAccentOverride(accent);
            return new AccentOverrideScope();
        }

        private sealed class AccentOverrideScope : IDisposable
        {
            public void Dispose() => Colors.PopAccentOverride();
        }
    }
}
