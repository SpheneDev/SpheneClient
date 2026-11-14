using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Design;
using Sphene.UI.Panels;
using Sphene.WebAPI;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Shared pending-items list behind the icon notification bubble and the
/// hub "Pending" card: live pair requests (accept / decline inline) plus
/// the <see cref="NotificationInboxService"/> entries (mod transfers,
/// updates, area-bound consents, notifications) with per-kind actions.
/// Renders nothing when empty  ·  hosts hide their card then.
/// </summary>
internal static class NotificationInbox
{
    /// <summary>Total pending count for badges (inbox items + pair requests).</summary>
    public static int PendingCount(NotificationInboxService inbox, PairManager pairManager)
        => inbox.Count + PairRequestsSection.PendingCount(pairManager);

    /// <summary>Draws everything pending. Returns false when nothing was shown.</summary>
    public static bool Draw(NotificationInboxService inbox, PairManager pairManager,
        ApiController apiController, SpheneMediator mediator)
    {
        var hasRequests = PairRequestsSection.HasPendingRequests(pairManager);
        var items = inbox.GetItems();
        if (!hasRequests && items.Count == 0)
        {
            return false;
        }

        if (hasRequests)
        {
            PairRequestsSection.Draw(pairManager, apiController, mediator);
            if (items.Count > 0)
            {
                SpheneUI.Layout.Separator();
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            DrawItem(items[i], inbox, mediator);
        }
        return true;
    }

    private static void DrawItem(InboxItem item, NotificationInboxService inbox,
        SpheneMediator mediator)
    {
        var scale = SpheneUI.Scale;
        var icon = item.Kind switch
        {
            InboxKind.ModTransfer => FontAwesomeIcon.FileDownload,
            InboxKind.ModTransferDone => FontAwesomeIcon.CheckCircle,
            InboxKind.Update => FontAwesomeIcon.ArrowCircleUp,
            InboxKind.AreaBoundConsent => FontAwesomeIcon.MapMarkedAlt,
            InboxKind.Chat => FontAwesomeIcon.CommentDots,
            _ => FontAwesomeIcon.Bell,
        };
        var tint = item.Kind switch
        {
            InboxKind.Update => SpheneUI.Palette.NetworkActive,
            InboxKind.ModTransferDone => SpheneUI.Palette.CrystalBlue,
            InboxKind.AreaBoundConsent => SpheneUI.Palette.EtherealPurple,
            InboxKind.Chat => SpheneUI.Palette.SpheneGold,
            _ => SpheneUI.Palette.SpheneGold,
        };

        ImGui.PushID(item.Id);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var pad = SpheneUI.Spacing.X8.Scaled;
        var act = ActionFor(item);
        var actionsW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Times).X
            + (act != null
                ? SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ArrowRight).X + spacing
                : 0f);
        var iconW = ImGui.GetTextLineHeight();
        var hasDetail = !string.IsNullOrWhiteSpace(item.Detail);
        var rowW = SpheneUI.Controls.BoundedAvailX();
        var min = ImGui.GetCursorScreenPos();
        var textX = min.X + pad + iconW + 4f * scale;
        var textW = min.X + rowW - pad - actionsW - spacing - textX;

        // clicked rows expand to the full detail · action rows keep their
        // click for the action itself.
        var expanded = Expanded.Contains(item.Id);
        var detailH = hasDetail
            ? (expanded
                ? ImGui.CalcTextSize(item.Detail!, false, textW).Y
                : iconW)
            : 0f;
        var rowH = iconW + (hasDetail ? detailH + 3f * scale : 0) + pad * 2f;

