using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.IO;
using Sphene.Interop.Ipc;
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
        int ActiveFilesCount,
        int TotalFilesCount,
        string Status,
        bool HasCharacterLegacyShpk
    );

    private Dictionary<string, (string ModName, string OptionName, int Priority)> _lookupCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheDirty = true;
    private readonly SemaphoreSlim _lock = new(1, 1);
    
    // Tracks mods that are currently "in use" by the local player (i.e., at least one file from them is resolved).
    private readonly ConcurrentDictionary<string, byte> _activePlayerMods = new(StringComparer.OrdinalIgnoreCase);

    public List<ModDebugInfo> LastScanDebugInfo { get; private set; } = [];

    public PenumbraModScanner(ILogger<PenumbraModScanner> logger, IpcManager ipcManager, SpheneMediator mediator)
        : base(logger, mediator)
    {
        _ipcManager = ipcManager;
        Mediator.Subscribe<PenumbraModSettingChangedMessage>(this, _ => _cacheDirty = true);
        Mediator.Subscribe<PenumbraInitializedMessage>(this, _ => _cacheDirty = true);
    }

    public void MarkDirty()
    {
        _cacheDirty = true;
        Logger.LogDebug("Penumbra mod cache marked as dirty by user request.");
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

            var bag = new ConcurrentBag<(string Path, string ModName, string OptionName, int Priority)>();
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

                try
                {
                    var result = await ProcessModFiles(fullModPath, modSettings, modPriority, bag, resolvedModName, isDebugTarget, token).ConfigureAwait(false);
                    added = result.AddedCount;
                    total = result.TotalCount;
                    hasCharacterLegacyShpk = result.HasCharacterLegacyShpk;
                    
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
                    modSettings.SelectMany(x => x.Value.Select(v => $"{x.Key}: {v}")).ToList(),
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
                        // Group by ModName to handle multiple options from same mod
                        var modGroups = g.GroupBy(x => x.ModName, StringComparer.Ordinal);
                        
                        // Pick the mod with highest priority, tie-break alphabetically
                        var bestModGroup = modGroups.OrderByDescending(mg => mg.Max(x => x.Priority))
                                                    .ThenBy(mg => mg.Key, StringComparer.OrdinalIgnoreCase)
                                                    .First();
                        
                        var modName = bestModGroup.Key;
                        var priority = bestModGroup.First().Priority;
                        var options = string.Join(", ", bestModGroup.Select(x => x.OptionName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                        
                        return (modName, options, priority);
                    },
                    StringComparer.OrdinalIgnoreCase
                );

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

    private async Task<(int AddedCount, int TotalCount, string ResolvedModName, bool HasCharacterLegacyShpk)> ProcessModFiles(string modPath, Dictionary<string, List<string>> settings, int priority, ConcurrentBag<(string Path, string ModName, string OptionName, int Priority)> bag, string resolvedModName, bool isDebugTarget, CancellationToken ct)
    {
        int addedCount = 0;
        int totalCount = 0;
        bool hasCharacterLegacyShpk = false;
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
                    foreach (var file in defaultMod.Files.Values)
                    {
                        var localPath = Path.GetFullPath(Path.Combine(modPath, file));
                        bag.Add((localPath, modName, "Default", priority));
                        addedCount++;
                        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] Added default file: {path} -> {mod}", localPath, modName);
                    }
                }

                if (defaultMod?.FileSwaps != null)
                {
                    if (!hasCharacterLegacyShpk && defaultMod.FileSwaps.Keys.Any(k => k.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase)))
                         hasCharacterLegacyShpk = true;

                    totalCount += defaultMod.FileSwaps.Count;
                    foreach (var swap in defaultMod.FileSwaps.Values)
                    {
                        var localPath = Path.GetFullPath(Path.Combine(modPath, swap));
                        // Only add if it's a local file
                        if (File.Exists(localPath))
                        {
                            bag.Add((localPath, modName, "Default", priority));
                            addedCount++;
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

                        // Calculate total files in group
                        if (group.Options != null)
                        {
                            foreach (var opt in group.Options)
                            {
                                if (!hasCharacterLegacyShpk && opt.Files != null && opt.Files.Keys.Any(k => k.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase)))
                                     hasCharacterLegacyShpk = true;
                                if (!hasCharacterLegacyShpk && opt.FileSwaps != null && opt.FileSwaps.Keys.Any(k => k.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase)))
                                     hasCharacterLegacyShpk = true;

                                if (opt.Files != null) totalCount += opt.Files.Count;
                                if (opt.FileSwaps != null) totalCount += opt.FileSwaps.Count;
                            }
                        }
                        if (group.Containers != null)
                        {
                             foreach (var cont in group.Containers)
                             {
                                 if (!hasCharacterLegacyShpk && cont.Files != null && cont.Files.Keys.Any(k => k.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase)))
                                     hasCharacterLegacyShpk = true;
                                 if (!hasCharacterLegacyShpk && cont.FileSwaps != null && cont.FileSwaps.Keys.Any(k => k.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase)))
                                     hasCharacterLegacyShpk = true;

                                 if (cont.Files != null) totalCount += cont.Files.Count;
                                 if (cont.FileSwaps != null) totalCount += cont.FileSwaps.Count;
                             }
                        }

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
                                        foreach (var file in container.Files.Values)
                                        {
                                            var localPath = Path.GetFullPath(Path.Combine(modPath, file));
                                            bag.Add((localPath, modName, combinedOptionName, priority));
                                            addedCount++;
                                            if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Added combining group file: {path} -> {mod} ({opt})", localPath, modName, combinedOptionName);
                                        }
                                    }

                                    if (container.FileSwaps != null)
                                    {
                                        foreach (var swap in container.FileSwaps.Values)
                                        {
                                            var localPath = Path.GetFullPath(Path.Combine(modPath, swap));
                                            if (File.Exists(localPath))
                                            {
                                                bag.Add((localPath, modName, combinedOptionName, priority));
                                                addedCount++;
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
                                                foreach (var file in container.Files.Values)
                                                {
                                                    var localPath = Path.GetFullPath(Path.Combine(modPath, file));
                                                    bag.Add((localPath, modName, combinedOptionName, priority));
                                                    addedCount++;
                                                    if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Added complex group file: {path} -> {mod} ({opt})", localPath, modName, combinedOptionName);
                                                }
                                            }

                                            if (container.FileSwaps != null)
                                            {
                                                foreach (var swap in container.FileSwaps.Values)
                                                {
                                                    var localPath = Path.GetFullPath(Path.Combine(modPath, swap));
                                                    if (File.Exists(localPath))
                                                    {
                                                        bag.Add((localPath, modName, combinedOptionName, priority));
                                                        addedCount++;
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
                                            
                                            if (option.Files != null)
                                            {
                                                foreach (var file in option.Files.Values)
                                                {
                                                    var localPath = Path.GetFullPath(Path.Combine(modPath, file));
                                                    bag.Add((localPath, modName, $"{group.Name}: {option.Name}", priority));
                                                    addedCount++;
                                                    if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Added group option file: {path} -> {mod} ({opt})", localPath, modName, $"{group.Name}: {option.Name}");
                                                }
                                            }

                                            if (option.FileSwaps != null)
                                            {
                                                foreach (var swap in option.FileSwaps.Values)
                                                {
                                                    var localPath = Path.GetFullPath(Path.Combine(modPath, swap));
                                                    if (File.Exists(localPath))
                                                    {
                                                        bag.Add((localPath, modName, $"{group.Name}: {option.Name}", priority));
                                                        addedCount++;
                                                        if (Logger.IsEnabled(LogLevel.Trace) || isDebugTarget) Logger.LogTrace("[FileReplacementNew] Added group swap: {path} -> {mod} ({opt})", localPath, modName, $"{group.Name}: {option.Name}");
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

        return (addedCount, totalCount, modName, hasCharacterLegacyShpk);
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
