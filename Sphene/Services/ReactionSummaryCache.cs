using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;

namespace Sphene.Services;

/// <summary>One viewer's reaction state, shared by feed rows and discussion windows.</summary>
internal sealed class ReactionSummaryCache(Func<ReactionSetDto, Task<ReactionSummaryDto>> load,
    Action<Exception> reportFailure, TimeProvider clock)
{
    private sealed class Entry
    {
        public ReactionSummaryDto? Summary;
        public string? Group;
        public string? Owner;
        public int Revision;
        public int Writers;
        public int LastWrite;
        public bool Reading;
        public DateTimeOffset NextRead;
        public DateTimeOffset LastSeen;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<(ReactionTargetKind, long), Entry> _entries = [];
    private int _reads;
    private DateTimeOffset _nextPrune;

    private Entry Get(ReactionTargetKind kind, long id)
    {
        if (!_entries.TryGetValue((kind, id), out var entry))
            _entries[(kind, id)] = entry = new();
        entry.LastSeen = clock.GetUtcNow();
        return entry;
    }

    public ReactionSummaryDto? Read(ReactionTargetKind kind, long id, string? owner, GroupData? group)
    {
        Entry entry;
        int revision;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (now >= _nextPrune)
            {
                foreach (var key in _entries.Where(pair => !pair.Value.Reading && pair.Value.Writers == 0
                    && now - pair.Value.LastSeen > TimeSpan.FromMinutes(5)).Select(pair => pair.Key).ToArray())
                    _entries.Remove(key);
                _nextPrune = now.AddMinutes(1);
            }
            entry = Get(kind, id);
            entry.Group ??= group?.GID;
            entry.Owner ??= owner;
            if (entry.Reading || entry.Writers != 0 || entry.NextRead > now || _reads >= 6)
                return entry.Summary;
            entry.Reading = true;
            _reads++;
            revision = entry.Revision;
        }
        _ = Refresh(entry, revision, new(kind, id, ReactionKind.Like, true, OwnerUid: owner, Group: group));
        lock (_gate) return entry.Summary;
    }

    private async Task Refresh(Entry entry, int revision, ReactionSetDto dto)
    {
        ReactionSummaryDto? summary = null;
        try { summary = await load(dto).ConfigureAwait(false); }
        catch (Exception ex) { reportFailure(ex); }
        finally
        {
            lock (_gate)
            {
                _reads--;
                entry.Reading = false;
                if (revision == entry.Revision)
                {
                    if (summary != null) entry.Summary = summary;
                    // Public visitors may be outside the server's push audience.
                    entry.NextRead = clock.GetUtcNow().AddSeconds(summary == null ? 5 : 60);
                }
            }
        }
    }

    public async Task<ReactionSummaryDto> Write(ReactionSetDto dto, Func<ReactionSetDto, Task<ReactionSummaryDto>> save)
    {
        Entry entry;
        int revision;
        int write;
        lock (_gate)
        {
            entry = Get(dto.TargetKind, dto.TargetId);
            entry.Group ??= dto.Group?.GID;
            entry.Owner ??= dto.OwnerUid;
            revision = ++entry.Revision;
            write = ++entry.LastWrite;
            entry.Writers++;
        }
        ReactionSummaryDto? result = null;
        try { return result = await save(dto).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                entry.Writers--;
                if (result != null && entry.Writers == 0 && write == entry.LastWrite)
                    entry.Summary = result;
                var settled = result != null && entry.Writers == 0 && revision == entry.Revision;
                entry.Revision++;
                entry.NextRead = settled ? clock.GetUtcNow().AddMinutes(1) : default;
            }
        }
    }

    public void Invalidate(ReactionTargetKind? kind, long? id, string? group, string? owner)
    {
        lock (_gate)
        {
            foreach (var (key, entry) in _entries)
            {
                if (kind != null && key.Item1 != kind || id != null && key.Item2 != id
                    || group != null && !string.Equals(entry.Group, group, StringComparison.Ordinal)
                    || owner != null && !string.Equals(entry.Owner, owner, StringComparison.Ordinal)) continue;
                entry.Revision++;
                entry.NextRead = default;
            }
        }
    }

    public void Clear()
    {
        // Pending operations only hold their detached entry after a session change.
        lock (_gate) _entries.Clear();
    }
}