        // Card behind the item · separates entries visually inside the inbox
        var dl = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(rowW, rowH));
        var hovered = ImGui.IsItemHovered();
        // IsItemClicked on the ID-0 dummy fires on mouse-down via rect test,
        // while the icon buttons return on mouse-up - so a frame-level flag
        // can never mask button hits. Instead the button strip is simply not
        // part of the row's click zone: clicks at/inside the action buttons
        // only ever reach those buttons.
        var rowClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left)
            && ImGui.GetIO().MousePos.X < min.X + rowW - pad - actionsW;
        var radius = 8f * scale;
        var max = min + new Vector2(rowW, rowH);
        dl.AddRectFilled(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.Layout.NavRailBandColor()), radius);
        dl.AddRect(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                hovered ? tint
                        : item.Important ? SpheneUI.Palette.SpheneGold
                        : SpheneUI.Colors.Border,
                hovered || item.Important ? 0.55f : 0.3f)), radius);

        // left text column: title + detail, vertically centered as a block
        var titleY = hasDetail
            ? min.Y + pad + 1f * scale
            : min.Y + (rowH - iconW) * 0.5f;
        dl.AddText(new Vector2(textX, titleY),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary),
            SpheneUI.Text.Ellipsize(item.Title, textW));
        if (hasDetail && !expanded)
        {
            dl.AddText(new Vector2(textX, titleY + iconW + 3f * scale),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
                SpheneUI.Text.Ellipsize(item.Detail!, textW));
        }

        // kind icon in the left pad column, vertically centered
        var glyphStr = FontAwesomeExtensions.ToIconString(icon);
        var gi = ImGui.CalcTextSize(glyphStr);
        dl.AddText(UiBuilder.IconFont, gi.Y,
            min + new Vector2(pad + (iconW - gi.X) * 0.5f,
                (rowH - gi.Y) * 0.5f),
            SpheneUI.ColorUtils.ToU32(tint), glyphStr);

        // expanded rows draw the full detail, wrapped into the text column
        if (hasDetail && expanded)
        {
            var detailPos = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(
                new Vector2(textX, titleY + iconW + 3f * scale));
            SpheneUI.Text.MutedWrapped(item.Detail!,
                textX + textW - ImGui.GetWindowPos().X);
            ImGui.SetCursorScreenPos(detailPos);
        }

        // action buttons · docked to the right edge, vertically centered.
        // The → button only renders when the item actually has an action -
        // a dead disabled button reads as a bug; the row itself is the
        // click target for actionable items.
        var rowEnd = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(
            min.X + rowW - pad - actionsW,
            min.Y + (rowH - ImGui.GetFrameHeight()) * 0.5f));
        if (act != null)
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowRight))
            {
                act.Invoke(mediator, inbox, item);
            }
            SpheneUI.AttachTooltip(TooltipFor(item.Kind));
            ImGui.SameLine(0f, spacing);
        }
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
        {
            inbox.Dismiss(item.Id);
        }
        SpheneUI.AttachTooltip("Dismiss");

        // whole row activates the action · no-action rows expand instead
        if (act != null && rowClicked)
        {
            act.Invoke(mediator, inbox, item);
        }
        else if (act == null && rowClicked && hasDetail && !Expanded.Remove(item.Id))
        {
            Expanded.Add(item.Id);
        }
        if (hovered && (act != null || hasDetail))
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            SpheneUI.AttachTooltip(act == null
                ? (expanded ? "Click to collapse" : "Click to read the full message")
                : TooltipFor(item.Kind));
        }
        // the button draw rewound the cursor into the card · put it back at
        // the row end or the next item renders on top of this one.
        ImGui.SetCursorScreenPos(rowEnd);
        ImGui.PopID();
    }

    /// <summary>Rows the user unfolded to read the full detail text.</summary>
    private static readonly HashSet<string> Expanded = new(StringComparer.Ordinal);

    private static string TooltipFor(InboxKind kind) => kind switch
    {
        InboxKind.ModTransfer or InboxKind.ModTransferDone => "Open Downloads",
        InboxKind.Update => "View update",
        InboxKind.AreaBoundConsent => "Review consent",
        InboxKind.Chat => "Open chat",
        _ => "Open",
    };

    private static Action<SpheneMediator, NotificationInboxService, InboxItem>? ActionFor(
        InboxItem item) => item.Kind switch
    {
        InboxKind.ModTransfer or InboxKind.ModTransferDone =>
            (m, inbox, i) => { m.Publish(new UiToggleMessage(typeof(DownloadUi))); inbox.Dismiss(i.Id); },
        InboxKind.Update =>
            (m, inbox, i) => { m.Publish(new UiToggleMessage(typeof(UpdateNotificationUi))); inbox.Dismiss(i.Id); },
        InboxKind.AreaBoundConsent =>
            (m, inbox, i) =>
            {
                if (i.Payload is SameThreadMessage msg)
                {
                    m.Publish(msg);
                }
                inbox.Dismiss(i.Id);
            },
        InboxKind.Chat => item.Payload is Pair chatPair
            ? (m, inbox, i) =>
            {
                m.Publish(new OpenSpheneHubMessage(SpheneHubPage.Friends,
                    null, chatPair, FriendSub: FriendHubSubPage.Chat));
                inbox.Dismiss(i.Id);
            }
            : null,
        InboxKind.Server => ServerActionFor(item),
        _ => null,
    };

    /// <summary>Deep-links a durable server notification into the hub -
    /// community content rows land on the shell's community page opened at
    /// the notified item; everything else opens the community page when a
    /// group is attached. Rows without any target dismiss only.</summary>
    private static Action<SpheneMediator, NotificationInboxService, InboxItem>? ServerActionFor(
        InboxItem item)
    {
        if (item.Payload is not UserNotificationDto dto) return null;
        var itemId = long.TryParse(dto.ContentId, out var parsed) ? parsed : (long?)null;
        SyncshellCommunitySection? section = dto.ContentKind switch
        {
            "news" => SyncshellCommunitySection.News,
            "event" => SyncshellCommunitySection.Events,
            // blog/comment/report rows have no hub-surface target - landing
            // on the community page itself is the honest destination.
            _ => null,
        };
        if (string.IsNullOrEmpty(dto.GroupGID)) return null;
        return (m, inbox, i) =>
        {
            m.Publish(new OpenSpheneHubMessage(GroupGid: dto.GroupGID,
                CommunitySection: section, CommunityItemId: section == null ? null : itemId));
            inbox.Dismiss(i.Id);
        };
    }
}
