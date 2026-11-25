using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;

namespace Sphene.Services;

/// <summary>
/// Dependency-free state machine behind <see cref="CommunityFeedService"/> -
/// owns the item list, cursor, generation and failure flags for the community
/// feed session cache.
///
/// Request lifetimes bind to the generation that issued them
/// (<see cref="FeedRequest.Generation"/>): a page completing after
/// <see cref="Invalidate"/> is discarded instead of repopulating a stale
/// cache. Item lists are published as snapshots and never mutated in place,
/// so readers always hold a stable view while a page append lands.
/// </summary>
public sealed class CommunityFeedStore
{
    /// <summary>One in-flight request bound to the generation + cursor it started on.</summary>
    public readonly record struct FeedRequest(int Generation, string? Cursor, int ChangeVersion = 0, int VisibleCount = 0)
    {
        public bool IsFirstPage => Cursor == null;
    }

    /// <summary>Minimum pause between auto-retries after a failed first-page fetch ·
    /// keeps a persistent outage from firing a request every frame.</summary>
    public static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private IReadOnlyList<SyncshellCommunityFeedItemDto> _items = [];
    private string? _nextCursor;
    private bool _loading;
    private bool _requested;
    private int _generation;
    private int _changeVersion;
    private DateTime _nextRetryUtc;
    private bool _refreshFailed;
    private bool _pageFailed;
    private SyncshellCommunityFeedItemDto? _nextEvent;
    private bool _nextEventRequested;
    private bool _nextEventLoading;
    private DateTime _nextEventRetryUtc;

    /// <summary>Newest-first snapshot of the feed. Empty until the first refresh lands.</summary>
    public IReadOnlyList<SyncshellCommunityFeedItemDto> Items
    {
        get { lock (_lock) { return _items; } }
    }

    /// <summary>True while a fetch is in flight.</summary>
    public bool IsLoading
    {
        get { lock (_lock) { return _loading; } }
    }

    /// <summary>Another page is available.</summary>
    public bool HasMore
    {
        get { lock (_lock) { return _nextCursor != null; } }
    }

    /// <summary>The last first-page fetch failed - the visible items (if any)
    /// stayed up and a manual retry is the next step once the backoff passes.</summary>
    public bool RefreshFailed
    {
        get { lock (_lock) { return _refreshFailed; } }
    }

    /// <summary>The last page-append failed - items and cursor are untouched,
    /// so the same page can be retried.</summary>
    public bool PageFailed
    {
        get { lock (_lock) { return _pageFailed; } }
    }

    /// <summary>The earliest eligible upcoming event for the highlight tile -
    /// resolved server-side, so feed paging can never substitute a later one.
    /// Null until the dedicated lookup lands.</summary>
    public SyncshellCommunityFeedItemDto? NextEvent
    {
        get { lock (_lock) { return _nextEvent; } }
    }

    /// <summary>Whether the next-event lookup may start - same request/backoff
    /// discipline as the feed fetch. A cached event whose start has passed is
    /// expired here: it stops rendering and unlatches the request flag so the
    /// next eligible event resolves on its own.</summary>
    public bool ShouldRequestNextEvent(DateTime utcNow)
    {
        lock (_lock)
        {
            if (_nextEvent != null && _nextEvent.TimestampUtc <= utcNow)
            {
                _nextEvent = null;
                _nextEventRequested = false;
            }
            return !_nextEventRequested && !_nextEventLoading
                && utcNow >= _nextEventRetryUtc;
        }
    }

    /// <summary>Latches the next-event request flag before the async path spins up.</summary>
    public void MarkNextEventRequested()
    {
        lock (_lock) { _nextEventRequested = true; }
    }

    /// <summary>Starts the next-event lookup; the returned generation binds
    /// the response - a stale completion is discarded by the apply guards.</summary>
    public bool TryBeginNextEvent(out int generation)
    {
        lock (_lock)
        {
            if (_nextEventLoading)
            {
                generation = 0;
                return false;
            }
            _nextEventLoading = true;
            generation = _generation;
            return true;
        }
    }

    /// <summary>Applies the next-event response - no-op when a profile switch
    /// bumped the generation while the request was in flight.</summary>
    public void ApplyNextEvent(int generation, SyncshellCommunityFeedItemDto? item)
    {
        lock (_lock)
        {
            if (generation != _generation) return;
            _nextEvent = item;
            _nextEventRetryUtc = default;
        }
    }

