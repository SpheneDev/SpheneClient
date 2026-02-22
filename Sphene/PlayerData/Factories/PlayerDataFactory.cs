using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Sphene.API.Data.Enum;
using Sphene.FileCache;
using Sphene.Interop.Ipc;
using Sphene.SpheneConfiguration.Models;
using Sphene.PlayerData.Data;
using Sphene.PlayerData.Handlers;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Microsoft.Extensions.Logging;
using CharacterData = Sphene.PlayerData.Data.CharacterData;
using System.Text.RegularExpressions;

namespace Sphene.PlayerData.Factories;

public class PlayerDataFactory
{
    private static readonly Regex _gamePathRegex = new(@"^[a-zA-Z0-9/._-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileCacheManager _fileCacheManager;
    private readonly IpcManager _ipcManager;
    private readonly ILogger<PlayerDataFactory> _logger;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly XivDataAnalyzer _modelAnalyzer;
    private readonly SpheneMediator _spheneMediator;
    private readonly TransientResourceManager _transientResourceManager;
    private readonly PenumbraModScanner _penumbraModScanner;

    public PlayerDataFactory(ILogger<PlayerDataFactory> logger, DalamudUtilService dalamudUtil, IpcManager ipcManager,
        TransientResourceManager transientResourceManager, FileCacheManager fileReplacementFactory,
        PerformanceCollectorService performanceCollector, XivDataAnalyzer modelAnalyzer, SpheneMediator spheneMediator,
        PenumbraModScanner penumbraModScanner)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _ipcManager = ipcManager;
        _transientResourceManager = transientResourceManager;
        _fileCacheManager = fileReplacementFactory;
        _performanceCollector = performanceCollector;
        _modelAnalyzer = modelAnalyzer;
        _spheneMediator = spheneMediator;
        _penumbraModScanner = penumbraModScanner;
        _logger.LogTrace("Creating {this}", nameof(PlayerDataFactory));
    }

