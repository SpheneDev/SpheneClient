using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Sphene.SpheneConfiguration;
using Sphene.Services.Mediator;
using Sphene.UI;
using Sphene.UI.Components.Popup;
using Sphene.UI.Panels;
using Sphene.UI.Syncshell;
using Sphene.UI.Design;
using Microsoft.Extensions.Logging;

namespace Sphene.Services;

public sealed class UiService : DisposableMediatorSubscriberBase
{
    private readonly List<WindowMediatorSubscriberBase> _createdWindows = [];
    private readonly IUiBuilder _uiBuilder;
    private readonly FileDialogManager _fileDialogManager;
    private readonly IFramework _framework;
    private readonly ILogger<UiService> _logger;
    private readonly SpheneConfigService _SpheneConfigService;
    private readonly WindowSystem _windowSystem;
    private readonly UiFactory _uiFactory;
    private volatile bool _disposed;

    public UiService(ILogger<UiService> logger, IUiBuilder uiBuilder,
        SpheneConfigService SpheneConfigService, WindowSystem windowSystem,
        IEnumerable<WindowMediatorSubscriberBase> windows,
        UiFactory uiFactory, FileDialogManager fileDialogManager,
        SpheneMediator spheneMediator, IFramework framework) : base(logger, spheneMediator)
    {
        _logger = logger;
        _logger.LogTrace("Creating {type}", GetType().Name);
        _uiBuilder = uiBuilder;
        _SpheneConfigService = SpheneConfigService;
        _windowSystem = windowSystem;
        _uiFactory = uiFactory;
        _fileDialogManager = fileDialogManager;
        _framework = framework;

        _uiBuilder.DisableGposeUiHide = true;
        _uiBuilder.Draw += Draw;
        _uiBuilder.OpenConfigUi += ToggleUi;
        _uiBuilder.OpenMainUi += ToggleMainUi;

        _logger.LogDebug("UiService received {count} WindowMediatorSubscriberBase instances", windows.Count());
        foreach (var window in windows)
        {
            _logger.LogDebug("Adding window to WindowSystem: {WindowType}", window.GetType().Name);
            if (string.Equals(window.GetType().Name, "CitySyncshellExplanationUI", StringComparison.Ordinal))
            {
                _logger.LogDebug("CitySyncshellExplanationUI instance found and being added to WindowSystem");
            }
            _windowSystem.AddWindow(window);
        }

        Mediator.Publish(new UiServiceInitializedMessage());

        // Mediator messages are processed on the mediator queue thread, while
        // WindowSystem.Draw() enumerates its window lists on the draw thread.
        // AddWindow/RemoveWindow must therefore be marshalled onto the
        // framework thread so they can never race with a draw in progress.
        Mediator.Subscribe<OpenSyncshellAdminPanel>(this, (msg) =>
        {
            _ = _framework.RunOnFrameworkThread(() =>
            {
                if (_disposed) return;
                var window = _createdWindows.OfType<SyncshellAdminUI>().FirstOrDefault(ui =>
                    string.Equals(ui.GroupFullInfo.GID, msg.GroupInfo.GID, StringComparison.Ordinal));
                if (window == null)
                {
                    window = _uiFactory.CreateSyncshellAdminUi(msg.GroupInfo);
                    _createdWindows.Add(window);
                    _windowSystem.AddWindow(window);
                }
                window.IsOpen = true;
            });
        });

        Mediator.Subscribe<OpenPermissionWindow>(this, (msg) =>
        {
            _ = _framework.RunOnFrameworkThread(() =>
            {
                if (_disposed) return;
                if (!_createdWindows.Exists(p => p is PermissionWindowUI ui
                    && msg.Pair == ui.Pair))
                {
                    var window = _uiFactory.CreatePermissionPopupUi(msg.Pair);
                    _createdWindows.Add(window);
                    _windowSystem.AddWindow(window);
                }
            });
        });

        Mediator.Subscribe<RemoveWindowMessage>(this, (msg) =>
        {
            _ = _framework.RunOnFrameworkThread(() =>
            {
                if (_disposed) return;
                _windowSystem.RemoveWindow(msg.Window);
                _createdWindows.Remove(msg.Window);
                msg.Window.Dispose();
            });
        });

        Mediator.Subscribe<OpenSyncshellCommunityMessage>(this, (msg) =>
        {
            _ = _framework.RunOnFrameworkThread(() =>
            {
                if (_disposed) return;
                // Queued opens carry their request's validity - a stale one
                // (profile switched while the fetch was in flight) is dropped
                // here, not at publish time.
                if (msg.IsStillValid?.Invoke() == false) return;
                _logger.LogDebug("Creating syncshell community UI with GroupFullInfo: {GroupAliasOrGID}", msg.GroupFullInfo?.Group.AliasOrGID);
                var window = _uiFactory.CreateSyncshellCommunityUi(msg.Community, msg.GroupFullInfo,
                    msg.JoinPopup, msg.PreviewAsVisitor);
                _createdWindows.Add(window);
                _windowSystem.AddWindow(window);
                window.IsOpen = true;
            });
        });
    }

    public void ToggleMainUi()
    {
        if (_SpheneConfigService.Current.HasValidSetup())
            Mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
        else
            Mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
    }

    public void ToggleUi()
    {
        if (_SpheneConfigService.Current.HasValidSetup())
            Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
        else
            Mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _logger.LogTrace("Disposing {type}", GetType().Name);

        _disposed = true;
        _windowSystem.RemoveAllWindows();

        foreach (var window in _createdWindows)
        {
            window.Dispose();
        }

        _uiBuilder.Draw -= Draw;
        _uiBuilder.OpenConfigUi -= ToggleUi;
        _uiBuilder.OpenMainUi -= ToggleMainUi;
    }

    private void Draw()
    {
        _windowSystem.Draw();
        _fileDialogManager.Draw();
    }
}
