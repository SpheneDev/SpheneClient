using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;
using Sphene.Services.E2ee;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using System.Collections.Concurrent;

namespace Sphene.Services;

/// <summary>
/// Session-scoped DM history between the user and their friends. Wire rows
/// arrive as <see cref="ChatMessageDto"/>; the service resolves each into a
/// <see cref="ChatEntry"/> - plaintext rows pass through, sealed rows are
/// opened locally via <see cref="ChatCryptoService"/>. Sends always go out
/// sealed; there is no plaintext fallback. Legacy plaintext rows are migrated
/// to sealed storage incrementally when both participants have device keys.
/// </summary>
public class ChatService : DisposableMediatorSubscriberBase, IHostedService
{
    private const int MigrationBatchSize = 25;

    private readonly ApiController _apiController;
    private readonly ChatCryptoService _crypto;
    // Mediator callbacks and background tasks mutate this while Draw reads
    // per frame - the dictionary must be concurrent and each list guarded
    // by its own lock.
    private readonly ConcurrentDictionary<string, List<ChatEntry>> _history
        = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _loading
        = new(StringComparer.Ordinal);
    /// <summary>Conversations whose first page was fetched this session -
    /// distinct from <see cref="_history"/> membership, which live pushes
    /// also create.</summary>
    private readonly ConcurrentDictionary<string, byte> _loaded
        = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _migrating
        = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    // mediator callbacks mutate this on the queue thread while Draw reads it
    // per frame per row - a concurrent set keeps the badge lookup safe
    private readonly ConcurrentDictionary<string, byte> _unread
        = new(StringComparer.Ordinal);
    private string? _openConversation;
    private DateTime _openMarkedAt;

    public ChatService(ILogger<ChatService> logger, SpheneMediator mediator,
        ApiController apiController, ChatCryptoService crypto) : base(logger, mediator)
    {
        _apiController = apiController;
        _crypto = crypto;
        // Live pushes only reach connected clients - messages that arrived
        // while this client was offline sit in server history. Refreshing
        // loaded conversations picks them up. ConnectedMessage covers full
        // reconnects/server restarts that auto-reconnect never fires for.
        Mediator.Subscribe<HubReconnectedMessage>(this, _ => RefreshLoadedConversations());
        Mediator.Subscribe<ConnectedMessage>(this, _ => RefreshLoadedConversations());
        Mediator.Subscribe<ChatMessageReceivedMessage>(this, msg =>
        {
            var dto = msg.Message;
            var own = _apiController.UID ?? string.Empty;
            var other = string.Equals(dto.Sender.UID, own,
                StringComparison.Ordinal) ? dto.Recipient.UID : dto.Sender.UID;
            var entry = Resolve(dto);
            var list = _history.GetOrAdd(other, _ => new(32));
            lock (list)
            {
                list.Add(entry);
            }
            var fromOther = !string.Equals(dto.Sender.UID, own,
                StringComparison.Ordinal);
            var firstUnread = false;
            if (fromOther)
            {
                if (ConversationIsOnScreen(other))
                {
                    // the conversation is on screen - keep the server's read
                    // state in step instead of letting unread pile up behind it
                    _ = _apiController.UserMarkChatRead(new UserDto(new UserData(other)));
                }
                else
                {
                    // Set the flag BEFORE publishing the ready message - the
                    // inbox handler reads HasUnread to decide its row.
                    firstUnread = _unread.TryAdd(other, 1);
                }
            }
            Mediator.Publish(new ChatMessageReadyMessage(other, entry));
            if (firstUnread)
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

    /// <summary>Snapshot for Draw - the live list is mutated on other
    /// threads, so callers get a stable copy.</summary>
    public IReadOnlyList<ChatEntry> History(string uid)
    {
        if (!_history.TryGetValue(uid, out var list)) return [];
        lock (list)
        {
            return list.ToList();
        }
    }

    /// <summary>Pulls the first history page for a conversation once per
    /// session - tracked separately from <see cref="_history"/> so a live
    /// push landing before the first open does not suppress the fetch -
    /// then kicks the resumable plaintext→sealed migration.</summary>
    public void EnsureHistory(string uid)
    {
        if (_loaded.ContainsKey(uid) || !_loading.TryAdd(uid, 0))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // A disconnected fetch returns an empty page - must not mark
                // the conversation loaded or the real fetch never happens.
                if (_cts.IsCancellationRequested || !_apiController.IsConnected)
                {
                    return;
                }
                var page = await _apiController.UserGetChatHistory(
                    new UserDto(new UserData(uid)), take: 50).ConfigureAwait(false);
                page.Reverse();
                MergePage(uid, page);
                _loaded[uid] = 0;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Chat history load failed for {Uid}", uid);
            }
            finally
            {
                _loading.TryRemove(uid, out _);
            }
            MigrateConversationInBackground(uid);
        });
    }

