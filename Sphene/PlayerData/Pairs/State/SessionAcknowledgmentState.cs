using Sphene.API.Data;
using System.Collections.Concurrent;

namespace Sphene.PlayerData.Pairs.State;

/// <summary>
/// Pure business-logic state machine for session acknowledgments.
/// Contains no Dalamud dependencies and is fully unit-testable.
/// </summary>
public sealed class SessionAcknowledgmentState
{
    public readonly record struct LatestInfo(string AcknowledgmentId, DateTimeOffset CreatedAt);

    public readonly record struct SetPendingResult(
        bool WasSet,
        string? PreviousAcknowledgmentId,
        bool WasReplaced);

    public readonly record struct ProcessAcknowledgmentResult(
        bool WasProcessed,
        bool WasLatest,
        bool WasHistory,
        bool Success,
        Sphene.API.Dto.User.AcknowledgmentErrorCode ErrorCode,
        long ResponseMs);

    public readonly record struct RemovePendingResult(
        bool WasRemoved,
        string? RemovedAcknowledgmentId);

    public readonly record struct CleanupEntry(
        string UserUid,
        string AckId,
        DateTimeOffset CreatedAt,
        long ResponseMs);

    public readonly record struct AckResultMetrics(
        long Total,
        long Success,
        long Fail,
        double AverageResponseTimeMs,
        IReadOnlyDictionary<Sphene.API.Dto.User.AcknowledgmentErrorCode, long> ErrorCounts);

    private readonly ConcurrentDictionary<string, LatestInfo> _latestAcknowledgments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<string>> _acknowledgmentHistory = new(StringComparer.Ordinal);

    private long _resultTotal = 0;
    private long _resultSuccess = 0;
    private long _resultFail = 0;
    private long _resultResponseTotalMs = 0;
    private long _resultResponseCount = 0;
    private readonly ConcurrentDictionary<Sphene.API.Dto.User.AcknowledgmentErrorCode, long> _resultErrorCounts = new();
    private readonly ConcurrentDictionary<string, List<string>> _processedAcknowledgments = new(StringComparer.Ordinal);

    private const int MaxHistoryEntriesPerUser = 5;
    private static long _sessionCounter = 0;

    /// <summary>
    /// Stores a pending acknowledgment for a user.
    /// </summary>
    public SetPendingResult SetPending(string userUid, string acknowledgmentId, DateTimeOffset createdAt)
    {
        if (string.IsNullOrEmpty(userUid) || string.IsNullOrEmpty(acknowledgmentId))
        {
            return new SetPendingResult(false, null, false);
        }

        string? previousAckId = null;
        var wasReplaced = false;

        _latestAcknowledgments.AddOrUpdate(
            userUid,
            _ => new LatestInfo(acknowledgmentId, createdAt),
            (_, existing) =>
            {
                previousAckId = existing.AcknowledgmentId;
                wasReplaced = !string.Equals(existing.AcknowledgmentId, acknowledgmentId, StringComparison.Ordinal);
                return new LatestInfo(acknowledgmentId, createdAt);
            });

        AddToHistory(userUid, acknowledgmentId);
        return new SetPendingResult(true, previousAckId, wasReplaced);
    }

    /// <summary>
    /// Processes a received acknowledgment. Returns information about what happened.
    /// </summary>
    public ProcessAcknowledgmentResult ProcessReceivedAcknowledgment(
        string userUid,
        string acknowledgmentId,
        bool success,
        Sphene.API.Dto.User.AcknowledgmentErrorCode errorCode,
        DateTimeOffset receivedAt)
    {
        if (string.IsNullOrEmpty(userUid) || string.IsNullOrEmpty(acknowledgmentId))
        {
            return new ProcessAcknowledgmentResult(false, false, false, success, errorCode, 0);
        }

        var matchesLatest = _latestAcknowledgments.TryGetValue(userUid, out var latestInfo)
            && string.Equals(latestInfo.AcknowledgmentId, acknowledgmentId, StringComparison.Ordinal);

        var matchesHistory = !matchesLatest && IsHashInHistory(userUid, acknowledgmentId);

        if (!matchesLatest && !matchesHistory)
        {
            return new ProcessAcknowledgmentResult(false, false, false, success, errorCode, 0);
        }

        var infoForTiming = latestInfo;
        if (matchesHistory && !matchesLatest)
        {
            if (!_latestAcknowledgments.TryGetValue(userUid, out infoForTiming))
            {
                infoForTiming = new LatestInfo(acknowledgmentId, receivedAt);
            }
        }

        infoForTiming = infoForTiming.AcknowledgmentId != null ? infoForTiming : new LatestInfo(acknowledgmentId, receivedAt);
        var responseMs = Math.Max(0, (long)(receivedAt - infoForTiming.CreatedAt).TotalMilliseconds);

        Interlocked.Increment(ref _resultTotal);
        Interlocked.Add(ref _resultResponseTotalMs, responseMs);
        Interlocked.Increment(ref _resultResponseCount);

        if (success)
        {
            Interlocked.Increment(ref _resultSuccess);
        }
        else
        {
            Interlocked.Increment(ref _resultFail);
            _resultErrorCounts.AddOrUpdate(errorCode, 1, (_, old) => old + 1);
        }

        if (matchesLatest)
        {
            _latestAcknowledgments.TryRemove(userUid, out _);
            AddToProcessed(userUid, acknowledgmentId);
        }
        else if (matchesHistory)
        {
            if (IsProcessed(userUid, acknowledgmentId))
            {
                return new ProcessAcknowledgmentResult(false, false, false, success, errorCode, 0);
            }
            AddToProcessed(userUid, acknowledgmentId);
        }

        return new ProcessAcknowledgmentResult(true, matchesLatest, matchesHistory, success, errorCode, responseMs);
    }

