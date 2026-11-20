using Sphene.API.Dto.User;

namespace Sphene.Services;

/// <summary>
/// Dependency-free state machine behind <see cref="PublicProfileService"/> -
/// search results, the per-uid public-profile cache and the "Following" feed
/// (a dedicated <see cref="CommunityFeedStore"/> so its cursor can never
/// collide with the All feed).
///
/// Every response is bound to the state that issued it: search answers to a
/// (generation, query, position, stamp) tuple, profile answers to a per-uid
/// generation. The result list is only ever REPLACED, never mutated in place -
/// readers hold a stable snapshot. A monotonic invalidation stamp lets a late
/// answer drop rows whose target was revoked or re-followed after the request
/// was issued, without killing the whole query's remaining rows.
/// </summary>
public sealed class PublicProfileStore
{
    /// <summary>Bounded revalidation for open public views - older entries
    /// refetch once on the next read instead of trusting a push that may
    /// never arrive (block, sharing toggled off, profile disabled).</summary>
    public static readonly TimeSpan ProfileTtl = TimeSpan.FromSeconds(45);

    /// <summary>One in-flight search request bound to (generation, query,
    /// skip, stamp) - page fetches share the query's generation so a query
    /// change kills a still-flying page answer; <paramref name="Stamp"/>
    /// records the invalidation clock at issue time so an answer issued
    /// before a revocation can't re-insert the revoked row.</summary>
    public readonly record struct SearchRequest(int Generation, string Query,
        int Skip, long Stamp);

    /// <summary>Per-uid cache entry: resolved profiles and explicit
    /// "not available" answers share one slot; only one fetch at a time.</summary>
    private sealed class ProfileEntry
    {
        public PublicProfileDto? Dto;
        public bool Resolved;
        public bool Loading;
        public int Generation;
        public DateTime FetchedAtUtc;
    }

    private readonly Lock _lock = new();
    private string _query = string.Empty;
    private IReadOnlyList<PublicProfileSummaryDto> _results = [];
    private bool _searchLoading;
    private bool _searchFailed;
    private bool _searchHasMore;
    private int _generation;
    /// <summary>Server rows consumed by the held pages - the paging position,
    /// decoupled from the VISIBLE row count (revoked rows leave the snapshot
    /// but keep their consumed position).</summary>
    private int _position;
    /// <summary>Monotonic clock for every invalidation/override - requests
    /// bind its value at issue time; an answer only filters rows stamped
    /// AFTER its issue stamp.</summary>
    private long _stamp;
    /// <summary>Uids revoked after their stamp - late answers drop them,
    /// fresh requests (stamped later) see through to post-revoke truth.</summary>
    private readonly Dictionary<string, long> _invalidatedUidStamps = new(StringComparer.Ordinal);
    /// <summary>FIFO order for <see cref="_invalidatedUidStamps"/> - evicts
    /// the oldest revoke so the bound never drops all protection at once.</summary>
    private readonly Queue<string> _invalidatedOrder = new();
    /// <summary>Last locally-confirmed follow flag per uid, stamped - late
    /// answers can't regress a confirmed toggle; cleared on fresh searches.</summary>
    private readonly Dictionary<string, (bool Following, long Stamp)> _followOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProfileEntry> _profiles = new(StringComparer.Ordinal);

    /// <summary>The dedicated Following feed - separate items and cursor.</summary>
    public CommunityFeedStore FollowingFeed { get; } = new();

    /// <summary>The query whose results are currently held.</summary>
    public string Query
    {
        get { lock (_lock) { return _query; } }
    }

    /// <summary>Results for <see cref="Query"/> - an immutable snapshot list
    /// that is only replaced, so callers can read it between frames safely.</summary>
    public IReadOnlyList<PublicProfileSummaryDto> Results
    {
        get { lock (_lock) { return _results; } }
    }

    public bool SearchLoading
    {
        get { lock (_lock) { return _searchLoading; } }
    }

