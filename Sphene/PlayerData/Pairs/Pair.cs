using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.User;
using Sphene.PlayerData.Factories;
using Sphene.PlayerData.Handlers;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Sphene.Services;
using Sphene.Services.CharaData;
using NotificationType = Sphene.SpheneConfiguration.Models.NotificationType;
using Sphene.Services.Events;
using Dalamud.Interface.ImGuiNotification;
using Sphene.WebAPI;
using Sphene.API.Dto.Visibility;

namespace Sphene.PlayerData.Pairs;

public class Pair : DisposableMediatorSubscriberBase
{
    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly SemaphoreSlim _creationSemaphore = new(1);
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly Lazy<ApiController> _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SpheneConfigService _configService;
    private readonly CharacterDataSqliteStore _characterDataSqliteStore;
    private CancellationTokenSource _applicationCts = new();
    private OnlineUserIdentDto? _onlineUserIdentDto = null;
    private readonly VisibilityGateService _visibilityGateService;
    private const int BaseApplyRetryDelaySeconds = 2;
    private const int MaxApplyRetryDelaySeconds = 30;
    private const int MaxApplyRetryBackoffSteps = 5;
    private const int ApplyAttemptCooldownMs = 1500;
    private CancellationTokenSource _applyRetryCts = new();
    private int _applyRetryCount = 0;
    private const int MaxApplyDebugLines = 200;
    private const string SyncProgressTag = "[SyncProgress]";
    private readonly ConcurrentQueue<string> _applyDebugLog = new();
    private string? _lastApplyAttemptHash;
    private DateTimeOffset _lastApplyAttemptTime = DateTimeOffset.MinValue;

    public Pair(ILogger<Pair> logger, UserFullPairDto userPair, PairHandlerFactory cachedPlayerFactory,
        SpheneMediator mediator, ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService, Lazy<ApiController> apiController,
        VisibilityGateService visibilityGateService, DalamudUtilService dalamudUtilService,
        SpheneConfigService configService, CharacterDataSqliteStore characterDataSqliteStore) : base(logger, mediator)
    {
        UserPair = userPair;
        _cachedPlayerFactory = cachedPlayerFactory;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _apiController = apiController;
        _visibilityGateService = visibilityGateService;
        _dalamudUtilService = dalamudUtilService;
        _configService = configService;
        _characterDataSqliteStore = characterDataSqliteStore;

        // Subscribe to character data application completion messages
        Mediator.Subscribe<CharacterDataApplicationCompletedMessage>(this, message => { _ = OnCharacterDataApplicationCompleted(message); });
        Mediator.Subscribe<GposeStartMessage>(this, _ => { WasMutuallyVisibleInGpose = IsMutuallyVisible; });
        Mediator.Subscribe<GposeEndMessage>(this, _ => { WasMutuallyVisibleInGpose = false; });
        _ = LoadPersistedLastReceivedDataAsync();
    }

    public bool HasCachedPlayer => CachedPlayer != null && !string.IsNullOrEmpty(CachedPlayer.PlayerName) && _onlineUserIdentDto != null;
    public IndividualPairStatus IndividualPairStatus => UserPair.IndividualPairStatus;
    public bool IsDirectlyPaired => IndividualPairStatus != IndividualPairStatus.None;
    public bool IsOneSidedPair => IndividualPairStatus == IndividualPairStatus.OneSided;
    public bool IsOnline => CachedPlayer != null;
    public bool IsInDuty => _dalamudUtilService.IsInDuty;

    public bool IsPaired => IndividualPairStatus == IndividualPairStatus.Bidirectional || UserPair.Groups.Any();
    public bool IsPaused => UserPair.OwnPermissions.IsPaused();
    public bool IsVisible => CachedPlayer?.IsVisible ?? false;
    public bool IsMutuallyVisible { get; private set; } = false;
    public bool WasMutuallyVisibleInGpose { get; private set; } = false;
    public bool IsInGpose { get; private set; } = false;
    public CharacterData? LastReceivedCharacterData { get; private set; }
    public CharacterData? PreviousReceivedCharacterData { get; private set; }
    public string? LastReceivedCharacterDataHash { get; private set; }
    public string? PreviousReceivedCharacterDataHash { get; private set; }
    public DateTimeOffset? LastReceivedCharacterDataTime { get; private set; }
    public DateTimeOffset? LastReceivedCharacterDataChangeTime { get; private set; }
    public bool LastReceivedContainsCharacterLegacyShpk { get; private set; } = false;
    public bool LastReceivedContainsCharacterShpk { get; private set; } = false;
    public bool LastReceivedCharacterLegacyShpkFiltered { get; private set; } = false;
    public bool LastReceivedCharacterShpkFiltered { get; private set; } = false;
    public string? PlayerName => CachedPlayer?.PlayerName ?? string.Empty;
    public long LastAppliedDataBytes => CachedPlayer?.LastAppliedDataBytes ?? -1;
    public long LastAppliedDataTris { get; set; } = -1;
    public long LastAppliedApproximateVRAMBytes { get; set; } = -1;
    public string Ident => _onlineUserIdentDto?.Ident ?? string.Empty;
    internal int ApplyRetryCount => _applyRetryCount;

    public bool TryGetPenumbraCollectionId(out Guid collectionId)
    {
        if (CachedPlayer == null)
        {
            collectionId = Guid.Empty;
            return false;
        }

        return CachedPlayer.TryGetPenumbraCollectionId(out collectionId);
    }
    
    // Data synchronization status properties
    public bool? LastAcknowledgmentSuccess { get; private set; } = null;
    public DateTimeOffset? LastAcknowledgmentTime { get; private set; } = null;
    public string? LastAcknowledgmentId { get; private set; } = null;
    public bool HasPendingAcknowledgment { get; private set; } = false;
    
    // Queue for pending acknowledgment data to handle multiple rapid requests
    private readonly ConcurrentQueue<OnlineUserCharaDataDto> _pendingAcknowledgmentQueue = new();
    private volatile int _acknowledgmentSequence = 0;

    public UserData UserData => UserPair.User;
    public bool OtherAllowsReceivingPenumbraMods => UserPair.OtherAllowsReceivingPenumbraMods;

    public UserFullPairDto UserPair { get; set; }
    private PairHandler? CachedPlayer { get; set; }
    
    public void ApplyBypassEmote(string data)
    {
        if (CachedPlayer == null) return;
        _ = CachedPlayer.ApplyBypassEmoteDataAsync(data);
    }

    public string? GetCurrentDataHash()
    {
        return LastReceivedCharacterData?.DataHash?.Value;
    }

    internal void SetMutualVisibility(bool isMutual)
    {
        if (IsMutuallyVisible == isMutual) return;
        IsMutuallyVisible = isMutual;
        Mediator.Publish(new StructuralRefreshUiMessage());
    }

    internal void SetGposeState(bool isInGpose)
    {
        if (IsInGpose == isInGpose) return;
        IsInGpose = isInGpose;
        Mediator.Publish(new StructuralRefreshUiMessage());
    }