    /// <summary>
    /// Removes a pending acknowledgment for a user.
    /// </summary>
    public RemovePendingResult RemovePending(string userUid, string? expectedAcknowledgmentId = null)
    {
        if (string.IsNullOrEmpty(userUid))
        {
            return new RemovePendingResult(false, null);
        }

        if (!_latestAcknowledgments.TryGetValue(userUid, out var latestInfo))
        {
            return new RemovePendingResult(false, null);
        }

        if (!string.IsNullOrEmpty(expectedAcknowledgmentId)
            && !string.Equals(latestInfo.AcknowledgmentId, expectedAcknowledgmentId, StringComparison.Ordinal))
        {
            return new RemovePendingResult(false, null);
        }

        if (_latestAcknowledgments.TryRemove(userUid, out var removedInfo))
        {
            RemoveFromHistory(userUid, removedInfo.AcknowledgmentId);
            AddToProcessed(userUid, removedInfo.AcknowledgmentId);
            return new RemovePendingResult(true, removedInfo.AcknowledgmentId);
        }

        return new RemovePendingResult(false, null);
    }

    /// <summary>
    /// Finds and removes all acknowledgments older than <paramref name="maxAge"/>
    /// relative to <paramref name="now"/>.
    /// </summary>
    public List<CleanupEntry> CleanupOldPending(TimeSpan maxAge, DateTimeOffset now)
    {
        var cutoff = now.Subtract(maxAge);
        var toRemove = new List<CleanupEntry>();

        foreach (var kvp in _latestAcknowledgments)
        {
            if (kvp.Value.CreatedAt < cutoff)
            {
                var responseMs = Math.Max(0, (long)(now - kvp.Value.CreatedAt).TotalMilliseconds);
                toRemove.Add(new CleanupEntry(kvp.Key, kvp.Value.AcknowledgmentId, kvp.Value.CreatedAt, responseMs));
            }
        }

        foreach (var entry in toRemove)
        {
            if (_latestAcknowledgments.TryRemove(entry.UserUid, out _))
            {
                RemoveFromHistory(entry.UserUid, entry.AckId);
                AddToProcessed(entry.UserUid, entry.AckId);

                Interlocked.Increment(ref _resultTotal);
                Interlocked.Increment(ref _resultFail);
                Interlocked.Add(ref _resultResponseTotalMs, entry.ResponseMs);
                Interlocked.Increment(ref _resultResponseCount);
                _resultErrorCounts.AddOrUpdate(Sphene.API.Dto.User.AcknowledgmentErrorCode.NotArrivedTimeout, 1, (_, old) => old + 1);
            }
        }

        return toRemove;
    }

    /// <summary>
    /// Finds and removes all pending acknowledgments matching the given ID.
    /// </summary>
    public List<CleanupEntry> ProcessTimeout(string acknowledgmentId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(acknowledgmentId))
        {
            return new List<CleanupEntry>();
        }

        var timedOut = new List<CleanupEntry>();

        foreach (var kvp in _latestAcknowledgments)
        {
            if (string.Equals(kvp.Value.AcknowledgmentId, acknowledgmentId, StringComparison.Ordinal))
            {
                var responseMs = Math.Max(0, (long)(now - kvp.Value.CreatedAt).TotalMilliseconds);
                timedOut.Add(new CleanupEntry(kvp.Key, kvp.Value.AcknowledgmentId, kvp.Value.CreatedAt, responseMs));
            }
        }

