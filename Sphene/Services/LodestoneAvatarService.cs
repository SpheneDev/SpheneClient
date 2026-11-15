using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Sphene.API.Data;
using Sphene.API.Dto.User;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.WebAPI;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sphene.Services;

/// <summary>
/// Seeds the profile avatar from the character's Lodestone portrait on first
/// connect - only while the user has no avatar of their own. Manual uploads
/// via the photo studio win immediately; the once-only flag prevents reseeding
/// after the user deliberately removed or replaced the picture.
/// </summary>
public partial class LodestoneAvatarService : DisposableMediatorSubscriberBase, IHostedService
{
    private const string LodestoneBase = "https://eu.finalfantasyxiv.com";
    private const int AvatarSize = 256;
    private const int AvatarMaxBytes = 250 * 1024;

    private readonly HttpClient _httpClient;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly ApiController _apiController;
    private readonly SpheneConfigService _configService;
    private Task<string?>? _prefetchTask;
    private bool _applyAttempted;

    /// <summary>Raw portrait bytes once prefetched - lets UI preview the
    /// fetched avatar before it is applied to the profile.</summary>
    public byte[]? PrefetchedPortrait { get; private set; }

    [GeneratedRegex(@"/lodestone/character/(?<id>\d+)/", RegexOptions.ExplicitCapture, 500)]
    private static partial Regex CharacterIdRegex();

    [GeneratedRegex(@"(?:https?:)?//img2\.finalfantasyxiv\.com/f/[^""'\s?]+", RegexOptions.IgnoreCase, 500)]
    private static partial Regex PortraitUrlRegex();

