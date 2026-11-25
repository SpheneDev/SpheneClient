using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Data;
using Sphene.API.Dto.Group;
using Sphene.UI.Syncshell;
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
/// </summary>
internal static class NotificationInbox
{
    /// <summary>Total pending count for badges (inbox items + pair requests).</summary>
    public static int PendingCount(NotificationInboxService inbox, PairManager pairManager)
        => inbox.Count + pairManager.GetInboundIndividualPairRequestsSnapshot().Count;

    public static float Width => 400f * SpheneUI.Scale;

    public static void DrawPanel(string flyout, NotificationInboxService inbox, PairManager pairManager,
        ApiController apiController, SpheneMediator mediator, SpheneProfileManager profiles, Action? back = null)
    {
        if (back != null)
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowLeft)) back();
            SpheneUI.AttachTooltip("Back to menu");
            ImGui.SameLine();
        }
        SpheneUI.Text.Plain("Notifications");
        SpheneUI.Controls.DockToCardEdge(SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.List, "Event log"));
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.List, "Event log"))
        {
            mediator.Publish(new OpenEventLogMessage());
            SpheneUI.Popups.CloseFlyout(flyout);
        }
        SpheneUI.Layout.Separator();
        var requests = pairManager.GetInboundIndividualPairRequestsSnapshot();
        var items = inbox.GetItems();
        if (requests.Count == 0 && items.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.BellSlash, "All caught up", "No new notifications.");
            return;
        }
        var scale = SpheneUI.Scale;
        var height = Math.Min(Math.Min(320f * scale, ImGui.GetIO().DisplaySize.Y * 0.55f),
            (items.Count * 84f + requests.Count * 44f + (requests.Count > 0 ? 40f : 12f)) * scale);
        if (SpheneUI.Layout.BeginScrollArea(flyout + "-rows", new Vector2(0f, height)))
        {
            if (requests.Count > 0)
            {
                SpheneUI.Text.SectionHeader("Pair requests");
                foreach (var pair in requests)
                {
                    ImGui.PushID(pair.UserData.UID);
                    DrawSourceAvatar(profiles, pair.UserData, ImGui.GetFrameHeight(), FontAwesomeIcon.User);
                    ImGui.SameLine();
                    PairRequestsSection.DrawRequestRow(pair, pairManager, apiController, mediator);
                    ImGui.PopID();
                }
                if (items.Count > 0) SpheneUI.Layout.Separator();
            }
            foreach (var item in items) DrawItem(item, inbox, mediator, profiles, pairManager);
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private static void DrawSourceAvatar(SpheneProfileManager profiles, UserData? actor, float size, FontAwesomeIcon fallback)
    {
        var profile = actor == null ? null : profiles.GetAvatarProfile(actor);
        var texture = profile == null ? null : SpheneUI.Media.Texture(
            $"inbox-avatar:{actor!.UID}:{SyncshellCommunityView.MediaVersion(null, profile.Base64ProfilePicture)}",
            () => profile.ImageData.Value);
        SpheneUI.Media.Avatar(texture, size, placeholderIcon: fallback);
    }

    private static void DrawItem(InboxItem item, NotificationInboxService inbox,
        SpheneMediator mediator, SpheneProfileManager profiles, PairManager pairs)
    {
        var scale = SpheneUI.Scale;
        var icon = item.Kind switch
        {
            InboxKind.ModTransfer => FontAwesomeIcon.FileDownload,
            InboxKind.ModTransferDone => FontAwesomeIcon.CheckCircle,
            InboxKind.Update => FontAwesomeIcon.ArrowCircleUp,
            InboxKind.AreaBoundConsent => FontAwesomeIcon.MapMarkedAlt,
            InboxKind.Chat => FontAwesomeIcon.CommentDots,
            InboxKind.Announcement => FontAwesomeIcon.Newspaper,
            InboxKind.Server when item.GroupGid != null => FontAwesomeIcon.Users,
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
        var act = ActionFor(item, pairs);
        var actionsW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Times).X
            + (act != null
                ? SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ArrowRight).X + spacing
                : 0f);
        var lineH = ImGui.GetTextLineHeight();
        var avatarSize = 36f * scale;
        var actor = item.Actor;
        var person = actor == null ? null : SyncshellCommunityView.HumanName(actor)
            ?? profiles.GetPreferredName(actor.UID) ?? pairs.GetPairByUID(actor.UID)?.UserData.PreferredName ?? actor.UID;
        var group = item.GroupGid == null ? null : pairs.GroupPairs.Keys.FirstOrDefault(g =>
            string.Equals(g.Group.GID, item.GroupGid, StringComparison.Ordinal))?.Group.AliasOrGID ?? item.GroupGid;
        var source = person != null && group != null ? $"{person} · {group}"
            : person ?? item.Source ?? group ?? "Sphene";
        var hasDetail = !string.IsNullOrWhiteSpace(item.Detail);
        var rowW = SpheneUI.Controls.BoundedAvailX();
        var min = ImGui.GetCursorScreenPos();
        var textX = min.X + pad + avatarSize + pad;
        var textW = Math.Max(1f, min.X + rowW - pad - actionsW - spacing - textX);

        // clicked rows expand to the full detail · action rows keep their
        // click for the action itself.
        var expanded = Expanded.Contains(item.Id);
        var detailH = hasDetail
            ? (expanded
                ? ImGui.CalcTextSize(item.Detail!, false, textW).Y
                : lineH)
            : 0f;
        var rowH = Math.Max(avatarSize, lineH * 2 + (hasDetail ? detailH + 3f * scale : 0)) + pad * 2f;

        // Card behind the item · separates entries visually inside the inbox
        var dl = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(rowW, rowH));
        var hovered = ImGui.IsItemHovered();
        var visible = ImGui.IsItemVisible();
        var afterRow = ImGui.GetCursorScreenPos();
        // IsItemClicked on the ID-0 dummy fires on mouse-down via rect test,
        // while the icon buttons return on mouse-up - so a frame-level flag
        // can never mask button hits. Instead the button strip is simply not
        // part of the row's click zone: clicks at/inside the action buttons
        // only ever reach those buttons.
        var avatarMin = min + new Vector2(pad, pad);
        var overAvatar = actor != null && hovered && ImGui.IsMouseHoveringRect(avatarMin, avatarMin + new Vector2(avatarSize));
        var rowClicked = !overAvatar && ImGui.IsItemClicked(ImGuiMouseButton.Left)
            && ImGui.GetIO().MousePos.X < min.X + rowW - pad - actionsW;
        var radius = 8f * scale;
        var max = min + new Vector2(rowW, rowH);
        dl.AddRectFilled(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.Layout.NavRailBandColor()), radius);
        dl.AddRect(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                hovered ? tint : SpheneUI.Colors.Border,
                hovered ? 0.55f : 0.3f)), radius);

        // left text column: title + detail, vertically centered as a block
        var titleY = min.Y + pad + lineH;
        dl.AddText(new Vector2(textX, min.Y + pad), SpheneUI.ColorUtils.ToU32(tint),
            SpheneUI.Text.Ellipsize(source, textW));
        dl.AddText(new Vector2(textX, titleY),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary),
            SpheneUI.Text.Ellipsize(item.Title, textW));
        if (hasDetail && !expanded)
        {
            dl.AddText(new Vector2(textX, titleY + lineH + 3f * scale),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
                SpheneUI.Text.Ellipsize(item.Detail!, textW));
        }

        if (visible)
        {
            ImGui.SetCursorScreenPos(min + new Vector2(pad, pad));
            DrawSourceAvatar(profiles, actor, avatarSize, actor == null ? icon : FontAwesomeIcon.User);
            if (overAvatar)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                SpheneUI.AttachTooltip($"Open profile: {person}", true);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    mediator.Publish(new OpenAuthorProfileMessage(actor!.UID));
            }
            ImGui.SetCursorScreenPos(afterRow);
        }

        // expanded rows draw the full detail, wrapped into the text column
        if (hasDetail && expanded)
        {
            var detailPos = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(
                new Vector2(textX, titleY + lineH + 3f * scale));
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
        if (hovered && !overAvatar && (act != null || hasDetail))
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.GetIO().MousePos.X < min.X + rowW - pad - actionsW)
                SpheneUI.AttachTooltip(act == null
                ? (expanded ? "Click to collapse" : "Click to read the full message")
                : TooltipFor(item.Kind), hovered);
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
        InboxKind.Announcement => "Read announcement",
        _ => "Open",
    };

    private static Action<SpheneMediator, NotificationInboxService, InboxItem>? ActionFor(
        InboxItem item, PairManager pairs) => item.Kind switch
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
        InboxKind.Chat => (item.Actor == null ? item.Payload as Pair : pairs.GetPairByUID(item.Actor.UID)) is Pair chatPair
            ? (m, inbox, i) =>
            {
                m.Publish(new OpenSpheneHubMessage(SpheneHubPage.Friends,
                    null, chatPair, FriendSub: FriendHubSubPage.Chat));
                inbox.Dismiss(i.Id);
            }
            : null,
        InboxKind.Announcement when item.Payload is long articleId =>
            (m, inbox, i) => { m.Publish(new OpenSpheneHubMessage(NewsArticleId: articleId)); inbox.Dismiss(i.Id); },
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
        // SubContentId carries the inner target - the comment to focus when
        // the row points into a thread (comments, replies, mentions and
        // comment-target reactions all publish it server-side).
        var commentId = long.TryParse(dto.SubContentId, out var cid) ? cid : (long?)null;
        // Blog targets deep-link into the Chronicle reader on the owner's
        // profile.
        if (string.Equals(dto.ContentKind, "blog", StringComparison.Ordinal) && itemId != null)
        {
            return (m, inbox, i) =>
            {
                m.Publish(new OpenSpheneHubMessage(
                    ChronicleOwnerUid: dto.TargetUid,
                    ChroniclePostId: itemId,
                    FocusCommentId: commentId));
                inbox.Dismiss(i.Id);
            };
        }
        SyncshellCommunitySection? section = dto.ContentKind switch
        {
            "news" => SyncshellCommunitySection.News,
            "event" => SyncshellCommunitySection.Events,
            // comment/report rows have no hub-surface target - landing
            // on the community page itself is the honest destination.
            _ => null,
        };
        if (string.IsNullOrEmpty(dto.GroupGID)) return null;
        return (m, inbox, i) =>
        {
            m.Publish(new OpenSpheneHubMessage(GroupGid: dto.GroupGID,
                CommunitySection: section,
                CommunityItemId: section == null ? null : itemId,
                FocusCommentId: commentId));
            inbox.Dismiss(i.Id);
        };
    }
}
