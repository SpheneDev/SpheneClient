using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.User;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.WebAPI;
using System.Collections.Concurrent;

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
    private const int MaxItems = 24;

    private const string ServerItemPrefix = "srv:";

    private readonly ConcurrentDictionary<string, InboxItem> _items = new(StringComparer.Ordinal);
    private readonly Lock _itemsLock = new();
    private readonly SpheneConfigService _configService;
    private readonly ApiController _apiController;
    private readonly ChatService _chatService;
    private readonly PairManager _pairManager;

    public NotificationInboxService(ILogger<NotificationInboxService> logger,
        SpheneMediator mediator, SpheneConfigService configService, ApiController apiController,
        ChatService chatService, PairManager pairManager)
        : base(logger, mediator)
    {
        _configService = configService;
        _apiController = apiController;
        _chatService = chatService;
        _pairManager = pairManager;
        Mediator.Subscribe<PenumbraModTransferAvailableMessage>(this, OnModTransferAvailable);
        Mediator.Subscribe<PenumbraModTransferCompletedMessage>(this, OnModTransferCompleted);
        Mediator.Subscribe<ShowUpdateNotificationMessage>(this, OnUpdateAvailable);
        Mediator.Subscribe<NotificationMessage>(this, OnNotification);
        Mediator.Subscribe<AreaBoundSyncshellConsentRequestMessage>(this, OnAreaBoundConsent);
        Mediator.Subscribe<UserNotificationReceivedMessage>(this, OnServerNotification);
        Mediator.Subscribe<ChatMessageReadyMessage>(this, OnChatMessage);
        Mediator.Subscribe<ChatConversationOpenedMessage>(this, OnChatOpened);
        Mediator.Subscribe<HubReconnectedMessage>(this, (m) => _ = SyncServerNotificationsAsync());
    }

    /// <summary>Current items, newest first - a snapshot of the live store.</summary>
    public IReadOnlyList<InboxItem> GetItems()
    {
        lock (_itemsLock)
        {
            return _items.Values.OrderByDescending(i => i.Timestamp).ToList();
        }
    }

    public int Count => _items.Count;

    /// <summary>Removes one item (user dismissed or acted on it). Server
    /// notifications are marked read on the server at the same time.</summary>
    public void Dismiss(string id)
    {
        if (!_items.TryRemove(id, out _)) return;
        if (id.StartsWith(ServerItemPrefix, StringComparison.Ordinal)
            && long.TryParse(id.AsSpan(ServerItemPrefix.Length), out var serverId))
        {
            _ = _apiController.UserMarkNotificationsRead([serverId]);
        }
    }

    /// <summary>Clears everything · e.g. on manual "acknowledge all". Also
    /// acknowledges every durable server notification.</summary>
    public void Clear()
    {
        var serverIds = new List<long>();
        foreach (var key in _items.Keys)
        {
            if (key.StartsWith(ServerItemPrefix, StringComparison.Ordinal)
                && long.TryParse(key.AsSpan(ServerItemPrefix.Length), out var serverId))
            {
                serverIds.Add(serverId);
            }
        }
        _items.Clear();
        if (serverIds.Count > 0) _ = _apiController.UserMarkNotificationsRead(serverIds);
    }

    private void Add(InboxItem item)
    {
        // one-shot notifications: shown once, then never pending again -
        // "Welcome to Sphene Network" / seasonal notices don't reappear on
        // every restart. Seen at add-time because the bubble already did
        // its job (the user saw it).
        if (item.Kind == InboxKind.Notification && !item.Important)
        {
            var seen = _configService.Current.SeenInboxNotificationIds;
            if (seen.Contains(item.Id, StringComparer.Ordinal))
            {
                return;
            }
            seen.Add(item.Id);
            if (seen.Count > 50)
            {
                seen.RemoveRange(0, seen.Count - 50);
            }
            _configService.Save();
        }
        _items[item.Id] = item;
        lock (_itemsLock)
        {
            if (_items.Count > MaxItems)
            {
                foreach (var stale in _items.Values
                    .OrderBy(i => i.Timestamp).Take(_items.Count - MaxItems).ToList())
                {
                    _items.TryRemove(stale.Id, out _);
                }
            }
        }
    }

    private void OnModTransferAvailable(PenumbraModTransferAvailableMessage msg)
    {
        var sender = msg.Notification.Sender?.AliasOrUID ?? "someone";
        var name = msg.Notification.ModFolderName ?? msg.Notification.FileName;
        Add(new InboxItem($"mod:{msg.Notification.Hash}", InboxKind.ModTransfer,
            $"Mod incoming from {sender}",
            string.IsNullOrWhiteSpace(name) ? null : name,
            DateTimeOffset.UtcNow, msg.Notification));
    }

    private void OnModTransferCompleted(PenumbraModTransferCompletedMessage msg)
    {
        var sender = msg.Notification.Sender?.AliasOrUID ?? "someone";
        Add(new InboxItem($"moddone:{msg.Notification.Hash}", InboxKind.ModTransferDone,
            msg.Success ? $"Mod received from {sender}" : $"Mod transfer failed ({sender})",
            null, DateTimeOffset.UtcNow, msg.Notification));
    }

    private void OnUpdateAvailable(ShowUpdateNotificationMessage msg)
    {
        if (msg.UpdateInfo?.IsUpdateAvailable != true)
        {
            return;
        }
        Add(new InboxItem("update", InboxKind.Update,
            $"Update available: {msg.UpdateInfo.LatestVersion}",
            msg.UpdateInfo.IsTestBuildUpdate ? "Test build" : null,
            DateTimeOffset.UtcNow, msg.UpdateInfo));
    }

    private void OnNotification(NotificationMessage msg)
    {
        if (msg.SuppressInbox
            || msg.Type is not (NotificationType.Info or NotificationType.Warning or NotificationType.Error))
        {
            return;
        }
        // Info messages are seen-once · warnings and errors are important
        // and always land in pending until dismissed.
        var important = msg.Type is not NotificationType.Info;
        Add(new InboxItem($"note:{msg.Title}:{msg.Message.GetHashCode(StringComparison.Ordinal):X8}",
            InboxKind.Notification, msg.Title, msg.Message, DateTimeOffset.UtcNow,
            Important: important));
    }

    private void OnServerNotification(UserNotificationReceivedMessage msg)
    {
        var dto = msg.Notification;
        if (dto.IsRead) return;
        Add(ToInboxItem(dto));
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
            _items.TryRemove(id, out _);
            return;
        }
        var name = dto.Sender.DisplayName ?? dto.Sender.Alias ?? "Someone";
        var text = msg.Entry.Gif != null
            ? "[GIF]"
            : msg.Entry.Text ?? "[encrypted message]";
        var preview = text.Length > 80
            ? string.Concat(text.AsSpan(0, 80), "…")
            : text;
        Add(new InboxItem(id, InboxKind.Chat, $"New message from {name}",
            preview, DateTimeOffset.UtcNow,
            _pairManager.GetPairByUID(dto.Sender.UID)));
    }

    private void OnChatOpened(ChatConversationOpenedMessage msg)
        => _items.TryRemove($"chat:{msg.OtherUid}", out _);

    /// <summary>Reconnect sync: pull unread durable notifications and drop
    /// session entries the server no longer reports unread.</summary>
    private async Task SyncServerNotificationsAsync()
    {
        try
        {
            var dtos = await _apiController.UserGetNotifications().ConfigureAwait(false);
            var unreadIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dto in dtos)
            {
                if (dto.IsRead) continue;
                var item = ToInboxItem(dto);
                unreadIds.Add(item.Id);
                Add(item);
            }
            foreach (var key in _items.Keys)
            {
                if (key.StartsWith(ServerItemPrefix, StringComparison.Ordinal) && !unreadIds.Contains(key))
                {
                    _items.TryRemove(key, out _);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to sync server notifications");
        }
    }

    private static InboxItem ToInboxItem(UserNotificationDto dto) => new(
        $"{ServerItemPrefix}{dto.Id}", InboxKind.Server,
        dto.Title, dto.Snippet,
        new DateTimeOffset(dto.CreatedAt, TimeSpan.Zero), dto);

    private void OnAreaBoundConsent(AreaBoundSyncshellConsentRequestMessage msg)
    {
        var gid = msg.Syncshell?.GID ?? "unknown";
        Add(new InboxItem($"consent:{gid}", InboxKind.AreaBoundConsent,
            "Syncshell nearby wants your consent",
            msg.RequiresRulesAcceptance ? "Rules acceptance required" : gid,
            DateTimeOffset.UtcNow, msg));
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Clear();
        return Task.CompletedTask;
    }
}