    internal void ReportVisibility(bool isProximityVisible)
    {
        try
        {
            if (isProximityVisible && _visibilityGateService.IsGateActive)
            {
                return;
            }
            var uid = _apiController.Value.UID;
            if (string.IsNullOrEmpty(uid))
            {
                Logger.LogDebug("Skipping visibility report for {alias} - UID not available yet", UserData.AliasOrUID);
                return;
            }
            var dto = new UserVisibilityReportDto(new(uid), UserData, isProximityVisible, DateTime.UtcNow);
            Logger.LogDebug("Reporting visibility: reporter={uid}, target={target}, proximity={visible}", uid, UserData.AliasOrUID, isProximityVisible);
            _ = _apiController.Value.UserReportVisibility(dto);

            if (isProximityVisible)
            {
                if (!IsMutuallyVisible && UserPair.Groups.Any())
                {
                    SetMutualVisibility(true);
                }
            }
            else if (UserPair.Groups.Any() && IsMutuallyVisible)
            {
                SetMutualVisibility(false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to report proximity visibility for {target}", UserData.AliasOrUID);
        }
    }

    public void AddContextMenu(IMenuOpenedArgs args)
    {
        if (CachedPlayer == null || (args.Target is not MenuTargetDefault target) || target.TargetObjectId != CachedPlayer.PlayerCharacterId || IsPaused) return;

        SeStringBuilder seStringBuilder = new();
        SeStringBuilder seStringBuilder2 = new();
        SeStringBuilder seStringBuilder3 = new();
        SeStringBuilder seStringBuilder4 = new();
        SeStringBuilder seStringBuilder5 = new();
        SeStringBuilder seStringBuilder6 = new();
        var openProfileSeString = seStringBuilder.AddText("Open Profile").Build();
        var reapplyDataSeString = seStringBuilder2.AddText("Reapply last data").Build();
        var cyclePauseState = seStringBuilder3.AddText("Cycle pause state").Build();
        var changePermissions = seStringBuilder4.AddText("Change Permissions").Build();
        
        // Performance whitelist functionality
        var config = _playerPerformanceConfigService.Current;
        var userIdentifier = !string.IsNullOrEmpty(UserPair.User.Alias) ? UserPair.User.Alias : UserData.UID;
        var isWhitelisted = System.Linq.Enumerable.Contains(config.UIDsToIgnore, UserData.UID, StringComparer.Ordinal);
        var whitelistText = isWhitelisted ? "Remove from Performance Whitelist" : "Add to Performance Whitelist";
        var performanceWhitelistSeString = seStringBuilder5.AddText(whitelistText).Build();
        var tempWhitelist = config.TemporaryCollectionWhitelist;
        var isTempWhitelisted = System.Linq.Enumerable.Contains(tempWhitelist, userIdentifier, StringComparer.Ordinal) ||
                                System.Linq.Enumerable.Contains(tempWhitelist, UserData.UID, StringComparer.Ordinal);
        var tempWhitelistText = isTempWhitelisted ? "Remove from Temporary Collection Whitelist" : "Add to Temporary Collection Whitelist";
        var tempWhitelistSeString = seStringBuilder6.AddText(tempWhitelistText).Build();
        
        args.AddMenuItem(new MenuItem()
        {
            Name = openProfileSeString,
            OnClicked = (a) => Mediator.Publish(new ProfileOpenStandaloneMessage(this)),
            UseDefaultPrefix = false,
            PrefixChar = 'S',
            PrefixColor = 500
        });

        args.AddMenuItem(new MenuItem()
        {
            Name = reapplyDataSeString,
            OnClicked = (a) => ApplyLastReceivedData(forced: true),
            UseDefaultPrefix = false,
            PrefixChar = 'S',
            PrefixColor = 500
        });

        args.AddMenuItem(new MenuItem()
        {
            Name = changePermissions,
            OnClicked = (a) => Mediator.Publish(new OpenPermissionWindow(this)),
            UseDefaultPrefix = false,
            PrefixChar = 'S',
            PrefixColor = 500
        });

        args.AddMenuItem(new MenuItem()
        {
            Name = cyclePauseState,
            OnClicked = (a) => Mediator.Publish(new CyclePauseMessage(UserData)),
            UseDefaultPrefix = false,
            PrefixChar = 'S',
            PrefixColor = 500
        });
        
        // Add performance whitelist menu item
        args.AddMenuItem(new MenuItem()
        {
            Name = performanceWhitelistSeString,
            OnClicked = (a) => {
                if (isWhitelisted)
                {
                    // Remove from whitelist
                    config.UIDsToIgnore.Remove(UserData.UID);
                    Logger.LogInformation("Removed {identifier} ({uid}) from performance whitelist", userIdentifier, UserData.UID);
                }
                else
                {
                    // Add to whitelist with identifier for reference
                    config.UIDsToIgnore.Add(UserData.UID);
                    Logger.LogInformation("Added {identifier} ({uid}) to performance whitelist", userIdentifier, UserData.UID);
                }
                _playerPerformanceConfigService.Save();
            },
            UseDefaultPrefix = false,
            PrefixChar = 'S',
            PrefixColor = 500
        });

        args.AddMenuItem(new MenuItem()
        {
            Name = tempWhitelistSeString,
            OnClicked = (a) =>
            {
                if (isTempWhitelisted)
                {
                    tempWhitelist.RemoveAll(uid =>
                        string.Equals(uid, UserPair.User.Alias, StringComparison.Ordinal) ||
                        string.Equals(uid, UserData.UID, StringComparison.Ordinal));
                    Logger.LogInformation("Removed {identifier} ({uid}) from temporary collection whitelist", userIdentifier, UserData.UID);
                }
                else
                {
                    var identifierToAdd = !string.IsNullOrEmpty(UserPair.User.Alias) ? UserPair.User.Alias : UserData.UID;
                    tempWhitelist.Add(identifierToAdd);
                    Logger.LogInformation("Added {identifier} ({uid}) to temporary collection whitelist", userIdentifier, UserData.UID);
                }
                _playerPerformanceConfigService.Save();
            },
            UseDefaultPrefix = false,
            PrefixChar = 'S',
            PrefixColor = 500
        });
    }

    public void ApplyData(OnlineUserCharaDataDto data)
    {
        _applicationCts = _applicationCts.CancelRecreate();
        UpdateReceivedCharacterDataCache(data);
        ResetApplyRetry();
        bool shouldApply = CachedPlayer == null
            || LastReceivedCharacterData == null
            || !CachedPlayer.IsCharacterDataAppliedForCurrentCharacter(LastReceivedCharacterData);

        var hash = data.DataHash;
        var shortHash = string.IsNullOrEmpty(hash) ? "NONE" : hash[..Math.Min(8, hash.Length)];
        AddApplyDebug($"Data received hash={shortHash} requiresAck={data.RequiresAcknowledgment}");
        Logger.LogDebug("{tag} Receive: user={user} hash={hash} requiresAck={requiresAck} shouldApply={shouldApply}",
            SyncProgressTag, data.User.AliasOrUID, shortHash, data.RequiresAcknowledgment, shouldApply);

        if (!shouldApply)
        {
            Logger.LogDebug("{tag} Apply skipped: user={user} hash={hash} alreadyApplied=true", SyncProgressTag, data.User.AliasOrUID, shortHash);
            if (data.RequiresAcknowledgment && !string.IsNullOrEmpty(data.DataHash))
            {
                _ = SendAcknowledgmentIfRequired(data, true, true);
            }

            return;
        }

        // Assign sequence number for tracking order
        var currentSequence = Interlocked.Increment(ref _acknowledgmentSequence);
        
        if (CachedPlayer == null)
        {
            Logger.LogDebug("{tag} Apply deferred: cached player missing for uid={uid} hash={hash}", SyncProgressTag, data.User.UID, shortHash);
            _ = Task.Run(async () =>
            {
                using var timeoutCts = new CancellationTokenSource();
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
                var appToken = _applicationCts.Token;
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, appToken);
                while (CachedPlayer == null && !combined.Token.IsCancellationRequested)
                {
                    await Task.Delay(250, combined.Token).ConfigureAwait(false);
                }

                if (!combined.IsCancellationRequested)
                {
                    Logger.LogDebug("{tag} Apply delayed data: uid={uid} hash={hash}", SyncProgressTag, data.User.UID, shortHash);
                    if (shouldApply)
                    {
                        ApplyLastReceivedData();
                    }
                    
                    // Enqueue acknowledgment data for delayed sending after application completes
                    if (data.RequiresAcknowledgment && !string.IsNullOrEmpty(data.DataHash))
                    {
                        // Add sequence number to track order
                        var dataWithSequence = data.DeepClone();
                        dataWithSequence.SequenceNumber = currentSequence;
                        _pendingAcknowledgmentQueue.Enqueue(dataWithSequence);
                        Logger.LogDebug("{tag} Ack queued (delayed path): hash={hash} sequence={sequence}", 
                            SyncProgressTag,
                            data.DataHash[..Math.Min(8, data.DataHash.Length)], currentSequence);
                    }
                }
                else
                {
                    Logger.LogDebug("{tag} Apply delayed path timed out: uid={uid} hash={hash}", SyncProgressTag, data.User.UID, shortHash);
                    await SendAcknowledgmentIfRequired(data, false).ConfigureAwait(false);
                }
            });
            return;
        }

        if (shouldApply)
        {
            ApplyLastReceivedData();
        }
        
        // Enqueue acknowledgment data for delayed sending after application completes
        if (data.RequiresAcknowledgment && !string.IsNullOrEmpty(data.DataHash))
        {
            // Add sequence number to track order
            var dataWithSequence = data.DeepClone();
            dataWithSequence.SequenceNumber = currentSequence;
            _pendingAcknowledgmentQueue.Enqueue(dataWithSequence);
            Logger.LogDebug("{tag} Ack queued: hash={hash} sequence={sequence} queueSize={queueSize}", 
                SyncProgressTag,
                data.DataHash[..Math.Min(8, data.DataHash.Length)], currentSequence, _pendingAcknowledgmentQueue.Count);
        }
    }

    private void UpdateReceivedCharacterDataCache(OnlineUserCharaDataDto data)
    {
        var previousHash = LastReceivedCharacterData?.DataHash?.Value;
        var newHash = data.CharaData?.DataHash?.Value;
        var now = DateTimeOffset.UtcNow;

        if (!string.Equals(previousHash, newHash, StringComparison.Ordinal))
        {
            PreviousReceivedCharacterData = LastReceivedCharacterData;
            PreviousReceivedCharacterDataHash = previousHash;
            LastReceivedCharacterDataChangeTime = now;
        }

        LastReceivedCharacterData = data.CharaData;
        LastReceivedCharacterDataHash = newHash;
        LastReceivedCharacterDataTime = now;
        LastReceivedContainsCharacterLegacyShpk = ContainsGamePath(LastReceivedCharacterData, "characterlegacy.shpk");
        LastReceivedContainsCharacterShpk = ContainsGamePath(LastReceivedCharacterData, "character.shpk");
        LastReceivedCharacterLegacyShpkFiltered = LastReceivedContainsCharacterLegacyShpk && _configService.Current.FilterCharacterLegacyShpk;
        LastReceivedCharacterShpkFiltered = LastReceivedContainsCharacterShpk && _configService.Current.FilterCharacterShpk;
        Logger.LogDebug("{tag} Cache updated: user={user} newHash={newHash} previousHash={prevHash} changed={changed}",
            SyncProgressTag,
            data.User.AliasOrUID,
            string.IsNullOrEmpty(newHash) ? "NONE" : newHash[..Math.Min(8, newHash.Length)],
            string.IsNullOrEmpty(previousHash) ? "NONE" : previousHash[..Math.Min(8, previousHash.Length)],
            !string.Equals(previousHash, newHash, StringComparison.Ordinal));

        if (data.CharaData != null && !string.IsNullOrWhiteSpace(newHash))
        {
            var playerName = CachedPlayer?.PlayerName;
            _ = _characterDataSqliteStore.StoreReceivedPairDataAsync(data.User.UID, data.CharaData, playerName, null, null, null, null, data.SessionId, data.SequenceNumber, now);
        }
    }

    private async Task LoadPersistedLastReceivedDataAsync()
    {
        if (string.IsNullOrWhiteSpace(UserData.UID)) return;

        var persisted = await _characterDataSqliteStore.GetLatestPairDataAsync(UserData.UID).ConfigureAwait(false);
        if (persisted == null) return;

        LastReceivedCharacterData = persisted.CharacterData;
        LastReceivedCharacterDataHash = persisted.DataHash;
        LastReceivedCharacterDataTime = persisted.ReceivedAt;
        LastReceivedCharacterDataChangeTime = persisted.ReceivedAt;
        LastReceivedContainsCharacterLegacyShpk = ContainsGamePath(LastReceivedCharacterData, "characterlegacy.shpk");
        LastReceivedContainsCharacterShpk = ContainsGamePath(LastReceivedCharacterData, "character.shpk");
        LastReceivedCharacterLegacyShpkFiltered = LastReceivedContainsCharacterLegacyShpk && _configService.Current.FilterCharacterLegacyShpk;
        LastReceivedCharacterShpkFiltered = LastReceivedContainsCharacterShpk && _configService.Current.FilterCharacterShpk;
        Logger.LogDebug("{tag} Loaded persisted character data: user={user} hash={hash}", SyncProgressTag, UserData.AliasOrUID, persisted.DataHash[..Math.Min(8, persisted.DataHash.Length)]);
    }

    public void ApplyLastReceivedData(bool forced = false)
    {
        if (CachedPlayer == null) return;
        if (LastReceivedCharacterData == null) return;

        if (!forced && CachedPlayer.IsCharacterDataAppliedForCurrentCharacter(LastReceivedCharacterData))
        {
            var hash = LastReceivedCharacterData.DataHash.Value;
            var shortHash = string.IsNullOrEmpty(hash) ? "NONE" : hash[..Math.Min(8, hash.Length)];
            AddApplyDebug($"Apply skipped hash already applied hash={shortHash}");
            Logger.LogDebug("{tag} Apply skipped: already applied hash={hash} user={user}", SyncProgressTag, shortHash, UserData.AliasOrUID);
            return;
        }

        var applyHash = LastReceivedCharacterData.DataHash.Value;
        var applyShortHash = string.IsNullOrEmpty(applyHash) ? "NONE" : applyHash[..Math.Min(8, applyHash.Length)];
        if (!forced)
        {
            if (CachedPlayer.IsApplyPipelineRunningForHash(applyHash))
            {
                AddApplyDebug($"Apply skipped pipeline in progress hash={applyShortHash}");
                Logger.LogDebug("{tag} Apply skipped: pipeline in progress hash={hash} user={user}",
                    SyncProgressTag, applyShortHash, UserData.AliasOrUID);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (string.Equals(_lastApplyAttemptHash, applyHash, StringComparison.Ordinal)
                && (now - _lastApplyAttemptTime).TotalMilliseconds < ApplyAttemptCooldownMs)
            {
                AddApplyDebug($"Apply skipped cooldown hash={applyShortHash}");
                Logger.LogDebug("{tag} Apply skipped: cooldown hash={hash} user={user}",
                    SyncProgressTag, applyShortHash, UserData.AliasOrUID);
                return;
            }
        }

        _lastApplyAttemptHash = applyHash;
        _lastApplyAttemptTime = DateTimeOffset.UtcNow;
        AddApplyDebug($"Apply start forced={forced} hash={applyShortHash}");
        Logger.LogDebug("{tag} Apply start: user={user} forced={forced} hash={hash}", SyncProgressTag, UserData.AliasOrUID, forced, applyShortHash);
        CachedPlayer.ApplyCharacterData(Guid.NewGuid(), RemoveNotSyncedFiles(LastReceivedCharacterData.DeepClone())!, forced, forceRedraw: forced);
    }

    public void CreateCachedPlayer(OnlineUserIdentDto? dto = null)
    {
        try
        {
            _creationSemaphore.Wait();

            if (CachedPlayer != null) return;

            if (dto == null && _onlineUserIdentDto == null)
            {
                CachedPlayer?.Dispose();
                CachedPlayer = null;
                return;
            }
            if (dto != null)
            {
                _onlineUserIdentDto = dto;
            }

            CachedPlayer?.Dispose();
            CachedPlayer = _cachedPlayerFactory.Create(this);
            if (_configService.Current.PreloadPairCollectionFromLastReceivedData && LastReceivedCharacterData != null && IsTemporaryCollectionPreloadAllowed())
            {
                _ = CachedPlayer.PreloadTemporaryCollectionFromLastReceivedDataAsync(LastReceivedCharacterData.DeepClone());
            }
        }
        finally
        {
            _creationSemaphore.Release();
        }
    }

    public string? GetNote()
    {
        return _serverConfigurationManager.GetNoteForUid(UserData.UID);
    }

    private bool IsTemporaryCollectionPreloadAllowed()
    {
        var whitelist = _playerPerformanceConfigService.Current.TemporaryCollectionWhitelist;
        if (whitelist.Count == 0) return false;
        var userIdentifier = !string.IsNullOrEmpty(UserData.Alias) ? UserData.Alias : UserData.UID;
        return whitelist.Contains(userIdentifier, StringComparer.Ordinal) ||
               whitelist.Contains(UserData.UID, StringComparer.Ordinal);
    }

    public string GetPlayerNameHash()
    {
        return CachedPlayer?.PlayerNameHash ?? string.Empty;
    }

    public bool HasAnyConnection()
    {
        return UserPair.Groups.Any() || UserPair.IndividualPairStatus != IndividualPairStatus.None;
    }

    public void MarkOffline(bool wait = true)
    {
        try
        {
            if (wait)
                _creationSemaphore.Wait();
            ResetApplyRetry();
            LastReceivedCharacterData = null;
            PreviousReceivedCharacterData = null;
            LastReceivedCharacterDataHash = null;
            PreviousReceivedCharacterDataHash = null;
            LastReceivedCharacterDataTime = null;
            LastReceivedCharacterDataChangeTime = null;
            LastReceivedContainsCharacterLegacyShpk = false;
            LastReceivedContainsCharacterShpk = false;
            LastReceivedCharacterLegacyShpkFiltered = false;
            LastReceivedCharacterShpkFiltered = false;
            var player = CachedPlayer;
            CachedPlayer = null;
            player?.Dispose();
            _onlineUserIdentDto = null;
            IsInGpose = false;
        }
        finally
        {
            if (wait)
                _creationSemaphore.Release();
        }
    }

    public void SetNote(string note)
    {
        _serverConfigurationManager.SetNoteForUid(UserData.UID, note);
    }

    internal void SetIsUploading(bool isUploading = true)
    {
        CachedPlayer?.SetUploading(isUploading);
    }

    private CharacterData? RemoveNotSyncedFiles(CharacterData? data)
    {
        Logger.LogTrace("Removing not synced files");
        if (data == null)
        {
            Logger.LogTrace("Nothing to remove");
            return data;
        }

        bool disableIndividualAnimations = (UserPair.OtherPermissions.IsDisableAnimations() || UserPair.OwnPermissions.IsDisableAnimations());
        bool disableIndividualVFX = (UserPair.OtherPermissions.IsDisableVFX() || UserPair.OwnPermissions.IsDisableVFX());
        bool disableIndividualSounds = (UserPair.OtherPermissions.IsDisableSounds() || UserPair.OwnPermissions.IsDisableSounds());
        bool filterCharacterLegacy = _configService.Current.FilterCharacterLegacyShpk;
        bool filterCharacterShpk = _configService.Current.FilterCharacterShpk;

        if (_dalamudUtilService.IsInDuty
            && (UserPair.OtherPermissions.IsDisableVFXInDuty() || UserPair.OwnPermissions.IsDisableVFXInDuty()))
        {
            disableIndividualVFX = true;
        }

        Logger.LogTrace("Disable: Sounds: {disableIndividualSounds}, Anims: {disableIndividualAnims}; " +
            "VFX: {disableGroupSounds}, FilterLegacy: {filterCharacterLegacy}, FilterCharacterShpk: {filterCharacterShpk}",
            disableIndividualSounds, disableIndividualAnimations, disableIndividualVFX, filterCharacterLegacy, filterCharacterShpk);

        if (disableIndividualAnimations || disableIndividualSounds || disableIndividualVFX || filterCharacterLegacy || filterCharacterShpk)
        {
            Logger.LogTrace("Data cleaned up: Animations disabled: {disableAnimations}, Sounds disabled: {disableSounds}, VFX disabled: {disableVFX}, Filter Legacy: {filterCharacterLegacy}, Filter Character Shpk: {filterCharacterShpk}",
                disableIndividualAnimations, disableIndividualSounds, disableIndividualVFX, filterCharacterLegacy, filterCharacterShpk);
            foreach (var objectKind in data.FileReplacements.Select(k => k.Key))
            {
                if (disableIndividualSounds)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("scd", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                if (disableIndividualAnimations)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("tmb", StringComparison.OrdinalIgnoreCase) || p.EndsWith("pap", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                if (disableIndividualVFX)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("atex", StringComparison.OrdinalIgnoreCase) || p.EndsWith("avfx", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                if (filterCharacterLegacy)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                if (filterCharacterShpk)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("character.shpk", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
            }
        }

        return data;
    }

    private static bool ContainsGamePath(CharacterData? data, string fileName)
    {
        if (data?.FileReplacements == null) return false;
        foreach (var objectKind in data.FileReplacements.Keys)
        {
            foreach (var replacement in data.FileReplacements[objectKind])
            {
                if (!string.IsNullOrEmpty(replacement.FileSwapPath) &&
                    replacement.FileSwapPath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (replacement.GamePaths.Any(p => p.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public async Task UpdateAcknowledgmentStatus(string? acknowledgmentId, bool success, DateTimeOffset timestamp)
    {
        Logger.LogTrace("UpdateAcknowledgmentStatus called: {acknowledgmentId} - Success: {success} for user {user}",
            acknowledgmentId ?? "null", success, UserData.AliasOrUID);
        
        var currentAckYouBefore = UserPair.OwnPermissions.IsAckYou();
        Logger.LogTrace("UpdateAcknowledgmentStatus: AckYou BEFORE update: {ackYou} for user {user}",
            currentAckYouBefore, UserData.AliasOrUID);
        
        Logger.LogDebug("Updating acknowledgment status: {acknowledgmentId} - Success: {success} for user {user}",
            acknowledgmentId ?? "null", success, UserData.AliasOrUID);
        LastAcknowledgmentId = acknowledgmentId;
        LastAcknowledgmentSuccess = success;
        LastAcknowledgmentTime = timestamp;
        HasPendingAcknowledgment = false;

        // Update AckYou status based on current icon state
        // Green checkmark (success) = true, no icon (cleared) = false
        bool newAckYouStatus = success;

        var permissions = UserPair.OwnPermissions;
        var oldAckYou = permissions.IsAckYou();
        permissions.SetAckYou(newAckYouStatus);

        Logger.LogTrace("UpdateAcknowledgmentStatus: Setting AckYou from {oldAckYou} to {newAckYou} for user {user}",
            oldAckYou, newAckYouStatus, UserData.AliasOrUID);

        try
        {
            await _apiController.Value.UserSetPairPermissions(new(UserData, permissions)).ConfigureAwait(false);
            Logger.LogTrace("UpdateAcknowledgmentStatus: API call succeeded for AckYou update user={user}", UserData.AliasOrUID);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update AckYou status for user {user}", UserData.AliasOrUID);
        }

        // Publish specific pair acknowledgment status change event
        Mediator.Publish(new PairAcknowledgmentStatusChangedMessage(
            UserData,
            acknowledgmentId,
            HasPendingAcknowledgment,
            LastAcknowledgmentSuccess,
            LastAcknowledgmentTime
        ));

        // Publish optimized icon update for acknowledgment status
        var ackData = new AcknowledgmentStatusData(HasPendingAcknowledgment, LastAcknowledgmentSuccess, LastAcknowledgmentTime);
        Mediator.Publish(new UserPairIconUpdateMessage(UserData, IconUpdateType.AcknowledgmentStatus, ackData));

        // Publish granular UI refresh for this specific acknowledgment
        Mediator.Publish(new AcknowledgmentUiRefreshMessage(
            AcknowledgmentId: acknowledgmentId,
            User: UserData
        ));
        
        var finalAckYou = UserPair.OwnPermissions.IsAckYou();
        Logger.LogTrace("UpdateAcknowledgmentStatus: AckYou AFTER update: {finalAckYou} for user {user}",
            finalAckYou, UserData.AliasOrUID);
    }

    public async Task SetPendingAcknowledgment(string acknowledgmentId)
    {
        Logger.LogDebug("Setting pending acknowledgment: {acknowledgmentId} for user {user}", acknowledgmentId, UserData.AliasOrUID);
        LastAcknowledgmentId = acknowledgmentId;
        HasPendingAcknowledgment = true;
        LastAcknowledgmentSuccess = null;
        LastAcknowledgmentTime = null;
        
        // Update AckYou status based on current icon state
        // Yellow clock (pending) = false
        bool newAckYouStatus = false;
        
        // Update local permissions immediately for UI responsiveness
        var permissions = UserPair.OwnPermissions;
        permissions.SetAckYou(newAckYouStatus);
        UserPair.OwnPermissions = permissions;
        
        try
        {
            await _apiController.Value.UserSetPairPermissions(new(UserData, permissions)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update AckYou status for user {user}", UserData.AliasOrUID);
            // Revert local change if server update failed
            var revertPermissions = UserPair.OwnPermissions;
            revertPermissions.SetAckYou(!newAckYouStatus);
            UserPair.OwnPermissions = revertPermissions;
        }
        
        // Publish specific pair acknowledgment status change event
        Mediator.Publish(new PairAcknowledgmentStatusChangedMessage(
            UserData,
            acknowledgmentId,
            HasPendingAcknowledgment,
            LastAcknowledgmentSuccess,
            LastAcknowledgmentTime
        ));
        
        // Publish optimized icon update for acknowledgment status
        var ackData = new AcknowledgmentStatusData(HasPendingAcknowledgment, LastAcknowledgmentSuccess, LastAcknowledgmentTime);
        Mediator.Publish(new UserPairIconUpdateMessage(UserData, IconUpdateType.AcknowledgmentStatus, ackData));
        
        // Publish acknowledgment pending event
        Mediator.Publish(new AcknowledgmentPendingMessage(
            acknowledgmentId,
            UserData,
            DateTime.UtcNow
        ));
        
        // Publish granular UI refresh for this specific acknowledgment
        Mediator.Publish(new AcknowledgmentUiRefreshMessage(
            AcknowledgmentId: acknowledgmentId,
            User: UserData
        ));
        
        // Keep legacy acknowledgment status change event for backward compatibility
        Mediator.Publish(new AcknowledgmentStatusChangedMessage(
            acknowledgmentId,
            UserData,
            AcknowledgmentStatus.Pending,
            DateTime.UtcNow
        ));
    }

    public void ResolvePendingAcknowledgmentFromRemoteAckYou()
    {
        if (!HasPendingAcknowledgment)
        {
            return;
        }

        var acknowledgmentId = LastAcknowledgmentId;
        Logger.LogDebug("Resolving pending acknowledgment from remote AckYou for user {user} (AckId: {ackId})", UserData.AliasOrUID, acknowledgmentId);

        HasPendingAcknowledgment = false;
        LastAcknowledgmentSuccess = true;
        LastAcknowledgmentTime = DateTimeOffset.UtcNow;

        Mediator.Publish(new PairAcknowledgmentStatusChangedMessage(
            UserData,
            acknowledgmentId,
            HasPendingAcknowledgment,
            LastAcknowledgmentSuccess,
            LastAcknowledgmentTime
        ));

        var ackData = new AcknowledgmentStatusData(HasPendingAcknowledgment, LastAcknowledgmentSuccess, LastAcknowledgmentTime);
        Mediator.Publish(new UserPairIconUpdateMessage(UserData, IconUpdateType.AcknowledgmentStatus, ackData));

        Mediator.Publish(new AcknowledgmentUiRefreshMessage(
            AcknowledgmentId: acknowledgmentId,
            User: UserData
        ));

        if (!string.IsNullOrEmpty(acknowledgmentId))
        {
            Mediator.Publish(new AcknowledgmentStatusChangedMessage(
                acknowledgmentId,
                UserData,
                AcknowledgmentStatus.Received,
                DateTime.UtcNow
            ));
        }
    }

    public void SetBuildStartPendingStatus()
    {
        Logger.LogInformation("Setting build start pending status for user {user}", UserData.AliasOrUID);
        HasPendingAcknowledgment = true;
        LastAcknowledgmentSuccess = null;
        LastAcknowledgmentTime = null;
        LastAcknowledgmentId = null; // No specific acknowledgment ID for build start
    }

    public async Task ClearPendingAcknowledgment(string acknowledgmentId, MessageService? messageService = null)
    {
        // Only clear if this is the acknowledgment we're waiting for
        if (string.Equals(LastAcknowledgmentId, acknowledgmentId, StringComparison.Ordinal))
        {
            Logger.LogDebug("Clearing pending acknowledgment: {acknowledgmentId} for user {user}", acknowledgmentId, UserData.AliasOrUID);
            HasPendingAcknowledgment = false;
            LastAcknowledgmentId = null;
            
            // Update AckYou status based on current icon state
            // No icon (cleared) = false
            bool newAckYouStatus = false;
            
            // Update local permissions immediately for UI responsiveness
            var permissions = UserPair.OwnPermissions;
            permissions.SetAckYou(newAckYouStatus);
            UserPair.OwnPermissions = permissions;
            
            try
            {
                await _apiController.Value.UserSetPairPermissions(new(UserData, permissions)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to update AckYou status for user {user}", UserData.AliasOrUID);
                // Revert local change if server update failed
                var revertPermissions = UserPair.OwnPermissions;
                revertPermissions.SetAckYou(!newAckYouStatus);
                UserPair.OwnPermissions = revertPermissions;
            }
            
            // Add notification if MessageService is available
            messageService?.AddTaggedMessage(
                $"pair_clear_{acknowledgmentId}_{UserData.UID}",
                $"Cleared pending acknowledgment for {UserData.AliasOrUID}",
                NotificationType.Info,
                "Acknowledgment Cleared",
                TimeSpan.FromSeconds(2)
            );
            
            // Publish specific pair acknowledgment status change event
            Mediator.Publish(new PairAcknowledgmentStatusChangedMessage(
                UserData,
                acknowledgmentId,
                HasPendingAcknowledgment,
                LastAcknowledgmentSuccess,
                LastAcknowledgmentTime
            ));
            
            // Publish granular UI refresh for this specific acknowledgment
            Mediator.Publish(new AcknowledgmentUiRefreshMessage(
                AcknowledgmentId: acknowledgmentId,
                User: UserData
            ));
            
            // Publish acknowledgment status change event
            Mediator.Publish(new AcknowledgmentStatusChangedMessage(
                acknowledgmentId,
                UserData,
                AcknowledgmentStatus.Received,
                DateTime.UtcNow
            ));
        }
        else
        {
            Logger.LogDebug("Not clearing pending acknowledgment - ID mismatch. Expected: {expected}, Got: {got} for user {user}", 
                LastAcknowledgmentId, acknowledgmentId, UserData.AliasOrUID);
        }
    }

    public void ClearPendingAcknowledgmentForce(MessageService? messageService = null)
    {
        var previousAckId = LastAcknowledgmentId;
        Logger.LogDebug("Force clearing pending acknowledgment for user {user}", UserData.AliasOrUID);
        HasPendingAcknowledgment = false;
        LastAcknowledgmentId = null;
        
        // Add notification if MessageService is available
        messageService?.AddTaggedMessage(
            $"pair_force_clear_{previousAckId}_{UserData.UID}",
            $"Force cleared pending acknowledgment for {UserData.AliasOrUID}",
            NotificationType.Warning,
            "Acknowledgment Force Cleared",
            TimeSpan.FromSeconds(3)
        );
        
        // Publish specific pair acknowledgment status change event
        Mediator.Publish(new PairAcknowledgmentStatusChangedMessage(
            UserData,
            previousAckId,
            HasPendingAcknowledgment,
            LastAcknowledgmentSuccess,
            LastAcknowledgmentTime
        ));
        
        // Publish granular UI refresh for this user
        if (previousAckId != null)
        {
            Mediator.Publish(new AcknowledgmentUiRefreshMessage(
                AcknowledgmentId: previousAckId,
                User: UserData
            ));
        }
        else
        {
            Mediator.Publish(new AcknowledgmentUiRefreshMessage(
                User: UserData
            ));
        }
        
        // Publish acknowledgment status change event if we had a pending acknowledgment
        if (previousAckId != null)
        {
            Mediator.Publish(new AcknowledgmentStatusChangedMessage(
                previousAckId,
                UserData,
                AcknowledgmentStatus.Cancelled,
                DateTime.UtcNow
            ));
        }

    }



    private async Task SendAcknowledgmentIfRequired(OnlineUserCharaDataDto data, bool success, bool hashVerificationPassed = true)
    {
        Logger.LogTrace("SendAcknowledgmentIfRequired called - RequiresAcknowledgment: {requires}, Hash: {hash}, Success: {success}, HashVerification: {hashVerification}", 
            data.RequiresAcknowledgment, data.DataHash[..Math.Min(8, data.DataHash.Length)], success, hashVerificationPassed);

        if (!data.RequiresAcknowledgment || string.IsNullOrEmpty(data.DataHash))
        {
            Logger.LogTrace("Skipping acknowledgment - RequiresAcknowledgment: {requires}, DataHash null/empty: {empty}",
                data.RequiresAcknowledgment, string.IsNullOrEmpty(data.DataHash));
            return;
        }

        try
        {
            var finalSuccess = success && hashVerificationPassed;
            var errorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.None;
            string? errorMessage = null;

            if (!success)
            {
                errorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.DataCorrupted;
                errorMessage = "Failed to apply character data";
            }
            else if (!hashVerificationPassed)
            {
                errorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.HashVerificationFailed;
                errorMessage = "Data hash verification failed - data integrity compromised";
            }

            var acknowledgmentDto = new CharacterDataAcknowledgmentDto(UserData, data.DataHash)
            {
                Success = finalSuccess,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                AcknowledgedAt = DateTime.UtcNow,
                SessionId = data.SessionId // Include session ID for batch acknowledgment tracking
            };

            Logger.LogTrace("Sending acknowledgment to server - Hash: {hash}, User: {user}, Success: {success}, ErrorCode: {errorCode}",
                data.DataHash[..Math.Min(8, data.DataHash.Length)], UserData.AliasOrUID, finalSuccess, errorCode);

            // Send acknowledgment through the mediator
             Mediator.Publish(new SendCharacterDataAcknowledgmentMessage(acknowledgmentDto));
            Logger.LogTrace("Successfully published SendCharacterDataAcknowledgmentMessage for Hash: {hash}",
                data.DataHash[..Math.Min(8, data.DataHash.Length)]);

            var permissions = UserPair.OwnPermissions;
            var currentAckYou = permissions.IsAckYou();
            var newAckYouStatus = finalSuccess;
            
            Logger.LogTrace("SendAcknowledgmentIfRequired: Current AckYou={current}, newAckYouStatus={newStatus} for user {user}",
                currentAckYou, newAckYouStatus, UserData.AliasOrUID);
            
            if (currentAckYou != newAckYouStatus)
            {
                Logger.LogTrace("SendAcknowledgmentIfRequired: Setting Own AckYou={status} for user {user}", newAckYouStatus, UserData.AliasOrUID);
                permissions.SetAckYou(newAckYouStatus);
                try
                {
                    await _apiController.Value.UserSetPairPermissions(new(UserData, permissions)).ConfigureAwait(false);
                    Logger.LogTrace("SendAcknowledgmentIfRequired: API call succeeded for AckYou update user={user}", UserData.AliasOrUID);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "SendAcknowledgmentIfRequired: Failed to send Own AckYou update for user {user}", UserData.AliasOrUID);
                }
            }
            else
            {
                Logger.LogTrace("SendAcknowledgmentIfRequired: AckYou already matches desired status - no API call needed user={user}", UserData.AliasOrUID);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send character data acknowledgment for Hash: {hash}",
                data.DataHash[..Math.Min(8, data.DataHash.Length)]);
        }
    }
    
    
    // Handles character data application completion and sends delayed acknowledgment if needed
    private async Task OnCharacterDataApplicationCompleted(CharacterDataApplicationCompletedMessage message)
    {
        // Check if this message is for this pair
        if (string.Equals(message.UserUID, UserPair.User.UID, StringComparison.Ordinal))
        {
            Logger.LogTrace("{tag} Apply completed: user={user} success={success} hash={hash} appId={appId}",
                SyncProgressTag, UserData.AliasOrUID, message.Success,
                string.IsNullOrEmpty(message.DataHash) ? "NONE" : message.DataHash[..Math.Min(8, message.DataHash.Length)],
                message.ApplicationId);
            
            // Debug trace for AckYou status before processing
            var currentAckYouStatus = UserPair.OwnPermissions.IsAckYou();
            Logger.LogTrace("{tag} AckYou status BEFORE processing: {ackYou} for user={user}",
                SyncProgressTag, currentAckYouStatus, UserData.AliasOrUID);
            
            if (message.Success)
            {
                ResetApplyRetry();
                Logger.LogTrace("{tag} Apply retry reset after success for user={user}",
                    SyncProgressTag, UserData.AliasOrUID);
            }
            else
            {
                ScheduleApplyRetry(increment: true);
                Logger.LogTrace("{tag} Apply retry scheduled after failure for user={user}",
                    SyncProgressTag, UserData.AliasOrUID);
            }

            // If data was successfully applied, set acknowledgment to true immediately
            // This ensures the server knows we have applied the data
            if (message.Success && !string.IsNullOrEmpty(message.DataHash))
            {
                try
                {
                    // Check if we have a pending acknowledgment for this hash
                    OnlineUserCharaDataDto? matchingAcknowledgment = null;
                    var remaining = new List<OnlineUserCharaDataDto>();
                    var queueCountBefore = _pendingAcknowledgmentQueue.Count;

                    Logger.LogTrace("{tag} Processing acknowledgment queue: count={count} hash={hash}",
                        SyncProgressTag, queueCountBefore, message.DataHash[..Math.Min(8, message.DataHash.Length)]);

                    while (_pendingAcknowledgmentQueue.TryDequeue(out var acknowledgmentData))
                    {
                        if (string.Equals(acknowledgmentData.DataHash, message.DataHash, StringComparison.Ordinal))
                        {
                            if (matchingAcknowledgment == null || acknowledgmentData.SequenceNumber > matchingAcknowledgment.SequenceNumber)
                            {
                                matchingAcknowledgment = acknowledgmentData;
                                Logger.LogTrace("{tag} Found matching acknowledgment: seq={seq} hash={hash}",
                                    SyncProgressTag, acknowledgmentData.SequenceNumber, message.DataHash[..Math.Min(8, message.DataHash.Length)]);
                            }
                            else
                            {
                                remaining.Add(acknowledgmentData);
                                Logger.LogTrace("{tag} Duplicate acknowledgment (lower seq): seq={seq}",
                                    SyncProgressTag, acknowledgmentData.SequenceNumber);
                            }
                        }
                        else
                        {
                            remaining.Add(acknowledgmentData);
                            Logger.LogTrace("{tag} Non-matching acknowledgment: hash={otherHash}",
                                SyncProgressTag, acknowledgmentData.DataHash[..Math.Min(8, acknowledgmentData.DataHash.Length)]);
                        }
                    }

                    // Re-enqueue remaining acknowledgments
                    foreach (var pending in remaining)
                    {
                        _pendingAcknowledgmentQueue.Enqueue(pending);
                    }

                    Logger.LogTrace("{tag} Acknowledgment queue after processing: remaining={count}",
                        SyncProgressTag, _pendingAcknowledgmentQueue.Count);

                    if (matchingAcknowledgment != null)
                    {
                        // Send acknowledgment to server for matching hash
                        var verificationSuccess = true;
                        var isInDutyOrCombat = _dalamudUtilService.IsInDuty || _dalamudUtilService.IsInCombatOrPerforming;
                        var shouldSkipVerification = _configService.Current.DisableSyncPauseDuringDutyOrCombat && isInDutyOrCombat;

                        Logger.LogTrace("{tag} Processing matching acknowledgment: isInDutyOrCombat={inDuty} skipVerification={skip}",
                            SyncProgressTag, isInDutyOrCombat, shouldSkipVerification);

                        if (!shouldSkipVerification)
                        {
                            verificationSuccess = VerifyDataHashIntegrity(matchingAcknowledgment, message.DataHash);
                            Logger.LogTrace("{tag} Hash verification result: {verificationSuccess}",
                                SyncProgressTag, verificationSuccess);
                        }

                        Logger.LogTrace("{tag} Ack sending: appSuccess={appSuccess} hashVerified={hashSuccess} hash={hash}",
                            SyncProgressTag,
                            message.Success, verificationSuccess, message.DataHash[..Math.Min(8, message.DataHash.Length)]);

                        await SendAcknowledgmentIfRequired(matchingAcknowledgment, message.Success, verificationSuccess).ConfigureAwait(false);
                        
                        Logger.LogTrace("{tag} SendAcknowledgmentIfRequired completed for hash={hash}",
                            SyncProgressTag, message.DataHash[..Math.Min(8, message.DataHash.Length)]);
                    }
                    else
                    {
                        // No matching acknowledgment in queue, but data was applied successfully
                        // Update local acknowledgment status to indicate successful application
                        Logger.LogTrace("{tag} No matching acknowledgment in queue - setting AckYou directly hash={hash}",
                            SyncProgressTag, message.DataHash[..Math.Min(8, message.DataHash.Length)]);
                        
                        // Set AckYou permission to true to indicate successful application
                        var permissions = UserPair.OwnPermissions;
                        var wasAckYou = permissions.IsAckYou();
                        Logger.LogTrace("{tag} Current AckYou status: {wasAckYou} - attempting to set to true",
                            SyncProgressTag, wasAckYou);
                        
                        if (!wasAckYou)
                        {
                            Logger.LogTrace("{tag} Setting AckYou to true for user={user}",
                                SyncProgressTag, UserData.AliasOrUID);
                            permissions.SetAckYou(true);
                            try
                            {
                                await _apiController.Value.UserSetPairPermissions(new(UserData, permissions)).ConfigureAwait(false);
                                Logger.LogTrace("{tag} AckYou set to true after successful apply hash={hash} - API call succeeded",
                                    SyncProgressTag, message.DataHash[..Math.Min(8, message.DataHash.Length)]);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError(ex, "{tag} Failed to set AckYou after successful apply hash={hash}",
                                    SyncProgressTag, message.DataHash[..Math.Min(8, message.DataHash.Length)]);
                            }
                        }
                        else
                        {
                            Logger.LogTrace("{tag} AckYou already true - no action needed hash={hash}",
                                SyncProgressTag, message.DataHash[..Math.Min(8, message.DataHash.Length)]);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "{tag} Failed to process acknowledgment after successful apply hash={hash}",
                        SyncProgressTag, message.DataHash ?? "NONE");
                }
            }
            else if (!message.Success)
            {
                // Data application failed, check if we need to update acknowledgment status
                Logger.LogTrace("{tag} Data application failed - checking acknowledgment status success={success} hasPending={hasPending}",
                    SyncProgressTag, message.Success, HasPendingAcknowledgment);

                if (HasPendingAcknowledgment || (message.Success && !UserPair.OwnPermissions.IsAckYou()))
                {
                    Logger.LogTrace("{tag} Updating acknowledgment status: lastAckId={ackId}",
                        SyncProgressTag, LastAcknowledgmentId ?? "null");
                    await UpdateAcknowledgmentStatus(LastAcknowledgmentId, message.Success, DateTime.UtcNow).ConfigureAwait(false);
                }
            }
            
            // Final debug trace for AckYou status after processing
            var finalAckYouStatus = UserPair.OwnPermissions.IsAckYou();
            Logger.LogTrace("{tag} AckYou status AFTER processing: {finalAckYou} for user={user}",
                SyncProgressTag, finalAckYouStatus, UserData.AliasOrUID);
        }
    }

    private void ScheduleApplyRetry(bool increment)
    {
        if (LastReceivedCharacterData == null || CachedPlayer == null) return;
        if (!IsVisible || !IsMutuallyVisible) return;

        if (increment && _applyRetryCount < MaxApplyRetryBackoffSteps)
        {
            _applyRetryCount++;
        }

        if (_applyRetryCount <= 0)
        {
            _applyRetryCount = 1;
        }

        _applyRetryCts.Cancel();
        _applyRetryCts.Dispose();
        _applyRetryCts = new CancellationTokenSource();
        var token = _applyRetryCts.Token;
        var delaySeconds = Math.Min(BaseApplyRetryDelaySeconds * (1 << (_applyRetryCount - 1)), MaxApplyRetryDelaySeconds);
        AddApplyDebug($"Retry scheduled in {delaySeconds}s attempt={_applyRetryCount}");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (token.IsCancellationRequested) return;
            if (LastReceivedCharacterData == null || CachedPlayer == null) return;
            if (!IsVisible || !IsMutuallyVisible) return;

            var isInCombatOrCutscene = _dalamudUtilService.IsInCombatOrPerforming || _dalamudUtilService.IsInGpose || _dalamudUtilService.IsInCutscene;
            var shouldSkipPauseCheck = _configService.Current.DisableSyncPauseDuringDutyOrCombat;
            
            if (isInCombatOrCutscene && !shouldSkipPauseCheck)
            {
                ScheduleApplyRetry(increment: false);
                return;
            }

            CachedPlayer.ApplyCharacterData(Guid.NewGuid(),
                RemoveNotSyncedFiles(LastReceivedCharacterData.DeepClone())!,
                forceApplyCustomization: true);
        }, token);
    }

    private void ResetApplyRetry()
    {
        _applyRetryCount = 0;
        _applyRetryCts.Cancel();
        _applyRetryCts.Dispose();
        _applyRetryCts = new CancellationTokenSource();
    }

    internal string[] GetApplyDebugLines()
    {
        return _applyDebugLog.ToArray();
    }

    internal void ClearApplyDebug()
    {
        _applyDebugLog.Clear();
    }

    internal void AddApplyDebug(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _applyDebugLog.Enqueue($"[{timestamp}] {message}");
        while (_applyDebugLog.Count > MaxApplyDebugLines)
        {
            if (!_applyDebugLog.TryDequeue(out _))
            {
                break;
            }
        }
    }

    private bool VerifyDataHashIntegrity(OnlineUserCharaDataDto acknowledgmentData, string? appliedHash)
     {
         try
         {
             if (acknowledgmentData?.CharaData == null)
             {
                Logger.LogWarning("{tag} Hash verify failed: data missing", SyncProgressTag);
                 return false;
             }
 
            var receivedHash = acknowledgmentData.CharaData.DataHash.Value;
            var expectedHash = appliedHash;
            if (string.IsNullOrEmpty(expectedHash) && LastReceivedCharacterData != null)
            {
                expectedHash = LastReceivedCharacterData.DataHash.Value;
            }

            if (string.IsNullOrEmpty(expectedHash))
            {
                Logger.LogWarning("{tag} Hash verify failed: applied hash missing", SyncProgressTag);
                return false;
            }
             
            var hashMatch = string.Equals(receivedHash, expectedHash, StringComparison.Ordinal);
             
            Logger.LogDebug("{tag} Hash verify: received={receivedHash} applied={appliedHash} match={hashMatch}", 
                SyncProgressTag, receivedHash, expectedHash, hashMatch);
             
             return hashMatch;
         }
         catch (Exception ex)
         {
            Logger.LogError(ex, "{tag} Hash verify failed with exception", SyncProgressTag);
             return false;
         }
     }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _applicationCts?.Cancel();
            _applicationCts?.Dispose();
            _applyRetryCts.Cancel();
            _applyRetryCts.Dispose();
            _creationSemaphore.Dispose();
            CachedPlayer?.Dispose();
            CachedPlayer = null;
        }
        base.Dispose(disposing);
    }
}
