using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.Group;
using Sphene.API.Routes;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.Files;

namespace Sphene.Services;

/// <summary>
/// Client half of the community media pipeline - hash, upload and local
/// cache for images that live on the static file server rather than inside
/// base64 DTO columns. Reuses the existing file-transfer orchestrator (same
/// CDN base URI + auth as mod transfers); the
/// <c>/community/media/{hash}</c> GET route is anonymous-by-design, matching
/// the server contract - see docs/COMMUNITY_PLATFORM_MEDIA.md.
/// </summary>
public sealed class CommunityMediaClientService : DisposableMediatorSubscriberBase
{
    private const long MaxImageBytes = 2 * 1024 * 1024;
    /// <summary>Per-request ceiling - a stalled CDN GET must not park for the
    /// HttpClient default (~100s) while the image stays blank.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);
    /// <summary>Transient failure cooldown before the same hash retries.</summary>
    private static readonly TimeSpan TransientRetryAfter = TimeSpan.FromSeconds(15);
    /// <summary>Missing/unauthorized media cooldown - content-addressed hashes
    /// don't reappear server-side, so 404/403 stays negative a while.</summary>
    private static readonly TimeSpan PermanentRetryAfter = TimeSpan.FromMinutes(10);
    /// <summary>404 retry window - shorter than <see cref="PermanentRetryAfter"/>
    /// because media legitimately lands on durable storage moments after the
    /// announcing push (upload fan-out, replication); a long negative latch
    /// turned that propagation gap into minutes of missing images.</summary>
    private static readonly TimeSpan MissingRetryAfter = TimeSpan.FromSeconds(90);
    /// <summary>Fallback when a 429 carries no Retry-After header.</summary>
    private static readonly TimeSpan RateLimitRetryAfter = TimeSpan.FromSeconds(60);

    private readonly FileTransferOrchestrator _orchestrator;
    private readonly SpheneConfigService _configService;
    private readonly ConcurrentDictionary<string, Task<string?>> _pendingDownloads
        = new(StringComparer.Ordinal);
    /// <summary>Negative cache - hash → retry-not-before. Keeps failed media
    /// from restarting a request on every frame while still allowing
    /// recovery once the cooldown elapses.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _negativeCache
        = new(StringComparer.Ordinal);

    public CommunityMediaClientService(ILogger<CommunityMediaClientService> logger,
        SpheneMediator mediator, FileTransferOrchestrator orchestrator,
        SpheneConfigService configService)
        : base(logger, mediator)
    {
        _orchestrator = orchestrator;
        _configService = configService;
        Mediator.Subscribe<HubReconnectedMessage>(this, (_) => ResetFailures());
        Mediator.Subscribe<ConnectedMessage>(this, (_) => ResetFailures());
    }

