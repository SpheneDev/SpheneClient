using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Dto.User;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using System.Collections.Concurrent;

namespace Sphene.Services;

/// <summary>
/// Session-scoped DM history between the user and their friends. Pushes
/// arrive via <see cref="ChatMessageReceivedMessage"/>; history pages in
/// lazily per conversation on first open. Unread tracking is purely local
/// - opening a conversation marks it read server-side.
/// </summary>
public class ChatService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ApiController _apiController;
    private readonly Dictionary<string, List<ChatMessageDto>> _history
        = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    // mediator callbacks mutate this on the queue thread while Draw reads it
    // per frame per row - a concurrent set keeps the badge lookup safe
    private readonly ConcurrentDictionary<string, byte> _unread
        = new(StringComparer.Ordinal);
    private string? _openConversation;

    public ChatService(ILogger<ChatService> logger, SpheneMediator mediator,
        ApiController apiController) : base(logger, mediator)
    {
        _apiController = apiController;
        Mediator.Subscribe<ChatMessageReceivedMessage>(this, msg =>
        {
            var dto = msg.Message;
            var own = _apiController.UID ?? string.Empty;
            var other = string.Equals(dto.Sender.UID, own,
                StringComparison.Ordinal) ? dto.Recipient.UID : dto.Sender.UID;
            if (!_history.TryGetValue(other, out var list))
            {
                _history[other] = list = new(32);
            }
            list.Add(dto);
            if (string.Equals(dto.Sender.UID, own, StringComparison.Ordinal))
            {
                return;
            }
            if (string.Equals(other, _openConversation, StringComparison.Ordinal))
            {
                // the conversation is on screen - keep the server's read
                // state in step instead of letting unread pile up behind it
                _ = _apiController.UserMarkChatRead(new UserDto(new UserData(other)));
                return;
            }
            if (_unread.TryAdd(other, 1))
            {
                // first unread for this conversation - surface it once; the
                // inbox row (stable chat:{uid} entry) carries the rest
                var name = dto.Sender.DisplayName ?? dto.Sender.Alias ?? "Someone";
                Mediator.Publish(new NotificationMessage("New message",
                    $"{name} sent you a message",
                    SpheneConfiguration.Models.NotificationType.Info,
                    TimeSpan.FromSeconds(5), SuppressInbox: true));
            }
        });
    }

    public int UnreadCount => _unread.Count;

    public bool HasUnread(string uid) => _unread.ContainsKey(uid);

    public IReadOnlyList<ChatMessageDto> History(string uid)
        => _history.TryGetValue(uid, out var l) ? l : [];

    /// <summary>Pulls the first history page for a conversation once.</summary>
    public void EnsureHistory(string uid)
    {
        if (_history.ContainsKey(uid) || !_loading.Add(uid))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var page = await _apiController.UserGetChatHistory(
                    new UserDto(new UserData(uid)), take: 50).ConfigureAwait(false);
                page.Reverse();
                if (!_history.TryGetValue(uid, out var list))
                {
                    _history[uid] = list = new(32);
                }
                list.InsertRange(0, page);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Chat history load failed for {Uid}", uid);
            }
            finally
            {
                _loading.Remove(uid);
            }
        });
    }

    /// <summary>Marks the conversation as on screen: clears the local unread
    /// flag, tells the server the history is read (once per open, not per
    /// frame) and notifies unread surfaces so they can dismiss their row.</summary>
    public void MarkOpen(string uid)
    {
        var openChanged = !string.Equals(_openConversation, uid,
            StringComparison.Ordinal);
        _openConversation = uid;
        var hadUnread = _unread.TryRemove(uid, out _);
        if (!openChanged && !hadUnread)
        {
            return;
        }
        _ = _apiController.UserMarkChatRead(new UserDto(new UserData(uid)));
        Mediator.Publish(new ChatConversationOpenedMessage(uid));
    }

    public void MarkClosed() => _openConversation = null;

    public Task Send(string uid, string text)
        => _apiController.UserSendChatMessage(new UserDto(new UserData(uid)), text);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
