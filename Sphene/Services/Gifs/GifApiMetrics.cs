using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Sphene.Services.Gifs;

/// <summary>
/// Rolling 60-minute counter for outgoing GIF-provider traffic. API calls
/// (search/trending/lookup/share) are counted separately from media-file
/// downloads (preview/full rendition fetches against the CDN) - provider
/// request quotas apply to API calls only.
///
/// Local-only instrumentation: counts reflect THIS installation's traffic,
/// not global usage of a shared API key. Authoritative global usage lives
/// in the provider's partner-panel analytics. No request payloads or search
/// terms are retained - only (timestamp, call kind, outcome) tuples.
/// </summary>
public sealed class GifApiMetrics
{
    /// <summary>Call classification.</summary>
    public enum Kind
    {
        Search = 0,
        Trending = 1,
        Lookup = 2,
        Share = 3,
        Preview = 4,
        Media = 5,
    }

    /// <summary>Kinds that count against the provider's API request quota
    /// (media downloads hit the CDN, not the request budget).</summary>
    private static readonly Kind[] ApiKinds = [Kind.Search, Kind.Trending, Kind.Lookup, Kind.Share];

    private const int WindowMinutes = 60;
    private const int MaxRetained = 8192;

    /// <summary>Assumed test-key request budget per hour; shown as an
    /// estimate only - the provider is authoritative.</summary>
    public const int AssumedHourlyLimit = 100;

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Auto)]
    private readonly struct Entry
    {
        internal Entry(DateTime atUtc, Kind kind, bool failed, bool throttled)
        {
            AtUtc = atUtc;
            Kind = kind;
            Failed = failed;
            Throttled = throttled;
        }

        internal DateTime AtUtc { get; }
        internal Kind Kind { get; }
        internal bool Failed { get; }
        internal bool Throttled { get; }
    }

    /// <summary>Immutable read model for the diagnostics view.</summary>
    public sealed record Snapshot
    {
        public required int Search { get; init; }
        public required int Trending { get; init; }
        public required int Lookup { get; init; }
        public required int Share { get; init; }
        public required int PreviewDownloads { get; init; }
        public required int MediaDownloads { get; init; }
        public required int Errors { get; init; }
        public required int Throttled { get; init; }
        /// <summary>Quota-relevant API calls inside the rolling window.</summary>
        public required int ApiCalls { get; init; }
        /// <summary>Configured assumed request budget the estimate is
        /// calculated against (server-published; falls back to the KLIPY
        /// test-key default).</summary>
        public required int Limit { get; init; }
        /// <summary>Locally estimated remaining quota (never authoritative).</summary>
        public required int EstimatedRemaining { get; init; }
        /// <summary>Oldest event still inside the window; null when empty.</summary>
        public required DateTime? WindowStartUtc { get; init; }
    }

    private readonly ConcurrentQueue<Entry> _events = new();
    private readonly Func<DateTime> _clock;

    public GifApiMetrics(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Records one outgoing call. <paramref name="failed"/> covers
    /// transport/HTTP/parse failures; <paramref name="throttled"/> marks a
    /// 429 rate-limit response (counted additionally, not instead).</summary>
    public void Record(Kind kind, bool failed = false, bool throttled = false)
    {
        _events.Enqueue(new Entry(_clock(), kind, failed, throttled));
        while (_events.Count > MaxRetained)
        {
            _events.TryDequeue(out _);
        }
    }

    /// <summary>Counts for the trailing 60 minutes. <paramref name="limit"/>
    /// is the assumed request budget the remaining-quota estimate is
    /// calculated against.</summary>
    public Snapshot GetSnapshot(int limit = AssumedHourlyLimit)
    {
        var cutoff = _clock().AddMinutes(-WindowMinutes);
        var counts = new int[6];
        var errors = new int[6];
        var throttled = new int[6];
        DateTime? oldest = null;
        var any = false;
        foreach (var e in _events)
        {
            if (e.AtUtc < cutoff)
            {
                continue;
            }
            any = true;
            if (oldest == null || e.AtUtc < oldest)
            {
                oldest = e.AtUtc;
            }
            var i = (int)e.Kind;
            counts[i]++;
            if (e.Failed) errors[i]++;
            if (e.Throttled) throttled[i]++;
        }
        if (!any)
        {
            return new Snapshot
            {
                Search = 0, Trending = 0, Lookup = 0, Share = 0,
                PreviewDownloads = 0, MediaDownloads = 0,
                Errors = 0, Throttled = 0, ApiCalls = 0,
                Limit = limit, EstimatedRemaining = Math.Max(0, limit),
                WindowStartUtc = null,
            };
        }
        var api = 0;
        foreach (var k in ApiKinds)
        {
            api += counts[(int)k];
        }
        var err = errors.Sum();
        var thr = throttled.Sum();
        return new Snapshot
        {
            Search = counts[(int)Kind.Search],
            Trending = counts[(int)Kind.Trending],
            Lookup = counts[(int)Kind.Lookup],
            Share = counts[(int)Kind.Share],
            PreviewDownloads = counts[(int)Kind.Preview],
            MediaDownloads = counts[(int)Kind.Media],
            Errors = err,
            Throttled = thr,
            ApiCalls = api,
            Limit = limit,
            EstimatedRemaining = Math.Max(0, limit - api),
            WindowStartUtc = oldest,
        };
    }
}