    /// <summary>Merges a fetched (ascending) page into the conversation list:
    /// live pushes that raced the fetch and are not in the page are kept.</summary>
    private void MergePage(string uid, List<ChatMessageDto> page)
    {
        var resolved = page.Select(Resolve).ToList();
        var seen = new HashSet<Guid>(page.Select(d => d.Id));
        var list = _history.GetOrAdd(uid, _ => new(32));
        lock (list)
        {
            var tail = list.Where(e => !seen.Contains(e.Dto.Id)).ToList();
            list.Clear();
            list.AddRange(resolved);
            list.AddRange(tail);
            list.Sort((a, b) => a.Dto.SentAt.CompareTo(b.Dto.SentAt));
        }
    }

    private void RefreshLoadedConversations()
    {
        foreach (var uid in _history.Keys)
        {
            RefreshConversationInBackground(uid);
        }
    }

    /// <summary>Marks the conversation as on screen: clears the local unread
    /// flag, tells the server the history is read (once per open, not per
    /// frame) and notifies unread surfaces so they can dismiss their row.</summary>
    public void MarkOpen(string uid)
    {
        var openChanged = !string.Equals(_openConversation, uid,
            StringComparison.Ordinal);
        _openConversation = uid;
        // Called every frame while the chat tab renders; when the user
        // navigates away the mark goes stale and unread accrues again.
        _openMarkedAt = DateTime.UtcNow;
        var hadUnread = _unread.TryRemove(uid, out _);
        if (!openChanged && !hadUnread)
        {
            return;
        }
        _ = _apiController.UserMarkChatRead(new UserDto(new UserData(uid)));
        Mediator.Publish(new ChatConversationOpenedMessage(uid));
    }

    public void MarkClosed() => _openConversation = null;

    /// <summary>True while the conversation's chat tab is actually being
    /// rendered - marks are re-stamped per frame and expire quickly.</summary>
    private bool ConversationIsOnScreen(string uid)
        => string.Equals(uid, _openConversation, StringComparison.Ordinal)
            && DateTime.UtcNow - _openMarkedAt < TimeSpan.FromSeconds(2);

    /// <summary>Encrypts and sends a DM. There is intentionally no plaintext
    /// fallback - a failure returns <see cref="ChatSealFailure"/> for the UI.</summary>
    public async Task<ChatSealFailure> Send(string uid, string text)
    {
        if (!_apiController.IsConnected) return ChatSealFailure.NotConnected;
        var own = _apiController.UID ?? string.Empty;
        if (own.Length == 0) return ChatSealFailure.NoSelfDevices;
        var messageId = Guid.NewGuid();
        // Sealed and stored timestamps must be bit-identical: the value is
        // bound into the AEAD metadata and the DB keeps microsecond precision.
        var sentAtUtc = Services.E2ee.ChatEnvelope.NormalizeTimestamp(DateTime.UtcNow);
        var seal = await _crypto.SealAsync(own, uid, own, uid, text, messageId,
            ChatMessageKind.SealedNative, sentAtUtc).ConfigureAwait(false);
        if (!seal.Success)
        {
            return seal.Failure;
        }
        await _apiController.UserSendSealedChatMessage(
            new UserDto(new UserData(uid)), messageId, seal.Copies, sentAtUtc)
            .ConfigureAwait(false);
        return ChatSealFailure.None;
    }

    /// <summary>Whether the peer currently has publishable devices - drives
    /// the composer hint. Null = not yet resolved (triggers a fetch).</summary>
    public async Task<bool> CanSealForAsync(string uid)
    {
        var own = _apiController.UID ?? string.Empty;
        var devices = await _crypto.GetDeviceKeysAsync([own, uid])
            .ConfigureAwait(false);
        return devices[own].Count > 0 && devices[uid].Count > 0;
    }

    /// <summary>Peer key material changed after first sight - UI banner.</summary>
    public bool PeerKeyChanged(string uid) => _crypto.PeerKeyChanged(uid);

    /// <summary>Pairwise safety number for out-of-band verification; null
    /// while either side's keys are unknown.</summary>
    public Task<string?> SafetyNumberFor(string uid)
        => _crypto.ComputeSafetyNumberAsync(_apiController.UID ?? string.Empty, uid);

