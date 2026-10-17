using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sphene.UI.Design;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sphene.Services.Mediator;

public abstract class WindowMediatorSubscriberBase : Window, IMediatorSubscriber, IDisposable
{
    protected readonly ILogger _logger;
    private readonly PerformanceCollectorService _performanceCollectorService;

    protected WindowMediatorSubscriberBase(ILogger logger, SpheneMediator mediator, string name,
        PerformanceCollectorService performanceCollectorService) : base(name)
    {
        _logger = logger;
        Mediator = mediator;
        _performanceCollectorService = performanceCollectorService;
        _logger.LogTrace("Creating {type}", GetType());

        Mediator.Subscribe<UiToggleMessage>(this, (msg) =>
        {
            if (msg.UiType == GetType())
            {
                var wasOpen = IsOpen;
                _logger.LogDebug("UiToggleMessage received for {type}. Toggling window. Previous IsOpen={wasOpen}", GetType().Name, wasOpen);
                Toggle();
                _logger.LogDebug("Window toggled for {type}. New IsOpen={isOpen}", GetType().Name, IsOpen);
            }
        });
    }

    public SpheneMediator Mediator { get; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Windows that push their own theme scope in <see cref="Window.PreDraw"/>
    /// (e.g. CompactUI) must not have the general window theme layered on top
    /// during <see cref="DrawInternal"/>.
    /// </summary>
    protected virtual bool AppliesOwnThemeScope => false;

    /// <summary>
    /// Whether the window renders the design-system chrome — themed surface,
    /// custom title bar with drag region and fold animation — instead of the
    /// native ImGui frame. On by default; overlays and popup hosts opt out.
    /// </summary>
    protected virtual bool UseSpheneChrome => !AppliesOwnThemeScope;

    /// <summary>Whether the title bar shows the fold toggle. Off for auto-resizing windows.</summary>
    protected virtual bool ChromeCollapsible => true;

    /// <summary>
    /// Whether the chrome draws its own title bar. Windows whose content
    /// begins with a full-width header band (e.g. the Sphene Hub) return
    /// false — that band then acts as the drag region and owns the fold and
    /// close buttons itself.
    /// </summary>
    protected virtual bool ChromeHasTitleBar => true;

    /// <summary>
    /// Draws the star-chart backdrop over the window surface, under all
    /// content. Opt-in per window — off by default so small overlays and
    /// helper windows stay clean.
    /// </summary>
    protected virtual bool ChromeWindowPattern => false;

    /// <summary>
    /// Whether <see cref="DrawCollapsedContent"/> replaces
    /// <see cref="DrawInternal"/> for the entire fold animation instead of the
    /// fading-content behavior. Only sensible when <see cref="ChromeHasTitleBar"/>
    /// is false — pages with a chrome title bar keep the bar during the fold.
    /// </summary>
    protected virtual bool DrawCollapsedDuringFold => false;

    /// <summary>Collapsed window height in unscaled units — defaults to the chrome title bar height.</summary>
    protected virtual float ChromeCollapsedHeight => SpheneUI.Chrome.TitleBarHeightUnscaled;

    /// <summary>Collapsed window width in unscaled units; 0 keeps the open width.</summary>
    protected virtual float ChromeCollapsedWidth => 0f;

    /// <summary>
    /// Content drawn instead of <see cref="DrawInternal"/> while the window is
    /// folded or folding — only used when <see cref="DrawCollapsedDuringFold"/>
    /// is set.
    /// </summary>
    protected virtual void DrawCollapsedContent() { }

    /// <summary>Title shown in the chrome title bar — defaults to the window name without its ###id suffix.</summary>
    protected virtual string ChromeTitle
    {
        get
        {
            var name = WindowName;
            var idx = name.IndexOf("##", StringComparison.Ordinal);
            return idx > 0 ? name[..idx] : name;
        }
    }

    /// <summary>Icon drawn left of the chrome title.</summary>
    protected virtual FontAwesomeIcon? ChromeIcon => null;

    /// <summary>Muted text rendered right of the title, before the right buttons.</summary>
    protected virtual string? ChromeTrailingText => null;

    /// <summary>
    /// Optional content drawn in the title bar between the title and the
    /// right-side buttons — receives the zone's min/max screen positions.
    /// </summary>
    protected virtual Action<Vector2, Vector2>? ChromeMiddleContent => null;

    /// <summary>Right-side title bar buttons; defaults to a close button honoring <see cref="Window.ShowCloseButton"/>.</summary>
    /// <summary>Optional texture drawn instead of <see cref="ChromeIcon"/> when set.</summary>
    protected virtual IDalamudTextureWrap? ChromeIconTexture => null;

    protected virtual IReadOnlyList<SpheneUI.Chrome.TitleBarButton>? ChromeRightButtons =>
        ShowCloseButton
            ? [new SpheneUI.Chrome.TitleBarButton(FontAwesomeIcon.Times, "Close", () => IsOpen = false)]
            : null;

    private string ChromeId => WindowName;

    public override void PreDraw()
    {
        if (UseSpheneChrome)
        {
            Flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.NoBackground;
            if (ChromeCollapsible)
            {
                SpheneUI.Chrome.UpdateCollapse(this, ChromeId,
                    ChromeCollapsedHeight, ChromeCollapsedWidth);
            }

            // Native resize grip stays hidden; the chrome draws its own frame.
            ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Vector4.Zero);
        }
        base.PreDraw();
    }

