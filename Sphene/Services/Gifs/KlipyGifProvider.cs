using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;

namespace Sphene.Services.Gifs;

/// <summary>
/// KLIPY backend for private-chat GIFs. Dedicated HttpClient (no Sphene
/// auth headers), <c>*.klipy.com</c> host allowlist for media downloads,
/// bounded byte reads, a negative cache so failures never hot-loop, and
/// <c>Retry-After</c>-aware rate limiting - test keys cap at 100 req/h
/// until a production key is approved. The key comes from the connected
/// server's <c>ServerInfo</c>; a local config key is the dev fallback.
/// Media bytes stay in memory only: KLIPY terms forbid retaining copies,
/// only search-result thumbnails may be kept client-side.
/// </summary>
public sealed class KlipyGifProvider : DisposableMediatorSubscriberBase, IGifProvider, IHostedService
{
    public const int PageSize = 24;
    private const string ApiBase = "https://api.klipy.com/api/v1";
    private const int MaxDownloadBytes = 5 * 1024 * 1024;
    private const int NegativeCacheSeconds = 600;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // file-group preference: md (~300px) fits the 320px bubble best, sm as
    // the 220px fallback, hd only when nothing smaller exists (size-capped).
    private static readonly string[] MediaPreference = ["md", "sm", "hd", "xs"];
    private static readonly string[] PreviewPreference = ["sm", "xs", "md"];
    // per-catalog rendition formats in fallback order - stickers are
    // usually static or animated webp, gifs are gif-first
    private static readonly string[] GifFormats = ["gif", "webp"];
    private static readonly string[] StickerFormats = ["webp", "gif", "jpg", "png"];

    private const int MaxPersistedMeta = 512;
    private const string MetaFileName = "gif-meta.json";
    /// <summary>Session-scoped media byte budget - KLIPY terms forbid
    /// retaining media on disk, so recently fetched GIFs/previews live in
    /// an LRU that is dropped with the process. Re-opened conversations
    /// never refetch the network for media already shown this session.</summary>
    private const long MaxCachedMediaBytes = 48L * 1024 * 1024;

