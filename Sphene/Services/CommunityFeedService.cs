using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.Services.Mediator;
using Sphene.WebAPI;

namespace Sphene.Services;

/// <summary>
/// Session cache for the aggregated community feed (news, events, photos
/// across the user's syncshells). Fetched once on demand and on hub
/// reconnect  ·  never polled, so the UI reads a snapshot and a manual
/// <see cref="Refresh"/> is the only network path.
///
/// Requests bind to the feed generation (<see cref="CommunityFeedStore"/>):
/// a response landing after <see cref="Invalidate"/> - disconnect, profile
/// switch, reconnect - is discarded instead of repopulating the stale cache,
/// and the unlatched request flag lets the next render refetch cleanly.
/// </summary>
public class CommunityFeedService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ApiController _apiController;
    private readonly CommunityFeedStore _store = new();
    private readonly CommunityFeedStore _forYouStore = new();

    public CommunityFeedService(ILogger<CommunityFeedService> logger,
        SpheneMediator mediator, ApiController apiController)
        : base(logger, mediator)
    {
        _apiController = apiController;
        Mediator.Subscribe<HubReconnectedMessage>(this, (_) => Invalidate());
        Mediator.Subscribe<HubClosedMessage>(this, (_) => Invalidate());
        // Community writes push per shell · invalidate lazily so the next
        // render refetches once instead of fetching on every push.
        // Reaction toggles push too - but likes/chips patch in place, so a
        // reaction-only change must not collapse the whole feed into a reload
        Mediator.Subscribe<CommunityChangedMessage>(this, (msg) =>
        {
            var change = msg.Community;
            if (!change.ReactionsOnly)
            {
                Invalidate();
            }
            else if (change.ReactionTargetKind is { } rk && change.ReactionTargetId is { } rid)
            {
                ScheduleReactionRefetch(rk, rid, change.Group);
            }
        });
        Mediator.Subscribe<ClearProfileDataMessage>(this, (_) => Invalidate());
        // Follow/unfollow changes the eligible set (followed shells feed
        // public events) - a cached result, including an empty one, must
        // re-resolve against the new set.
        Mediator.Subscribe<SyncshellFavoritesChangedMessage>(this, (_) => Invalidate());
    }

    /// <summary>Newest-first snapshot of the feed. Empty until the first refresh lands.</summary>
    public IReadOnlyList<SyncshellCommunityFeedItemDto> Items => _store.Items;

    /// <summary>True while a fetch is in flight.</summary>
    public bool IsLoading => _store.IsLoading;

    /// <summary>Another page is available via <see cref="LoadMoreAsync"/>.</summary>
    public bool HasMore => _store.HasMore;

    /// <summary>The last first-page fetch failed - visible items stay up and
    /// the feed surfaces a retry instead of the plain empty state.</summary>
    public bool RefreshFailed => _store.RefreshFailed;

    /// <summary>The last <see cref="LoadMoreAsync"/> failed - items and cursor
    /// are untouched, so the same page can be retried.</summary>
    public bool PageFailed => _store.PageFailed;

    /// <summary>The earliest eligible upcoming event for the highlight tile,
    /// resolved by the server independently of feed paging - a later page
    /// can never swap in a farther event. Null until the lookup lands.</summary>
    public SyncshellCommunityFeedItemDto? NextEvent => _store.NextEvent;

    /// <summary>Kicks the next-event lookup the first time the highlights
    /// strip renders (idempotent, deduped per generation). Bound to the
    /// feed generation - a profile switch discards a stale response.</summary>
    public void EnsureNextEventRequested()
    {
        if (!_store.ShouldRequestNextEvent(DateTime.UtcNow))
        {
            return;
        }
        _store.MarkNextEventRequested();
        _ = LoadNextEvent();
    }

    /// <summary>
    /// Kicks a fetch the first time a feed surface renders (idempotent).
    /// Safe to call every frame  ·  deduped against in-flight requests.
    /// After a failed attempt the request flag is released again, gated by
    /// <see cref="CommunityFeedStore.RetryBackoff"/>, so the feed recovers on
    /// its own instead of requiring a manual refresh.
    /// </summary>
    public void EnsureRequested()
    {
        if (!_store.ShouldRequest(DateTime.UtcNow))
        {
            return;
        }
        _store.MarkRequested();
        _ = Refresh();
    }

    /// <summary>Drops the cache; the next <see cref="EnsureRequested"/> refetches.
    /// In-flight responses bound to the old generation are discarded.</summary>
    public void Invalidate()
    {
        _store.Invalidate();
        _forYouStore.Invalidate();
    }

    // ------------------------------------------------------------------
    //  "For You" digest - a server-ranked slice, no client paging
    // ------------------------------------------------------------------

    /// <summary>Ranked For You rows; empty until the first fetch lands.</summary>
    public IReadOnlyList<SyncshellCommunityFeedItemDto> ForYouItems => _forYouStore.Items;

    /// <summary>True while the ranked fetch is in flight.</summary>
    public bool ForYouLoading => _forYouStore.IsLoading;

    /// <summary>The last For You fetch failed - the tab surfaces retry.</summary>
    public bool ForYouRefreshFailed => _forYouStore.RefreshFailed;

    /// <summary>Same first-touch trigger as <see cref="EnsureRequested"/>,
    /// bound to the For You store's own generation.</summary>
    public void EnsureForYouRequested()
    {
        if (!_forYouStore.ShouldRequest(DateTime.UtcNow)) return;
        _forYouStore.MarkRequested();
        _ = RefreshForYou();
    }

    /// <summary>Fetches the ranked slice; dedupes concurrent calls.</summary>
    public async Task RefreshForYou()
    {
        if (!_forYouStore.TryBeginRefresh(out var request)) return;
        try
        {
            var page = await _apiController.UserGetForYouFeed(
                new FeedPageRequestDto(Take: 20)).ConfigureAwait(false);
            _forYouStore.ApplyPage(request, page.Items, page.NextCursor);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "For You feed fetch failed");
            _forYouStore.ApplyFailure(request, DateTime.UtcNow);
        }
        finally { _forYouStore.EndRequest(request); }
    }

    /// <summary>Patches a like toggle's authoritative result into every
    /// feed store the row may live in - count and red-heart state update
    /// in place, no refetch, no feed reload.</summary>
    public void ApplyReactionSummary(SyncshellFeedItemKind kind, long itemId,
        ReactionSummaryDto summary)
    {
        _store.PatchReaction(kind, itemId, summary);
        _forYouStore.PatchReaction(kind, itemId, summary);
    }

    /// <summary>Live reaction updates: a reactions-only push carries the
    /// affected target, so exactly one summary refetch per item patches the
    /// rows in place - no feed reload, no guessing. Pushes coalesce per item
    /// over a short debounce window, so a burst of toggles costs one fetch.</summary>
    private readonly System.Threading.Lock _reactionRefetchGate = new();
    private readonly HashSet<(int Kind, long Id)> _pendingReactionRefetches = new();

    private void ScheduleReactionRefetch(int kind, long id, GroupData group)
    {
        using (_reactionRefetchGate.EnterScope())
        {
            if (!_pendingReactionRefetches.Add((kind, id))) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                var rk = (ReactionTargetKind)kind;
                var summary = await _apiController.GetReactionSummary(
                    new ReactionSetDto(rk, id, ReactionKind.Like, true,
                        Group: group))
                    .ConfigureAwait(false);
                // the enums share ordinals for News/Event/Blog - rows of
                // other kinds simply match nothing
                ApplyReactionSummary((SyncshellFeedItemKind)kind, id, summary);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Live reaction refetch failed");
            }
            finally
            {
                using (_reactionRefetchGate.EnterScope())
                {
                    _pendingReactionRefetches.Remove((kind, id));
                }
            }
        });
    }

    /// <summary>Fetches the first page; dedupes concurrent calls.</summary>
    public async Task Refresh()
    {
        if (!_store.TryBeginRefresh(out var request)) return;
        try
        {
            var page = await _apiController.GroupGetCommunityFeedPage(
                new FeedPageRequestDto()).ConfigureAwait(false);
            _store.ApplyPage(request, page.Items, page.NextCursor);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Community feed fetch failed");
            _store.ApplyFailure(request, DateTime.UtcNow);
        }
        finally { _store.EndRequest(request); }
    }

    /// <summary>Appends the next cursor page; no-op at the end or while a
    /// fetch is in flight. New inserts between pages are deduped by identity.
    /// A failed append leaves the visible items untouched and can be retried.</summary>
    public async Task LoadMoreAsync()
    {
        if (!_store.TryBeginPage(out var request)) return;
        try
        {
            var page = await _apiController.GroupGetCommunityFeedPage(
                new FeedPageRequestDto(request.Cursor)).ConfigureAwait(false);
            _store.ApplyPage(request, page.Items, page.NextCursor);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Community feed page fetch failed");
            _store.ApplyFailure(request, DateTime.UtcNow);
        }
        finally { _store.EndRequest(request); }
    }

    /// <summary>Fetches the true next community event - a single narrow
    /// call, not a feed page, so the result is correct no matter how far
    /// the feed has paged.</summary>
    private async Task LoadNextEvent()
    {
        if (!_store.TryBeginNextEvent(out var generation)) return;
        try
        {
            var ev = await _apiController.GroupGetNextCommunityEvent().ConfigureAwait(false);
            _store.ApplyNextEvent(generation, ev);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Community next-event fetch failed");
            _store.ApplyNextEventFailure(generation, DateTime.UtcNow);
        }
        finally { _store.EndNextEventRequest(generation); }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Invalidate();
        return Task.CompletedTask;
    }
}
