using Sphene.API.Data.Enum;
using Sphene.PlayerData.Data;
using Sphene.PlayerData.Factories;
using Sphene.PlayerData.Handlers;
using Sphene.Interop.Ipc;
using Sphene.Services;
using Sphene.Services.Mediator;
using Microsoft.Extensions.Logging;

namespace Sphene.PlayerData.Services;

public sealed class CacheCreationService : DisposableMediatorSubscriberBase
{
    private readonly SemaphoreSlim _cacheCreateLock = new(1);
    private readonly System.Threading.Lock _playerDataLock = new();
    private readonly HashSet<ObjectKind> _cachesToCreate = [];
    private readonly PlayerDataFactory _characterDataFactory;
    private readonly HashSet<ObjectKind> _currentlyCreating = [];
    private readonly HashSet<ObjectKind> _debouncedObjectCache = [];
    private readonly CharacterData _playerData = new();
    private readonly Dictionary<ObjectKind, GameObjectHandler> _playerRelatedObjects = [];
    private readonly IpcManager _ipcManager;
    private readonly Dictionary<ObjectKind, string> _forceRebuildTraceByKind = [];
    private readonly CancellationTokenSource _runtimeCts = new();
    private CancellationTokenSource _creationCts = new();
    private CancellationTokenSource _debounceCts = new();
    private bool _haltCharaDataCreation;
    private bool _isZoning = false;
    private string? _lastDataHash = null;
    private bool _forcePublishNext = false;

