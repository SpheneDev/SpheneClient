using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.Events;
using Sphene.UI.Design;
using Sphene.SpheneConfiguration;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Sphene.UI.Components.Popup;

public class PopupHandler : WindowMediatorSubscriberBase
{
    protected bool _openPopup = false;
    private readonly HashSet<IPopupHandler> _handlers;
    private IPopupHandler? _currentHandler = null;

    public PopupHandler(ILogger<PopupHandler> logger, SpheneMediator mediator, IEnumerable<IPopupHandler> popupHandlers,
        PerformanceCollectorService performanceCollectorService, SpheneConfigService configService)
        : base(logger, mediator, "SphenePopupHandler", performanceCollectorService)
    {
        Flags = ImGuiWindowFlags.NoBringToFrontOnFocus
          | ImGuiWindowFlags.NoDecoration
          | ImGuiWindowFlags.NoInputs
          | ImGuiWindowFlags.NoSavedSettings
          | ImGuiWindowFlags.NoBackground
          | ImGuiWindowFlags.NoMove
          | ImGuiWindowFlags.NoNav
          | ImGuiWindowFlags.NoTitleBar
          | ImGuiWindowFlags.NoFocusOnAppearing;

        IsOpen = true;

        _handlers = popupHandlers.ToHashSet();

        Mediator.Subscribe<OpenBanUserPopupMessage>(this, (msg) =>
        {
            _openPopup = true;
            _currentHandler = _handlers.OfType<BanUserPopupHandler>().Single();
            ((BanUserPopupHandler)_currentHandler).Open(msg);
            IsOpen = true;
        });

        Mediator.Subscribe<OpenCensusPopupMessage>(this, (msg) =>
        {
            _openPopup = true;
            _currentHandler = _handlers.OfType<CensusPopupHandler>().Single();
            IsOpen = true;
        });
        DisableWindowSounds = true;
    }

    protected override bool UseSpheneChrome => false;

    protected override void DrawInternal()
    {
        if (_currentHandler == null) return;

        if (_openPopup)
        {
            var size = _currentHandler.PopupSize * ImGuiHelpers.GlobalScale;
            ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);

            var viewportSize = ImGui.GetWindowViewport().Size;
            ImGui.SetNextWindowPos(viewportSize / 2, ImGuiCond.Appearing, new Vector2(0.5f));

            ImGui.OpenPopup(WindowName);
            _openPopup = false;
        }
        if (SpheneUI.Popups.BeginStyled(WindowName))
        {
            _currentHandler.DrawContent();
            if (_currentHandler.ShowClose)
            {
                SpheneUI.Layout.Separator();
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Close", null, false, ButtonStyleKeys.Popup_Close))
                {
                    ImGui.CloseCurrentPopup();
                }
            }
            SpheneUI.Popups.EndStyled();
        }
    }
}
