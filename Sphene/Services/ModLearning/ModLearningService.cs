#pragma warning disable CS0618
using Sphene.Interop.Ipc;
using Sphene.PlayerData.Data;
using Sphene.Services.ModLearning.Models;
using Sphene.Services.Mediator;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Sphene.Services;
using Sphene.API.Data.Enum;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

using Sphene.Services.CharaData;
using Sphene.API.Data;
using Sphene.API.Dto.CharaData;
using System.Threading;
using System.Diagnostics;
using System.Security.Cryptography;
using Sphene.FileCache;
using Sphene.SpheneConfiguration;

namespace Sphene.Services.ModLearning;

public class ModLearningService : DisposableMediatorSubscriberBase, Microsoft.Extensions.Hosting.IHostedService
{
    private readonly IpcCallerPenumbra _penumbra;
    private readonly ILogger<ModLearningService> _logger;
    private readonly CharacterDataSqliteStore _sqliteStore;
    private readonly IClientState _clientState;
    private readonly IDataManager _gameData;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileCacheManager _fileCacheManager;
    private readonly SpheneConfigService _configService;
    private readonly TransientConfigService _transientConfigService;
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, HashSet<string>>>> _modOptionIndexCache = [];
    private readonly Dictionary<string, Dictionary<string, OptionGroupFileEntry>> _modOptionFileMapCache = [];
    private readonly Dictionary<string, string> _stateHashCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _modResourceLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, HashSet<string>>>> _modResourceLinksBySettings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(IntPtr, string), List<ResourceLoadStamp>> _recentResourceLoads = [];
    private readonly Dictionary<string, Dictionary<uint, HashSet<string>>> _observedTransientPathsByCharacter = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPenumbraSettingChangeAt = DateTime.MinValue;
    private int _penumbraSettingChangeCount = 0;
    private readonly HashSet<string> _pendingModSettingChanges = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasGlobalModSettingChange = false;
    private DateTime _lastRestoreAt = DateTime.MinValue;
    private string? _lastReadyPushHash = null;
    private readonly Lazy<Dictionary<string, List<string>>> _papEmoteMap;

    // In-memory cache to avoid frequent DB reads, keyed by "CharacterKey" (Name@Homeworld)
    public Dictionary<string, CharacterModProfile> Profiles { get; private set; } = [];

    private readonly Lock _lock = new();

    public ModLearningService(ILogger<ModLearningService> logger, IpcCallerPenumbra penumbra, SpheneMediator mediator, IClientState clientState, IDataManager gameData, CharacterDataSqliteStore sqliteStore, DalamudUtilService dalamudUtil, FileCacheManager fileCacheManager, SpheneConfigService configService, TransientConfigService transientConfigService) 
        : base(logger, mediator)
    {
        _logger = logger;
        _penumbra = penumbra;
        _clientState = clientState;
        _gameData = gameData;
        _sqliteStore = sqliteStore;
        _dalamudUtil = dalamudUtil;
        _fileCacheManager = fileCacheManager;
        _configService = configService;
        _transientConfigService = transientConfigService;
        _papEmoteMap = new Lazy<Dictionary<string, List<string>>>(BuildPapEmoteMap);

        Mediator.Subscribe<CharacterDataCreatedMessage>(this, OnCharacterDataCreated);
        Mediator.Subscribe<PenumbraModSettingChangedMessage>(this, OnPenumbraModSettingChanged);
        Mediator.Subscribe<ResetCharacterDataDatabaseMessage>(this, _ => ResetCaches());
        Mediator.Subscribe<PenumbraResourceLoadMessage>(this, OnPenumbraResourceLoad);
        Mediator.Subscribe<TransientResourceObservedMessage>(this, OnTransientResourceObserved);
    }

    private void ResetCaches()
    {
        lock (_lock)
        {
            Profiles.Clear();
            _modOptionIndexCache.Clear();
            _modOptionFileMapCache.Clear();
            _stateHashCache.Clear();
            _modResourceLinks.Clear();
            _modResourceLinksBySettings.Clear();
            _recentResourceLoads.Clear();
            _observedTransientPathsByCharacter.Clear();
            _pendingModSettingChanges.Clear();
            _hasGlobalModSettingChange = false;
        }
        _lastRestoreAt = DateTime.MinValue;
        _lastReadyPushHash = null;
    }

    private void OnPenumbraResourceLoad(PenumbraResourceLoadMessage msg)
    {
        if (!_configService.Current.EnableModLearning) return;
        var gamePath = NormalizePathString(msg.GamePath);
        var filePath = NormalizeFilePath(msg.FilePath);
        if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(filePath)) return;

        if (!TryGetModNameFromPath(filePath, out var modName)) return;
        modName = modName.ToLowerInvariant();

        var ext = Path.GetExtension(gamePath);
        if (!IsTrackedExt(ext)) return;

        var key = (msg.GameObject, modName);
        var now = Environment.TickCount64;
        var settingsHash = TryGetSettingsHashForMod(modName);