    private readonly HttpClient _http = new() { Timeout = RequestTimeout };
    private readonly SpheneConfigService _configService;
    private readonly GifApiMetrics _metrics = new();
    private readonly ConcurrentDictionary<string, GifItem> _meta = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _metaOrder = new();
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _pendingBytes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _byteCache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Key, int Size)> _byteOrder = new();
    private long _byteCacheSize;
    private readonly ConcurrentDictionary<string, DateTime> _negative = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _searchLock = new(1, 1);
    private readonly System.Threading.Lock _metaFileLock = new();
    private readonly string? _stateDir;
    private string? _serverApiKey;
    private bool _serverGifAllowed;
    private bool _metaLoaded;
    private bool _metaDirty;
    private int _serverKeyLimit = GifApiMetrics.AssumedHourlyLimit;
    private DateTime _rateLimitedUntil = DateTime.MinValue;

    public KlipyGifProvider(ILogger<KlipyGifProvider> logger, SpheneMediator mediator,
        SpheneConfigService configService, string? stateDir = null) : base(logger, mediator)
    {
        _configService = configService;
        _stateDir = stateDir;
        Mediator.Subscribe<ConnectedMessage>(this, m =>
        {
            _serverApiKey = m.Connection.ServerInfo.GifApiKey;
            _serverGifAllowed = m.Connection.ServerInfo.GifEnabled;
            _serverKeyLimit = m.Connection.ServerInfo.GifTestKeyLimit > 0
                ? m.Connection.ServerInfo.GifTestKeyLimit
                : GifApiMetrics.AssumedHourlyLimit;
        });
        Mediator.Subscribe<DisconnectedMessage>(this, _ =>
        {
            _serverApiKey = null;
            _serverGifAllowed = false;
            _serverKeyLimit = GifApiMetrics.AssumedHourlyLimit;
        });
    }

    public string ProviderId => "klipy";
    public string DisplayName => "KLIPY";
    public string SearchPlaceholder => "Search KLIPY";
    public string AttributionText => "Powered by KLIPY";

    /// <inheritdoc cref="IGifProvider.IsConfigured"/>
    /// Server grants the capability (<c>ServerInfo.GifEnabled</c>) AND a key
    /// is available - the server's master switch is never bypassed by the
    /// local dev/test key.
    public bool IsConfigured => _serverGifAllowed && ActiveKey.Length > 0;

    /// <inheritdoc cref="IGifProvider.ConsentGiven"/>
    public bool ConsentGiven => _configService.Current.ChatExternalMediaEnabled;

    /// <inheritdoc cref="IGifProvider.Available"/>
    public bool Available => IsConfigured && ConsentGiven && DateTime.UtcNow >= _rateLimitedUntil;

    /// <inheritdoc cref="IGifProvider.RateLimited"/>
    public bool RateLimited => DateTime.UtcNow < _rateLimitedUntil;

    /// <inheritdoc cref="IGifProvider.Usage"/>
    public GifApiMetrics.Snapshot Usage => _metrics.GetSnapshot(_serverKeyLimit);

    private string ActiveKey
        => !string.IsNullOrEmpty(_serverApiKey)
            ? _serverApiKey!
            : _configService.Current.GifApiKey ?? string.Empty;

    /// <summary>
    /// KLIPY personalizes results per opaque customer id. A stable random
    /// per-installation id keeps that personalization without exposing the
    /// user's Sphene identity to the provider.
    /// </summary>
    private string CustomerId
    {
        get
        {
            var id = _configService.Current.GifCustomerId;
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                _configService.Current.GifCustomerId = id;
                _configService.Save();
            }
            return id;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Warm the persisted metadata cache off the draw path - the first
        // lazy load would otherwise do file IO inside a UI call.
        _ = Task.Run(EnsureMetaLoaded, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        FlushMeta();
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="IGifProvider.SearchAsync"/>
    public async Task<GifPage> SearchAsync(string query, int page, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await TrendingAsync(page, ct).ConfigureAwait(false);
        }
        var url = PageUrl("gifs", "search", page)
            + $"&q={Uri.EscapeDataString(query.Trim())}";
        return await RequestPageAsync(url, GifApiMetrics.Kind.Search, ct, sticker: false).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IGifProvider.TrendingAsync"/>
    public Task<GifPage> TrendingAsync(int page, CancellationToken ct)
        => RequestPageAsync(PageUrl("gifs", "trending", page), GifApiMetrics.Kind.Trending, ct, sticker: false);

    /// <inheritdoc cref="IGifProvider.SearchStickersAsync"/>
    public async Task<GifPage> SearchStickersAsync(string query, int page, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await TrendingStickersAsync(page, ct).ConfigureAwait(false);
        }
        var url = PageUrl("stickers", "search", page)
            + $"&q={Uri.EscapeDataString(query.Trim())}";
        return await RequestPageAsync(url, GifApiMetrics.Kind.Search, ct, sticker: true).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IGifProvider.TrendingStickersAsync"/>
    public Task<GifPage> TrendingStickersAsync(int page, CancellationToken ct)
        => RequestPageAsync(PageUrl("stickers", "trending", page), GifApiMetrics.Kind.Trending, ct, sticker: true);

    /// <inheritdoc cref="IGifProvider.GetByIdAsync"/>
    public async Task<GifItem?> GetByIdAsync(string id, CancellationToken ct)
    {
        // in-memory hits (search-parse, prior lookups) never touch the disk
        // load - the file read only pays for true misses, once
        if (_meta.TryGetValue(id, out var cached))
        {
            return cached;
        }
        EnsureMetaLoaded();
        if (_meta.TryGetValue(id, out cached))
        {
            return cached;
        }
        if (!Available || IsNegative(id) || !IsValidId(id))
        {
            return null;
        }
        // A gif slug misses on the sticker catalog and vice versa, so both
        // catalogs are tried; stickers ride the type "gif" wire payload
        // and resolve through this same path.
        var item = await FetchItemAsync("gifs", id, ct).ConfigureAwait(false)
            ?? await FetchItemAsync("stickers", id, ct).ConfigureAwait(false);
        if (item == null)
        {
            // only both-catalog misses mark negative - a gif miss must not
            // poison the sticker lookup of the same slug
            MarkNegative(id);
        }
        return item;
    }

    private async Task<GifItem?> FetchItemAsync(string catalog, string id, CancellationToken ct)
    {
        try
        {
            var url = $"{ApiBase}/{Uri.EscapeDataString(ActiveKey)}/{catalog}/items"
                + $"?slugs={Uri.EscapeDataString(id)}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!CheckResponse(resp, id: null))
            {
                _metrics.Record(GifApiMetrics.Kind.Lookup,
                    failed: !RateLimited, throttled: RateLimited);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            var item = FirstItem(doc.RootElement,
                string.Equals(catalog, "stickers", StringComparison.Ordinal));
            if (item != null)
            {
                TrackMeta(item);
                FlushMeta();
            }
            _metrics.Record(GifApiMetrics.Kind.Lookup);
            return item;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _metrics.Record(GifApiMetrics.Kind.Lookup, failed: true);
            Logger.LogDebug(ex, "KLIPY: failed to resolve {catalog} {id}", catalog, id);
            return null;
        }
    }

    /// <inheritdoc cref="IGifProvider.FetchPreviewBytesAsync"/>
    public Task<byte[]?> FetchPreviewBytesAsync(GifItem item, CancellationToken ct)
        => FetchBytesAsync(item.PreviewUrl, item.Id + ".prev", GifApiMetrics.Kind.Preview, ct);

    /// <inheritdoc cref="IGifProvider.FetchGifBytesAsync"/>
    public Task<byte[]?> FetchGifBytesAsync(GifItem item, CancellationToken ct)
        => FetchBytesAsync(item.MediaUrl, item.Id, GifApiMetrics.Kind.Media, ct);

    /// <inheritdoc cref="IGifProvider.RegisterShare"/>
    public void RegisterShare(GifItem item)
    {
        if (!Available || !IsValidId(item.Id))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // spec: POST /{key}/{catalog}/share/{slug} with customer_id body
                var catalog = item.IsSticker ? "stickers" : "gifs";
                var url = $"{ApiBase}/{Uri.EscapeDataString(ActiveKey)}/{catalog}/share/{Uri.EscapeDataString(item.Id)}";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(
                    $"{{\"customer_id\":\"{CustomerId}\"}}",
                    System.Text.Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                _metrics.Record(GifApiMetrics.Kind.Share,
                    failed: !resp.IsSuccessStatusCode,
                    throttled: (int)resp.StatusCode == 429);
            }
            catch (Exception ex)
            {
                _metrics.Record(GifApiMetrics.Kind.Share, failed: true);
                Logger.LogDebug(ex, "KLIPY: share trigger failed for {id}", item.Id);
            }
        });
    }

    /// <summary>Page URL for a catalog ("gifs"/"stickers") endpoint. The
    /// gif catalog is pinned to gif files via format_filter; stickers
    /// accept their native formats (webp/jpg/png/gif).</summary>
    private string PageUrl(string catalog, string endpoint, int page)
        => $"{ApiBase}/{Uri.EscapeDataString(ActiveKey)}/{catalog}/{endpoint}"
            + $"?page={page + 1}&per_page={PageSize}"
            + $"&customer_id={Uri.EscapeDataString(CustomerId)}"
            + (string.Equals(catalog, "gifs", StringComparison.Ordinal)
                ? "&format_filter=gif" : string.Empty);

    /// <summary>
    /// Media bytes are consent-gated only - no API key needed. KLIPY media
    /// URLs are already-public CDN links, so a user whose beta access was
    /// revoked can still read existing chat history without regaining
    /// search/create capability.
    /// </summary>
    private Task<byte[]?> FetchBytesAsync(string? mediaUrl, string cacheKey,
        GifApiMetrics.Kind kind, CancellationToken ct)
    {
        // negative marks are scoped to the fetch target: a failed preview
        // fetch must not poison the media fetch (or meta lookup) of the
        // same item for the whole cooldown
        // session-scoped byte cache: re-opening a chat or the picker never
        // refetches the network (KLIPY terms forbid disk persistence, so
        // the cache stays memory-only). Hits win over negative marks - a
        // later failed lookup/fetch must not hide bytes already fetched.
        if (_byteCache.TryGetValue(cacheKey, out var cached))
        {
            return Task.FromResult<byte[]?>(cached);
        }
        // negative marks are scoped to the fetch target: a failed preview
        // fetch must not poison the media fetch (or meta lookup) of the
        // same item for the whole cooldown
        if (IsNegative(cacheKey) || !ConsentGiven || mediaUrl == null)
        {
            return Task.FromResult<byte[]?>(null);
        }
        return _pendingBytes.GetOrAdd(cacheKey, key => FetchCoreAsync(key, mediaUrl, key, kind, ct));
    }

    private async Task<byte[]?> FetchCoreAsync(string id, string mediaUrl, string cacheKey,
        GifApiMetrics.Kind kind, CancellationToken ct)
    {
        try
        {
            if (!IsAllowedMediaUrl(mediaUrl))
            {
                MarkNegative(id);
                return null;
            }
            using var resp = await _http.GetAsync(mediaUrl,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!CheckResponse(resp, id))
            {
                _metrics.Record(kind, failed: !RateLimited, throttled: RateLimited);
                if (!RateLimited)
                {
                    Logger.LogDebug("KLIPY: media fetch {id} returned {status}",
                        id, (int)resp.StatusCode);
                }
                return null;
            }
            if (resp.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                MarkNegative(id);
                _metrics.Record(kind, failed: true);
                return null;
            }
            var bytes = await ReadBoundedAsync(resp, ct).ConfigureAwait(false);
            _metrics.Record(kind, failed: bytes == null);
            if (bytes == null)
            {
                // oversize aborts land here - without the negative mark the
                // bubble would refetch every frame it is visible
                MarkNegative(id);
            }
            else
            {
                CacheBytes(cacheKey, bytes);
            }
            return bytes;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            MarkNegative(id);
            _metrics.Record(kind, failed: true);
            Logger.LogDebug(ex, "KLIPY: download failed for {id}", id);
            return null;
        }
        finally
        {
            _pendingBytes.TryRemove(cacheKey, out _);
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            if (ms.Length > MaxDownloadBytes)
            {
                return null;
            }
        }
        return ms.ToArray();
    }

    /// <summary>Media fetches only ever go to KLIPY's own hosts over HTTPS.</summary>
    internal static bool IsAllowedMediaUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && (uri.Host.Equals("klipy.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".klipy.com", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly System.Text.RegularExpressions.Regex IdPattern
        = new("^[A-Za-z0-9_-]{3,64}$",
            System.Text.RegularExpressions.RegexOptions.Compiled
                | System.Text.RegularExpressions.RegexOptions.NonBacktracking);

    internal static bool IsValidId(string id) => IdPattern.IsMatch(id);

    private async Task<GifPage> RequestPageAsync(string url, GifApiMetrics.Kind kind,
        CancellationToken ct, bool sticker)
    {
        if (!IsConfigured)
        {
            return new GifPage(Array.Empty<GifItem>(), false, Error: "no-key");
        }
        if (!ConsentGiven)
        {
            return new GifPage(Array.Empty<GifItem>(), false, Error: "no-consent");
        }
        if (RateLimited)
        {
            return new GifPage(Array.Empty<GifItem>(), false, RateLimited: true);
        }
        await _searchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!CheckResponse(resp, id: null))
            {
                _metrics.Record(kind, failed: !RateLimited, throttled: RateLimited);
                return new GifPage(Array.Empty<GifItem>(), false,
                    RateLimited: RateLimited,
                    Error: RateLimited ? "rate-limited" : $"http-{(int)resp.StatusCode}");
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            var items = new List<GifItem>();
            var hasMore = false;
            // v1 envelope: { result, data: { data: [items], has_next, … } }
            // a 200 whose body we can't itemize must leave a debug trail -
            // a shape change otherwise surfaces as an unexplained empty grid
            if (TryItemsArray(doc.RootElement, out var results))
            {
                foreach (var el in results.EnumerateArray())
                {
                    var item = ParseItem(el, sticker);
                    if (item != null)
                    {
                        items.Add(item);
                        TrackMeta(item);
                    }
                }
            }
            else
            {
                Logger.LogDebug("KLIPY: {kind} response had no data[] payload ({bytes} bytes)",
                    kind, bytes.Length);
            }
            if (doc.RootElement.TryGetProperty("data", out var dataEl)
                && dataEl.ValueKind == JsonValueKind.Object
                && dataEl.TryGetProperty("has_next", out var hn))
            {
                hasMore = hn.ValueKind == JsonValueKind.True;
            }
            else
            {
                hasMore = items.Count >= PageSize;
            }
            _metrics.Record(kind);
            FlushMeta();
            // Preserve provider ordering verbatim - KLIPY terms forbid
            // reordering or filtering the returned result composition.
            return new GifPage(items, hasMore);
        }
        catch (OperationCanceledException)
        {
            return new GifPage(Array.Empty<GifItem>(), false, Error: "cancelled");
        }
        catch (Exception ex)
        {
            _metrics.Record(kind, failed: true);
            Logger.LogDebug(ex, "KLIPY: search request failed");
            return new GifPage(Array.Empty<GifItem>(), false, Error: "request-failed");
        }
        finally
        {
            _searchLock.Release();
        }
    }

    /// <summary>Unwraps the v1 envelope <c>{result, data:{data:[…]}}</c>;
    /// tolerates a bare <c>data:[…]</c> body too (items lookup shape).</summary>
    private static bool TryItemsArray(JsonElement root, out JsonElement results)
    {
        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("data", out results)
            && results.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        if (root.TryGetProperty("data", out results)
            && results.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        results = default;
        return false;
    }

    private static GifItem? FirstItem(JsonElement root, bool sticker)
    {
        if (!TryItemsArray(root, out var results))
        {
            return null;
        }
        foreach (var el in results.EnumerateArray())
        {
            var item = ParseItem(el, sticker);
            if (item != null)
            {
                return item;
            }
        }
        return null;
    }

    private bool CheckResponse(HttpResponseMessage resp, string? id)
    {
        if ((int)resp.StatusCode == 429)
        {
            var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
            _rateLimitedUntil = DateTime.UtcNow + retry;
            return false;
        }
        if (!resp.IsSuccessStatusCode)
        {
            if (id != null)
            {
                MarkNegative(id);
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Parses one v1 item ({slug, title, file:{hd|md|sm|xs:{gif:{url,width,
    /// height,size}}}}). The media rendition is the preferred size group
    /// whose declared byte size fits the download bound; the preview
    /// prefers the small animated thumbnail.
    /// </summary>
    private static GifItem? ParseItem(JsonElement el, bool sticker)
    {
        try
        {
            var slug = el.TryGetProperty("slug", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(slug) || !IsValidId(slug))
            {
                return null;
            }
            var title = el.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (!el.TryGetProperty("file", out var file)
                || file.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var formats = sticker ? StickerFormats : GifFormats;
            // media dims ride the sealed payload (bounded at 512) - a group
            // larger than that is unsendable, so it can't be the media pick
            var media = PickFile(file, MediaPreference, formats, maxDim: 512);
            if (media == null || media.Value.W <= 0 || media.Value.H <= 0)
            {
                return null;
            }
            var preview = PickFile(file, PreviewPreference, formats, maxDim: 0) ?? media.Value;
            return new GifItem(slug, title, preview.Url, media.Value.Url,
                media.Value.W, media.Value.H) { IsSticker = sticker };
        }
        catch
        {
            return null;
        }
    }

    private static (string Url, int W, int H, long Size)? PickFile(JsonElement file,
        string[] preference, string[] formats, int maxDim)
    {
        (string Url, int W, int H, long Size)? fallback = null;
        foreach (var sizeKey in preference)
        {
            if (!file.TryGetProperty(sizeKey, out var group)
                || group.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            JsonElement fmt = default;
            var found = false;
            foreach (var key in formats)
            {
                if (group.TryGetProperty(key, out fmt)
                    && fmt.ValueKind == JsonValueKind.Object)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                continue;
            }
            var url = fmt.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }
            var w = ReadInt(fmt, "width");
            var h = ReadInt(fmt, "height");
            if (maxDim > 0 && (w > maxDim || h > maxDim))
            {
                continue;
            }
            var size = fmt.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number
                ? sz.GetInt64() : 0L;
            var candidate = (url, w, h, size);
            if (size is > 0 and <= MaxDownloadBytes)
            {
                return candidate;
            }
            fallback ??= candidate;
        }
        return fallback;
    }

    private static int ReadInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    private bool IsNegative(string id)
        => _negative.TryGetValue(id, out var until) && DateTime.UtcNow < until;

    private void MarkNegative(string id)
        => _negative[id] = DateTime.UtcNow.AddSeconds(NegativeCacheSeconds);

    /// <summary>Stores fetched bytes in the session LRU and evicts the
    /// oldest entries once the budget is exceeded. Eviction is approximate
    /// under races - the budget is a bound, not a contract.</summary>
    private void CacheBytes(string key, byte[] bytes)
    {
        if (!_byteCache.TryAdd(key, bytes))
        {
            return;
        }
        _byteOrder.Enqueue((key, bytes.Length));
        Interlocked.Add(ref _byteCacheSize, bytes.Length);
        while (Interlocked.Read(ref _byteCacheSize) > MaxCachedMediaBytes
            && _byteOrder.TryDequeue(out var oldest)
            && _byteCache.TryRemove(oldest.Key, out var removed))
        {
            Interlocked.Add(ref _byteCacheSize, -removed.Length);
        }
    }

    // -- Persisted metadata cache -------------------------------------------
    // Provider refs (id + media URLs + dims) survive restarts, so received
    // GIF history stays renderable after beta revocation or a lapsed
    // session - KLIPY terms allow retaining result metadata, only media
    // bytes are forbidden to persist. Bounded to MaxPersistedMeta entries,
    // recency-ordered.

    private string? MetaFilePath
        => _stateDir == null ? null : Path.Combine(_stateDir, MetaFileName);

    private void EnsureMetaLoaded()
    {
        if (_metaLoaded)
        {
            return;
        }
        lock (_metaFileLock)
        {
            if (_metaLoaded)
            {
                return;
            }
            _metaLoaded = true;
            try
            {
                var path = MetaFilePath;
                if (path == null || !File.Exists(path))
                {
                    return;
                }
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var id = el.GetProperty("i").GetString();
                    var media = el.GetProperty("m").GetString();
                    var w = el.GetProperty("w").GetInt32();
                    var h = el.GetProperty("h").GetInt32();
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(media)
                        || w <= 0 || h <= 0 || !IsValidId(id) || !IsAllowedMediaUrl(media))
                    {
                        continue;
                    }
                    var preview = el.TryGetProperty("p", out var p) ? p.GetString() : null;
                    if (string.IsNullOrEmpty(preview) || !IsAllowedMediaUrl(preview))
                    {
                        preview = media;
                    }
                    var title = el.TryGetProperty("t", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    var item = new GifItem(id, title, preview, media, w, h)
                    {
                        IsSticker = el.TryGetProperty("k", out var k)
                            && k.ValueKind == JsonValueKind.Number
                            && k.GetInt32() == 1,
                    };
                    if (_meta.TryAdd(id, item))
                    {
                        _metaOrder.Enqueue(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "KLIPY: failed to load gif meta cache");
            }
        }
    }

    private void TrackMeta(GifItem item)
    {
        EnsureMetaLoaded();
        if (_meta.TryAdd(item.Id, item))
        {
            _metaOrder.Enqueue(item.Id);
        }
        else
        {
            _meta[item.Id] = item;
            _metaOrder.Enqueue(item.Id);
        }
        _metaDirty = true;
    }

    private void FlushMeta()
    {
        if (!_metaDirty)
        {
            return;
        }
        lock (_metaFileLock)
        {
            if (!_metaDirty)
            {
                return;
            }
            _metaDirty = false;
            var path = MetaFilePath;
            if (path == null)
            {
                return;
            }
            try
            {
                // Recency-first: walk the first-seen queue backwards, keep
                // the newest unique ids up to the bound, prune the rest.
                var keep = new HashSet<string>(StringComparer.Ordinal);
                var ordered = _metaOrder.ToArray();
                for (var i = ordered.Length - 1; i >= 0 && keep.Count < MaxPersistedMeta; i--)
                {
                    if (_meta.ContainsKey(ordered[i]))
                    {
                        keep.Add(ordered[i]);
                    }
                }
                if (_meta.Count > keep.Count)
                {
                    foreach (var key in _meta.Keys)
                    {
                        if (!keep.Contains(key))
                        {
                            _meta.TryRemove(key, out _);
                        }
                    }
                }
                // Rebound the order queue to the kept ids (oldest -> newest)
                // so it cannot grow past the persisted bound mid-session.
                var dedup = new List<string>(keep.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = ordered.Length - 1; i >= 0; i--)
                {
                    if (keep.Contains(ordered[i]) && seen.Add(ordered[i]))
                    {
                        dedup.Add(ordered[i]);
                    }
                }
                _metaOrder.Clear();
                for (var i = dedup.Count - 1; i >= 0; i--)
                {
                    _metaOrder.Enqueue(dedup[i]);
                }
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartArray();
                    foreach (var id in keep)
                    {
                        var item = _meta[id];
                        writer.WriteStartObject();
                        writer.WriteString("i", item.Id);
                        if (item.Title.Length > 0)
                        {
                            writer.WriteString("t", item.Title);
                        }
                        if (item.PreviewUrl != null)
                        {
                            writer.WriteString("p", item.PreviewUrl);
                        }
                        writer.WriteString("m", item.MediaUrl);
                        writer.WriteNumber("w", item.Width);
                        writer.WriteNumber("h", item.Height);
                        if (item.IsSticker)
                        {
                            writer.WriteNumber("k", 1);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.Flush();
                }
                File.WriteAllBytes(path, ms.ToArray());
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "KLIPY: failed to persist gif meta cache");
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
            _searchLock.Dispose();
        }
        base.Dispose(disposing);
    }
}
