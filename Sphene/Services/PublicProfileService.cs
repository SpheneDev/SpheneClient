using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.WebAPI;

namespace Sphene.Services;

/// <summary>
/// Session state for the public Community-Hub surface - profile search, the
/// per-uid public-profile cache and the "Following" feed. Kept separate from
/// <see cref="SpheneProfileManager"/> pair caches on purpose: a public entry
/// expires on a short TTL and on targeted invalidation (profile pushes,
/// blocks, follow changes), never on a pair-cache wipe.
///
/// All fetches are generation-bound through <see cref="PublicProfileStore"/>,
/// so a response landing after a query change, uid invalidation or reconnect
/// is discarded instead of reviving revoked data.
/// </summary>
public class PublicProfileService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ApiController _apiController;
    private readonly SpheneConfigService _configService;
    private readonly PublicProfileStore _store = new();

    /// <summary>Result-page size for the People search - the request asks for
    /// one more row than delivered so a follow-up page is provable.</summary>
    public const int SearchPageSize = 20;

    public PublicProfileService(ILogger<PublicProfileService> logger,
        SpheneMediator mediator, ApiController apiController,
        SpheneConfigService configService)
        : base(logger, mediator)
    {
        _apiController = apiController;
        _configService = configService;
        Mediator.Subscribe<HubReconnectedMessage>(this, (_) => InvalidateAll());
        Mediator.Subscribe<HubClosedMessage>(this, (_) => InvalidateAll());
        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData is { } user)
            {
                _store.InvalidateProfile(user.UID);
            }
            else
            {
                InvalidateAll();
            }
        });
    }

    public PublicProfileStore Store => _store;
    public string Query => _store.Query;
    public IReadOnlyList<PublicProfileSummaryDto> SearchResults => _store.Results;
    public bool SearchLoading => _store.SearchLoading;
    public bool SearchFailed => _store.SearchFailed;
    public bool SearchHasMore => _store.SearchHasMore;

    /// <summary>Whether this server speaks the public-profile contract -
    /// additive capability, so an old server reports false and the surfaces
    /// stay hidden instead of failing calls.</summary>
    public bool Enabled => _apiController.ServerInfo.PublicProfilesEnabled;

    /// <summary>The viewer's NSFW-content gate - read per frame so a live
    /// settings change redacts warm entries immediately.</summary>
    public bool NsfwAllowed => _configService.Current.ProfilesAllowNsfw;

    /// <summary>Kicks a search for <paramref name="query"/>; concurrent calls
    /// dedupe, a changed query supersedes the pending fetch.</summary>
    public void Search(string query)
    {
        if (!Enabled) return;
        if (!_store.TryBeginSearch(query, out var request)) return;
        _ = FetchSearchPage(request);
    }

    /// <summary>Fetches the next result page for the current query -
    /// refused without <see cref="PublicProfileStore.SearchHasMore"/>.</summary>
    public void LoadMoreSearch()
    {
        if (!_store.TryBeginSearchPage(out var request)) return;
        _ = FetchSearchPage(request);
    }

    private async Task FetchSearchPage(PublicProfileStore.SearchRequest request)
    {
        try
        {
            var rows = await _apiController.UserSearchPublicProfiles(
                new PublicProfileSearchDto(request.Query,
                    Skip: request.Skip, Take: SearchPageSize + 1))
                .ConfigureAwait(false);
            var hasMore = rows.Count > SearchPageSize;
            if (hasMore) rows.RemoveRange(SearchPageSize, rows.Count - SearchPageSize);
            _store.ApplySearch(request, rows, hasMore);
            _searchAppliedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Public profile search failed");
            _store.ApplySearchFailure(request);
        }
        finally { _store.EndSearch(request); }
    }

    /// <summary>Silently revalidates a warm result set while the People tab
    /// sits open - revocation/block pushes handle the common case, this is
    /// the bounded fallback for a missed or unavailable push. EVERY held row
    /// is covered: the window [0, position+1) is re-pulled in chunks of
    /// ≤50 rows (the server's Take bound) and applied as one snapshot. A
    /// successful empty result re-pulls the first page so a re-enabled or
    /// unblocked profile can surface without a retype.</summary>
    public void EnsureSearchFresh()
    {
        if (!Enabled) return;
        var now = DateTime.UtcNow;
        if (now - _searchAppliedUtc < SearchRevalidation) return;
        var query = _store.Query;
        if (string.IsNullOrEmpty(query)) return;
        _searchAppliedUtc = now;
        if (_store.SearchPosition == 0)
        {
            Search(query);
            return;
        }
        if (!_store.TryBeginSearchRefresh(out var request)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var want = _store.SearchPosition + 1; // +1 re-proves hasMore
                var delivered = new List<PublicProfileSummaryDto>(want);
                var hops = 0;
                while (delivered.Count < want && hops++ < MaxRefreshHops)
                {
                    var thisTake = Math.Min(50, want - delivered.Count);
                    var rows = await _apiController.UserSearchPublicProfiles(
                        new PublicProfileSearchDto(request.Query,
                            Skip: delivered.Count, Take: thisTake))
                        .ConfigureAwait(false);
                    delivered.AddRange(rows);
                    if (rows.Count < thisTake) break; // server ran out of rows
                }
                var hasMore = delivered.Count > _store.SearchPosition;
                if (delivered.Count > _store.SearchPosition)
                {
                    delivered.RemoveRange(_store.SearchPosition,
                        delivered.Count - _store.SearchPosition);
                }
                _store.ApplySearchRefresh(request, delivered, hasMore);
                _searchAppliedUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Public profile search refresh failed");
            }
            finally { _store.EndSearch(request); }
        });
    }

    /// <summary>The same placeholder line <see cref="SpheneProfileManager"/>
    /// substitutes for a redacted bio.</summary>
    public const string NsfwPlaceholder = "Profile not displayed - NSFW";

    /// <summary>NSFW gate applied at read time - a live settings change or a
    /// warm cache entry redacts immediately, matching the substitution
    /// <see cref="SpheneProfileManager"/> performs on pair profiles. Media
    /// and authored text are hidden behind the same placeholder; identity
    /// and the NSFW flag itself stay so the surface can badge and still
    /// offer unfollow.</summary>
    public PublicProfileDto? RedactIfNsfw(PublicProfileDto? dto)
    {
        if (dto == null || NsfwAllowed || !dto.IsNsfw) return dto;
        return dto with
        {
            ProfilePictureBase64 = null,
            BannerImageBase64 = null,
            Description = NsfwPlaceholder,
            Tags = null,
            Chronicles = [],
        };
    }

    /// <summary>Same gate for a search row - the avatar hash is the only
    /// protected content on a summary.</summary>
    public PublicProfileSummaryDto RedactIfNsfw(PublicProfileSummaryDto hit)
    {
        if (NsfwAllowed || !hit.IsNsfw) return hit;
        return hit with { AvatarMediaHash = null };
    }

    /// <summary>Same gate for a Following-feed row - title, snippet and
    /// thumbnail are authored content and redact on an NSFW author.</summary>
    public SyncshellCommunityFeedItemDto RedactIfNsfw(SyncshellCommunityFeedItemDto item)
    {
        if (NsfwAllowed || !item.OwnerIsNsfw) return item;
        return item with
        {
            Title = NsfwPlaceholder,
            Snippet = null,
            ThumbnailMediaHash = null,
            ThumbnailBase64 = null,
        };
    }

    /// <summary>Clears the result list when the search surface closes.</summary>
    public void ClearSearch() => _store.ClearSearch();

    /// <summary>The cached profile for <paramref name="uid"/>; starts the
    /// fetch when missing or past the revalidation TTL.
    /// Resolved+null means "not available" - private, blocked or removed.</summary>
    public (bool Resolved, PublicProfileDto? Dto) EnsureProfile(string uid)
    {
        // Capability gate: an old API-33 server has no public-profile
        // methods - report "not available" without calling into the void.
        if (!Enabled) return (true, null);
        var snapshot = _store.Profile(uid);
        if (!_store.ShouldFetchProfile(uid, DateTime.UtcNow)) return snapshot;
        _ = FetchProfile(uid);
        return snapshot;
    }

    /// <summary>Bounded revalidation for an open Following tab - the feed
    /// has no push of its own, so a warm list silently refetches after the
    /// window instead of trusting silence. Profile/block pushes still
    /// invalidate it instantly.</summary>
    private static readonly TimeSpan FollowingRevalidation = TimeSpan.FromSeconds(60);
    private DateTime _followingFetchedUtc = DateTime.MinValue;

    /// <summary>Same window for warm People results - a revocation between
    /// pushes or a missed push is caught the next time the tab draws.</summary>
    private static readonly TimeSpan SearchRevalidation = TimeSpan.FromSeconds(60);

    /// <summary>Refresh chunk bound - 10 × 50-row requests cover 500 held
    /// results per pass; anything beyond stays on push invalidation.</summary>
    private const int MaxRefreshHops = 10;
    private DateTime _searchAppliedUtc = DateTime.MinValue;

    /// <summary>Follow/unfollow - optimistic flag flip, rollback on rejection
    /// or error. A successful call invalidates the target entry and the
    /// following feed so revoked content can't linger.</summary>
    public async Task<bool> SetFollowAsync(string uid, bool follow)
    {
        if (!Enabled) return false;
        _store.SetFollowFlag(uid, follow);
        var ok = false;
        try
        {
            ok = await _apiController.UserSetProfileFollow(
                new UserProfileFollowDto(new API.Data.UserData(uid), follow))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Follow toggle failed for {Uid}", uid);
        }
        if (!ok)
        {
            _store.SetFollowFlag(uid, !follow);
            return false;
        }
        // Follow changes keep the People row - only the profile entry and
        // the following feed refetch; the row already shows the confirmed
        // flag via SetFollowFlag.
        _store.InvalidateProfileData(uid);
        Mediator.Publish(new PublicFollowChangedMessage(uid, follow));
        return true;
    }

    /// <summary>Following feed accessors - a dedicated store, never the All feed.</summary>
    public IReadOnlyList<SyncshellCommunityFeedItemDto> FollowingItems => _store.FollowingFeed.Items;
    public bool FollowingLoading => _store.FollowingFeed.IsLoading;
    public bool FollowingHasMore => _store.FollowingFeed.HasMore;
    public bool FollowingRefreshFailed => _store.FollowingFeed.RefreshFailed;
    public bool FollowingPageFailed => _store.FollowingFeed.PageFailed;

    public void EnsureFollowingRequested()
    {
        if (!Enabled) return;
        var now = DateTime.UtcNow;
        if (_store.FollowingFeed.Items.Count > 0
            && now - _followingFetchedUtc >= FollowingRevalidation)
        {
            // Silent coverage refresh - re-pull enough pages to cover every
            // held row instead of invalidating back to page one.
            _followingFetchedUtc = now;
            _ = RefreshFollowingPreserving();
            return;
        }
        if (!_store.FollowingFeed.ShouldRequest(now)) return;
        _store.FollowingFeed.MarkRequested();
        _ = RefreshFollowing();
    }

    public void InvalidateFollowing() => _store.FollowingFeed.Invalidate();

    /// <summary>Patches a like toggle's authoritative result into the
    /// Following feed - same in-place update the community stores get, so
    /// a liked chronicle flips its heart without a refetch.</summary>
    public void ApplyReactionSummary(SyncshellFeedItemKind kind, long itemId,
        ReactionSummaryDto summary)
        => _store.FollowingFeed.PatchReaction(kind, itemId, summary);

    public async Task RefreshFollowing()
    {
        if (!_store.FollowingFeed.TryBeginRefresh(out var request)) return;
        try
        {
            var page = await _apiController.UserGetFollowingFeed(
                new FeedPageRequestDto()).ConfigureAwait(false);
            _store.FollowingFeed.ApplyPage(request, page.Items, page.NextCursor);
            _followingFetchedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Following feed fetch failed");
            _store.FollowingFeed.ApplyFailure(request, DateTime.UtcNow);
        }
        finally { _store.FollowingFeed.EndRequest(request); }
    }

    public async Task LoadMoreFollowingAsync()
    {
        if (!_store.FollowingFeed.TryBeginPage(out var request)) return;
        try
        {
            var page = await _apiController.UserGetFollowingFeed(
                new FeedPageRequestDto(request.Cursor)).ConfigureAwait(false);
            _store.FollowingFeed.ApplyPage(request, page.Items, page.NextCursor);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Following feed page fetch failed");
            _store.FollowingFeed.ApplyFailure(request, DateTime.UtcNow);
        }
        finally { _store.FollowingFeed.EndRequest(request); }
    }

    /// <summary>Silent revalidation that keeps every loaded page: cursor-walks
    /// 30-row chunks until the fresh set covers the held row count (bounded at
    /// <see cref="MaxFeedRefreshHops"/> hops), then replaces the snapshot in
    /// one apply. Revoked tail rows legitimately disappear; the user never
    /// bounces to page one while enough content still exists.</summary>
    private const int MaxFeedRefreshHops = 20; // 20 × 30 = 600 held rows

    private async Task RefreshFollowingPreserving()
    {
        if (!_store.FollowingFeed.TryBeginRefresh(out var request)) return;
        try
        {
            var held = _store.FollowingFeed.Items.Count;
            var fresh = new List<SyncshellCommunityFeedItemDto>(held);
            string? cursor = null;
            var hops = 0;
            while (fresh.Count < held && hops++ < MaxFeedRefreshHops)
            {
                var page = await _apiController.UserGetFollowingFeed(
                    new FeedPageRequestDto(cursor)).ConfigureAwait(false);
                fresh.AddRange(page.Items);
                cursor = page.NextCursor;
                if (cursor == null || page.Items.Count == 0) break;
            }
            _store.FollowingFeed.ApplyPage(request, fresh, cursor);
            _followingFetchedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Following feed refresh failed");
            // Silent retry on the next revalidation window - a failed
            // background pass keeps the held pages and must not unlatch
            // _requested into a page-one bounce.
        }
        finally { _store.FollowingFeed.EndRequest(request); }
    }

    /// <summary>Force-refetches a profile ignoring the TTL - manual refresh
    /// and the just-followed transition use this.</summary>
    public void InvalidateProfile(string uid) => _store.InvalidateProfile(uid);

    private async Task FetchProfile(string uid)
    {
        var generation = _store.BeginProfileFetch(uid);
        try
        {
            var dto = await _apiController.UserGetPublicProfile(
                new UserDto(new API.Data.UserData(uid))).ConfigureAwait(false);
            _store.ApplyProfile(uid, generation, dto, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Public profile fetch failed for {Uid}", uid);
            // A transport failure keeps the entry unresolved - retry on the
            // next read; a null DTO is the only "not available" verdict.
        }
        finally { _store.EndProfileFetch(uid, generation); }
    }

    private void InvalidateAll() => _store.InvalidateAll();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        InvalidateAll();
        return Task.CompletedTask;
    }
}
