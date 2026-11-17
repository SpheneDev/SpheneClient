using System.Collections.Generic;

namespace Sphene.Services.Gifs;

/// <summary>
/// One provider result page (or a failure description). Item order is the
/// provider's own and must be displayed unchanged.
/// </summary>
public sealed record GifPage(IReadOnlyList<GifItem> Items, bool HasMore,
    bool RateLimited = false, string? Error = null);
