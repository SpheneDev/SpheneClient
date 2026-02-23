using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Sphene.SpheneConfiguration.Models;
using Sphene.PlayerData.Handlers;
using Sphene.Services;
using Sphene.Services.Mediator;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using System.Collections.Concurrent;

namespace Sphene.Interop.Ipc;

public sealed class IpcCallerPenumbra : DisposableMediatorSubscriberBase, IIpcCaller
{
    private readonly IDalamudPluginInterface _pi;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SpheneMediator _spheneMediator;
    private readonly RedrawManager _redrawManager;
    private bool _shownPenumbraUnavailable = false;
    private string? _penumbraModDirectory;
    public string? ModDirectory
    {
        get => _penumbraModDirectory;
        private set
        {
            if (!string.Equals(_penumbraModDirectory, value, StringComparison.Ordinal))
            {
                _penumbraModDirectory = value;
                _spheneMediator.Publish(new PenumbraDirectoryChangedMessage(_penumbraModDirectory));
            }
        }
    }

    private CancellationTokenSource _debouncedRedrawCts = new();

    private readonly EventSubscriber _penumbraDispose;
    private readonly EventSubscriber<nint, string, string> _penumbraGameObjectResourcePathResolved;
    private readonly EventSubscriber _penumbraInit;
    private readonly EventSubscriber<ModSettingChange, Guid, string, bool> _penumbraModSettingChanged;
    private readonly EventSubscriber<nint, int> _penumbraObjectIsRedrawn;
    private readonly EventSubscriber<string> _penumbraModAdded;
    private readonly EventSubscriber<string> _penumbraModDeleted;
    private readonly EventSubscriber<string, string> _penumbraModMoved;

    private readonly AddTemporaryMod _penumbraAddTemporaryMod;
    private readonly AssignTemporaryCollection _penumbraAssignTemporaryCollection;
    private readonly ConvertTextureFile _penumbraConvertTextureFile;
    private readonly CreateTemporaryCollection _penumbraCreateNamedTemporaryCollection;
    private readonly GetEnabledState _penumbraEnabled;
    private readonly GetPlayerMetaManipulations _penumbraGetMetaManipulations;
    private readonly RedrawObject _penumbraRedraw;
    private readonly DeleteTemporaryCollection _penumbraRemoveTemporaryCollection;
    private readonly RemoveTemporaryMod _penumbraRemoveTemporaryMod;
    private readonly GetModDirectory _penumbraResolveModDir;
    private readonly ResolvePlayerPathsAsync _penumbraResolvePaths;
    private readonly GetGameObjectResourcePaths _penumbraResourcePaths;
    private readonly ICallGateSubscriber<ApiCollectionType, (Guid, string)?> _penumbraGetCollection;
    private readonly ICallGateSubscriber<Guid, bool, bool, int, (PenumbraApiEc, Dictionary<string, (bool, int, Dictionary<string, List<string>>, bool, bool)>?)> _penumbraGetAllModSettings;
    private readonly ICallGateSubscriber<Dictionary<string, string>> _penumbraGetModList;
    private readonly ICallGateSubscriber<string, string, (PenumbraApiEc, string, bool, bool)> _penumbraGetModPath;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> _penumbraGetCollectionForObject;
    private readonly ICallGateSubscriber<string, string, Dictionary<string, object?>> _penumbraGetChangedItems;

