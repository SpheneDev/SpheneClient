using System.Threading;
using System.Threading.Tasks;

namespace Sphene.Services.Gifs;

/// <summary>
/// Pluggable GIF backend for private chat. Implementations own everything
/// provider-specific: endpoint shape, key delivery, content filtering,
/// media host allowlist, attribution strings and share/recency reporting.
/// The picker and chat renderer only ever see this contract. Providers are
/// never queried or combined in parallel - their terms require separate
/// result sets and preserved ordering.
/// </summary>
public interface IGifProvider
{
    /// <summary>Short stable id persisted inside the sealed payload ("klipy").</summary>
    string ProviderId { get; }

    /// <summary>Attribution display name ("KLIPY").</summary>
    string DisplayName { get; }

    /// <summary>Search-box placeholder required by the provider's attribution rules.</summary>
    string SearchPlaceholder { get; }

    /// <summary>Footer attribution text required by the provider's guidelines.</summary>
    string AttributionText { get; }

    /// <summary>Whether the server granted this account GIF capability
    /// (<c>ServerInfo.GifEnabled</c>) AND a provider key is available.
    /// When false the UI must not expose GIF search and no provider API
    /// calls may run.</summary>
    bool IsConfigured { get; }

    /// <summary>Whether the user consented to contacting the external provider.</summary>
    bool ConsentGiven { get; }

    /// <summary>Whether search/fetch can run right now.</summary>
    bool Available { get; }

    /// <summary>Provider is actively rate-limiting (test keys cap at 100 req/h).</summary>
    bool RateLimited { get; }

    /// <summary>Rolling 60-minute usage counters for the diagnostics view.
    /// Local-only - never authoritative global key usage.</summary>
    GifApiMetrics.Snapshot Usage { get; }

    /// <summary>Keyword search; <paramref name="page"/> is a zero-based page index.</summary>
    Task<GifPage> SearchAsync(string query, int page, CancellationToken ct);

    /// <summary>Trending page used as the picker's empty-query state.</summary>
    Task<GifPage> TrendingAsync(int page, CancellationToken ct);

    /// <summary>Keyword search in the provider's sticker catalog; same
    /// paging contract as <see cref="SearchAsync"/>. Items carry
    /// <see cref="GifItem.IsSticker"/>.</summary>
    Task<GifPage> SearchStickersAsync(string query, int page, CancellationToken ct);

    /// <summary>Trending sticker page for the sticker picker's empty state.</summary>
    Task<GifPage> TrendingStickersAsync(int page, CancellationToken ct);

    /// <summary>Resolves one item's metadata by id (received messages).</summary>
    Task<GifItem?> GetByIdAsync(string id, CancellationToken ct);

    /// <summary>Preview rendition bytes (picker cells) - memory only.</summary>
    Task<byte[]?> FetchPreviewBytesAsync(GifItem item, CancellationToken ct);

    /// <summary>Full media bytes for rendering - memory only, never persisted.</summary>
    Task<byte[]?> FetchGifBytesAsync(GifItem item, CancellationToken ct);

    /// <summary>Reports a share/send event to the provider (fire-and-forget).</summary>
    void RegisterShare(GifItem item);
}