    /// <summary>The last search request failed - distinct from a legitimately
    /// empty result so the UI can offer retry instead of "no matches".</summary>
    public bool SearchFailed
    {
        get { lock (_lock) { return _searchFailed; } }
    }

    /// <summary>A follow-up page exists - the server proved it via the +1
    /// row beyond the page size.</summary>
    public bool SearchHasMore
    {
        get { lock (_lock) { return _searchHasMore; } }
    }

    /// <summary>Server rows consumed so far - the position the next page
    /// continues from.</summary>
    public int SearchPosition
    {
        get { lock (_lock) { return _position; } }
    }

    /// <summary>Starts a fresh search for <paramref name="query"/> - a changed
    /// query resets paging state and bumps the generation immediately, so a
    /// still-flying answer for the old query can never repopulate the list.
    /// A same-query retry supersedes an in-flight page/refresh too.</summary>
    public bool TryBeginSearch(string query, out SearchRequest request)
    {
        lock (_lock)
        {
            var changed = !string.Equals(query, _query, StringComparison.Ordinal);
            if (changed || !_searchLoading)
            {
                if (changed)
                {
                    _query = query;
                    _followOverrides.Clear();
                }
                _generation++;
                _results = [];
                _position = 0;
                _searchHasMore = false;
                _searchFailed = false;
                _searchLoading = true;
                request = new SearchRequest(_generation, query, 0, _stamp);
                return true;
            }
            request = default;
            return false;
        }
    }

    /// <summary>Begins the next page for the current query - refused while a
    /// request is in flight, after a failure (retry reloads the first page)
    /// or once the server said no more rows exist. The continuation is the
    /// consumed server-row <see cref="SearchPosition"/>, not the visible row
    /// count - revoked rows already left the snapshot.</summary>
    public bool TryBeginSearchPage(out SearchRequest request)
    {
        lock (_lock)
        {
            if (_searchLoading || _searchFailed || !_searchHasMore
                || string.IsNullOrEmpty(_query))
            {
                request = default;
                return false;
            }
            _searchLoading = true;
            request = new SearchRequest(_generation, _query, _position, _stamp);
            return true;
        }
    }

    /// <summary>Begins a silent revalidation of the visible result set -
    /// chunked requests cover every held row (never one-per-row). Refused
    /// while anything is in flight.</summary>
    public bool TryBeginSearchRefresh(out SearchRequest request)
    {
        lock (_lock)
        {
            if (_searchLoading || string.IsNullOrEmpty(_query)
                || _position == 0)
            {
                request = default;
                return false;
            }
            _searchLoading = true;
            request = new SearchRequest(_generation, _query, -1, _stamp);
            return true;
        }
    }

    /// <summary>Replaces the visible set with its revalidated truth - revoked
    /// rows vanish, hidden avatars re-mask, follow flags resync to the newer
    /// of server answer and confirmed override. <paramref name="hasMore"/>
    /// is re-proved by the caller via the +1 window.</summary>
    public void ApplySearchRefresh(SearchRequest request,
        List<PublicProfileSummaryDto> results, bool hasMore)
    {
        lock (_lock)
        {
            if (request.Generation != _generation
                || !string.Equals(request.Query, _query, StringComparison.Ordinal)
                || request.Skip != -1) return;
            _results = StampFilter(results, request.Stamp);
            _position = _results.Count;
            _searchHasMore = hasMore;
        }
    }

