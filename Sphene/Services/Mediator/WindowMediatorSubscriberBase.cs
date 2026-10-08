using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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

    /// <summary>Right-side title bar buttons; defaults to a close button honoring <see cref="Window.ShowCloseButton"/>.</summary>
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
                    SpheneUI.Chrome.TitleBarHeightUnscaled);
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
        if (AppliesOwnThemeScope)
        {
            _performanceCollectorService.LogPerformance(this, $"Draw", DrawInternal);
            return;
        }
        using var themeScope = SpheneUI.Theme.WindowScope();
        SpheneUI.Chrome.ThemedWindowBlur();
        _performanceCollectorService.LogPerformance(this, $"Draw",
            UseSpheneChrome ? DrawWithChrome : DrawInternal);
    }

    private void DrawWithChrome()
    {
        SpheneUI.Chrome.TitleBar(ChromeId, ChromeTitle, ChromeIcon,
            rightButtons: ChromeRightButtons, trailingText: ChromeTrailingText,
            collapsible: ChromeCollapsible);

        var foldAmount = ChromeCollapsible ? SpheneUI.Chrome.CollapseAmount(ChromeId) : 0f;
        if (foldAmount >= 1f)
        {
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

    protected virtual void Dispose(bool disposing)
    {
        _logger.LogTrace("Disposing {type}", GetType());

        Mediator.UnsubscribeAll(this);
    }
}
