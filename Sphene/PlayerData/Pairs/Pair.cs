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
using Sphene.Utils;
using Microsoft.Extensions.Logging;

namespace Sphene.PlayerData.Pairs;

public class Pair : DisposableMediatorSubscriberBase
{
    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly SemaphoreSlim _creationSemaphore = new(1);
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private CancellationTokenSource _applicationCts = new();
    private OnlineUserIdentDto? _onlineUserIdentDto = null;

    public Pair(ILogger<Pair> logger, UserFullPairDto userPair, PairHandlerFactory cachedPlayerFactory,
        SpheneMediator mediator, ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService) : base(logger, mediator)
    {
        UserPair = userPair;
        _cachedPlayerFactory = cachedPlayerFactory;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        
        // Subscribe to character data application completion messages
        Mediator.Subscribe<CharacterDataApplicationCompletedMessage>(this, OnCharacterDataApplicationCompleted);
    }

    public bool HasCachedPlayer => CachedPlayer != null && !string.IsNullOrEmpty(CachedPlayer.PlayerName) && _onlineUserIdentDto != null;
    public IndividualPairStatus IndividualPairStatus => UserPair.IndividualPairStatus;
    public bool IsDirectlyPaired => IndividualPairStatus != IndividualPairStatus.None;
    public bool IsOneSidedPair => IndividualPairStatus == IndividualPairStatus.OneSided;
    public bool IsOnline => CachedPlayer != null;

    public bool IsPaired => IndividualPairStatus == IndividualPairStatus.Bidirectional || UserPair.Groups.Any();
    public bool IsPaused => UserPair.OwnPermissions.IsPaused();
    public bool IsVisible => CachedPlayer?.IsVisible ?? false;
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
    
    // Pending acknowledgment data for delayed sending
    private OnlineUserCharaDataDto? _pendingAcknowledgmentData = null;

    public UserData UserData => UserPair.User;

    public UserFullPairDto UserPair { get; set; }
    private PairHandler? CachedPlayer { get; set; }

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
        var isWhitelisted = config.UIDsToIgnore.Contains(UserData.UID);
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
                    
                    // Store acknowledgment data for delayed sending after application completes
                    if (data.RequiresAcknowledgment && !string.IsNullOrEmpty(data.AcknowledgmentId))
                    {
                        _pendingAcknowledgmentData = data;
                        Logger.LogInformation("Stored pending acknowledgment data for delayed sending (delayed path) - AckId: {acknowledgmentId}", data.AcknowledgmentId);
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
        
        // Store acknowledgment data for delayed sending after application completes
        if (data.RequiresAcknowledgment && !string.IsNullOrEmpty(data.AcknowledgmentId))
        {
            _pendingAcknowledgmentData = data;
            Logger.LogInformation("Stored pending acknowledgment data for delayed sending - AckId: {acknowledgmentId}", data.AcknowledgmentId);
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

    internal void SetIsUploading()
    {
        CachedPlayer?.SetUploading();
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

    public void UpdateAcknowledgmentStatus(string acknowledgmentId, bool success, DateTimeOffset timestamp)
    {
        Logger.LogInformation("Updating acknowledgment status: {acknowledgmentId} - Success: {success} for user {user}", acknowledgmentId, success, UserData.AliasOrUID);
        LastAcknowledgmentId = acknowledgmentId;
        LastAcknowledgmentSuccess = success;
        LastAcknowledgmentTime = timestamp;
        HasPendingAcknowledgment = false;
        
        // Trigger UI update
        Mediator.Publish(new RefreshUiMessage());
    }

    public void SetPendingAcknowledgment(string acknowledgmentId)
    {
        Logger.LogInformation("Setting pending acknowledgment: {acknowledgmentId} for user {user}", acknowledgmentId, UserData.AliasOrUID);
        LastAcknowledgmentId = acknowledgmentId;
        HasPendingAcknowledgment = true;
        LastAcknowledgmentSuccess = null;
        LastAcknowledgmentTime = null;
    }

    public void SetBuildStartPendingStatus()
    {
        Logger.LogInformation("Setting build start pending status for user {user}", UserData.AliasOrUID);
        HasPendingAcknowledgment = true;
        LastAcknowledgmentSuccess = null;
        LastAcknowledgmentTime = null;
        LastAcknowledgmentId = null; // No specific acknowledgment ID for build start
    }



    private async Task SendAcknowledgmentIfRequired(OnlineUserCharaDataDto data, bool success)
    {
        Logger.LogInformation("SendAcknowledgmentIfRequired called - RequiresAcknowledgment: {requires}, AcknowledgmentId: {id}, Success: {success}", 
            data.RequiresAcknowledgment, data.AcknowledgmentId, success);
        
        if (!data.RequiresAcknowledgment || string.IsNullOrEmpty(data.AcknowledgmentId))
        {
            Logger.LogInformation("Skipping acknowledgment - RequiresAcknowledgment: {requires}, AcknowledgmentId null/empty: {empty}", 
                data.RequiresAcknowledgment, string.IsNullOrEmpty(data.AcknowledgmentId));
            return;
        }

        try
        {
            var acknowledgmentDto = new CharacterDataAcknowledgmentDto(UserData, data.AcknowledgmentId)
            {
                Success = success,
                AcknowledgedAt = DateTime.UtcNow
            };

            Logger.LogInformation("Sending acknowledgment to server - AckId: {acknowledgmentId}, User: {user}, Success: {success}", 
                data.AcknowledgmentId, UserData.AliasOrUID, success);

            // Send acknowledgment through the mediator
             Mediator.Publish(new SendCharacterDataAcknowledgmentMessage(acknowledgmentDto));
            Logger.LogInformation("Successfully published SendCharacterDataAcknowledgmentMessage for {acknowledgmentId}", data.AcknowledgmentId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send character data acknowledgment for {acknowledgmentId}", data.AcknowledgmentId);
        }
    }
    
    /// <summary>
    /// Handles character data application completion and sends delayed acknowledgment if needed
    /// </summary>
    private void OnCharacterDataApplicationCompleted(CharacterDataApplicationCompletedMessage message)
    {
        // Check if this message is for this pair and we have pending acknowledgment data
        if (message.UserUID == UserPair.User.UID && _pendingAcknowledgmentData != null)
        {
            Logger.LogInformation("Character data application completed for {playerName} - sending delayed acknowledgment", message.PlayerName);
            
            try
            {
                _ = Task.Run(async () => 
                {
                    await SendAcknowledgmentIfRequired(_pendingAcknowledgmentData, message.Success).ConfigureAwait(false);
                    _pendingAcknowledgmentData = null; // Clear pending data after sending
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to send delayed acknowledgment for {userUid}", message.UserUID);
            }
        }
    }
}