    /// <summary>Applies a search page - dropped when the query moved on, the
    /// generation went stale, or the continuation no longer matches the
    /// consumed position. Rows stamped-revoked after issue are filtered;
    /// confirmed follow overrides stamp over stale flags.</summary>
    public void ApplySearch(SearchRequest request,
        List<PublicProfileSummaryDto> results, bool hasMore)
    {
        lock (_lock)
        {
            if (request.Generation != _generation
                || !string.Equals(request.Query, _query, StringComparison.Ordinal)
                || request.Skip != _position) return;
            if (results.Count > 0)
            {
                var filtered = StampFilter(results, request.Stamp);
                var merged = new List<PublicProfileSummaryDto>(_results.Count + filtered.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in _results) { merged.Add(r); seen.Add(r.User.UID); }
                foreach (var r in filtered)
                {
                    if (seen.Add(r.User.UID)) merged.Add(r);
                }
                _results = merged;
            }
            // Position advances by SERVER rows consumed, not by rows that
            // survived filtering - a revoked row still consumed an offset.
            _position += results.Count;
            _searchHasMore = hasMore;
        }
    }

    /// <summary>Marks the bound request failed - page failures keep the
    /// visible rows and flag retryable state, first-page failures flag the
    /// whole surface.</summary>
    public void ApplySearchFailure(SearchRequest request)
    {
        lock (_lock)
        {
            if (request.Generation != _generation
                || !string.Equals(request.Query, _query, StringComparison.Ordinal)) return;
            _searchFailed = true;
            _searchHasMore = false;
        }
    }

    public void EndSearch(SearchRequest request)
    {
        lock (_lock)
        {
            if (request.Generation != _generation) return;
            _searchLoading = false;
        }
    }

    /// <summary>The cached profile entry, if any - Resolved=false means the
    /// fetch hasn't landed yet; Resolved+Dto=null means "not available".</summary>
    public (bool Resolved, PublicProfileDto? Dto) Profile(string uid)
    {
        lock (_lock)
        {
            return _profiles.TryGetValue(uid, out var e)
                ? (e.Resolved, e.Dto) : (false, null);
        }
    }

    /// <summary>Whether a fetch for <paramref name="uid"/> may start: nothing
    /// in flight and the entry is unresolved or past the revalidation TTL.</summary>
    public bool ShouldFetchProfile(string uid, DateTime utcNow)
    {
        lock (_lock)
        {
            if (!_profiles.TryGetValue(uid, out var e)) return true;
            if (e.Loading) return false;
            return !e.Resolved || utcNow - e.FetchedAtUtc >= ProfileTtl;
        }
    }

    /// <summary>Begins a profile fetch; the returned generation binds the
    /// response so an invalidate can't be overwritten by a stale answer.</summary>
    public int BeginProfileFetch(string uid)
    {
        lock (_lock)
        {
            var e = Entry(uid);
            e.Loading = true;
            return e.Generation;
        }
    }

    /// <summary>Applies a profile response; null = neutral "not available".
    /// Stale generations are discarded; a confirmed follow toggle can't be
    /// regressed by a wire flag from a request issued before it.</summary>
    public void ApplyProfile(string uid, int generation, PublicProfileDto? dto,
        DateTime utcNow)
    {
        lock (_lock)
        {
            var e = Entry(uid);
            if (generation != e.Generation) return;
            if (dto != null && _followOverrides.TryGetValue(uid, out var o))
            {
                dto = dto with { Following = o.Following };
            }
            e.Dto = dto;
            e.Resolved = true;
            e.FetchedAtUtc = utcNow;
        }
    }

    public void EndProfileFetch(string uid, int generation)
    {
        lock (_lock)
        {
            var e = Entry(uid);
            if (generation != e.Generation) return;
            e.Loading = false;
        }
    }

    /// <summary>Optimistically flips the follow flag on a cached entry and on
    /// matching search rows; rolls back via a second call on failure. The
    /// stamped override also rewrites the flag inside late search answers -
    /// a request issued before the toggle can't regress the confirmed state.</summary>
    public void SetFollowFlag(string uid, bool following)
    {
        lock (_lock)
        {
            _followOverrides[uid] = (following, ++_stamp);
            if (_profiles.TryGetValue(uid, out var e) && e.Dto != null)
            {
                e.Dto = e.Dto with { Following = following };
            }
            _results = MapRow(_results, uid,
                r => r with { Following = following });
        }
    }

