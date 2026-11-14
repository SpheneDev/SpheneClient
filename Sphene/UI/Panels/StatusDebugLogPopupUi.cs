using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI;
using Sphene.UI.Design;
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
        SpheneUI.Layout.PageHeader("Communication Log", "commlog", () =>
        {
            SpheneUI.Text.MutedWrapped("Raw log of server communication - for troubleshooting sync issues.", ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });

        if (SpheneUI.Layout.BeginScrollArea("debug-log-content", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            _statusDebugUi.DrawLogsPanel(showPopupButton: false);
        }
        SpheneUI.Layout.EndScrollArea();
    }
}
