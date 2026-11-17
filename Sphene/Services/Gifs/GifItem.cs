namespace Sphene.Services.Gifs;

/// <summary>
/// One selectable GIF: provider item id + rendition URLs + rendition size.
/// Ids and URLs are used verbatim - provider delivery/tracking data must
/// not be altered, rewritten or reconstructed.
/// </summary>
public sealed record GifItem(string Id, string Title, string PreviewUrl,
    string MediaUrl, int Width, int Height);