        foreach (var entry in timedOut)
        {
            if (_latestAcknowledgments.TryRemove(entry.UserUid, out _))
            {
                RemoveFromHistory(entry.UserUid, acknowledgmentId);
                AddToProcessed(entry.UserUid, acknowledgmentId);

                Interlocked.Increment(ref _resultTotal);
                Interlocked.Increment(ref _resultFail);
                Interlocked.Add(ref _resultResponseTotalMs, entry.ResponseMs);
                Interlocked.Increment(ref _resultResponseCount);
                _resultErrorCounts.AddOrUpdate(Sphene.API.Dto.User.AcknowledgmentErrorCode.NotArrivedTimeout, 1, (_, old) => old + 1);
            }
        }

        return timedOut;
    }

    public bool HasPendingAcknowledgments()
    {
        return !_latestAcknowledgments.IsEmpty;
    }

    public int GetPendingCount()
    {
        return _latestAcknowledgments.Count;
    }

    public Dictionary<string, string> GetPendingAcknowledgments()
    {
        return _latestAcknowledgments.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.AcknowledgmentId,
            StringComparer.Ordinal);
    }

    public bool IsPending(string userUid)
    {
        return _latestAcknowledgments.ContainsKey(userUid);
    }

    public bool IsLatest(string userUid, string acknowledgmentId)
    {
        return _latestAcknowledgments.TryGetValue(userUid, out var info)
            && string.Equals(info.AcknowledgmentId, acknowledgmentId, StringComparison.Ordinal);
    }

    public bool IsInHistory(string userUid, string acknowledgmentId)
    {
        return IsHashInHistory(userUid, acknowledgmentId);
    }

    public AckResultMetrics GetMetrics()
    {
        var total = Interlocked.Read(ref _resultTotal);
        var success = Interlocked.Read(ref _resultSuccess);
        var fail = Interlocked.Read(ref _resultFail);
        var totalMs = Interlocked.Read(ref _resultResponseTotalMs);
        var count = Interlocked.Read(ref _resultResponseCount);
        var avg = count > 0 ? (double)totalMs / count : 0d;

        return new AckResultMetrics(
            total, success, fail, avg,
            new Dictionary<Sphene.API.Dto.User.AcknowledgmentErrorCode, long>(_resultErrorCounts));
    }

    public void Clear()
    {
        _latestAcknowledgments.Clear();
        _acknowledgmentHistory.Clear();
        _processedAcknowledgments.Clear();
    }

    // ------------------------------------------------------------------
    // Static helpers (moved from SessionAcknowledgmentManager)
    // ------------------------------------------------------------------

    public static string GenerateSessionId()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var counter = Interlocked.Increment(ref _sessionCounter);
        return $"session_{timestamp}_{counter}";
    }

    public static string? ExtractSessionId(string acknowledgmentId)
    {
        if (string.IsNullOrEmpty(acknowledgmentId))
            return null;

        var parts = acknowledgmentId.Split('_');
        if (parts.Length >= 3 && string.Equals(parts[0], "session", StringComparison.Ordinal))
        {
            return $"{parts[0]}_{parts[1]}_{parts[2]}";
        }

        return null;
    }

    public static bool IsValidHashVersion(string hashVersionKey)
    {
        return !string.IsNullOrEmpty(hashVersionKey) && hashVersionKey.Contains("_", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private void AddToProcessed(string userKey, string acknowledgmentId)
    {
        _processedAcknowledgments.AddOrUpdate(
            userKey,
            _ => new List<string> { acknowledgmentId },
            (_, existing) =>
            {
                var list = new List<string>(existing);
                if (!list.Contains(acknowledgmentId, StringComparer.Ordinal))
                {
                    list.Add(acknowledgmentId);
                }
                if (list.Count > MaxHistoryEntriesPerUser)
                {
                    list.RemoveAt(0);
                }
                return list;
            });
    }

    private bool IsProcessed(string userKey, string acknowledgmentId)
    {
        return _processedAcknowledgments.TryGetValue(userKey, out var processed)
            && processed.Contains(acknowledgmentId, StringComparer.Ordinal);
    }

    private void AddToHistory(string userKey, string acknowledgmentId)
    {
        _acknowledgmentHistory.AddOrUpdate(
            userKey,
            _ => new List<string> { acknowledgmentId },
            (_, existing) =>
            {
                var list = new List<string>(existing);
                list.Remove(acknowledgmentId);
                list.Add(acknowledgmentId);
                if (list.Count > MaxHistoryEntriesPerUser)
                {
                    list.RemoveAt(0);
                }
                return list;
            });
    }

    private bool IsHashInHistory(string userKey, string acknowledgmentId)
    {
        return _acknowledgmentHistory.TryGetValue(userKey, out var history)
            && history.Contains(acknowledgmentId, StringComparer.Ordinal);
    }

    private void RemoveFromHistory(string userKey, string acknowledgmentId)
    {
        if (_acknowledgmentHistory.TryGetValue(userKey, out var history))
        {
            history.Remove(acknowledgmentId);
        }
    }
}
