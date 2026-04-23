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
    private readonly object _ackStateLock = new();
    private long _acknowledgmentVersion = 0;
    private CancellationTokenSource _applicationCts = new();
    private OnlineUserIdentDto? _onlineUserIdentDto = null;
    private static readonly Version LegacyAckMaxClientVersion = new(1, 1, 13, 1);
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
    private int _forceRedrawOnNextData = 0;
    private DateTimeOffset _forceRedrawRequestAt = DateTimeOffset.MinValue;
    private int _forceRedrawRequestToken = 0;

    public Pair(ILogger<Pair> logger, UserFullPairDto userPair, PairHandlerFactory cachedPlayerFactory,
        SpheneMediator mediator, ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService, Lazy<ApiController> apiController,
        VisibilityGateService visibilityGateService, DalamudUtilService dalamudUtilService) : base(logger, mediator)
    {
        UserPair = userPair;
        _cachedPlayerFactory = cachedPlayerFactory;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _apiController = apiController;
        _visibilityGateService = visibilityGateService;
        _dalamudUtilService = dalamudUtilService;
        
        // Subscribe to character data application completion messages
        Mediator.Subscribe<CharacterDataApplicationCompletedMessage>(this, message => { _ = OnCharacterDataApplicationCompleted(message); });
        Mediator.Subscribe<GposeStartMessage>(this, _ => { WasMutuallyVisibleInGpose = IsMutuallyVisible; });
        Mediator.Subscribe<GposeEndMessage>(this, _ => { WasMutuallyVisibleInGpose = false; });
    }

    public bool HasCachedPlayer => CachedPlayer != null && !string.IsNullOrEmpty(CachedPlayer.PlayerName) && _onlineUserIdentDto != null;
    public IndividualPairStatus IndividualPairStatus => UserPair.IndividualPairStatus;
    public bool IsDirectlyPaired => IndividualPairStatus != IndividualPairStatus.None;
    public bool IsOneSidedPair => IndividualPairStatus == IndividualPairStatus.OneSided;
    public bool IsOnline => CachedPlayer != null;
    public bool IsInDuty => _dalamudUtilService.IsInDuty;
    public bool IsEffectivelyOffline => !IsOnline;

    public bool IsPaired => IndividualPairStatus == IndividualPairStatus.Bidirectional || UserPair.Groups.Any();
    public bool IsPaused => UserPair.OwnPermissions.IsPaused();
    public bool IsVisible => CachedPlayer?.IsVisible ?? false;
    public bool IsMutuallyVisible { get; private set; } = false;
    public bool WasMutuallyVisibleInGpose { get; private set; } = false;
    public bool IsInGpose { get; private set; } = false;
    private bool _isInOfflineGrace = false;
    public CharacterData? LastReceivedCharacterData { get; private set; }
    public CharacterData? PreviousReceivedCharacterData { get; private set; }
    public string? LastReceivedCharacterDataHash { get; private set; }
    public string? PreviousReceivedCharacterDataHash { get; private set; }
    public DateTimeOffset? LastReceivedCharacterDataTime { get; private set; }
    public DateTimeOffset? LastReceivedCharacterDataChangeTime { get; private set; }
    public string? PlayerName => CachedPlayer?.PlayerName ?? string.Empty;
    public long LastAppliedDataBytes => CachedPlayer?.LastAppliedDataBytes ?? -1;
    public long LastAppliedDataTris { get; set; } = -1;
    public long LastAppliedApproximateVRAMBytes { get; set; } = -1;
    public string Ident => _onlineUserIdentDto?.Ident ?? string.Empty;
    public string? RemoteClientVersion => UserPair.RemoteClientVersion ?? _onlineUserIdentDto?.ClientVersion;
    public bool IsLegacyAcknowledgmentClient => TryGetNormalizedRemoteClientVersion(out var v) && v <= LegacyAckMaxClientVersion;
    internal int ApplyRetryCount => _applyRetryCount;

    private bool TryGetNormalizedRemoteClientVersion(out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var raw = RemoteClientVersion;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!Version.TryParse(raw, out var parsed))
        {
            return false;
        }

        var major = parsed.Major;
        var minor = parsed.Minor;
        var build = parsed.Build < 0 ? 0 : parsed.Build;
        var revision = parsed.Revision < 0 ? 0 : parsed.Revision;
        version = new Version(major, minor, build, revision);
        return true;
    }
    
    // Data synchronization status properties
    public bool? LastAcknowledgmentSuccess { get; private set; } = null;
    public DateTimeOffset? LastAcknowledgmentTime { get; private set; } = null;
    public string? LastAcknowledgmentId { get; private set; } = null;
    public bool HasPendingAcknowledgment { get; private set; } = false;
    public Sphene.API.Dto.User.AcknowledgmentErrorCode LastAcknowledgmentErrorCode { get; private set; } = Sphene.API.Dto.User.AcknowledgmentErrorCode.None;
    public string? LastAcknowledgmentErrorMessage { get; private set; } = null;

    public string? LastAcknowledgedIncomingDataHash { get; private set; } = null;
    public DateTimeOffset? LastAcknowledgedIncomingTime { get; private set; } = null;
    public string? LastIncomingAcknowledgmentHash { get; private set; } = null;
    public bool? LastIncomingAcknowledgmentSuccess { get; private set; } = null;
    public Sphene.API.Dto.User.AcknowledgmentErrorCode LastIncomingAcknowledgmentErrorCode { get; private set; } = Sphene.API.Dto.User.AcknowledgmentErrorCode.None;
    public string? LastIncomingAcknowledgmentErrorMessage { get; private set; } = null;
    public DateTimeOffset? LastIncomingAcknowledgmentTime { get; private set; } = null;
    public string? LastOutgoingAcknowledgmentHash { get; private set; } = null;
    public string? LastOutgoingAcknowledgmentSessionId { get; private set; } = null;

    public enum AckV3Outcome
    {
        Unknown = 0,
        Pending = 1,
        Success = 2,
        Fail = 3
    }

    public readonly record struct AckV3State(AckV3Outcome Outcome, string? Hash, DateTimeOffset? Time,
        Sphene.API.Dto.User.AcknowledgmentErrorCode ErrorCode, string? ErrorMessage);

    public AckV3State GetOutgoingAckV3State()
    {
        if (!IsMutuallyVisible)
        {
            return new(AckV3Outcome.Unknown, null, null, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
        }

        bool hasPending;
        string? ackId;
        DateTimeOffset? ackTime;
        bool? ackSuccess;
        Sphene.API.Dto.User.AcknowledgmentErrorCode errorCode;
        string? errorMessage;

        lock (_ackStateLock)
        {
            hasPending = HasPendingAcknowledgment;
            ackId = LastAcknowledgmentId;
            ackTime = LastAcknowledgmentTime;
            ackSuccess = LastAcknowledgmentSuccess;
            errorCode = LastAcknowledgmentErrorCode;
            errorMessage = LastAcknowledgmentErrorMessage;
        }

        if (hasPending)
        {
            return new(AckV3Outcome.Pending, ackId, ackTime, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
        }

        if (ackSuccess == true)
        {
            return new(AckV3Outcome.Success, ackId, ackTime, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
        }

        if (ackSuccess == false)
        {
            return new(AckV3Outcome.Fail, ackId, ackTime, errorCode, errorMessage);
        }

        return new(AckV3Outcome.Unknown, ackId, ackTime, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
    }

    public AckV3State GetIncomingAckV3State()
    {
        if (!IsMutuallyVisible)
        {
            return new(AckV3Outcome.Unknown, null, null, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
        }

        var currentHash = LastReceivedCharacterDataHash;
        if (string.IsNullOrWhiteSpace(currentHash))
        {
            return new(AckV3Outcome.Unknown, null, null, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
        }

        var ctx = _lastIncomingAckContext;
        if (ctx != null
            && string.Equals(ctx.DataHash, currentHash, StringComparison.Ordinal)
            && !ctx.RequiresAcknowledgment)
        {
            return new(AckV3Outcome.Success, currentHash, LastReceivedCharacterDataTime, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
        }

        string? incomingHash;
        bool? incomingSuccess;
        DateTimeOffset? incomingTime;
        Sphene.API.Dto.User.AcknowledgmentErrorCode incomingErrorCode;
        string? incomingErrorMessage;

        lock (_ackStateLock)
        {
            incomingHash = LastIncomingAcknowledgmentHash;
            incomingSuccess = LastIncomingAcknowledgmentSuccess;
            incomingTime = LastIncomingAcknowledgmentTime;
            incomingErrorCode = LastIncomingAcknowledgmentErrorCode;
            incomingErrorMessage = LastIncomingAcknowledgmentErrorMessage;
        }

        if (string.Equals(incomingHash, currentHash, StringComparison.Ordinal))
        {
            if (incomingSuccess == true)
            {
                return new(AckV3Outcome.Success, currentHash, incomingTime, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
            }

            if (incomingSuccess == false)
            {
                return new(AckV3Outcome.Fail, currentHash, incomingTime, incomingErrorCode, incomingErrorMessage);
            }
        }

        return new(AckV3Outcome.Pending, currentHash, LastReceivedCharacterDataTime, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
    }
    
    // Get both incoming and outgoing acknowledgment states atomically to prevent race conditions in UI
    public (AckV3State Incoming, AckV3State Outgoing) GetCombinedAckV3State()
    {
        if (!IsMutuallyVisible)
        {
            var unknownState = new AckV3State(AckV3Outcome.Unknown, null, null, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
            return (unknownState, unknownState);
        }

        // Read all outgoing state atomically
        bool hasPending;
        string? outgoingAckId;
        DateTimeOffset? outgoingAckTime;
        bool? outgoingAckSuccess;
        Sphene.API.Dto.User.AcknowledgmentErrorCode outgoingErrorCode;
        string? outgoingErrorMessage;

        lock (_ackStateLock)
        {
            hasPending = HasPendingAcknowledgment;
            outgoingAckId = LastAcknowledgmentId;
            outgoingAckTime = LastAcknowledgmentTime;
            outgoingAckSuccess = LastAcknowledgmentSuccess;
            outgoingErrorCode = LastAcknowledgmentErrorCode;
            outgoingErrorMessage = LastAcknowledgmentErrorMessage;
        }

        AckV3State outgoingState;
        if (hasPending)
        {
            outgoingState = new(AckV3Outcome.Pending, outgoingAckId, outgoingAckTime, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
        }
        else if (outgoingAckSuccess == true)
        {
            outgoingState = new(AckV3Outcome.Success, outgoingAckId, outgoingAckTime, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
        }
        else if (outgoingAckSuccess == false)
        {
            outgoingState = new(AckV3Outcome.Fail, outgoingAckId, outgoingAckTime, outgoingErrorCode, outgoingErrorMessage);
        }
        else
        {
            outgoingState = new(AckV3Outcome.Unknown, outgoingAckId, outgoingAckTime, Sphene.API.Dto.User.AcknowledgmentErrorCode.None, null);
        }

        // Get incoming state (already atomic from GetIncomingAckV3State)
        var incomingState = GetIncomingAckV3State();

        return (incomingState, outgoingState);
    }
    
    private sealed record LastReceivedAckContext(string DataHash, bool RequiresAcknowledgment, string? SessionId);
    private LastReceivedAckContext? _lastIncomingAckContext;
    public string? LastIncomingAckHash => _lastIncomingAckContext?.DataHash;
    public string? LastIncomingAckSessionId => _lastIncomingAckContext?.SessionId;

    public void SetOutgoingAcknowledgmentContext(string? dataHash, string? sessionId)
    {
        lock (_ackStateLock)
        {
            LastOutgoingAcknowledgmentHash = dataHash;
            LastOutgoingAcknowledgmentSessionId = sessionId;
        }
    }

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

    public nint GetPlayerCharacterAddress()
    {
        return CachedPlayer?.PlayerCharacter ?? nint.Zero;
    }

    public string? GetCurrentDataHash()
    {
        return LastReceivedCharacterData?.DataHash?.Value;
    }

    public bool IsLatestReceivedDataApplied()
    {
        return CachedPlayer != null
            && LastReceivedCharacterData != null
            && CachedPlayer.IsCharacterDataAppliedForCurrentCharacter(LastReceivedCharacterData);
    }

    public Guid GetPenumbraCollectionId()
    {
        return CachedPlayer?.GetPenumbraCollectionId() ?? Guid.Empty;
    }

    public IReadOnlyDictionary<string, string> GetLoadedCollectionPathsSnapshot()
    {
        if (CachedPlayer == null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return CachedPlayer.GetLastLoadedCollectionPathsSnapshot();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetCurrentPenumbraActivePathsByGamePathAsync()
    {
        if (CachedPlayer == null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return await CachedPlayer.GetCurrentPenumbraActivePathsByGamePathAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetMinionOrMountActivePathsByGamePathAsync()
    {
        if (CachedPlayer == null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return await CachedPlayer.GetMinionOrMountActivePathsByGamePathAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPetActivePathsByGamePathAsync()
    {
        if (CachedPlayer == null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return await CachedPlayer.GetPetActivePathsByGamePathAsync().ConfigureAwait(false);
    }

    internal void SetMutualVisibility(bool isMutual)
    {
        if (IsMutuallyVisible == isMutual) return;
        if (!isMutual)
        {
            ResetAcknowledgmentState();
        }
        IsMutuallyVisible = isMutual;
        Mediator.Publish(new StructuralRefreshUiMessage());
    }

    internal void ResetSpheneDataToVanilla()
    {
        if (CachedPlayer == null) return;

        Logger.LogDebug("Resetting Sphene data to vanilla for {user}", UserData.AliasOrUID);
        CachedPlayer.ResetSpheneDataToVanilla();
    }

    private void ResetAcknowledgmentState()
    {
        lock (_ackStateLock)
        {
            HasPendingAcknowledgment = false;
            LastAcknowledgmentSuccess = null;
            LastAcknowledgmentTime = null;
            LastAcknowledgmentId = null;
            LastAcknowledgmentErrorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.None;
            LastAcknowledgmentErrorMessage = null;

            // Preserve incoming acknowledgment history - only clear outgoing state
            // Incoming acknowledgment state should persist regardless of visibility changes
            LastAcknowledgedIncomingDataHash = null;
            LastAcknowledgedIncomingTime = null;
            // Do NOT clear: LastIncomingAcknowledgmentHash, LastIncomingAcknowledgmentSuccess, etc.
            // These should persist across visibility changes
            // LastIncomingAcknowledgmentHash = null;
            // LastIncomingAcknowledgmentSuccess = null;
            // LastIncomingAcknowledgmentErrorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.None;
            // LastIncomingAcknowledgmentErrorMessage = null;
            // LastIncomingAcknowledgmentTime = null;
            // _lastIncomingAckContext = null;

            LastOutgoingAcknowledgmentHash = null;
            LastOutgoingAcknowledgmentSessionId = null;
        }

        Mediator.Publish(new AcknowledgmentUiRefreshMessage(User: UserData));
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
            if (_apiController.Value.IsTransientDisconnectInProgress)
            {
                return;
            }
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
    }

    public void ApplyData(OnlineUserCharaDataDto data)
    {
        _applicationCts = _applicationCts.CancelRecreate();
        if (!string.IsNullOrWhiteSpace(data.DataHash))
        {
            _lastIncomingAckContext = new LastReceivedAckContext(data.DataHash, data.RequiresAcknowledgment, data.SessionId);
        }
        UpdateReceivedCharacterDataCache(data);
        ResetApplyRetry();
        var forceRedrawFromReload = ConsumeForcedRedrawRequest();
        bool shouldApply = forceRedrawFromReload
            || CachedPlayer == null
            || LastReceivedCharacterData == null
            || !CachedPlayer.IsCharacterDataAppliedForCurrentCharacter(LastReceivedCharacterData);

        var hash = data.DataHash;
        var shortHash = string.IsNullOrEmpty(hash) ? "NONE" : hash[..Math.Min(8, hash.Length)];
        AddApplyDebug($"Data received hash={shortHash} requiresAck={data.RequiresAcknowledgment} forceRedraw={forceRedrawFromReload}");
        Logger.LogDebug("{tag} Receive: user={user} hash={hash} requiresAck={requiresAck} shouldApply={shouldApply} forceRedraw={forceRedraw}",
            SyncProgressTag, data.User.AliasOrUID, shortHash, data.RequiresAcknowledgment, shouldApply, forceRedrawFromReload);

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
                        ApplyLastReceivedData(forceRedrawFromReload, forceRedrawFromReload);
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
                    await SendAcknowledgmentIfRequired(data, false, true,
                        Sphene.API.Dto.User.AcknowledgmentErrorCode.Timeout,
                        "Apply delayed path timed out").ConfigureAwait(false);
                }
            });
            return;
        }

        if (shouldApply)
        {
            ApplyLastReceivedData(forceRedrawFromReload, forceRedrawFromReload);
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

    public int RequestForcedRedrawOnNextCharacterReload()
    {
        _forceRedrawRequestAt = DateTimeOffset.UtcNow;
        var requestToken = Interlocked.Increment(ref _forceRedrawRequestToken);
        Interlocked.Exchange(ref _forceRedrawOnNextData, 1);
        return requestToken;
    }

    public async Task ApplyLastKnownDataIfReloadPendingAsync(int requestToken, TimeSpan? waitTime = null)
    {
        await Task.Delay(waitTime ?? TimeSpan.FromSeconds(4)).ConfigureAwait(false);

        if (requestToken != Volatile.Read(ref _forceRedrawRequestToken))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _forceRedrawOnNextData, 0, 1) != 1)
        {
            return;
        }

        Logger.LogDebug("{tag} Reload fallback apply triggered: user={user}", SyncProgressTag, UserData.AliasOrUID);
        await _dalamudUtilService.RunOnFrameworkThread(() => ApplyLastReceivedData(forced: true, forceRedrawIfDisabled: true)).ConfigureAwait(false);
    }

    private bool ConsumeForcedRedrawRequest()
    {
        if (Interlocked.Exchange(ref _forceRedrawOnNextData, 0) == 0)
        {
            return false;
        }

        return (DateTimeOffset.UtcNow - _forceRedrawRequestAt) <= TimeSpan.FromSeconds(30);
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
        Logger.LogDebug("{tag} Cache updated: user={user} newHash={newHash} previousHash={prevHash} changed={changed}",
            SyncProgressTag,
            data.User.AliasOrUID,
            string.IsNullOrEmpty(newHash) ? "NONE" : newHash[..Math.Min(8, newHash.Length)],
            string.IsNullOrEmpty(previousHash) ? "NONE" : previousHash[..Math.Min(8, previousHash.Length)],
            !string.Equals(previousHash, newHash, StringComparison.Ordinal));
    }

    public void RestoreReceivedCharacterDataCache(CharacterData data, DateTimeOffset cachedAt)
    {
        var previousHash = LastReceivedCharacterData?.DataHash?.Value;
        var newHash = data.DataHash.Value;

        if (!string.Equals(previousHash, newHash, StringComparison.Ordinal))
        {
            PreviousReceivedCharacterData = LastReceivedCharacterData;
            PreviousReceivedCharacterDataHash = previousHash;
            LastReceivedCharacterDataChangeTime = cachedAt;
        }

        LastReceivedCharacterData = data;
        LastReceivedCharacterDataHash = newHash;
        LastReceivedCharacterDataTime = cachedAt;
        Logger.LogDebug("{tag} Cache restored: user={user} hash={hash} cachedAt={cachedAt}",
            SyncProgressTag,
            UserData.AliasOrUID,
            string.IsNullOrEmpty(newHash) ? "NONE" : newHash[..Math.Min(8, newHash.Length)],
            cachedAt);
    }

    public void ApplyLastReceivedData(bool forced = false, bool forceRedrawIfDisabled = false)
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
        CachedPlayer.ApplyCharacterData(Guid.NewGuid(),
            RemoveNotSyncedFiles(LastReceivedCharacterData.DeepClone())!,
            forced,
            forceRedrawIfDisabled,
            forceRedrawApplication: forceRedrawIfDisabled);
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

    public string GetPlayerNameHash()
    {
        return CachedPlayer?.PlayerNameHash ?? string.Empty;
    }

    public bool HasAnyConnection()
    {
        return UserPair.Groups.Any() || UserPair.IndividualPairStatus != IndividualPairStatus.None;
    }

    public void MarkOffline(bool wait = true, bool endGracePeriod = false)
    {
        try
        {
            if (wait)
                _creationSemaphore.Wait();
            
            // If entering grace period, just set the flag and keep CachedPlayer
            if (!endGracePeriod)
            {
                _isInOfflineGrace = true;
                return;
            }
            
            // Grace period ended - perform full offline cleanup
            _isInOfflineGrace = false;
            SetMutualVisibility(false);
            WasMutuallyVisibleInGpose = false;
            ResetApplyRetry();
            LastReceivedCharacterData = null;
            PreviousReceivedCharacterData = null;
            LastReceivedCharacterDataHash = null;
            PreviousReceivedCharacterDataHash = null;
            LastReceivedCharacterDataTime = null;
            LastReceivedCharacterDataChangeTime = null;
            LastAcknowledgedIncomingDataHash = null;
            LastAcknowledgedIncomingTime = null;
            LastIncomingAcknowledgmentHash = null;
            LastIncomingAcknowledgmentSuccess = null;
            LastIncomingAcknowledgmentErrorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.None;
            LastIncomingAcknowledgmentErrorMessage = null;
            LastIncomingAcknowledgmentTime = null;
            LastOutgoingAcknowledgmentHash = null;
            LastOutgoingAcknowledgmentSessionId = null;
            _lastIncomingAckContext = null;
            LastAcknowledgmentErrorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.None;
            LastAcknowledgmentErrorMessage = null;
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

        if (_dalamudUtilService.IsInDuty
            && (UserPair.OtherPermissions.IsDisableVFXInDuty() || UserPair.OwnPermissions.IsDisableVFXInDuty()))
        {
            disableIndividualVFX = true;
        }

        Logger.LogTrace("Disable: Sounds: {disableIndividualSounds}, Anims: {disableIndividualAnims}; " +
            "VFX: {disableGroupSounds}",
            disableIndividualSounds, disableIndividualAnimations, disableIndividualVFX);

        if (disableIndividualAnimations || disableIndividualSounds || disableIndividualVFX)
        {
            Logger.LogTrace("Data cleaned up: Animations disabled: {disableAnimations}, Sounds disabled: {disableSounds}, VFX disabled: {disableVFX}",
                disableIndividualAnimations, disableIndividualSounds, disableIndividualVFX);
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
            }
        }

        return data;
    }

    public async Task UpdateAcknowledgmentStatus(string? acknowledgmentId, bool success, DateTimeOffset timestamp,
        Sphene.API.Dto.User.AcknowledgmentErrorCode errorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.None,
        string? errorMessage = null)
    {
        Logger.LogDebug("Updating acknowledgment status: {acknowledgmentId} - Success: {success} for user {user}", acknowledgmentId ?? "null", success, UserData.AliasOrUID);

        bool hasPending;
        bool? ackSuccess;
        DateTimeOffset? ackTime;
        long currentVersion;
        bool idMatchesLatest;

        lock (_ackStateLock)
        {
            idMatchesLatest = string.Equals(LastAcknowledgmentId, acknowledgmentId, StringComparison.Ordinal);
            if (idMatchesLatest)
            {
                LastAcknowledgmentSuccess = success;
                LastAcknowledgmentTime = timestamp;
                HasPendingAcknowledgment = false;
                LastAcknowledgmentErrorCode = success ? Sphene.API.Dto.User.AcknowledgmentErrorCode.None : errorCode;
                LastAcknowledgmentErrorMessage = success ? null : errorMessage;
            }
            else
            {
                Logger.LogDebug("Ignoring stale acknowledgment status update: expected {expected}, got {got} for user {user}",
                    LastAcknowledgmentId, acknowledgmentId, UserData.AliasOrUID);
            }

            hasPending = HasPendingAcknowledgment;
            ackSuccess = LastAcknowledgmentSuccess;
            ackTime = LastAcknowledgmentTime;

            // Increment version counter for UI update coordination
            currentVersion = Interlocked.Increment(ref _acknowledgmentVersion);
        }
        
        // Publish specific pair acknowledgment status change event with version
        Mediator.Publish(new PairAcknowledgmentStatusChangedMessage(
            UserData,
            acknowledgmentId,
            hasPending,
            ackSuccess,
            ackTime,
            currentVersion
        ));
        
        // Publish optimized icon update for acknowledgment status
        var ackData = new AcknowledgmentStatusData(hasPending, ackSuccess, ackTime);
        Mediator.Publish(new UserPairIconUpdateMessage(UserData, IconUpdateType.AcknowledgmentStatus, ackData));
        
        // Publish granular UI refresh for this specific acknowledgment
        Mediator.Publish(new AcknowledgmentUiRefreshMessage(
            AcknowledgmentId: acknowledgmentId,
            User: UserData
        ));
    }

    public async Task SetPendingAcknowledgment(string acknowledgmentId)
    {
        Logger.LogDebug("Setting pending acknowledgment: {acknowledgmentId} for user {user}", acknowledgmentId, UserData.AliasOrUID);
        
        bool hasPending;
        bool? ackSuccess;
        DateTimeOffset? ackTime;
        long currentVersion;
        
        lock (_ackStateLock)
        {
            LastAcknowledgmentId = acknowledgmentId;
            HasPendingAcknowledgment = true;
            LastAcknowledgmentSuccess = null;
            LastAcknowledgmentTime = null;
            LastAcknowledgmentErrorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.None;
            LastAcknowledgmentErrorMessage = null;
            
            hasPending = HasPendingAcknowledgment;
            ackSuccess = LastAcknowledgmentSuccess;
            ackTime = LastAcknowledgmentTime;
            
            // Increment version counter for UI update coordination
            currentVersion = Interlocked.Increment(ref _acknowledgmentVersion);
        }
        
        // Publish specific pair acknowledgment status change event with version
        Mediator.Publish(new PairAcknowledgmentStatusChangedMessage(
            UserData,
            acknowledgmentId,
            hasPending,
            ackSuccess,
            ackTime,
            currentVersion
        ));
        
        // Publish optimized icon update for acknowledgment status
        var ackData = new AcknowledgmentStatusData(hasPending, ackSuccess, ackTime);
        Mediator.Publish(new UserPairIconUpdateMessage(UserData, IconUpdateType.AcknowledgmentStatus, ackData));
        
        // Publish acknowledgment pending event
        Mediator.Publish(new AcknowledgmentPendingMessage(
            new AcknowledgmentEventDto(
                acknowledgmentId,
                UserData,
                AcknowledgmentStatus.Pending,
                DateTime.UtcNow)
        ));
        
        // Publish granular UI refresh for this specific acknowledgment
        Mediator.Publish(new AcknowledgmentUiRefreshMessage(
            AcknowledgmentId: acknowledgmentId,
            User: UserData
        ));
        
        // Keep legacy acknowledgment status change event for backward compatibility
        Mediator.Publish(new AcknowledgmentStatusChangedMessage(
            new AcknowledgmentEventDto(
                acknowledgmentId,
                UserData,
                AcknowledgmentStatus.Pending,
                DateTime.UtcNow)
        ));
    }

    public void SetBuildStartPendingStatus()
    {
        Logger.LogInformation("Setting build start pending status for user {user}", UserData.AliasOrUID);
        lock (_ackStateLock)
        {
            HasPendingAcknowledgment = true;
            LastAcknowledgmentSuccess = null;
            LastAcknowledgmentTime = null;
            LastAcknowledgmentId = null; // No specific acknowledgment ID for build start
        }
    }

    public void ClearBuildStartPendingStatus()
    {
        bool shouldClear;
        long currentVersion = 0;
        
        lock (_ackStateLock)
        {
            if (!HasPendingAcknowledgment || LastAcknowledgmentId != null)
            {
                shouldClear = false;
            }
            else
            {
                HasPendingAcknowledgment = false;
                LastAcknowledgmentSuccess = null;
                LastAcknowledgmentTime = null;
                shouldClear = true;
                
                // Increment version counter for UI update coordination
                currentVersion = Interlocked.Increment(ref _acknowledgmentVersion);
            }
        }

        if (!shouldClear) return;

        Mediator.Publish(new PairAcknowledgmentStatusChangedMessage(
            UserData,
            null,
            HasPendingAcknowledgment,
            LastAcknowledgmentSuccess,
            LastAcknowledgmentTime,
            currentVersion
        ));

        var ackData = new AcknowledgmentStatusData(HasPendingAcknowledgment, LastAcknowledgmentSuccess, LastAcknowledgmentTime);
        Mediator.Publish(new UserPairIconUpdateMessage(UserData, IconUpdateType.AcknowledgmentStatus, ackData));

        Mediator.Publish(new AcknowledgmentUiRefreshMessage(User: UserData));
    }

    public async Task ClearPendingAcknowledgment(string acknowledgmentId, MessageService? messageService = null)
    {
        bool shouldClear;
        long currentVersion = 0;
        
        lock (_ackStateLock)
        {
            // Only clear if this is the acknowledgment we're waiting for
            if (string.Equals(LastAcknowledgmentId, acknowledgmentId, StringComparison.Ordinal))
            {
                Logger.LogDebug("Clearing pending acknowledgment: {acknowledgmentId} for user {user}", acknowledgmentId, UserData.AliasOrUID);
                HasPendingAcknowledgment = false;
                LastAcknowledgmentId = null;
                shouldClear = true;
                
                // Increment version counter for UI update coordination
                currentVersion = Interlocked.Increment(ref _acknowledgmentVersion);
            }
            else
            {
                Logger.LogDebug("Not clearing pending acknowledgment - ID mismatch. Expected: {expected}, Got: {got} for user {user}", 
                    LastAcknowledgmentId, acknowledgmentId, UserData.AliasOrUID);
                shouldClear = false;
            }
        }

        if (!shouldClear) return;
        
        // Add notification if MessageService is available
        messageService?.AddTaggedMessage(
            $"pair_clear_{acknowledgmentId}_{UserData.UID}",
            $"Cleared pending acknowledgment for {UserData.AliasOrUID}",
            NotificationType.Info,
            "Acknowledgment Cleared",
            TimeSpan.FromSeconds(2)
        );
        
        // Publish specific pair acknowledgment status change event with version
        Mediator.Publish(new PairAcknowledgmentStatusChangedMessage(
            UserData,
            acknowledgmentId,
            HasPendingAcknowledgment,
            LastAcknowledgmentSuccess,
            LastAcknowledgmentTime,
            currentVersion
        ));
        
        // Publish granular UI refresh for this specific acknowledgment
        Mediator.Publish(new AcknowledgmentUiRefreshMessage(
            AcknowledgmentId: acknowledgmentId,
            User: UserData
        ));
        
        // Publish acknowledgment status change event
        Mediator.Publish(new AcknowledgmentStatusChangedMessage(
            new AcknowledgmentEventDto(
                acknowledgmentId,
                UserData,
                AcknowledgmentStatus.Received,
                DateTime.UtcNow)
        ));
    }

    public void ClearPendingAcknowledgmentForce(MessageService? messageService = null)
    {
        string? previousAckId;
        bool hasPending;
        bool? ackSuccess;
        DateTimeOffset? ackTime;
        long currentVersion;
        
        lock (_ackStateLock)
        {
            previousAckId = LastAcknowledgmentId;
            Logger.LogDebug("Force clearing pending acknowledgment for user {user}", UserData.AliasOrUID);
            HasPendingAcknowledgment = false;
            LastAcknowledgmentId = null;
            
            hasPending = HasPendingAcknowledgment;
            ackSuccess = LastAcknowledgmentSuccess;
            ackTime = LastAcknowledgmentTime;
            
            // Increment version counter for UI update coordination
            currentVersion = Interlocked.Increment(ref _acknowledgmentVersion);
        }
        
        // Add notification if MessageService is available
        messageService?.AddTaggedMessage(
            $"pair_force_clear_{previousAckId}_{UserData.UID}",
            $"Force cleared pending acknowledgment for {UserData.AliasOrUID}",
            NotificationType.Warning,
            "Acknowledgment Force Cleared",
            TimeSpan.FromSeconds(3)
        );
        
        // Publish specific pair acknowledgment status change event with version
        Mediator.Publish(new PairAcknowledgmentStatusChangedMessage(
            UserData,
            previousAckId,
            hasPending,
            ackSuccess,
            ackTime,
            currentVersion
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
                new AcknowledgmentEventDto(
                    previousAckId,
                    UserData,
                    AcknowledgmentStatus.Cancelled,
                    DateTime.UtcNow)
            ));
        }

    }



    private async Task SendAcknowledgmentIfRequired(OnlineUserCharaDataDto data, bool success, bool hashVerificationPassed = true,
        Sphene.API.Dto.User.AcknowledgmentErrorCode? errorCodeOverride = null, string? errorMessageOverride = null)
    {
        Logger.LogDebug("SendAcknowledgmentIfRequired called - RequiresAcknowledgment: {requires}, Hash: {hash}, Success: {success}, HashVerification: {hashVerification}", 
            data.RequiresAcknowledgment, data.DataHash[..Math.Min(8, data.DataHash.Length)], success, hashVerificationPassed);
        
        if (!data.RequiresAcknowledgment || string.IsNullOrEmpty(data.DataHash))
        {
            Logger.LogDebug("Skipping acknowledgment - RequiresAcknowledgment: {requires}, DataHash null/empty: {empty}", 
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
                errorCode = errorCodeOverride ?? Sphene.API.Dto.User.AcknowledgmentErrorCode.ApplyFailed;
                errorMessage = errorMessageOverride ?? "Failed to apply character data";
            }
            else if (!hashVerificationPassed)
            {
                errorCode = errorCodeOverride ?? Sphene.API.Dto.User.AcknowledgmentErrorCode.HashVerificationFailed;
                errorMessage = errorMessageOverride ?? "Data hash verification failed - data integrity compromised";
            }

            if (!finalSuccess)
            {
                var context = $"ctx: localInDuty={_dalamudUtilService.IsInDuty} localInCombat={_dalamudUtilService.IsInCombatOrPerforming}";
                errorMessage = string.IsNullOrWhiteSpace(errorMessage) ? context : $"{errorMessage} ({context})";
            }
            
            var acknowledgmentDto = new CharacterDataAcknowledgmentDto(UserData, data.DataHash)
            {
                Success = finalSuccess,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                AcknowledgedAt = DateTime.UtcNow,
                SessionId = data.SessionId ?? string.Empty
            };

            LastIncomingAcknowledgmentHash = data.DataHash;
            LastIncomingAcknowledgmentSuccess = finalSuccess;
            LastIncomingAcknowledgmentErrorCode = finalSuccess ? Sphene.API.Dto.User.AcknowledgmentErrorCode.None : errorCode;
            LastIncomingAcknowledgmentErrorMessage = finalSuccess ? null : errorMessage;
            LastIncomingAcknowledgmentTime = DateTimeOffset.UtcNow;

            Logger.LogDebug("Sending acknowledgment to server - Hash: {hash}, User: {user}, Success: {success}, ErrorCode: {errorCode}", 
                data.DataHash[..Math.Min(8, data.DataHash.Length)], UserData.AliasOrUID, finalSuccess, errorCode);

            // Send acknowledgment through the mediator
             Mediator.Publish(new SendCharacterDataAcknowledgmentMessage(acknowledgmentDto));
            Mediator.Publish(new DebugLogEventMessage(
                finalSuccess ? LogLevel.Information : LogLevel.Warning,
                "ACK",
                finalSuccess ? "Ack sent" : "Ack sent (fail)",
                Uid: UserData.UID,
                Details: $"hash={data.DataHash[..Math.Min(8, data.DataHash.Length)]} code={errorCode} msg={errorMessage ?? "-"} session={data.SessionId ?? "-"}"));
            Logger.LogDebug("Successfully published SendCharacterDataAcknowledgmentMessage for Hash: {hash}", 
                data.DataHash[..Math.Min(8, data.DataHash.Length)]);

            if (finalSuccess)
            {
                var currentReceivedHash = LastReceivedCharacterDataHash;
                if (!string.IsNullOrEmpty(currentReceivedHash)
                    && !string.Equals(currentReceivedHash, data.DataHash, StringComparison.Ordinal))
                {
                    var shortAck = data.DataHash[..Math.Min(8, data.DataHash.Length)];
                    var shortCurrent = currentReceivedHash[..Math.Min(8, currentReceivedHash.Length)];
                    Mediator.Publish(new DebugLogEventMessage(LogLevel.Warning, "ACK",
                        "Ack sent for non-latest hash",
                        Uid: UserData.UID,
                        Details: $"acked={shortAck} current={shortCurrent} session={data.SessionId ?? "-"}"));
                }

                LastAcknowledgedIncomingDataHash = data.DataHash;
                LastAcknowledgedIncomingTime = DateTimeOffset.UtcNow;
                Mediator.Publish(new RefreshUiMessage());
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
            Logger.LogDebug("{tag} Apply completed: user={user} success={success} hash={hash} appId={appId}",
                SyncProgressTag, UserData.AliasOrUID, message.Success,
                string.IsNullOrEmpty(message.DataHash) ? "NONE" : message.DataHash[..Math.Min(8, message.DataHash.Length)],
                message.ApplicationId);
            if (message.Success)
            {
                ResetApplyRetry();
            }
            
            if (!_pendingAcknowledgmentQueue.IsEmpty)
            {
                OnlineUserCharaDataDto? matchingAcknowledgment = null;
                var processedCount = 0;
                var remaining = new List<OnlineUserCharaDataDto>();
                var appliedHash = message.DataHash;
                
                while (_pendingAcknowledgmentQueue.TryDequeue(out var acknowledgmentData))
                {
                    processedCount++;
                    if (!string.IsNullOrEmpty(appliedHash)
                        && string.Equals(acknowledgmentData.DataHash, appliedHash, StringComparison.Ordinal))
                    {
                        if (matchingAcknowledgment == null || acknowledgmentData.SequenceNumber > matchingAcknowledgment.SequenceNumber)
                        {
                            matchingAcknowledgment = acknowledgmentData;
                        }
                    }
                    else
                    {
                        remaining.Add(acknowledgmentData);
                    }
                }

                foreach (var pending in remaining)
                {
                    _pendingAcknowledgmentQueue.Enqueue(pending);
                }

                Logger.LogDebug("{tag} Ack queue processed: processed={processedCount} remaining={remainingCount} appliedHash={hash}",
                    SyncProgressTag,
                    processedCount, remaining.Count, string.IsNullOrEmpty(appliedHash) ? "NONE" : appliedHash[..Math.Min(8, appliedHash.Length)]);

                if (matchingAcknowledgment != null)
                {
                    try
                    {
                        if (!message.Success)
                        {
                            if (_applyRetryCount < MaxApplyRetryBackoffSteps)
                            {
                                ScheduleApplyRetry(increment: true);
                                _pendingAcknowledgmentQueue.Enqueue(matchingAcknowledgment);
                                return;
                            }

                            var errorCodeOverride = message.ErrorCode == Sphene.API.Dto.User.AcknowledgmentErrorCode.None
                                ? Sphene.API.Dto.User.AcknowledgmentErrorCode.ApplyFailed
                                : message.ErrorCode;
                            var errorMessageOverride = string.IsNullOrWhiteSpace(message.ErrorMessage)
                                ? "Apply failed after retries"
                                : message.ErrorMessage;
                            await SendAcknowledgmentIfRequired(matchingAcknowledgment, false, true, errorCodeOverride, errorMessageOverride).ConfigureAwait(false);
                            return;
                        }

                        var verificationSuccess = true;
                        var verificationErrorCode = (Sphene.API.Dto.User.AcknowledgmentErrorCode?)null;
                        var verificationErrorMessage = (string?)null;

                        for (var attempt = 1; attempt <= 5; attempt++)
                        {
                            verificationSuccess = true;
                            verificationErrorCode = null;
                            verificationErrorMessage = null;

                            if (!_dalamudUtilService.IsInDuty && !_dalamudUtilService.IsInCombatOrPerforming)
                            {
                                verificationSuccess = VerifyDataHashIntegrity(matchingAcknowledgment, appliedHash);
                                if (!verificationSuccess)
                                {
                                    verificationErrorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.MismatchFailed;
                                    verificationErrorMessage = "Local data verification failed";
                                }
                            }

                            if (verificationSuccess)
                            {
                                verificationSuccess = await VerifyServerHashIntegrityAsync(matchingAcknowledgment, appliedHash).ConfigureAwait(false);
                                if (!verificationSuccess)
                                {
                                    verificationErrorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.HashVerificationFailed;
                                    verificationErrorMessage = "Server hash verification failed";
                                }
                            }

                            if (verificationSuccess && !_dalamudUtilService.IsInDuty && !_dalamudUtilService.IsInCombatOrPerforming)
                            {
                                var (pathsOk, mismatchCount, examplePath) = await VerifyPenumbraActivePathsIntegrityAsync(matchingAcknowledgment).ConfigureAwait(false);
                                if (!pathsOk)
                                {
                                    verificationSuccess = false;
                                    verificationErrorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.MismatchFailed;
                                    verificationErrorMessage = $"Active paths mismatch count={mismatchCount}{(string.IsNullOrWhiteSpace(examplePath) ? string.Empty : $" example={examplePath}")}";
                                }
                            }

                            if (verificationSuccess)
                            {
                                break;
                            }

                            if (attempt < 5)
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                            }
                        }
                        
                        Logger.LogDebug("{tag} Ack sending: appSuccess={appSuccess} hashVerified={hashSuccess} hash={hash}", 
                            SyncProgressTag,
                            message.Success, verificationSuccess, string.IsNullOrEmpty(appliedHash) ? "NONE" : appliedHash[..Math.Min(8, appliedHash.Length)]);
                        
                        await SendAcknowledgmentIfRequired(matchingAcknowledgment, true, verificationSuccess, verificationErrorCode, verificationErrorMessage).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "{tag} Ack send failed: user={userUid}", SyncProgressTag, message.UserUID);
                    }
                }
                else
                {
                    Logger.LogDebug("{tag} Ack missing: appliedHash={hash} remaining={count}",
                        SyncProgressTag,
                        string.IsNullOrEmpty(appliedHash) ? "NONE" : appliedHash[..Math.Min(8, appliedHash.Length)],
                        remaining.Count);

                    if (!message.Success && remaining.Count > 0 && _applyRetryCount < MaxApplyRetryBackoffSteps)
                    {
                        ScheduleApplyRetry(increment: true);
                    }
                }
            }
            else
            {
                Logger.LogDebug("{tag} Ack queue empty: playerName={playerName} success={success}", SyncProgressTag, message.PlayerName, message.Success);

                if (!message.Success && _applyRetryCount < MaxApplyRetryBackoffSteps)
                {
                    ScheduleApplyRetry(increment: true);
                }
            }
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

            if (_dalamudUtilService.IsInCombatOrPerforming || _dalamudUtilService.IsInGpose || _dalamudUtilService.IsInCutscene)
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

    private async Task<bool> VerifyServerHashIntegrityAsync(OnlineUserCharaDataDto acknowledgmentData, string? appliedHash)
    {
        try
        {
            var senderUid = acknowledgmentData.User.UID;
            var expectedHash = appliedHash;
            if (string.IsNullOrEmpty(expectedHash) && LastReceivedCharacterData != null)
            {
                expectedHash = LastReceivedCharacterData.DataHash.Value;
            }

            if (string.IsNullOrWhiteSpace(senderUid) || string.IsNullOrWhiteSpace(expectedHash))
            {
                Logger.LogWarning("{tag} Server hash verify skipped: sender/hash missing", SyncProgressTag);
                return false;
            }

            var response = await _apiController.Value.ValidateCharaDataHash(senderUid, expectedHash).ConfigureAwait(false);
            if (response == null)
            {
                Logger.LogWarning("{tag} Server hash verify failed: null response sender={sender}", SyncProgressTag, acknowledgmentData.User.AliasOrUID);
                return false;
            }

            var remoteMatches = response.IsValid
                && (string.IsNullOrEmpty(response.CurrentHash)
                    || string.Equals(response.CurrentHash, expectedHash, StringComparison.Ordinal));

            Logger.LogDebug("{tag} Server hash verify: sender={sender} expected={expected} valid={valid} current={current} match={match}",
                SyncProgressTag,
                acknowledgmentData.User.AliasOrUID,
                expectedHash[..Math.Min(8, expectedHash.Length)],
                response.IsValid,
                string.IsNullOrEmpty(response.CurrentHash) ? "NONE" : response.CurrentHash[..Math.Min(8, response.CurrentHash.Length)],
                remoteMatches);

            return remoteMatches;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{tag} Server hash verify failed with exception", SyncProgressTag);
            return false;
        }
    }

    private async Task<(bool Success, int MismatchCount, string? ExamplePath)> VerifyPenumbraActivePathsIntegrityAsync(OnlineUserCharaDataDto acknowledgmentData)
    {
        try
        {
            if (CachedPlayer == null)
            {
                return (true, 0, null);
            }

            var charaData = acknowledgmentData?.CharaData;
            if (charaData == null || charaData.FileReplacements == null)
            {
                return (true, 0, null);
            }

            var delivered = BuildDeliveredPathState(charaData);
            if (delivered.Count == 0)
            {
                return (true, 0, null);
            }

            var playerActivePaths = await GetCurrentPenumbraActivePathsByGamePathAsync().ConfigureAwait(false);
            var minionActivePaths = await GetMinionOrMountActivePathsByGamePathAsync().ConfigureAwait(false);
            var petActivePaths = await GetPetActivePathsByGamePathAsync().ConfigureAwait(false);

            var mismatchCount = 0;
            string? example = null;

            foreach (var kvp in delivered)
            {
                var gamePath = kvp.Key;
                var state = kvp.Value;
                if (!state.IsActive)
                {
                    continue;
                }

                var activePaths = state.IsMinionOrMount ? minionActivePaths : state.IsPet ? petActivePaths : playerActivePaths;
                var isPenumbraActive = activePaths.TryGetValue(gamePath, out var source) && !string.IsNullOrEmpty(source);
                if (isPenumbraActive)
                {
                    continue;
                }

                mismatchCount++;
                example ??= gamePath;
                if (mismatchCount >= 10)
                {
                    break;
                }
            }

            return (mismatchCount == 0, mismatchCount, example);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{tag} Active paths verify failed with exception", SyncProgressTag);
            return (true, 0, null);
        }
    }

    private static Dictionary<string, DeliveredPathState> BuildDeliveredPathState(CharacterData characterData)
    {
        var result = new Dictionary<string, DeliveredPathState>(StringComparer.OrdinalIgnoreCase);

        foreach (var objectKvp in characterData.FileReplacements)
        {
            var objectKind = objectKvp.Key;
            foreach (var fileReplacement in objectKvp.Value)
            {
                if (fileReplacement.GamePaths == null)
                {
                    continue;
                }

                foreach (var gamePath in fileReplacement.GamePaths)
                {
                    if (string.IsNullOrWhiteSpace(gamePath))
                    {
                        continue;
                    }

                    var normalizedPath = gamePath.Replace('\\', '/').ToLowerInvariant();
                    if (!result.TryGetValue(normalizedPath, out var state))
                    {
                        state = new DeliveredPathState();
                        result[normalizedPath] = state;
                    }

                    state.IsActive = fileReplacement.IsActive;
                    state.IsMinionOrMount |= objectKind == ObjectKind.MinionOrMount;
                    state.IsPet |= objectKind == ObjectKind.Pet;
                }
            }
        }

        return result;
    }

    private sealed class DeliveredPathState
    {
        public bool IsActive { get; set; }
        public bool IsMinionOrMount { get; set; }
        public bool IsPet { get; set; }
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