        lock (_lock)
        {
            if (!_recentResourceLoads.TryGetValue(key, out var list))
            {
                list = [];
                _recentResourceLoads[key] = list;
            }

            list.RemoveAll(e => now - e.TimestampMs > 2000);

            if (string.Equals(ext, ".scd", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var entry in list)
                {
                    if (!IsTrackedParentExt(entry.Extension)) continue;
                    var addedGlobal = AddResourceLink(modName, entry.GamePath, gamePath);
                    if (settingsHash != null)
                    {
                        AddResourceLinkForSettings(modName, settingsHash, entry.GamePath, gamePath);
                    }
                    if (addedGlobal)
                    {
                        _ = Task.Run(() => _sqliteStore.UpsertResourceLinkAsync(modName, entry.GamePath, gamePath));
                    }
                }
                if (list.Any(e => IsTrackedParentExt(e.Extension)))
                {
                    _logger.LogDebug("[ModLearning] Link learned: mod={mod} scd={scd} parents={parents}",
                        modName, gamePath, string.Join(", ", list.Where(e => IsTrackedParentExt(e.Extension)).Select(e => e.GamePath)));
                }
            }
            else if (IsTrackedParentExt(ext))
            {
                list.Add(new ResourceLoadStamp(gamePath, ext, now));
                _logger.LogDebug("[ModLearning] Parent loaded: mod={mod} ext={ext} path={path}", modName, ext, gamePath);
            }
        }
    }

    private void OnTransientResourceObserved(TransientResourceObservedMessage msg)
    {
        if (!_configService.Current.EnableModLearning) return;
        if (msg.ObjectKind != ObjectKind.Player) return;
        if (msg.HomeWorldId == 0 || msg.JobId == 0 || string.IsNullOrWhiteSpace(msg.CharacterName) || string.IsNullOrWhiteSpace(msg.GamePath)) return;

        var transientKey = $"{msg.CharacterName}_{msg.HomeWorldId}";
        var normalizedPath = NormalizePathString(msg.GamePath);
        if (string.IsNullOrWhiteSpace(normalizedPath)) return;

        lock (_lock)
        {
            if (!_observedTransientPathsByCharacter.TryGetValue(transientKey, out var byJob))
            {
                byJob = new Dictionary<uint, HashSet<string>>();
                _observedTransientPathsByCharacter[transientKey] = byJob;
            }

            if (!byJob.TryGetValue(msg.JobId, out var paths))
            {
                paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byJob[msg.JobId] = paths;
            }

            paths.Add(normalizedPath);
        }
    }

    private void OnPenumbraModSettingChanged(PenumbraModSettingChangedMessage msg)
    {
        if (!_configService.Current.EnableModLearning) return;
        _penumbraSettingChangeCount++;
        _lastPenumbraSettingChangeAt = DateTime.UtcNow;
        lock (_lock)
        {
            var hasModName = !string.IsNullOrWhiteSpace(msg.ModDirectoryName);
            var isTargetedChange = msg.ChangeType is Penumbra.Api.Enums.ModSettingChange.Setting
                or Penumbra.Api.Enums.ModSettingChange.EnableState
                or Penumbra.Api.Enums.ModSettingChange.Priority
                or Penumbra.Api.Enums.ModSettingChange.TemporaryMod
                or Penumbra.Api.Enums.ModSettingChange.Edited
                or Penumbra.Api.Enums.ModSettingChange.TemporarySetting;

            if (isTargetedChange)
            {
                if (hasModName)
                {
                    _pendingModSettingChanges.Add(msg.ModDirectoryName);
                }
                else
                {
                    _hasGlobalModSettingChange = true;
                }
            }
            else
            {
                _hasGlobalModSettingChange = true;
                if (hasModName)
                {
                    _pendingModSettingChanges.Add(msg.ModDirectoryName);
                }
            }
        }
        _logger.LogDebug("[ModLearning] Penumbra settings changed. Count={count}", _penumbraSettingChangeCount);
    }

    private void PublishReadyForPush(Sphene.API.Data.CharacterData data)
    {
        var hash = data.DataHash?.Value;
        if (string.IsNullOrWhiteSpace(hash)) return;
        if (string.Equals(_lastReadyPushHash, hash, StringComparison.Ordinal)) return;
        _lastReadyPushHash = hash;
        Mediator.Publish(new CharacterDataReadyForPushMessage(data));
    }

    private void OnCharacterDataCreated(CharacterDataCreatedMessage msg)
    {
        var traceId = $"MLC-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        _logger.LogDebug("[Trace:ModLearning:{trace}] START kinds={kinds} hash={hash}", traceId, msg.RawData?.FileReplacements.Count ?? 0, msg.CharacterData.DataHash?.Value ?? "null");
        if (!_configService.Current.EnableModLearning)
        {
            _logger.LogDebug("[Trace:ModLearning:{trace}] SKIP disabled", traceId);
            PublishReadyForPush(msg.CharacterData);
            return;
        }
        var rawData = msg.RawData;
        if (rawData == null)
        {
            _logger.LogDebug("[Trace:ModLearning:{trace}] SKIP no-rawdata", traceId);
            return;
        }

        _logger.LogDebug("[Trace:ModLearning:{trace}] RAW kinds={kinds} hash={hash}", traceId, rawData.FileReplacements.Count, msg.CharacterData.DataHash?.Value ?? "null");
        // Extract relevant data synchronously (safe because we are inside the lock of CacheCreationService)
        var extractedData = new Dictionary<ObjectKind, List<FileReplacement>>();
            foreach (var kvp in rawData.FileReplacements)
        {
            var list = new List<FileReplacement>();
            foreach (var r in kvp.Value)
            {
                 if ((r.HasFileReplacement || r.IsFileSwap) && IsTrackedReplacement(r, kvp.Key))
                 {
                     list.Add(r);
                 }
            }
            if (list.Any())
                extractedData[kvp.Key] = list;
        }
        _logger.LogDebug("[Trace:ModLearning:{trace}] EXTRACT kinds={kinds} totalEntries={count}", traceId, extractedData.Count, extractedData.Sum(k => k.Value.Count));

        // Dispatch to Main Thread
        var runTask = _dalamudUtil.RunOnFrameworkThread(async () =>
        {
            try
            {
                if ((DateTime.UtcNow - _lastRestoreAt).TotalMilliseconds < 1500)
                {
                    _logger.LogDebug("[Trace:ModLearning:{trace}] RESTORE SKIP debounce", traceId);
                    return;
                }

                if (_lastPenumbraSettingChangeAt != DateTime.MinValue)
                {
                    var deltaMs = (DateTime.UtcNow - _lastPenumbraSettingChangeAt).TotalMilliseconds;
                    _logger.LogDebug("[ModLearning] CharacterDataCreated after last setting change: {ms}ms", deltaMs);
                }

                // 1. Attempt to restore missing files from learned data
                var player = _clientState.LocalPlayer;
                if (player == null)
                {
                    _logger.LogDebug("[ModLearning] LocalPlayer is null, skipping processing.");
                    return;
                }

                var name = player.Name.ToString();
                var homeWorld = player.HomeWorld.Value.Name.ToString();
                var homeWorldId = player.HomeWorld.RowId;
                var jobId = player.ClassJob.RowId;
                var key = $"{name}@{homeWorld}";

                _logger.LogDebug("[Trace:ModLearning:{trace}] RESTORE start key={key} job={job}", traceId, key, jobId);
                var restored = await RestoreLearnedData(msg, key, name, homeWorld, jobId).ConfigureAwait(false);
                var dataToPush = msg.CharacterData;
                if (restored)
                {
                    _lastRestoreAt = DateTime.UtcNow;
                    var newData = CloneCharacterData(msg.CharacterData);
                    await _sqliteStore.UpsertLocalCharacterDataAsync(newData).ConfigureAwait(false);
                    _logger.LogDebug("[Trace:ModLearning:{trace}] RESTORE updated local data", traceId);
                    Mediator.Publish(new CharacterDataCreatedMessage(newData, msg.RawData));
                    dataToPush = newData;
                }
                _logger.LogDebug("[Trace:ModLearning:{trace}] RESTORE done restored={restored}", traceId, restored);

                if (!extractedData.Any())
                {
                    _logger.LogDebug("[Trace:ModLearning:{trace}] PROCESS SKIP no-extracted", traceId);
                    PublishReadyForPush(dataToPush);
                    return;
                }

                _logger.LogDebug("[Trace:ModLearning:{trace}] PROCESS begin key={key} job={job} kinds={kinds}", traceId, key, jobId, extractedData.Count);

                await Task.Run(() => ProcessCharacterData(key, name, homeWorld, homeWorldId, jobId, extractedData)).ConfigureAwait(false);
                _logger.LogDebug("[Trace:ModLearning:{trace}] PROCESS done", traceId);
                PublishReadyForPush(dataToPush);
                _logger.LogDebug("[Trace:ModLearning:{trace}] READY push", traceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ModLearning] Error in ModLearning flow");
            }
        });
        _ = runTask.ContinueWith(t => _logger.LogError(t.Exception, "[ModLearning] Error in ModLearning flow"), TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task<bool> RestoreLearnedData(CharacterDataCreatedMessage msg, string key, string name, string homeWorld, uint jobId)
    {
        if (msg.CharacterData == null) return false;
        var apiData = msg.CharacterData;

        // Ensure collections are initialized
        if (apiData.FileReplacements == null) apiData.FileReplacements = [];
        var baselineParentPaths = apiData.FileReplacements.Values
            .SelectMany(l => l)
            .SelectMany(e => e.GamePaths)
            .Where(p => p.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
            .Select(NormalizePathString)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _logger.LogTrace("[ModLearning] Restore start: key={key} jobId={jobId}", key, jobId);

        // Ensure profile is loaded
        EnsureProfileLoaded(key, name, homeWorld);
        
        if (!_penumbra.APIAvailable) return false;

        var (valid, _, collection) = _penumbra.GetCollectionForObject(0);
        if (!valid) return false;

        if (!Profiles.TryGetValue(key, out var profile)) return false;

        bool modified = false;

        List<string> modDirs;
        lock (_lock)
        {
             modDirs = profile.LearnedMods.Keys.ToList();
        }

        var enabledMods = new List<(string ModDirName, Dictionary<string, List<string>> Settings, List<LearnedModState> States)>();
        var allStates = new List<LearnedModState>();
        HashSet<string> pendingModSettings = [];
        lock (_lock)
        {
            if (_pendingModSettingChanges.Count > 0 &&
                (DateTime.UtcNow - _lastPenumbraSettingChangeAt).TotalSeconds < 10)
            {
                pendingModSettings = new HashSet<string>(_pendingModSettingChanges, StringComparer.OrdinalIgnoreCase);
            }
        }
        foreach (var modDirName in modDirs)
        {
            var (ec, settingsTuple) = _penumbra.GetModSettings(collection.Id, modDirName);
            if (ec != Penumbra.Api.Enums.PenumbraApiEc.Success || settingsTuple == null)
                continue;

            var (enabled, _, settings, _) = settingsTuple.Value;
            if (!enabled) continue;

            var settingsDict = settings.ToDictionary(k => k.Key, k => k.Value.ToList(), StringComparer.Ordinal);
            List<LearnedModState>? states;
            lock (_lock)
            {
                if (!profile.LearnedMods.TryGetValue(modDirName, out states)) continue;
                states = states.ToList();
            }

            var matchingStates = states.Where(s => SettingsSubsetMatch(settingsDict, s.Settings)).ToList();
            if (matchingStates.Count == 0) continue;
            enabledMods.Add((modDirName, settingsDict, matchingStates));
            allStates.AddRange(matchingStates);
        }

        if (enabledMods.Count == 0) return false;

        var resolvedMap = await ResolveEffectivePathsAsync(allStates).ConfigureAwait(false);
        var emoteOwners = BuildEffectiveEmoteOwners(enabledMods, resolvedMap);

        foreach (var modEntry in enabledMods)
        {
            var modDirName = modEntry.ModDirName;
            var settingsDict = modEntry.Settings;
            var matchingStates = modEntry.States;
            var forceRestoreMod = pendingModSettings.Contains(modDirName)
                || IsForceRestoreConfigured(key, modDirName)
                || _configService.Current.ModLearningForceRestorePersistent;
            var allowActiveDbBackfill = true;
            _logger.LogTrace("[ModLearning] Restore mod={mod} enabled={enabled} settingsKeys={settingsCount}", modDirName, true, settingsDict.Count);
            var modKey = modDirName.ToLowerInvariant();

            var scdLinksMap = GetScdLinksForStates(matchingStates);
            var modResourceLinksSnapshot = GetModResourceLinksSnapshot(modDirName);
            var hasMinionState = matchingStates.Any(state =>
                state.Fragments.TryGetValue(ObjectKind.MinionOrMount, out var minionFragment) &&
                FragmentHasAnyReplacement(minionFragment));
            if (hasMinionState && modResourceLinksSnapshot.Count > 0)
            {
                var removedPlayerScd = RemovePlayerScdLinkedToMinionMod(apiData, modResourceLinksSnapshot);
                if (removedPlayerScd > 0)
                {
                    modified = true;
                    _logger.LogDebug("[ModLearning] Removed player SCD entries linked to minion mod: mod={mod} removed={count}",
                        modDirName, removedPlayerScd);
                }
            }
            var parentStatus = GetParentEffectiveness(matchingStates, resolvedMap, jobId);
            var overriddenParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (parentStatus.Count > 0)
            {
                foreach (var parentKey in parentStatus.Keys.ToList())
                {
                    if (TryGetEmoteNameForPap(parentKey, matchingStates, out var emoteName) &&
                        emoteOwners.TryGetValue(emoteName, out var owners) &&
                        owners.Count > 0 && !owners.Contains(modDirName))
                    {
                        parentStatus[parentKey] = false;
                        overriddenParents.Add(parentKey);
                        _logger.LogTrace("[ModLearning] Emote override: mod={mod} emote={emote} owners={owners} pap={pap}",
                            modDirName, emoteName, string.Join(", ", owners), parentKey);
                    }
                }
            }

            var effectiveParentPathsForScdLinks = parentStatus.Where(p => p.Value)
                .Select(p => p.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var scdWithEffectiveParents = GetScdWithEffectiveParents(scdLinksMap, effectiveParentPathsForScdLinks);
            var scdToRemove = GetScdToRemove(scdLinksMap, parentStatus, scdWithEffectiveParents);
            var modScdHashes = GetScdHashes(matchingStates);
            _logger.LogTrace("[ModLearning] SCD link snapshot: mod={mod} parents={parents} effectiveParents={effective} scdEffective={scdEffective} scdToRemove={scdRemove} modScdHashes={hashCount} linkParents={linkParents}",
                modDirName, parentStatus.Count, effectiveParentPathsForScdLinks.Count, scdWithEffectiveParents.Count, scdToRemove.Count, modScdHashes.Count, scdLinksMap.Count);

            var removedLinkedTotal = 0;
            HashSet<string>? scdToRemoveAll = null;
            var scdToRemoveEmote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (overriddenParents.Count > 0)
            {
                foreach (var parent in overriddenParents)
                {
                    if (!scdLinksMap.TryGetValue(parent, out var scds)) continue;
                    foreach (var scd in scds)
                    {
                        scdToRemoveEmote.Add(scd);
                    }
                }
            }
            if (effectiveParentPathsForScdLinks.Count == 0 && scdLinksMap.Count > 0)
            {
                scdToRemoveAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var scds in scdLinksMap.Values)
                {
                    foreach (var scd in scds)
                    {
                        scdToRemoveAll.Add(scd);
                    }
                }
            }
            var blockedScdToRestore = new HashSet<string>(scdToRemove, StringComparer.OrdinalIgnoreCase);
            if (scdToRemoveAll != null)
            {
                foreach (var scd in scdToRemoveAll)
                {
                    blockedScdToRestore.Add(NormalizePathString(scd));
                }
            }
            foreach (var scd in scdToRemoveEmote)
            {
                blockedScdToRestore.Add(NormalizePathString(scd));
            }

            if (scdToRemove.Count > 0 || (scdToRemoveAll != null && scdToRemoveAll.Count > 0) || scdToRemoveEmote.Count > 0)
            {
                if (modResourceLinksSnapshot.Count > 0)
                {
                    var scdFromResourceLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (scdToRemoveAll != null && scdToRemoveAll.Count > 0)
                    {
                        foreach (var scdSet in modResourceLinksSnapshot.Values)
                        {
                            foreach (var scd in scdSet)
                            {
                                scdFromResourceLinks.Add(scd);
                            }
                        }
                    }
                    else if (scdToRemove.Count > 0)
                    {
                        foreach (var parent in scdLinksMap.Keys)
                        {
                            if (!modResourceLinksSnapshot.TryGetValue(parent, out var scds)) continue;
                            foreach (var scd in scds)
                            {
                                scdFromResourceLinks.Add(scd);
                            }
                        }
                    }

                    if (scdFromResourceLinks.Count > 0)
                    {
                        foreach (var targetList in apiData.FileReplacements.Values)
                        {
                            removedLinkedTotal += RemoveLinkedScdByGamePathsIgnoreHash(targetList, scdFromResourceLinks);
                        }
                    }
                }

                if (scdToRemoveEmote.Count > 0)
                {
                    foreach (var targetList in apiData.FileReplacements.Values)
                    {
                        removedLinkedTotal += RemoveLinkedScdByGamePathsIgnoreHash(targetList, scdToRemoveEmote);
                    }
                }

                if (scdToRemoveAll != null && scdToRemoveAll.Count > 0)
                {
                    foreach (var targetList in apiData.FileReplacements.Values)
                    {
                        removedLinkedTotal += RemoveLinkedScdByGamePathsIgnoreHash(targetList, scdToRemoveAll);
                    }
                }

                var targetScdPaths = apiData.FileReplacements.Values
                    .SelectMany(l => l)
                    .SelectMany(e => e.GamePaths)
                    .Where(p => p.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                    .Select(NormalizePathString)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var scdRemovalSet = blockedScdToRestore;
                var missingScd = scdRemovalSet.Where(p => !targetScdPaths.Contains(p)).ToList();
                _logger.LogTrace("[ModLearning] SCD remove check: mod={mod} scdToRemove={scdRemove} targetScd={targetScd} missingScd={missing} missingSample={sample}",
                    modDirName, scdRemovalSet.Count, targetScdPaths.Count, missingScd.Count, string.Join(", ", missingScd.Take(5)));
                foreach (var targetList in apiData.FileReplacements.Values)
                {
                    removedLinkedTotal += RemoveLinkedScdByGamePaths(targetList, blockedScdToRestore, modScdHashes);
                }
                    if (removedLinkedTotal > 0)
                {
                    modified = true;
                        _logger.LogTrace("[ModLearning] Removed linked SCDs by parent override: mod={mod} removed={removed}", modDirName, removedLinkedTotal);
                }
                else
                {
                    var totalEntries = apiData.FileReplacements.Values.Sum(l => l.Count);
                    _logger.LogTrace("[ModLearning] No linked SCDs removed: mod={mod} targetKinds={kinds} targetEntries={entries} scdToRemoveSample={sample}",
                        modDirName, apiData.FileReplacements.Count, totalEntries, string.Join(", ", scdToRemove.Take(5)));
                }
            }

            var modRestoredCount = 0;
            var skippedNotEffective = 0;
            var skippedEmoteOverride = 0;
            var skippedScdParentOverride = 0;
            var skippedParentBaseline = 0;
            var skippedScdBaseline = 0;
            var skippedMissingFile = 0;
            var skippedExistingHash = 0;
            foreach (var matchingState in matchingStates)
            {
                var overriddenEmotesForState = GetOverriddenEmotesForState(matchingState, modDirName, emoteOwners);
                if (overriddenEmotesForState.Count > 0)
                {
                    var removedFromState = RemoveStateReplacementsFromCharacterData(apiData, matchingState, jobId);
                    if (removedFromState > 0)
                    {
                        modified = true;
                    }
                    skippedEmoteOverride += Math.Max(1, removedFromState);
                    _logger.LogTrace("[ModLearning] Skip state due to overridden emote: mod={mod} emotes={emotes} removed={removed}",
                        modDirName, string.Join(", ", overriddenEmotesForState), removedFromState);
                    continue;
                }
                foreach (var fragmentKvp in matchingState.Fragments)
                {
                 var kind = fragmentKvp.Key;
                 var fragment = fragmentKvp.Value;

                if ((fragment.FileReplacements == null || fragment.FileReplacements.Count == 0)
                    && (fragment.JobFileReplacements == null || fragment.JobFileReplacements.Count == 0)) continue;

                 if (!apiData.FileReplacements.ContainsKey(kind))
                 {
                     apiData.FileReplacements[kind] = [];
                 }

                 var targetList = apiData.FileReplacements[kind];
                 var existingHashes = targetList.Select(x => x.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var addedLinked = AddLinkedScdReplacements(targetList, fragment, scdWithEffectiveParents, resolvedMap, existingHashes);
                    if (addedLinked > 0)
                    {
                        modRestoredCount += addedLinked;
                        modified = true;
                    }

                    foreach (var replacement in GetEffectiveReplacements(fragment, jobId))
                    {
                    var isEffective = IsReplacementEffective(replacement, resolvedMap)
                        || IsScdLinkedToEffectiveParent(replacement, scdWithEffectiveParents);
                    if (!isEffective && !forceRestoreMod && !allowActiveDbBackfill)
                    {
                        skippedNotEffective++;
                        _logger.LogTrace("[ModLearning] Skip non-effective replacement: mod={mod} kind={kind} hash={hash} paths={paths}",
                            modDirName, kind, replacement.Hash, string.Join(", ", replacement.GamePaths));
                        var removed = RemoveFromTargetList(targetList, replacement);
                        if (removed)
                        {
                            modified = true;
                            _logger.LogTrace("[ModLearning] Removed non-effective replacement: {path} (Hash: {hash})", replacement.ResolvedPath, replacement.Hash);
                        }
                        if (IsTrackedParentReplacement(replacement))
                        {
                            var linkedScd = GetLinkedScdGamePaths(modKey, replacement);
                            if (linkedScd.Count > 0)
                            {
                                var removedLinked = RemoveLinkedScdReplacements(targetList, fragment, jobId, linkedScd, scdWithEffectiveParents, modScdHashes);
                                if (removedLinked > 0)
                                {
                                    _logger.LogDebug("[ModLearning] Removed linked SCDs: mod={mod} parentPaths={parents} removed={count}",
                                        modKey, string.Join(", ", replacement.GamePaths), removedLinked);
                                    modified = true;
                                }
                            }
                            else
                            {
                                _logger.LogDebug("[ModLearning] No linked SCDs for parent: mod={mod} parents={parents}", modKey, string.Join(", ", replacement.GamePaths));
                            }
                        }
                        continue;
                    }

                    if (IsTrackedParentReplacement(replacement) && TryGetEmoteNameForPapFromReplacement(replacement, matchingStates, out var emoteName) &&
                        emoteOwners.TryGetValue(emoteName, out var emoteOwnerSet) && emoteOwnerSet.Count > 0 &&
                        !emoteOwnerSet.Contains(modDirName))
                    {
                        skippedEmoteOverride++;
                        var removed = RemoveFromTargetList(targetList, replacement);
                        if (removed)
                        {
                            _logger.LogTrace("[ModLearning] Removed emote-overridden parent: mod={mod} emote={emote} owners={owners} paths={paths}",
                                modDirName, emoteName, string.Join(", ", emoteOwnerSet), string.Join(", ", replacement.GamePaths));
                            modified = true;
                        }
                        continue;
                    }

                    if (IsScdReplacement(replacement) && blockedScdToRestore.Count > 0 &&
                        replacement.GamePaths.Any(p => blockedScdToRestore.Contains(NormalizePathString(p))))
                    {
                        skippedScdParentOverride++;
                        _logger.LogTrace("[ModLearning] Skip re-add SCD due to parent override: mod={mod} paths={paths}",
                            modDirName, string.Join(", ", replacement.GamePaths));
                        continue;
                    }

                    if (!forceRestoreMod && !allowActiveDbBackfill && IsTrackedParentReplacement(replacement) &&
                        !replacement.GamePaths.Any(p => baselineParentPaths.Contains(NormalizePathString(p))))
                    {
                        skippedParentBaseline++;
                        _logger.LogTrace("[ModLearning] Skip parent not in baseline: mod={mod} kind={kind} hash={hash} paths={paths}",
                            modDirName, kind, replacement.Hash, string.Join(", ", replacement.GamePaths));
                        continue;
                    }

                    if (!forceRestoreMod && !allowActiveDbBackfill && IsScdReplacement(replacement) && scdLinksMap.Count > 0)
                    {
                        var linkedParents = scdLinksMap.Where(k => k.Value.Any(s => replacement.GamePaths.Any(p => string.Equals(NormalizePathString(p), NormalizePathString(s), StringComparison.OrdinalIgnoreCase))))
                            .Select(k => k.Key)
                            .ToList();
                        if (linkedParents.Count > 0 && !linkedParents.Any(p => baselineParentPaths.Contains(p)))
                        {
                            skippedScdBaseline++;
                            _logger.LogTrace("[ModLearning] Skip SCD linked parents not in baseline: mod={mod} hash={hash} paths={paths}",
                                modDirName, replacement.Hash, string.Join(", ", replacement.GamePaths));
                            continue;
                        }
                    }

                    var canAdd = replacement.IsFileSwap || File.Exists(replacement.ResolvedPath);
                    if (!existingHashes.Contains(replacement.Hash) && canAdd)
                    {
                        RemoveGamePathsFromOtherEntries(targetList, replacement.GamePaths);
                        targetList.Add(replacement.ToFileReplacementDto());
                        existingHashes.Add(replacement.Hash);
                        modRestoredCount++;
                        modified = true;
                        _logger.LogTrace("[ModLearning] Restored missing file: {path} (Hash: {hash})", replacement.ResolvedPath, replacement.Hash);
                        _logger.LogTrace("[ModLearning] Restore add: mod={mod} kind={kind} hash={hash}", modDirName, kind, replacement.Hash);
                    }
                    else
                    {
                        if (!canAdd)
                        {
                            skippedMissingFile++;
                            _logger.LogTrace("[ModLearning] Skip missing file on disk: mod={mod} kind={kind} hash={hash} path={path}",
                                modDirName, kind, replacement.Hash, replacement.ResolvedPath);
                        }
                        else if (existingHashes.Contains(replacement.Hash))
                        {
                            if (MergeGamePathsByHash(targetList, replacement))
                            {
                                modRestoredCount++;
                                modified = true;
                                _logger.LogTrace("[ModLearning] Restore merge by hash: mod={mod} kind={kind} hash={hash} paths={paths}",
                                    modDirName, kind, replacement.Hash, string.Join(", ", replacement.GamePaths));
                            }
                            else
                            {
                                skippedExistingHash++;
                                _logger.LogTrace("[ModLearning] Skip already present hash: mod={mod} kind={kind} hash={hash}", modDirName, kind, replacement.Hash);
                            }
                        }
                    }
                    }
                }
            }
            if (modRestoredCount > 0 || skippedNotEffective > 0 || skippedEmoteOverride > 0 || skippedScdParentOverride > 0 || skippedParentBaseline > 0 || skippedScdBaseline > 0 || skippedMissingFile > 0 || skippedExistingHash > 0)
            {
                _logger.LogDebug("[ModLearning] Restore summary: mod={mod} added={added} skipNotEffective={notEff} skipEmote={emote} skipScdOverride={scdOverride} skipParentBaseline={parentBase} skipScdBaseline={scdBase} skipMissingFile={missingFile} skipExistingHash={existingHash}",
                    modDirName, modRestoredCount, skippedNotEffective, skippedEmoteOverride, skippedScdParentOverride, skippedParentBaseline, skippedScdBaseline, skippedMissingFile, skippedExistingHash);
            }
        }
        _logger.LogTrace("[ModLearning] Restore complete: modified={modified}", modified);
        return modified;
    }

    private static HashSet<string> GetOverriddenEmotesForState(
        LearnedModState state,
        string modDirName,
        Dictionary<string, HashSet<string>> emoteOwners)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.PapEmotes == null || state.PapEmotes.Count == 0) return result;
        foreach (var entry in state.PapEmotes)
        {
            var emoteNames = entry.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var emoteName in emoteNames)
            {
                if (!emoteOwners.TryGetValue(emoteName, out var owners) || owners.Count == 0) continue;
                if (owners.Any(owner => !string.Equals(owner, modDirName, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(emoteName);
                }
            }
        }
        return result;
    }

    private static int RemoveStateReplacementsFromCharacterData(
        Sphene.API.Data.CharacterData apiData,
        LearnedModState state,
        uint jobId)
    {
        var removed = 0;
        foreach (var fragmentKvp in state.Fragments)
        {
            var kind = fragmentKvp.Key;
            var fragment = fragmentKvp.Value;
            if (!apiData.FileReplacements.TryGetValue(kind, out var targetList) || targetList == null || targetList.Count == 0)
            {
                continue;
            }
            foreach (var replacement in GetEffectiveReplacements(fragment, jobId))
            {
                if (RemoveFromTargetList(targetList, replacement))
                {
                    removed++;
                }
            }
        }
        return removed;
    }

    private static int RemoveLinkedScdReplacements(List<FileReplacementData> targetList, ModFileFragment fragment, uint jobId, HashSet<string> linkedScdGamePaths, HashSet<string> scdWithEffectiveParents, HashSet<string> modScdHashes)
    {
        var removedCount = 0;
        foreach (var replacement in GetEffectiveReplacements(fragment, jobId))
        {
            if (!IsScdReplacement(replacement)) continue;
            if (!replacement.GamePaths.Any(p => linkedScdGamePaths.Contains(NormalizePathString(p)))) continue;
            if (replacement.GamePaths.Any(p => scdWithEffectiveParents.Contains(NormalizePathString(p)))) continue;
            if (modScdHashes.Count > 0 && !modScdHashes.Contains(replacement.Hash)) continue;
            if (RemoveFromTargetList(targetList, replacement))
            {
                removedCount++;
            }
        }
        return removedCount;
    }

    private static int RemoveLinkedScdByGamePaths(List<FileReplacementData> targetList, HashSet<string> scdToRemove, HashSet<string> modScdHashes)
    {
        if (scdToRemove.Count == 0 || targetList.Count == 0) return 0;
        var removed = 0;
        for (var i = targetList.Count - 1; i >= 0; i--)
        {
            var entry = targetList[i];
            if (modScdHashes.Count > 0 && !modScdHashes.Contains(entry.Hash)) continue;
            if (entry.GamePaths.Any(p => scdToRemove.Contains(NormalizePathString(p))))
            {
                targetList.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    private static int RemoveLinkedScdByGamePathsIgnoreHash(List<FileReplacementData> targetList, HashSet<string> scdToRemove)
    {
        if (scdToRemove.Count == 0 || targetList.Count == 0) return 0;
        var removed = 0;
        for (var i = targetList.Count - 1; i >= 0; i--)
        {
            var entry = targetList[i];
            if (entry.GamePaths.Any(p => scdToRemove.Contains(NormalizePathString(p))))
            {
                targetList.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    private static int RemovePlayerScdLinkedToMinionMod(Sphene.API.Data.CharacterData apiData, Dictionary<string, HashSet<string>> modResourceLinksSnapshot)
    {
        if (!apiData.FileReplacements.TryGetValue(ObjectKind.Player, out var playerList) || playerList == null || playerList.Count == 0)
        {
            return 0;
        }

        var linkedScd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scdSet in modResourceLinksSnapshot.Values)
        {
            foreach (var scd in scdSet)
            {
                if (scd.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                {
                    linkedScd.Add(NormalizePathString(scd));
                }
            }
        }
        if (linkedScd.Count == 0) return 0;

        var removed = 0;
        for (var i = playerList.Count - 1; i >= 0; i--)
        {
            var entry = playerList[i];
            if (entry.GamePaths.Any(p => linkedScd.Contains(NormalizePathString(p))))
            {
                playerList.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    private static int AddLinkedScdReplacements(List<FileReplacementData> targetList, ModFileFragment fragment, HashSet<string> scdWithEffectiveParents, Dictionary<string, string> resolvedMap, HashSet<string> existingHashes)
    {
        if (scdWithEffectiveParents.Count == 0) return 0;
        var changed = 0;
        foreach (var replacement in GetAllReplacements(fragment))
        {
            if (!IsScdReplacement(replacement)) continue;
            if (!replacement.GamePaths.Any(p => scdWithEffectiveParents.Contains(NormalizePathString(p)))) continue;
            if (!IsReplacementEffective(replacement, resolvedMap) && !IsScdLinkedToEffectiveParent(replacement, scdWithEffectiveParents)) continue;
            if (!existingHashes.Contains(replacement.Hash))
            {
                RemoveGamePathsFromOtherEntries(targetList, replacement.GamePaths);
                targetList.Add(replacement.ToFileReplacementDto());
                existingHashes.Add(replacement.Hash);
                changed++;
            }
            else if (MergeGamePathsByHash(targetList, replacement))
            {
                changed++;
            }
        }
        return changed;
    }

    private static bool IsScdLinkedToEffectiveParent(FileReplacement replacement, HashSet<string> scdWithEffectiveParents)
    {
        if (!IsScdReplacement(replacement)) return false;
        return replacement.GamePaths.Any(p => scdWithEffectiveParents.Contains(NormalizePathString(p)));
    }

    private static bool MergeGamePathsByHash(List<FileReplacementData> targetList, FileReplacement replacement)
    {
        if (string.IsNullOrWhiteSpace(replacement.Hash)) return false;
        var existing = targetList.FirstOrDefault(x => string.Equals(x.Hash, replacement.Hash, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return false;

        RemoveGamePathsFromOtherEntries(targetList, replacement.GamePaths, existing);

        var mergedPaths = (existing.GamePaths ?? [])
            .Select(NormalizePathString)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var gp in replacement.GamePaths)
        {
            if (mergedPaths.Add(NormalizePathString(gp)))
            {
                changed = true;
            }
        }

        if (changed)
        {
            existing.GamePaths = [.. mergedPaths];
        }

        return changed;
    }

    private static void RemoveGamePathsFromOtherEntries(List<FileReplacementData> targetList, IEnumerable<string> gamePaths, FileReplacementData? keepEntry = null)
    {
        var normalizedPaths = gamePaths
            .Select(NormalizePathString)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedPaths.Count == 0) return;

        for (var i = targetList.Count - 1; i >= 0; i--)
        {
            var entry = targetList[i];
            if (ReferenceEquals(entry, keepEntry)) continue;

            var existingPaths = (entry.GamePaths ?? [])
                .Select(NormalizePathString)
                .ToList();
            if (existingPaths.Count == 0) continue;

            var filtered = existingPaths
                .Where(p => !normalizedPaths.Contains(p))
                .ToArray();
            if (filtered.Length == existingPaths.Count) continue;

            if (filtered.Length == 0)
            {
                targetList.RemoveAt(i);
                continue;
            }

            entry.GamePaths = filtered;
        }
    }

    private static IEnumerable<FileReplacement> GetAllReplacements(ModFileFragment fragment)
    {
        fragment.FileReplacements ??= [];
        fragment.JobFileReplacements ??= [];
        var results = new HashSet<FileReplacement>();
        foreach (var replacement in fragment.FileReplacements)
        {
            results.Add(replacement);
        }
        foreach (var jobSet in fragment.JobFileReplacements.Values)
        {
            if (jobSet == null) continue;
            foreach (var replacement in jobSet)
            {
                results.Add(replacement);
            }
        }
        return results;
    }

    private static bool FragmentHasAnyReplacement(ModFileFragment? fragment)
    {
        if (fragment == null) return false;
        if (fragment.FileReplacements != null && fragment.FileReplacements.Count > 0) return true;
        if (fragment.JobFileReplacements == null || fragment.JobFileReplacements.Count == 0) return false;
        return fragment.JobFileReplacements.Values.Any(set => set != null && set.Count > 0);
    }

    private static Dictionary<string, bool> GetParentEffectiveness(IEnumerable<LearnedModState> states, Dictionary<string, string> resolvedMap, uint jobId)
    {
        var status = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            foreach (var fragment in state.Fragments.Values)
            {
                foreach (var replacement in GetEffectiveReplacements(fragment, jobId))
                {
                    if (!IsTrackedParentReplacement(replacement)) continue;
                    var isEffective = IsReplacementEffective(replacement, resolvedMap);
                    foreach (var gp in replacement.GamePaths)
                    {
                        if (!gp.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) &&
                            !gp.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)) continue;
                        var key = NormalizePathString(gp);
                        if (!status.TryGetValue(key, out var current) || !current)
                        {
                            status[key] = isEffective;
                        }
                    }
                }
            }
        }
        return status;
    }

    private static HashSet<string> GetScdHashes(IEnumerable<LearnedModState> states)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            foreach (var fragment in state.Fragments.Values)
            {
                foreach (var replacement in GetAllReplacements(fragment))
                {
                    if (!IsScdReplacement(replacement)) continue;
                    hashes.Add(replacement.Hash);
                }
            }
        }
        return hashes;
    }

    private static HashSet<string> GetScdWithEffectiveParents(Dictionary<string, HashSet<string>> scdLinksMap, HashSet<string> effectiveParentPaths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parent in effectiveParentPaths)
        {
            if (!scdLinksMap.TryGetValue(parent, out var scds)) continue;
            foreach (var scd in scds)
            {
                result.Add(scd);
            }
        }
        return result;
    }

    private static HashSet<string> GetScdToRemove(Dictionary<string, HashSet<string>> scdLinksMap, Dictionary<string, bool> parentStatus, HashSet<string> scdWithEffectiveParents)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in parentStatus)
        {
            if (kvp.Value) continue;
            if (!scdLinksMap.TryGetValue(kvp.Key, out var scds)) continue;
            foreach (var scd in scds)
            {
                if (scdWithEffectiveParents.Contains(scd)) continue;
                result.Add(scd);
            }
        }
        return result;
    }

    private Dictionary<string, HashSet<string>> GetScdLinksForStates(IEnumerable<LearnedModState> states)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            if (state.ScdLinks == null || state.ScdLinks.Count == 0)
            {
                if (_modResourceLinks.TryGetValue(state.ModDirectoryName.ToLowerInvariant(), out var runtimeMap))
                {
                    foreach (var kvp in runtimeMap)
                    {
                        var parent = NormalizePathString(kvp.Key);
                        if (!map.TryGetValue(parent, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            map[parent] = set;
                        }
                        foreach (var scd in kvp.Value)
                        {
                            set.Add(NormalizePathString(scd));
                        }
                    }
                }
                continue;
            }
            foreach (var kvp in state.ScdLinks)
            {
                var parent = NormalizePathString(kvp.Key);
                if (!map.TryGetValue(parent, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[parent] = set;
                }
                foreach (var scd in kvp.Value)
                {
                    set.Add(NormalizePathString(scd));
                }
            }
        }
        return map;
    }

    private Dictionary<string, List<string>> BuildScdLinksForState(string modDirName, LearnedModState state, HashSet<string>? skipLogByMod = null)
    {
        var modKey = modDirName.ToLowerInvariant();
        Dictionary<string, HashSet<string>>? map;
        var settingsHash = ComputeSettingsHash(state.Settings);
        lock (_lock)
        {
            if (_modResourceLinksBySettings.TryGetValue(modKey, out var settingsMap) &&
                settingsMap.TryGetValue(settingsHash, out var settingsLinks))
            {
                map = settingsLinks;
            }
            else
            {
                _modResourceLinks.TryGetValue(modKey, out map);
            }
        }
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if ((map == null || map.Count == 0) && state.ScdLinks != null && state.ScdLinks.Count > 0)
        {
            map = ToScdLinksMap(state.ScdLinks);
        }
        if (map == null || map.Count == 0)
        {
            if (skipLogByMod == null || skipLogByMod.Add(modDirName))
            {
                _logger.LogTrace("[ModLearning] SCD link build skipped: mod={mod} reason=no_runtime_links", modDirName);
            }
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var fragment in state.Fragments.Values)
        {
            foreach (var replacement in GetAllReplacements(fragment))
            {
                if (!IsTrackedParentReplacement(replacement)) continue;
                foreach (var gp in replacement.GamePaths)
                {
                    if (!gp.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) &&
                        !gp.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)) continue;
                    var parentKey = NormalizePathString(gp);
                    if (!map.TryGetValue(parentKey, out var scds) || scds.Count == 0) continue;
                    if (!result.TryGetValue(parentKey, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        result[parentKey] = set;
                    }
                    foreach (var scd in scds)
                    {
                        var normalizedScd = NormalizePathString(scd);
                        set.Add(normalizedScd);
                    }
                }
            }
        }

        var output = result.ToDictionary(k => k.Key, v => v.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        _logger.LogTrace("[ModLearning] SCD link build: mod={mod} parents={parents} scds={scds}",
            modDirName, output.Count, output.Values.Sum(v => v.Count));
        return output;
    }

    private Dictionary<string, string> BuildPapEmotesForState(LearnedModState state)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var map = _papEmoteMap.Value;
        if (map.Count == 0) return result;

        foreach (var fragment in state.Fragments.Values)
        {
            foreach (var replacement in GetAllReplacements(fragment))
            {
                foreach (var gp in replacement.GamePaths)
                {
                    if (!gp.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)) continue;
                    var key = Path.GetFileName(gp).ToLowerInvariant();
                    if (!map.TryGetValue(key, out var names) || names.Count == 0) continue;
                    var name = string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    result[NormalizePathString(gp)] = name;
                }
            }
        }

        return result;
    }

    private bool IsEmoteParentReplacement(FileReplacement replacement)
    {
        var map = _papEmoteMap.Value;
        if (map.Count == 0) return false;
        foreach (var gp in replacement.GamePaths)
        {
            if (!gp.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)) continue;
            var key = Path.GetFileName(gp).ToLowerInvariant();
            if (map.TryGetValue(key, out var names) && names.Count > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static void PromoteEmoteLinkedReplacementsToGlobal(LearnedModState state)
    {
        if (state.PapEmotes == null || state.PapEmotes.Count == 0) return;
        var emoteParents = state.PapEmotes.Keys
            .Select(NormalizePathString)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (emoteParents.Count == 0) return;

        var emoteScd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in state.ScdLinks)
        {
            if (!emoteParents.Contains(NormalizePathString(kvp.Key))) continue;
            foreach (var scd in kvp.Value)
            {
                emoteScd.Add(NormalizePathString(scd));
            }
        }

        foreach (var fragment in state.Fragments.Values)
        {
            var toPromote = GetAllReplacements(fragment)
                .Where(replacement =>
                    replacement.GamePaths.Any(p => emoteParents.Contains(NormalizePathString(p))) ||
                    replacement.GamePaths.Any(p => emoteScd.Contains(NormalizePathString(p))))
                .Distinct()
                .ToList();
            foreach (var replacement in toPromote)
            {
                RemoveConflictingReplacements(fragment, replacement);
                AddOrElevateJobReplacement(fragment, 0, replacement, true);
            }
        }
    }

    private Dictionary<string, List<string>> BuildPapEmoteMap()
    {
        try
        {
            var sheet = _gameData.GetExcelSheet<Emote>(_gameData.Language);
            if (sheet == null) return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var emote in sheet.Where(n => n.Name.ByteLength > 0))
            {
                var name = emote.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;

                foreach (var timeline in emote.ActionTimeline.Where(t => t.RowId != 0 && t.ValueNullable.HasValue).Select(t => t.Value))
                {
                    var key = timeline.Key.ExtractText();
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    var papKey = $"{Path.GetFileName(key)}.pap";
                    AddEmoteKey(papKey, name);
                }
            }

            var sit = sheet.GetRow(50);
            if (sit.RowId != 0) AddSpecialSit(sit);
            var sitOnGround = sheet.GetRow(52);
            if (sitOnGround.RowId != 0) AddSpecialSitOnGround(sitOnGround);
            var doze = sheet.GetRow(13);
            if (doze.RowId != 0) AddSpecialDoze(doze);

            return map.ToDictionary(k => k.Key, v => v.Value.ToList(), StringComparer.OrdinalIgnoreCase);

            void AddEmoteKey(string? key, string emoteName)
            {
                if (string.IsNullOrWhiteSpace(key)) return;
                key = key.ToLowerInvariant();
                if (!map.TryGetValue(key, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[key] = set;
                }
                set.Add(emoteName);
            }

            void AddSpecialSit(Emote emote)
            {
                var name = emote.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) return;
                AddEmoteKey("s_pose01_loop.pap", name);
                AddEmoteKey("s_pose02_loop.pap", name);
                AddEmoteKey("s_pose03_loop.pap", name);
                AddEmoteKey("s_pose04_loop.pap", name);
                AddEmoteKey("s_pose05_loop.pap", name);
            }

            void AddSpecialSitOnGround(Emote emote)
            {
                var name = emote.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) return;
                AddEmoteKey("j_pose01_loop.pap", name);
                AddEmoteKey("j_pose02_loop.pap", name);
                AddEmoteKey("j_pose03_loop.pap", name);
                AddEmoteKey("j_pose04_loop.pap", name);
            }

            void AddSpecialDoze(Emote emote)
            {
                var name = emote.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) return;
                AddEmoteKey("l_pose01_loop.pap", name);
                AddEmoteKey("l_pose02_loop.pap", name);
                AddEmoteKey("l_pose03_loop.pap", name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ModLearning] Failed to build pap emote map");
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, HashSet<string>> BuildEffectiveEmoteOwners(
        IEnumerable<(string ModDirName, Dictionary<string, List<string>> Settings, List<LearnedModState> States)> mods,
        Dictionary<string, string> resolvedMap)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var modEntry in mods)
        {
            var modName = modEntry.ModDirName;
            foreach (var state in modEntry.States)
            {
                foreach (var fragment in state.Fragments.Values)
                {
                    foreach (var replacement in GetAllReplacements(fragment))
                    {
                        if (!IsTrackedParentReplacement(replacement)) continue;
                        if (!IsReplacementEffective(replacement, resolvedMap)) continue;
                        foreach (var gp in replacement.GamePaths)
                        {
                            if (!gp.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)) continue;
                            var normalized = NormalizePathString(gp);
                            if (!state.PapEmotes.TryGetValue(normalized, out var emoteName) ||
                                string.IsNullOrWhiteSpace(emoteName)) continue;
                            if (!owners.TryGetValue(emoteName, out var set))
                            {
                                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                owners[emoteName] = set;
                            }
                            set.Add(modName);
                        }
                    }
                }
            }
        }
        return owners;
    }

    private static bool TryGetEmoteNameForPap(string papPath, IEnumerable<LearnedModState> states, out string emoteName)
    {
        var normalized = NormalizePathString(papPath);
        foreach (var state in states)
        {
            if (state.PapEmotes != null && state.PapEmotes.TryGetValue(normalized, out var name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                emoteName = name;
                return true;
            }
        }
        emoteName = string.Empty;
        return false;
    }

    private static bool TryGetEmoteNameForPapFromReplacement(FileReplacement replacement, IEnumerable<LearnedModState> states, out string emoteName)
    {
        foreach (var gp in replacement.GamePaths)
        {
            if (!gp.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)) continue;
            if (TryGetEmoteNameForPap(gp, states, out emoteName))
            {
                return true;
            }
        }
        emoteName = string.Empty;
        return false;
    }

    private static Dictionary<string, HashSet<string>> ToScdLinksMap(Dictionary<string, List<string>> links)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in links)
        {
            var parent = NormalizePathString(kvp.Key);
            if (!map.TryGetValue(parent, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                map[parent] = set;
            }
            foreach (var scd in kvp.Value)
            {
                set.Add(NormalizePathString(scd));
            }
        }
        return map;
    }

    private Dictionary<string, HashSet<string>> GetModResourceLinksSnapshot(string modDirName)
    {
        lock (_lock)
        {
            if (!_modResourceLinks.TryGetValue(modDirName, out var map))
            {
                return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            }

            var snapshot = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in map)
            {
                snapshot[kvp.Key] = new HashSet<string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
            }
            return snapshot;
        }
    }

    private async Task<Dictionary<string, string>> ResolveEffectivePathsAsync(List<LearnedModState> states)
    {
        var gamePathList = new List<string>();
        var gamePathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            foreach (var fragmentEntry in state.Fragments)
            {
                var kind = fragmentEntry.Key;
                var fragment = fragmentEntry.Value;
                foreach (var replacement in GetAllReplacements(fragment))
                {
                    foreach (var gp in replacement.GamePaths)
                    {
                        if (!IsTrackedExtForKind(kind, Path.GetExtension(gp))) continue;
                        if (gamePathSet.Add(gp))
                        {
                            gamePathList.Add(gp);
                        }
                    }
                }
            }
        }

        if (gamePathList.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var (resolved, _) = await _penumbra.ResolvePathsAsync(gamePathList.ToArray(), []).ConfigureAwait(false);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < gamePathList.Count && i < resolved.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(resolved[i]))
                {
                    map[NormalizePathString(gamePathList[i])] = NormalizePathString(resolved[i]);
                }
            }
            return map;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[ModLearning] Failed to resolve effective paths.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsReplacementEffective(FileReplacement replacement, Dictionary<string, string> resolvedMap)
    {
        if (replacement.GamePaths.Count == 0) return false;
        var normalizedResolved = NormalizePathString(replacement.ResolvedPath);
        foreach (var gp in replacement.GamePaths)
        {
            var normalizedGp = NormalizePathString(gp);
            if (resolvedMap.TryGetValue(normalizedGp, out var resolved) &&
                string.Equals(resolved, normalizedResolved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool RemoveFromTargetList(List<FileReplacementData> targetList, FileReplacement replacement)
    {
        if (targetList.Count == 0) return false;
        var removed = false;
        var replacementPaths = replacement.GamePaths.Select(NormalizePathString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = targetList.Count - 1; i >= 0; i--)
        {
            var entry = targetList[i];
            if (!string.Equals(entry.Hash, replacement.Hash, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.GamePaths.Any(p => replacementPaths.Contains(NormalizePathString(p))))
            {
                targetList.RemoveAt(i);
                removed = true;
            }
        }
        return removed;
    }

    private static string NormalizePathString(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }

    private static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (path.StartsWith("|", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split("|");
            if (parts.Length >= 3)
            {
                path = parts[2];
            }
        }
        return path.Replace('\\', '/').ToLowerInvariant();
    }

    private bool AddResourceLink(string modName, string parentGamePath, string scdGamePath)
    {
        if (!_modResourceLinks.TryGetValue(modName, out var map))
        {
            map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            _modResourceLinks[modName] = map;
        }

        if (!map.TryGetValue(parentGamePath, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            map[parentGamePath] = set;
        }
        return set.Add(scdGamePath);
    }

    private void AddResourceLinkForSettings(string modName, string settingsHash, string parentGamePath, string scdGamePath)
    {
        if (!_modResourceLinksBySettings.TryGetValue(modName, out var settingsMap))
        {
            settingsMap = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            _modResourceLinksBySettings[modName] = settingsMap;
        }

        if (!settingsMap.TryGetValue(settingsHash, out var map))
        {
            map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            settingsMap[settingsHash] = map;
        }

        if (!map.TryGetValue(parentGamePath, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            map[parentGamePath] = set;
        }

        set.Add(scdGamePath);
    }

    private string? TryGetSettingsHashForMod(string modDirName)
    {
        if (!_penumbra.APIAvailable) return null;
        var (valid, _, collection) = _penumbra.GetCollectionForObject(0);
        if (!valid) return null;

        var (ec, settingsTuple) = _penumbra.GetModSettings(collection.Id, modDirName);
        if (ec != Penumbra.Api.Enums.PenumbraApiEc.Success || settingsTuple == null) return null;

        var (enabled, _, settings, _) = settingsTuple.Value;
        if (!enabled) return null;

        var settingsDict = settings.ToDictionary(k => k.Key, k => k.Value.ToList(), StringComparer.Ordinal);
        return ComputeSettingsHash(settingsDict);
    }

    private HashSet<string> GetLinkedScdGamePaths(string modName, FileReplacement replacement)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_modResourceLinks.TryGetValue(modName, out var map)) return result;
        foreach (var gp in replacement.GamePaths)
        {
            var key = NormalizePathString(gp);
            if (map.TryGetValue(key, out var set))
            {
                foreach (var scd in set)
                {
                    result.Add(scd);
                }
            }
        }
        return result;
    }

    private bool TryGetModNameFromPath(string filePath, out string modName)
    {
        modName = string.Empty;
        var modDir = _penumbra.ModDirectory;
        if (string.IsNullOrWhiteSpace(modDir)) return false;
        var modDirNormalized = NormalizePathString(modDir);
        if (!modDirNormalized.EndsWith("/", StringComparison.Ordinal)) modDirNormalized += "/";
        var filePathNormalized = NormalizePathString(filePath);
        if (!filePathNormalized.StartsWith(modDirNormalized, StringComparison.OrdinalIgnoreCase)) return false;
        var remainder = filePathNormalized.Substring(modDirNormalized.Length);
        var idx = remainder.IndexOf('/');
        if (idx <= 0) return false;
        modName = remainder.Substring(0, idx);
        return true;
    }

    private static bool IsTrackedExt(string extension)
    {
        return string.Equals(extension, ".scd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".pap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".avfx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".sklb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".atex", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tmb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrackedExtForKind(ObjectKind objectKind, string extension)
    {
        if (IsTrackedExt(extension)) return true;
        return objectKind == ObjectKind.MinionOrMount &&
               (string.Equals(extension, ".mdl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".mtrl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".tex", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTrackedParentExt(string extension)
    {
        return string.Equals(extension, ".pap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".avfx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrackedParentReplacement(FileReplacement replacement)
    {
        return replacement.GamePaths.Any(p =>
            p.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTrackedReplacement(FileReplacement replacement, ObjectKind objectKind)
    {
        return replacement.GamePaths.Any(p => IsTrackedExtForKind(objectKind, Path.GetExtension(p)));
    }

    private static void FilterUntrackedReplacements(ModFileFragment fragment, ObjectKind objectKind)
    {
        fragment.FileReplacements ??= [];
        fragment.JobFileReplacements ??= [];
        fragment.FileReplacements = fragment.FileReplacements.Where(r => IsTrackedReplacement(r, objectKind)).ToHashSet();
        foreach (var jobId in fragment.JobFileReplacements.Keys.ToList())
        {
            var list = fragment.JobFileReplacements[jobId];
            if (list == null) continue;
            fragment.JobFileReplacements[jobId] = list.Where(r => IsTrackedReplacement(r, objectKind)).ToHashSet();
        }
    }

    private static bool IsScdReplacement(FileReplacement replacement)
    {
        return replacement.GamePaths.Any(p => p.EndsWith(".scd", StringComparison.OrdinalIgnoreCase));
    }

    private bool HasDisabledModInSet(Guid collectionId, HashSet<string> modNames)
    {
        foreach (var modName in modNames)
        {
            var (ec, settingsTuple) = _penumbra.GetModSettings(collectionId, modName);
            if (ec != Penumbra.Api.Enums.PenumbraApiEc.Success || settingsTuple == null) continue;
            var (enabled, _, _, _) = settingsTuple.Value;
            if (!enabled)
            {
                return true;
            }
        }
        return false;
    }

    private bool IsForceRestoreConfigured(string characterKey, string modDirName)
    {
        var forced = _configService.Current.ModLearningForceRestoreMods;
        if (!forced.TryGetValue(characterKey, out var mods)) return false;
        return mods.Any(m => string.Equals(m, modDirName, StringComparison.OrdinalIgnoreCase));
    }

    private static Sphene.API.Data.CharacterData CloneCharacterData(Sphene.API.Data.CharacterData source)
    {
        return new Sphene.API.Data.CharacterData
        {
            CustomizePlusData = source.CustomizePlusData,
            FileReplacements = source.FileReplacements,
            GlamourerData = source.GlamourerData,
            HeelsData = source.HeelsData,
            HonorificData = source.HonorificData,
            ManipulationData = source.ManipulationData,
            MoodlesData = source.MoodlesData,
            PetNamesData = source.PetNamesData,
            BypassEmoteData = source.BypassEmoteData
        };
    }

    private async Task ProcessCharacterData(string key, string name, string homeWorld, uint homeWorldId, uint jobId, Dictionary<ObjectKind, List<FileReplacement>> data)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogDebug("[ModLearning] ProcessCharacterData start: key={key} jobId={jobId} kinds={kinds}", key, jobId, data.Count);
            if (!_penumbra.APIAvailable)
            {
                _logger.LogDebug("[ModLearning] Penumbra API not available.");
                return;
            }

            // Ensure profile is loaded in memory
            EnsureProfileLoaded(key, name, homeWorld);

            var modDir = _penumbra.ModDirectory;
            if (string.IsNullOrEmpty(modDir))
            {
                _logger.LogDebug("[ModLearning] Penumbra ModDirectory is empty.");
                return;
            }

            var (valid, _, collection) = _penumbra.GetCollectionForObject(0);
            if (!valid)
            {
                _logger.LogDebug("[ModLearning] Could not get collection for object 0 (LocalPlayer).");
                return;
            }
            var collectionId = collection.Id;
            _logger.LogDebug("[ModLearning] Using collection {id} ({name})", collectionId, collection.Name);
            _logger.LogTrace("[ModLearning] Processing: key={key} jobId={jobId} dataKinds={kinds}", key, jobId, data.Count);

            var changedItems = _penumbra.GetChangedItemsForCollection(collectionId);
            if (changedItems.Count > 0)
            {
                var first = changedItems.First();
                _logger.LogDebug("[ModLearning] First item: Key={key}, ValueType={type}, Value={value}", first.Key, first.Value?.GetType().Name ?? "null", first.Value);
            }
            _logger.LogTrace("[ModLearning] ChangedItems count={count}", changedItems.Count);
            var knownMods = _penumbra.GetMods();
            _logger.LogTrace("[ModLearning] Known mods count={count}", knownMods.Count);
            var changedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var changedKey in changedItems.Keys)
            {
                if (knownMods.ContainsKey(changedKey))
                {
                    changedMods.Add(changedKey);
                }
            }
            var hasChangedMods = changedMods.Count > 0;
            HashSet<string> pendingModSettings = [];
            bool restrictToPending = false;
            bool restrictToChangedItems = false;
            lock (_lock)
            {
                if (!_hasGlobalModSettingChange && _pendingModSettingChanges.Count > 0 &&
                    (DateTime.UtcNow - _lastPenumbraSettingChangeAt).TotalSeconds < 10)
                {
                    pendingModSettings = new HashSet<string>(_pendingModSettingChanges, StringComparer.OrdinalIgnoreCase);
                    restrictToPending = true;
                }
                else if (_hasGlobalModSettingChange && hasChangedMods)
                {
                    restrictToChangedItems = true;
                }
            }
            if (restrictToPending && HasDisabledModInSet(collectionId, pendingModSettings))
            {
                restrictToPending = false;
                _logger.LogDebug("[ModLearning] Pending set contains disabled mod; processing all observed replacements to capture ownership changes.");
            }

            // Group replacements by Mod Directory Name
            var replacementsByMod = new Dictionary<string, List<FileReplacement>>(StringComparer.OrdinalIgnoreCase);

            // Gather all GamePaths to resolve them in batch
            var allGamePaths = new List<string>();
            foreach (var kvp in data)
            {
                foreach (var replacement in kvp.Value)
                {
                     allGamePaths.AddRange(replacement.GamePaths);
                }
            }

            // Resolve GamePaths to Local Paths using Penumbra
            var resolvedPathsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try 
            {
                var (forwardResolved, _) = await _penumbra.ResolvePathsAsync(allGamePaths.ToArray(), []).ConfigureAwait(false);
                for (int i = 0; i < allGamePaths.Count; i++)
                {
                    if (i < forwardResolved.Length && !string.IsNullOrEmpty(forwardResolved[i]))
                    {
                        resolvedPathsMap[allGamePaths[i]] = forwardResolved[i];
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ModLearning] Failed to resolve paths via Penumbra.");
            }

            foreach (var kvp in data)
            {
                foreach (var replacement in kvp.Value)
                {
                    // Try to resolve mod from GamePaths first using Penumbra's ResolvePath
                    string? modName = null;
                    
                    // Check if we have a resolved path from Penumbra for any of the GamePaths
                    foreach (var gp in replacement.GamePaths)
                    {
                        if (resolvedPathsMap.TryGetValue(gp, out var localPath))
                        {
                            var normalizedModDir = modDir.Replace('\\', '/');
                            if (!normalizedModDir.EndsWith('/')) normalizedModDir += '/';
                            
                            var normalizedLocalPath = localPath.Replace('\\', '/');

                            if (normalizedLocalPath.StartsWith(normalizedModDir, StringComparison.OrdinalIgnoreCase))
                            {
                                var relPath = normalizedLocalPath.Substring(normalizedModDir.Length);
                                var parts = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length > 0)
                                {
                                    modName = parts[0];
                                    break; // Found the mod
                                }
                            }
                        }
                    }

                    // Fallback: Check replacement.ResolvedPath (if available and not already found)
                    if (modName == null && !string.IsNullOrEmpty(replacement.ResolvedPath))
                    {
                        var normalizedModDir = modDir.Replace('\\', '/');
                        if (!normalizedModDir.EndsWith('/')) normalizedModDir += '/';
                        
                        var normalizedResolvedPath = replacement.ResolvedPath.Replace('\\', '/');

                        if (normalizedResolvedPath.StartsWith(normalizedModDir, StringComparison.OrdinalIgnoreCase))
                        {
                            var relPath = normalizedResolvedPath.Substring(normalizedModDir.Length);
                            var parts = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                modName = parts[0];
                            }
                        }
                    }

                    if (modName != null)
                    {
                        if (hasChangedMods && !changedMods.Contains(modName))
                        {
                            continue;
                        }
                        if (!replacementsByMod.ContainsKey(modName))
                            replacementsByMod[modName] = [];
                        replacementsByMod[modName].Add(replacement);
                    }
                }
            }

            if (!replacementsByMod.Any())
            {
                _logger.LogDebug("[ModLearning] No replacements found that match ModDirectory. totalReplacements={count}", data.Sum(k => k.Value.Count));
            }

            foreach (var changedKey in changedMods)
            {
                if (!replacementsByMod.ContainsKey(changedKey))
                {
                    replacementsByMod[changedKey] = [];
                }
            }

            if (restrictToPending)
            {
                replacementsByMod = replacementsByMod
                    .Where(kvp => pendingModSettings.Contains(kvp.Key))
                    .ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var pendingMod in pendingModSettings)
                {
                    if (!replacementsByMod.ContainsKey(pendingMod))
                    {
                        replacementsByMod[pendingMod] = [];
                    }
                }
            }
            else if (restrictToChangedItems)
            {
                replacementsByMod = replacementsByMod
                    .Where(kvp => changedMods.Contains(kvp.Key))
                    .ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var changedMod in changedMods)
                {
                    if (!replacementsByMod.ContainsKey(changedMod))
                    {
                        replacementsByMod[changedMod] = [];
                    }
                }
            }

            if (!replacementsByMod.Any())
            {
                _logger.LogDebug("[ModLearning] No mods to process after ChangedItems merge. Skipping persist.");
                return;
            }

            _logger.LogDebug("[ModLearning] Found {count} mods involved in replacements.", replacementsByMod.Count);
            _logger.LogTrace("[ModLearning] Mods involved: {mods}", string.Join(", ", replacementsByMod.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
            int totalUpserts = 0;
            int totalSkipped = 0;
            int totalStates = 0;
            var observedTransientPaths = GetObservedTransientGamePathsForCharacter(name, homeWorldId, jobId);

            // We do not lock the whole process, but we should be careful with the dictionary access.
            // Since this runs in a task, and OnCharacterDataCreated spawns tasks, we might have concurrency.
            // However, for a single character (local player), data creation is usually sequential or spaced out.
            
            var profile = Profiles[key];
            var scdBuildSkipLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in replacementsByMod)
            {
                var modDirName = kvp.Key;
                var modReplacements = kvp.Value;
                var modVersion = GetModVersion(modDir, modDirName);
                int modUpserts = 0;
                int modSkipped = 0;
                _logger.LogTrace("[ModLearning] Mod start: {mod} replacements={count}", modDirName, modReplacements.Count);

                var (ec, settingsTuple) = _penumbra.GetModSettings(collectionId, modDirName);
                if (ec != Penumbra.Api.Enums.PenumbraApiEc.Success || settingsTuple == null)
                {
                    _logger.LogDebug("[ModLearning] Failed to get settings for mod {modDirName}. EC: {ec}", modDirName, ec);
                    continue;
                }

                var (enabled, _, settings, _) = settingsTuple.Value;

                if (enabled)
                {
                    var settingsDict = settings.ToDictionary(k => k.Key, k => k.Value.ToList(), StringComparer.Ordinal);
                    if (modReplacements.Count == 0)
                    {
                        var settingsHash = ComputeSettingsHash(settingsDict);
                        var cacheKey = $"{key}|{modDirName}|{settingsHash}";
                        if (_stateHashCache.ContainsKey(cacheKey))
                        {
                            _logger.LogTrace("[ModLearning] Skipping mod without replacements due to cached settings hash: {mod} settingsHash={settingsHash}", modDirName, settingsHash);
                            continue;
                        }
                    }
                    _logger.LogTrace("[ModLearning] Mod settings: {mod} enabled={enabled} groups={groups}", modDirName, enabled, settingsDict.Count);
                    var optionStates = await BuildOptionStatesAsync(modDir, modDirName, modReplacements, data, settingsDict, observedTransientPaths).ConfigureAwait(false);
                    _logger.LogDebug("[ModLearning] Option states built: {mod} count={count}", modDirName, optionStates.Count);
                    
                    lock (_lock)
                    {
                        if (!profile.LearnedMods.ContainsKey(modDirName))
                        {
                            profile.LearnedMods[modDirName] = [];
                        }

                        var existingStates = profile.LearnedMods[modDirName];
                        var existingState = existingStates.FirstOrDefault(s => AreSettingsEqual(s.Settings, settingsDict));
                        var isNewState = existingState == null;

                        if (isNewState)
                        {
                            _logger.LogDebug("[ModLearning] New settings configuration found for mod {modDirName}. Creating new state.", modDirName);
                            existingState = new LearnedModState
                            {
                                ModDirectoryName = modDirName,
                                ModVersion = modVersion,
                                Settings = settingsDict,
                            };
                            existingStates.Add(existingState);
                        }
                        else
                        {
                            _logger.LogDebug("[ModLearning] Updating existing state for mod {modDirName}.", modDirName);
                        }

                        existingState = existingState ?? new LearnedModState
                        {
                            ModDirectoryName = modDirName,
                            ModVersion = modVersion,
                            Settings = settingsDict,
                        };
                        existingState.ModVersion = modVersion;

                        foreach (var optionState in optionStates)
                        {
                            var optionSettings = optionState.Settings;
                            var optionFragments = optionState.Fragments;
                            totalStates++;

                            existingState = existingStates.FirstOrDefault(s => AreSettingsEqual(s.Settings, optionSettings));
                            if (existingState == null)
                            {
                                existingState = new LearnedModState
                                {
                                    ModDirectoryName = modDirName,
                                    ModVersion = modVersion,
                                    Settings = optionSettings,
                                };
                                existingStates.Add(existingState);
                            }

                            existingState.ModVersion = modVersion;

                            foreach (var fragmentKvp in optionFragments)
                            {
                                var kind = fragmentKvp.Key;
                                var newFragment = fragmentKvp.Value;
                                var jobIdForKind = kind == ObjectKind.MinionOrMount ? 0u : jobId;

                                if (!existingState.Fragments.TryGetValue(kind, out var existingFragment))
                                {
                                    existingFragment = new ModFileFragment();
                                    existingState.Fragments[kind] = existingFragment;
                                }

                                foreach (var replacement in newFragment.FileReplacements)
                                {
                                    var emoteGlobal = IsEmoteParentReplacement(replacement);
                                    var targetJobId = emoteGlobal ? 0u : jobIdForKind;
                                    var promoteToGlobal = emoteGlobal || IsReplacementShared(existingFragment, targetJobId, replacement);
                                    RemoveConflictingReplacements(existingFragment, replacement);
                                    AddOrElevateJobReplacement(existingFragment, targetJobId, replacement, promoteToGlobal);
                                }

                                FilterUntrackedReplacements(existingFragment, kind);
                                NormalizeJobReplacements(existingFragment);
                            }

                            RemoveMinionOverridesFromPlayer(existingState);

                            var hadScdLinks = existingState.ScdLinks != null && existingState.ScdLinks.Count > 0;
                            existingState.ScdLinks = BuildScdLinksForState(modDirName, existingState, scdBuildSkipLogged);
                            var hasScdLinks = existingState.ScdLinks.Count > 0;
                            existingState.PapEmotes = BuildPapEmotesForState(existingState);
                            PromoteEmoteLinkedReplacementsToGlobal(existingState);
                            existingState.LastUpdated = DateTime.UtcNow;

                            var stateHash = ComputeStateHash(existingState);
                            var cacheKey = $"{key}|{modDirName}|{ComputeSettingsHash(optionSettings)}";
                            if (_stateHashCache.TryGetValue(cacheKey, out var cachedHash) && StringComparer.Ordinal.Equals(cachedHash, stateHash) && !(hasScdLinks && !hadScdLinks))
                            {
                                totalSkipped++;
                                modSkipped++;
                                _logger.LogTrace("[ModLearning] Skipping unchanged state: {mod} settingsHash={settingsHash}", modDirName, ComputeSettingsHash(optionSettings));
                                continue;
                            }

                            _stateHashCache[cacheKey] = stateHash;
                            _ = _sqliteStore.UpsertLearnedModAsync(key, existingState);
                            totalUpserts++;
                            modUpserts++;
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("[ModLearning] Mod {modDirName} is disabled in collection, skipping.", modDirName);
                }

                if (modUpserts > 0 || modSkipped > 0)
                {
                    _logger.LogDebug("[ModLearning] Persisted {count} states for mod {modDirName} (skipped {skipped}).", modUpserts, modDirName, modSkipped);
                }
                _logger.LogTrace("[ModLearning] Mod end: {mod} upserts={upserts} skipped={skipped}", modDirName, modUpserts, modSkipped);
            }

            stopwatch.Stop();
            if (restrictToPending)
            {
                lock (_lock)
                {
                    foreach (var mod in pendingModSettings)
                    {
                        _pendingModSettingChanges.Remove(mod);
                    }
                }
            }
            lock (_lock)
            {
                if (_hasGlobalModSettingChange)
                {
                    _pendingModSettingChanges.Clear();
                    _hasGlobalModSettingChange = false;
                }
            }
            _logger.LogDebug("[ModLearning] Persist summary: Mods={mods}, States={states}, Upserts={upserts}, Skipped={skipped}, DurationMs={durationMs}.",
                replacementsByMod.Count, totalStates, totalUpserts, totalSkipped, stopwatch.ElapsedMilliseconds);
            _logger.LogTrace("[ModLearning] Process complete: key={key} durationMs={durationMs}", key, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ModLearning] Error processing character data for mod learning.");
        }
    }

    private HashSet<string> GetObservedTransientGamePathsForCharacter(string characterName, uint homeWorldId, uint jobId)
    {
        var transientKey = $"{characterName}_{homeWorldId}";
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_transientConfigService.Current.TransientConfigs.TryGetValue(transientKey, out var playerConfig))
        {
            foreach (var path in playerConfig.GlobalPersistentCache)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    observed.Add(NormalizePathString(path));
                }
            }

            if (jobId != 0 && playerConfig.JobSpecificCache.TryGetValue(jobId, out var jobCache))
            {
                foreach (var path in jobCache)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        observed.Add(NormalizePathString(path));
                    }
                }
            }
        }

        lock (_lock)
        {
            if (_observedTransientPathsByCharacter.TryGetValue(transientKey, out var byJob)
                && byJob.TryGetValue(jobId, out var livePaths))
            {
                foreach (var path in livePaths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        observed.Add(path);
                    }
                }
            }
        }

        return observed;
    }

    private sealed record OptionState(Dictionary<string, List<string>> Settings, Dictionary<ObjectKind, ModFileFragment> Fragments);
    private readonly record struct ResourceLoadStamp(string GamePath, string Extension, long TimestampMs);

    private async Task<List<OptionState>> BuildOptionStatesAsync(string modDirectoryPath, string modDirectoryName, List<FileReplacement> modReplacements, Dictionary<ObjectKind, List<FileReplacement>> fullData, Dictionary<string, List<string>> settingsDict, HashSet<string> observedTransientPaths)
    {
        var replacementSet = modReplacements.ToHashSet();
        var optionIndex = GetOptionIndex(modDirectoryPath, modDirectoryName);
        var optionFileMap = GetOptionFileMap(modDirectoryPath, modDirectoryName);
        var selectedOptionLists = settingsDict.ToDictionary(k => k.Key, k => k.Value.ToList(), StringComparer.Ordinal);
        var selectedOptionSets = selectedOptionLists.ToDictionary(k => k.Key, k => new HashSet<string>(k.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        var optionPriorityMap = BuildOptionPriorityMap(selectedOptionLists, optionFileMap);
        var states = new Dictionary<string, OptionState>(StringComparer.Ordinal);
        var baseKey = string.Empty;
        var baseFragments = new Dictionary<ObjectKind, ModFileFragment>();
        states[baseKey] = new OptionState(new Dictionary<string, List<string>>(StringComparer.Ordinal), baseFragments);

        foreach (var kvp in fullData)
        {
            var kind = kvp.Key;
            var replacements = kvp.Value;
            var requireTransientObservation = kind == ObjectKind.Player;
            
            var relevant = replacements.Where(r => replacementSet.Contains(r)).ToHashSet();
            
            if (relevant.Any())
            {
                foreach (var replacement in relevant)
                {
                    var optionMatches = GetOptionMatches(optionIndex, selectedOptionSets, replacement.GamePaths);
                    var seenInTransient = !requireTransientObservation || replacement.GamePaths.Any(gp => observedTransientPaths.Contains(NormalizePathString(gp)));
                    if (optionMatches.Count == 0)
                    {
                        AddReplacementToOptionState(states[baseKey], kind, replacement);
                        continue;
                    }

                    var winningMatch = SelectWinningOptionMatch(optionMatches, optionPriorityMap);
                    if (winningMatch == null)
                    {
                        AddReplacementToOptionState(states[baseKey], kind, replacement);
                        continue;
                    }

                    if (!states.TryGetValue(winningMatch.OptionKey, out var optionState))
                    {
                        var settings = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                        {
                            [winningMatch.GroupName] = [winningMatch.OptionName]
                        };
                        optionState = new OptionState(settings, new Dictionary<ObjectKind, ModFileFragment>());
                        states[winningMatch.OptionKey] = optionState;
                    }
                    if (!seenInTransient)
                    {
                        continue;
                    }
                    AddReplacementToOptionState(optionState, kind, replacement);
                }
            }
        }

        foreach (var kvp in selectedOptionLists)
        {
            foreach (var optionName in kvp.Value)
            {
                var optionKey = $"{kvp.Key}\u001F{optionName}";
                if (!states.ContainsKey(optionKey))
                {
                    var settings = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                    {
                        [kvp.Key] = [optionName]
                    };
                    states[optionKey] = new OptionState(settings, new Dictionary<ObjectKind, ModFileFragment>());
                }
            }
        }

        var optionReplacements = await BuildOptionFileReplacementsAsync(modDirectoryPath, modDirectoryName, selectedOptionLists).ConfigureAwait(false);
        foreach (var optionEntry in optionReplacements)
        {
            if (!states.TryGetValue(optionEntry.Key, out var state))
            {
                var keyParts = optionEntry.Key.Split('\u001F', 2);
                var settings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (keyParts.Length == 2)
                {
                    settings[keyParts[0]] = [keyParts[1]];
                }
                state = new OptionState(settings, new Dictionary<ObjectKind, ModFileFragment>());
                states[optionEntry.Key] = state;
            }
            foreach (var replacement in optionEntry.Value)
            {
                if (!replacement.GamePaths.Any(gp => observedTransientPaths.Contains(NormalizePathString(gp))))
                {
                    continue;
                }
                AddReplacementToOptionState(state, ObjectKind.Player, replacement);
            }
        }

        return states.Values.ToList();
    }

    private sealed record OptionMatch(string GroupName, string OptionName, string OptionKey);

    private static List<OptionMatch> GetOptionMatches(Dictionary<string, Dictionary<string, HashSet<string>>> optionIndex, Dictionary<string, HashSet<string>> selectedOptions, HashSet<string> gamePaths)
    {
        var matches = new List<OptionMatch>();
        foreach (var groupEntry in optionIndex)
        {
            if (!selectedOptions.TryGetValue(groupEntry.Key, out var selected)) continue;
            foreach (var optionEntry in groupEntry.Value)
            {
                if (!selected.Contains(optionEntry.Key)) continue;
                if (gamePaths.Overlaps(optionEntry.Value))
                {
                    matches.Add(new OptionMatch(groupEntry.Key, optionEntry.Key, $"{groupEntry.Key}\u001F{optionEntry.Key}"));
                }
            }
        }
        return matches;
    }

    private static OptionMatch? SelectWinningOptionMatch(List<OptionMatch> matches, Dictionary<string, OptionPriorityInfo> optionPriorityMap)
    {
        if (matches.Count == 0) return null;

        var winner = matches[0];
        var winnerPriority = GetOptionPriorityInfo(winner.OptionKey, optionPriorityMap);

        for (var i = 1; i < matches.Count; i++)
        {
            var current = matches[i];
            var currentPriority = GetOptionPriorityInfo(current.OptionKey, optionPriorityMap);
            if (IsHigherOptionPriority(currentPriority, winnerPriority))
            {
                winner = current;
                winnerPriority = currentPriority;
            }
        }

        return winner;
    }

    private static void AddReplacementToOptionState(OptionState state, ObjectKind kind, FileReplacement replacement)
    {
        if (!state.Fragments.TryGetValue(kind, out var fragment))
        {
            fragment = new ModFileFragment();
            state.Fragments[kind] = fragment;
        }
        fragment.FileReplacements.Add(replacement);
    }

    private Dictionary<string, Dictionary<string, HashSet<string>>> GetOptionIndex(string modDirectoryPath, string modDirectoryName)
    {
        lock (_lock)
        {
            if (_modOptionIndexCache.TryGetValue(modDirectoryName, out var cached))
            {
                return cached;
            }
        }

        var index = BuildOptionIndex(modDirectoryPath, modDirectoryName);
        lock (_lock)
        {
            _modOptionIndexCache[modDirectoryName] = index;
        }
        return index;
    }

    private Dictionary<string, OptionGroupFileEntry> GetOptionFileMap(string modDirectoryPath, string modDirectoryName)
    {
        lock (_lock)
        {
            if (_modOptionFileMapCache.TryGetValue(modDirectoryName, out var cached))
            {
                return cached;
            }
        }

        var map = BuildOptionFileMap(modDirectoryPath, modDirectoryName);
        lock (_lock)
        {
            _modOptionFileMapCache[modDirectoryName] = map;
        }
        return map;
    }

    private Dictionary<string, Dictionary<string, HashSet<string>>> BuildOptionIndex(string modDirectoryPath, string modDirectoryName)
    {
        var result = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
        var modPath = Path.Combine(modDirectoryPath, modDirectoryName);
        if (!Directory.Exists(modPath)) return result;

        foreach (var file in Directory.GetFiles(modPath, "group_*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                var groupName = root.TryGetProperty("Name", out var nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : Path.GetFileNameWithoutExtension(file);

                if (!root.TryGetProperty("Options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var optionsMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var optionElement in optionsElement.EnumerateArray())
                {
                    if (!optionElement.TryGetProperty("Name", out var optionNameElement)) continue;
                    var optionName = optionNameElement.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(optionName)) continue;

                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectGamePaths(optionElement, "Files", paths);
                    CollectGamePaths(optionElement, "FileSwaps", paths);
                    if (paths.Count == 0) continue;
                    optionsMap[optionName] = paths;
                }

                if (optionsMap.Count > 0)
                {
                    result[groupName] = optionsMap;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ModLearning] Failed to parse option file {file}", file);
            }
        }

        return result;
    }

    private static void CollectGamePaths(JsonElement optionElement, string propertyName, HashSet<string> paths)
    {
        if (!optionElement.TryGetProperty(propertyName, out var filesElement) || filesElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in filesElement.EnumerateObject())
        {
            var normalized = prop.Name.Replace('\\', '/').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                paths.Add(normalized);
            }
        }
    }

    private sealed record OptionFileEntry(Dictionary<string, string> Files, Dictionary<string, string> FileSwaps, int Priority);
    private sealed record OptionGroupFileEntry(int Priority, Dictionary<string, OptionFileEntry> Options);
    private readonly record struct OptionPriorityWinner(string OptionKey, int GroupPriority, int OptionPriority, int SelectedOrder);
    private readonly record struct OptionPriorityInfo(int GroupPriority, int OptionPriority, int SelectedOrder);

    private Dictionary<string, OptionGroupFileEntry> BuildOptionFileMap(string modDirectoryPath, string modDirectoryName)
    {
        var result = new Dictionary<string, OptionGroupFileEntry>(StringComparer.Ordinal);
        var modPath = Path.Combine(modDirectoryPath, modDirectoryName);
        if (!Directory.Exists(modPath)) return result;

        foreach (var file in Directory.GetFiles(modPath, "group_*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                var groupName = root.TryGetProperty("Name", out var nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : Path.GetFileNameWithoutExtension(file);
                var groupPriority = ReadPriority(root, "Priority", 0);

                if (!root.TryGetProperty("Options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var optionsMap = new Dictionary<string, OptionFileEntry>(StringComparer.Ordinal);
                var optionIndex = 0;
                foreach (var optionElement in optionsElement.EnumerateArray())
                {
                    if (!optionElement.TryGetProperty("Name", out var optionNameElement)) continue;
                    var optionName = optionNameElement.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(optionName)) continue;

                    var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    CollectOptionMappings(optionElement, "Files", files);
                    CollectOptionMappings(optionElement, "FileSwaps", swaps);
                    if (files.Count == 0 && swaps.Count == 0) continue;
                    var optionPriority = ReadPriority(optionElement, "Priority", optionIndex);
                    optionsMap[optionName] = new OptionFileEntry(files, swaps, optionPriority);
                    optionIndex++;
                }

                if (optionsMap.Count > 0)
                {
                    result[groupName] = new OptionGroupFileEntry(groupPriority, optionsMap);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ModLearning] Failed to parse option file {file}", file);
            }
        }

        return result;
    }

    private static void CollectOptionMappings(JsonElement optionElement, string propertyName, Dictionary<string, string> mappings)
    {
        if (!optionElement.TryGetProperty(propertyName, out var filesElement) || filesElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in filesElement.EnumerateObject())
        {
            var key = prop.Name.Replace('\\', '/').ToLowerInvariant();
            var value = prop.Value.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            mappings[key] = value.Replace('\\', '/');
        }
    }

    private async Task<Dictionary<string, List<FileReplacement>>> BuildOptionFileReplacementsAsync(string modDirectoryPath, string modDirectoryName, Dictionary<string, List<string>> selectedOptions)
    {
        var optionFileMap = GetOptionFileMap(modDirectoryPath, modDirectoryName);
        var optionReplacements = new Dictionary<string, List<FileReplacement>>(StringComparer.Ordinal);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var winnerByGamePath = new Dictionary<string, OptionPriorityWinner>(StringComparer.OrdinalIgnoreCase);

        foreach (var groupEntry in selectedOptions)
        {
            if (!optionFileMap.TryGetValue(groupEntry.Key, out var groupFileEntry)) continue;
            for (var selectedOrder = 0; selectedOrder < groupEntry.Value.Count; selectedOrder++)
            {
                var optionName = groupEntry.Value[selectedOrder];
                if (!groupFileEntry.Options.TryGetValue(optionName, out var entry)) continue;
                var optionKey = $"{groupEntry.Key}\u001F{optionName}";
                foreach (var fileEntry in entry.Files)
                {
                    var resolvedPath = Path.Combine(modDirectoryPath, modDirectoryName, fileEntry.Value);
                    filePaths.Add(resolvedPath);
                    if (ShouldReplaceWinner(winnerByGamePath, fileEntry.Key, optionKey, groupFileEntry.Priority, entry.Priority, selectedOrder))
                    {
                        winnerByGamePath[fileEntry.Key] = new OptionPriorityWinner(optionKey, groupFileEntry.Priority, entry.Priority, selectedOrder);
                    }
                }
                foreach (var swapEntry in entry.FileSwaps)
                {
                    if (ShouldReplaceWinner(winnerByGamePath, swapEntry.Key, optionKey, groupFileEntry.Priority, entry.Priority, selectedOrder))
                    {
                        winnerByGamePath[swapEntry.Key] = new OptionPriorityWinner(optionKey, groupFileEntry.Priority, entry.Priority, selectedOrder);
                    }
                }
            }
        }

        Dictionary<string, FileCacheEntity?> fileCache = new(StringComparer.OrdinalIgnoreCase);
        if (filePaths.Count > 0)
        {
            fileCache = await _fileCacheManager.GetFileCachesByPathsAsync(filePaths.ToArray()).ConfigureAwait(false);
        }

        foreach (var groupEntry in selectedOptions)
        {
            if (!optionFileMap.TryGetValue(groupEntry.Key, out var groupFileEntry)) continue;
            foreach (var optionName in groupEntry.Value)
            {
                if (!groupFileEntry.Options.TryGetValue(optionName, out var entry)) continue;
                var optionKey = $"{groupEntry.Key}\u001F{optionName}";
                if (!optionReplacements.TryGetValue(optionKey, out var list))
                {
                    list = [];
                    optionReplacements[optionKey] = list;
                }

                foreach (var fileEntry in entry.Files)
                {
                    if (!winnerByGamePath.TryGetValue(fileEntry.Key, out var winner) || !string.Equals(winner.OptionKey, optionKey, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var resolvedPath = Path.Combine(modDirectoryPath, modDirectoryName, fileEntry.Value);
                    if (!fileCache.TryGetValue(resolvedPath, out var cacheEntry) || cacheEntry == null) continue;
                    var replacement = new FileReplacement([fileEntry.Key], resolvedPath);
                    replacement.Hash = cacheEntry.Hash;
                    list.Add(replacement);
                }

                foreach (var swapEntry in entry.FileSwaps)
                {
                    if (!winnerByGamePath.TryGetValue(swapEntry.Key, out var winner) || !string.Equals(winner.OptionKey, optionKey, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var replacement = new FileReplacement([swapEntry.Key], swapEntry.Value);
                    list.Add(replacement);
                }
            }
        }

        return optionReplacements;
    }

    private static Dictionary<string, OptionPriorityInfo> BuildOptionPriorityMap(Dictionary<string, List<string>> selectedOptions, Dictionary<string, OptionGroupFileEntry> optionFileMap)
    {
        var result = new Dictionary<string, OptionPriorityInfo>(StringComparer.Ordinal);
        foreach (var groupEntry in selectedOptions)
        {
            optionFileMap.TryGetValue(groupEntry.Key, out var groupInfo);
            var groupPriority = groupInfo?.Priority ?? 0;
            for (var selectedOrder = 0; selectedOrder < groupEntry.Value.Count; selectedOrder++)
            {
                var optionName = groupEntry.Value[selectedOrder];
                var optionKey = $"{groupEntry.Key}\u001F{optionName}";
                var optionPriority = selectedOrder;
                if (groupInfo != null && groupInfo.Options.TryGetValue(optionName, out var optionInfo))
                {
                    optionPriority = optionInfo.Priority;
                }

                result[optionKey] = new OptionPriorityInfo(groupPriority, optionPriority, selectedOrder);
            }
        }
        return result;
    }

    private static OptionPriorityInfo GetOptionPriorityInfo(string optionKey, Dictionary<string, OptionPriorityInfo> optionPriorityMap)
    {
        if (optionPriorityMap.TryGetValue(optionKey, out var info))
        {
            return info;
        }

        return new OptionPriorityInfo(0, 0, -1);
    }

    private static bool IsHigherOptionPriority(OptionPriorityInfo candidate, OptionPriorityInfo current)
    {
        if (candidate.GroupPriority != current.GroupPriority)
        {
            return candidate.GroupPriority > current.GroupPriority;
        }

        if (candidate.OptionPriority != current.OptionPriority)
        {
            return candidate.OptionPriority > current.OptionPriority;
        }

        return candidate.SelectedOrder > current.SelectedOrder;
    }

    private static bool ShouldReplaceWinner(Dictionary<string, OptionPriorityWinner> winnerByGamePath, string gamePath, string optionKey, int groupPriority, int optionPriority, int selectedOrder)
    {
        if (!winnerByGamePath.TryGetValue(gamePath, out var current))
        {
            return true;
        }

        if (groupPriority != current.GroupPriority)
        {
            return groupPriority > current.GroupPriority;
        }

        if (optionPriority != current.OptionPriority)
        {
            return optionPriority > current.OptionPriority;
        }

        if (selectedOrder != current.SelectedOrder)
        {
            return selectedOrder > current.SelectedOrder;
        }

        return !string.Equals(optionKey, current.OptionKey, StringComparison.Ordinal);
    }

    private static int ReadPriority(JsonElement element, string propertyName, int fallback)
    {
        if (!element.TryGetProperty(propertyName, out var priorityElement))
        {
            return fallback;
        }

        if (priorityElement.ValueKind == JsonValueKind.Number && priorityElement.TryGetInt32(out var value))
        {
            return value;
        }

        if (priorityElement.ValueKind == JsonValueKind.String
            && int.TryParse(priorityElement.GetString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }


    private static IEnumerable<FileReplacement> GetEffectiveReplacements(ModFileFragment fragment, uint jobId)
    {
        fragment.FileReplacements ??= [];
        fragment.JobFileReplacements ??= [];
        var results = new HashSet<FileReplacement>();
        foreach (var replacement in fragment.FileReplacements)
        {
            results.Add(replacement);
        }

        if (jobId != 0 && fragment.JobFileReplacements.TryGetValue(jobId, out var jobReplacements))
        {
            foreach (var replacement in jobReplacements)
            {
                results.Add(replacement);
            }
        }

        return results;
    }

    private static void RemoveConflictingReplacements(ModFileFragment fragment, FileReplacement replacement)
    {
        fragment.FileReplacements ??= [];
        fragment.JobFileReplacements ??= [];
        fragment.FileReplacements.RemoveWhere(existing =>
            string.Equals(existing.ResolvedPath, replacement.ResolvedPath, StringComparison.OrdinalIgnoreCase) ||
            existing.GamePaths.Overlaps(replacement.GamePaths));

        foreach (var jobSet in fragment.JobFileReplacements.Values)
        {
            jobSet.RemoveWhere(existing =>
                string.Equals(existing.ResolvedPath, replacement.ResolvedPath, StringComparison.OrdinalIgnoreCase) ||
                existing.GamePaths.Overlaps(replacement.GamePaths));
        }
        CleanupEmptyJobSets(fragment);
    }

    private static void AddOrElevateJobReplacement(ModFileFragment fragment, uint jobId, FileReplacement replacement, bool promoteToGlobal)
    {
        fragment.FileReplacements ??= [];
        fragment.JobFileReplacements ??= [];
        if (fragment.FileReplacements.Contains(replacement)) return;

        if (jobId == 0 || promoteToGlobal)
        {
            foreach (var jobSetValues in fragment.JobFileReplacements.Values)
            {
                jobSetValues.Remove(replacement);
            }
            fragment.FileReplacements.Add(replacement);
            CleanupEmptyJobSets(fragment);
            return;
        }

        if (!fragment.JobFileReplacements.TryGetValue(jobId, out var jobSet))
        {
            jobSet = new HashSet<FileReplacement>();
            fragment.JobFileReplacements[jobId] = jobSet;
        }

        jobSet.Add(replacement);
    }

    private static bool IsReplacementShared(ModFileFragment fragment, uint jobId, FileReplacement replacement)
    {
        fragment.FileReplacements ??= [];
        fragment.JobFileReplacements ??= [];

        if (fragment.FileReplacements.Any(existing =>
                string.Equals(existing.ResolvedPath, replacement.ResolvedPath, StringComparison.OrdinalIgnoreCase) ||
                existing.GamePaths.Overlaps(replacement.GamePaths)))
        {
            return true;
        }

        foreach (var kvp in fragment.JobFileReplacements)
        {
            if (kvp.Key == jobId) continue;
            if (kvp.Value == null) continue;
            if (kvp.Value.Any(existing =>
                    string.Equals(existing.ResolvedPath, replacement.ResolvedPath, StringComparison.OrdinalIgnoreCase) ||
                    existing.GamePaths.Overlaps(replacement.GamePaths)))
            {
                return true;
            }
        }

        return false;
    }

    private static void CleanupEmptyJobSets(ModFileFragment fragment)
    {
        foreach (var entry in fragment.JobFileReplacements.Where(kvp => kvp.Value == null || kvp.Value.Count == 0).ToList())
        {
            fragment.JobFileReplacements.Remove(entry.Key);
        }
    }

    private static void NormalizeJobReplacements(ModFileFragment fragment)
    {
        fragment.FileReplacements ??= [];
        fragment.JobFileReplacements ??= [];

        var pathUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var jobSet in fragment.JobFileReplacements.Values)
        {
            if (jobSet == null) continue;
            foreach (var replacement in jobSet)
            {
                foreach (var gp in replacement.GamePaths)
                {
                    var key = NormalizePathString(gp);
                    if (!pathUsage.TryAdd(key, 1))
                    {
                        pathUsage[key]++;
                    }
                }
            }
        }

        var toPromote = new List<FileReplacement>();
        foreach (var kvp in fragment.JobFileReplacements)
        {
            var jobId = kvp.Key;
            var jobSet = kvp.Value;
            if (jobSet == null || jobSet.Count == 0) continue;
            foreach (var replacement in jobSet)
            {
                var sharedByJob = replacement.GamePaths.Any(p => pathUsage.TryGetValue(NormalizePathString(p), out var count) && count > 1);
                if (sharedByJob || IsReplacementShared(fragment, jobId, replacement))
                {
                    toPromote.Add(replacement);
                }
            }
        }

        foreach (var replacement in toPromote.Distinct())
        {
            RemoveConflictingReplacements(fragment, replacement);
            AddOrElevateJobReplacement(fragment, 0, replacement, true);
        }
    }

    private static void RemoveMinionOverridesFromPlayer(LearnedModState state)
    {
        if (!state.Fragments.TryGetValue(ObjectKind.MinionOrMount, out var minionFragment)) return;
        if (!state.Fragments.TryGetValue(ObjectKind.Player, out var playerFragment)) return;

        var minionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var replacement in GetAllReplacements(minionFragment))
        {
            foreach (var gp in replacement.GamePaths)
            {
                minionPaths.Add(NormalizePathString(gp));
            }
        }

        if (minionPaths.Count == 0) return;

        playerFragment.FileReplacements.RemoveWhere(replacement =>
            replacement.GamePaths.Any(p => minionPaths.Contains(NormalizePathString(p))));

        foreach (var jobSet in playerFragment.JobFileReplacements.Values)
        {
            if (jobSet == null) continue;
            jobSet.RemoveWhere(replacement =>
                replacement.GamePaths.Any(p => minionPaths.Contains(NormalizePathString(p))));
        }
        CleanupEmptyJobSets(playerFragment);
    }

    private static bool AreSettingsEqual(Dictionary<string, List<string>> a, Dictionary<string, List<string>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var bVal)) return false;
            if (kvp.Value.Count != bVal.Count) return false;
            if (!kvp.Value.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(bVal.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal)) return false;
        }
        return true;
    }

    private static bool SettingsSubsetMatch(Dictionary<string, List<string>> current, Dictionary<string, List<string>> state)
    {
        if (state.Count == 0) return true;
        foreach (var kvp in state)
        {
            if (!current.TryGetValue(kvp.Key, out var currentValues)) return false;
            var currentSet = currentValues.ToHashSet(StringComparer.Ordinal);
            foreach (var value in kvp.Value)
            {
                if (!currentSet.Contains(value)) return false;
            }
        }
        return true;
    }

    private static string ComputeStateHash(LearnedModState state)
    {
        var settingsJson = JsonSerializer.Serialize(state.Settings);
        var fragmentsJson = JsonSerializer.Serialize(state.Fragments);
        var scdLinksJson = JsonSerializer.Serialize(state.ScdLinks);
        var papEmotesJson = JsonSerializer.Serialize(state.PapEmotes);
        var modVersion = state.ModVersion ?? string.Empty;
        return ComputeHash($"{settingsJson}|{modVersion}|{fragmentsJson}|{scdLinksJson}|{papEmotesJson}");
    }

    private static string ComputeSettingsHash(Dictionary<string, List<string>> settings)
    {
        var settingsJson = JsonSerializer.Serialize(settings);
        return ComputeHash(settingsJson);
    }

    private static string ComputeHash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private string? GetModVersion(string modDirectoryPath, string modDirectoryName)
    {
        var metaPath = Path.Combine(modDirectoryPath, modDirectoryName, "meta.json");
        if (!File.Exists(metaPath)) return null;
        try
        {
            using var stream = File.OpenRead(metaPath);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("Version", out var versionElement)
                ? versionElement.GetString()
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[ModLearning] Failed to read meta.json for mod {modDirName}.", modDirectoryName);
            return null;
        }
    }

    private void EnsureProfileLoaded(string key, string name, string homeWorld)
    {
        bool needsLoading = false;
        lock (_lock)
        {
            if (!Profiles.ContainsKey(key))
            {
                Profiles[key] = new CharacterModProfile
                {
                    CharacterName = name,
                    Homeworld = homeWorld
                };
                needsLoading = true;
            }
        }

        if (needsLoading)
        {
            // Load from SQLite
            // This is a blocking call in a background task (usually), so GetResult() is acceptable here for initialization
            var states = _sqliteStore.GetLearnedModsAsync(key).GetAwaiter().GetResult();

            lock (_lock)
            {
                var profile = Profiles[key];
                
                // Only populate if empty to avoid overwriting concurrent additions
                if (profile.LearnedMods.Count == 0)
                {
                    foreach (var state in states)
                    {
                        if (!profile.LearnedMods.ContainsKey(state.ModDirectoryName))
                        {
                            profile.LearnedMods[state.ModDirectoryName] = [];
                        }
                        profile.LearnedMods[state.ModDirectoryName].Add(state);
                    }
                }
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            var links = await _sqliteStore.GetResourceLinksAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _modResourceLinks.Clear();
                foreach (var modEntry in links)
                {
                    _modResourceLinks[modEntry.Key] = modEntry.Value;
                }
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }
}
