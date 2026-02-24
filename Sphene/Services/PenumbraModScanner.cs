using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.IO;
using System.Reflection;
using Sphene.Interop.Ipc;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Sphene.Services.Mediator;

namespace Sphene.Services;

public class PenumbraModScanner : DisposableMediatorSubscriberBase
{
    private readonly IpcManager _ipcManager;

    public record ModDebugInfo(
        string DirectoryName,
        string ModName,
        string Path,
        bool IsEnabled,
        bool IsInherited,
        bool IsTemporary,
        int Priority,
        List<string> ActiveOptions,
        List<string> ChangedItems,
        int ActiveFilesCount,
        int TotalFilesCount,
        string Status,
        bool HasCharacterLegacyShpk
    );

    private Dictionary<string, (string ModName, string OptionName, int Priority)> _lookupCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (string ResolvedPath, string ModName, string OptionName, int Priority)> _gamePathLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private List<(string GamePath, string ResolvedPath, string ModName, string OptionName, int Priority)> _allEnabledModsCache = [];
    private bool _cacheDirty = true;
    private readonly bool _autoFullScanEnabled = false;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IDataManager _gameData;
    private readonly Dictionary<uint, string> _actionTimelineCache = new();
    private readonly Lock _emoteCacheLock = new();
    private readonly Dictionary<string, List<string>> _emoteFileNameCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _emoteLookupBuilt;
    private static readonly PropertyInfo? ActionTimelineNameProperty = typeof(ActionTimeline).GetProperty("Name");
    private static readonly PropertyInfo? ActionTimelineKeyProperty = typeof(ActionTimeline).GetProperty("Key");
    private static readonly string[] GamePathRoots =
    [
        "chara/",
        "sound/",
        "music/",
        "se/",
        "ui/",
        "common/",
        "vfx/",
        "bgcommon/",
        "bg/",
        "bgex/",
        "bgparts/",
        "cut/",
        "event/",
        "field/",
        "shader/"
    ];
    private string? _playerRaceCode;
    
    // Tracks mods that are currently "in use" by the local player (i.e., at least one file from them is resolved).
    private readonly ConcurrentDictionary<string, byte> _activePlayerMods = new(StringComparer.OrdinalIgnoreCase);

    public List<ModDebugInfo> LastScanDebugInfo { get; private set; } = [];

    internal string? PlayerRaceCode => _playerRaceCode;

