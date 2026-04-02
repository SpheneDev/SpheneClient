using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Theme;
using Sphene.WebAPI;
using System.Numerics;

namespace Sphene.UI.Panels;

public sealed class PairRequestsUi : WindowMediatorSubscriberBase
{
    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private IDisposable? _themeScope;

    public PairRequestsUi(ILogger<PairRequestsUi> logger,
        SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        PairManager pairManager,
        ApiController apiController,
        UiSharedService uiSharedService)
        : base(logger, mediator, "Pair Requests###SphenePairRequests", performanceCollectorService)
    {
        _pairManager = pairManager;
        _apiController = apiController;
        _uiSharedService = uiSharedService;

        Size = new Vector2(360, 260);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoDocking;

        IsOpen = false;

        Mediator.Subscribe<OpenPairRequestsUiMessage>(this, (_) =>
        {
            _pairManager.MarkAllInboundIndividualPairRequestsSeen();
            IsOpen = true;
        });
    }

    public override void PreDraw()
    {
        _themeScope = SpheneCustomTheme.ApplyThemeWithOriginalRadius();

        if (!SpheneCustomTheme.CurrentTheme.CompactShowImGuiHeader)
        {
            Flags |= ImGuiWindowFlags.NoTitleBar;
        }
        else
        {
            Flags &= ~ImGuiWindowFlags.NoTitleBar;
        }

        base.PreDraw();
    }

    public override void PostDraw()
    {
        _themeScope?.Dispose();
        _themeScope = null;
        base.PostDraw();
    }

    protected override void DrawInternal()
    {
        using var font = ImRaii.PushFont(UiBuilder.DefaultFont);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Pair Requests");
        ImGui.SameLine();
        var closeButtonWidth = 90f * ImGuiHelpers.GlobalScale;
        var availX = ImGui.GetContentRegionAvail().X;
        if (availX > closeButtonWidth)
        {
            var shift = 10f * ImGuiHelpers.GlobalScale;
            var targetX = ImGui.GetCursorPosX() + MathF.Max(0, (availX - closeButtonWidth) - shift);
            ImGui.SameLine(targetX);
        }
        if (ImGui.Button("Close", new Vector2(closeButtonWidth, 0)))
        {
            IsOpen = false;
            return;
        }
        ImGui.Separator();

        var incoming = _pairManager.GetInboundIndividualPairRequestsSnapshot();
        var outgoing = _pairManager.GetOutboundIndividualPairRequestsSnapshot();

        if (incoming.Count == 0 && outgoing.Count == 0)
        {
            ImGuiHelpers.ScaledDummy(2f);
            ImGui.TextUnformatted("No pending pair requests.");
            return;
        }

        using var child = ImRaii.Child("##PairRequestsList", new Vector2(0, 0), true);
        if (!child)
        {
            return;
        }

        if (incoming.Count > 0)
        {
            ImGui.TextUnformatted("Incoming");
            ImGui.Separator();
            foreach (var pair in incoming)
            {
                DrawRequestRow(pair);
            }
        }

        if (outgoing.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Outgoing");
            ImGui.Separator();
            foreach (var pair in outgoing)
            {
                DrawOutgoingRow(pair);
            }
        }
    }

    private void DrawRequestRow(Pair pair)
    {
        var uid = pair.UserData.UID;
        if (string.IsNullOrWhiteSpace(uid))
        {
            return;
        }

        var rowStart = ImGui.GetCursorPos();
        var unseen = _pairManager.IsInboundIndividualPairRequestUnseen(uid);
        if (unseen)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "New");
            ImGui.SameLine();
        }

        var displayName = !string.IsNullOrEmpty(pair.PlayerName) ? pair.PlayerName : pair.UserData.AliasOrUID;
        ImGui.TextUnformatted(displayName);
        var iconButtonSize = ImGui.GetFrameHeight();
        var totalIcons = 3;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rowIconsWidth = (iconButtonSize * totalIcons) + (spacing * (totalIcons - 1));
        var shift = 10f * ImGuiHelpers.GlobalScale;
        var regionMaxX = ImGui.GetWindowContentRegionMax().X;
        var startX = MathF.Max(rowStart.X, regionMaxX - rowIconsWidth - shift);
        ImGui.SetCursorPos(new Vector2(startX, rowStart.Y));

        if (_uiSharedService.IconButton(FontAwesomeIcon.User))
        {
            Mediator.Publish(new ProfileOpenStandaloneMessage(pair));
        }
        UiSharedService.AttachToolTip("Open Profile");
        ImGui.SameLine();

        if (_uiSharedService.IconButton(FontAwesomeIcon.Check))
        {
            _pairManager.MarkInboundIndividualPairRequestSeen(uid);
            _ = _apiController.UserAddPair(new(new(uid)));
        }
        UiSharedService.AttachToolTip("Accept Pair");
        ImGui.SameLine();

        if (_uiSharedService.IconButton(FontAwesomeIcon.Times))
        {
            _pairManager.DeclineInboundIndividualPairRequest(uid);
        }
        UiSharedService.AttachToolTip("Decline Pair");
    }

    private void DrawOutgoingRow(Pair pair)
    {
        var uid = pair.UserData.UID;
        if (string.IsNullOrWhiteSpace(uid))
        {
            return;
        }

        var rowStart = ImGui.GetCursorPos();
        var displayName = !string.IsNullOrEmpty(pair.PlayerName) ? pair.PlayerName : pair.UserData.AliasOrUID;
        ImGui.TextUnformatted(displayName);
        var iconButtonSize = ImGui.GetFrameHeight();
        var totalIcons = 2;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rowIconsWidth = (iconButtonSize * totalIcons) + (spacing * (totalIcons - 1));
        var shift = 10f * ImGuiHelpers.GlobalScale;
        var regionMaxX = ImGui.GetWindowContentRegionMax().X;
        var startX = MathF.Max(rowStart.X, regionMaxX - rowIconsWidth - shift);
        ImGui.SetCursorPos(new Vector2(startX, rowStart.Y));

        if (_uiSharedService.IconButton(FontAwesomeIcon.User))
        {
            Mediator.Publish(new ProfileOpenStandaloneMessage(pair));
        }
        UiSharedService.AttachToolTip("Open Profile");
        ImGui.SameLine();

        if (_uiSharedService.IconButton(FontAwesomeIcon.Times))
        {
            _ = _apiController.UserRemovePair(new(new(uid)));
        }
        UiSharedService.AttachToolTip("Cancel Request");
    }

}
