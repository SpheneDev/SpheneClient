using System.Collections.Concurrent;

namespace Sphene.Services;

/// <summary>
/// Per-key bookkeeping for lazy server fetches. Claims the in-flight slot
/// and records failure timestamps so a transient error (e.g. a first read
/// racing the connect handshake) never pins placeholder data for the rest
/// of the session - reads retry once the cooldown lapses, and
/// <see cref="ResetFailures"/> on a fresh connect re-arms them immediately.
/// </summary>
internal sealed class ProfileFetchTracker<TKey> where TKey : notnull
{
    private readonly TimeSpan _retryDelay;
    private readonly ConcurrentDictionary<TKey, byte> _inFlight;
    private readonly ConcurrentDictionary<TKey, DateTimeOffset> _failures;

    public ProfileFetchTracker(IEqualityComparer<TKey> keyComparer, TimeSpan retryDelay)
    {
        _retryDelay = retryDelay;
        _inFlight = new(keyComparer);
        _failures = new(keyComparer);
    }

    /// <summary>True while a fetch for the key is claimed but not finished.</summary>
    public bool IsInFlight(TKey key) => _inFlight.ContainsKey(key);

    /// <summary>
    /// Claims the fetch slot for a key. False while another fetch is in
    /// flight or the last recorded failure is still inside its retry
    /// cooldown. The claim itself is atomic, so per-frame callers cannot
    /// stampede the service.
    /// </summary>
    public bool TryBeginFetch(TKey key, DateTimeOffset utcNow)
    {
        if (_failures.TryGetValue(key, out var failedAt)
            && utcNow - failedAt < _retryDelay)
        {
            return false;
        }

        return _inFlight.TryAdd(key, 0);
    }

    /// <summary>Clears the claim and any recorded failure.</summary>
    public void ReportSucceeded(TKey key) => Drop(key);

    /// <summary>Frees the slot but arms the retry cooldown.</summary>
    public void ReportFailed(TKey key, DateTimeOffset utcNow)
    {
        _inFlight.TryRemove(key, out _);
        _failures[key] = utcNow;
    }

    /// <summary>
    /// Drops every failure record - a fresh connect lets failed reads retry
    /// on their next call instead of waiting out the cooldown.
    /// </summary>
    public void ResetFailures() => _failures.Clear();

    /// <summary>Drops all bookkeeping for a single key - targeted cache clear.</summary>
    public void Drop(TKey key)
    {
        _inFlight.TryRemove(key, out _);
        _failures.TryRemove(key, out _);
    }

    /// <summary>Full reset - disconnect or whole-cache clear.</summary>
    public void Reset()
    {
        _inFlight.Clear();
        _failures.Clear();
    }
}
