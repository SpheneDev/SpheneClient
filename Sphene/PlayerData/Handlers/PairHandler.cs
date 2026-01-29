using Sphene.API.Data;
using Sphene.FileCache;
using Sphene.Interop.Ipc;
using Sphene.PlayerData.Factories;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Events;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.Utils;
using Sphene.WebAPI.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using ObjectKind = Sphene.API.Data.Enum.ObjectKind;
using System.Numerics;

namespace Sphene.PlayerData.Handlers;

public sealed class PairHandler : DisposableMediatorSubscriberBase
{
    private sealed record CombatData(Guid ApplicationId, CharacterData CharacterData, bool Forced);

    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileDownloadManager _downloadManager;
    private readonly FileCacheManager _fileDbManager;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly IpcManager _ipcManager;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly ServerConfigurationManager _serverConfigManager;
    private volatile bool _localVisibilityGateActive = false;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager;
    private CancellationTokenSource? _applicationCancellationTokenSource = new();
    private Guid _applicationId;
    private Task? _applicationTask;
    private CharacterData? _cachedData = null;
    private CharacterData? _lastKnownMinionData = null;
    private readonly ConcurrentDictionary<string, string> _lastKnownMinionFileOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastKnownMinionScdOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastKnownMinionOverrideHash;
    private GameObjectHandler? _charaHandler;
    private readonly Dictionary<ObjectKind, Guid?> _customizeIds = [];
    private CombatData? _dataReceivedInDowntime;
    private CancellationTokenSource? _downloadCancellationTokenSource = new();
    private bool _forceApplyMods = false;
    private bool _isVisible;
    private Guid _penumbraCollection;
    private bool _redrawOnNextApplication = false;
    private bool _forceRedrawAfterCurrentApplication = false;
    private string? _inProgressPenumbraHash;
    private string? _inProgressGlamourerHash;
    private string? _inProgressRestHash;
    private string? _inProgressDataHash;
    private string? _lastSuccessfullyAppliedPenumbraHash;
    private string? _lastSuccessfullyAppliedGlamourerHash;
    private string? _lastSuccessfullyAppliedRestHash;
    private string? _lastSuccessfullyAppliedDataHash;
    private nint _lastSuccessfullyAppliedCharacterAddress = nint.Zero;
    private nint _lastBoundMinionAddress = nint.Zero;
    private nint _lastAppliedMinionAddress = nint.Zero;
    private bool _minionReapplyInProgress = false;
    private bool _initIdentMissingLogged = false;
    private bool _proximityReportedVisible = false;
    private DateTime _postZoneCheckUntil = DateTime.MinValue;
    private DateTime _postZoneLastCheck = DateTime.MinValue;
    private bool _postZoneReaffirmDone = false;
    private bool _forceHonorificReapply = false;
    private bool _pendingPenumbraReapply = false;
    private DateTime _lastFrameworkUpdateError = DateTime.MinValue;
    private DateTime _lastMinionReapplyAttempt = DateTime.MinValue;
    private DateTime _lastMinionCollectionBindAttempt = DateTime.MinValue;
    private DateTime _lastPlayerCollectionBindAttempt = DateTime.MinValue;
    private DateTime _lastMinionScdOverrideAttempt = DateTime.MinValue;
    private DateTime _lastMinionTempModsApplyAttempt = DateTime.MinValue;
    private nint _lastMinionTempModsAddress = nint.Zero;
    private int _lastMinionTempModsHash;
    private int _minionTempModsApplyInProgress;
    private const int MinionReapplyRetryDelayMs = 500;
    private const int MinionCollectionBindRetryDelayMs = 1500;
    private const int MinionTempModsCooldownMs = 2000;