    public LodestoneAvatarService(ILogger<LodestoneAvatarService> logger,
        SpheneMediator mediator, HttpClient httpClient,
        DalamudUtilService dalamudUtilService, ApiController apiController,
        SpheneConfigService configService)
        : base(logger, mediator)
    {
        _httpClient = httpClient;
        _dalamudUtilService = dalamudUtilService;
        _apiController = apiController;
        _configService = configService;
        Mediator.Subscribe<DalamudLoginMessage>(this, _ => StartPrefetch());
        Mediator.Subscribe<ConnectedMessage>(this, _ => TryApply());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("[Lodestone] service started, waiting for login");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>True once a prefetch attempt settled - success or failure.</summary>
    public bool PrefetchSettled => _prefetchTask?.IsCompleted == true;

    /// <summary>Kicks the portrait prefetch for preview purposes - unlike
    /// <see cref="StartPrefetch"/> it ignores the applied-seed flag, since a
    /// preview fetch never applies anything.</summary>
    public void EnsurePrefetch()
    {
        _prefetchTask ??= FetchPreparedAvatarAsync();
    }

    /// <summary>Game login - the portrait fetch needs no server connection.</summary>
    private void StartPrefetch()
    {
        if (_prefetchTask != null || _configService.Current.AutoLodestoneAvatarApplied)
        {
            return;
        }
        Logger.LogDebug("[Lodestone] prefetching portrait");
        _prefetchTask = FetchPreparedAvatarAsync();
    }

    /// <summary>Server connect - upload the prefetched portrait if the profile
    /// still has no avatar.</summary>
    private void TryApply()
    {
        if (_applyAttempted) return;
        _applyAttempted = true;
        if (_configService.Current.AutoLodestoneAvatarApplied)
        {
            Logger.LogDebug("[Lodestone] seed already applied previously · skipping");
            return;
        }
        if (!_apiController.IsConnected)
        {
            _applyAttempted = false;
            return;
        }
        Logger.LogDebug("[Lodestone] connected · applying avatar seed for {uid}", _apiController.UID);
        StartPrefetch();
        _ = Task.Run(ApplyAsync);
    }

    private async Task ApplyAsync()
    {
        try
        {
            var userData = new UserData(_apiController.UID);
            var existing = await _apiController.UserGetProfile(new UserDto(userData)).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(existing.ProfilePictureBase64))
            {
                _configService.Current.AutoLodestoneAvatarApplied = true;
                _configService.Save();
                return;
            }

            var prepared = await (_prefetchTask ?? Task.FromResult<string?>(null)).ConfigureAwait(false);
            if (prepared == null)
            {
                Logger.LogDebug("[Lodestone] no portrait to apply");
                return;
            }

            // recheck - a manual upload during the fetch wins over the seed
            existing = await _apiController.UserGetProfile(new UserDto(userData)).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(existing.ProfilePictureBase64))
            {
                return;
            }

            await _apiController.UserSetProfile(new UserProfileDto(userData,
                Disabled: false, IsNSFW: null, ProfilePictureBase64: prepared,
                Description: null)).ConfigureAwait(false);
            _configService.Current.AutoLodestoneAvatarApplied = true;
            _configService.Save();
            Logger.LogDebug("[Lodestone] seeded profile avatar from portrait");
        }
        catch (Exception ex)
        {
            Logger.LogDebug("[Lodestone] avatar seed failed: {err}", ex.Message);
        }
    }

    /// <summary>Name + world → Lodestone portrait → avatar-spec PNG base64.</summary>
    private async Task<string?> FetchPreparedAvatarAsync()
    {
        try
        {
            var playerName = await _dalamudUtilService.GetPlayerNameAsync().ConfigureAwait(false);
            var homeWorldId = await _dalamudUtilService.GetHomeWorldIdAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(playerName)
                || !_dalamudUtilService.WorldData.Value.TryGetValue(
                    (ushort)homeWorldId, out var worldName))
            {
                Logger.LogDebug("[Lodestone] prefetch skipped: no player name or world ({name})", playerName);
                return null;
            }

            var portraitBytes = await FetchPortraitAsync(playerName, worldName).ConfigureAwait(false);
            if (portraitBytes == null) return null;
            var prepared = PrepareAvatar(portraitBytes);
            if (prepared == null)
            {
                Logger.LogDebug("[Lodestone] portrait prepared failed size check ({raw} raw bytes)", portraitBytes.Length);
                return null;
            }
            PrefetchedPortrait = portraitBytes;
            Logger.LogDebug("[Lodestone] portrait prefetched ({raw} raw bytes)", portraitBytes.Length);
            return prepared;
        }
        catch (Exception ex)
        {
            Logger.LogDebug("[Lodestone] prefetch crashed: {err}", ex.Message);
            return null;
        }
    }

    private async Task<byte[]?> FetchPortraitAsync(string playerName, string worldName)
    {
        try
        {
            var searchUrl = $"{LodestoneBase}/lodestone/character/?q={Uri.EscapeDataString(playerName)}&worldname={Uri.EscapeDataString(worldName)}";
            Logger.LogDebug("[Lodestone] searching {name}@{world}", playerName, worldName);
            var searchHtml = await GetStringAsync(searchUrl).ConfigureAwait(false);
            Logger.LogDebug("[Lodestone] search page {chars} chars", searchHtml.Length);
            var idMatch = CharacterIdRegex().Match(searchHtml);
            if (!idMatch.Success)
            {
                Logger.LogDebug("[Lodestone] search found no character for {name}@{world}", playerName, worldName);
                return null;
            }

            var pageHtml = await GetStringAsync(
                $"{LodestoneBase}/lodestone/character/{idMatch.Groups["id"].Value}/").ConfigureAwait(false);
            Logger.LogDebug("[Lodestone] character {id} page {chars} chars", idMatch.Groups["id"].Value, pageHtml.Length);
            var matches = PortraitUrlRegex().Matches(pageHtml);
            Match? urlMatch = null;
            foreach (Match candidate in matches)
            {
                // prefer the full-body portrait (…_fl0) but accept any image
                if (urlMatch == null
                    || candidate.Value.Contains("_fl0", StringComparison.OrdinalIgnoreCase))
                {
                    urlMatch = candidate;
                }
            }
            if (urlMatch == null || !urlMatch.Success)
            {
                // any img2 reference at all? 0 refs on a tiny page means a
                // bot-challenge/block page rather than the real character page
                var img2Refs = pageHtml.Split("img2.finalfantasyxiv.com", StringSplitOptions.None).Length - 1;
                // sample the first raw img2 occurrences so we can see the real URL shape
                var sampleStart = pageHtml.IndexOf("img2.finalfantasyxiv.com", StringComparison.Ordinal);
                var sample = sampleStart >= 0
                    ? pageHtml.Substring(sampleStart, Math.Min(160, pageHtml.Length - sampleStart))
                    : "none";
                Logger.LogDebug("[Lodestone] no portrait match for {name} · page {chars} chars, {refs} img2 refs · sample: {sample}",
                    playerName, pageHtml.Length, img2Refs, sample);
                return null;
            }

            var portraitUrl = urlMatch.Value.StartsWith("//", StringComparison.Ordinal)
                ? "https:" + urlMatch.Value
                : urlMatch.Value;
            Logger.LogDebug("[Lodestone] downloading portrait {url}", portraitUrl);

            using var imgReq = new HttpRequestMessage(HttpMethod.Get, portraitUrl);
            imgReq.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            var imgRes = await _httpClient.SendAsync(imgReq).ConfigureAwait(false);
            if (!imgRes.IsSuccessStatusCode)
            {
                Logger.LogDebug("[Lodestone] portrait download failed: {status}", imgRes.StatusCode);
                return null;
            }
            return await imgRes.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug("[Lodestone] portrait fetch failed: {err}", ex.Message);
            return null;
        }
    }

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private async Task<string> GetStringAsync(string url)
    {
        // the shared client has no tight timeout - a hanging lodestone
        // request must fail visibly instead of stalling the seed forever
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        var res = await _httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>Square-crops and resizes the portrait to the avatar upload spec.</summary>
    private string? PrepareAvatar(byte[] portraitBytes)
    {
        try
        {
            using var image = Image.Load(portraitBytes);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(AvatarSize, AvatarSize),
                Mode = ResizeMode.Crop,
            }));
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.Length <= AvatarMaxBytes ? Convert.ToBase64String(ms.ToArray()) : null;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Lodestone avatar encode failed");
            return null;
        }
    }
}
