using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.SpheneConfiguration;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public sealed class ModSharingUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly SpheneConfigService _configService;
    private readonly PenumbraSendModUi _penumbraSendModUi;
    private readonly PenumbraReceiveModUi _penumbraReceiveModUi;
    private readonly ModPackageHistoryUi _modPackageHistoryUi;

    private ModSharingTab? _selectTabOnNextDraw;

    public ModSharingUi(
        ILogger<ModSharingUi> logger,
        SpheneMediator mediator,
        ApiController apiController,
        SpheneConfigService configService,
        PenumbraSendModUi penumbraSendModUi,
        PenumbraReceiveModUi penumbraReceiveModUi,
        ModPackageHistoryUi modPackageHistoryUi,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Mod Sharing###SpheneModSharing", performanceCollectorService)
    {
        _apiController = apiController;
        _configService = configService;
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

        if (SpheneUI.Layout.BeginScrollArea("mod-sharing-content", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            DrawSharingContent(requested);
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawSharingContent(ModSharingTab? requested)
    {
        SpheneUI.Text.PageHeader("Mod Sharing", "Share Penumbra mods with paired users, install received packages, and manage history & backups.");

        var allowPenumbraMods = _configService.Current.AllowReceivingPenumbraMods;
        if (SpheneUI.Controls.Toggle("Allow receiving Penumbra mod packages", ref allowPenumbraMods))
        {
            _configService.Current.AllowReceivingPenumbraMods = allowPenumbraMods;
            _configService.Save();
            _ = _apiController.UserUpdatePenumbraReceivePreference(allowPenumbraMods);
        }
        SpheneUI.Controls.HelpText("When disabled, incoming Penumbra mod packages are ignored and no install popups are shown.");

        SpheneUI.Layout.Separator();

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
                if (!allowPenumbraMods)
                {
                    SpheneUI.Text.Muted("Receiving mod packages is currently disabled.");
                    ImGui.Spacing();
                }
                using (ImRaii.Disabled(!allowPenumbraMods))
                {
                    _penumbraReceiveModUi.DrawEmbedded();
                }
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