    public PairHandler(ILogger<PairHandler> logger, Pair pair,
        GameObjectHandlerFactory gameObjectHandlerFactory,
        IpcManager ipcManager, FileDownloadManager transferManager,
        PluginWarningNotificationService pluginWarningNotificationManager,
        DalamudUtilService dalamudUtil, IHostApplicationLifetime lifetime,
        FileCacheManager fileDbManager, SpheneMediator mediator,
        PlayerPerformanceService playerPerformanceService,
        ServerConfigurationManager serverConfigManager,
        VisibilityGateService visibilityGateService) : base(logger, mediator)
    {
        Pair = pair;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _ipcManager = ipcManager;
        _downloadManager = transferManager;
        _pluginWarningNotificationManager = pluginWarningNotificationManager;
        _dalamudUtil = dalamudUtil;
        _lifetime = lifetime;
        _fileDbManager = fileDbManager;
        _playerPerformanceService = playerPerformanceService;
        _serverConfigManager = serverConfigManager;
        _localVisibilityGateActive = visibilityGateService.IsGateActive;
        // Initialize Penumbra collection asynchronously to avoid blocking constructor
        _ = Task.Run(async () => 
        {
            try
            {
                _penumbraCollection = await _ipcManager.Penumbra.CreateTemporaryCollectionAsync(logger, Pair.UserData.UID).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to create Penumbra collection for {uid}", Pair.UserData.UID);
            }
        });

        Mediator.Subscribe<FrameworkUpdateMessage>(this, (_) => FrameworkUpdate());
        Mediator.Subscribe<PenumbraResourceLoadMessage>(this, OnPenumbraResourceLoad);
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (_) =>
        {
            _localVisibilityGateActive = true;
            _downloadCancellationTokenSource?.CancelDispose();
            _charaHandler?.Invalidate();
            if (IsVisible)
            {
                IsVisible = false;
                Pair.ReportVisibility(false);
                _proximityReportedVisible = false;
            }
            _postZoneReaffirmDone = false;
        });
        Mediator.Subscribe<ZoneSwitchEndMessage>(this, (_) =>
        {
            _localVisibilityGateActive = false;
            _postZoneCheckUntil = DateTime.UtcNow.AddSeconds(20);
            _postZoneLastCheck = DateTime.MinValue;
            // Ensure we will re-report proximity when encountering the player after zoning
            _proximityReportedVisible = false;
            _postZoneReaffirmDone = false;
            _forceHonorificReapply = true;
        });
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) =>
        {
            _localVisibilityGateActive = true;
            _downloadCancellationTokenSource?.CancelDispose();
            _charaHandler?.Invalidate();
            if (IsVisible)
            {
                IsVisible = false;
                Pair.ReportVisibility(false);
                _proximityReportedVisible = false;
            }
        });
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) =>
        {
            _localVisibilityGateActive = false;
        });
        Mediator.Subscribe<PenumbraInitializedMessage>(this, msg =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _penumbraCollection = await _ipcManager.Penumbra.CreateTemporaryCollectionAsync(logger, Pair.UserData.UID).ConfigureAwait(false);
                    if (!IsVisible && _charaHandler != null)
                    {
                        PlayerName = string.Empty;
                        _charaHandler.Dispose();
                        _charaHandler = null;
                    }
                    else if (IsVisible && _cachedData != null && _charaHandler != null && _charaHandler.Address != nint.Zero)
                    {
                        ApplyCharacterData(Guid.NewGuid(), _cachedData, forceApplyCustomization: true);
                        _pendingPenumbraReapply = false;
                    }
                    else if (_cachedData != null)
                    {
                        _pendingPenumbraReapply = true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to recreate Penumbra collection for {uid}", Pair.UserData.UID);
                }
            });
        });
        Mediator.Subscribe<PenumbraDisposedMessage>(this, _ =>
        {
            _pendingPenumbraReapply = true;
        });
        Mediator.Subscribe<ClassJobChangedMessage>(this, (msg) =>
        {
            if (msg.GameObjectHandler == _charaHandler)
            {
                _redrawOnNextApplication = true;
            }
        });
        Mediator.Subscribe<CombatOrPerformanceEndMessage>(this, (msg) =>
        {
            if (IsVisible && _dataReceivedInDowntime != null)
            {
                ApplyCharacterData(_dataReceivedInDowntime.ApplicationId,
                    _dataReceivedInDowntime.CharacterData, _dataReceivedInDowntime.Forced);
                _dataReceivedInDowntime = null;
            }
        });
        Mediator.Subscribe<CombatOrPerformanceStartMessage>(this, _ =>
        {
            _dataReceivedInDowntime = null;
            _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate();
            _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate();
        });

        LastAppliedDataBytes = -1;
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                string text = "User Visibility Changed, now: " + (_isVisible ? "Is Visible" : "Is not Visible");
                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
                    EventSeverity.Informational, text)));
                Mediator.Publish(new StructuralRefreshUiMessage());
            }
        }
    }

    public long LastAppliedDataBytes { get; private set; }
    public Pair Pair { get; private set; }
    public nint PlayerCharacter => _charaHandler?.Address ?? nint.Zero;
    public unsafe uint PlayerCharacterId => (_charaHandler?.Address ?? nint.Zero) == nint.Zero
        ? uint.MaxValue
        : ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_charaHandler!.Address)->EntityId;
    public string? PlayerName { get; private set; }
    public string PlayerNameHash => Pair.Ident;

    internal bool IsCharacterDataAppliedForCurrentCharacter(CharacterData data)
    {
        if (_charaHandler == null || _charaHandler.Address == nint.Zero)
        {
            return false;
        }

        if (_lastSuccessfullyAppliedCharacterAddress != _charaHandler.Address)
        {
            return false;
        }

        var hasComponentHashes = !string.IsNullOrEmpty(_lastSuccessfullyAppliedPenumbraHash)
            && !string.IsNullOrEmpty(_lastSuccessfullyAppliedGlamourerHash)
            && !string.IsNullOrEmpty(_lastSuccessfullyAppliedRestHash);

        if (hasComponentHashes
            && string.Equals(_lastSuccessfullyAppliedPenumbraHash, data.PenumbraHash.Value, StringComparison.Ordinal)
            && string.Equals(_lastSuccessfullyAppliedGlamourerHash, data.GlamourerHash.Value, StringComparison.Ordinal)
            && string.Equals(_lastSuccessfullyAppliedRestHash, data.RestHash.Value, StringComparison.Ordinal))
        {
            return true;
        }

        return AreDataHashesEqual(data, _lastSuccessfullyAppliedDataHash);
    }

    public void ApplyCharacterData(Guid applicationBase, CharacterData characterData, bool forceApplyCustomization = false)
    {
        if (_dalamudUtil.IsInCombatOrPerforming)
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: you are in combat or performing music, deferring application")));
            Logger.LogDebug("[BASE-{appBase}] Received data but player is in combat or performing", applicationBase);
            _dataReceivedInDowntime = new(applicationBase, characterData, forceApplyCustomization);
            SetUploading(isUploading: false);
            return;
        }

        if (_charaHandler == null || (PlayerCharacter == IntPtr.Zero))
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: Receiving Player is in an invalid state, deferring application")));
            Logger.LogDebug("[BASE-{appBase}] Received data but player was in invalid state, charaHandlerIsNull: {charaIsNull}, playerPointerIsNull: {ptrIsNull}",
                applicationBase, _charaHandler == null, PlayerCharacter == IntPtr.Zero);
            var hasDiffMods = characterData.CheckUpdatedData(applicationBase, _cachedData, Logger,
                this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _forceApplyMods = hasDiffMods || _forceApplyMods || (PlayerCharacter == IntPtr.Zero && _cachedData == null);
            _cachedData = characterData;
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase, _cachedData.DataHash.Value, _forceApplyMods);
            return;
        }

        SetUploading(isUploading: false);

        Logger.LogDebug("[BASE-{appbase}] Applying data for {player}, forceApplyCustomization: {forced}, forceApplyMods: {forceMods}", applicationBase, this, forceApplyCustomization, _forceApplyMods);
        Logger.LogDebug("[BASE-{appbase}] Hash for data is {newHash}, current cache hash is {oldHash}", applicationBase, characterData.DataHash.Value, _cachedData?.DataHash.Value ?? "NODATA");

        if (_applicationTask != null
            && !_applicationTask.IsCompleted
            && (AreComponentHashesEqual(characterData, _inProgressPenumbraHash, _inProgressGlamourerHash, _inProgressRestHash)
                || AreDataHashesEqual(characterData, _inProgressDataHash)))
        {
            if (forceApplyCustomization || _forceApplyMods || _redrawOnNextApplication)
            {
                _forceRedrawAfterCurrentApplication = true;
                _redrawOnNextApplication = false;
            }
            Logger.LogDebug("[BASE-{appbase}] Skipping application - component hashes already in progress", applicationBase);
            return;
        }

        var hashesAreEqual = AreComponentHashesEqual(characterData, _cachedData) || AreDataHashesEqual(characterData, _cachedData);
        var hasNewCharacterAddress = _charaHandler != null && _charaHandler.Address != _lastSuccessfullyAppliedCharacterAddress;
        if (hashesAreEqual && !forceApplyCustomization && !_forceApplyMods && !_redrawOnNextApplication && !hasNewCharacterAddress)
        {
            Logger.LogTrace("[BASE-{appbase}] Skipping application - hash unchanged and no forced customization", applicationBase);
            return;
        }
        if (hashesAreEqual && hasNewCharacterAddress && !forceApplyCustomization)
        {
            Logger.LogDebug("[BASE-{appbase}] Reapplying customization for new character address", applicationBase);
            forceApplyCustomization = true;
        }
        
        // Log when we're applying despite same hash (due to forced application)
        if (hashesAreEqual && forceApplyCustomization)
        {
            Logger.LogDebug("[BASE-{appbase}] Applying despite same hash due to forced customization", applicationBase);
        }

        if (_dalamudUtil.IsInCutscene || _dalamudUtil.IsInGpose || !_ipcManager.Penumbra.APIAvailable || !_ipcManager.Glamourer.APIAvailable)
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: you are in GPose, a Cutscene or Penumbra/Glamourer is not available")));
            Logger.LogInformation("[BASE-{appbase}] Application of data for {player} while in cutscene/gpose or Penumbra/Glamourer unavailable, returning", applicationBase, this);
            return;
        }

        Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
            "Applying Character Data")));

        _forceApplyMods |= forceApplyCustomization;

        _inProgressPenumbraHash = characterData.PenumbraHash.Value;
        _inProgressGlamourerHash = characterData.GlamourerHash.Value;
        _inProgressRestHash = characterData.RestHash.Value;
        _inProgressDataHash = characterData.DataHash?.Value;

        var charaDataToUpdate = characterData.CheckUpdatedData(applicationBase, _cachedData?.DeepClone() ?? new(), Logger, this, forceApplyCustomization, _forceApplyMods);

        if (_charaHandler != null && _forceApplyMods)
        {
            _forceApplyMods = false;
        }

        if (_redrawOnNextApplication && charaDataToUpdate.TryGetValue(ObjectKind.Player, out var player))
        {
            player.Add(PlayerChanges.ForcedRedraw);
            _redrawOnNextApplication = false;
        }

        if (charaDataToUpdate.TryGetValue(ObjectKind.Player, out var playerChanges))
        {
            _pluginWarningNotificationManager.NotifyForMissingPlugins(Pair.UserData, PlayerName!, playerChanges);
        }

        Logger.LogDebug("[BASE-{appbase}] Downloading and applying character for {name}", applicationBase, this);

        DownloadAndApplyCharacter(applicationBase, characterData.DeepClone(), charaDataToUpdate);
    }

    public override string ToString()
    {
        return Pair == null
            ? base.ToString() ?? string.Empty
            : Pair.UserData.AliasOrUID + ":" + PlayerName + ":" + (PlayerCharacter != nint.Zero ? "HasChar" : "NoChar");
    }

    internal void SetUploading(bool isUploading = true)
    {
        Logger.LogTrace("Setting {this} uploading {uploading}", this, isUploading);
        if (_charaHandler != null)
        {
            Mediator.Publish(new PlayerUploadingMessage(_charaHandler, isUploading));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        SetUploading(isUploading: false);
        var name = PlayerName;
        Logger.LogDebug("Disposing {name} ({user})", name, Pair);
        try
        {
            Guid applicationId = Guid.NewGuid();
            _applicationCancellationTokenSource?.Cancel();
            _applicationCancellationTokenSource?.Dispose();
            _applicationCancellationTokenSource = null;
            _downloadCancellationTokenSource?.Cancel();
            _downloadCancellationTokenSource?.Dispose();
            _downloadCancellationTokenSource = null;
            _downloadManager.Dispose();
            _charaHandler?.Dispose();
            _charaHandler = null;

            if (!string.IsNullOrEmpty(name))
            {
                Mediator.Publish(new EventMessage(new Event(name, Pair.UserData, nameof(PairHandler), EventSeverity.Informational, "Disposing User")));
            }

            if (_lifetime.ApplicationStopping.IsCancellationRequested) return;

            if (_dalamudUtil is { IsZoning: false, IsInCutscene: false } && !string.IsNullOrEmpty(name))
            {
                Logger.LogTrace("[{applicationId}] Restoring state for {name} ({OnlineUser})", applicationId, name, Pair.UserPair);
                Logger.LogDebug("[{applicationId}] Removing Temp Collection for {name} ({user})", applicationId, name, Pair.UserPair);
                _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(Logger, applicationId, _penumbraCollection).GetAwaiter().GetResult();
                if (!IsVisible)
                {
                    Logger.LogDebug("[{applicationId}] Restoring Glamourer for {name} ({user})", applicationId, name, Pair.UserPair);
                    _ipcManager.Glamourer.RevertByNameAsync(Logger, name, applicationId).GetAwaiter().GetResult();
                }
                else
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(TimeSpan.FromSeconds(60));

                    Logger.LogInformation("[{applicationId}] CachedData is null {isNull}, contains things: {contains}", applicationId, _cachedData == null, _cachedData?.FileReplacements.Any() ?? false);

                    foreach (KeyValuePair<ObjectKind, List<FileReplacementData>> item in _cachedData?.FileReplacements ?? [])
                    {
                        try
                        {
                            RevertCustomizationDataAsync(item.Key, name, applicationId, cts.Token).GetAwaiter().GetResult();
                        }
                        catch (InvalidOperationException ex)
                        {
                            Logger.LogWarning(ex, "Failed disposing player (not present anymore?)");
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error on disposal of {name}", name);
        }
        finally
        {
            PlayerName = null;
            _cachedData = null;
        Pair.ReportVisibility(false);
        Logger.LogDebug("Disposing {name} complete", name);
        }
    }

    private async Task ApplyCustomizationDataAsync(Guid applicationId, KeyValuePair<ObjectKind, HashSet<PlayerChanges>> changes, CharacterData charaData, CancellationToken token)
    {
        if (PlayerCharacter == nint.Zero) return;
        var ptr = PlayerCharacter;

        var handler = changes.Key switch
        {
            ObjectKind.Player => _charaHandler!,
            ObjectKind.Companion => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetCompanionPtr(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.MinionOrMount => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetMinionOrMountPtr(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.Pet => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetPetPtr(ptr), isWatched: false).ConfigureAwait(false),
            _ => throw new NotSupportedException("ObjectKind not supported: " + changes.Key)
        };

        try
        {
            if (handler.Address == nint.Zero)
            {
                return;
            }

            Logger.LogDebug("[{applicationId}] Applying Customization Data for {handler}", applicationId, handler);
            if (handler.ObjectKind != ObjectKind.MinionOrMount)
            {
                await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, handler, applicationId, 30000, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            foreach (var change in changes.Value.OrderBy(p => (int)p))
            {
                Logger.LogDebug("[{applicationId}] Processing {change} for {handler}", applicationId, change, handler);
                switch (change)
                {
                    case PlayerChanges.Customize:
                        if (charaData.CustomizePlusData.TryGetValue(changes.Key, out var customizePlusData))
                        {
                            _customizeIds[changes.Key] = await _ipcManager.CustomizePlus.SetBodyScaleAsync(handler.Address, customizePlusData).ConfigureAwait(false);
                        }
                        else if (_customizeIds.TryGetValue(changes.Key, out var customizeId))
                        {
                            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                            _customizeIds.Remove(changes.Key);
                        }
                        break;

                    case PlayerChanges.Heels:
                        await _ipcManager.Heels.SetOffsetForPlayerAsync(handler.Address, charaData.HeelsData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.Honorific:
                        await _ipcManager.Honorific.SetTitleAsync(handler.Address, charaData.HonorificData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.Glamourer:
                        if (charaData.GlamourerData.TryGetValue(changes.Key, out var glamourerData))
                        {
                            if (changes.Key == ObjectKind.MinionOrMount
                                && _penumbraCollection != Guid.Empty
                                && charaData.FileReplacements.TryGetValue(ObjectKind.MinionOrMount, out var minionFileReplacements)
                                && minionFileReplacements.Count > 0)
                            {
                                await EnsureMinionCollectionBindingsAsync(handler.Address).ConfigureAwait(false);
                            }
                            await _ipcManager.Glamourer.ApplyAllAsync(Logger, handler, glamourerData, applicationId, token).ConfigureAwait(false);
                        }
                        break;

                    case PlayerChanges.Moodles:
                        await _ipcManager.Moodles.SetStatusAsync(handler.Address, charaData.MoodlesData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.PetNames:
                        await _ipcManager.PetNames.SetPlayerData(handler.Address, charaData.PetNamesData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.ForcedRedraw:
                        if (changes.Key == ObjectKind.MinionOrMount
                            && _penumbraCollection != Guid.Empty
                            && charaData.FileReplacements.TryGetValue(ObjectKind.MinionOrMount, out var minionReplacements)
                            && minionReplacements.Count > 0)
                        {
                            await EnsureMinionCollectionBindingsAsync(handler.Address).ConfigureAwait(false);
                        }
                        await _ipcManager.Penumbra.RedrawAsync(Logger, handler, applicationId, token).ConfigureAwait(false);
                        break;

                    default:
                        break;
                }
                token.ThrowIfCancellationRequested();
            }

        }
        finally
        {
            if (handler != _charaHandler) handler.Dispose();
        }
    }

    private static bool AreComponentHashesEqual(CharacterData newData, CharacterData? cachedData)
    {
        if (cachedData == null) return false;
        return string.Equals(newData.PenumbraHash.Value, cachedData.PenumbraHash.Value, StringComparison.Ordinal)
            && string.Equals(newData.GlamourerHash.Value, cachedData.GlamourerHash.Value, StringComparison.Ordinal)
            && string.Equals(newData.RestHash.Value, cachedData.RestHash.Value, StringComparison.Ordinal);
    }

    private static bool AreComponentHashesEqual(CharacterData cachedData, string? penumbraHash, string? glamourerHash, string? restHash)
    {
        if (string.IsNullOrEmpty(penumbraHash) || string.IsNullOrEmpty(glamourerHash) || string.IsNullOrEmpty(restHash))
        {
            return false;
        }

        return string.Equals(cachedData.PenumbraHash.Value, penumbraHash, StringComparison.Ordinal)
            && string.Equals(cachedData.GlamourerHash.Value, glamourerHash, StringComparison.Ordinal)
            && string.Equals(cachedData.RestHash.Value, restHash, StringComparison.Ordinal);
    }

    private static bool AreDataHashesEqual(CharacterData newData, CharacterData? cachedData)
    {
        if (cachedData == null) return false;
        var newHash = newData.DataHash?.Value;
        var oldHash = cachedData.DataHash?.Value;
        if (string.IsNullOrEmpty(newHash) || string.IsNullOrEmpty(oldHash))
        {
            return false;
        }

        return string.Equals(newHash, oldHash, StringComparison.Ordinal);
    }

    private static bool AreDataHashesEqual(CharacterData newData, string? dataHash)
    {
        if (string.IsNullOrEmpty(dataHash))
        {
            return false;
        }

        var newHash = newData.DataHash?.Value;
        if (string.IsNullOrEmpty(newHash))
        {
            return false;
        }

        return string.Equals(newHash, dataHash, StringComparison.Ordinal);
    }

    private static bool HasMinionData(CharacterData data)
    {
        if (data.CustomizePlusData.TryGetValue(ObjectKind.MinionOrMount, out var customizeData) && !string.IsNullOrEmpty(customizeData))
        {
            return true;
        }

        if (data.GlamourerData.TryGetValue(ObjectKind.MinionOrMount, out var glamourerData) && !string.IsNullOrEmpty(glamourerData))
        {
            return true;
        }

        if (data.FileReplacements.TryGetValue(ObjectKind.MinionOrMount, out var replacements) && replacements.Count > 0)
        {
            return true;
        }

        return false;
    }

    private void UpdateLastKnownMinionData(CharacterData data)
    {
        if (HasMinionData(data))
        {
            _lastKnownMinionData = data;
            RefreshMinionFileOverrides(data);
        }
    }

    private void RefreshMinionFileOverrides(CharacterData data)
    {
        if (!data.FileReplacements.TryGetValue(ObjectKind.MinionOrMount, out var minionReplacements) || minionReplacements.Count == 0)
        {
            _lastKnownMinionOverrideHash = null;
            _lastKnownMinionFileOverrides.Clear();
            _lastKnownMinionScdOverrides.Clear();
            return;
        }

        var minionHash = ComputeMinionDataHash(minionReplacements);
        if (string.Equals(_lastKnownMinionOverrideHash, minionHash, StringComparison.Ordinal)
            && !_lastKnownMinionFileOverrides.IsEmpty)
        {
            return;
        }

        _lastKnownMinionOverrideHash = minionHash;
        _lastKnownMinionFileOverrides.Clear();
        _lastKnownMinionScdOverrides.Clear();

        var missingFiles = TryCalculateModdedDictionary(Guid.NewGuid(), data, out var moddedPaths, CancellationToken.None);
        if (missingFiles.Count > 0 || moddedPaths.Count == 0)
        {
            return;
        }

        foreach (var entry in moddedPaths)
        {
            var normalizedGamePath = entry.Key.GamePath.Replace('\\', '/').ToLowerInvariant();
            _lastKnownMinionFileOverrides[normalizedGamePath] = entry.Value;
            if (normalizedGamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
            {
                _lastKnownMinionScdOverrides[normalizedGamePath] = entry.Value;
            }
        }

        if (_ipcManager.Penumbra.APIAvailable && !_dalamudUtil.IsInCutscene && !_dalamudUtil.IsInGpose)
        {
            var tempMods = new Dictionary<string, string>(_lastKnownMinionFileOverrides, StringComparer.Ordinal);
            _ = Task.Run(async () =>
            {
                try
                {
                    await ApplyMinionTempModsToCollectionAsync(tempMods).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Minion temp mods preapply failed for {this}", this);
                }
            });
        }
    }

    private static string ComputeMinionDataHash(List<FileReplacementData> minionReplacements)
    {
        if (minionReplacements == null || minionReplacements.Count == 0) return string.Empty;
        var hash = new HashCode();
        foreach (var item in minionReplacements.OrderBy(x => x.Hash, StringComparer.Ordinal))
        {
            hash.Add(item.Hash, StringComparer.Ordinal);
            if (item.GamePaths != null)
            {
                foreach (var path in item.GamePaths.OrderBy(x => x, StringComparer.Ordinal))
                {
                    hash.Add(path, StringComparer.Ordinal);
                }
            }
            hash.Add(item.FileSwapPath, StringComparer.Ordinal);
        }
        return hash.ToHashCode().ToString();
    }

    private CharacterData? GetMinionReapplyData()
    {
        if (_cachedData != null && HasMinionData(_cachedData))
        {
            return _cachedData;
        }

        if (_lastKnownMinionData != null && HasMinionData(_lastKnownMinionData))
        {
            return _lastKnownMinionData;
        }

        return null;
    }

    private void DownloadAndApplyCharacter(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData)
    {
        if (!updatedData.Any())
        {
            Logger.LogDebug("[BASE-{appBase}] Nothing to update for {obj}", applicationBase, this);
            _cachedData = charaData;
            UpdateLastKnownMinionData(charaData);
            if (_charaHandler != null)
            {
                _lastSuccessfullyAppliedPenumbraHash = charaData.PenumbraHash.Value;
                _lastSuccessfullyAppliedGlamourerHash = charaData.GlamourerHash.Value;
                _lastSuccessfullyAppliedRestHash = charaData.RestHash.Value;
            _lastSuccessfullyAppliedDataHash = charaData.DataHash?.Value;
                _lastSuccessfullyAppliedCharacterAddress = _charaHandler.Address;
            }
            Mediator.Publish(new CharacterDataApplicationCompletedMessage(PlayerName ?? string.Empty, Pair.UserData.UID, applicationBase, true));
            return;
        }

        var updateModdedPaths = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModFiles));
        var updateManip = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModManip));

        _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate() ?? new CancellationTokenSource();
        var downloadToken = _downloadCancellationTokenSource.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadAndApplyCharacterAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, downloadToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("DownloadAndApplyCharacterAsync was cancelled for {obj}", this);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error in DownloadAndApplyCharacterAsync for {obj}: {message}", this, ex.Message);
                
                // Publish failure message to notify other components
                Mediator.Publish(new CharacterDataApplicationCompletedMessage(PlayerName ?? string.Empty, Pair.UserData.UID, applicationBase, false));
            }
        });
    }

    private Task? _pairDownloadTask;

    private async Task DownloadAndApplyCharacterAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData,
        bool updateModdedPaths, bool updateManip, CancellationToken downloadToken)
    {
        Dictionary<(string GamePath, string? Hash), string> moddedPaths = [];

        List<FileReplacementData> toDownloadReplacements = [];
        if (updateModdedPaths)
        {
            int attempts = 0;
            toDownloadReplacements = TryCalculateModdedDictionary(applicationBase, charaData, out moddedPaths, downloadToken);

            while (toDownloadReplacements.Count > 0 && attempts++ <= 10 && !downloadToken.IsCancellationRequested)
            {
                if (_pairDownloadTask != null && !_pairDownloadTask.IsCompleted)
                {
                    Logger.LogDebug("[BASE-{appBase}] Finishing prior running download task for player {name}, {kind}", applicationBase, PlayerName, updatedData);
                    await _pairDownloadTask.ConfigureAwait(false);
                }

                Logger.LogDebug("[BASE-{appBase}] Downloading missing files for player {name}, {kind}", applicationBase, PlayerName, updatedData);

                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
                    $"Starting download for {toDownloadReplacements.Count} files")));
                var toDownloadFiles = await _downloadManager.InitiateDownloadList(_charaHandler!, toDownloadReplacements, downloadToken).ConfigureAwait(false);

                if (!_playerPerformanceService.ComputeAndAutoPauseOnVRAMUsageThresholds(this, charaData, toDownloadFiles))
                {
                    _downloadManager.ClearDownload();
                    return;
                }

                _pairDownloadTask = Task.Run(async () => await _downloadManager.DownloadFiles(_charaHandler!, toDownloadReplacements, downloadToken).ConfigureAwait(false));

                await _pairDownloadTask.ConfigureAwait(false);

                if (downloadToken.IsCancellationRequested)
                {
                    Logger.LogTrace("[BASE-{appBase}] Detected cancellation", applicationBase);
                    return;
                }

                toDownloadReplacements = TryCalculateModdedDictionary(applicationBase, charaData, out moddedPaths, downloadToken);

                if (toDownloadReplacements.TrueForAll(c => _downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, c.Hash, StringComparison.Ordinal))))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), downloadToken).ConfigureAwait(false);
            }

            if (toDownloadReplacements.Count > 0)
            {
                Logger.LogWarning("[BASE-{appBase}] Missing {count} files after download attempts for player {name}, {kind}", applicationBase, toDownloadReplacements.Count, PlayerName, updatedData);
                Mediator.Publish(new CharacterDataApplicationCompletedMessage(PlayerName ?? string.Empty, Pair.UserData.UID, applicationBase, false));
                return;
            }

            if (!await _playerPerformanceService.CheckBothThresholds(this, charaData).ConfigureAwait(false))
                return;
        }

        downloadToken.ThrowIfCancellationRequested();

        var appToken = _applicationCancellationTokenSource?.Token;
        while ((!_applicationTask?.IsCompleted ?? false)
               && !downloadToken.IsCancellationRequested
               && (!appToken?.IsCancellationRequested ?? false))
        {
            // block until current application is done
            Logger.LogDebug("[BASE-{appBase}] Waiting for current data application (Id: {id}) for player ({handler}) to finish", applicationBase, _applicationId, PlayerName);
            await Task.Delay(250).ConfigureAwait(false);
        }

        if (downloadToken.IsCancellationRequested || (appToken?.IsCancellationRequested ?? false)) return;

        if (updateModdedPaths && moddedPaths.Count > 0 && charaData.FileReplacements.TryGetValue(ObjectKind.MinionOrMount, out var minionRepls) && minionRepls.Count > 0)
        {
            var minionHash = ComputeMinionDataHash(minionRepls);
            if (!string.Equals(_lastKnownMinionOverrideHash, minionHash, StringComparison.Ordinal))
            {
                _lastKnownMinionFileOverrides.Clear();
                _lastKnownMinionScdOverrides.Clear();
                foreach (var entry in moddedPaths)
                {
                    var normalizedGamePath = entry.Key.GamePath.Replace('\\', '/').ToLowerInvariant();
                    _lastKnownMinionFileOverrides[normalizedGamePath] = entry.Value;
                    if (normalizedGamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                    {
                        _lastKnownMinionScdOverrides[normalizedGamePath] = entry.Value;
                    }
                }
                _lastKnownMinionOverrideHash = minionHash;
            }
        }

        _applicationCancellationTokenSource = _applicationCancellationTokenSource.CancelRecreate() ?? new CancellationTokenSource();
        var token = _applicationCancellationTokenSource.Token;

        _applicationTask = ApplyCharacterDataAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, moddedPaths, token);
    }

    private async Task ApplyCharacterDataAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData, bool updateModdedPaths, bool updateManip,
        Dictionary<(string GamePath, string? Hash), string> moddedPaths, CancellationToken token)
    {
        try
        {
            _applicationId = Guid.NewGuid();
            Logger.LogDebug("[BASE-{applicationId}] Starting application task for {this}: {appId}", applicationBase, this, _applicationId);

            Logger.LogDebug("[{applicationId}] Waiting for initial draw for for {handler}", _applicationId, _charaHandler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, _charaHandler!, _applicationId, 30000, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (updateModdedPaths)
            {
                // ensure collection is set
                var objIndex = await _dalamudUtil.RunOnFrameworkThread(() => _charaHandler!.GetGameObject()!.ObjectIndex).ConfigureAwait(false);
                await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, objIndex).ConfigureAwait(false);

                await _ipcManager.Penumbra.SetTemporaryModsAsync(Logger, _applicationId, _penumbraCollection,
                    moddedPaths.ToDictionary(k => k.Key.GamePath, k => k.Value, StringComparer.Ordinal)).ConfigureAwait(false);
                LastAppliedDataBytes = -1;
                foreach (var path in moddedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(v => new FileInfo(v)).Where(p => p.Exists))
                {
                    if (LastAppliedDataBytes == -1) LastAppliedDataBytes = 0;

                    LastAppliedDataBytes += path.Length;
                }
            }

            if (updateManip)
            {
                await _ipcManager.Penumbra.SetManipulationDataAsync(Logger, _applicationId, _penumbraCollection, charaData.ManipulationData).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            foreach (var kind in updatedData)
            {
                await ApplyCustomizationDataAsync(_applicationId, kind, charaData, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            if (_charaHandler != null
                && updatedData.TryGetValue(ObjectKind.Player, out var playerChanges)
                && playerChanges.Contains(PlayerChanges.ModFiles)
                && !playerChanges.Contains(PlayerChanges.ForcedRedraw))
            {
                await _ipcManager.Penumbra.RedrawAsync(Logger, _charaHandler, _applicationId, token).ConfigureAwait(false);
            }

            if (_forceRedrawAfterCurrentApplication
                && _charaHandler != null
                && (!updatedData.TryGetValue(ObjectKind.Player, out var playerUpdates)
                    || (!playerUpdates.Contains(PlayerChanges.ForcedRedraw) && !playerUpdates.Contains(PlayerChanges.ModFiles))))
            {
                _forceRedrawAfterCurrentApplication = false;
                await _ipcManager.Penumbra.RedrawAsync(Logger, _charaHandler, _applicationId, token).ConfigureAwait(false);
            }
            else
            {
                _forceRedrawAfterCurrentApplication = false;
            }

            _cachedData = charaData;
            UpdateLastKnownMinionData(charaData);
            _lastSuccessfullyAppliedPenumbraHash = charaData.PenumbraHash.Value;
            _lastSuccessfullyAppliedGlamourerHash = charaData.GlamourerHash.Value;
            _lastSuccessfullyAppliedRestHash = charaData.RestHash.Value;
            _lastSuccessfullyAppliedDataHash = charaData.DataHash?.Value;
            _lastSuccessfullyAppliedCharacterAddress = _charaHandler?.Address ?? nint.Zero;

            Logger.LogDebug("[{applicationId}] Application finished", _applicationId);
            
            // Publish message that character data application is completed
            Mediator.Publish(new CharacterDataApplicationCompletedMessage(PlayerName ?? string.Empty, Pair.UserData.UID, _applicationId, true));
        }
        catch (Exception ex)
        {
            if (ex is AggregateException aggr && aggr.InnerExceptions.Any(e => e is ArgumentNullException))
            {
                IsVisible = false;
                _forceApplyMods = true;
                _cachedData = charaData;
                Logger.LogDebug("[{applicationId}] Cancelled, player turned null during application", _applicationId);
            }
            else
            {
                Logger.LogWarning(ex, "[{applicationId}] Cancelled", _applicationId);
            }
            
            // Publish message that character data application failed
            Mediator.Publish(new CharacterDataApplicationCompletedMessage(PlayerName ?? string.Empty, Pair.UserData.UID, _applicationId, false));
        }
        finally
        {
            _inProgressPenumbraHash = null;
            _inProgressGlamourerHash = null;
            _inProgressRestHash = null;
            _inProgressDataHash = null;
        }
    }

    private void FrameworkUpdate()
    {
        try
        {
            if (string.IsNullOrEmpty(PlayerName))
            {
                var pc = _dalamudUtil.FindPlayerByNameHash(Pair.Ident);
                if (pc == default((string, nint)))
                {
                    if (!_initIdentMissingLogged)
                    {
                        _initIdentMissingLogged = true;
                        Logger.LogDebug("Initialize deferred for {alias} - ident not found in cache: {ident}", Pair.UserData.AliasOrUID, Pair.Ident);
                    }
                    return;
                }
                Logger.LogDebug("One-Time Initializing {this}", this);
                Initialize(pc.Name);
                Logger.LogDebug("One-Time Initialized {this}", this);
                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
                    $"Initializing User For Character {pc.Name}")));
            }

            if (_charaHandler?.Address != nint.Zero)
            {
                var gameObj = _charaHandler!.GetGameObject();
                var self = _dalamudUtil.GetPlayerCharacter();
                bool inSameParty = _dalamudUtil.IsPlayerInParty(PlayerName ?? string.Empty);
                var currentLocation = _dalamudUtil.GetMapData();
                float maxRange = _dalamudUtil.GetDynamicAroundRangeMetersForLocation(currentLocation);
                var selfPos = self?.Position ?? Vector3.Zero;
                var gamePos = gameObj?.Position ?? Vector3.Zero;
                float distance = Vector3.Distance(selfPos, gamePos);
                bool withinPartyRange = !inSameParty || distance <= maxRange;

                if (DateTime.UtcNow < _postZoneCheckUntil)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _postZoneLastCheck) > TimeSpan.FromSeconds(1))
                    {
                        _postZoneLastCheck = now;
                        var screenPos = gameObj != null ? _dalamudUtil.WorldToScreen(gameObj) : Vector2.Zero;
                        bool onScreen = screenPos != Vector2.Zero;
                        bool shouldReportVisible = onScreen && withinPartyRange && !_localVisibilityGateActive;
                        if (shouldReportVisible && !_proximityReportedVisible)
                        {
                            Pair.ReportVisibility(true);
                            _proximityReportedVisible = true;
                        }
                        else if (shouldReportVisible && _proximityReportedVisible && !Pair.IsMutuallyVisible && !_postZoneReaffirmDone)
                        {
                            Logger.LogDebug("Post-zone reaffirm visibility for {this}: onScreen=true distance={dist:F1}m", this, distance);
                            Pair.ReportVisibility(true);
                            _postZoneReaffirmDone = true;
                        }
                    }
                }

                if (!_proximityReportedVisible && !_localVisibilityGateActive && withinPartyRange)
                {
                    Pair.ReportVisibility(true);
                    _proximityReportedVisible = true;
                }
                if (_proximityReportedVisible && !withinPartyRange && !_dalamudUtil.IsInGpose)
                {
                    Pair.ReportVisibility(false);
                    _proximityReportedVisible = false;
                }

                bool allowed = Pair.IsMutuallyVisible && withinPartyRange;

                if (allowed && !IsVisible)
                {
                    Guid appData = Guid.NewGuid();
                    IsVisible = true;
                    if (_cachedData != null)
                    {
                        var currentAddress = _charaHandler!.Address;
                        if (!string.IsNullOrEmpty(_cachedData.HonorificData))
                        {
                            _ = _ipcManager.Honorific.SetTitleAsync(currentAddress, _cachedData.HonorificData).ConfigureAwait(false);
                        }
                        var alreadyApplied = _cachedData != null && IsCharacterDataAppliedForCurrentCharacter(_cachedData);

                        if (!alreadyApplied)
                        {
                            Logger.LogDebug("[BASE-{appBase}] {this} visibility changed (mutual), cached data exists", appData, this);
                            _ = Task.Run(() =>
                            {
                                ApplyCharacterData(appData, _cachedData!, forceApplyCustomization: false);
                            });
                        }
                    }
                    else
                    {
                        Logger.LogDebug("{this} visibility changed (mutual), no cached data exists", this);
                    }
                }
                else if (!allowed && IsVisible)
                {
                    IsVisible = false;
                    _downloadCancellationTokenSource?.CancelDispose();
                    _downloadCancellationTokenSource = null;
                    Pair.ReportVisibility(false);
                    _proximityReportedVisible = false;
                    Logger.LogDebug("{this} visibility changed (not mutual), now: {visi}", this, IsVisible);
                }

                if (allowed && IsVisible && _pendingPenumbraReapply && _cachedData != null)
                {
                    _pendingPenumbraReapply = false;
                    ApplyCharacterData(Guid.NewGuid(), _cachedData, forceApplyCustomization: true);
                }

                if (allowed && IsVisible)
                {
                    if (!_dalamudUtil.IsInCutscene
                        && !_dalamudUtil.IsInGpose
                        && _ipcManager.Penumbra.APIAvailable
                        && DateTime.UtcNow - _lastPlayerCollectionBindAttempt > TimeSpan.FromMilliseconds(MinionCollectionBindRetryDelayMs))
                    {
                        _lastPlayerCollectionBindAttempt = DateTime.UtcNow;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await EnsurePenumbraCollectionAsync().ConfigureAwait(false);
                                if (_penumbraCollection == Guid.Empty || _charaHandler == null || _charaHandler.Address == nint.Zero)
                                {
                                    return;
                                }

                                var playerIndex = await _dalamudUtil.RunOnFrameworkThread(() => _charaHandler.GetGameObject()?.ObjectIndex).ConfigureAwait(false);
                                if (playerIndex.HasValue)
                                {
                                    await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, playerIndex.Value).ConfigureAwait(false);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogDebug(ex, "Player collection binding failed for {this}", this);
                            }
                        });
                    }

                    TryQueueMinionReapply();
                }

                if (allowed && IsVisible && _forceHonorificReapply && !string.IsNullOrEmpty(_cachedData?.HonorificData))
                {
                    _forceHonorificReapply = false;
                    _ = _ipcManager.Honorific.SetTitleAsync(PlayerCharacter, _cachedData.HonorificData).ConfigureAwait(false);
                }
            }
            else if (_charaHandler != null && _charaHandler.Address == nint.Zero && IsVisible)
            {
                IsVisible = false;
                _charaHandler.Invalidate();
                _downloadCancellationTokenSource?.CancelDispose();
                _downloadCancellationTokenSource = null;
                Pair.ReportVisibility(false);
                _proximityReportedVisible = false;
                Logger.LogTrace("{this} visibility changed, now: {visi}", this, IsVisible);
            }
        }
        catch (Exception ex)
        {
            if (_lastFrameworkUpdateError.AddSeconds(10) <= DateTime.UtcNow)
            {
                Logger.LogDebug(ex, "Error during FrameworkUpdate for {this}", this);
                _lastFrameworkUpdateError = DateTime.UtcNow;
            }
        }
    }

    private void Initialize(string name)
    {
        PlayerName = name;
        _charaHandler = _gameObjectHandlerFactory.Create(ObjectKind.Player, () => _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident), isWatched: false).GetAwaiter().GetResult();

        Logger.LogDebug("Initialized PairHandler for {alias} with name={name} ident={ident}", Pair.UserData.AliasOrUID, name, Pair.Ident);

        _serverConfigManager.AutoPopulateNoteForUid(Pair.UserData.UID, name);
        if (!_localVisibilityGateActive)
        {
            Pair.ReportVisibility(true);
            _proximityReportedVisible = true;
        }

        Mediator.Subscribe<ConnectedMessage>(this, (_) =>
        {
            // Reaffirm visibility once the hub is connected; UID becomes available then
            Logger.LogDebug("ConnectedMessage received - reaffirming visibility for {alias}", Pair.UserData.AliasOrUID);
            if (!_localVisibilityGateActive)
            {
                Pair.ReportVisibility(true);
                _proximityReportedVisible = true;
            }
        });

        Mediator.Subscribe<HubReconnectedMessage>(this, (_) =>
        {
            // Server reconnected after outage; reaffirm local proximity visibility
            Logger.LogDebug("HubReconnectedMessage received - reaffirming visibility for {alias}", Pair.UserData.AliasOrUID);
            if (!_localVisibilityGateActive)
            {
                Pair.ReportVisibility(true);
                _proximityReportedVisible = true;
            }
        });

        Mediator.Subscribe<DalamudLoginMessage>(this, (_) =>
        {
            // Client relogged; if player is locally present, reaffirm visibility
            if (_charaHandler?.Address != nint.Zero)
            {
                Logger.LogDebug("DalamudLoginMessage received - reaffirming visibility for {alias}", Pair.UserData.AliasOrUID);
                if (!_localVisibilityGateActive)
                {
                    Pair.ReportVisibility(true);
                    _proximityReportedVisible = true;
                }
            }
        });

        Mediator.Subscribe<HonorificReadyMessage>(this, msg =>
        {
            if (string.IsNullOrEmpty(_cachedData?.HonorificData)) return;
            Logger.LogTrace("Reapplying Honorific data for {this}", this);
            _ = _ipcManager.Honorific.SetTitleAsync(PlayerCharacter, _cachedData.HonorificData).ConfigureAwait(false);
        });

        Mediator.Subscribe<PetNamesReadyMessage>(this, msg =>
        {
            if (string.IsNullOrEmpty(_cachedData?.PetNamesData)) return;
            Logger.LogTrace("Reapplying Pet Names data for {this}", this);
            _ = _ipcManager.PetNames.SetPlayerData(PlayerCharacter, _cachedData.PetNamesData).ConfigureAwait(false);
        });

        _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, _charaHandler.GetGameObject()!.ObjectIndex).GetAwaiter().GetResult();
    }

    private void TryQueueMinionReapply()
    {
        if (_minionReapplyInProgress || _charaHandler == null || _charaHandler.Address == nint.Zero)
        {
            return;
        }

        if (_dalamudUtil.IsInCutscene || _dalamudUtil.IsInGpose || !_ipcManager.Penumbra.APIAvailable || !_ipcManager.Glamourer.APIAvailable)
        {
            return;
        }

        var minionAddress = _dalamudUtil.GetMinionOrMountPtr(PlayerCharacter);
        if (minionAddress == nint.Zero)
        {
            _lastAppliedMinionAddress = nint.Zero;
            _lastBoundMinionAddress = nint.Zero;
            return;
        }

        if (minionAddress == _lastAppliedMinionAddress)
        {
            return;
        }

        if (DateTime.UtcNow - _lastMinionReapplyAttempt < TimeSpan.FromMilliseconds(MinionReapplyRetryDelayMs))
        {
            return;
        }

        var minionData = GetMinionReapplyData();
        if (minionData == null)
        {
            return;
        }

        if (!TryGetMinionReapplyChanges(minionData, out var changes, out var hasCustomize, out var hasGlamourer, out var hasFileReplacements))
        {
            return;
        }

        if (DateTime.UtcNow - _lastMinionCollectionBindAttempt > TimeSpan.FromMilliseconds(MinionCollectionBindRetryDelayMs))
        {
            _lastMinionCollectionBindAttempt = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureMinionCollectionBindingsAsync(minionAddress).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Minion collection binding failed for {this}", this);
                }
            });
        }

        if (minionAddress != _lastBoundMinionAddress && hasFileReplacements)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var bound = await TryApplyMinionFileReplacementsAsync(minionAddress, minionData, CancellationToken.None).ConfigureAwait(false);
                    if (bound)
                    {
                        _lastBoundMinionAddress = minionAddress;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Early minion bind failed for {this}", this);
                }
            });
        }

        Logger.LogDebug("Queueing minion reapply for {this} address {address:X} (customize:{customize}, glamourer:{glamourer}, files:{files})",
            this, minionAddress, hasCustomize, hasGlamourer, hasFileReplacements);

        _minionReapplyInProgress = true;
        _lastMinionReapplyAttempt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                Logger.LogDebug("Reapplying cached minion data for {this}", this);
                var localChanges = new HashSet<PlayerChanges>(changes);
                var appliedFiles = false;
                if (hasFileReplacements)
                {
                    appliedFiles = await TryApplyMinionFileReplacementsAsync(minionAddress, minionData, CancellationToken.None).ConfigureAwait(false);
                }

                await ApplyCustomizationDataAsync(Guid.NewGuid(),
                    new KeyValuePair<ObjectKind, HashSet<PlayerChanges>>(ObjectKind.MinionOrMount, localChanges),
                    minionData,
                    CancellationToken.None).ConfigureAwait(false);

                if (appliedFiles)
                {
                    _lastBoundMinionAddress = minionAddress;
                }

                if (!hasFileReplacements || appliedFiles)
                {
                    _lastAppliedMinionAddress = minionAddress;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Minion reapply failed for {this}", this);
            }
            finally
            {
                _minionReapplyInProgress = false;
            }
        });
    }

    private async Task<bool> TryApplyMinionFileReplacementsAsync(nint minionAddress, CharacterData minionData, CancellationToken token)
    {
        var hasCollection = await EnsureMinionCollectionBindingsAsync(minionAddress).ConfigureAwait(false);
        if (!hasCollection)
        {
            return false;
        }

        if (_penumbraCollection == Guid.Empty)
        {
            return false;
        }

        var missingFiles = TryCalculateModdedDictionary(Guid.NewGuid(), minionData, out var moddedPaths, token);
        if (missingFiles.Count > 0 || moddedPaths.Count == 0)
        {
            return false;
        }

        var tempMods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in moddedPaths)
        {
            var normalizedGamePath = entry.Key.GamePath.Replace('\\', '/').ToLowerInvariant();
            tempMods[normalizedGamePath] = entry.Value;
        }
        return await ApplyMinionTempModsAsync(minionAddress, tempMods, token).ConfigureAwait(false);
    }

    private async Task<bool> ApplyMinionTempModsToCollectionAsync(Dictionary<string, string> tempMods)
    {
        if (tempMods.Count == 0)
        {
            return false;
        }

        await EnsurePenumbraCollectionAsync().ConfigureAwait(false);
        if (_penumbraCollection == Guid.Empty)
        {
            return false;
        }

        var tempModsHash = ComputeTempModsHash(tempMods);
        if (tempModsHash == _lastMinionTempModsHash
            && DateTime.UtcNow - _lastMinionTempModsApplyAttempt < TimeSpan.FromMilliseconds(MinionTempModsCooldownMs))
        {
            return true;
        }

        if (Interlocked.Exchange(ref _minionTempModsApplyInProgress, 1) == 1)
        {
            return true;
        }

        try
        {
            await _ipcManager.Penumbra.SetTemporaryModsAsync(Logger, Guid.NewGuid(), _penumbraCollection, tempMods).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _minionTempModsApplyInProgress, 0);
        }

        _lastMinionTempModsApplyAttempt = DateTime.UtcNow;
        _lastMinionTempModsAddress = nint.Zero;
        _lastMinionTempModsHash = tempModsHash;

        return true;
    }

    private async Task<bool> ApplyMinionTempModsAsync(nint minionAddress, Dictionary<string, string> tempMods, CancellationToken token)
    {
        if (tempMods.Count == 0)
        {
            return false;
        }

        var hasCollection = await EnsureMinionCollectionBindingsAsync(minionAddress).ConfigureAwait(false);
        if (!hasCollection || _penumbraCollection == Guid.Empty)
        {
            return false;
        }

        var tempModsHash = ComputeTempModsHash(tempMods);
        if (minionAddress == _lastMinionTempModsAddress
            && tempModsHash == _lastMinionTempModsHash
            && DateTime.UtcNow - _lastMinionTempModsApplyAttempt < TimeSpan.FromMilliseconds(MinionTempModsCooldownMs))
        {
            return true;
        }

        if (Interlocked.Exchange(ref _minionTempModsApplyInProgress, 1) == 1)
        {
            return true;
        }

        using var minionHandler = await _gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => minionAddress, isWatched: false).ConfigureAwait(false);
        try
        {
            await _ipcManager.Penumbra.SetTemporaryModsAsync(Logger, Guid.NewGuid(), _penumbraCollection, tempMods).ConfigureAwait(false);
            await _ipcManager.Penumbra.RedrawAsync(Logger, minionHandler, Guid.NewGuid(), token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _minionTempModsApplyInProgress, 0);
        }

        _lastMinionTempModsApplyAttempt = DateTime.UtcNow;
        _lastMinionTempModsAddress = minionAddress;
        _lastMinionTempModsHash = tempModsHash;

        return true;
    }

    private static int ComputeTempModsHash(Dictionary<string, string> tempMods)
    {
        var keys = new string[tempMods.Count];
        var index = 0;
        foreach (var key in tempMods.Keys)
        {
            keys[index] = key;
            index++;
        }

        Array.Sort(keys, StringComparer.Ordinal);

        var hash = new HashCode();
        foreach (var key in keys)
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(tempMods[key], StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private void OnPenumbraResourceLoad(PenumbraResourceLoadMessage msg)
    {
        if (!_ipcManager.Penumbra.APIAvailable || _lastKnownMinionScdOverrides.Count == 0)
        {
            return;
        }

        if (_dalamudUtil.IsInCutscene || _dalamudUtil.IsInGpose)
        {
            return;
        }

        var gamePath = msg.GamePath.Replace('\\', '/').ToLowerInvariant();
        if (!gamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_lastKnownMinionScdOverrides.ContainsKey(gamePath))
        {
            return;
        }

        Logger.LogDebug("Detected Minion SCD load: {path} for GameObject {ptr:X}", gamePath, msg.GameObject);

        if (DateTime.UtcNow - _lastMinionScdOverrideAttempt < TimeSpan.FromMilliseconds(MinionCollectionBindRetryDelayMs))
        {
            Logger.LogDebug("Skipping SCD override due to cooldown");
            return;
        }

        var resourceAddress = msg.GameObject;
        if (resourceAddress == nint.Zero)
        {
            return;
        }

        var playerAddress = _charaHandler?.Address ?? nint.Zero;
        var minionAddress = _dalamudUtil.GetMinionOrMountPtr(PlayerCharacter);
        
        Logger.LogDebug("SCD Load Addresses - Resource: {res:X}, Player: {plr:X}, Minion: {min:X}", resourceAddress, playerAddress, minionAddress);

        if (resourceAddress != playerAddress)
        {
            minionAddress = resourceAddress;
        }
        else if (minionAddress == nint.Zero)
        {
            return;
        }

        if (_lastKnownMinionFileOverrides.Count == 0)
        {
            return;
        }

        if (DateTime.UtcNow - _lastMinionTempModsApplyAttempt < TimeSpan.FromMilliseconds(MinionTempModsCooldownMs))
        {
            Logger.LogDebug("Skipping TempMods apply due to cooldown");
            return;
        }

        _lastMinionScdOverrideAttempt = DateTime.UtcNow;
        var tempMods = new Dictionary<string, string>(_lastKnownMinionFileOverrides, StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(async () =>
        {
            try
            {
                Logger.LogDebug("Attempting to bind collection to {addr:X} and apply temp mods", resourceAddress);
                await TryBindCollectionToGameObjectAsync(resourceAddress).ConfigureAwait(false);
                await ApplyMinionTempModsAsync(minionAddress, tempMods, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Minion SCD override retry failed for {this}", this);
            }
        });
    }

    private async Task<bool> TryBindCollectionToGameObjectAsync(nint address)
    {
        if (address == nint.Zero)
        {
            return false;
        }

        await EnsurePenumbraCollectionAsync().ConfigureAwait(false);
        if (_penumbraCollection == Guid.Empty)
        {
            Logger.LogDebug("Failed to bind collection: PenumbraCollection is Empty");
            return false;
        }

        var objectIndex = await _dalamudUtil.RunOnFrameworkThread(() => _dalamudUtil.CreateGameObject(address)?.ObjectIndex).ConfigureAwait(false);
        if (!objectIndex.HasValue)
        {
            Logger.LogDebug("Failed to bind collection: Could not find object index for address {addr:X}", address);
            return false;
        }

        Logger.LogDebug("Binding collection {coll} to object index {idx} (Address: {addr:X})", _penumbraCollection, objectIndex.Value, address);
        await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, objectIndex.Value).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> EnsureMinionCollectionBindingsAsync(nint minionAddress)
    {
        await EnsurePenumbraCollectionAsync().ConfigureAwait(false);
        if (_penumbraCollection == Guid.Empty)
        {
            return false;
        }

        int? playerIndex = null;
        if (_charaHandler != null && _charaHandler.Address != nint.Zero)
        {
            playerIndex = await _dalamudUtil.RunOnFrameworkThread(() => _charaHandler.GetGameObject()!.ObjectIndex).ConfigureAwait(false);
            await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, playerIndex.Value).ConfigureAwait(false);
        }

        int? minionIndex = null;
        using (var minionHandler = await _gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => minionAddress, isWatched: false).ConfigureAwait(false))
        {
            minionIndex = await _dalamudUtil.RunOnFrameworkThread(() => minionHandler.GetGameObject()?.ObjectIndex).ConfigureAwait(false);
        }

        if (!minionIndex.HasValue && playerIndex.HasValue)
        {
            minionIndex = playerIndex.Value + 1;
        }

        if (!minionIndex.HasValue)
        {
            return false;
        }

        await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, minionIndex.Value).ConfigureAwait(false);
        return true;
    }

    private async Task EnsurePenumbraCollectionAsync()
    {
        if (_penumbraCollection != Guid.Empty)
        {
            return;
        }

        try
        {
            _penumbraCollection = await _ipcManager.Penumbra.CreateTemporaryCollectionAsync(Logger, Pair.UserData.UID).ConfigureAwait(false);
            if (_charaHandler != null && _charaHandler.Address != nint.Zero)
            {
                var idx = await _dalamudUtil.RunOnFrameworkThread(() => _charaHandler.GetGameObject()!.ObjectIndex).ConfigureAwait(false);
                await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, idx).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to create Penumbra collection for {uid}", Pair.UserData.UID);
        }
    }
    private static bool TryGetMinionReapplyChanges(CharacterData data, out HashSet<PlayerChanges> changes, out bool hasCustomize, out bool hasGlamourer, out bool hasFileReplacements)
    {
        changes = [];
        hasCustomize = false;
        hasGlamourer = false;
        hasFileReplacements = false;

        if (data.CustomizePlusData.TryGetValue(ObjectKind.MinionOrMount, out var customizeData) && !string.IsNullOrEmpty(customizeData))
        {
            changes.Add(PlayerChanges.Customize);
            hasCustomize = true;
        }

        if (data.GlamourerData.TryGetValue(ObjectKind.MinionOrMount, out var glamourerData) && !string.IsNullOrEmpty(glamourerData))
        {
            changes.Add(PlayerChanges.Glamourer);
            hasGlamourer = true;
        }

        if (data.FileReplacements.TryGetValue(ObjectKind.MinionOrMount, out var replacements) && replacements.Count > 0)
        {
            changes.Add(PlayerChanges.ForcedRedraw);
            hasFileReplacements = true;
        }

        return changes.Count > 0;
    }

    private async Task RevertCustomizationDataAsync(ObjectKind objectKind, string name, Guid applicationId, CancellationToken cancelToken)
    {
        nint address = _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident);
        if (address == nint.Zero) return;

        Logger.LogDebug("[{applicationId}] Reverting all Customization for {alias}/{name} {objectKind}", applicationId, Pair.UserData.AliasOrUID, name, objectKind);

        if (_customizeIds.TryGetValue(objectKind, out var customizeId))
        {
            _customizeIds.Remove(objectKind);
        }

        if (objectKind == ObjectKind.Player)
        {
            using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => address, isWatched: false).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Customization and Equipment for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Heels for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Heels.RestoreOffsetForPlayerAsync(address).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring C+ for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Honorific for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Honorific.ClearTitleAsync(address).ConfigureAwait(false);
            Logger.LogDebug("[{applicationId}] Restoring Moodles for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Moodles.RevertStatusAsync(address).ConfigureAwait(false);
            Logger.LogDebug("[{applicationId}] Restoring Pet Nicknames for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.PetNames.ClearPlayerData(address).ConfigureAwait(false);
        }
        else if (objectKind == ObjectKind.MinionOrMount)
        {
            var minionOrMount = await _dalamudUtil.GetMinionOrMountAsync(address).ConfigureAwait(false);
            if (minionOrMount != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => minionOrMount, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Pet)
        {
            var pet = await _dalamudUtil.GetPetAsync(address).ConfigureAwait(false);
            if (pet != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => pet, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Companion)
        {
            var companion = await _dalamudUtil.GetCompanionAsync(address).ConfigureAwait(false);
            if (companion != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => companion, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
    }

    private List<FileReplacementData> TryCalculateModdedDictionary(Guid applicationBase, CharacterData charaData, out Dictionary<(string GamePath, string? Hash), string> moddedDictionary, CancellationToken token)
    {
        Stopwatch st = Stopwatch.StartNew();
        ConcurrentBag<FileReplacementData> missingFiles = [];
        moddedDictionary = [];
        ConcurrentDictionary<(string GamePath, string? Hash), string> outputDict = new();
        bool hasMigrationChanges = false;
        bool cancellationRequested = false;

        // Check for cancellation at the start
        if (token.IsCancellationRequested)
        {
            Logger.LogDebug("[BASE-{appBase}] Calculation cancelled before starting", applicationBase);
            return [.. missingFiles];
        }

        try
        {
            // Validate input data before processing
            if (charaData?.FileReplacements == null)
            {
                Logger.LogWarning("[BASE-{appBase}] CharacterData or FileReplacements is null", applicationBase);
                return [.. missingFiles];
            }

            var replacementList = charaData.FileReplacements.SelectMany(k => k.Value.Where(v => string.IsNullOrEmpty(v.FileSwapPath))).ToList();
            
            if (replacementList.Count == 0)
            {
                Logger.LogDebug("[BASE-{appBase}] No file replacements to process", applicationBase);
                return [.. missingFiles];
            }

            try
            {
                Parallel.ForEach(replacementList, new ParallelOptions()
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = 4
                },
                (item) =>
                {
                    try
                    {
                        // Check for cancellation without throwing
                        if (token.IsCancellationRequested)
                        {
                            cancellationRequested = true;
                            return;
                        }
                        
                        // Validate item data
                        if (item == null || string.IsNullOrEmpty(item.Hash) || item.GamePaths == null || item.GamePaths.Length == 0)
                        {
                            Logger.LogWarning("[BASE-{appBase}] Invalid FileReplacementData item: Hash={hash}, GamePaths={paths}", 
                                applicationBase, item?.Hash ?? "null", item?.GamePaths?.Length ?? 0);
                            return;
                        }

                        var fileCache = _fileDbManager.GetFileCacheByHash(item.Hash);
                        if (fileCache != null)
                        {
                            // Validate file cache data
                            if (string.IsNullOrEmpty(fileCache.ResolvedFilepath))
                            {
                                Logger.LogWarning("[BASE-{appBase}] FileCache has empty ResolvedFilepath for hash {hash}", applicationBase, item.Hash);
                                return;
                            }

                            // Check if file actually exists before processing
                            if (!File.Exists(fileCache.ResolvedFilepath))
                            {
                                Logger.LogWarning("[BASE-{appBase}] FileCache points to non-existent file: {path} for hash {hash}", 
                                    applicationBase, fileCache.ResolvedFilepath, item.Hash);
                                missingFiles.Add(item);
                                return;
                            }

                            if (string.IsNullOrEmpty(new FileInfo(fileCache.ResolvedFilepath).Extension))
                            {
                                hasMigrationChanges = true;
                                // Validate game path before splitting
                                var firstGamePath = item.GamePaths[0];
                                if (string.IsNullOrEmpty(firstGamePath) || !firstGamePath.Contains('.'))
                                {
                                    Logger.LogWarning("[BASE-{appBase}] Invalid game path for extension extraction: {path}", applicationBase, firstGamePath);
                                    return;
                                }
                                
                                try
                                {
                                    fileCache = _fileDbManager.MigrateFileHashToExtension(fileCache, firstGamePath.Split(".")[^1]);
                                }
                                catch (Exception migrationEx)
                                {
                                    Logger.LogWarning(migrationEx, "[BASE-{appBase}] Failed to migrate file hash to extension for hash {hash}", applicationBase, item.Hash);
                                    return;
                                }
                            }

                            foreach (var gamePath in item.GamePaths)
                            {
                                if (!string.IsNullOrEmpty(gamePath))
                                {
                                    outputDict[(gamePath, item.Hash)] = fileCache.ResolvedFilepath;
                                }
                            }
                        }
                        else
                        {
                            Logger.LogTrace("Missing file: {hash}", item.Hash);
                            missingFiles.Add(item);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationRequested = true;
                    }
                    catch (Exception itemEx)
                    {
                        Logger.LogWarning(itemEx, "[BASE-{appBase}] Error processing FileReplacementData item with hash {hash}. Exception: {exceptionType} - {message}. GamePaths count: {pathCount}", 
                            applicationBase, item?.Hash ?? "unknown", itemEx.GetType().Name, itemEx.Message, item?.GamePaths?.Length ?? 0);
                        
                        // Log additional details for debugging
                        if (item != null)
                        {
                            Logger.LogDebug("[BASE-{appBase}] Failed item details - Hash: {hash}, GamePaths: [{paths}]", 
                                applicationBase, item.Hash, item.GamePaths != null ? string.Join(", ", item.GamePaths) : "null");
                        }
                    }
                });
            }
            catch (OperationCanceledException)
            {
                cancellationRequested = true;
            }

            if (cancellationRequested || token.IsCancellationRequested)
            {
                Logger.LogDebug("[BASE-{appBase}] Replacement calculation was cancelled. Processed items: {count}", applicationBase, outputDict.Count);
                return [.. missingFiles];
            }

            moddedDictionary = outputDict.ToDictionary(k => k.Key, k => k.Value);

            // Process file swaps with additional validation
            var fileSwapItems = charaData.FileReplacements.SelectMany(k => k.Value.Where(v => !string.IsNullOrEmpty(v.FileSwapPath))).ToList();
            foreach (var item in fileSwapItems)
            {
                if (token.IsCancellationRequested)
                {
                    Logger.LogDebug("[BASE-{appBase}] Cancellation requested during file swap processing", applicationBase);
                    break;
                }

                if (item?.GamePaths != null && !string.IsNullOrEmpty(item.FileSwapPath))
                {
                    foreach (var gamePath in item.GamePaths)
                    {
                        if (!string.IsNullOrEmpty(gamePath))
                        {
                            Logger.LogTrace("[BASE-{appBase}] Adding file swap for {path}: {fileSwap}", applicationBase, gamePath, item.FileSwapPath);
                            moddedDictionary[(gamePath, null)] = item.FileSwapPath;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("[BASE-{appBase}] Replacement calculation was cancelled. Processed items: {count}", applicationBase, outputDict.Count);
            return [.. missingFiles];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BASE-{appBase}] Something went wrong during calculation replacements. Exception: {exceptionType} - {message}. Processed items: {processedCount}", 
                applicationBase, ex.GetType().Name, ex.Message, outputDict.Count);
            
            // Log stack trace for debugging
            Logger.LogDebug("[BASE-{appBase}] Full exception details: {stackTrace}", applicationBase, ex.ToString());
            
            // Return empty collections to prevent further errors
            moddedDictionary = [];
            return [];
        }
        
        if (hasMigrationChanges) 
        {
            try
            {
                _fileDbManager.WriteOutFullCsv();
            }
            catch (Exception csvEx)
            {
                Logger.LogWarning(csvEx, "[BASE-{appBase}] Failed to write CSV after migration changes", applicationBase);
            }
        }
        
        st.Stop();
        Logger.LogDebug("[BASE-{appBase}] ModdedPaths calculated in {time}ms, missing files: {count}, total files: {total}", applicationBase, st.ElapsedMilliseconds, missingFiles.Count, moddedDictionary.Keys.Count);
        return [.. missingFiles];
    }
}
