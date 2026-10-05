using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Sphene.API.Dto.User;
using Sphene.WebAPI;

namespace Sphene.Services;

public sealed class SupporterIconService : IDisposable
{
    private enum IconLoadState { Loading, Failed, Ready }

    private sealed class SupporterIconEntry
    {
        public IconLoadState State = IconLoadState.Loading;
        public SupporterAnimatedIcon? Icon;
    }

    private const int MaxIconDimension = 512;
    private const int MaxIconFrames = 64;
    private const float DefaultFrameDelaySeconds = 0.1f;
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromSeconds(60);

    private readonly ApiController _apiController;
    private readonly ITextureProvider _textureProvider;
    private readonly ILogger<SupporterIconService> _logger;
    private readonly System.Threading.Lock _iconsLock = new();
    private readonly Dictionary<string, SupporterIconEntry> _icons = new(StringComparer.OrdinalIgnoreCase);
    private List<SupporterIconInfoDto> _catalog = [];
    private DateTime _catalogFetchedAtUtc = DateTime.MinValue;
    private Task? _catalogTask;
    private bool _disposed;

    public SupporterIconService(ApiController apiController, ITextureProvider textureProvider,
        ILogger<SupporterIconService> logger)
    {
        _apiController = apiController;
        _textureProvider = textureProvider;
        _logger = logger;
    }

    public IReadOnlyList<SupporterIconInfoDto> GetCatalog()
    {
        EnsureCatalog();
        return _catalog;
    }

    public SupporterAnimatedIcon? GetIcon(string? iconId)
    {
        if (string.IsNullOrWhiteSpace(iconId) || _disposed)
            return null;

        lock (_iconsLock)
        {
            if (_icons.TryGetValue(iconId, out var existing))
                return existing.Icon;

            var entry = new SupporterIconEntry();
            _icons[iconId] = entry;
            _ = LoadIconAsync(iconId, entry);
            return null;
        }
    }

    public bool IsIconKnown(string? iconId)
    {
        if (string.IsNullOrWhiteSpace(iconId))
            return true;
        var catalog = GetCatalog();
        for (var i = 0; i < catalog.Count; i++)
        {
            if (string.Equals(catalog[i].Id, iconId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void EnsureCatalog()
    {
        var now = DateTime.UtcNow;
        if (_disposed || _catalogTask != null || now - _catalogFetchedAtUtc < CatalogTtl)
            return;

        _catalogTask = Task.Run(async () =>
        {
            try
            {
                _catalog = await _apiController.SupporterGetIcons().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[SupporterIcons] Catalog fetch failed");
            }
            finally
            {
                _catalogFetchedAtUtc = DateTime.UtcNow;
                _catalogTask = null;
            }
        });
    }

    private async Task LoadIconAsync(string iconId, SupporterIconEntry entry)
    {
        try
        {
            var bytes = await _apiController.SupporterGetIconFile(iconId).ConfigureAwait(false);
            var icon = bytes == null || bytes.Length == 0 ? null : await DecodeIconAsync(bytes).ConfigureAwait(false);
            lock (_iconsLock)
            {
                entry.Icon = icon;
                entry.State = icon == null ? IconLoadState.Failed : IconLoadState.Ready;
            }
            _logger.LogDebug("[SupporterIcons] Loaded {iconId}: {state} ({frames} frames)", iconId, entry.State, icon?.FrameCount ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SupporterIcons] Failed to load icon {iconId}", iconId);
            lock (_iconsLock)
            {
                entry.State = IconLoadState.Failed;
            }
        }
    }

    private async Task<SupporterAnimatedIcon?> DecodeIconAsync(byte[] bytes)
    {
        using var image = Image.Load<Rgba32>(bytes);
        var frameWidth = image.Width;
        var frameHeight = image.Height;
        if (frameWidth <= 0 || frameHeight <= 0 || frameWidth > MaxIconDimension || frameHeight > MaxIconDimension)
            return null;

        var frameCount = Math.Clamp(image.Frames.Count, 1, MaxIconFrames);
        var durations = new float[frameCount];
        using var atlas = new Image<Rgba32>(frameWidth * frameCount, frameHeight);
        var framePixels = new Rgba32[frameWidth * frameHeight];
        for (var i = 0; i < frameCount; i++)
        {
            var frame = image.Frames[i];
            durations[i] = GetFrameDelaySeconds(frame);
            frame.CopyPixelDataTo(framePixels);
            var xOffset = i * frameWidth;
            atlas.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < frameHeight; y++)
                {
                    framePixels.AsSpan(y * frameWidth, frameWidth)
                        .CopyTo(accessor.GetRowSpan(y).Slice(xOffset, frameWidth));
                }
            });
        }

        using var stream = new MemoryStream();
        await atlas.SaveAsPngAsync(stream).ConfigureAwait(false);
        var wrap = await _textureProvider.CreateFromImageAsync(stream.ToArray()).ConfigureAwait(false);
        return new SupporterAnimatedIcon(wrap, frameWidth, frameHeight, durations);
    }

    private static float GetFrameDelaySeconds(ImageFrame<Rgba32> frame)
    {
        var gif = frame.Metadata.GetGifMetadata();
        if (gif != null && gif.FrameDelay > 0)
            return gif.FrameDelay / 100f;
        var webp = frame.Metadata.GetWebpMetadata();
        if (webp != null && webp.FrameDelay > 0)
            return webp.FrameDelay / 1000f;
        return DefaultFrameDelaySeconds;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_iconsLock)
        {
            foreach (var entry in _icons.Values)
            {
                entry.Icon?.Dispose();
                entry.Icon = null;
            }
            _icons.Clear();
        }
    }
}
