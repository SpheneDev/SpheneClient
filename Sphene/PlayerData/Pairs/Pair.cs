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
    private CancellationTokenSource _applicationCts = new();
    private OnlineUserIdentDto? _onlineUserIdentDto = null;
    private readonly VisibilityGateService _visibilityGateService;

    public Pair(ILogger<Pair> logger, UserFullPairDto userPair, PairHandlerFactory cachedPlayerFactory,
        SpheneMediator mediator, ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService, Lazy<ApiController> apiController,
        VisibilityGateService visibilityGateService) : base(logger, mediator)
    {
        UserPair = userPair;
        _cachedPlayerFactory = cachedPlayerFactory;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _apiController = apiController;
        _visibilityGateService = visibilityGateService;
        
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

    public bool IsPaired => IndividualPairStatus == IndividualPairStatus.Bidirectional || UserPair.Groups.Any();
    public bool IsPaused => UserPair.OwnPermissions.IsPaused();
    public bool IsVisible => CachedPlayer?.IsVisible ?? false;
    public bool IsMutuallyVisible { get; private set; } = false;
    public bool WasMutuallyVisibleInGpose { get; private set; } = false;
    public bool IsInGpose { get; private set; } = false;
    public CharacterData? LastReceivedCharacterData { get; set; }
    public string? PlayerName => CachedPlayer?.PlayerName ?? string.Empty;
    public long LastAppliedDataBytes => CachedPlayer?.LastAppliedDataBytes ?? -1;
    public long LastAppliedDataTris { get; set; } = -1;
    public long LastAppliedApproximateVRAMBytes { get; set; } = -1;
    public string Ident => _onlineUserIdentDto?.Ident ?? string.Empty;
    
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
        LastReceivedCharacterData = data.CharaData;

        // Assign sequence number for tracking order
        var currentSequence = Interlocked.Increment(ref _acknowledgmentSequence);
        
        if (CachedPlayer == null)
        {
            Logger.LogDebug("Received Data for {uid} but CachedPlayer does not exist, waiting", data.User.UID);
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
                    Logger.LogDebug("Applying delayed data for {uid}", data.User.UID);
                    ApplyLastReceivedData();
                    
                    // Enqueue acknowledgment data for delayed sending after application completes
                    if (data.RequiresAcknowledgment && !string.IsNullOrEmpty(data.DataHash))
                    {
                        // Add sequence number to track order
                        var dataWithSequence = data.DeepClone();
                        dataWithSequence.SequenceNumber = currentSequence;
                        _pendingAcknowledgmentQueue.Enqueue(dataWithSequence);
                        Logger.LogDebug("Enqueued pending acknowledgment data for delayed sending (delayed path) - Hash: {hash}, Sequence: {sequence}", 
                            data.DataHash[..Math.Min(8, data.DataHash.Length)], currentSequence);
                    }
                }
                else
                {
                    await SendAcknowledgmentIfRequired(data, false).ConfigureAwait(false);
                }
            });
            return;
        }

        ApplyLastReceivedData();
        
        // Enqueue acknowledgment data for delayed sending after application completes
        if (data.RequiresAcknowledgment && !string.IsNullOrEmpty(data.DataHash))
        {
            // Add sequence number to track order
            var dataWithSequence = data.DeepClone();
            dataWithSequence.SequenceNumber = currentSequence;
            _pendingAcknowledgmentQueue.Enqueue(dataWithSequence);
            Logger.LogDebug("Enqueued pending acknowledgment data for delayed sending - Hash: {hash}, Sequence: {sequence}, Queue size: {queueSize}", 
                data.DataHash[..Math.Min(8, data.DataHash.Length)], currentSequence, _pendingAcknowledgmentQueue.Count);
        }
    }

    public void ApplyLastReceivedData(bool forced = false)
    {
        if (CachedPlayer == null) return;
        if (LastReceivedCharacterData == null) return;

        CachedPlayer.ApplyCharacterData(Guid.NewGuid(), RemoveNotSyncedFiles(LastReceivedCharacterData.DeepClone())!, forced);
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

    public void MarkOffline(bool wait = true)
    {
        try
        {
            if (wait)
                _creationSemaphore.Wait();
            LastReceivedCharacterData = null;
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

    public async Task UpdateAcknowledgmentStatus(string acknowledgmentId, bool success, DateTimeOffset timestamp)
    {
        Logger.LogDebug("Updating acknowledgment status: {acknowledgmentId} - Success: {success} for user {user}", acknowledgmentId, success, UserData.AliasOrUID);
        LastAcknowledgmentId = acknowledgmentId;
        LastAcknowledgmentSuccess = success;
        LastAcknowledgmentTime = timestamp;
        HasPendingAcknowledgment = false;
        
        // Update AckYou status based on current icon state
        // Green checkmark (success) = true, no icon (cleared) = false
        bool newAckYouStatus = success;
        
        var permissions = UserPair.OwnPermissions;
        permissions.SetAckYou(newAckYouStatus);
        
        try
        {
            await _apiController.Value.UserSetPairPermissions(new(UserData, permissions)).ConfigureAwait(false);
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

            Logger.LogDebug("Sending acknowledgment to server - Hash: {hash}, User: {user}, Success: {success}, ErrorCode: {errorCode}", 
                data.DataHash[..Math.Min(8, data.DataHash.Length)], UserData.AliasOrUID, finalSuccess, errorCode);

            // Send acknowledgment through the mediator
             Mediator.Publish(new SendCharacterDataAcknowledgmentMessage(acknowledgmentDto));
            Logger.LogDebug("Successfully published SendCharacterDataAcknowledgmentMessage for Hash: {hash}", 
                data.DataHash[..Math.Min(8, data.DataHash.Length)]);

            var permissions = UserPair.OwnPermissions;
            var newAckYouStatus = finalSuccess;
            if (permissions.IsAckYou() != newAckYouStatus)
            {
                Logger.LogDebug("SendAcknowledgmentIfRequired: Setting Own AckYou={status} for user {user}", newAckYouStatus, UserData.AliasOrUID);
                permissions.SetAckYou(newAckYouStatus);
                try
                {
                    await _apiController.Value.UserSetPairPermissions(new(UserData, permissions)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "SendAcknowledgmentIfRequired: Failed to send Own AckYou update for user {user}", UserData.AliasOrUID);
                }
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
            Logger.LogDebug("Character data application completed for {playerName} - processing acknowledgment queue", message.PlayerName);
            
            // Process acknowledgment queue and send only the latest acknowledgment
            if (!_pendingAcknowledgmentQueue.IsEmpty)
            {
                OnlineUserCharaDataDto? latestAcknowledgment = null;
                var processedCount = 0;
                var discardedCount = 0;
                
                // Dequeue all pending acknowledgments and keep only the latest one
                while (_pendingAcknowledgmentQueue.TryDequeue(out var acknowledgmentData))
                {
                    processedCount++;
                    if (latestAcknowledgment == null || acknowledgmentData.SequenceNumber > latestAcknowledgment.SequenceNumber)
                    {
                        if (latestAcknowledgment != null)
                        {
                            discardedCount++;
                            Logger.LogDebug("Discarding outdated acknowledgment - Hash: {hash}, Sequence: {sequence}", 
                                latestAcknowledgment.DataHash[..Math.Min(8, latestAcknowledgment.DataHash.Length)], latestAcknowledgment.SequenceNumber);
                        }
                        latestAcknowledgment = acknowledgmentData;
                    }
                    else
                    {
                        discardedCount++;
                        Logger.LogDebug("Discarding outdated acknowledgment - Hash: {hash}, Sequence: {sequence}", 
                            acknowledgmentData.DataHash[..Math.Min(8, acknowledgmentData.DataHash.Length)], acknowledgmentData.SequenceNumber);
                    }
                }
                
                Logger.LogDebug("Processed {processedCount} acknowledgments, discarded {discardedCount}, sending latest with sequence {sequence}", 
                    processedCount, discardedCount, latestAcknowledgment?.SequenceNumber ?? -1);
                
                // Send the latest acknowledgment with hash verification
                if (latestAcknowledgment != null)
                {
                    try
                    {
                        // Verify that the applied data hash matches the received data hash
                        var verificationSuccess = VerifyDataHashIntegrity(latestAcknowledgment);
                        
                        Logger.LogDebug("Sending acknowledgment - Application success: {appSuccess}, Hash verification: {hashSuccess}", 
                            message.Success, verificationSuccess);
                        
                        await SendAcknowledgmentIfRequired(latestAcknowledgment, message.Success, verificationSuccess).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to send delayed acknowledgment for {userUid}", message.UserUID);
                    }
                }
            }
            else
            {
                Logger.LogInformation("No pending acknowledgment data, but character data application completed for {playerName}", message.PlayerName);
            }
        }
    }

    private bool VerifyDataHashIntegrity(OnlineUserCharaDataDto acknowledgmentData)
     {
         try
         {
             if (acknowledgmentData?.CharaData == null)
             {
                 Logger.LogWarning("Cannot verify data hash integrity - acknowledgment data or character data is null");
                 return false;
             }
 
             if (LastReceivedCharacterData == null)
             {
                 Logger.LogWarning("Cannot verify data hash integrity - no last received character data available");
                 return false;
             }
 
             // Compare data hashes using the built-in DataHash property
             var receivedHash = acknowledgmentData.CharaData.DataHash.Value;
             var appliedHash = LastReceivedCharacterData.DataHash.Value;
             
             var hashMatch = string.Equals(receivedHash, appliedHash, StringComparison.Ordinal);
             
             Logger.LogInformation("Data hash verification - Received: {receivedHash}, Applied: {appliedHash}, Match: {hashMatch}", 
                 receivedHash, appliedHash, hashMatch);
             
             return hashMatch;
         }
         catch (Exception ex)
         {
             Logger.LogWarning(ex, "Failed to verify data hash integrity - assuming verification failed");
             return false;
         }
     }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _applicationCts?.Cancel();
            _applicationCts?.Dispose();
            _creationSemaphore.Dispose();
            CachedPlayer?.Dispose();
            CachedPlayer = null;
        }
        base.Dispose(disposing);
    }
}
