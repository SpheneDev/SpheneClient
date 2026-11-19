using Sphene.API.Dto.Group;

namespace Sphene.Services;

/// <summary>
/// Dependency-free request state for the Hub's Discover surface - the
/// community directory fetch and public-community preview lookups.
///
/// Every request binds to the generation that issued it; every generation
/// check and the state write it guards run inside one lock, so a profile
/// reset (<see cref="Reset"/>) can't interleave between them. Queued UI
/// work re-validates <see cref="IsCurrent"/> at open time - a stale
/// completion can neither repopulate the directory, latch an error, claim a
/// loading flag nor surface a retry hint under the new profile.
/// </summary>
public sealed class DiscoverRequestTracker
{
    private readonly Lock _lock = new();
    private int _generation;
    private List<SyncshellDirectoryEntryDto>? _entries;
    private bool _directoryLoading;
    private bool _directoryError;
    private string? _previewLoadingGid;
    private string? _previewError;

    /// <summary>Loaded directory snapshot; null until the first result lands.</summary>
    public List<SyncshellDirectoryEntryDto>? Entries
    {
        get { lock (_lock) { return _entries; } }
    }

    /// <summary>True while a directory fetch is in flight.</summary>
    public bool DirectoryLoading
    {
        get { lock (_lock) { return _directoryLoading; } }
    }

    /// <summary>The last directory fetch failed - the section shows a retry.</summary>
    public bool DirectoryError
    {
        get { lock (_lock) { return _directoryError; } }
    }

    /// <summary>GID of the preview request in flight; null when idle.</summary>
    public string? PreviewLoadingGid
    {
        get { lock (_lock) { return _previewLoadingGid; } }
    }

    /// <summary>Inline retry hint after a failed preview open; null when clear.</summary>
    public string? PreviewError
    {
        get { lock (_lock) { return _previewError; } }
    }

    /// <summary>Whether a request issued under <paramref name="generation"/>
    /// is still the current one - re-checked wherever queued work lands.</summary>
    public bool IsCurrent(int generation)
    {
        lock (_lock) { return generation == _generation; }
    }

    /// <summary>Bumps the generation and clears all request state - a profile
    /// switch or followed-set change. In-flight completions become stale and
    /// the freed flags let the new state request immediately.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _generation++;
            _entries = null;
            _directoryLoading = false;
            _directoryError = false;
            _previewLoadingGid = null;
            _previewError = null;
        }
    }

    /// <summary>Atomic gate + claim for the directory fetch - refuses while
    /// entries exist, a fetch is in flight or an error is latched.</summary>
    public bool TryBeginDirectory(bool hasEntries, bool connected, out int generation)
    {
        lock (_lock)
        {
            generation = _generation;
            if (hasEntries || _directoryLoading || _directoryError || !connected)
            {
                return false;
            }
            _directoryLoading = true;
            return true;
        }
    }

    /// <summary>Publishes a successful directory result - no-op for a stale
    /// generation.</summary>
    public bool ApplyDirectoryResult(int generation, List<SyncshellDirectoryEntryDto> entries)
    {
        lock (_lock)
        {
            if (generation != _generation) return false;
            _entries = entries;
            return true;
        }
    }

    /// <summary>Latches the directory error state - no-op for a stale
    /// generation.</summary>
    public bool ApplyDirectoryFailure(int generation)
    {
        lock (_lock)
        {
            if (generation != _generation) return false;
            _directoryError = true;
            return true;
        }
    }

    /// <summary>The user asked to retry after a latched error.</summary>
    public void ClearDirectoryError()
    {
        lock (_lock) { _directoryError = false; }
    }

    /// <summary>Releases the directory slot - only while the finishing
    /// request still owns the generation, so a stale finally can't evict
    /// the request that claimed it after <see cref="Reset"/>.</summary>
    public void EndDirectory(int generation)
    {
        lock (_lock)
        {
            if (generation != _generation) return;
            _directoryLoading = false;
        }
    }

    /// <summary>Atomic gate + claim for a public-preview fetch - one preview
    /// request at a time; a fresh claim clears the previous hint.</summary>
    public bool TryBeginPreview(string gid, out int generation)
    {
        lock (_lock)
        {
            generation = _generation;
            if (_previewLoadingGid != null) return false;
            _previewLoadingGid = gid;
            _previewError = null;
            return true;
        }
    }

    /// <summary>Latches the inline preview retry hint - no-op for a stale
    /// generation.</summary>
    public bool ApplyPreviewError(int generation, string error)
    {
        lock (_lock)
        {
            if (generation != _generation) return false;
            _previewError = error;
            return true;
        }
    }

    /// <summary>Releases the preview slot - generation-bound like
    /// <see cref="EndDirectory"/>.</summary>
    public void EndPreview(int generation)
    {
        lock (_lock)
        {
            if (generation != _generation) return;
            _previewLoadingGid = null;
        }
    }
}