    public PenumbraModScanner(ILogger<PenumbraModScanner> logger, IpcManager ipcManager, SpheneMediator mediator, IDataManager gameData)
        : base(logger, mediator)
    {
        _ipcManager = ipcManager;
        _gameData = gameData;
        Mediator.Subscribe<PenumbraModSettingChangedMessage>(this, _ => TriggerAutoScan());
        Mediator.Subscribe<PenumbraInitializedMessage>(this, _ => TriggerAutoScan());
        Mediator.Subscribe<CensusUpdateMessage>(this, msg => UpdatePlayerRaceCode(msg.Gender, msg.TribeId));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    private CancellationTokenSource? _scanCts;

    private void TriggerAutoScan()
    {
        _cacheDirty = true;
        if (_autoFullScanEnabled)
        {
            TriggerScan();
        }
    }

    private void TriggerScan()
    {
        _cacheDirty = true;
        
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        _ = Task.Run(async () => 
        {
            try 
            {
                await Task.Delay(500, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                await GetModFileLookupAsync(CancellationToken.None).ConfigureAwait(false);
                
                if (!token.IsCancellationRequested)
                {
                    Logger.LogDebug("Penumbra mod scan finished, publishing completion message.");
                    Mediator.Publish(new PenumbraModScanFinishedMessage());
                }
            }
            catch (OperationCanceledException)
            {
                Logger.LogTrace("Penumbra mod scan was cancelled.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to scan mods after change");
            }
        }, token);
    }


    public void MarkDirty()
    {
        Logger.LogDebug("Penumbra mod cache marked as dirty by user request -> Triggering Rescan.");
        TriggerScan();
    }

    public void SetActivePlayerMods(IEnumerable<string> modNames)
    {
        _activePlayerMods.Clear();
        foreach (var name in modNames)
        {
            _activePlayerMods[name] = 1;
        }
    }

    public bool IsModActiveForPlayer(string modName)
    {
        return _activePlayerMods.ContainsKey(modName);
    }

    public async Task<List<(string GamePath, string ResolvedPath, string ModName, string OptionName, int Priority)>> GetEnabledModReplacementsAsync(CancellationToken ct)
    {
        await GetModFileLookupAsync(ct).ConfigureAwait(false);
        return _gamePathLookupCache.Select(kvp => (kvp.Key, kvp.Value.ResolvedPath, kvp.Value.ModName, kvp.Value.OptionName, kvp.Value.Priority)).ToList();
    }

    public async Task<List<(string GamePath, string ResolvedPath, string ModName, string OptionName, int Priority)>> GetAllEnabledModReplacementsAsync(CancellationToken ct)
    {
        await GetModFileLookupAsync(ct).ConfigureAwait(false);
        return _allEnabledModsCache.ToList();
    }

    public async Task<Dictionary<string, (string ModName, string OptionName, int Priority)>> ForceFullScanAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cacheDirty = true;
            _lookupCache.Clear();
            _gamePathLookupCache.Clear();
            _allEnabledModsCache.Clear();
        }
        finally
        {
            _lock.Release();
        }

        return await GetModFileLookupAsync(ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, (string ModName, string OptionName, int Priority)>> GetActiveModLookupForResolvedPathsAsync(Dictionary<string, HashSet<string>> resolvedPaths, CancellationToken ct)
    {
        var result = new Dictionary<string, (string ModName, string OptionName, int Priority)>(StringComparer.OrdinalIgnoreCase);
        if (!_ipcManager.Penumbra.APIAvailable || resolvedPaths == null)
        {
            return result;
        }

        var pathList = new List<string>();
        foreach (var path in resolvedPaths.Keys)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                pathList.Add(path);
            }
        }

        if (pathList.Count == 0)
        {
            return result;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_ipcManager.Penumbra.APIAvailable)
            {
                return result;
            }

            (Guid Id, string Name) collectionInfo;
            var playerCollection = _ipcManager.Penumbra.GetCollectionForObject(0);
            if (playerCollection.ObjectValid)
            {
                collectionInfo = playerCollection.EffectiveCollection;
            }
            else
            {
                var currentCollection = _ipcManager.Penumbra.GetCollection(ApiCollectionType.Current);
                if (currentCollection == null)
                {
                    return result;
                }
                collectionInfo = currentCollection.Value;
            }

            var allSettings = _ipcManager.Penumbra.GetAllModSettings(collectionInfo.Id);
            if (allSettings == null || allSettings.Count == 0)
            {
                return result;
            }

            var enabledSettings = new List<KeyValuePair<string, (bool Enabled, int Priority, Dictionary<string, List<string>> Settings, bool Inherited, bool Temporary)>>();
            foreach (var setting in allSettings)
            {
                if (setting.Value.Enabled || setting.Value.Temporary)
                {
                    enabledSettings.Add(setting);
                }
            }

            if (enabledSettings.Count == 0)
            {
                return result;
            }

            var modList = _ipcManager.Penumbra.GetModList();
            var dirToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (modList.Count > 0 && allSettings.Count > 0)
            {
                int keyMatches = 0;
                int valueMatches = 0;
                foreach (var item in modList.Take(50))
                {
                    if (allSettings.ContainsKey(item.Key)) keyMatches++;
                    if (allSettings.ContainsKey(item.Value)) valueMatches++;
                }

                if (valueMatches > keyMatches)
                {
                    foreach (var kvp in modList)
                    {
                        if (!dirToName.ContainsKey(kvp.Value))
                        {
                            dirToName[kvp.Value] = kvp.Key;
                        }
                    }
                }
                else
                {
                    dirToName = new Dictionary<string, string>(modList, StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                dirToName = new Dictionary<string, string>(modList, StringComparer.OrdinalIgnoreCase);
            }

            var baseModDirectory = _ipcManager.Penumbra.ModDirectory;
            var modOptionsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var debugList = new List<ModDebugInfo>(enabledSettings.Count);
            foreach (var setting in enabledSettings)
            {
                var directoryName = setting.Key;
                var modName = dirToName.TryGetValue(directoryName, out var name) ? name : directoryName;
                var modPath = _ipcManager.Penumbra.GetModPath(directoryName, string.Empty);
                if (string.IsNullOrWhiteSpace(modPath))
                {
                    modPath = _ipcManager.Penumbra.GetModPath(string.Empty, directoryName);
                }
                if (!string.IsNullOrWhiteSpace(modPath) && !Path.IsPathRooted(modPath) && !string.IsNullOrWhiteSpace(baseModDirectory))
                {
                    modPath = Path.Combine(baseModDirectory, modPath);
                }

                var activeOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (setting.Value.Settings != null && setting.Value.Settings.Count > 0)
                {
                    foreach (var group in setting.Value.Settings)
                    {
                        var options = group.Value;
                        if (options == null || options.Count == 0)
                        {
                            activeOptions.Add("Default");
                            continue;
                        }

                        for (int i = 0; i < options.Count; i++)
                        {
                            activeOptions.Add($"{group.Key}: {options[i]}");
                        }
                    }
                }
                if (activeOptions.Count == 0)
                {
                    activeOptions.Add("Default");
                }
                var optionName = string.Join(", ", activeOptions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                modOptionsByName[modName] = optionName;

                var status = setting.Value.Enabled ? "Active (Fast Mode)" : "Inactive (Fast Mode)";
                debugList.Add(new ModDebugInfo(
                    directoryName,
                    modName,
                    modPath ?? string.Empty,
                    setting.Value.Enabled,
                    setting.Value.Inherited,
                    setting.Value.Temporary,
                    setting.Value.Priority,
                    activeOptions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                    [],
                    0,
                    0,
                    status,
                    false));
            }
            LastScanDebugInfo = debugList.OrderBy(x => x.DirectoryName, StringComparer.OrdinalIgnoreCase).ToList();
            var modRoots = new List<(string RootPath, string ModName, string OptionName, int Priority)>();
            foreach (var mod in enabledSettings)
            {
                var modName = dirToName.TryGetValue(mod.Key, out var name) ? name : mod.Key;
                var modPath = _ipcManager.Penumbra.GetModPath(mod.Key, string.Empty);
                if (string.IsNullOrWhiteSpace(modPath))
                {
                    modPath = _ipcManager.Penumbra.GetModPath(string.Empty, mod.Key);
                }
                if (string.IsNullOrWhiteSpace(modPath))
                {
                    continue;
                }
                if (!Path.IsPathRooted(modPath) && !string.IsNullOrWhiteSpace(baseModDirectory))
                {
                    modPath = Path.Combine(baseModDirectory, modPath);
                }
                modPath = modPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (!modPath.EndsWith(Path.DirectorySeparatorChar))
                {
                    modPath += Path.DirectorySeparatorChar;
                }
                var optionName = modOptionsByName.TryGetValue(modName, out var options) ? options : "Default";
                modRoots.Add((modPath, modName, optionName, mod.Value.Priority));
            }

            if (modRoots.Count == 0)
            {
                return result;
            }

            modRoots.Sort((a, b) => b.RootPath.Length.CompareTo(a.RootPath.Length));

            var activeModNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gamePathCache = new Dictionary<string, (string ResolvedPath, string ModName, string OptionName, int Priority)>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in pathList)
            {
                ct.ThrowIfCancellationRequested();
                string normalizedPath;
                try
                {
                    normalizedPath = Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < modRoots.Count; i++)
                {
                    var root = modRoots[i];
                    if (normalizedPath.StartsWith(root.RootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        result[normalizedPath] = (root.ModName, root.OptionName, root.Priority);
                        activeModNames.Add(root.ModName);
                        if (resolvedPaths.TryGetValue(path, out var gamePaths) && gamePaths != null)
                        {
                            foreach (var gamePath in gamePaths)
                            {
                                if (string.IsNullOrWhiteSpace(gamePath))
                                {
                                    continue;
                                }
                                var normalizedGamePath = gamePath.Replace('\\', '/');
                                gamePathCache[normalizedGamePath] = (normalizedPath, root.ModName, root.OptionName, root.Priority);
                            }
                        }
                        break;
                    }
                }
            }

            SetActivePlayerMods(activeModNames);
            _lookupCache = new Dictionary<string, (string ModName, string OptionName, int Priority)>(result, StringComparer.OrdinalIgnoreCase);
            _gamePathLookupCache = gamePathCache;
            _cacheDirty = false;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Dictionary<string, (string ModName, string OptionName, int Priority)>> GetModFileLookupAsync(CancellationToken ct)
    {
        if (!_cacheDirty && _lookupCache.Count > 0)
        {
            return new Dictionary<string, (string ModName, string OptionName, int Priority)>(_lookupCache, StringComparer.OrdinalIgnoreCase);
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_cacheDirty && _lookupCache.Count > 0)
            {
                return new Dictionary<string, (string ModName, string OptionName, int Priority)>(_lookupCache, StringComparer.OrdinalIgnoreCase);
            }

            if (!_ipcManager.Penumbra.APIAvailable)
            {
                Logger.LogWarning("Penumbra API not available, skipping mod scan.");
                return [];
            }

            // 1. Get Collection for Local Player
            (Guid Id, string Name) collectionInfo;
            var playerCollection = _ipcManager.Penumbra.GetCollectionForObject(0);
            if (playerCollection.ObjectValid)
            {
                collectionInfo = playerCollection.EffectiveCollection;
                Logger.LogDebug("[FileReplacementNew] Using collection for Local Player: {Name} ({Id})", collectionInfo.Name, collectionInfo.Id);
            }
            else
            {
                // Fallback to Current
                var currentCollection = _ipcManager.Penumbra.GetCollection(ApiCollectionType.Current);
                if (currentCollection == null)
                {
                    Logger.LogWarning("[FileReplacementNew] Could not get current collection.");
                    return [];
                }
                collectionInfo = currentCollection.Value;
                Logger.LogDebug("[FileReplacementNew] Using Current collection: {Name} ({Id})", collectionInfo.Name, collectionInfo.Id);
            }
            var collectionId = collectionInfo.Id;
            Logger.LogDebug("[FileReplacementNew] Scanning collection {CollectionId} ({CollectionName})", collectionId, collectionInfo.Name);

            // 2. Get All Mod Settings
            var allSettings = _ipcManager.Penumbra.GetAllModSettings(collectionId);
            if (allSettings == null)
            {
                Logger.LogWarning("[FileReplacementNew] Could not get mod settings.");
                return [];
            }
            Logger.LogDebug("[FileReplacementNew] GetAllModSettings returned {count} entries", allSettings.Count);

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                 foreach(var setting in allSettings)
                 {
                      bool isDebug = setting.Key.Contains("Skate Shoe", StringComparison.OrdinalIgnoreCase) || 
                                     setting.Key.Contains("Ribbon Galore", StringComparison.OrdinalIgnoreCase);
                      if (isDebug || Logger.IsEnabled(LogLevel.Trace))
                      {
                          Logger.LogDebug("[FileReplacementNew] Settings for {Key}: Enabled={Enabled}, Priority={Priority}, Inherited={Inherited}", 
                              setting.Key, setting.Value.Enabled, setting.Value.Priority, setting.Value.Inherited);
                      }
                 }
            }

            // 3. Get Mod List for Name Resolution
            var modList = _ipcManager.Penumbra.GetModList();
            Logger.LogDebug("[FileReplacementNew] GetModList returned {count} entries", modList.Count);

            // Determine GetModList format (Key=Dir vs Key=Name)
            var dirToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (modList.Count > 0 && allSettings.Count > 0)
            {
                // Check overlap with AllSettings keys (which are definitely Directory Names)
                int keyMatches = 0;
                int valueMatches = 0;
                
                // Sample up to 50 entries to decide
                foreach (var item in modList.Take(50))
                {
                    if (allSettings.ContainsKey(item.Key)) keyMatches++;
                    if (allSettings.ContainsKey(item.Value)) valueMatches++;
                }

                if (valueMatches > keyMatches)
                {
                    Logger.LogDebug("[FileReplacementNew] Detected GetModList format: Key=ModName, Value=DirectoryName. Reversing map.");
                    foreach (var kvp in modList)
                    {
                        // Handle potential duplicate directories if multiple names map to same dir (rare but possible)
                        if (!dirToName.ContainsKey(kvp.Value))
                        {
                            dirToName[kvp.Value] = kvp.Key;
                        }
                    }
                }
                else
                {
                    Logger.LogDebug("[FileReplacementNew] Detected GetModList format: Key=DirectoryName, Value=ModName. Using as is.");
                    dirToName = new Dictionary<string, string>(modList, StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                // Fallback: assume Key=DirectoryName as per previous assumption
                dirToName = new Dictionary<string, string>(modList, StringComparer.OrdinalIgnoreCase);
            }

            // Cross-reference ModList with AllSettings to find unassigned/missing mods
            var settingsKeys = new HashSet<string>(allSettings.Keys, StringComparer.OrdinalIgnoreCase);
            var missingMods = modList.Where(x => !settingsKeys.Contains(x.Key) && !settingsKeys.Contains(x.Value)).ToList();
            if (missingMods.Count > 0)
            {
                Logger.LogDebug("[FileReplacementNew] Found {Count} mods in ModList that are NOT in Collection Settings.", missingMods.Count);
                foreach (var missing in missingMods)
                {
                    bool isTarget = missing.Key.Contains("Skate Shoe", StringComparison.OrdinalIgnoreCase) || 
                                    missing.Value.Contains("Skate Shoe", StringComparison.OrdinalIgnoreCase) ||
                                    missing.Key.Contains("Ribbon Galore", StringComparison.OrdinalIgnoreCase) ||
                                    missing.Value.Contains("Ribbon Galore", StringComparison.OrdinalIgnoreCase);
                                    
                    if (isTarget)
                    {
                        Logger.LogWarning("[FileReplacementNew] Target mod found in ModList but MISSING from Collection Settings! Key: {Key}, Value: {Value}", missing.Key, missing.Value);
                    }
                }
            }

            var enabledMods = allSettings.Where(m => m.Value.Enabled || m.Value.Temporary).ToList();
            Logger.LogDebug("[FileReplacementNew] Found {Count} enabled mods.", enabledMods.Count);

            var baseModDir = _ipcManager.Penumbra.ModDirectory;
            if (string.IsNullOrEmpty(baseModDir))
            {
                Logger.LogWarning("[FileReplacementNew] Penumbra Mod Directory is not resolved. File scanning may fail for relative paths.");
            }
            else
            {
                 Logger.LogDebug("[FileReplacementNew] Penumbra Base Mod Directory: {Path}", baseModDir);
            }

            var bag = new ConcurrentBag<(string GamePath, string Path, string ModName, string OptionName, int Priority)>();
            var debugBag = new ConcurrentBag<ModDebugInfo>();

            // Add missing mods to debug info
            foreach(var missing in missingMods)
            {
                 // Try to guess Name vs Dir based on our previous detection
                 string name = missing.Value; 
                 string dir = missing.Key;
                 
                 // If we detected reversed map (Key=Name, Value=Dir), then swap
                 // We can check if dirToName was built from Key=Name
                 // Actually dirToName logic: if valueMatches > keyMatches, then Key=ModName, Value=DirectoryName.
                 // And we populated dirToName[Value] = Key.
                 // So dirToName keys are Directories.
                 
                 // Let's just use the raw Key/Value and let user decipher, or use heuristic.
                 // If key looks like a path (has slashes), it's dir.
                 if (dir.Contains('/') || dir.Contains('\\'))
                 {
                     // dir is likely directory
                 }
                 else if (name.Contains('/') || name.Contains('\\'))
                 {
                     var temp = dir;
                     dir = name;
                     name = temp;
                 }

                 debugBag.Add(new ModDebugInfo(
                        dir,
                        name,
                        "Not in Collection",
                        false,
                        false,
                        false,
                        0,
                        [],
                       [],
                        0,
                        0,
                        "Missing from Collection Settings",
                        false
                    ));
            }

            // 3. Process each mod
            await Parallel.ForEachAsync(enabledMods, ct, async (modEntry, token) =>
            {
                var modDirectoryName = modEntry.Key;
                var modPriority = modEntry.Value.Priority;
                var modSettings = modEntry.Value.Settings;
                var isTemporary = modEntry.Value.Temporary;
                string status = "OK";
                bool isDebugTarget = modDirectoryName.Contains("Skate Shoe", StringComparison.OrdinalIgnoreCase) || 
                                     modDirectoryName.Contains("Ribbon Galore", StringComparison.OrdinalIgnoreCase);

                // Resolve Mod Name from our smart map
                string resolvedModName = modDirectoryName;
                if (dirToName.TryGetValue(modDirectoryName, out var nameFromList))
                {
                    resolvedModName = nameFromList;
                }

                var fullModPath = _ipcManager.Penumbra.GetModPath(modDirectoryName, string.Empty);
                if (string.IsNullOrEmpty(fullModPath))
                {
                    fullModPath = _ipcManager.Penumbra.GetModPath(string.Empty, modDirectoryName);
                    if (string.IsNullOrEmpty(fullModPath))
                    {
                        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] Failed to resolve path for mod directory: {dir}", modDirectoryName);
                        debugBag.Add(new ModDebugInfo(
                            modDirectoryName,
                            resolvedModName,
                            "Unresolved Path",
                            true,
                            modEntry.Value.Inherited,
                            isTemporary,
                            modPriority,
                            modSettings.SelectMany(x => x.Value.Select(v => $"{x.Key}: {v}")).ToList(),
                            [],
                            0,
                            0,
                            "Path Resolution Failed",
                            false
                        ));
                        return;
                    }
                }

                // Handle relative paths
                if (!Path.IsPathRooted(fullModPath) && !string.IsNullOrEmpty(baseModDir))
                {
                    fullModPath = Path.Combine(baseModDir, fullModPath);
                }

                // Normalize path separators
                fullModPath = fullModPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

                // Log first mod path to verify
                if (bag.IsEmpty && Logger.IsEnabled(LogLevel.Trace))
                {
                     Logger.LogTrace("[FileReplacementNew] Processing mod: {dir} -> {path}", modDirectoryName, fullModPath);
                }

                // Verify directory exists
                if (!Directory.Exists(fullModPath))
                {
                    if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] Mod directory does not exist: {path}. Key was: {key}", fullModPath, modDirectoryName);
                    
                    // Try resolving as Mod Name if it was treated as Directory, or vice versa
                    // The key from GetAllModSettings is supposed to be Directory, so we tried GetModPath(key, empty)
                    // Now try GetModPath(empty, key) in case it's actually a Name
                    var altPath = _ipcManager.Penumbra.GetModPath(string.Empty, modDirectoryName);
                    if (!string.IsNullOrEmpty(altPath))
                    {
                        if (!Path.IsPathRooted(altPath) && !string.IsNullOrEmpty(baseModDir))
                        {
                            altPath = Path.Combine(baseModDir, altPath);
                        }
                        altPath = altPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                        
                        if (Directory.Exists(altPath))
                        {
                            Logger.LogDebug("[FileReplacementNew] Found valid directory by treating key as Mod Name: {path}", altPath);
                            fullModPath = altPath;
                        }
                    }

                    // Try stripping potential category prefix (e.g. "Body\ModName" -> "ModName")
                    if (!Directory.Exists(fullModPath) && (modDirectoryName.Contains('/') || modDirectoryName.Contains('\\')))
                    {
                        var leafName = Path.GetFileName(modDirectoryName);
                        if (!string.IsNullOrEmpty(leafName))
                        {
                             if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] Trying to resolve leaf name: {leaf} from {key}", leafName, modDirectoryName);
                             var leafPath = _ipcManager.Penumbra.GetModPath(string.Empty, leafName);
                             if (!string.IsNullOrEmpty(leafPath))
                             {
                                if (!Path.IsPathRooted(leafPath) && !string.IsNullOrEmpty(baseModDir))
                                {
                                    leafPath = Path.Combine(baseModDir, leafPath);
                                }
                                leafPath = leafPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

                                if (Directory.Exists(leafPath))
                                {
                                    Logger.LogDebug("[FileReplacementNew] Found valid directory by resolving leaf name: {path}", leafPath);
                                    fullModPath = leafPath;
                                }
                             }
                        }
                    }
                }

                if (!Directory.Exists(fullModPath) && !string.IsNullOrEmpty(baseModDir))
                {
                     // Final fallback: check if the key is a relative path that actually exists in base dir, ignoring Penumbra's resolved path
                     var directPath = Path.Combine(baseModDir, modDirectoryName);
                     if (Directory.Exists(directPath))
                     {
                         Logger.LogDebug("[FileReplacementNew] Found valid directory by direct combination: {path}", directPath);
                         fullModPath = directPath;
                     }
                }

                if (!Directory.Exists(fullModPath))
                {
                    debugBag.Add(new ModDebugInfo(
                        modDirectoryName,
                        resolvedModName,
                        fullModPath ?? "null",
                        true,
                        modEntry.Value.Inherited,
                        isTemporary,
                        modPriority,
                        modSettings.SelectMany(x => x.Value.Select(v => $"{x.Key}: {v}")).ToList(),
                        [],
                        0,
                        0,
                        "Directory Not Found",
                        false
                    ));
                    return;
                }

                int added = 0;
                int total = 0;
                bool hasCharacterLegacyShpk = false;
                var changedItems = new List<string>();
                var activeOptions = new List<string>();

                try
                {
                    var result = await ProcessModFiles(modDirectoryName, fullModPath, modSettings, modPriority, bag, resolvedModName, isDebugTarget, token).ConfigureAwait(false);
                    added = result.AddedCount;
                    total = result.TotalCount;
                    hasCharacterLegacyShpk = result.HasCharacterLegacyShpk;
                    changedItems = result.ChangedItems;
                    activeOptions = result.ActiveOptions;
                    
                    // resolvedModName = result.ResolvedModName; // Don't overwrite if we already have it from list, but ProcessModFiles might update it from meta.json if list was empty?
                    if (string.IsNullOrEmpty(resolvedModName) || string.Equals(resolvedModName, modDirectoryName, StringComparison.Ordinal))
                    {
                        resolvedModName = result.ResolvedModName;
                    }
                    
                    if (added == 0)
                    {
                        status = "No Files Added (Check Options)";
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing mod at {Path}", fullModPath);
                    status = $"Error: {ex.Message}";
                }
                
                debugBag.Add(new ModDebugInfo(
                    modDirectoryName,
                    resolvedModName,
                    fullModPath,
                    true,
                    modEntry.Value.Inherited,
                    isTemporary,
                    modPriority,
                    activeOptions,
                    changedItems,
                    added,
                    total,
                    status,
                    hasCharacterLegacyShpk
                ));

            }).ConfigureAwait(false);

            LastScanDebugInfo = debugBag.OrderBy(x => x.DirectoryName, StringComparer.OrdinalIgnoreCase).ToList();

            if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] Finished scanning mods. Total entries found: {Count}", bag.Count);

            _lookupCache = bag
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var best = g.OrderByDescending(x => x.Priority)
                                    .ThenBy(x => x.ModName, StringComparer.OrdinalIgnoreCase)
                                    .ThenBy(x => x.OptionName, StringComparer.OrdinalIgnoreCase)
                                    .First();
                        return (best.ModName, best.OptionName, best.Priority);
                    },
                    StringComparer.OrdinalIgnoreCase
                );

            _gamePathLookupCache = bag
                .GroupBy(x => x.GamePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => 
                    {
                        // Similar logic: Highest priority wins
                        var best = g.OrderByDescending(x => x.Priority)
                                    .ThenBy(x => x.ModName, StringComparer.OrdinalIgnoreCase)
                                    .First();
                        return (best.Path, best.ModName, best.OptionName, best.Priority);
                    },
                    StringComparer.OrdinalIgnoreCase
                );

            _allEnabledModsCache = bag.OrderBy(x => x.ModName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Priority).ToList();

            _cacheDirty = false;
            return _lookupCache;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during mod scan.");
            return [];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Dictionary<string, (string ResolvedPath, string ModName, string OptionName, int Priority)>> GetGamePathLookupAsync(CancellationToken ct)
    {
        if (!_cacheDirty && _gamePathLookupCache.Count > 0)
        {
            return new Dictionary<string, (string ResolvedPath, string ModName, string OptionName, int Priority)>(_gamePathLookupCache, StringComparer.OrdinalIgnoreCase);
        }

        // Trigger build by calling GetModFileLookupAsync (it builds both)
        await GetModFileLookupAsync(ct).ConfigureAwait(false);

        return new Dictionary<string, (string ResolvedPath, string ModName, string OptionName, int Priority)>(_gamePathLookupCache, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGetCachedGamePathEntry(string gamePath, out (string ResolvedPath, string ModName, string OptionName, int Priority) entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(gamePath)) return false;
        if (_cacheDirty || _gamePathLookupCache.Count == 0) return false;
        return _gamePathLookupCache.TryGetValue(gamePath, out entry);
    }

    private async Task<(int AddedCount, int TotalCount, string ResolvedModName, bool HasCharacterLegacyShpk, List<string> ChangedItems, List<string> ActiveOptions)> ProcessModFiles(string modDirectoryName, string modPath, Dictionary<string, List<string>> settings, int priority, ConcurrentBag<(string GamePath, string Path, string ModName, string OptionName, int Priority)> bag, string resolvedModName, bool isDebugTarget, CancellationToken ct)
    {
        int addedCount = 0;
        int totalCount = 0;
        bool hasCharacterLegacyShpk = false;
        bool containsUi = false;
        var localBag = new List<(string GamePath, string Path, string ModName, string OptionName, int Priority)>();
        var changedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usePenumbraChangedItems = false;

        // Create case-insensitive settings lookup
        var settingsCaseInsensitive = new Dictionary<string, List<string>>(settings, StringComparer.OrdinalIgnoreCase);

        if (isDebugTarget) Logger.LogDebug("[FileReplacementNew] Processing target mod: {Path} ({Name})", modPath, resolvedModName);

        var jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNameCaseInsensitive = true, 
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        // Use resolved name from API if available, otherwise default to directory name
        string modName = resolvedModName;
        
        // If we don't have a good name (it's just directory name), try meta.json
        // We assume that if resolvedModName is different from directory name, it's a good name (from API)
        bool hasGoodName = !string.Equals(modName, Path.GetFileName(modPath), StringComparison.OrdinalIgnoreCase);

        var metaPath = Path.Combine(modPath, "meta.json");
        PenumbraModMeta? meta = null;

        if (File.Exists(metaPath))
        {
            try
            {
                using var stream = File.OpenRead(metaPath);
                meta = await JsonSerializer.DeserializeAsync<PenumbraModMeta>(stream, jsonOptions, cancellationToken: ct).ConfigureAwait(false);
                if (!hasGoodName && !string.IsNullOrEmpty(meta?.Name))
                {
                    modName = meta.Name;
                }
            }
            catch (Exception ex)
            {
                Logger.LogTrace(ex, "Failed to read meta.json for {Path}", modPath);
            }
        }
        else
        {
             if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] meta.json not found for {path}", modPath);
        }

        var penumbraChangedItems = _ipcManager.Penumbra.GetChangedItems(modDirectoryName, modName);
        if (penumbraChangedItems.Count > 0)
        {
            foreach (var item in penumbraChangedItems.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                changedItems.Add(item);
            }
            usePenumbraChangedItems = true;
        }

        // 1. Process Default Files
        var defaultModPath = Path.Combine(modPath, "default_mod.json");
        if (File.Exists(defaultModPath))
        {
            try
            {
                using var stream = File.OpenRead(defaultModPath);
                var defaultMod = await JsonSerializer.DeserializeAsync<PenumbraModDefault>(stream, jsonOptions, cancellationToken: ct).ConfigureAwait(false);
                
                if (defaultMod?.Files != null)
                {
                    if (defaultMod.Files.Keys.Any(k => k.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase)))
                        hasCharacterLegacyShpk = true;

                    totalCount += defaultMod.Files.Count;
                    foreach (var kvp in defaultMod.Files)
                    {
                        var gamePath = NormalizeGamePath(kvp.Key);
                        if (string.IsNullOrEmpty(gamePath))
                        {
                            continue;
                        }
                        if (!hasCharacterLegacyShpk && gamePath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                        {
                            hasCharacterLegacyShpk = true;
                        }
                        if (!usePenumbraChangedItems)
                        {
                            AddChangedItemsFromGamePath(gamePath, changedItems);
                        }
                        if (gamePath.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) || gamePath.StartsWith("common/font", StringComparison.OrdinalIgnoreCase))
                        {
                            containsUi = true;
                        }

                        var localPath = Path.GetFullPath(Path.Combine(modPath, kvp.Value));
                        localBag.Add((gamePath, localPath, modName, "Default", priority));
                        addedCount++;
                        activeOptions.Add("Default");
                        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] Added default file: {path} -> {mod}", localPath, modName);
                    }
                }

                if (defaultMod?.FileSwaps != null)
                {
                    if (!hasCharacterLegacyShpk && defaultMod.FileSwaps.Keys.Any(k => k.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase)))
                         hasCharacterLegacyShpk = true;

                    totalCount += defaultMod.FileSwaps.Count;
                    foreach (var kvp in defaultMod.FileSwaps)
                    {
                        var gamePath = NormalizeGamePath(kvp.Key);
                        if (string.IsNullOrEmpty(gamePath))
                        {
                            continue;
                        }
                        if (!hasCharacterLegacyShpk && gamePath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                        {
                            hasCharacterLegacyShpk = true;
                        }
                        if (!usePenumbraChangedItems)
                        {
                            AddChangedItemsFromGamePath(gamePath, changedItems);
                        }
                        if (gamePath.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) || gamePath.StartsWith("common/font", StringComparison.OrdinalIgnoreCase))
                        {
                            containsUi = true;
                        }

                        var localPath = Path.GetFullPath(Path.Combine(modPath, kvp.Value));
                        // Only add if it's a local file
                        if (File.Exists(localPath))
                        {
                            localBag.Add((gamePath, localPath, modName, "Default", priority));
                            addedCount++;
                            activeOptions.Add("Default");
                            if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] Added default swap: {path} -> {mod}", localPath, modName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogTrace(ex, "Failed to read default_mod.json for {Path}", modPath);
            }
        }
        else
        {
            if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] default_mod.json not found for {path}", modPath);
        }

