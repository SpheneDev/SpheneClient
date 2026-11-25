using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.User;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;
using Sphene.WebAPI;

namespace Sphene.Services;

/// <summary>
/// Session-scoped pending-items store behind the icon notification bubble
/// and the hub "Pending" card. Subscribes to the same mediator events the
/// icon already tracks plus area-bound consents, keeps a bounded list, and
/// lets rows acknowledge/dismiss individually. Pair requests are NOT stored
/// here  ·  they are queried live from <see cref="PlayerData.Pairs.PairManager"/>
/// so state never desyncs.
/// </summary>
public class NotificationInboxService : DisposableMediatorSubscriberBase, IHostedService
{
    private const string ServerItemPrefix = "srv:";

    private readonly NotificationInboxState _state = new();
    private readonly ApiController _apiController;
    private readonly ChatService _chatService;
    private readonly PairManager _pairManager;

    public NotificationInboxService(ILogger<NotificationInboxService> logger,
        SpheneMediator mediator, ApiController apiController,
        ChatService chatService, PairManager pairManager)
        : base(logger, mediator)
    {
        _apiController = apiController;
        _chatService = chatService;
        _pairManager = pairManager;
        Mediator.Subscribe<PenumbraModTransferAvailableMessage>(this, OnModTransferAvailable);
        Mediator.Subscribe<PenumbraModTransferCompletedMessage>(this, OnModTransferCompleted);
        Mediator.Subscribe<ShowUpdateNotificationMessage>(this, OnUpdateAvailable);
        Mediator.Subscribe<AnnouncementPublishedMessage>(this, msg =>
        {
            var article = msg.Announcement;
            if (!article.IsDraft) _state.Add(new InboxItem($"announcement:{article.Id}", InboxKind.Announcement,
                article.Title, article.Summary, DateTimeOffset.UtcNow, article.Id,
                Source: string.IsNullOrWhiteSpace(article.AuthorDisplay) ? "Sphene News" : article.AuthorDisplay));
        });
        Mediator.Subscribe<AreaBoundSyncshellConsentRequestMessage>(this, OnAreaBoundConsent);
        Mediator.Subscribe<UserNotificationReceivedMessage>(this, OnServerNotification);
        Mediator.Subscribe<ChatMessageReadyMessage>(this, OnChatMessage);
        Mediator.Subscribe<ChatConversationOpenedMessage>(this, OnChatOpened);
        Mediator.Subscribe<ConnectedMessage>(this, msg => _ = SyncServerNotificationsAsync());
        Mediator.Subscribe<DisconnectedMessage>(this, msg => _state.Reset());
    }

    /// <summary>Current items, newest first - a snapshot of the live store.</summary>
    public IReadOnlyList<InboxItem> GetItems() => _state.Snapshot();
    public int Count => _state.Count;

    /// <summary>Removes one item (user dismissed or acted on it). Server
    /// notifications are marked read on the server at the same time.</summary>
    public void Dismiss(string id)
    {
        if (!_state.Remove(id)) return;
        if (id.StartsWith(ServerItemPrefix, StringComparison.Ordinal)
            && long.TryParse(id.AsSpan(ServerItemPrefix.Length), out var serverId))
        {
            _ = _apiController.UserMarkNotificationsRead([serverId]);
        }
    }

    private void OnModTransferAvailable(PenumbraModTransferAvailableMessage msg)
    {
        var name = msg.Notification.ModFolderName ?? msg.Notification.FileName;
        _state.Add(new InboxItem($"mod:{msg.Notification.Hash}", InboxKind.ModTransfer,
            "Incoming mod transfer",
            string.IsNullOrWhiteSpace(name) ? null : name,
            DateTimeOffset.UtcNow, msg.Notification, Actor: msg.Notification.Sender));
    }

