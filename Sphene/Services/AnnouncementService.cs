using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.Announcement;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration.Models;
using Sphene.WebAPI;
using System.Net.Http;

namespace Sphene.Services;

/// <summary>
/// Session cache for server-published announcements · fetched once on demand
/// and on hub reconnect, live-invalidated by the publish/change pushes.
/// Banner and markdown images resolve through <see cref="GetCachedImage"/>:
/// absolute http(s) URLs only, fetched lazily so the first frame renders a
/// placeholder and the texture lands a frame later.
/// </summary>
public class AnnouncementService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ApiController _apiController;
    private readonly HttpClient _httpClient;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, byte[]?> _imageCache = new(StringComparer.Ordinal);
    private List<AnnouncementDto> _items = [];
    private List<AnnouncementDto> _staffItems = [];
    private bool _loading;
    private bool _requested;
    private bool _staffRequested;

    public AnnouncementService(ILogger<AnnouncementService> logger,
        SpheneMediator mediator, ApiController apiController, HttpClient httpClient)
        : base(logger, mediator)
    {
        _apiController = apiController;
        _httpClient = httpClient;
        Mediator.Subscribe<HubReconnectedMessage>(this, (_) => Invalidate());
        Mediator.Subscribe<HubClosedMessage>(this, (_) => Invalidate());
        Mediator.Subscribe<AnnouncementPublishedMessage>(this, OnPublished);
        Mediator.Subscribe<AnnouncementsChangedMessage>(this, (_) => Invalidate());
    }

    /// <summary>Pinned-first, newest-next snapshot · empty until the first refresh lands.</summary>
    public IReadOnlyList<AnnouncementDto> Items
    {
        get
        {
            lock (_lock)
            {
                return _items;
            }
        }
    }

    /// <summary>
    /// Staff list · every live announcement incl. drafts, for the admin
    /// surfaces. Empty for non-staff callers (the server returns [] anyway).
    /// </summary>
    public IReadOnlyList<AnnouncementDto> StaffItems
    {
        get
        {
            lock (_lock)
            {
                return _staffItems;
            }
        }
    }

    /// <summary>True while a fetch is in flight.</summary>
    public bool IsLoading
    {
        get { lock (_lock) { return _loading; } }
    }

    /// <summary>
    /// Kicks a fetch the first time a news surface renders (idempotent).
    /// Safe to call every frame · deduped against in-flight requests.
    /// </summary>
    public void EnsureRequested()
    {
        lock (_lock)
        {
            if (_requested || _loading)
            {
                return;
            }
            _requested = true;
        }
        _ = Refresh();
    }

    /// <summary>
    /// Kicks the staff fetch for admin surfaces (idempotent · safe every
    /// frame). Only call it when the connection actually reports staff -
    /// the server returns an empty list for anyone else.
    /// </summary>
    public void EnsureStaffRequested()
    {
        lock (_lock)
        {
            if (_staffRequested || _loading)
            {
                return;
            }
            _staffRequested = true;
        }
        _ = RefreshStaff();
    }

    /// <summary>Drops both caches; the next ensure-call refetches.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _requested = false;
            _staffRequested = false;
            _items = [];
            _staffItems = [];
        }
    }

    /// <summary>Fetches the feed; dedupes concurrent calls.</summary>
    public async Task Refresh()
    {
        lock (_lock)
        {
            if (_loading)
            {
                return;
            }
            _loading = true;
        }
        try
        {
            var items = await _apiController.GetAnnouncements().ConfigureAwait(false);
            lock (_lock)
            {
                _items = items;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Announcement fetch failed");
        }
        finally
        {
            lock (_lock)
            {
                _loading = false;
            }
        }
    }

    /// <summary>Fetches the staff list; dedupes concurrent calls.</summary>
    public async Task RefreshStaff()
    {
        lock (_lock)
        {
            if (_loading)
            {
                return;
            }
            _loading = true;
        }
        try
        {
            var items = await _apiController.AdminGetAnnouncements().ConfigureAwait(false);
            lock (_lock)
            {
                _staffItems = items;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Announcement staff fetch failed");
        }
        finally
        {
            lock (_lock)
            {
                _loading = false;
            }
        }
    }

    /// <summary>
    /// Cached bytes for a remote image URL · first call starts the fetch and
    /// returns null, the texture appears on the next call. Non-http(s) or
    /// empty URLs never fetch.
    /// </summary>
    public byte[]? GetCachedImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                 || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        if (_imageCache.TryGetValue(url, out var cached)) return cached;
        _imageCache[url] = null;
        var fetch = url;
        _ = Task.Run(async () =>
        {
            try
            {
                _imageCache[fetch] = await _httpClient.GetByteArrayAsync(fetch)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Announcement image fetch failed: {url}", fetch);
            }
        });
        return null;
    }

    /// <summary>A publish landed · toast into the real inbox and refresh the cache.</summary>
    private void OnPublished(AnnouncementPublishedMessage msg)
    {
        Invalidate();
        Mediator.Publish(new NotificationMessage(
            $"Announcement: {msg.Announcement.Title}",
            string.IsNullOrWhiteSpace(msg.Announcement.Summary)
                ? "New announcement published."
                : msg.Announcement.Summary,
            NotificationType.Info));
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Invalidate();
        return Task.CompletedTask;
    }
}
