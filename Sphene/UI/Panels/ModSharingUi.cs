using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using System.Numerics;

namespace Sphene.UI.Panels;

public sealed class ModSharingUi : WindowMediatorSubscriberBase
{
    private readonly PenumbraSendModUi _penumbraSendModUi;
    private readonly PenumbraReceiveModUi _penumbraReceiveModUi;
    private readonly ModPackageHistoryUi _modPackageHistoryUi;

    private ModSharingTab? _selectTabOnNextDraw;

    public ModSharingUi(
        ILogger<ModSharingUi> logger,
        SpheneMediator mediator,
        PenumbraSendModUi penumbraSendModUi,
        PenumbraReceiveModUi penumbraReceiveModUi,
        ModPackageHistoryUi modPackageHistoryUi,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Mod Sharing###SpheneModSharing", performanceCollectorService)
    {
        _penumbraSendModUi = penumbraSendModUi;
        _penumbraReceiveModUi = penumbraReceiveModUi;
        _modPackageHistoryUi = modPackageHistoryUi;

        Size = new Vector2(1050f, 750f);
        SizeCondition = ImGuiCond.FirstUseEver;

        Mediator.Subscribe<OpenModSharingWindow>(this, msg =>
        {
            _selectTabOnNextDraw = msg.Tab;
            IsOpen = true;
        });

        Mediator.Subscribe<OpenSendPenumbraModWindow>(this, _ =>
        {
            _selectTabOnNextDraw = ModSharingTab.Send;
            IsOpen = true;
        });

        Mediator.Subscribe<OpenPenumbraReceiveModWindow>(this, _ =>
        {
            _selectTabOnNextDraw = ModSharingTab.Receive;
            IsOpen = true;
        });

        IsOpen = false;
    }

    protected override void DrawInternal()
    {
        var requested = _selectTabOnNextDraw;
        _selectTabOnNextDraw = null;

        UiSharedService.TextWrapped("Share Penumbra mods with paired users, install received packages, and manage history & backups.");
        ImGui.Separator();

        using var tabBar = ImRaii.TabBar("ModSharingTabs");
        if (!tabBar)
        {
            return;
        }

        using (var sendTab = ImRaii.TabItem("Send Mods", requested == ModSharingTab.Send ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
        {
            if (sendTab)
            {
                ImGui.Spacing();
                _penumbraSendModUi.DrawEmbedded();
            }
        }

        using (var receiveTab = ImRaii.TabItem("Receive Mods", requested == ModSharingTab.Receive ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
        {
            if (receiveTab)
            {
                ImGui.Spacing();
                _penumbraReceiveModUi.DrawEmbedded();
            }
        }

        using (var historyTab = ImRaii.TabItem("Mod Packages", requested == ModSharingTab.History ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
        {
            if (historyTab)
            {
                ImGui.Spacing();
                _modPackageHistoryUi.DrawEmbedded();
            }
        }
    }
}
