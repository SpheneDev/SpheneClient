using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Components;
using Sphene.UI.Design;
using Sphene.WebAPI;
using System.Numerics;

namespace Sphene.UI.Panels;

public sealed class PairRequestsUi : WindowMediatorSubscriberBase
{
    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;

    public PairRequestsUi(ILogger<PairRequestsUi> logger,
        SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        PairManager pairManager,
        ApiController apiController)
        : base(logger, mediator, "Pair Requests###SphenePairRequests", performanceCollectorService)
    {
        _pairManager = pairManager;
        _apiController = apiController;
        Size = new Vector2(400, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 200),
        };

        IsOpen = false;

        Mediator.Subscribe<OpenPairRequestsUiMessage>(this, (_) =>
        {
            _pairManager.MarkAllInboundIndividualPairRequestsSeen();
            IsOpen = true;
        });
    }

    protected override FontAwesomeIcon? ChromeIcon => FontAwesomeIcon.UserPlus;

    protected override void DrawInternal()
    {
        SpheneUI.Layout.PageHeader("Pair Requests", "pairrequests", () =>
        {
            SpheneUI.Text.MutedWrapped("Incoming and outgoing pair requests - accept or decline inline.", ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });

        if (!PairRequestsSection.HasPendingRequests(_pairManager))
        {
            SpheneUI.Layout.Callout(
                "No pending pair requests right now.",
                FontAwesomeIcon.UserPlus, SpheneUI.Palette.CrystalBlue,
                title: "All caught up");
            return;
        }

        if (SpheneUI.Layout.BeginFillToBottomScrollArea("##PairRequestsList", border: true))
        {
            PairRequestsSection.Draw(_pairManager, _apiController, Mediator);
        }
        SpheneUI.Layout.EndScrollArea();
    }
}