    public IpcCallerPenumbra(ILogger<IpcCallerPenumbra> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil,
        SpheneMediator spheneMediator, RedrawManager redrawManager) : base(logger, spheneMediator)
    {
        _pi = pi;
        _dalamudUtil = dalamudUtil;
        _spheneMediator = spheneMediator;
        _redrawManager = redrawManager;
        _penumbraInit = Initialized.Subscriber(pi, PenumbraInit);
        _penumbraDispose = Disposed.Subscriber(pi, PenumbraDispose);
        _penumbraResolveModDir = new GetModDirectory(pi);
        _penumbraRedraw = new RedrawObject(pi);
        _penumbraObjectIsRedrawn = GameObjectRedrawn.Subscriber(pi, RedrawEvent);
        _penumbraGetMetaManipulations = new GetPlayerMetaManipulations(pi);
        _penumbraRemoveTemporaryMod = new RemoveTemporaryMod(pi);
        _penumbraAddTemporaryMod = new AddTemporaryMod(pi);
        _penumbraCreateNamedTemporaryCollection = new CreateTemporaryCollection(pi);
        _penumbraRemoveTemporaryCollection = new DeleteTemporaryCollection(pi);
        _penumbraAssignTemporaryCollection = new AssignTemporaryCollection(pi);
        _penumbraResolvePaths = new ResolvePlayerPathsAsync(pi);
        _penumbraEnabled = new GetEnabledState(pi);
        _penumbraModSettingChanged = ModSettingChanged.Subscriber(pi, (change, collection, mod, inherited) =>
        {
            // Trigger rescan for any setting change that affects active mods
            // We include almost all changes: EnableState, Priority, Setting, TemporaryMod, etc.
            // Even Edited might change file paths (e.g. meta changes).
            if (change != ModSettingChange.Edited)
            {
                logger.LogDebug("Penumbra setting changed: {Type} (Collection: {Collection}, Mod: {Mod}) -> Triggering Rescan", change, collection, mod);
                _spheneMediator.Publish(new PenumbraModSettingChangedMessage());
            }
        });
        _penumbraModAdded = ModAdded.Subscriber(pi, s => 
        {
            logger.LogDebug("Penumbra mod added: {Mod} -> Triggering Rescan", s);
            _spheneMediator.Publish(new PenumbraModSettingChangedMessage());
        });
        _penumbraModDeleted = ModDeleted.Subscriber(pi, s => 
        {
            logger.LogDebug("Penumbra mod deleted: {Mod} -> Triggering Rescan", s);
            _spheneMediator.Publish(new PenumbraModSettingChangedMessage());
        });
        _penumbraModMoved = ModMoved.Subscriber(pi, (s1, s2) => 
        {
            logger.LogDebug("Penumbra mod moved: {Old} -> {New} -> Triggering Rescan", s1, s2);
            _spheneMediator.Publish(new PenumbraModSettingChangedMessage());
        });
        _penumbraConvertTextureFile = new ConvertTextureFile(pi);
        _penumbraResourcePaths = new GetGameObjectResourcePaths(pi);
        
        // Initialize new IPC subscribers
        _penumbraGetCollection = _pi.GetIpcSubscriber<ApiCollectionType, (Guid, string)?>("Penumbra.GetCollection");
        _penumbraGetAllModSettings = _pi.GetIpcSubscriber<Guid, bool, bool, int, (PenumbraApiEc, Dictionary<string, (bool, int, Dictionary<string, List<string>>, bool, bool)>?)>("Penumbra.GetAllModSettings");
        _penumbraGetModList = _pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        _penumbraGetModPath = _pi.GetIpcSubscriber<string, string, (PenumbraApiEc, string, bool, bool)>("Penumbra.GetModPath.V5");
        _penumbraGetCollectionForObject = _pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        _penumbraGetChangedItems = _pi.GetIpcSubscriber<string, string, Dictionary<string, object?>>("Penumbra.GetChangedItems.V5");

        _penumbraGameObjectResourcePathResolved = GameObjectResourcePathResolved.Subscriber(pi, ResourceLoaded);

        CheckAPI();
        CheckModDirectory();

        Mediator.Subscribe<PenumbraRedrawCharacterMessage>(this, (msg) =>
        {
            if (msg.Character.ObjectIndex < 0) return;
            _penumbraRedraw.Invoke(msg.Character.ObjectIndex, RedrawType.Redraw);
        });

        Mediator.Subscribe<DalamudLoginMessage>(this, (msg) => _shownPenumbraUnavailable = false);
    }



    public bool APIAvailable { get; private set; } = false;

