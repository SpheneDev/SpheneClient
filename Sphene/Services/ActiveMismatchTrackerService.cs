using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Sphene.Services;

public sealed class ActiveMismatchTrackerService : IDisposable
{
    private readonly ILogger<ActiveMismatchTrackerService> _logger;
    private readonly string _configDirectory;
    private readonly Dictionary<(string Uid, string GamePath), ActiveMismatchRecord> _records = new();
    private long _globalTotalCheckCount;
    private long _globalTotalMismatchCount;
    private readonly Lock _lock = new();
    private readonly Timer _saveTimer;
    private bool _dirty;
    private bool _disposed;

    private const string FileName = "active_mismatch_tracker.json";

    public ActiveMismatchTrackerService(ILogger<ActiveMismatchTrackerService> logger, string configDirectory)
    {
        _logger = logger;
        _configDirectory = configDirectory;
        Load();
        _saveTimer = new Timer(_ => SaveIfDirty(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private long RecordCheckInternal(string uid, string gamePath, bool isMismatch, HashSet<string> sources, HashSet<string> objectKinds)
    {
        lock (_lock)
        {
            var key = (uid, gamePath.ToLowerInvariant());
            if (_records.TryGetValue(key, out var existing))
            {
                existing.TotalCheckCount++;
                if (isMismatch)
                {
                    existing.MismatchCount++;
                    existing.LastSeen = DateTimeOffset.UtcNow;
                }
                foreach (var source in sources) existing.Sources.Add(source);
                foreach (var kind in objectKinds) existing.ObjectKinds.Add(kind);
            }
            else
            {
                _records[key] = new ActiveMismatchRecord
                {
                    Uid = uid,
                    GamePath = gamePath,
                    MismatchCount = isMismatch ? 1 : 0,
                    TotalCheckCount = 1,
                    FirstSeen = DateTimeOffset.UtcNow,
                    LastSeen = isMismatch ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue,
                    Sources = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase),
                    ObjectKinds = new HashSet<string>(objectKinds, StringComparer.OrdinalIgnoreCase),
                };
            }

            _dirty = true;
            return _records[key].MismatchCount;
        }
    }

    public void RecordCheck(string uid, string gamePath, bool isMismatch, HashSet<string> sources, HashSet<string> objectKinds)
    {
        _ = RecordCheckInternal(uid, gamePath, isMismatch, sources, objectKinds);
    }

    public void RecordScan(string uid, bool hasMismatch)
    {
        lock (_lock)
        {
            _globalTotalCheckCount++;
            if (hasMismatch) _globalTotalMismatchCount++;
            _dirty = true;
        }
    }

    public void RecordMismatch(string uid, string gamePath, HashSet<string> sources, HashSet<string> objectKinds)
    {
        _ = RecordCheckInternal(uid, gamePath, true, sources, objectKinds);
    }

    public long RecordMismatchAndGetMismatchCount(string uid, string gamePath, HashSet<string> sources, HashSet<string> objectKinds)
    {
        return RecordCheckInternal(uid, gamePath, true, sources, objectKinds);
    }

    public List<ActiveMismatchRecord> GetRecords()
    {
        lock (_lock)
        {
            var records = _records.Values.ToList();
            // Populate global total for percentage calculation
            foreach (var record in records)
            {
                record.GlobalTotalCheckCount = _globalTotalCheckCount;
            }
            return records;
        }
    }

    public (long TotalChecks, long TotalMismatches) GetGlobalStats()
    {
        lock (_lock)
        {
            return (_globalTotalCheckCount, _globalTotalMismatchCount);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _records.Clear();
            _globalTotalCheckCount = 0;
            _globalTotalMismatchCount = 0;
            _dirty = true;
        }

        Save();
    }

    public void ResetGlobalCounters()
    {
        lock (_lock)
        {
            _globalTotalCheckCount = 0;
            _globalTotalMismatchCount = 0;
            // Also reset per-record check counts
            foreach (var record in _records.Values)
            {
                record.TotalCheckCount = 0;
            }
            _dirty = true;
        }

        Save();
    }

    public void Save()
    {
        try
        {
            string filePath = Path.Combine(_configDirectory, FileName);
            List<ActiveMismatchRecordDto> dtos;
            lock (_lock)
            {
                dtos = _records.Values.Select(r => new ActiveMismatchRecordDto
                {
                    Uid = r.Uid,
                    GamePath = r.GamePath,
                    MismatchCount = r.MismatchCount,
                    TotalCheckCount = r.TotalCheckCount,
                    FirstSeen = r.FirstSeen,
                    LastSeen = r.LastSeen,
                    Sources = r.Sources.ToList(),
                    ObjectKinds = r.ObjectKinds.ToList(),
                }).ToList();
            }

            var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
            
            // Use atomic write pattern: write to temp file, then move to final location
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
            
            _dirty = false;
            _logger.LogDebug("Saved {count} active mismatch records to {path}", dtos.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save active mismatch tracker data");
        }
    }

    private void Load()
    {
        try
        {
            string filePath = Path.Combine(_configDirectory, FileName);
            if (!File.Exists(filePath))
            {
                _logger.LogDebug("No existing active mismatch tracker file at {path}", filePath);
                return;
            }

            var json = File.ReadAllText(filePath);
            var dtos = JsonSerializer.Deserialize<List<ActiveMismatchRecordDto>>(json);
            if (dtos == null) return;

            lock (_lock)
            {
                foreach (var dto in dtos)
                {
                    var key = (dto.Uid, dto.GamePath.ToLowerInvariant());
                    _records[key] = new ActiveMismatchRecord
                    {
                        Uid = dto.Uid,
                        GamePath = dto.GamePath,
                        MismatchCount = dto.MismatchCount,
                        TotalCheckCount = dto.TotalCheckCount,
                        FirstSeen = dto.FirstSeen,
                        LastSeen = dto.LastSeen,
                        Sources = new HashSet<string>(dto.Sources ?? [], StringComparer.OrdinalIgnoreCase),
                        ObjectKinds = new HashSet<string>(dto.ObjectKinds ?? [], StringComparer.OrdinalIgnoreCase),
                    };
                }
            }

            _logger.LogDebug("Loaded {count} active mismatch records from {path}", dtos.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load active mismatch tracker data");
        }
    }

    private void SaveIfDirty()
    {
        if (_dirty)
        {
            Save();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Dispose();
        SaveIfDirty();
    }
}
