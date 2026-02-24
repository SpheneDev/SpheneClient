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
using System.Text;
using System.Text.RegularExpressions;

namespace Sphene.PlayerData.Factories;

public class PlayerDataFactory
{
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

        Dictionary<string, HashSet<string>>? resolvedPaths;
        Dictionary<string, (string ModName, string OptionName, int Priority)> modLookup;
        Dictionary<string, (string ResolvedPath, string ModName, string OptionName, int Priority)> gamePathLookup;
        HashSet<string>? activeGamePaths = null;

        if (objectKind == ObjectKind.Player)
        {
            resolvedPaths = await _ipcManager.Penumbra.GetCharacterData(_logger, playerRelatedObject).ConfigureAwait(false);
            if (resolvedPaths != null)
            {
                activeGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var set in resolvedPaths.Values)
                {
                    foreach (var gamePath in set)
                    {
                        if (string.IsNullOrWhiteSpace(gamePath)) continue;
                        activeGamePaths.Add(gamePath.Replace('\\', '/').ToLowerInvariant());
                    }
                }
            }

            modLookup = await _penumbraModScanner.ForceFullScanAsync(ct).ConfigureAwait(false);
            gamePathLookup = await _penumbraModScanner.GetGamePathLookupAsync(ct).ConfigureAwait(false);
            var playerRaceCode = _penumbraModScanner.PlayerRaceCode;
            var optionPapRaceStats = new Dictionary<string, OptionPapRaceGroupStats>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(playerRaceCode))
            {
                foreach (var entry in gamePathLookup)
                {
                    var info = entry.Value;
                    if (!IsPapPath(entry.Key, info.ResolvedPath))
                    {
                        continue;
                    }

                    var normalizedGamePath = entry.Key.Replace('\\', '/');
                    var normalizedResolvedPath = info.ResolvedPath?.Replace('\\', '/');
                    var modKey = string.IsNullOrWhiteSpace(info.ModName) ? "Unknown Mod" : info.ModName;
                    var optionKey = string.IsNullOrWhiteSpace(info.OptionName) ? "Default" : info.OptionName;
                    var hasAnyRaceCode = PenumbraModScanner.PathContainsAnyRaceCode(normalizedGamePath)
                        || (!string.IsNullOrWhiteSpace(normalizedResolvedPath) && PenumbraModScanner.PathContainsAnyRaceCode(normalizedResolvedPath));
                    if (!hasAnyRaceCode)
                    {
                        continue;
                    }

                    var groupSource = PenumbraModScanner.PathContainsAnyRaceCode(normalizedGamePath)
                        ? normalizedGamePath
                        : normalizedResolvedPath ?? normalizedGamePath;
                    var groupKey = BuildPapRaceGroupKey(groupSource);
                    var key = $"{modKey}\u001F{optionKey}\u001F{groupKey}";
                    if (!optionPapRaceStats.TryGetValue(key, out var stats))
                    {
                        stats = new OptionPapRaceGroupStats();
                        optionPapRaceStats[key] = stats;
                    }

                    stats.LastRaceTaggedPapGamePath = normalizedGamePath;
                    if (!stats.HasPlayerRaceCode
                        && (PenumbraModScanner.PathContainsRaceCode(normalizedGamePath, playerRaceCode)
                            || (!string.IsNullOrWhiteSpace(normalizedResolvedPath) && PenumbraModScanner.PathContainsRaceCode(normalizedResolvedPath, playerRaceCode))))
                    {
                        stats.HasPlayerRaceCode = true;
                    }
                }
            }

            fragment.FileReplacements = new HashSet<FileReplacement>(FileReplacementComparer.Instance);
            var replacementsByResolvedPath = new Dictionary<string, FileReplacement>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in gamePathLookup)
            {
                var gamePath = entry.Key;
                if (string.IsNullOrWhiteSpace(gamePath))
                {
                    continue;
                }

                var normalizedGamePath = gamePath.Replace('\\', '/');
                if (activeGamePaths != null)
                {
                    var decision = _penumbraModScanner.IsEquipmentGamePath(normalizedGamePath);
                    if (decision == PenumbraModScanner.EquipmentPathDecision.Exclude)
                    {
                        _logger.LogTrace("[winning] Excluding path by equipment decision. GamePath={gamePath}", normalizedGamePath);
                        continue;
                    }
                    if (decision == PenumbraModScanner.EquipmentPathDecision.Include
                        && !activeGamePaths.Contains(normalizedGamePath.ToLowerInvariant()))
                    {
                        continue;
                    }
                }

                if (!CacheMonitor.AllowedFileExtensions.Any(e => normalizedGamePath.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var info = entry.Value;
                if (string.IsNullOrWhiteSpace(info.ResolvedPath))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(playerRaceCode) && IsPapPath(normalizedGamePath, info.ResolvedPath))
                {
                    var modKey = string.IsNullOrWhiteSpace(info.ModName) ? "Unknown Mod" : info.ModName;
                    var optionKey = string.IsNullOrWhiteSpace(info.OptionName) ? "Default" : info.OptionName;
                    var normalizedResolvedPath = info.ResolvedPath?.Replace('\\', '/');
                    var entryHasRaceCode = PenumbraModScanner.PathContainsAnyRaceCode(normalizedGamePath)
                        || (!string.IsNullOrWhiteSpace(normalizedResolvedPath) && PenumbraModScanner.PathContainsAnyRaceCode(normalizedResolvedPath));
                    if (entryHasRaceCode)
                    {
                        var groupSource = PenumbraModScanner.PathContainsAnyRaceCode(normalizedGamePath)
                            ? normalizedGamePath
                            : normalizedResolvedPath ?? normalizedGamePath;
                        var groupKey = BuildPapRaceGroupKey(groupSource);
                        var key = $"{modKey}\u001F{optionKey}\u001F{groupKey}";
                        if (optionPapRaceStats.TryGetValue(key, out var stats))
                        {
                            var entryHasPlayerCode = PenumbraModScanner.PathContainsRaceCode(normalizedGamePath, playerRaceCode)
                                || (!string.IsNullOrWhiteSpace(normalizedResolvedPath) && PenumbraModScanner.PathContainsRaceCode(normalizedResolvedPath, playerRaceCode));
                            if (stats.HasPlayerRaceCode)
                            {
                                if (!entryHasPlayerCode)
                                {
                                    _logger.LogTrace("[winning] Excluding pap path without player race code. Mod={mod} Option={opt} GamePath={gamePath} ResolvedPath={resolvedPath} PlayerCode={playerRaceCode}", modKey, optionKey, normalizedGamePath, info.ResolvedPath, playerRaceCode);
                                    continue;
                                }
                            }
                            else if (!entryHasPlayerCode && !string.IsNullOrWhiteSpace(stats.LastRaceTaggedPapGamePath)
                                && !string.Equals(stats.LastRaceTaggedPapGamePath, normalizedGamePath, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogTrace("[winning] Excluding pap path because player race is missing and a different fallback pap is selected. Mod={mod} Option={opt} GamePath={gamePath} ResolvedPath={resolvedPath} PlayerCode={playerRaceCode} FallbackGamePath={fallback}", modKey, optionKey, normalizedGamePath, info.ResolvedPath, playerRaceCode, stats.LastRaceTaggedPapGamePath);
                                continue;
                            }
                        }
                    }
                }

                var resolvedPath = info.ResolvedPath;
                if (string.IsNullOrWhiteSpace(resolvedPath))
                {
                    continue;
                }

                if (!replacementsByResolvedPath.TryGetValue(resolvedPath, out var replacement))
                {
                    replacement = new FileReplacement([normalizedGamePath], resolvedPath)
                    {
                        ModName = info.ModName,
                        OptionName = info.OptionName,
                    };
                    replacementsByResolvedPath.Add(resolvedPath, replacement);
                    fragment.FileReplacements.Add(replacement);
                }
                else
                {
                    replacement.GamePaths.Add(normalizedGamePath.ToLowerInvariant());
                    if (string.IsNullOrEmpty(replacement.ModName) && !string.IsNullOrEmpty(info.ModName))
                    {
                        replacement.ModName = info.ModName;
                    }
                    if (string.IsNullOrEmpty(replacement.OptionName) && !string.IsNullOrEmpty(info.OptionName))
                    {
                        replacement.OptionName = info.OptionName;
                    }
                }
            }

            if (activeGamePaths != null)
            {
                foreach (var replacement in fragment.FileReplacements)
                {
                    replacement.IsActive = replacement.GamePaths.Any(activeGamePaths.Contains);
                }
            }
        }
        else
        {
            resolvedPaths = await _ipcManager.Penumbra.GetCharacterData(_logger, playerRelatedObject).ConfigureAwait(false);
            if (resolvedPaths == null) throw new InvalidOperationException("Penumbra returned null data");

            ct.ThrowIfCancellationRequested();

            fragment.FileReplacements =
                    new HashSet<FileReplacement>(resolvedPaths.Select(c => new FileReplacement([.. c.Value], c.Key) { IsActive = true }), FileReplacementComparer.Instance)
                    .Where(p => p.HasFileReplacement).ToHashSet();
            fragment.FileReplacements.RemoveWhere(c => c.GamePaths.Any(g => !CacheMonitor.AllowedFileExtensions.Any(e => g.EndsWith(e, StringComparison.OrdinalIgnoreCase))));

            modLookup = await _penumbraModScanner.GetModFileLookupAsync(ct).ConfigureAwait(false);
            gamePathLookup = await _penumbraModScanner.GetGamePathLookupAsync(ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            _logger.LogDebug("[FileReplacementNew] PenumbraModScanner returned {count} entries", modLookup.Count);

            if (modLookup.Count > 0 && _logger.IsEnabled(LogLevel.Trace))
            {
                 var sampleKeys = string.Join(", ", modLookup.Keys.Take(5));
                 _logger.LogTrace("[FileReplacementNew] Sample keys in lookup: {keys}", sampleKeys);
            }

            var activeModNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var replacement in fragment.FileReplacements)
            {
                if (modLookup.TryGetValue(replacement.ResolvedPath, out var modInfo))
                {
                    replacement.ModName = modInfo.ModName;
                    replacement.OptionName = modInfo.OptionName;
                    activeModNames.Add(modInfo.ModName);
                    _logger.LogTrace("[FileReplacementNew] Found match for {path}: Mod={mod}, Option={opt}", replacement.ResolvedPath, modInfo.ModName, modInfo.OptionName);
                }
                else
                {
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
                        bool foundByGamePath = false;
                        foreach (var gamePath in replacement.GamePaths)
                        {
                            if (gamePathLookup.TryGetValue(gamePath, out var gamePathInfo))
                            {
                                replacement.ModName = gamePathInfo.ModName;
                                replacement.OptionName = gamePathInfo.OptionName;
                                activeModNames.Add(gamePathInfo.ModName);
                                _logger.LogTrace("[FileReplacementNew] Found match for game path {path}: Mod={mod}, Option={opt}", gamePath, gamePathInfo.ModName, gamePathInfo.OptionName);
                                foundByGamePath = true;
                                break;
                            }
                        }

                        if (!foundByGamePath)
                        {
                            _logger.LogDebug("[FileReplacementNew] Failed to find mod info for path: '{path}' (Normalized: '{norm}')", replacement.ResolvedPath, normalizedPath);
                        }
                    }
                }
            }

            if (objectKind == ObjectKind.Player)
            {
                _penumbraModScanner.SetActivePlayerMods(activeModNames);
            }

            RemoveLowerPriorityOptionConflicts(fragment.FileReplacements, gamePathLookup);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileReplacementNew] Failed to populate mod details");
        }

        bool logReplacements = _logger.IsEnabled(LogLevel.Trace);
        if (logReplacements)
        {
            _logger.LogTrace("== Static Replacements ==");
            foreach (var replacement in fragment.FileReplacements.Where(i => i.HasFileReplacement).OrderBy(i => i.GamePaths.First(), StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogTrace("=> {repl}", replacement);
                ct.ThrowIfCancellationRequested();
            }
        }

        await _transientResourceManager.WaitForRecording(ct).ConfigureAwait(false);

        // if it's pet then it's summoner, if it's summoner we actually want to keep all filereplacements alive at all times
        // or we get into redraw city for every change and nothing works properly
        if (objectKind == ObjectKind.Pet)
        {
            foreach (var item in fragment.FileReplacements.Where(i => i.HasFileReplacement).SelectMany(p => p.GamePaths))
            {
                if (_transientResourceManager.AddTransientResource(objectKind, item) && logReplacements)
                {
                    _logger.LogTrace("Marking static {item} for Pet as transient", item);
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

        if (logReplacements)
        {
            _logger.LogTrace("== Transient Replacements ==");
        }
        foreach (var replacement in resolvedTransientPaths.Select(c => new FileReplacement([.. c.Value], c.Key)).OrderBy(f => f.ResolvedPath, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(replacement.ModName))
            {
                if (modLookup.TryGetValue(replacement.ResolvedPath, out var modInfo))
                {
                    replacement.ModName = modInfo.ModName;
                    replacement.OptionName = modInfo.OptionName;
                }
                else
                {
                    var normalizedPath = Path.GetFullPath(replacement.ResolvedPath);
                    if (modLookup.TryGetValue(normalizedPath, out modInfo))
                    {
                        replacement.ModName = modInfo.ModName;
                        replacement.OptionName = modInfo.OptionName;
                    }
                    else
                    {
                        foreach (var gamePath in replacement.GamePaths)
                        {
                            if (gamePathLookup.TryGetValue(gamePath, out var gamePathInfo))
                            {
                                replacement.ModName = gamePathInfo.ModName;
                                replacement.OptionName = gamePathInfo.OptionName;
                                break;
                            }
                        }
                    }
                }
            }
            if (logReplacements)
            {
                _logger.LogTrace("=> {repl}", replacement);
            }
            fragment.FileReplacements.Add(replacement);
        }

        NormalizeModOptions(fragment.FileReplacements, gamePathLookup);

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

    private static bool IsPapPath(string? gamePath, string? resolvedPath)
    {
        if (!string.IsNullOrWhiteSpace(gamePath) && gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(resolvedPath) && resolvedPath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string BuildPapRaceGroupKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.Replace('\\', '/');
        var builder = new StringBuilder(normalized.Length);
        int index = 0;
        while (index <= normalized.Length - 5)
        {
            var current = normalized[index];
            if (current != 'c' && current != 'C')
            {
                builder.Append(current);
                index++;
                continue;
            }

            if (!char.IsDigit(normalized[index + 1])
                || !char.IsDigit(normalized[index + 2])
                || !char.IsDigit(normalized[index + 3])
                || !char.IsDigit(normalized[index + 4]))
            {
                builder.Append(current);
                index++;
                continue;
            }

            builder.Append("c0000");
            index += 5;
        }

        if (index < normalized.Length)
        {
            builder.Append(normalized.AsSpan(index));
        }

        return builder.ToString();
    }

    private sealed class OptionPapRaceGroupStats
    {
        internal bool HasPlayerRaceCode { get; set; }
        internal string? LastRaceTaggedPapGamePath { get; set; }
    }

    private void RemoveLowerPriorityOptionConflicts(HashSet<FileReplacement> replacements,
        Dictionary<string, (string ResolvedPath, string ModName, string OptionName, int Priority)> gamePathLookup)
    {
        var options = new Dictionary<string, OptionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var replacement in replacements)
        {
            if (!replacement.HasFileReplacement)
            {
                continue;
            }

            var modName = string.IsNullOrWhiteSpace(replacement.ModName) ? "Unknown Mod" : replacement.ModName;
            var optionName = string.IsNullOrWhiteSpace(replacement.OptionName) ? "Default" : replacement.OptionName;
            var optionKey = $"{modName}||{optionName}";

            if (!options.TryGetValue(optionKey, out var option))
            {
                option = new OptionEntry(modName, optionName);
                options.Add(optionKey, option);
            }

            var gamePaths = replacement.GamePaths;
            foreach (var gamePath in gamePaths)
            {
                if (gamePathLookup.TryGetValue(gamePath, out var entry) && entry.Priority > option.Priority)
                {
                    option.Priority = entry.Priority;
                }

                if (_penumbraModScanner.TryGetChangedItemsFromGamePath(gamePath, out var changedItems))
                {
                    for (int j = 0; j < changedItems.Count; j++)
                    {
                        option.Items.Add(changedItems[j]);
                    }
                }
            }
        }

        var itemWinners = new Dictionary<string, ItemWinner>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options.Values)
        {
            foreach (var item in option.Items)
            {
                if (!itemWinners.TryGetValue(item, out var winner))
                {
                    winner = new ItemWinner(option.Priority, option.Key);
                    itemWinners.Add(item, winner);
                    continue;
                }

                if (option.Priority > winner.Priority)
                {
                    winner.Priority = option.Priority;
                    winner.Options.Clear();
                    winner.Options.Add(option.Key);
                }
                else if (option.Priority == winner.Priority)
                {
                    winner.Options.Add(option.Key);
                }
            }
        }

        var losers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options.Values)
        {
            foreach (var item in option.Items)
            {
                if (!itemWinners.TryGetValue(item, out var winner))
                {
                    continue;
                }

                if (winner.Priority > option.Priority)
                {
                    losers.Add(option.Key);
                    break;
                }
            }
        }

        if (losers.Count == 0)
        {
            return;
        }

        replacements.RemoveWhere(r =>
        {
            if (!r.HasFileReplacement)
            {
                return false;
            }

            var modName = string.IsNullOrWhiteSpace(r.ModName) ? "Unknown Mod" : r.ModName;
            var optionName = string.IsNullOrWhiteSpace(r.OptionName) ? "Default" : r.OptionName;
            var optionKey = $"{modName}||{optionName}";
            return losers.Contains(optionKey);
        });
    }

    private sealed class OptionEntry
    {
        public OptionEntry(string modName, string optionName)
        {
            Key = $"{modName}||{optionName}";
        }

        public string Key { get; }
        public int Priority { get; set; } = int.MinValue;
        public HashSet<string> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ItemWinner
    {
        public ItemWinner(int priority, string key)
        {
            Priority = priority;
            Options.Add(key);
        }

        public int Priority { get; set; }
        public HashSet<string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void NormalizeModOptions(HashSet<FileReplacement> replacements, Dictionary<string, (string ResolvedPath, string ModName, string OptionName, int Priority)> gamePathLookup)
    {
        if (replacements.Count == 0 || gamePathLookup.Count == 0)
        {
            return;
        }

        var modBestOption = new Dictionary<string, (string OptionName, int Priority)>(StringComparer.OrdinalIgnoreCase);

        foreach (var replacement in replacements)
        {
            var modName = replacement.ModName;
            if (string.IsNullOrWhiteSpace(modName))
            {
                continue;
            }

            string? bestOptionName = null;
            int bestPriority = int.MinValue;

            foreach (var gamePath in replacement.GamePaths)
            {
                if (!gamePathLookup.TryGetValue(gamePath, out var info))
                {
                    continue;
                }

                if (!string.Equals(info.ModName, modName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(info.OptionName))
                {
                    continue;
                }

                if (info.Priority > bestPriority)
                {
                    bestPriority = info.Priority;
                    bestOptionName = info.OptionName;
                }
                else if (info.Priority == bestPriority && bestOptionName != null && string.Compare(info.OptionName, bestOptionName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    bestOptionName = info.OptionName;
                }
            }

            if (bestOptionName == null)
            {
                continue;
            }

            if (modBestOption.TryGetValue(modName, out var existing))
            {
                if (bestPriority > existing.Priority)
                {
                    modBestOption[modName] = (bestOptionName, bestPriority);
                }
                else if (bestPriority == existing.Priority && string.Compare(bestOptionName, existing.OptionName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    modBestOption[modName] = (bestOptionName, existing.Priority);
                }
            }
            else
            {
                modBestOption[modName] = (bestOptionName, bestPriority);
            }
        }

        if (modBestOption.Count == 0)
        {
            return;
        }

        foreach (var replacement in replacements)
        {
            var modName = replacement.ModName;
            if (string.IsNullOrWhiteSpace(modName))
            {
                continue;
            }

            if (modBestOption.TryGetValue(modName, out var best))
            {
                replacement.OptionName = best.OptionName;
            }
        }
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