    public void CheckAPI()
    {
        bool penumbraAvailable = false;
        try
        {
            var penumbraVersion = (_pi.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, "Penumbra", StringComparison.OrdinalIgnoreCase))
                ?.Version ?? new Version(0, 0, 0, 0));
            penumbraAvailable = penumbraVersion >= new Version(1, 2, 0, 22);
            try
            {
                penumbraAvailable &= _penumbraEnabled.Invoke();
            }
            catch
            {
                penumbraAvailable = false;
            }
            _shownPenumbraUnavailable = _shownPenumbraUnavailable && !penumbraAvailable;
            APIAvailable = penumbraAvailable;
        }
        catch
        {
            APIAvailable = penumbraAvailable;
        }
        finally
        {
            if (!penumbraAvailable && !_shownPenumbraUnavailable)
            {
                _shownPenumbraUnavailable = true;
                _spheneMediator.Publish(new NotificationMessage("Penumbra inactive",
                    "Your Penumbra installation is not active or out of date. Update Penumbra and/or the Enable Mods setting in Penumbra to continue to use Sphene. If you just updated Penumbra, ignore this message.",
                    NotificationType.Error));
            }
        }
    }

    public void CheckModDirectory()
    {
        if (!APIAvailable)
        {
            ModDirectory = string.Empty;
        }
        else
        {
            ModDirectory = _penumbraResolveModDir!.Invoke().ToLowerInvariant();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _redrawManager.Cancel();

        _penumbraModSettingChanged.Dispose();
        _penumbraGameObjectResourcePathResolved.Dispose();
        _penumbraDispose.Dispose();
        _penumbraInit.Dispose();
        _penumbraObjectIsRedrawn.Dispose();
        _penumbraModAdded.Dispose();
        _penumbraModDeleted.Dispose();
        _penumbraModMoved.Dispose();
    }

    public Dictionary<string, string> GetModList()
    {
        if (!APIAvailable) return [];
        try
        {
            return _penumbraGetModList.InvokeFunc();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to get mod list from Penumbra");
            return [];
        }
    }

    public Dictionary<string, object?> GetChangedItems(string modDirectory, string modName)
    {
        if (!APIAvailable) return [];
        try
        {
            return _penumbraGetChangedItems.InvokeFunc(modDirectory, modName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to get changed items from Penumbra for {ModDirectory}", modDirectory);
            return [];
        }
    }



    public async Task AssignTemporaryCollectionAsync(ILogger logger, Guid collName, int idx)
    {
        if (!APIAvailable) return;

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            var retAssign = _penumbraAssignTemporaryCollection.Invoke(collName, idx, forceAssignment: true);
            logger.LogTrace("Assigning Temp Collection {collName} to index {idx}, Success: {ret}", collName, idx, retAssign);
            return collName;
        }).ConfigureAwait(false);
    }

    public async Task ConvertTextureFiles(ILogger logger, Dictionary<string, string[]> textures, IProgress<(string, int)> progress, CancellationToken token)
    {
        if (!APIAvailable) return;

        _spheneMediator.Publish(new HaltScanMessage(nameof(ConvertTextureFiles)));
        int currentTexture = 0;
        foreach (var texture in textures)
        {
            if (token.IsCancellationRequested) break;

            progress.Report((texture.Key, ++currentTexture));

            logger.LogInformation("Converting Texture {path} to {type}", texture.Key, TextureType.Bc7Tex);
            var convertTask = _penumbraConvertTextureFile.Invoke(texture.Key, texture.Key, TextureType.Bc7Tex, mipMaps: true);
            await convertTask.ConfigureAwait(false);
            if (convertTask.IsCompletedSuccessfully && texture.Value.Any())
            {
                foreach (var duplicatedTexture in texture.Value)
                {
                    logger.LogInformation("Migrating duplicate {dup}", duplicatedTexture);
                    try
                    {
                        File.Copy(texture.Key, duplicatedTexture, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to copy duplicate {dup}", duplicatedTexture);
                    }
                }
            }
        }
        _spheneMediator.Publish(new ResumeScanMessage(nameof(ConvertTextureFiles)));

        await RedrawPlayerAsync().ConfigureAwait(false);
    }

    private void ScheduleDebouncedRedraw(int delayMs = 600)
    {
        try { _debouncedRedrawCts.Cancel(); } catch (Exception ex) { Logger.LogDebug(ex, "Failed to cancel debounced redraw CTS"); }
        try { _debouncedRedrawCts.Dispose(); } catch (Exception ex) { Logger.LogDebug(ex, "Failed to dispose debounced redraw CTS"); }
        _debouncedRedrawCts = new CancellationTokenSource();
        var token = _debouncedRedrawCts.Token;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, token).ConfigureAwait(false); }
            catch (Exception ex) { Logger.LogDebug(ex, "Debounced redraw delay failed or cancelled"); return; }
            if (token.IsCancellationRequested) return;

            try
            {
                await _dalamudUtil.RunOnFrameworkThread(async () =>
                {
                    var gameObject = await _dalamudUtil.CreateGameObjectAsync(await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    _penumbraRedraw.Invoke(gameObject!.ObjectIndex, setting: RedrawType.Redraw);
                }).ConfigureAwait(false);
            }
            catch (Exception ex) { Logger.LogDebug(ex, "Debounced redraw failed"); }
        }, token);
    }

    public async Task<Guid> CreateTemporaryCollectionAsync(ILogger logger, string uid)
    {
        if (!APIAvailable) return Guid.Empty;

        return await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            var collName = "Sphene_" + uid;
            _penumbraCreateNamedTemporaryCollection.Invoke("Sphene", collName, out var actualCollId);
            logger.LogTrace("Creating Temp Collection {collName}, GUID: {collId}", collName, actualCollId);
            return actualCollId;

        }).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, HashSet<string>>?> GetCharacterData(ILogger logger, GameObjectHandler handler)
    {
        if (!APIAvailable) return null;

        return await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            logger.LogTrace("Calling On IPC: Penumbra.GetGameObjectResourcePaths");
            var idx = handler.GetGameObject()?.ObjectIndex;
            if (idx == null) return null;
            return _penumbraResourcePaths.Invoke(idx.Value)[0];
        }).ConfigureAwait(false);
    }

    public string GetMetaManipulations()
    {
        if (!APIAvailable) return string.Empty;
        return _penumbraGetMetaManipulations.Invoke();
    }

    public async Task RedrawAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning) return;
        try
        {
            await _redrawManager.RedrawSemaphore.WaitAsync(token).ConfigureAwait(false);
            await _redrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, (chara) =>
            {
                logger.LogDebug("[{appid}] Calling on IPC: PenumbraRedraw", applicationId);
                _penumbraRedraw!.Invoke(chara.ObjectIndex, setting: RedrawType.Redraw);

            }, token).ConfigureAwait(false);
        }
        finally
        {
            _redrawManager.RedrawSemaphore.Release();
        }
    }

    public async Task RemoveTemporaryCollectionAsync(ILogger logger, Guid applicationId, Guid collId)
    {
        if (!APIAvailable) return;
        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            logger.LogTrace("[{applicationId}] Removing temp collection for {collId}", applicationId, collId);
            var ret2 = _penumbraRemoveTemporaryCollection.Invoke(collId);
            logger.LogTrace("[{applicationId}] RemoveTemporaryCollection: {ret2}", applicationId, ret2);
        }).ConfigureAwait(false);
    }

    public async Task<(string[], string[][])> ResolvePlayerPathsAsync(string[] forward, string[] reverse)
    {
        if (!APIAvailable) return ([], []);
        return await _penumbraResolvePaths.Invoke(forward, reverse).ConfigureAwait(false);
    }

    public (Guid, string)? GetCollection(ApiCollectionType type)
    {
        if (!APIAvailable) return null;
        try
        {
            return _penumbraGetCollection.InvokeFunc(type);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get collection for type {Type}", type);
            return null;
        }
    }

    public Dictionary<string, (bool Enabled, int Priority, Dictionary<string, List<string>> Settings, bool Inherited, bool Temporary)>? GetAllModSettings(Guid collectionId)
    {
        if (!APIAvailable) return null;
        try
        {
            // We want inherited settings to get the full effective configuration
            // And we want temporary settings (e.g. from Glamourer)
            var result = _penumbraGetAllModSettings.InvokeFunc(collectionId, false, false, 0);
            if (result.Item1 != PenumbraApiEc.Success)
            {
                Logger.LogWarning("GetAllModSettings failed: {Error}", result.Item1);
                return null;
            }
            return result.Item2;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get all mod settings for collection {CollectionId}", collectionId);
            return null;
        }
    }

    public string? GetModPath(string modDirectory, string modName)
    {
        if (!APIAvailable) return null;
        try
        {
            var result = _penumbraGetModPath.InvokeFunc(modDirectory, modName);
            if (result.Item1 != PenumbraApiEc.Success)
            {
                if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("[FileReplacementNew] GetModPath failed for {dir}/{name}: {code}", modDirectory, modName, result.Item1);
                return null;
            }
            return result.Item2;
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "[FileReplacementNew] Error getting mod path for {ModDirectory} {ModName}", modDirectory, modName);
            return null;
        }
    }

    public (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection) GetCollectionForObject(int objectIndex)
    {
        if (!APIAvailable) return (false, false, (Guid.Empty, string.Empty));
        try
        {
            return _penumbraGetCollectionForObject.InvokeFunc(objectIndex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get collection for object {ObjectIndex}", objectIndex);
            return (false, false, (Guid.Empty, string.Empty));
        }
    }

    public Task RedrawPlayerAsync(int delayMs = 600)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning) return Task.CompletedTask;

        ScheduleDebouncedRedraw(delayMs);
        return Task.CompletedTask;
    }

    public async Task SetManipulationDataAsync(ILogger logger, Guid applicationId, Guid collId, string manipulationData)
    {
        if (!APIAvailable) return;

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            logger.LogTrace("[{applicationId}] Manip: {data}", applicationId, manipulationData);
            var retAdd = _penumbraAddTemporaryMod.Invoke("SpheneChara_Meta", collId, [], manipulationData, 0);
            logger.LogTrace("[{applicationId}] Setting temp meta mod for {collId}, Success: {ret}", applicationId, collId, retAdd);
        }).ConfigureAwait(false);
    }

    public async Task SetTemporaryModsAsync(ILogger logger, Guid applicationId, Guid collId, Dictionary<string, string> modPaths)
    {
        if (!APIAvailable) return;

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            foreach (var mod in modPaths)
            {
                logger.LogTrace("[{applicationId}] Change: {from} => {to}", applicationId, mod.Key, mod.Value);
            }
            var retRemove = _penumbraRemoveTemporaryMod.Invoke("SpheneChara_Files", collId, 0);
            logger.LogTrace("[{applicationId}] Removing temp files mod for {collId}, Success: {ret}", applicationId, collId, retRemove);
            var retAdd = _penumbraAddTemporaryMod.Invoke("SpheneChara_Files", collId, modPaths, string.Empty, 0);
            logger.LogTrace("[{applicationId}] Setting temp files mod for {collId}, Success: {ret}", applicationId, collId, retAdd);
        }).ConfigureAwait(false);
    }

    public async Task SetTemporaryModsBatchAsync(ILogger logger, Guid applicationId, Guid collId, Dictionary<string, Dictionary<string, string>> modsByModName, IEnumerable<string> modsToRemove)
    {
        if (!APIAvailable) return;

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            // Remove old mods
            foreach (var modName in modsToRemove)
            {
                 var retRemove = _penumbraRemoveTemporaryMod.Invoke(modName, collId, 0);
                 logger.LogTrace("[{applicationId}] Removing temp mod {modName} for {collId}, Success: {ret}", applicationId, modName, collId, retRemove);
            }

            // Remove legacy "SpheneChara_Files" just in case
            _penumbraRemoveTemporaryMod.Invoke("SpheneChara_Files", collId, 0);

            // Add new mods
            foreach (var kvp in modsByModName)
            {
                var modName = kvp.Key;
                var modPaths = kvp.Value;
                
                if (modPaths.Count == 0) continue;

                foreach (var mod in modPaths)
                {
                    logger.LogTrace("[{applicationId}] [{modName}] Change: {from} => {to}", applicationId, modName, mod.Key, mod.Value);
                }

                var retAdd = _penumbraAddTemporaryMod.Invoke(modName, collId, modPaths, string.Empty, 0);
                logger.LogTrace("[{applicationId}] Setting temp mod {modName} for {collId}, Success: {ret}", applicationId, modName, collId, retAdd);
            }
        }).ConfigureAwait(false);
    }

    private void RedrawEvent(IntPtr objectAddress, int objectTableIndex)
    {
        var wasRequested = _redrawManager.TryConsumeRequestedRedraw(objectAddress);
        _redrawManager.NotifyGameObjectRedrawn(objectAddress, objectTableIndex);
        if (!wasRequested)
        {
            _spheneMediator.Publish(new PenumbraRedrawMessage(objectAddress, objectTableIndex, WasRequested: false));
        }
    }

    private void ResourceLoaded(IntPtr ptr, string arg1, string arg2)
    {
        if (ptr == IntPtr.Zero) return;

        // Always publish SCD files to allow minion sound overrides to hook in even if currently unmodded (Default collection)
        if (arg1.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)
            || string.Compare(arg1, arg2, ignoreCase: true, System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            _spheneMediator.Publish(new PenumbraResourceLoadMessage(ptr, arg1, arg2));
        }
    }

    private void PenumbraDispose()
    {
        _redrawManager.Cancel();
        _spheneMediator.Publish(new PenumbraDisposedMessage());
    }

    private void PenumbraInit()
    {
        APIAvailable = true;
        ModDirectory = _penumbraResolveModDir.Invoke();
        _spheneMediator.Publish(new PenumbraInitializedMessage());
        _penumbraRedraw!.Invoke(0, setting: RedrawType.Redraw);
    }
}
