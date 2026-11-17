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

    private static readonly string[] MediaPreference = ["gif", "mediumgif", "tinygif"];
    private static readonly string[] PreviewPreference = ["tinygif", "nanogif", "gif"];

    private const int MaxPersistedMeta = 512;
    private const string MetaFileName = "gif-meta.json";

    private readonly HttpClient _http = new() { Timeout = RequestTimeout };
    private readonly SpheneConfigService _configService;
    private readonly GifApiMetrics _metrics = new();
    private readonly ConcurrentDictionary<string, GifItem> _meta = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _metaOrder = new();
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _pendingBytes = new(StringComparer.Ordinal);
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
        var url = PageUrl("search", page)
            + $"&q={Uri.EscapeDataString(query.Trim())}";
        return await RequestPageAsync(url, GifApiMetrics.Kind.Search, ct).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IGifProvider.TrendingAsync"/>
    public Task<GifPage> TrendingAsync(int page, CancellationToken ct)
        => RequestPageAsync(PageUrl("trending", page), GifApiMetrics.Kind.Trending, ct);

    /// <inheritdoc cref="IGifProvider.GetByIdAsync"/>
    public async Task<GifItem?> GetByIdAsync(string id, CancellationToken ct)
    {
        EnsureMetaLoaded();
        if (_meta.TryGetValue(id, out var cached))
        {
            return cached;
        }
        if (!Available || IsNegative(id) || !IsValidId(id))
        {
            return null;
        }
        try
        {
            var url = $"{ApiBase}/{Uri.EscapeDataString(ActiveKey)}/gifs/items"
                + $"?slugs={Uri.EscapeDataString(id)}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!CheckResponse(resp, id))
            {
                _metrics.Record(GifApiMetrics.Kind.Lookup,
                    failed: !RateLimited, throttled: RateLimited);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            var item = FirstItem(doc.RootElement);
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
            MarkNegative(id);
            _metrics.Record(GifApiMetrics.Kind.Lookup, failed: true);
            Logger.LogDebug(ex, "KLIPY: failed to resolve gif {id}", id);
            return null;
        }
    }

    /// <inheritdoc cref="IGifProvider.FetchPreviewBytesAsync"/>
    public Task<byte[]?> FetchPreviewBytesAsync(GifItem item, CancellationToken ct)
        => FetchBytesAsync(item, item.PreviewUrl, item.Id + ".prev", GifApiMetrics.Kind.Preview, ct);

    /// <inheritdoc cref="IGifProvider.FetchGifBytesAsync"/>
    public Task<byte[]?> FetchGifBytesAsync(GifItem item, CancellationToken ct)
        => FetchBytesAsync(item, item.MediaUrl, item.Id, GifApiMetrics.Kind.Media, ct);

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
                var url = $"{ApiBase}/{Uri.EscapeDataString(ActiveKey)}/gifs/share/{Uri.EscapeDataString(item.Id)}";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
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

    private string PageUrl(string endpoint, int page)
        => $"{ApiBase}/{Uri.EscapeDataString(ActiveKey)}/gifs/{endpoint}"
            + $"?page={page + 1}&per_page={PageSize}"
            + $"&customer_id={Uri.EscapeDataString(CustomerId)}";

    /// <summary>
    /// Media bytes are consent-gated only - no API key needed. KLIPY media
    /// URLs are already-public CDN links, so a user whose beta access was
    /// revoked can still read existing chat history without regaining
    /// search/create capability.
    /// </summary>
    private Task<byte[]?> FetchBytesAsync(GifItem item, string? mediaUrl, string cacheKey,
        GifApiMetrics.Kind kind, CancellationToken ct)
    {
        if (IsNegative(item.Id) || !ConsentGiven || mediaUrl == null)
        {
            return Task.FromResult<byte[]?>(null);
        }
        return _pendingBytes.GetOrAdd(cacheKey, key => FetchCoreAsync(item.Id, mediaUrl, key, kind, ct));
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
        CancellationToken ct)
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
            if (doc.RootElement.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in results.EnumerateArray())
                {
                    var item = ParseItem(el);
                    if (item != null)
                    {
                        items.Add(item);
                        TrackMeta(item);
                    }
                }
            }
            // Preserve provider ordering verbatim - KLIPY terms forbid
            // reordering or filtering the returned result composition.
            var hasMore = items.Count >= PageSize;
            _metrics.Record(kind);
            FlushMeta();
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

    private static GifItem? FirstItem(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var el in results.EnumerateArray())
        {
            var item = ParseItem(el);
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
    /// Parses one result object (Tenor-shaped <c>media_formats</c> map). The
    /// media rendition is the preferred format whose declared size fits the
    /// download bound; the preview prefers the small animated thumbnail.
    /// </summary>
    private static GifItem? ParseItem(JsonElement el)
    {
        try
        {
            var id = el.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id) || !IsValidId(id))
            {
                return null;
            }
            var title = el.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (!el.TryGetProperty("media_formats", out var formats)
                || formats.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var media = PickFormat(formats, MediaPreference);
            if (media == null || media.Value.W <= 0 || media.Value.H <= 0)
            {
                return null;
            }
            var preview = PickFormat(formats, PreviewPreference) ?? media.Value;
            return new GifItem(id, title, preview.Url, media.Value.Url,
                media.Value.W, media.Value.H);
        }
        catch
        {
            return null;
        }
    }

    private static (string Url, int W, int H, long Size)? PickFormat(JsonElement formats,
        string[] preference)
    {
        (string Url, int W, int H, long Size)? fallback = null;
        foreach (var name in preference)
        {
            if (!formats.TryGetProperty(name, out var fmt)
                || fmt.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var url = fmt.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }
            var dims = ReadDims(fmt);
            var size = fmt.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64() : 0L;
            var candidate = (url, dims.W, dims.H, size);
            if (size is > 0 and <= MaxDownloadBytes)
            {
                return candidate;
            }
            fallback ??= candidate;
        }
        return fallback;
    }

    private static (int W, int H) ReadDims(JsonElement fmt)
    {
        if (!fmt.TryGetProperty("dims", out var d) || d.ValueKind != JsonValueKind.Array)
        {
            return (0, 0);
        }
        var w = 0;
        var h = 0;
        var i = 0;
        foreach (var v in d.EnumerateArray())
        {
            var n = v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            if (i == 0) w = n; else if (i == 1) h = n;
            if (++i >= 2)
            {
                break;
            }
        }
        return (w, h);
    }

    private bool IsNegative(string id)
        => _negative.TryGetValue(id, out var until) && DateTime.UtcNow < until;

    private void MarkNegative(string id)
        => _negative[id] = DateTime.UtcNow.AddSeconds(NegativeCacheSeconds);

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
                    var item = new GifItem(id, title, preview, media, w, h);
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
