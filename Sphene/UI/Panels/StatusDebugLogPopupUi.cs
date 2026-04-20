using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI;
using System.Numerics;

namespace Sphene.UI.Panels;

public sealed class StatusDebugLogPopupUi : WindowMediatorSubscriberBase
{
    private readonly StatusDebugUi _statusDebugUi;

    public StatusDebugLogPopupUi(ILogger<StatusDebugLogPopupUi> logger,
        SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        StatusDebugUi statusDebugUi)
        : base(logger, mediator, "Communication Log###CommunicationLogPopup", performanceCollectorService)
    {
        _statusDebugUi = statusDebugUi;
        IsOpen = false;
        Size = new Vector2(900, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.None;
    }

    protected override void DrawInternal()
    {
        _statusDebugUi.DrawLogsPanel(showPopupButton: false);
    }
}