    public override void PostDraw()
    {
        if (UseSpheneChrome)
        {
            ImGui.PopStyleColor(3);
        }
        base.PostDraw();
    }

    public override void Draw()
    {
        try
        {
            if (AppliesOwnThemeScope)
            {
                _performanceCollectorService.LogPerformance(this, $"Draw", DrawInternal);
                return;
            }
            using var themeScope = SpheneUI.Theme.WindowScope();
            // Blur is part of the chrome look — windows that opt out of the
            // chrome (overlay hosts like PopupHandler, the draggable icon)
            // must not get a ghost blur rect either.
            if (UseSpheneChrome)
            {
                SpheneUI.Chrome.ThemedWindowBlur();
            }
            _performanceCollectorService.LogPerformance(this, $"Draw",
                UseSpheneChrome ? DrawWithChrome : DrawInternal);
        }
        catch (Exception ex)
        {
            // One broken window must not abort WindowSystem.Draw and take all
            // following windows down with it.
            _logger.LogError(ex, "Window {type} ({name}) failed to draw",
                GetType().Name, WindowName);
        }
    }

    public override void OnOpen()
    {
        // Reopening always lands maximized — a window closed while collapsed
        // must not come back as a strip.
        if (UseSpheneChrome && ChromeCollapsible)
        {
            SpheneUI.Chrome.ResetFold(this, ChromeId);
        }
        base.OnOpen();
    }

    private void DrawWithChrome()
    {
        if (ChromeHasTitleBar)
        {
            SpheneUI.Chrome.TitleBar(ChromeId, ChromeTitle, ChromeIcon,
                rightButtons: ChromeRightButtons, trailingText: ChromeTrailingText,
                collapsible: ChromeCollapsible, leadingTexture: ChromeIconTexture,
                middleContent: ChromeMiddleContent);
        }
        else
        {
            // Without a chrome title bar the content owns the window top —
            // the themed surface and border ring still come from the chrome.
            var drawList = ImGui.GetWindowDrawList();
            var windowPos = ImGui.GetWindowPos();
            SpheneUI.Chrome.DrawWindowSurface(drawList, windowPos,
                windowPos + ImGui.GetWindowSize(), ImGui.GetStyle().WindowRounding);
        }

        var foldAmount = ChromeCollapsible ? SpheneUI.Chrome.CollapseAmount(ChromeId) : 0f;
        if (ChromeWindowPattern && foldAmount < 1f)
        {
            var drawList = ImGui.GetWindowDrawList();
            var windowPos = ImGui.GetWindowPos();
            SpheneUI.Fx.Constellation(drawList, windowPos, windowPos + ImGui.GetWindowSize());
        }
        if (foldAmount >= 1f || (DrawCollapsedDuringFold && foldAmount > 0f))
        {
            DrawCollapsedContent();
            return;
        }

        using var contentFade = ImRaii.PushStyle(ImGuiStyleVar.Alpha,
            ImGui.GetStyle().Alpha * (1f - MathF.Min(1f, foldAmount * 1.4f)), foldAmount > 0f);
        DrawInternal();
    }

    protected abstract void DrawInternal();

    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // Windows are registered as both their concrete type and
    // WindowMediatorSubscriberBase so embedded hosts can inject them — the DI
    // scope captures the shared instance once per descriptor and would
    // otherwise run the dispose chain twice. Overrides run before this
    // guard, so their teardown must stay idempotent (CancelDispose et al.).
    private int _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _logger.LogTrace("Disposing {type}", GetType());

        Mediator.UnsubscribeAll(this);
    }
}
