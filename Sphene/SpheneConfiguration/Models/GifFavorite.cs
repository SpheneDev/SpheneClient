namespace Sphene.SpheneConfiguration.Models;

/// <summary>
/// One pinned GIF reference for the private-chat picker. Stores the
/// provider item's rendition URLs and dimensions so a favorite can be
/// previewed and sent without a fresh provider lookup. Metadata only -
/// media bytes are never persisted (provider terms).
/// </summary>
public sealed class GifFavorite
{
    public string Provider { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PreviewUrl { get; set; } = string.Empty;
    public string MediaUrl { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Sticker catalog entry vs GIF - keeps favorites in the
    /// matching picker tab. Older entries default to GIF.</summary>
    public bool IsSticker { get; set; }
}