    public async Task<CharacterDataFragment?> BuildCharacterData(GameObjectHandler playerRelatedObject, CancellationToken token)
    {
        if (!_ipcManager.Initialized)
        {
            throw new InvalidOperationException("Penumbra or Glamourer is not connected");
        }

        if (playerRelatedObject == null) return null;

        bool pointerIsZero = true;
        try
        {
            pointerIsZero = playerRelatedObject.Address == IntPtr.Zero;
            try
            {
                pointerIsZero = await CheckForNullDrawObject(playerRelatedObject.Address).ConfigureAwait(false);
            }
            catch
            {
                pointerIsZero = true;
                _logger.LogDebug("NullRef for {object}", playerRelatedObject);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create data for {object}", playerRelatedObject);
        }

        if (pointerIsZero)
        {
            _logger.LogTrace("Pointer was zero for {objectKind}", playerRelatedObject.ObjectKind);
            return null;
        }

        try
        {
            return await _performanceCollector.LogPerformance(this, $"CreateCharacterData>{playerRelatedObject.ObjectKind}", async () =>
            {
                return await CreateCharacterData(playerRelatedObject, token).ConfigureAwait(false);
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Cancelled creating Character data for {object}", playerRelatedObject);
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to create {object} data", playerRelatedObject);
        }

        return null;
    }

    private async Task<bool> CheckForNullDrawObject(IntPtr playerPointer)
    {
        return await _dalamudUtil.RunOnFrameworkThread(() => CheckForNullDrawObjectUnsafe(playerPointer)).ConfigureAwait(false);
    }

    private static unsafe bool CheckForNullDrawObjectUnsafe(IntPtr playerPointer)
    {
        return ((Character*)playerPointer)->GameObject.DrawObject == null;
    }

    private async Task<CharacterDataFragment> CreateCharacterData(GameObjectHandler playerRelatedObject, CancellationToken ct)
    {
        var objectKind = playerRelatedObject.ObjectKind;
        CharacterDataFragment fragment = objectKind == ObjectKind.Player ? new CharacterDataFragmentPlayer() : new();

        _logger.LogDebug("Building character data for {obj}", playerRelatedObject);

        // wait until chara is not drawing and present so nothing spontaneously explodes
        await _dalamudUtil.WaitWhileCharacterIsDrawing(_logger, playerRelatedObject, Guid.NewGuid(), 30000, ct: ct).ConfigureAwait(false);
        int totalWaitTime = 10000;
        while (!await _dalamudUtil.IsObjectPresentAsync(await _dalamudUtil.CreateGameObjectAsync(playerRelatedObject.Address).ConfigureAwait(false)).ConfigureAwait(false) && totalWaitTime > 0)
        {
            _logger.LogTrace("Character is null but it shouldn't be, waiting");
            await Task.Delay(50, ct).ConfigureAwait(false);
            totalWaitTime -= 50;
        }

        ct.ThrowIfCancellationRequested();

        Dictionary<string, List<ushort>>? boneIndices =
            objectKind != ObjectKind.Player
            ? null
            : await _dalamudUtil.RunOnFrameworkThread(() => _modelAnalyzer.GetSkeletonBoneIndices(playerRelatedObject)).ConfigureAwait(false);

        DateTime start = DateTime.UtcNow;

        // penumbra call, it's currently broken
        Dictionary<string, HashSet<string>>? resolvedPaths;

        resolvedPaths = (await _ipcManager.Penumbra.GetCharacterData(_logger, playerRelatedObject).ConfigureAwait(false));
        if (resolvedPaths == null) throw new InvalidOperationException("Penumbra returned null data");

        ct.ThrowIfCancellationRequested();

        // Create initial FileReplacements from resolved paths (Mark as Active)
        fragment.FileReplacements =
                new HashSet<FileReplacement>(resolvedPaths.Select(c => new FileReplacement([.. c.Value], c.Key) { IsActive = true }), FileReplacementComparer.Instance)
                .Where(p => p.HasFileReplacement).ToHashSet();
        fragment.FileReplacements.RemoveWhere(c => c.GamePaths.Any(g => !CacheMonitor.AllowedFileExtensions.Any(e => g.EndsWith(e, StringComparison.OrdinalIgnoreCase))));

        ct.ThrowIfCancellationRequested();

        // Populate ModName and OptionName from PenumbraModScanner
            try
            {
                var modLookup = await _penumbraModScanner.GetModFileLookupAsync(ct).ConfigureAwait(false);
                var enabledReplacements = await _penumbraModScanner.GetAllEnabledModReplacementsAsync(ct).ConfigureAwait(false);
                
                _logger.LogDebug("[FileReplacementNew] PenumbraModScanner returned {count} entries", modLookup.Count);

                if (modLookup.Count > 0 && _logger.IsEnabled(LogLevel.Trace))
                {
                     var sampleKeys = string.Join(", ", modLookup.Keys.Take(5));
                     _logger.LogTrace("[FileReplacementNew] Sample keys in lookup: {keys}", sampleKeys);
                }

                var activeModNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Update existing replacements with Mod Info
                foreach (var replacement in fragment.FileReplacements)
                {
                    // Try exact match first (case-insensitive due to dictionary)
                    if (modLookup.TryGetValue(replacement.ResolvedPath, out var modInfo))
                    {
                        replacement.ModName = modInfo.ModName;
                        replacement.OptionName = modInfo.OptionName;
                        activeModNames.Add(modInfo.ModName);
                        _logger.LogTrace("[FileReplacementNew] Found match for {path}: Mod={mod}, Option={opt}", replacement.ResolvedPath, modInfo.ModName, modInfo.OptionName);
                    }
                    else
                    {
                        // Try normalizing separators
                        var normalizedPath = Path.GetFullPath(replacement.ResolvedPath);
                        if (modLookup.TryGetValue(normalizedPath, out modInfo))
                        {
                            replacement.ModName = modInfo.ModName;
                            replacement.OptionName = modInfo.OptionName;
                            activeModNames.Add(modInfo.ModName);
                             _logger.LogTrace("[FileReplacementNew] Found match for normalized {path}: Mod={mod}, Option={opt}", normalizedPath, modInfo.ModName, modInfo.OptionName);
                        }
                        else
                        {
                            // Always log failures for debugging
                             _logger.LogDebug("[FileReplacementNew] Failed to find mod info for path: '{path}' (Normalized: '{norm}')", replacement.ResolvedPath, normalizedPath);
                        }
                    }
                }

                // Add all enabled but not active mods
                if (enabledReplacements.Count > 0)
                {
                    // Create a set of all currently active GamePaths to avoid conflicts
                    // This ensures we don't add an inactive file for a GamePath that is already covered by an active mod
                    var activeGamePaths = fragment.FileReplacements
                        .SelectMany(f => f.GamePaths)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    // Group enabled replacements by ModName to handle strict priority
                    var replacementsByMod = enabledReplacements
                        .GroupBy(r => r.ModName, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new
                        {
                            ModName = g.Key,
                            Entries = g.ToList(),
                            MaxPriority = g.Max(e => e.Priority)
                        })
                        .OrderByDescending(x => x.MaxPriority)
                        .ToList();

                    // Index existing replacements for fast lookup
                    var existingReplacements = fragment.FileReplacements
                        .ToDictionary(r => r.ResolvedPath, StringComparer.OrdinalIgnoreCase);

                    int addedCount = 0;
                    foreach (var modGroup in replacementsByMod)
                    {
                        var modName = modGroup.ModName;
                        var isAlreadyActive = activeModNames.Contains(modName);
                        
                        var modEntries = modGroup.Entries;
                        
                        // Collect all GamePaths this mod attempts to provide
                        var allModGamePaths = modEntries
                            .Select(e => e.GamePath)
                            .Where(p => _gamePathRegex.IsMatch(p))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        if (allModGamePaths.Count == 0) continue;

                        // STRICT PRIORITY CHECK:
                        // If ANY GamePath from this mod is already claimed by a higher priority (or active) mod,
                        // we discard the ENTIRE mod. This prevents partial application of mods (e.g. mixed emotes).
                        // EXCEPTION: If the mod is ALREADY active (partially resolved), we allow adding its remaining files.
                        if (!isAlreadyActive)
                        {
                             bool hasConflict = allModGamePaths.Any(p => activeGamePaths.Contains(p.Replace('\\', '/')));
                             if (hasConflict)
                             {
                                 _logger.LogTrace("[FileReplacementNew] Mod {mod} skipped due to strict priority conflict", modName);
                                 continue;
                             }
                        }

                        // If no conflict (or is active), we check if the mod contains any "Important" files
                        // We also consider any mod containing Minion/Monster/Companion paths as important
                        // This ensures we can correctly identify Minion mods even if their models are not currently loaded (inactive)
                        bool isImportant = allModGamePaths.Any(p =>
                                p.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) ||
                                p.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) ||
                                p.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) ||
                                p.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase) ||
                                p.EndsWith(".atex", StringComparison.OrdinalIgnoreCase) ||
                                p.StartsWith("chara/monster/", StringComparison.OrdinalIgnoreCase) ||
                                p.StartsWith("chara/companion/", StringComparison.OrdinalIgnoreCase) ||
                                p.StartsWith("chara/demihuman/", StringComparison.OrdinalIgnoreCase));

                        if (!isImportant)
                        {
                            continue;
                        }

                        // The mod is valid, has no conflicts, and is important. Add its files.
                        
                        // We need to group entries by ResolvedPath to create FileReplacements
                        var entriesByFile = modEntries
                            .GroupBy(e => e.ResolvedPath, StringComparer.OrdinalIgnoreCase);

                        foreach (var fileGroup in entriesByFile)
                        {
                            var resolvedPath = fileGroup.Key;
                            var fileGamePaths = fileGroup.Select(e => e.GamePath).Where(p => _gamePathRegex.IsMatch(p)).ToArray();

                            if (fileGamePaths.Length == 0) continue;

                            // Skip UI/Icon related
                            if (fileGamePaths.Any(p => p.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("common/font", StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            // Check extension allowed (redundant if we trust scanner/importance check, but safe)
                            if (!fileGamePaths.Any(p => CacheMonitor.AllowedFileExtensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))))
                            {
                                continue;
                            }

                            // Get OptionName (just take first, or join them?)
                            // Usually a FileReplacement represents one file. If multiple options point to same file, we can just pick one option name or join them.
                            // Current FileReplacement structure has single OptionName.
                            // We can use the one from the highest priority entry for this file.
                            var bestEntry = fileGroup.OrderByDescending(e => e.Priority).First();
                            var optionName = bestEntry.OptionName;

                            if (existingReplacements.TryGetValue(resolvedPath, out var existing))
                            {
                                // This should ideally not happen if we checked activeModNames, but just in case
                                foreach (var gp in fileGamePaths)
                                {
                                    var normalizedGp = gp.Replace('\\', '/').ToLowerInvariant();
                                    if (existing.GamePaths.Add(normalizedGp))
                                    {
                                        activeGamePaths.Add(normalizedGp);
                                    }
                                }
                            }
                            else
                            {
                                var uniqueGamePaths = fileGamePaths
                                    .Where(p => !activeGamePaths.Contains(p.Replace('\\', '/')))
                                    .ToArray();

                                if (uniqueGamePaths.Length == 0) continue;

                                var newReplacement = new FileReplacement(uniqueGamePaths, resolvedPath)
                                {
                                    ModName = modName,
                                    OptionName = optionName,
                                    IsActive = false
                                };

                                fragment.FileReplacements.Add(newReplacement);
                                existingReplacements[resolvedPath] = newReplacement;
                                addedCount++;

                                foreach (var gp in uniqueGamePaths)
                                {
                                    activeGamePaths.Add(gp.Replace('\\', '/').ToLowerInvariant());
                                }
                            }
                        }
                    }
                    _logger.LogDebug("[FileReplacementNew] Added {count} enabled-but-not-active mod files to character data (Strict Priority)", addedCount);
                }

                if (objectKind == ObjectKind.Player)
                {
                    _penumbraModScanner.SetActivePlayerMods(activeModNames);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FileReplacementNew] Failed to populate mod details");
            }

        _logger.LogDebug("== Static Replacements ==");
        foreach (var replacement in fragment.FileReplacements.Where(i => i.HasFileReplacement).OrderBy(i => i.GamePaths.First(), StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug("=> {repl}", replacement);
            ct.ThrowIfCancellationRequested();
        }

        await _transientResourceManager.WaitForRecording(ct).ConfigureAwait(false);

        // if it's pet then it's summoner, if it's summoner we actually want to keep all filereplacements alive at all times
        // or we get into redraw city for every change and nothing works properly
        if (objectKind == ObjectKind.Pet)
        {
            foreach (var item in fragment.FileReplacements.Where(i => i.HasFileReplacement).SelectMany(p => p.GamePaths))
            {
                if (_transientResourceManager.AddTransientResource(objectKind, item))
                {
                    _logger.LogDebug("Marking static {item} for Pet as transient", item);
                }
            }

            _logger.LogTrace("Clearing {count} Static Replacements for Pet", fragment.FileReplacements.Count);
            fragment.FileReplacements.Clear();
        }

        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Handling transient update for {obj}", playerRelatedObject);

        // remove all potentially gathered paths from the transient resource manager that are resolved through static resolving
        _transientResourceManager.ClearTransientPaths(objectKind, fragment.FileReplacements.SelectMany(c => c.GamePaths).ToList());

        // get all remaining paths and resolve them
        var transientPaths = ManageSemiTransientData(objectKind);
        var resolvedTransientPaths = await GetFileReplacementsFromPaths(transientPaths, new HashSet<string>(StringComparer.Ordinal)).ConfigureAwait(false);

        _logger.LogDebug("== Transient Replacements ==");
        foreach (var replacement in resolvedTransientPaths.Select(c => new FileReplacement([.. c.Value], c.Key)).OrderBy(f => f.ResolvedPath, StringComparer.Ordinal))
        {
            _logger.LogDebug("=> {repl}", replacement);
            fragment.FileReplacements.Add(replacement);
        }

        // clean up all semi transient resources that don't have any file replacement (aka null resolve)
        _transientResourceManager.CleanUpSemiTransientResources(objectKind, [.. fragment.FileReplacements]);

        ct.ThrowIfCancellationRequested();

        // make sure we only return data that actually has file replacements
        fragment.FileReplacements = new HashSet<FileReplacement>(fragment.FileReplacements.Where(v => v.HasFileReplacement).OrderBy(v => v.ResolvedPath, StringComparer.Ordinal), FileReplacementComparer.Instance);

        // gather up data from ipc
        Task<string> getHeelsOffset = _ipcManager.Heels.GetOffsetAsync();
        Task<string> getGlamourerData = _ipcManager.Glamourer.GetCharacterCustomizationAsync(playerRelatedObject.Address);
        Task<string?> getCustomizeData = _ipcManager.CustomizePlus.GetScaleAsync(playerRelatedObject.Address);
        Task<string> getHonorificTitle = _ipcManager.Honorific.GetTitle();
        Task<string> getBypassEmoteData = _ipcManager.BypassEmote.GetStateForCharacterAsync(playerRelatedObject.Address);
        fragment.GlamourerString = await getGlamourerData.ConfigureAwait(false);
        _logger.LogDebug("Glamourer is now: {data}", fragment.GlamourerString);
        var customizeScale = await getCustomizeData.ConfigureAwait(false);
        fragment.CustomizePlusScale = customizeScale ?? string.Empty;
        _logger.LogDebug("Customize is now: {data}", fragment.CustomizePlusScale);

        if (objectKind == ObjectKind.Player)
        {
            var playerFragment = (fragment as CharacterDataFragmentPlayer)!;
            playerFragment.ManipulationString = _ipcManager.Penumbra.GetMetaManipulations();

            playerFragment!.HonorificData = await getHonorificTitle.ConfigureAwait(false);
            _logger.LogDebug("Honorific is now: {data}", playerFragment!.HonorificData);

            playerFragment!.HeelsData = await getHeelsOffset.ConfigureAwait(false);
            _logger.LogDebug("Heels is now: {heels}", playerFragment!.HeelsData);

            playerFragment!.MoodlesData = await _ipcManager.Moodles.GetStatusAsync(playerRelatedObject.Address).ConfigureAwait(false) ?? string.Empty;
            _logger.LogDebug("Moodles is now: {moodles}", playerFragment!.MoodlesData);

            playerFragment!.PetNamesData = _ipcManager.PetNames.GetLocalNames();
            _logger.LogDebug("Pet Nicknames is now: {petnames}", playerFragment!.PetNamesData);

            playerFragment!.BypassEmoteData = await getBypassEmoteData.ConfigureAwait(false);
            _logger.LogDebug("BypassEmote is now: {bypassEmote}", playerFragment!.BypassEmoteData);
        }

        ct.ThrowIfCancellationRequested();

        var toCompute = fragment.FileReplacements.Where(f => !f.IsFileSwap).ToArray();
        _logger.LogDebug("Getting Hashes for {amount} Files", toCompute.Length);
        var computedPaths = await _fileCacheManager.GetFileCachesByPathsAsync(toCompute.Select(c => c.ResolvedPath).ToArray()).ConfigureAwait(false);
        foreach (var file in toCompute)
        {
            ct.ThrowIfCancellationRequested();
            file.Hash = computedPaths[file.ResolvedPath]?.Hash ?? string.Empty;
        }
        var removed = fragment.FileReplacements.RemoveWhere(f => !f.IsFileSwap && string.IsNullOrEmpty(f.Hash));
        if (removed > 0)
        {
            _logger.LogDebug("Removed {amount} of invalid files", removed);
        }

        ct.ThrowIfCancellationRequested();

        if (objectKind == ObjectKind.Player)
        {
            try
            {
                await VerifyPlayerAnimationBones(boneIndices, (fragment as CharacterDataFragmentPlayer)!, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException e)
            {
                throw new OperationCanceledException("Cancelled during player animation verification", e);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to verify player animations, continuing without further verification");
            }
        }

        _logger.LogDebug("Building character data for {obj} took {time}ms", objectKind, TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start.Ticks).TotalMilliseconds);

        return fragment;
    }

    private async Task VerifyPlayerAnimationBones(Dictionary<string, List<ushort>>? boneIndices, CharacterDataFragmentPlayer fragment, CancellationToken ct)
    {
        if (boneIndices == null) return;

        foreach (var kvp in boneIndices)
        {
            _logger.LogDebug("Found {skellyname} ({idx} bone indices) on player: {bones}", kvp.Key, kvp.Value.Any() ? kvp.Value.Max() : 0, string.Join(',', kvp.Value));
        }

        if (boneIndices.All(u => u.Value.Count == 0)) return;

        int noValidationFailed = 0;
        var failedAnimations = new List<(string path, int animationBones, int playerBones)>();
        int maxPlayerBones = boneIndices.SelectMany(b => b.Value).Max();
        
        foreach (var file in fragment.FileReplacements.Where(f => !f.IsFileSwap && f.GamePaths.First().EndsWith("pap", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            ct.ThrowIfCancellationRequested();

            var skeletonIndices = await _dalamudUtil.RunOnFrameworkThread(() => _modelAnalyzer.GetBoneIndicesFromPap(file.Hash)).ConfigureAwait(false);
            bool validationFailed = false;
            int maxAnimationBones = 0;
            
            if (skeletonIndices != null)
            {
                // 105 is the maximum vanilla skellington spoopy bone index
                if (skeletonIndices.All(k => k.Value.Max() <= 105))
                {
                    _logger.LogTrace("All indices of {path} are <= 105, ignoring", file.ResolvedPath);
                    continue;
                }

                _logger.LogDebug("Verifying bone indices for {path}, found {x} skeletons", file.ResolvedPath, skeletonIndices.Count);

                foreach (var boneCount in skeletonIndices.Select(k => k).ToList())
                {
                    maxAnimationBones = Math.Max(maxAnimationBones, boneCount.Value.Max());
                    if (boneCount.Value.Max() > maxPlayerBones)
                    {
                        _logger.LogWarning("Found more bone indices on the animation {path} skeleton {skl} (max indice {idx}) than on any player related skeleton (max indice {idx2})",
                            file.ResolvedPath, boneCount.Key, boneCount.Value.Max(), maxPlayerBones);
                        validationFailed = true;
                    }
                }
            }

            if (validationFailed)
            {
                noValidationFailed++;
                failedAnimations.Add((Path.GetFileName(file.ResolvedPath), maxAnimationBones, maxPlayerBones));
                _logger.LogDebug("Removing {file} from sent file replacements and transient data", file.ResolvedPath);
                fragment.FileReplacements.Remove(file);
                foreach (var gamePath in file.GamePaths)
                {
                    _transientResourceManager.RemoveTransientResource(ObjectKind.Player, gamePath);
                }
            }

        }

        if (noValidationFailed > 0)
        {
            var firstFailedAnimation = failedAnimations[0];
            string detailedMessage = $"Animation skeleton mismatch detected! The animation requires {firstFailedAnimation.animationBones} bones, " +
                $"but your current player skeleton only supports {firstFailedAnimation.playerBones} bones.\n\n" +
                $"This suggests an incorrect skeleton is loaded. Try the following:\n" +
                $"1. Open Penumbra\n" +
                $"2. Search the right skeleton and click on 'Self'\n" +
                $"3. Also, check for conflicts with other mods.\n" +
                $"(Check /xllog for more information)\n" +
                $"({noValidationFailed} animation{(noValidationFailed == 1 ? " was" : "s were")} affected and removed from sync data)";

            _spheneMediator.Publish(new NotificationMessage("Skeleton Bone Count Mismatch",
                detailedMessage,
                NotificationType.Warning, TimeSpan.FromSeconds(15)));
        }
    }

    private async Task<IReadOnlyDictionary<string, string[]>> GetFileReplacementsFromPaths(HashSet<string> forwardResolve, HashSet<string> reverseResolve)
    {
        var forwardPaths = forwardResolve.ToArray();
        var reversePaths = reverseResolve.ToArray();
        Dictionary<string, List<string>> resolvedPaths = new(StringComparer.Ordinal);
        var (forward, reverse) = await _ipcManager.Penumbra.ResolvePlayerPathsAsync(forwardPaths, reversePaths).ConfigureAwait(false);
        for (int i = 0; i < forwardPaths.Length; i++)
        {
            var filePath = forward[i].ToLowerInvariant();
            if (resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.Add(forwardPaths[i].ToLowerInvariant());
            }
            else
            {
                resolvedPaths[filePath] = [forwardPaths[i].ToLowerInvariant()];
            }
        }

        for (int i = 0; i < reversePaths.Length; i++)
        {
            var filePath = reversePaths[i].ToLowerInvariant();
            if (resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.AddRange(reverse[i].Select(c => c.ToLowerInvariant()));
            }
            else
            {
                resolvedPaths[filePath] = new List<string>(reverse[i].Select(c => c.ToLowerInvariant()).ToList());
            }
        }

        return resolvedPaths.ToDictionary(k => k.Key, k => k.Value.ToArray(), StringComparer.OrdinalIgnoreCase).AsReadOnly();
    }

    private HashSet<string> ManageSemiTransientData(ObjectKind objectKind)
    {
        _transientResourceManager.PersistTransientResources(objectKind);

        HashSet<string> pathsToResolve = new(StringComparer.Ordinal);
        foreach (var path in _transientResourceManager.GetSemiTransientResources(objectKind).Where(path => !string.IsNullOrEmpty(path)))
        {
            pathsToResolve.Add(path);
        }

        return pathsToResolve;
    }


}