        // 2. Process Group Options
        // Scan for group_*.json files
        try
        {
            var groupFiles = Directory.GetFiles(modPath, "group_*.json");
            if (groupFiles.Length == 0 && Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] No group files found for {path}", modPath);
            foreach (var groupFile in groupFiles)
            {
                try
                {
                    using var stream = File.OpenRead(groupFile);
                    var group = await JsonSerializer.DeserializeAsync<PenumbraModGroup>(stream, jsonOptions, cancellationToken: ct).ConfigureAwait(false);

                    if (group != null)
                    {
                        if (isDebugTarget) Logger.LogDebug("[FileReplacementNew] {Mod}: Found group {Group}, Type: {Type}", modName, group.Name, group.Type);

                        if (settingsCaseInsensitive.TryGetValue(group.Name, out var enabledOptions))
                        {
                            if (isDebugTarget) Logger.LogDebug("[FileReplacementNew] {Mod}: Group {Group} enabled options: {Options}", modName, group.Name, string.Join(", ", enabledOptions));
                            
                            if (string.Equals(group.Type, "Combining", StringComparison.OrdinalIgnoreCase))
                            {
                                // Logic for CombiningModGroup
                                int setting = 0;
                                var activeOptionNames = new List<string>();

                                var optionsCount = group.Options?.Count ?? 0;
                                for (int i = 0; i < optionsCount; i++)
                                {
                                    var option = group.Options![i];
                                    if (enabledOptions.Any(o => string.Equals(o, option.Name, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        setting |= (1 << i);
                                        activeOptionNames.Add(option.Name);
                                    }
                                }

                                if (isDebugTarget) Logger.LogDebug("[FileReplacementNew] {Mod}: Combining group {Group} setting mask: {Mask}, Container Count: {Count}", modName, group.Name, setting, group.Containers?.Count ?? 0);

                                if (group.Containers != null && setting < group.Containers.Count)
                                {
                                    var container = group.Containers[setting];
                                    var combinedOptionName = $"{group.Name}: {string.Join(", ", activeOptionNames)}";
                                    
                                    if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Mod {mod}: Processing COMBINING group {group}, setting mask {mask}, options: {opts}", modName, group.Name, setting, string.Join(", ", activeOptionNames));

                                    if (container.Files != null)
                                    {
                                        foreach (var kvp in container.Files)
                                        {
                                            var gamePath = NormalizeGamePath(kvp.Key);
                                            if (string.IsNullOrEmpty(gamePath))
                                            {
                                                continue;
                                            }
                                            totalCount++;
                                            if (!hasCharacterLegacyShpk && gamePath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                                            {
                                                hasCharacterLegacyShpk = true;
                                            }
                                            if (!usePenumbraChangedItems)
                                            {
                                                AddChangedItemsFromGamePath(gamePath, changedItems);
                                            }
                                            if (gamePath.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) || gamePath.StartsWith("common/font", StringComparison.OrdinalIgnoreCase))
                                            {
                                                containsUi = true;
                                            }

                                            var localPath = Path.GetFullPath(Path.Combine(modPath, kvp.Value));
                                            localBag.Add((gamePath, localPath, modName, combinedOptionName, priority));
                                            addedCount++;
                                            activeOptions.Add(combinedOptionName);
                                            if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Added combining group file: {path} -> {mod} ({opt})", localPath, modName, combinedOptionName);
                                        }
                                    }

                                    if (container.FileSwaps != null)
                                    {
                                        foreach (var kvp in container.FileSwaps)
                                        {
                                            var gamePath = NormalizeGamePath(kvp.Key);
                                            if (string.IsNullOrEmpty(gamePath))
                                            {
                                                continue;
                                            }
                                            totalCount++;
                                            if (!hasCharacterLegacyShpk && gamePath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                                            {
                                                hasCharacterLegacyShpk = true;
                                            }
                                            if (!usePenumbraChangedItems)
                                            {
                                                AddChangedItemsFromGamePath(gamePath, changedItems);
                                            }
                                            if (gamePath.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) || gamePath.StartsWith("common/font", StringComparison.OrdinalIgnoreCase))
                                            {
                                                containsUi = true;
                                            }

                                            var localPath = Path.GetFullPath(Path.Combine(modPath, kvp.Value));
                                            if (File.Exists(localPath))
                                            {
                                                localBag.Add((gamePath, localPath, modName, combinedOptionName, priority));
                                                addedCount++;
                                                activeOptions.Add(combinedOptionName);
                                                if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Added combining group swap: {path} -> {mod} ({opt})", localPath, modName, combinedOptionName);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                     if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogWarning("[FileReplacementNew] Mod {mod}: Combining group {group} has invalid setting {setting} or no containers (Count: {count})", modName, group.Name, setting, group.Containers?.Count ?? 0);
                                }
                            }
                            else if (string.Equals(group.Type, "Complex", StringComparison.OrdinalIgnoreCase))
                            {
                                // Logic for ComplexModGroup
                                ulong setting = 0;
                                var activeOptionNames = new List<string>();

                                var optionsCount = group.Options?.Count ?? 0;
                                for (int i = 0; i < optionsCount; i++)
                                {
                                    var option = group.Options![i];
                                    if (enabledOptions.Any(o => string.Equals(o, option.Name, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        setting |= (1ul << i);
                                        activeOptionNames.Add(option.Name);
                                    }
                                }

                                var combinedOptionName = $"{group.Name}: {string.Join(", ", activeOptionNames)}";
                                if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Mod {mod}: Processing COMPLEX group {group}, setting mask {mask}, options: {opts}", modName, group.Name, setting, string.Join(", ", activeOptionNames));

                                if (group.Containers != null)
                                {
                                    foreach (var container in group.Containers)
                                    {
                                        // Check if container is enabled: (setting & mask) == value
                                        if ((setting & container.AssociationMask) == container.AssociationValue)
                                        {
                                            if (isDebugTarget) Logger.LogDebug("[FileReplacementNew] {Mod}: Complex group {Group} container matched (Mask: {Mask}, Value: {Value})", modName, group.Name, container.AssociationMask, container.AssociationValue);

                                            if (container.Files != null)
                                            {
                                                foreach (var kvp in container.Files)
                                                {
                                                    var gamePath = kvp.Key;
                                                    totalCount++;
                                                    if (!hasCharacterLegacyShpk && gamePath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        hasCharacterLegacyShpk = true;
                                                    }
                                                    if (!usePenumbraChangedItems)
                                                    {
                                                        AddChangedItemsFromGamePath(gamePath, changedItems);
                                                    }
                                                    if (gamePath.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) || gamePath.StartsWith("common/font", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        containsUi = true;
                                                    }

                                                    var localPath = Path.GetFullPath(Path.Combine(modPath, kvp.Value));
                                                    localBag.Add((gamePath, localPath, modName, combinedOptionName, priority));
                                                    addedCount++;
                                                    activeOptions.Add(combinedOptionName);
                                                    if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Added complex group file: {path} -> {mod} ({opt})", localPath, modName, combinedOptionName);
                                                }
                                            }

                                            if (container.FileSwaps != null)
                                            {
                                                foreach (var kvp in container.FileSwaps)
                                                {
                                                    var gamePath = kvp.Key;
                                                    totalCount++;
                                                    if (!hasCharacterLegacyShpk && gamePath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        hasCharacterLegacyShpk = true;
                                                    }
                                                    if (!usePenumbraChangedItems)
                                                    {
                                                        AddChangedItemsFromGamePath(gamePath, changedItems);
                                                    }
                                                    if (gamePath.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) || gamePath.StartsWith("common/font", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        containsUi = true;
                                                    }

                                                    var localPath = Path.GetFullPath(Path.Combine(modPath, kvp.Value));
                                                    if (File.Exists(localPath))
                                                    {
                                                        localBag.Add((gamePath, localPath, modName, combinedOptionName, priority));
                                                        addedCount++;
                                                        activeOptions.Add(combinedOptionName);
                                                        if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Added complex group swap: {path} -> {mod} ({opt})", localPath, modName, combinedOptionName);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Logic for Single/Multi groups
                                if (group.Options != null)
                                {
                                    foreach (var option in group.Options)
                                    {
                                        if (enabledOptions.Any(o => string.Equals(o, option.Name, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Mod {mod}: Processing group {group}, option {option}", modName, group.Name, option.Name);

                                            var optionName = $"{group.Name}: {option.Name}";
                                            if (option.Files != null)
                                            {
                                                foreach (var kvp in option.Files)
                                                {
                                                    var gamePath = NormalizeGamePath(kvp.Key);
                                                    if (string.IsNullOrEmpty(gamePath))
                                                    {
                                                        continue;
                                                    }
                                                    totalCount++;
                                                    if (!hasCharacterLegacyShpk && gamePath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        hasCharacterLegacyShpk = true;
                                                    }
                                                    if (!usePenumbraChangedItems)
                                                    {
                                                        AddChangedItemsFromGamePath(gamePath, changedItems);
                                                    }
                                                    if (gamePath.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) || gamePath.StartsWith("common/font", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        containsUi = true;
                                                    }

                                                    var localPath = Path.GetFullPath(Path.Combine(modPath, kvp.Value));
                                                    localBag.Add((gamePath, localPath, modName, optionName, priority));
                                                    addedCount++;
                                                    activeOptions.Add(optionName);
                                                    if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Added group option file: {path} -> {mod} ({opt})", localPath, modName, optionName);
                                                }
                                            }

                                            if (option.FileSwaps != null)
                                            {
                                                foreach (var kvp in option.FileSwaps)
                                                {
                                                    var gamePath = NormalizeGamePath(kvp.Key);
                                                    if (string.IsNullOrEmpty(gamePath))
                                                    {
                                                        continue;
                                                    }
                                                    totalCount++;
                                                    if (!hasCharacterLegacyShpk && gamePath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        hasCharacterLegacyShpk = true;
                                                    }
                                                    if (!usePenumbraChangedItems)
                                                    {
                                                        AddChangedItemsFromGamePath(gamePath, changedItems);
                                                    }
                                                    if (gamePath.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) || gamePath.StartsWith("common/font", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        containsUi = true;
                                                    }

                                                    var localPath = Path.GetFullPath(Path.Combine(modPath, kvp.Value));
                                                    if (File.Exists(localPath))
                                                    {
                                                    localBag.Add((gamePath, localPath, modName, optionName, priority));
                                                        addedCount++;
                                                    activeOptions.Add(optionName);
                                                    if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Added group swap: {path} -> {mod} ({opt})", localPath, modName, optionName);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (isDebugTarget) 
                            {
                                Logger.LogDebug("[FileReplacementNew] {Mod}: Group {Group} is not enabled or not found in settings. Available groups in settings: {SettingsGroups}", modName, group.Name, string.Join(", ", settingsCaseInsensitive.Keys));
                            }
                        }

                        if (isDebugTarget && settingsCaseInsensitive.TryGetValue(group.Name, out var debugEnabledOptions))
                        {
                            var unusedEnabledOptions = debugEnabledOptions.Where(eo => !(group.Options?.Any(o => string.Equals(o.Name, eo, StringComparison.OrdinalIgnoreCase)) ?? false)).ToList();
                            if (unusedEnabledOptions.Count > 0)
                            {
                                Logger.LogDebug("[FileReplacementNew] {Mod}: Group {Group} has enabled options that were not found in JSON options: {Unused}. Available JSON options: {JsonOptions}", modName, group.Name, string.Join(", ", unusedEnabledOptions), string.Join(", ", group.Options?.Select(o => o.Name) ?? []));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to read group file {Path}", groupFile);
                }
            }
        }
        catch (Exception ex)
        {
             Logger.LogWarning(ex, "Failed to scan group files for {Path}", modPath);
        }

        if (!containsUi)
        {
            foreach (var item in localBag)
            {
                bag.Add(item);
            }
        }
        else
        {
            if (isDebugTarget || Logger.IsEnabled(LogLevel.Debug)) Logger.LogDebug("[FileReplacementNew] Skipping UI/Icon mod: {Mod}", modName);
        }

        var items = changedItems.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var optionList = activeOptions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return (addedCount, totalCount, modName, hasCharacterLegacyShpk, items, optionList);
    }

    internal bool TryGetChangedItemsFromGamePath(string gamePath, out IReadOnlyList<string> changedItems)
    {
        changedItems = Array.Empty<string>();
        if (string.IsNullOrEmpty(gamePath)) return false;

        var items = new List<string>();
        if (TryGetEmoteNamesFromGamePath(gamePath, out var emoteNames) && emoteNames.Count > 0)
        {
            for (int i = 0; i < emoteNames.Count; i++)
            {
                items.Add($"Emote: {emoteNames[i]}");
            }
        }
        else
        {
            items.Add(gamePath.Replace('\\', '/'));
        }

        if (items.Count == 0) return false;
        changedItems = items;
        return true;
    }

    internal EquipmentPathDecision IsEquipmentGamePath(string gamePath, IReadOnlyDictionary<string, int>? raceCodeCounts = null)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return EquipmentPathDecision.NonEquipment;
        if (!gamePath.Contains("chara/", StringComparison.OrdinalIgnoreCase)) return EquipmentPathDecision.NonEquipment;
        if (gamePath.Contains("chara/demihuman/", StringComparison.OrdinalIgnoreCase)) return EquipmentPathDecision.NonEquipment;
        if (gamePath.Contains("chara/monster/", StringComparison.OrdinalIgnoreCase)) return EquipmentPathDecision.NonEquipment;
        if (gamePath.Contains("chara/companion/", StringComparison.OrdinalIgnoreCase)) return EquipmentPathDecision.NonEquipment;
        if (gamePath.Contains("emote/", StringComparison.OrdinalIgnoreCase)) return EquipmentPathDecision.NonEquipment;
        if (gamePath.Contains("animation/", StringComparison.OrdinalIgnoreCase)) return EquipmentPathDecision.NonEquipment;
        if (gamePath.Contains(".avfx", StringComparison.OrdinalIgnoreCase)) return EquipmentPathDecision.NonEquipment;

        if (string.IsNullOrWhiteSpace(_playerRaceCode)) return EquipmentPathDecision.Include;
        if (!TryGetRaceCodeMatchState(gamePath, _playerRaceCode, out var containsAny, out var containsPlayer)) return EquipmentPathDecision.Include;
        if (!containsAny) return EquipmentPathDecision.Include;
        if (containsPlayer) return EquipmentPathDecision.Include;

        if (raceCodeCounts != null)
        {
            if (raceCodeCounts.ContainsKey(_playerRaceCode)) return EquipmentPathDecision.Exclude;
            if (raceCodeCounts.Count == 1) return EquipmentPathDecision.Include;
            return EquipmentPathDecision.Exclude;
        }

        return EquipmentPathDecision.Include;
    }

    private void UpdatePlayerRaceCode(byte gender, byte tribeId)
    {
        _playerRaceCode = GetRaceCodeFromGenderAndTribe(gender, tribeId);
    }

    internal static IReadOnlyDictionary<string, int> BuildRaceCodeCounts(IEnumerable<string> gamePaths)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gamePath in gamePaths)
        {
            AddRaceCodesFromPath(gamePath, codes);
        }

        foreach (var code in codes)
        {
            counts[code] = 1;
        }

        return counts;
    }

    internal static bool PathContainsRaceCode(string path, string raceCode)
    {
        return TryGetRaceCodeMatchState(path, raceCode, out _, out var containsRaceCode) && containsRaceCode;
    }

    internal static bool PathContainsAnyRaceCode(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        int index = 0;
        while (index <= normalized.Length - 5)
        {
            var current = normalized[index];
            if (current != 'c' && current != 'C')
            {
                index++;
                continue;
            }

            if (!char.IsDigit(normalized[index + 1])
                || !char.IsDigit(normalized[index + 2])
                || !char.IsDigit(normalized[index + 3])
                || !char.IsDigit(normalized[index + 4]))
            {
                index++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static string NormalizeGamePath(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return string.Empty;
        }

        var normalized = gamePath.Replace('\\', '/').Trim();
        int bestIndex = -1;
        for (int i = 0; i < GamePathRoots.Length; i++)
        {
            var index = normalized.IndexOf(GamePathRoots[i], StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            if (bestIndex < 0 || index < bestIndex)
            {
                bestIndex = index;
            }
        }

        if (bestIndex > 0)
        {
            normalized = normalized.Substring(bestIndex);
        }

        return normalized;
    }

    private static bool TryGetRaceCodeMatchState(string path, string playerRaceCode, out bool containsAny, out bool containsPlayer)
    {
        containsAny = false;
        containsPlayer = false;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalized = path.Replace('\\', '/');
        int index = 0;
        while (index <= normalized.Length - 5)
        {
            var current = normalized[index];
            if (current != 'c' && current != 'C')
            {
                index++;
                continue;
            }

            if (!char.IsDigit(normalized[index + 1])
                || !char.IsDigit(normalized[index + 2])
                || !char.IsDigit(normalized[index + 3])
                || !char.IsDigit(normalized[index + 4]))
            {
                index++;
                continue;
            }

            containsAny = true;
            if (!containsPlayer)
            {
                var code = $"c{normalized.Substring(index + 1, 4)}";
                containsPlayer = string.Equals(code, playerRaceCode, StringComparison.OrdinalIgnoreCase);
            }
            index += 5;
        }

        return true;
    }

    internal static void AddRaceCodesFromPath(string path, HashSet<string> codes)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var normalized = path.Replace('\\', '/');
        int index = 0;
        while (index <= normalized.Length - 5)
        {
            var current = normalized[index];
            if (current != 'c' && current != 'C')
            {
                index++;
                continue;
            }

            if (!char.IsDigit(normalized[index + 1])
                || !char.IsDigit(normalized[index + 2])
                || !char.IsDigit(normalized[index + 3])
                || !char.IsDigit(normalized[index + 4]))
            {
                index++;
                continue;
            }

            codes.Add($"c{normalized.Substring(index + 1, 4)}");
            index += 5;
        }
    }

    private static string? GetRaceCodeFromGenderAndTribe(byte gender, byte tribeId)
    {
        var modelRace = GetModelRaceFromTribe(tribeId);
        if (modelRace == ModelRaceCode.Unknown) return null;

        return modelRace switch
        {
            ModelRaceCode.Midlander => GetGenderedRaceCode(gender, "0101", "0201", "0104", "0204"),
            ModelRaceCode.Highlander => GetGenderedRaceCode(gender, "0301", "0401", "0304", "0404"),
            ModelRaceCode.Elezen => GetGenderedRaceCode(gender, "0501", "0601", "0504", "0604"),
            ModelRaceCode.Lalafell => GetGenderedRaceCode(gender, "1101", "1201", "1104", "1204"),
            ModelRaceCode.Miqote => GetGenderedRaceCode(gender, "0701", "0801", "0704", "0804"),
            ModelRaceCode.Roegadyn => GetGenderedRaceCode(gender, "0901", "1001", "0904", "1004"),
            ModelRaceCode.AuRa => GetGenderedRaceCode(gender, "1301", "1401", "1304", "1404"),
            ModelRaceCode.Hrothgar => GetGenderedRaceCode(gender, "1501", "1601", "1504", "1604"),
            ModelRaceCode.Viera => GetGenderedRaceCode(gender, "1701", "1801", "1704", "1804"),
            _ => null,
        };
    }

    private static string? GetGenderedRaceCode(byte gender, string male, string female, string maleNpc, string femaleNpc)
    {
        return gender switch
        {
            0 => $"c{male}",
            1 => $"c{female}",
            2 => $"c{maleNpc}",
            3 => $"c{femaleNpc}",
            _ => null,
        };
    }

    private static ModelRaceCode GetModelRaceFromTribe(byte tribeId)
    {
        return tribeId switch
        {
            1 => ModelRaceCode.Midlander,
            2 => ModelRaceCode.Highlander,
            3 or 4 => ModelRaceCode.Elezen,
            5 or 6 => ModelRaceCode.Lalafell,
            7 or 8 => ModelRaceCode.Miqote,
            9 or 10 => ModelRaceCode.Roegadyn,
            11 or 12 => ModelRaceCode.AuRa,
            13 or 14 => ModelRaceCode.Hrothgar,
            15 or 16 => ModelRaceCode.Viera,
            _ => ModelRaceCode.Unknown,
        };
    }

    private enum ModelRaceCode
    {
        Unknown = 0,
        Midlander = 1,
        Highlander = 2,
        Elezen = 3,
        Lalafell = 4,
        Miqote = 5,
        Roegadyn = 6,
        AuRa = 7,
        Hrothgar = 8,
        Viera = 9,
    }

    internal enum EquipmentPathDecision
    {
        NonEquipment = 0,
        Include = 1,
        Exclude = 2,
    }

    private void AddChangedItemsFromGamePath(string gamePath, HashSet<string> changedItems)
    {
        if (!TryGetChangedItemsFromGamePath(gamePath, out var items)) return;
        for (int i = 0; i < items.Count; i++)
        {
            changedItems.Add(items[i]);
        }
    }

    internal bool TryGetEmoteNameFromGamePath(string gamePath, out string emoteName)
    {
        emoteName = string.Empty;
        if (!TryGetEmoteNamesFromGamePath(gamePath, out var emoteNames) || emoteNames.Count == 0)
        {
            return false;
        }
        emoteName = emoteNames[0];
        return true;
    }

    private bool TryGetEmoteNamesFromGamePath(string gamePath, out IReadOnlyList<string> emoteNames)
    {
        emoteNames = Array.Empty<string>();
        if (string.IsNullOrEmpty(gamePath)) return false;
        if (!IsEmotePath(gamePath)) return false;
        var fileName = Path.GetFileName(gamePath);
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        EnsureEmoteLookup();
        if (_emoteFileNameCache.TryGetValue(fileName, out var mappedNames) && mappedNames.Count > 0)
        {
            emoteNames = mappedNames;
            return true;
        }

        if (!TryExtractTimelineId(gamePath, out var timelineId)) return false;
        if (TryGetActionTimelineName(timelineId, out var name))
        {
            emoteNames = new[] { name };
            return true;
        }
        emoteNames = new[] { $"Unknown ({timelineId})" };
        return true;
    }

    private void EnsureEmoteLookup()
    {
        if (_emoteLookupBuilt) return;
        lock (_emoteCacheLock)
        {
            if (_emoteLookupBuilt) return;
            _emoteFileNameCache.Clear();
            var sheet = _gameData.GetExcelSheet<Emote>();
            if (sheet != null)
            {
                foreach (var emote in sheet)
                {
                    var emoteName = emote.Name.ToString();
                    if (string.IsNullOrWhiteSpace(emoteName)) continue;
                    foreach (var timeline in emote.ActionTimeline)
                    {
                        if (timeline.RowId == 0 || !timeline.ValueNullable.HasValue) continue;
                        var timelineKey = ExtractTimelineKey(timeline.Value);
                        if (string.IsNullOrWhiteSpace(timelineKey)) continue;
                        var baseName = Path.GetFileName(timelineKey);
                        if (string.IsNullOrWhiteSpace(baseName)) continue;
                        AddEmoteMapping($"{baseName}.pap", emoteName);
                        AddEmoteMapping($"{baseName}.tmb", emoteName);
                    }
                }

                AddSpecialEmoteMapping(sheet, AddEmoteMapping);
            }
            _emoteLookupBuilt = true;
        }
    }

    private static void AddSpecialEmoteMapping(ExcelSheet<Emote> sheet, Action<string, string> addEmoteMapping)
    {
        var sit = sheet.GetRow(50);
        if (sit.RowId != 0)
        {
            var name = sit.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name)) addEmoteMapping("s_pose01_loop.pap", name);
        }
        var sitOnGround = sheet.GetRow(52);
        if (sitOnGround.RowId != 0)
        {
            var name = sitOnGround.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name)) addEmoteMapping("j_pose01_loop.pap", name);
        }
        var doze = sheet.GetRow(13);
        if (doze.RowId != 0)
        {
            var name = doze.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name)) addEmoteMapping("l_pose01_loop.pap", name);
        }
    }

    private void AddEmoteMapping(string fileName, string emoteName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(emoteName)) return;
        if (!_emoteFileNameCache.TryGetValue(fileName, out var list))
        {
            list = new List<string>();
            _emoteFileNameCache[fileName] = list;
        }
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], emoteName, StringComparison.OrdinalIgnoreCase)) return;
        }
        list.Add(emoteName);
        list.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private bool TryGetActionTimelineName(uint timelineId, out string name)
    {
        name = string.Empty;
        lock (_emoteCacheLock)
        {
            if (_actionTimelineCache.TryGetValue(timelineId, out var cachedName) && !string.IsNullOrWhiteSpace(cachedName))
            {
                name = cachedName;
                return true;
            }
        }

        var sheet = _gameData.GetExcelSheet<ActionTimeline>();
        var row = sheet?.GetRow(timelineId);
        if (row == null)
        {
            name = string.Empty;
            return false;
        }
        var rowName = ExtractTimelineName(row.Value);
        if (string.IsNullOrWhiteSpace(rowName))
        {
            name = string.Empty;
            return false;
        }

        lock (_emoteCacheLock)
        {
            _actionTimelineCache[timelineId] = rowName;
        }

        name = rowName;
        return true;
    }

    private static string? ExtractTimelineName(ActionTimeline row)
    {
        var value = ActionTimelineNameProperty?.GetValue(row) ?? ActionTimelineKeyProperty?.GetValue(row);
        return value?.ToString();
    }

    private static string? ExtractTimelineKey(ActionTimeline row)
    {
        var value = ActionTimelineKeyProperty?.GetValue(row);
        return value?.ToString();
    }

    private static bool IsEmotePath(string gamePath)
    {
        if (!gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)
            && !gamePath.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = gamePath.Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("/emt/")
            || normalized.Contains("/emote/")
            || normalized.Contains("emote_");
    }

    private static bool TryExtractTimelineId(string gamePath, out uint timelineId)
    {
        timelineId = 0;
        var fileName = Path.GetFileNameWithoutExtension(gamePath);
        if (string.IsNullOrEmpty(fileName)) return false;

        int end = fileName.Length - 1;
        while (end >= 0 && !char.IsDigit(fileName[end]))
        {
            end--;
        }
        if (end < 0) return false;

        int start = end;
        while (start >= 0 && char.IsDigit(fileName[start]))
        {
            start--;
        }
        start++;

        var span = fileName.AsSpan(start, end - start + 1);
        return uint.TryParse(span, out timelineId);
    }

    private sealed class PenumbraModDefault
    {
        public Dictionary<string, string> Files { get; set; } = [];
        public Dictionary<string, string> FileSwaps { get; set; } = [];
    }

    private sealed class PenumbraModMeta
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class PenumbraModGroup
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "Single";
        public List<PenumbraModOption> Options { get; set; } = [];
        public List<PenumbraModContainer> Containers { get; set; } = [];
    }

    private sealed class PenumbraModOption
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, string> Files { get; set; } = [];
        public Dictionary<string, string> FileSwaps { get; set; } = [];
    }

    private sealed class PenumbraModContainer
    {
        public Dictionary<string, string> Files { get; set; } = [];
        public Dictionary<string, string> FileSwaps { get; set; } = [];
        public ulong AssociationMask { get; set; } = 0;
        public ulong AssociationValue { get; set; } = 0;
    }
}
