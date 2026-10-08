using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Design;
using Sphene.WebAPI;
using System.Numerics;

namespace Sphene.UI.Panels;

public sealed class PairRequestsUi : WindowMediatorSubscriberBase
{
    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;
    private IDisposable? _themeScope;

    public PairRequestsUi(ILogger<PairRequestsUi> logger,
        SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        PairManager pairManager,
        ApiController apiController)
        : base(logger, mediator, "Pair Requests###SphenePairRequests", performanceCollectorService)
    {
        _pairManager = pairManager;
        _apiController = apiController;
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

    protected override bool AppliesOwnThemeScope => true;

    public override void PreDraw()
    {
        _themeScope = SpheneUI.Theme.CompactScope();

        if (!SpheneTheme.Current.CompactShowImGuiHeader)
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
        if ((Flags & ImGuiWindowFlags.NoTitleBar) != ImGuiWindowFlags.None)
        {
            SpheneUI.Chrome.TitleBar("pairRequests", "Pair Requests",
                leadingIcon: FontAwesomeIcon.UserPlus,
                rightButtons:
                [
                    new SpheneUI.Chrome.TitleBarButton(FontAwesomeIcon.Times, "Close", () => IsOpen = false),
                ]);
        }

        var incoming = _pairManager.GetInboundIndividualPairRequestsSnapshot();
        var outgoing = _pairManager.GetOutboundIndividualPairRequestsSnapshot();

        if (incoming.Count == 0 && outgoing.Count == 0)
        {
            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Text.Muted("No pending pair requests.");
            return;
        }

        if (!SpheneUI.Layout.BeginFillToBottomScrollArea("##PairRequestsList", border: true))
        {
            SpheneUI.Layout.EndScrollArea();
            return;
        }

        if (incoming.Count > 0)
        {
            SpheneUI.Text.SectionHeader("Incoming");
            foreach (var pair in incoming)
            {
                DrawRequestRow(pair);
            }
        }

        if (outgoing.Count > 0)
        {
            if (incoming.Count > 0)
            {
                SpheneUI.Layout.Separator();
            }
            SpheneUI.Text.SectionHeader("Outgoing");
            foreach (var pair in outgoing)
            {
                DrawOutgoingRow(pair);
            }
        }
        SpheneUI.Layout.EndScrollArea();
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
            SpheneUI.Text.Colored("New", ImGuiColors.DalamudYellow);
            ImGui.SameLine();
        }

        var displayName = !string.IsNullOrEmpty(pair.PlayerName) ? pair.PlayerName : pair.UserData.AliasOrUID;
        SpheneUI.Text.Plain(displayName);
        var iconButtonSize = ImGui.GetFrameHeight();
        var totalIcons = 3;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rowIconsWidth = (iconButtonSize * totalIcons) + (spacing * (totalIcons - 1));
        var shift = 10f * ImGuiHelpers.GlobalScale;
        var regionMaxX = ImGui.GetWindowContentRegionMax().X;
        var startX = MathF.Max(rowStart.X, regionMaxX - rowIconsWidth - shift);
        ImGui.SetCursorPos(new Vector2(startX, rowStart.Y));

        if (SpheneUI.Icons.Button(FontAwesomeIcon.User))
        {
            Mediator.Publish(new ProfileOpenStandaloneMessage(pair));
        }
        SpheneUI.AttachTooltip("Open Profile");
        ImGui.SameLine();

        if (SpheneUI.Icons.Button(FontAwesomeIcon.Check))
        {
            _pairManager.MarkInboundIndividualPairRequestSeen(uid);
            _ = _apiController.UserAddPair(new(new(uid)));
        }
        SpheneUI.AttachTooltip("Accept Pair");
        ImGui.SameLine();

        if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
        {
            _pairManager.DeclineInboundIndividualPairRequest(uid);
        }
        SpheneUI.AttachTooltip("Decline Pair");
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
        SpheneUI.Text.Plain(displayName);
        var iconButtonSize = ImGui.GetFrameHeight();
        var totalIcons = 2;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rowIconsWidth = (iconButtonSize * totalIcons) + (spacing * (totalIcons - 1));
        var shift = 10f * ImGuiHelpers.GlobalScale;
        var regionMaxX = ImGui.GetWindowContentRegionMax().X;
        var startX = MathF.Max(rowStart.X, regionMaxX - rowIconsWidth - shift);
        ImGui.SetCursorPos(new Vector2(startX, rowStart.Y));

        if (SpheneUI.Icons.Button(FontAwesomeIcon.User))
        {
            Mediator.Publish(new ProfileOpenStandaloneMessage(pair));
        }
        SpheneUI.AttachTooltip("Open Profile");
        ImGui.SameLine();

        if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
        {
            _ = _apiController.UserRemovePair(new(new(uid)));
        }
        SpheneUI.AttachTooltip("Cancel Request");
    }

}
