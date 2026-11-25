namespace Sphene.Services;

/// <summary>Combines durable unread rows with session requests. Changes made while
/// an unread snapshot is loading take precedence over that snapshot.</summary>
internal sealed class NotificationInboxState
{
    private const int Capacity = 24;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, InboxItem> _items = new(StringComparer.Ordinal);
    private HashSet<string>? _changed;
    private int _generation;

    public int Count { get { lock (_gate) return _items.Count; } }

    public IReadOnlyList<InboxItem> Snapshot()
    {
        lock (_gate) return _items.Values.OrderByDescending(i => i.Timestamp).ToList();
    }

    public void Add(InboxItem item)
    {
        lock (_gate)
        {
            _changed?.Add(item.Id);
            Store(item);
        }
    }

    private void Store(InboxItem item)
    {
        _items[item.Id] = item;
        if (_items.Count > Capacity) _items.Remove(_items.Values.MinBy(i => i.Timestamp)!.Id);
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            _changed?.Add(id);
            return _items.Remove(id);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _changed = null;
            _items.Clear();
        }
    }

    public int BeginSync()
    {
        lock (_gate)
        {
            _changed = new(StringComparer.Ordinal);
            return ++_generation;
        }
    }

    public void EndSync(int generation, IReadOnlyList<InboxItem>? unread)
    {
        lock (_gate)
        {
            if (generation != _generation || _changed == null) return;
            if (unread != null)
            {
                var ids = unread.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var item in _items.Values.Where(i => i.Kind == InboxKind.Server
                    && !ids.Contains(i.Id) && !_changed.Contains(i.Id)).ToArray())
                    _items.Remove(item.Id);
                foreach (var item in unread)
                {
                    if (!_changed.Contains(item.Id)) Store(item);
                }
            }
            _changed = null;
        }
    }
}