    /// <summary>Unlatches the next-event request behind the retry backoff so a
    /// transient failure recovers without per-frame calls.</summary>
    public void ApplyNextEventFailure(int generation, DateTime utcNow)
    {
        lock (_lock)
        {
            if (generation != _generation) return;
            _nextEventRequested = false;
            _nextEventRetryUtc = utcNow + RetryBackoff;
        }
    }

    /// <summary>Releases the next-event in-flight slot - only while the
    /// finishing request still owns the generation. <see cref="Invalidate"/>
    /// frees the slot early so the new generation can request immediately;
    /// a stale request's finally then must not evict the new owner.</summary>
    public void EndNextEventRequest(int generation)
    {
        lock (_lock)
        {
            if (generation != _generation) return;
            _nextEventLoading = false;
        }
    }

    /// <summary>Whether a first-page fetch may start: nothing requested or in
    /// flight and the failure backoff has passed.</summary>
    public bool ShouldRequest(DateTime utcNow)
    {
        lock (_lock)
        {
            return !_requested && !_loading && utcNow >= _nextRetryUtc;
        }
    }

    /// <summary>Latches the request flag so renders don't re-fire while the
    /// async fetch path spins up.</summary>
    public void MarkRequested()
    {
        lock (_lock) { _requested = true; }
    }

    /// <summary>Schedule a quiet refresh of the loaded part of the stream. A push
    /// received during a fetch stays pending after that fetch completes.</summary>
    public void ActivityChanged(DateTime now)
    {
        lock (_lock)
        {
            if (!_loading && !_requested && _items.Count == 0) return;
            _changeVersion++;
            _requested = false;
            if (_nextRetryUtc == default) _nextRetryUtc = now.AddSeconds(2);
        }
    }

    public async Task ReadVisiblePages(FeedRequest request,
        Func<FeedPageRequestDto, Task<SyncshellCommunityFeedPageDto>> fetch)
    {
        var items = new List<SyncshellCommunityFeedItemDto>();
        var cursor = request.Cursor;
        do
        {
            lock (_lock)
            {
                if (request.Generation != _generation) return;
            }
            var page = await fetch(new(cursor)).ConfigureAwait(false);
            items.AddRange(page.Items);
            if (page.NextCursor != null && string.Equals(page.NextCursor, cursor, StringComparison.Ordinal))
                throw new InvalidOperationException("Activity feed returned the same continuation cursor.");
            cursor = page.NextCursor;
        } while (request.IsFirstPage && cursor != null && items.Count < request.VisibleCount);
        ApplyPage(request, items, cursor);
    }