    /// <summary>SHA-256 → first-20-bytes hex - byte-identical to the server
    /// <c>CommunityMediaService.ComputeMediaHash</c> and the <c>FileCache.Hash</c> scheme.</summary>
    public static string ComputeMediaHash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes), 0, 20);

    /// <summary>Local cache dir for downloaded community media (content-addressed, immutable).</summary>
    private string CacheDir => Path.Combine(_configService.Current.CacheFolder ?? Path.GetTempPath(),
        "community-media");

    /// <summary>
    /// Uploads raw image bytes to <c>community/media/{hash}</c> - the dedicated
    /// media route on the static file server (raw payload + SHA-256 media
    /// digest; the mod pipeline's LZ4/SHA-1 contract does not apply). Returns
    /// the media hash for the caller's write DTO; null on failure or when the
    /// transfer channel is down.
    /// </summary>
    public async Task<string?> UploadImageAsync(byte[] bytes, string? contentType,
        CancellationToken ct = default)
    {
        if (!_orchestrator.IsInitialized || _orchestrator.FilesCdnUri == null) return null;
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;

        var hash = ComputeMediaHash(bytes);
        try
        {
            var target = new UriBuilder(
                new Uri(_orchestrator.FilesCdnUri, $"community/media/{hash}"));
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                target.Query = $"contentType={Uri.EscapeDataString(contentType)}";
            }
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var response = await _orchestrator.SendRequestAsync(HttpMethod.Post,
                target.Uri, content, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                // Seed the local cache with the just-shipped bytes - the
                // uploader renders its own media without a self-GET and is
                // immune to the durable-copy propagation window server-side.
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    await File.WriteAllBytesAsync(Path.Combine(CacheDir, hash), bytes, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Community media cache seed failed · {hash}", hash);
                }
                return hash;
            }
            Logger.LogWarning("Community media upload failed · {hash} · {status}", hash, response.StatusCode);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Community media upload error · {hash}", hash);
            return null;
        }
    }

    /// <summary>
    /// Returns the locally cached file for a hash - kicks the background
    /// download on first miss so the next frame can serve it. Null while
    /// absent; callers fall back to base64 or a placeholder.
    /// </summary>
    public string? TryGetLocalFile(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length != 40) return null;
        var path = Path.Combine(CacheDir, hash);
        if (File.Exists(path)) return path;
        if (_negativeCache.TryGetValue(hash, out var retryNotBefore)
            && retryNotBefore > DateTimeOffset.UtcNow)
        {
            return null;
        }
        if (_orchestrator.FilesCdnUri != null)
        {
            _ = _pendingDownloads.GetOrAdd(hash, h => DownloadAsync(h));
        }
        return null;
    }

    /// <summary>Drops negative-cache entries - called on reconnect so media
    /// that failed while offline can retry immediately.</summary>
    public void ResetFailures()
    {
        _negativeCache.Clear();
    }

    /// <summary>Bytes for <c>SpheneUI.Media.Texture</c> - sync, tiny file, null while downloading.</summary>
    public byte[]? TryGetBytes(string? hash)
    {
        var path = TryGetLocalFile(hash);
        if (path == null) return null;
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Community media read failed · {hash}", hash);
            return null;
        }
    }

    private async Task<string?> DownloadAsync(string hash)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var uri = new Uri(_orchestrator.FilesCdnUri!, $"community/media/{hash}");
            using var cts = new CancellationTokenSource(DownloadTimeout);
            var response = await _orchestrator.SendRequestAsync(HttpMethod.Get, uri,
                cts.Token, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var cooldown = response.StatusCode switch
                {
                    HttpStatusCode.NotFound => MissingRetryAfter,
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => PermanentRetryAfter,
                    HttpStatusCode.TooManyRequests => response.Headers.RetryAfter?.Delta
                        ?? RateLimitRetryAfter,
                    _ => TransientRetryAfter,
                };
                _negativeCache[hash] = DateTimeOffset.UtcNow + cooldown;
                Logger.LogDebug("Community media GET {hash} · {status} · {ms}ms · retry in {cooldown}s",
                    hash, (int)response.StatusCode, sw.ElapsedMilliseconds, cooldown.TotalSeconds);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            if (bytes.Length == 0 || !string.Equals(ComputeMediaHash(bytes), hash, StringComparison.Ordinal))
            {
                _negativeCache[hash] = DateTimeOffset.UtcNow + PermanentRetryAfter;
                return null;
            }

            Directory.CreateDirectory(CacheDir);
            var path = Path.Combine(CacheDir, hash);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            _negativeCache.TryRemove(hash, out _);
            Logger.LogDebug("Community media {hash} downloaded · {bytes}b · {ms}ms",
                hash, bytes.Length, sw.ElapsedMilliseconds);
            return path;
        }
        catch (Exception ex)
        {
            _negativeCache[hash] = DateTimeOffset.UtcNow + TransientRetryAfter;
            Logger.LogDebug(ex, "Community media download failed · {hash} · {ms}ms · retry in {cooldown}s",
                hash, sw.ElapsedMilliseconds, TransientRetryAfter.TotalSeconds);
            return null;
        }
        finally
        {
            _pendingDownloads.TryRemove(hash, out _);
        }
    }
}
