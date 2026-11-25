using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;
using Sphene.UI.Design;
using Sphene.WebAPI;

namespace Sphene.UI.Components;

/// <summary>
/// Renders the pending individual pair requests (incoming and outgoing)  · 
/// the shared content behind the standalone <c>PairRequestsUi</c> window and
/// the Friends page card in the Sphene Hub. Draws rows only; hosts decide
/// how the section is framed (scroll area in the window, card in the hub).
/// </summary>
internal static class PairRequestsSection
{
    /// <summary>True when inbound or outbound requests are waiting.</summary>
    public static bool HasPendingRequests(PairManager pairManager)
        => pairManager.GetInboundIndividualPairRequestsSnapshot().Count > 0
            || pairManager.GetOutboundIndividualPairRequestsSnapshot().Count > 0;

    /// <summary>Pending request count (inbound + outbound) for badges.</summary>
    public static int PendingCount(PairManager pairManager)
        => pairManager.GetInboundIndividualPairRequestsSnapshot().Count
            + pairManager.GetOutboundIndividualPairRequestsSnapshot().Count;

    /// <summary>Draws both request groups and their action rows.</summary>
    public static void Draw(PairManager pairManager, ApiController apiController,
        SpheneMediator mediator)
    {
        var incoming = pairManager.GetInboundIndividualPairRequestsSnapshot();
        var outgoing = pairManager.GetOutboundIndividualPairRequestsSnapshot();

        if (incoming.Count == 0 && outgoing.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.UserCheck, "No pending requests",
                "Incoming and outgoing pair requests land here.");
            return;
        }

        if (incoming.Count > 0)
        {
            SpheneUI.Text.SectionHeader("Incoming");
            foreach (var pair in incoming)
            {
                DrawRequestRow(pair, pairManager, apiController, mediator);
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
                DrawOutgoingRow(pair, apiController, mediator);
            }
        }
    }

    internal static void DrawRequestRow(Pair pair, PairManager pairManager,
        ApiController apiController, SpheneMediator mediator)
    {
        var uid = pair.UserData.UID;
        if (string.IsNullOrWhiteSpace(uid))
        {
            return;
        }

        var iconButtonSize = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rowIconsWidth = iconButtonSize * 3f + spacing * 2f;

        if (pairManager.IsInboundIndividualPairRequestUnseen(uid))
        {
            SpheneUI.Layout.Badge("New", ImGuiColors.DalamudYellow);
            ImGui.SameLine();
        }

        var displayName = !string.IsNullOrEmpty(pair.PlayerName)
            ? pair.PlayerName : pair.UserData.PreferredName;
        var nameWidth = Math.Max(1f, SpheneUI.Controls.BoundedAvailX() - rowIconsWidth - spacing);
        SpheneUI.Text.Plain(SpheneUI.Text.Ellipsize(displayName, nameWidth));
        SpheneUI.AttachTooltip(displayName);

        SpheneUI.Controls.DockToCardEdge(rowIconsWidth);

        if (SpheneUI.Icons.Button(FontAwesomeIcon.User))
        {
            mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Friends, Friend: pair));
        }
        SpheneUI.AttachTooltip("Open Profile");
        ImGui.SameLine();

        if (SpheneUI.Icons.Button(FontAwesomeIcon.Check))
        {
            pairManager.MarkInboundIndividualPairRequestSeen(uid);
            _ = apiController.UserAddPair(new(new(uid)));
        }
        SpheneUI.AttachTooltip("Accept Pair");
        ImGui.SameLine();

        if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
        {
            pairManager.DeclineInboundIndividualPairRequest(uid);
        }
        SpheneUI.AttachTooltip("Decline Pair");
    }

    private static void DrawOutgoingRow(Pair pair, ApiController apiController,
        SpheneMediator mediator)
    {
        var uid = pair.UserData.UID;
        if (string.IsNullOrWhiteSpace(uid))
        {
            return;
        }

        var iconButtonSize = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rowIconsWidth = iconButtonSize * 2f + spacing;

        var displayName = !string.IsNullOrEmpty(pair.PlayerName)
            ? pair.PlayerName : pair.UserData.PreferredName;
        SpheneUI.Text.Plain(displayName);

        SpheneUI.Controls.DockToCardEdge(rowIconsWidth);

        if (SpheneUI.Icons.Button(FontAwesomeIcon.User))
        {
            mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Friends, Friend: pair));
        }
        SpheneUI.AttachTooltip("Open Profile");
        ImGui.SameLine();

        if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
        {
            _ = apiController.UserRemovePair(new(new(uid)));
        }
        SpheneUI.AttachTooltip("Cancel Request");
    }
}