    private void OnModTransferCompleted(PenumbraModTransferCompletedMessage msg)
    {
        _state.Remove($"mod:{msg.Notification.Hash}");
        if (msg.Success) return;
        _state.Add(new InboxItem($"moddone:{msg.Notification.Hash}", InboxKind.ModTransferDone,
            "Mod transfer failed", msg.Notification.ModFolderName ?? msg.Notification.FileName, DateTimeOffset.UtcNow, msg.Notification, Actor: msg.Notification.Sender));
    }

    private void OnUpdateAvailable(ShowUpdateNotificationMessage msg)
    {
        if (msg.UpdateInfo?.IsUpdateAvailable != true)
        {
            return;
        }
        _state.Add(new InboxItem("update", InboxKind.Update,
            $"Update available: {msg.UpdateInfo.LatestVersion}",
            msg.UpdateInfo.IsTestBuildUpdate ? "Test build" : null,
            DateTimeOffset.UtcNow, msg.UpdateInfo));
    }

    private void OnServerNotification(UserNotificationReceivedMessage msg)
    {
        var dto = msg.Notification;
        if (dto.IsRead)
        {
            _state.Remove($"{ServerItemPrefix}{dto.Id}");
            return;
        }
        _state.Add(ToInboxItem(dto));
    }

    /// <summary>One row per unread conversation (stable chat:{uid} id) - the
    /// title stays "New message from X", the detail refreshes to the latest
    /// preview. Uses the locally resolved text (post-decryption) and is
    /// skipped while that conversation is on screen.</summary>
    private void OnChatMessage(ChatMessageReadyMessage msg)
    {
        var dto = msg.Entry.Dto;
        var own = _apiController.UID ?? string.Empty;
        if (string.Equals(dto.Sender.UID, own, StringComparison.Ordinal)) return;
        var id = $"chat:{dto.Sender.UID}";
        if (!_chatService.HasUnread(dto.Sender.UID))
        {
            _state.Remove(id);
            return;
        }
        var text = msg.Entry.Gif != null
            ? "[GIF]"
            : msg.Entry.Text ?? "[encrypted message]";
        var preview = text.Length > 80
            ? string.Concat(text.AsSpan(0, 80), "...")
            : text;
        _state.Add(new InboxItem(id, InboxKind.Chat, "New message",
            preview, DateTimeOffset.UtcNow,
            _pairManager.GetPairByUID(dto.Sender.UID), Actor: dto.Sender));
    }

    private void OnChatOpened(ChatConversationOpenedMessage msg)
        => _state.Remove($"chat:{msg.OtherUid}");

    /// <summary>Reconnect sync: pull unread durable notifications and drop
    /// session entries the server no longer reports unread.</summary>
    private async Task SyncServerNotificationsAsync()
    {
        var generation = _state.BeginSync();
        IReadOnlyList<InboxItem>? unread = null;
        try
        {
            var dtos = await _apiController.UserGetNotifications().ConfigureAwait(false);
            unread = dtos.Where(dto => !dto.IsRead).Select(ToInboxItem).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to sync server notifications");
        }
        finally { _state.EndSync(generation, unread); }
    }

    private static InboxItem ToInboxItem(UserNotificationDto dto) => new(
        $"{ServerItemPrefix}{dto.Id}", InboxKind.Server,
        dto.Title, dto.Snippet,
        new DateTimeOffset(dto.CreatedAt, TimeSpan.Zero), dto, Actor: dto.Actor, GroupGid: dto.GroupGID);

    private void OnAreaBoundConsent(AreaBoundSyncshellConsentRequestMessage msg)
    {
        var gid = msg.Syncshell?.GID ?? "unknown";
        _state.Add(new InboxItem($"consent:{gid}", InboxKind.AreaBoundConsent,
            "Review nearby syncshell request",
            msg.RequiresRulesAcceptance ? "Rules acceptance required" : null,
            DateTimeOffset.UtcNow, msg, Source: msg.Syncshell?.Group.AliasOrGID, GroupGid: gid));
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _state.Reset();
        return Task.CompletedTask;
    }
}