    /// <summary>Fingerprint of the peer's newest published key.</summary>
    public string? PeerFingerprintFor(string uid) => _crypto.PeerFingerprint(uid);

    /// <summary>Clears the peer key-change flag after the user acknowledged it.</summary>
    public void DismissPeerKeyChange(string uid) => _crypto.DismissPeerKeyChange(uid);

    /// <summary>Refetches the first history page of an already-loaded
    /// conversation and replaces the local view - deduplicates live pushes
    /// that also exist in the page by message id.</summary>
    private void RefreshConversationInBackground(string uid)
    {
        if (!_loading.TryAdd(uid, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_cts.IsCancellationRequested || !_apiController.IsConnected)
                {
                    return;
                }
                var page = await _apiController.UserGetChatHistory(
                    new UserDto(new UserData(uid)), take: 50).ConfigureAwait(false);
                page.Reverse();
                if (!_history.ContainsKey(uid)) return;
                // Keep live rows that are not in the page (newest arrivals
                // racing the fetch) and re-resolve the page's entries.
                MergePage(uid, page);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Chat history refresh failed for {Uid}", uid);
            }
            finally
            {
                _loading.TryRemove(uid, out _);
            }
        });
    }

    /// <summary>Resolves a wire row into a display entry. Plaintext rows keep
    /// their text; sealed rows are opened with the matching local device key.</summary>
    private ChatEntry Resolve(ChatMessageDto dto)
    {
        if (dto.Kind == ChatMessageKind.Plaintext)
        {
            return new(dto, dto.Message, ChatEntryState.Plaintext);
        }
        return _crypto.TryOpen(dto, out var text)
            ? new(dto, text, ChatEntryState.Sealed)
            : new(dto, null, ChatEntryState.Undecryptable);
    }

    /// <summary>
    /// Resumable client-side migration: re-seals legacy plaintext rows for
    /// BOTH participants' devices and asks the server to atomically replace
    /// them. Rows stay untouched (and readable) whenever either side lacks
    /// published keys or the seal/replace fails.
    /// </summary>
    private void MigrateConversationInBackground(string otherUid)
    {
        if (!_migrating.TryAdd(otherUid, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var own = _apiController.UID ?? string.Empty;
                if (!_history.TryGetValue(otherUid, out var list)) return;
                // Drain in batches - any failure (missing peer keys, seal or
                // replace error) stops the pass and leaves remaining rows
                // for the next conversation open.
                var draining = true;
                while (draining && !_cts.IsCancellationRequested)
                {
                    draining = await MigrateBatchAsync(own, otherUid, list)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Chat history migration failed for {Uid}", otherUid);
            }
            finally
            {
                _migrating.TryRemove(otherUid, out _);
            }
        });
    }

    /// <summary>Migrates up to <see cref="MigrationBatchSize"/> plaintext
    /// rows. Returns false when nothing migrated - caller stops draining.</summary>
    private async Task<bool> MigrateBatchAsync(string own, string otherUid,
        List<ChatEntry> list)
    {
        List<ChatEntry> legacy;
        lock (list)
        {
            legacy = list.Where(e => e.IsPlaintext && e.Text != null)
                .Take(MigrationBatchSize).ToList();
        }
        if (legacy.Count == 0) return false;
        var devices = await _crypto.GetDeviceKeysAsync([own, otherUid])
            .ConfigureAwait(false);
        if (devices[own].Count == 0 || devices[otherUid].Count == 0)
        {
            return false;
        }
        foreach (var entry in legacy)
        {
            var dto = entry.Dto;
            var seal = await _crypto.SealAsync(dto.Sender.UID,
                dto.Recipient.UID, own, otherUid, entry.Text!, dto.Id,
                ChatMessageKind.SealedMigrated, dto.SentAt)
                .ConfigureAwait(false);
            if (!seal.Success) return false;
            var migrated = await _apiController.UserMigrateChatMessage(
                dto.Id, seal.Copies).ConfigureAwait(false);
            if (!migrated) return false;
            lock (list)
            {
                // Swap the local entry: keep the text, adopt sealed kind.
                var index = list.IndexOf(entry);
                if (index >= 0)
                {
                    list[index] = new ChatEntry(dto with
                    {
                        Kind = ChatMessageKind.SealedMigrated,
                        MigratedByUid = own,
                        Copies = [],
                    }, entry.Text, ChatEntryState.Sealed);
                }
            }
        }
        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