    public CacheCreationService(ILogger<CacheCreationService> logger, SpheneMediator mediator, GameObjectHandlerFactory gameObjectHandlerFactory,
        PlayerDataFactory characterDataFactory, DalamudUtilService dalamudUtil, IpcManager ipcManager) : base(logger, mediator)
    {
        _characterDataFactory = characterDataFactory;
        _ipcManager = ipcManager;

        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (msg) => _isZoning = true);
        Mediator.Subscribe<ZoneSwitchEndMessage>(this, (msg) =>
        {
            _isZoning = false;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (_isZoning) return;
                AddCacheToCreate(ObjectKind.Player);
            });
        });

        Mediator.Subscribe<HaltCharaDataCreation>(this, (msg) =>
        {
            _haltCharaDataCreation = !msg.Resume;
        });

        Mediator.Subscribe<CreateCacheForObjectMessage>(this, (msg) =>
        {
            Logger.LogDebug("Received CreateCacheForObject for {handler}, updating", msg.ObjectToCreateFor);
            AddCacheToCreate(msg.ObjectToCreateFor.ObjectKind);
        });
        Mediator.Subscribe<ForceLocalCharacterDataRebuildMessage>(this, (msg) =>
        {
            if (_isZoning)
            {
                Logger.LogDebug("[Trace:ForceRebuild:{trace}] SKIP zoning", msg.TraceId);
                return;
            }
            Logger.LogDebug("[Trace:ForceRebuild:{trace}] REQUEST kind={kind}", msg.TraceId, msg.ObjectKind);
            _forcePublishNext = true;
            _forceRebuildTraceByKind[msg.ObjectKind] = msg.TraceId;
            AddCacheToCreate(msg.ObjectKind);
        });

        _playerRelatedObjects[ObjectKind.Player] = gameObjectHandlerFactory.Create(ObjectKind.Player, dalamudUtil.GetPlayerPtr, isWatched: true)
            .GetAwaiter().GetResult();
        _playerRelatedObjects[ObjectKind.MinionOrMount] = gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => dalamudUtil.GetMinionOrMountPtr(), isWatched: true)
            .GetAwaiter().GetResult();
        _playerRelatedObjects[ObjectKind.Pet] = gameObjectHandlerFactory.Create(ObjectKind.Pet, () => dalamudUtil.GetPetPtr(), isWatched: true)
            .GetAwaiter().GetResult();
        _playerRelatedObjects[ObjectKind.Companion] = gameObjectHandlerFactory.Create(ObjectKind.Companion, () => dalamudUtil.GetCompanionPtr(), isWatched: true)
            .GetAwaiter().GetResult();

        Mediator.Subscribe<ClassJobChangedMessage>(this, (msg) =>
        {
            if (msg.GameObjectHandler == _playerRelatedObjects[ObjectKind.Player])
            {
                AddCacheToCreate(ObjectKind.Player);
                AddCacheToCreate(ObjectKind.Pet);
            }
        });

        Mediator.Subscribe<ClearCacheForObjectMessage>(this, (msg) =>
        {
            if (msg.ObjectToCreateFor.ObjectKind == ObjectKind.Pet)
            {
                Logger.LogTrace("Received clear cache for {obj}, ignoring", msg.ObjectToCreateFor);
                return;
            }
            Logger.LogDebug("Clearing cache for {obj}", msg.ObjectToCreateFor);
            AddCacheToCreate(msg.ObjectToCreateFor.ObjectKind);
        });

        Mediator.Subscribe<CustomizePlusMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            foreach (var item in _playerRelatedObjects
                .Where(item => msg.Address == null
                || item.Value.Address == msg.Address).Select(k => k.Key))
            {
                Logger.LogDebug("Received CustomizePlus change, updating {obj}", item);
                AddCacheToCreate(item);
            }
        });

        Mediator.Subscribe<HeelsOffsetMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            Logger.LogDebug("Received Heels Offset change, updating player");
            AddCacheToCreate();
        });

        Mediator.Subscribe<GlamourerChangedMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            var changedType = _playerRelatedObjects.FirstOrDefault(f => f.Value.Address == msg.Address);
            if (changedType.Value != null)
            {
                Logger.LogDebug("Received GlamourerChangedMessage for {kind}", changedType);
                Mediator.Publish(new CharacterDataBuildStartedMessage());
                AddCacheToCreate(changedType.Key);
            }
        });

        Mediator.Subscribe<HonorificMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            if (!string.Equals(msg.NewHonorificTitle, _playerData.HonorificData, StringComparison.Ordinal))
            {
                Logger.LogDebug("Received Honorific change, updating player");
                AddCacheToCreate(ObjectKind.Player);
            }
        });

        Mediator.Subscribe<MoodlesMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            var changedType = _playerRelatedObjects.FirstOrDefault(f => f.Value.Address == msg.Address);
            if (changedType.Value != null && changedType.Key == ObjectKind.Player)
            {
                Logger.LogDebug("Received Moodles change, updating player");
                AddCacheToCreate(ObjectKind.Player);
            }
        });

        Mediator.Subscribe<PetNamesMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            if (!string.Equals(msg.PetNicknamesData, _playerData.PetNamesData, StringComparison.Ordinal))
            {
                Logger.LogDebug("Received Pet Nicknames change, updating player");
                AddCacheToCreate(ObjectKind.Player);
            }
        });

        Mediator.Subscribe<BypassEmoteMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            
            lock (_playerDataLock)
            {
                if (!string.Equals(msg.BypassEmoteData, _playerData.BypassEmoteData, StringComparison.Ordinal))
                {
                    Logger.LogDebug("Received BypassEmote change, fast-tracking update. Old Hash: {Hash}", _lastDataHash ?? "null");
                    _playerData.BypassEmoteData = msg.BypassEmoteData;

                    var newData = _playerData.ToAPI();
                    var newHash = newData.DataHash?.Value;

                    // Fast path: Publish immediately using the NEW hash.
                    // The receiver (CharaDataManager) needs a valid hash to send to the server.
                    // Even if the server doesn't have this hash yet (Slow Path hasn't arrived),
                    // the server can still forward the message to recipients.
                    Mediator.Publish(new BypassEmoteUpdateMessage(msg.BypassEmoteData, newHash ?? string.Empty));
                    
                    if (!string.Equals(newHash, _lastDataHash, StringComparison.Ordinal))
                    {
                        Logger.LogDebug("Character data changed (BypassEmote), publishing update. Old hash: {OldHash}, New hash: {NewHash}", _lastDataHash ?? "null", newHash ?? "null");
                        _lastDataHash = newHash;
                        
                        // Slow path (consistency)
                        Mediator.Publish(new CharacterDataCreatedMessage(newData));
                    }
                }
            }
        });

        Mediator.Subscribe<PenumbraModSettingChangedMessage>(this, (msg) =>
        {
            if (!ShouldRebuildForPenumbraChange(msg))
            {
                Logger.LogTrace("Ignoring Penumbra Mod settings change for non-local collection {collection}", msg.CollectionId);
                return;
            }
            Logger.LogDebug("Received Penumbra Mod settings change, updating everything");
            _forcePublishNext = true;
            AddCacheToCreate(ObjectKind.Player);
            AddCacheToCreate(ObjectKind.Pet);
            AddCacheToCreate(ObjectKind.MinionOrMount);
            AddCacheToCreate(ObjectKind.Companion);
        });

        Mediator.Subscribe<FrameworkUpdateMessage>(this, (msg) => ProcessCacheCreation());
    }

    private bool ShouldRebuildForPenumbraChange(PenumbraModSettingChangedMessage msg)
    {
        if (msg.CollectionId == Guid.Empty) return true;
        if (!_ipcManager.Penumbra.APIAvailable) return true;
        var (valid, _, collection) = _ipcManager.Penumbra.GetCollectionForObject(0);
        if (!valid) return true;
        return msg.CollectionId == collection.Id;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _playerRelatedObjects.Values.ToList().ForEach(p => p.Dispose());
        _runtimeCts.Cancel();
        _runtimeCts.Dispose();
        _creationCts.Cancel();
        _creationCts.Dispose();
    }

    private void AddCacheToCreate(ObjectKind kind = ObjectKind.Player)
    {
        _debounceCts.Cancel();
        _debounceCts.Dispose();
        _debounceCts = new();
        var token = _debounceCts.Token;
        _cacheCreateLock.Wait();
        _debouncedObjectCache.Add(kind);
        _cacheCreateLock.Release();

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            Logger.LogTrace("Debounce complete, inserting objects to create for: {obj}", string.Join(", ", _debouncedObjectCache));
            await _cacheCreateLock.WaitAsync(token).ConfigureAwait(false);
            foreach (var item in _debouncedObjectCache)
            {
                _cachesToCreate.Add(item);
            }
            _debouncedObjectCache.Clear();
            _cacheCreateLock.Release();
        });
    }

    private void ProcessCacheCreation()
    {
        if (_isZoning || _haltCharaDataCreation) return;

        if (_cachesToCreate.Count == 0) return;

        if (_playerRelatedObjects.Any(p => p.Value.CurrentDrawCondition is
            not (GameObjectHandler.DrawCondition.None or GameObjectHandler.DrawCondition.DrawObjectZero or GameObjectHandler.DrawCondition.ObjectZero)))
        {
            Logger.LogDebug("Waiting for draw to finish before executing cache creation");
            return;
        }

        _creationCts.Cancel();
        _creationCts.Dispose();
        _creationCts = new();
        _cacheCreateLock.Wait(_creationCts.Token);
        var objectKindsToCreate = _cachesToCreate.ToList();
        foreach (var creationObj in objectKindsToCreate)
        {
            _currentlyCreating.Add(creationObj);
        }
        _cachesToCreate.Clear();
        _cacheCreateLock.Release();

        _ = Task.Run(async () =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_creationCts.Token, _runtimeCts.Token);

            await Task.Delay(TimeSpan.FromSeconds(1), linkedCts.Token).ConfigureAwait(false);

            Logger.LogDebug("Creating Caches for {objectKinds}", string.Join(", ", objectKindsToCreate));
            foreach (var kind in objectKindsToCreate)
            {
                if (_forceRebuildTraceByKind.TryGetValue(kind, out var traceId))
                {
                    Logger.LogDebug("[Trace:ForceRebuild:{trace}] BUILD start kind={kind}", traceId, kind);
                }
            }

            try
            {
                Dictionary<ObjectKind, CharacterDataFragment?> createdData = [];
                foreach (var objectKind in _currentlyCreating)
                {
                    createdData[objectKind] = await _characterDataFactory.BuildCharacterData(_playerRelatedObjects[objectKind], linkedCts.Token).ConfigureAwait(false);
                }

                RemovePlayerMinionOverlaps(createdData);

                string? newHash = null;
                var totalReplacements = 0;
                lock (_playerDataLock)
                {
                    foreach (var kvp in createdData)
                    {
                        if (kvp.Key == ObjectKind.MinionOrMount && kvp.Value == null)
                        {
                            continue;
                        }

                        _playerData.SetFragment(kvp.Key, kvp.Value);
                    }

                    // Check if data actually changed before publishing
                    var newData = _playerData.ToAPI();
                    newHash = newData.DataHash?.Value;
                    
                    var forcePublish = _forcePublishNext;
                    _forcePublishNext = false;

                    totalReplacements = newData.FileReplacements.Values.Sum(l => l.Count);
                    if (!string.Equals(newHash, _lastDataHash, StringComparison.Ordinal))
                    {
                        Logger.LogDebug("Character data changed, publishing update. Old hash: {oldHash}, New hash: {newHash}", _lastDataHash ?? "null", newHash ?? "null");
                        _lastDataHash = newHash;
                        Mediator.Publish(new CharacterDataCreatedMessage(newData, _playerData));
                    }
                    else if (forcePublish)
                    {
                        Logger.LogDebug("Character data unchanged but forced publish after mod settings change. Hash: {hash}", newHash ?? "null");
                        Mediator.Publish(new CharacterDataCreatedMessage(newData, _playerData));
                    }
                    else
                    {
                        Logger.LogDebug("Character data unchanged, skipping update. Hash: {hash}", newHash ?? "null");
                    }
                }
                
                foreach (var kind in objectKindsToCreate)
                {
                    if (_forceRebuildTraceByKind.TryGetValue(kind, out var traceId))
                    {
                        Logger.LogDebug("[Trace:ForceRebuild:{trace}] BUILD done kind={kind} hash={hash} totalEntries={total}",
                            traceId, kind, newHash ?? "null", totalReplacements);
                        _forceRebuildTraceByKind.Remove(kind);
                    }
                }
                _currentlyCreating.Clear();
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("Cache Creation cancelled");
            }
            catch (Exception ex)
            {
                Logger.LogCritical(ex, "Error during Cache Creation Processing");
            }
            finally
            {
                Logger.LogDebug("Cache Creation complete");
            }
        });
    }

    private static void RemovePlayerMinionOverlaps(Dictionary<ObjectKind, CharacterDataFragment?> createdData)
    {
        if (!createdData.TryGetValue(ObjectKind.Player, out var playerFragment) || playerFragment == null) return;
        if (!createdData.TryGetValue(ObjectKind.MinionOrMount, out var minionFragment) || minionFragment == null) return;
        if (playerFragment.FileReplacements.Count == 0 || minionFragment.FileReplacements.Count == 0) return;

        var minionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var replacement in minionFragment.FileReplacements)
        {
            foreach (var path in replacement.GamePaths)
            {
                minionPaths.Add(NormalizePathString(path));
            }
        }

        if (minionPaths.Count == 0) return;

        playerFragment.FileReplacements.RemoveWhere(replacement =>
            replacement.GamePaths.Any(p => minionPaths.Contains(NormalizePathString(p))));
    }

    private static string NormalizePathString(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }
}
