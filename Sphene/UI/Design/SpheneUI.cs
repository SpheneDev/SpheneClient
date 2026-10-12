using System;
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
/// module — see DESIGN.md for the rules and the extension guide.
/// </summary>
public static partial class SpheneUI
{
    private static IFontHandle? _iconFont;
    private static IFontHandle? _uidFont;
    private static IFontHandle? _displayFont;
    private static IFontHandle? _gameFont;
    private static ILogger? _logger;
    private static SpheneMediator? _mediator;

    /// <summary>Shared icon font (FontAwesome). Throws until <see cref="Initialize"/> runs.</summary>
    public static IFontHandle IconFont => _iconFont
        ?? throw new InvalidOperationException("SpheneUI.Initialize has not run yet.");

    /// <summary>UID/display font. Throws until <see cref="Initialize"/> runs.</summary>
    public static IFontHandle UidFont => _uidFont
        ?? throw new InvalidOperationException("SpheneUI.Initialize has not run yet.");

    /// <summary>Display/heading font (~26px) — crisp large text without bitmap
    /// scaling. Throws until <see cref="Initialize"/> runs.</summary>
    public static IFontHandle DisplayFont => _displayFont
        ?? throw new InvalidOperationException("SpheneUI.Initialize has not run yet.");

    /// <summary>Game font handle. Throws until <see cref="Initialize"/> runs.</summary>
    public static IFontHandle GameFont => _gameFont
        ?? throw new InvalidOperationException("SpheneUI.Initialize has not run yet.");

    /// <summary>
    /// Provides the shared font handles to the design system. Called once by
    /// <c>UiSharedService</c> during plugin initialization — the service keeps
    /// owning and disposing the handles.
    /// </summary>
    internal static void Initialize(ILogger logger, SpheneMediator mediator, IFontHandle iconFont,
        IFontHandle uidFont, IFontHandle displayFont, IFontHandle gameFont)
    {
        _logger = logger;
        _mediator = mediator;
        _iconFont = iconFont;
        _uidFont = uidFont;
        _displayFont = displayFont;
        _gameFont = gameFont;
    }

    internal static SpheneMediator? Mediator => _mediator;

    internal static void LogError(Exception ex, string message)
        => _logger?.LogError(ex, "{Message}", message);

    /// <summary>Global UI scale — every token multiplies this.</summary>
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
        /// <summary>The active theme — same object as <see cref="SpheneTheme.Current"/>.</summary>
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
        public static IDisposable CompactScope() => new CompactThemeScope();

        /// <summary>Tooltip rounding/border scope.</summary>
        public static IDisposable TooltipScope() => new TooltipThemeScope();

        /// <summary>Context menu / helper popup rounding/border/padding scope.</summary>
        public static IDisposable ContextMenuScope() => new ContextMenuThemeScope();
    }
}