    /// <summary>Revocation invalidation (profile push, block, sharing off,
    /// disable): drops the profile entry, removes the uid's warm search rows,
    /// stamps the uid so answers issued BEFORE the revoke can't re-insert it,
    /// and invalidates the following feed.</summary>
    public void InvalidateProfile(string uid)
    {
        lock (_lock)
        {
            var e = Entry(uid);
            e.Generation++;
            e.Dto = null;
            e.Resolved = false;
            e.Loading = false;
            _invalidatedUidStamps[uid] = ++_stamp;
            _invalidatedOrder.Enqueue(uid);
            while (_invalidatedOrder.Count > 64)
            {
                _invalidatedUidStamps.Remove(_invalidatedOrder.Dequeue());
            }
            _results = _results.Count == 0
                ? _results
                : _results.Where(r => !string.Equals(r.User.UID, uid,
                    StringComparison.Ordinal)).ToList();
        }
        FollowingFeed.Invalidate();
    }

    /// <summary>Follow-state invalidation (local confirm): drops the profile
    /// entry and the following feed but keeps the People row - a
    /// follow/unfollow never revokes search eligibility.</summary>
    public void InvalidateProfileData(string uid)
    {
        lock (_lock)
        {
            var e = Entry(uid);
            e.Generation++;
            e.Dto = null;
            e.Resolved = false;
            e.Loading = false;
        }
        FollowingFeed.Invalidate();
    }

    /// <summary>Clears the search surface without a follow-up fetch - unlike
    /// <see cref="TryBeginSearch"/> it leaves no request latched, so
    /// <see cref="SearchLoading"/> can't stick on a closed surface.</summary>
    public void ClearSearch()
    {
        lock (_lock)
        {
            _generation++;
            _query = string.Empty;
            _results = [];
            _position = 0;
            _searchLoading = false;
            _searchFailed = false;
            _searchHasMore = false;
            _followOverrides.Clear();
        }
    }

    /// <summary>Drops everything - search, profiles and the following feed.
    /// Reconnect, account switch and manual refresh go through here.</summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _generation++;
            _stamp++;
            _results = [];
            _position = 0;
            _searchLoading = false;
            _searchFailed = false;
            _searchHasMore = false;
            _invalidatedUidStamps.Clear();
            _invalidatedOrder.Clear();
            _followOverrides.Clear();
            foreach (var e in _profiles.Values)
            {
                e.Generation++;
                e.Dto = null;
                e.Resolved = false;
                e.Loading = false;
            }
        }
        FollowingFeed.Invalidate();
    }

    /// <summary>Filters rows stamped-revoked after the request issued and
    /// applies stamped follow overrides - the newer of the wire answer and
    /// the confirmed local toggle wins.</summary>
    private List<PublicProfileSummaryDto> StampFilter(
        List<PublicProfileSummaryDto> rows, long requestStamp)
    {
        var filtered = new List<PublicProfileSummaryDto>(rows.Count);
        foreach (var r in rows)
        {
            if (_invalidatedUidStamps.TryGetValue(r.User.UID, out var s)
                && s > requestStamp) continue;
            filtered.Add(_followOverrides.TryGetValue(r.User.UID, out var o)
                && o.Stamp > requestStamp
                ? r with { Following = o.Following } : r);
        }
        return filtered;
    }

    private static IReadOnlyList<PublicProfileSummaryDto> MapRow(
        IReadOnlyList<PublicProfileSummaryDto> rows, string uid,
        Func<PublicProfileSummaryDto, PublicProfileSummaryDto> map)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].User.UID, uid, StringComparison.Ordinal))
            {
                var next = new List<PublicProfileSummaryDto>(rows);
                next[i] = map(next[i]);
                return next;
            }
        }
        return rows;
    }

    private ProfileEntry Entry(string uid)
    {
        if (!_profiles.TryGetValue(uid, out var e))
        {
            e = new ProfileEntry();
            _profiles[uid] = e;
        }
        return e;
    }
}
