using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.Group;
using Sphene.Services.Mediator;
using Sphene.WebAPI;

namespace Sphene.Services;

/// <summary>
/// Session cache for the aggregated community feed (news, events, photos
/// across the user's syncshells). Fetched once on demand and on hub
/// reconnect  ·  never polled, so the UI reads a snapshot and a manual
/// <see cref="Refresh"/> is the only network path.
/// </summary>
public class CommunityFeedService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ApiController _apiController;
    private readonly Lock _lock = new();
    private List<SyncshellCommunityFeedItemDto> _items = [];
    private string? _nextCursor;
    private bool _loading;
    private bool _requested;
    private DateTime _nextRetryUtc;

    /// <summary>Minimum pause between auto-retries after a failed fetch ·
    /// keeps a persistent outage from firing a request every frame.</summary>
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(5);

    public CommunityFeedService(ILogger<CommunityFeedService> logger,
        SpheneMediator mediator, ApiController apiController)
        : base(logger, mediator)
    {
        _apiController = apiController;
        Mediator.Subscribe<HubReconnectedMessage>(this, (_) => Invalidate());
        Mediator.Subscribe<HubClosedMessage>(this, (_) => Invalidate());
        // Community writes push per shell · invalidate lazily so the next
        // render refetches once instead of fetching on every push.
        Mediator.Subscribe<CommunityChangedMessage>(this, (_) => Invalidate());
        Mediator.Subscribe<ClearProfileDataMessage>(this, (_) => Invalidate());
    }

    /// <summary>Newest-first snapshot of the feed. Empty until the first refresh lands.</summary>
    public IReadOnlyList<SyncshellCommunityFeedItemDto> Items
    {
        get
        {
            lock (_lock)
            {
                return _items;
            }
        }
    }

    /// <summary>True while a fetch is in flight.</summary>
    public bool IsLoading
    {
        get { lock (_lock) { return _loading; } }
    }

    /// <summary>Another page is available via <see cref="LoadMoreAsync"/>.</summary>
    public bool HasMore
    {
        get { lock (_lock) { return _nextCursor != null; } }
    }

    /// <summary>
    /// Kicks a fetch the first time a feed surface renders (idempotent).
    /// Safe to call every frame  ·  deduped against in-flight requests.
    /// After a failed attempt the request flag is released again, gated by
    /// <see cref="RetryBackoff"/>, so the feed recovers on its own instead
    /// of requiring a manual refresh.
    /// </summary>
    public void EnsureRequested()
    {
        lock (_lock)
        {
            if (_requested || _loading || DateTime.UtcNow < _nextRetryUtc)
            {
                return;
            }
            _requested = true;
        }
        _ = Refresh();
    }

    /// <summary>Drops the cache; the next <see cref="EnsureRequested"/> refetches.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _requested = false;
            _items = [];
            _nextCursor = null;
            _nextRetryUtc = default;
        }
    }

    /// <summary>Fetches the first page; dedupes concurrent calls.</summary>
    public async Task Refresh()
    {
        if (!BeginLoad()) return;
        try
        {
            var page = await _apiController.GroupGetCommunityFeedPage(
                new FeedPageRequestDto()).ConfigureAwait(false);
            lock (_lock)
            {
                _items = page.Items;
                _nextCursor = page.NextCursor;
                _requested = true;
                _nextRetryUtc = default;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Community feed fetch failed");
            lock (_lock)
            {
                // Unlatch so the next render retries - throttled by the
                // backoff so a persistent outage doesn't request per frame.
                _requested = false;
                _nextRetryUtc = DateTime.UtcNow + RetryBackoff;
            }
        }
        finally { EndLoad(); }
    }

    /// <summary>Appends the next cursor page; no-op at the end or while a
    /// fetch is in flight. New inserts between pages are deduped by identity.</summary>
    public async Task LoadMoreAsync()
    {
        string? cursor;
        lock (_lock)
        {
            if (_loading || _nextCursor == null) return;
            cursor = _nextCursor;
            _loading = true;
        }
        try
        {
            var page = await _apiController.GroupGetCommunityFeedPage(
                new FeedPageRequestDto(cursor)).ConfigureAwait(false);
            lock (_lock)
            {
                var seen = new HashSet<string>(_items.Select(KeyOf), StringComparer.Ordinal);
                foreach (var item in page.Items)
                {
                    if (seen.Add(KeyOf(item))) _items.Add(item);
                }
                _nextCursor = page.NextCursor;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Community feed page fetch failed");
        }
        finally { EndLoad(); }
    }

    /// <summary>Dedupe identity - activity rows can repeat (kind,id) pairs
    /// across actors, and insert races can resend a row across a cursor move.</summary>
    private static string KeyOf(SyncshellCommunityFeedItemDto i)
        => $"{i.Kind}:{i.Id}:{i.ActorUid}:{i.TimestampUtc.Ticks}";

    private bool BeginLoad()
    {
        lock (_lock)
        {
            if (_loading) return false;
            _loading = true;
            return true;
        }
    }

    private void EndLoad()
    {
        lock (_lock) { _loading = false; }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Invalidate();
        return Task.CompletedTask;
    }
}