    /// <summary>Drops the cache and bumps the generation - responses bound to
    /// older generations are discarded on arrival.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _generation++;
            _requested = false;
            // Free the slot so the new generation requests immediately; the
            // old request's finally is generation-bound and can't evict it.
            _loading = false;
            _items = [];
            _nextCursor = null;
            _nextRetryUtc = default;
            _refreshFailed = false;
            _pageFailed = false;
            _nextEvent = null;
            _nextEventRequested = false;
            _nextEventLoading = false;
            _nextEventRetryUtc = default;
        }
    }

    /// <summary>Starts a first-page request; the returned
    /// <see cref="FeedRequest"/> binds the completion to this generation.</summary>
    public bool TryBeginRefresh(out FeedRequest request)
    {
        lock (_lock)
        {
            if (_loading)
            {
                request = default;
                return false;
            }
            _loading = true;
            request = new FeedRequest(_generation, null, _changeVersion, _items.Count);
            return true;
        }
    }

    /// <summary>Starts a page-append request on the current cursor; the
    /// returned <see cref="FeedRequest"/> carries the cursor to send.</summary>
    public bool TryBeginPage(out FeedRequest request)
    {
        lock (_lock)
        {
            if (_loading || _nextCursor == null)
            {
                request = default;
                return false;
            }
            _loading = true;
            request = new FeedRequest(_generation, _nextCursor);
            return true;
        }
    }

    /// <summary>Applies a successful response - no-op when the request's
    /// generation went stale while it was in flight.</summary>
    public void ApplyPage(FeedRequest request, List<SyncshellCommunityFeedItemDto> items,
        string? nextCursor)
    {
        lock (_lock)
        {
            if (request.Generation != _generation) return;
            if (request.IsFirstPage)
            {
                _items = items;
                _requested = request.ChangeVersion == _changeVersion;
                if (_requested) _nextRetryUtc = default;
                _refreshFailed = false;
            }
            else
            {
                // Merge onto a NEW list - the published snapshot stays stable
                // for whoever still holds the previous Items reference.
                var merged = new List<SyncshellCommunityFeedItemDto>(_items);
                var seen = new HashSet<string>(_items.Select(KeyOf), StringComparer.Ordinal);
                foreach (var item in items)
                {
                    if (seen.Add(KeyOf(item))) merged.Add(item);
                }
                _items = merged;
                _pageFailed = false;
            }
            _nextCursor = nextCursor;
        }
    }

    /// <summary>Marks a failed response - no-op for stale generations. First-page
    /// failures unlatch the request flag behind the backoff; page failures keep
    /// items + cursor so the same page can be retried.</summary>
    public void ApplyFailure(FeedRequest request, DateTime utcNow)
    {
        lock (_lock)
        {
            if (request.Generation != _generation) return;
            if (request.IsFirstPage)
            {
                // Unlatch so the next render retries - throttled by the
                // backoff so a persistent outage doesn't request per frame.
                _requested = false;
                _nextRetryUtc = utcNow + RetryBackoff;
                _refreshFailed = true;
            }
            else
            {
                _pageFailed = true;
            }
        }
    }

    /// <summary>Releases the in-flight slot - only while the finishing
    /// request still owns the generation. <see cref="Invalidate"/> frees the
    /// slot early so the new generation can request immediately; a stale
    /// request's finally then must not evict the new owner.</summary>
    public void EndRequest(FeedRequest request)
    {
        lock (_lock)
        {
            if (request.Generation != _generation) return;
            _loading = false;
        }
    }

    /// <summary>Dedupe identity - activity rows can repeat (kind,id) pairs
    /// across actors, and insert races can resend a row across a cursor move.
    /// The row key keeps legitimately-distinct rows (same actor, same tick)
    /// from collapsing into one another.</summary>
    private static string KeyOf(SyncshellCommunityFeedItemDto i)
        => $"{i.Kind}:{i.Id}:{i.ActorUid}:{i.TimestampUtc.Ticks}:{i.FeedRowKey}";
    /// <summary>Single mapping from a reaction summary onto a feed row -
    /// thumbs variants count as likes, everything else groups into chips
    /// with the viewer's own state. Every reaction patch path (toggle
    /// responses, live refetches) funnels through here so the row shape can
    /// never diverge between write paths. Rows from older servers lack the
    /// emoji breakdown - the int fallbacks keep their prior state then.</summary>
    internal static SyncshellCommunityFeedItemDto WithReactionSummary(
        SyncshellCommunityFeedItemDto item, ReactionSummaryDto summary)
    {
        var viewerEmojis = summary.ViewerEmojis;
        var thumbsUp = Sphene.API.Data.Enum.ReactionEmojiMap.ThumbsUp;
        var liked = viewerEmojis != null
            ? viewerEmojis.Any(e => e.StartsWith(thumbsUp, StringComparison.Ordinal))
            : summary.ViewerKinds.Contains((int)ReactionKind.Like);
        int? likeCount = null;
        List<FeedReactionEmojiDto>? chips = null;
        if (summary.EmojiCounts != null)
        {
            var likes = 0;
            chips = new List<FeedReactionEmojiDto>();
            foreach (var kv in summary.EmojiCounts)
            {
                if (kv.Value <= 0) continue;
                if (kv.Key.StartsWith(thumbsUp, StringComparison.Ordinal))
                {
                    likes += kv.Value;
                }
                else
                {
                    chips.Add(new FeedReactionEmojiDto(kv.Key, kv.Value,
                        viewerEmojis?.Contains(kv.Key, StringComparer.Ordinal) == true));
                }
            }
            likeCount = likes;
            chips = chips.OrderByDescending(e => e.Count).ToList();
        }
        return item with
        {
            ReactionCount = summary.TotalCount,
            LikedByMe = liked,
            LikeCount = likeCount ?? item.LikeCount,
            ReactionEmojis = chips ?? item.ReactionEmojis,
        };
    }
}
