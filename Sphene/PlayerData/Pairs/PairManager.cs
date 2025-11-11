using Dalamud.Plugin.Services;
using Sphene.API.Data;
using Sphene.API.Data.Comparer;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.PlayerData.Factories;
using Sphene.Services.Events;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.Services;
using Sphene.WebAPI;
using System.Globalization;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Dalamud.Interface.ImGuiNotification;
using Sphene.API.Dto.Visibility;

namespace Sphene.PlayerData.Pairs;

public sealed class PairManager : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<UserData, Pair> _allClientPairs = new(UserDataComparer.Instance);
    private readonly ConcurrentDictionary<GroupData, GroupFullInfoDto> _allGroups = new(GroupDataComparer.Instance);
    private readonly ConcurrentDictionary<string, HashSet<UserData>> _senderPendingAcknowledgments = new();
    private readonly SessionAcknowledgmentManager _sessionAcknowledgmentManager;
    private readonly SpheneConfigService _configurationService;
    private readonly IContextMenu _dalamudContextMenu;
    private readonly PairFactory _pairFactory;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly Lazy<ApiController> _apiController;
    private readonly MessageService _messageService;
    private readonly AcknowledgmentTimeoutManager _acknowledgmentTimeoutManager;
    private readonly Lazy<AreaBoundSyncshellService> _areaBoundSyncshellService;
    private readonly VisibilityGateService _visibilityGateService;

    private volatile bool _localVisibilityGateActive = false;

    private Lazy<List<Pair>> _directPairsInternal;
    private Lazy<Dictionary<GroupFullInfoDto, List<Pair>>> _groupPairsInternal;
    private Lazy<Dictionary<Pair, List<GroupFullInfoDto>>> _pairsWithGroupsInternal;

    public PairManager(ILogger<PairManager> logger, PairFactory pairFactory,
                SpheneConfigService configurationService, SpheneMediator mediator,
                IContextMenu dalamudContextMenu, ServerConfigurationManager serverConfigurationManager,
                Lazy<ApiController> apiController, SessionAcknowledgmentManager sessionAcknowledgmentManager,
                MessageService messageService, AcknowledgmentTimeoutManager acknowledgmentTimeoutManager,
                Lazy<AreaBoundSyncshellService> areaBoundSyncshellService,
                VisibilityGateService visibilityGateService) : base(logger, mediator)
    {
        _pairFactory = pairFactory;
        _configurationService = configurationService;
        _dalamudContextMenu = dalamudContextMenu;
        _serverConfigurationManager = serverConfigurationManager;
        _apiController = apiController;
        _sessionAcknowledgmentManager = sessionAcknowledgmentManager;
        _messageService = messageService;
        _acknowledgmentTimeoutManager = acknowledgmentTimeoutManager;
        _areaBoundSyncshellService = areaBoundSyncshellService;
        _visibilityGateService = visibilityGateService;

        Mediator.Subscribe<DisconnectedMessage>(this, (_) => ClearPairs());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) =>
        {
            ClearLocalVisibilityGate("CutsceneEnd");
            ReapplyPairData();
        });
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => ApplyLocalVisibilityGate(true, "CutsceneStart"));
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (_) => ApplyLocalVisibilityGate(true, "ZoneSwitchStart"));
        Mediator.Subscribe<ZoneSwitchEndMessage>(this, (_) => ClearLocalVisibilityGate("ZoneSwitchEnd"));
        Mediator.Subscribe<CharacterDataBuildStartedMessage>(this, (_) => SetPendingAcknowledgmentForBuildStart());
        _directPairsInternal = DirectPairsLazy();
        _groupPairsInternal = GroupPairsLazy();
        _pairsWithGroupsInternal = PairsWithGroupsLazy();

        _dalamudContextMenu.OnMenuOpened += DalamudContextMenuOnOnOpenGameObjectContextMenu;
    }

    public List<Pair> DirectPairs => _directPairsInternal.Value;

    public Dictionary<GroupFullInfoDto, List<Pair>> GroupPairs => _groupPairsInternal.Value;
    public Dictionary<GroupData, GroupFullInfoDto> Groups => _allGroups.ToDictionary(k => k.Key, k => k.Value);
    public Pair? LastAddedUser { get; internal set; }
    public Dictionary<Pair, List<GroupFullInfoDto>> PairsWithGroups => _pairsWithGroupsInternal.Value;

    public void AddGroup(GroupFullInfoDto dto)
    {
        _allGroups[dto.Group] = dto;
        RecreateLazy();
    }

    public void AddGroupPair(GroupPairFullInfoDto dto)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
            _allClientPairs[dto.User] = _pairFactory.Create(new UserFullPairDto(dto.User, API.Data.Enum.IndividualPairStatus.None,
                [dto.Group.GID], dto.SelfToOtherPermissions, dto.OtherToSelfPermissions));
        else _allClientPairs[dto.User].UserPair.Groups.Add(dto.GID);
        RecreateLazy();
    }

    public Pair? GetPairByUID(string uid)
    {
        var existingPair = _allClientPairs.FirstOrDefault(f => f.Key.UID == uid);
        if (!Equals(existingPair, default(KeyValuePair<UserData, Pair>)))
        {
            return existingPair.Value;
        }

        return null;
    }

    public void AddUserPair(UserFullPairDto dto)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
        {
            _allClientPairs[dto.User] = _pairFactory.Create(dto);
        }
        else
        {
            _allClientPairs[dto.User].UserPair.IndividualPairStatus = dto.IndividualPairStatus;
            _allClientPairs[dto.User].ApplyLastReceivedData();
        }

        RecreateLazy();
    }

    public void AddUserPair(UserPairDto dto, bool addToLastAddedUser = true)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
        {
            _allClientPairs[dto.User] = _pairFactory.Create(dto);
        }
        else
        {
            addToLastAddedUser = false;
        }

        _allClientPairs[dto.User].UserPair.IndividualPairStatus = dto.IndividualPairStatus;
        _allClientPairs[dto.User].UserPair.OwnPermissions = dto.OwnPermissions;
        _allClientPairs[dto.User].UserPair.OtherPermissions = dto.OtherPermissions;
        if (addToLastAddedUser)
            LastAddedUser = _allClientPairs[dto.User];
        _allClientPairs[dto.User].ApplyLastReceivedData();
        RecreateLazy();
    }

    public void ClearPairs()
    {
        Logger.LogDebug("Clearing all Pairs");
        DisposePairs();
        _allClientPairs.Clear();
        _allGroups.Clear();
        RecreateLazy();
    }

    public List<Pair> GetOnlineUserPairs() => _allClientPairs.Where(p => !string.IsNullOrEmpty(p.Value.GetPlayerNameHash())).Select(p => p.Value).ToList();

    public int GetVisibleUserCount() => _allClientPairs.Count(p => p.Value.IsMutuallyVisible);

    public List<UserData> GetVisibleUsers() => [.. _allClientPairs.Where(p => p.Value.IsMutuallyVisible).Select(p => p.Key)];

    public void MarkPairOffline(UserData user)
    {
        if (_allClientPairs.TryGetValue(user, out var pair))
        {
            Mediator.Publish(new ClearProfileDataMessage(pair.UserData));
            pair.MarkOffline();
        }

        RecreateLazy();
    }

    public void MarkPairOnline(OnlineUserIdentDto dto, bool sendNotif = true)
    {
        if (!_allClientPairs.ContainsKey(dto.User)) throw new InvalidOperationException("No user found for " + dto);

        Mediator.Publish(new ClearProfileDataMessage(dto.User));

        var pair = _allClientPairs[dto.User];
        if (pair.HasCachedPlayer)
        {
            RecreateLazy();
            return;
        }

        if (sendNotif && _configurationService.Current.ShowOnlineNotifications
            && (_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs && pair.IsDirectlyPaired && !pair.IsOneSidedPair
            || !_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs)
            && (_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs && !string.IsNullOrEmpty(pair.GetNote())
            || !_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs))
        {
            string? note = pair.GetNote();
            var msg = !string.IsNullOrEmpty(note)
                ? $"{note} ({pair.UserData.AliasOrUID}) is now online"
                : $"{pair.UserData.AliasOrUID} is now online";
            Mediator.Publish(new NotificationMessage("User online", msg, SpheneConfiguration.Models.NotificationType.Info, TimeSpan.FromSeconds(5)));
        }

        pair.CreateCachedPlayer(dto);

        RecreateLazy();
    }

    public void ReceiveCharaData(OnlineUserCharaDataDto dto)
    {
        Logger.LogDebug("ReceiveCharaData called - User: {user}, Hash: {hash}, RequiresAck: {requiresAck}", 
            dto.User.AliasOrUID, dto.DataHash[..Math.Min(8, dto.DataHash.Length)], dto.RequiresAcknowledgment);
        
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            Logger.LogWarning("Received character data for user {User} who is not in paired users list. This can happen during connection setup.", dto.User.AliasOrUID);
            return;
        }

        Mediator.Publish(new EventMessage(new Event(pair.UserData, nameof(PairManager), EventSeverity.Informational, "Received Character Data")));
        Logger.LogDebug("Calling ApplyData for user {user} with Hash {hash}", dto.User.AliasOrUID, dto.DataHash[..Math.Min(8, dto.DataHash.Length)]);
        pair.ApplyData(dto);
    }

    // Track processed acknowledgments to prevent duplicates
    private readonly ConcurrentDictionary<string, DateTime> _processedAcknowledgments = new();
    private readonly TimeSpan _acknowledgmentCacheTimeout = TimeSpan.FromMinutes(5);

    public void ReceiveCharacterDataAcknowledgment(CharacterDataAcknowledgmentDto acknowledgmentDto)
    {
        
        // Create unique key for deduplication (hash + user + precise timestamp)
        var preciseTimestamp = acknowledgmentDto.AcknowledgedAt.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var deduplicationKey = $"{acknowledgmentDto.DataHash}_{acknowledgmentDto.User.UID}_{preciseTimestamp}";
        
        // Check for duplicate acknowledgment (exact same hash, user, and millisecond timestamp)
        if (_processedAcknowledgments.ContainsKey(deduplicationKey))
        {
            return;
        }
        
        // Also check for any acknowledgment from the same user within the last 100ms to handle rapid changes
        var recentCutoff = acknowledgmentDto.AcknowledgedAt.AddMilliseconds(-100);
        var hasRecentAcknowledgment = _processedAcknowledgments.Keys.Any(key => 
        {
            var parts = key.Split('_');
            if (parts.Length >= 3 && parts[1] == acknowledgmentDto.User.UID)
            {
                var keyTimestampStr = string.Join("_", parts.Skip(2));
                if (DateTime.TryParseExact(keyTimestampStr, "yyyy-MM-dd HH:mm:ss.fff", null, DateTimeStyles.None, out var keyTimestamp))
                {
                    return keyTimestamp > recentCutoff && keyTimestamp < acknowledgmentDto.AcknowledgedAt;
                }
            }
            return false;
        });
        
        if (hasRecentAcknowledgment)
        {
            Logger.LogDebug("Recent acknowledgment from same user detected - allowing new hash: {hash}, User: {user}", 
                acknowledgmentDto.DataHash[..Math.Min(8, acknowledgmentDto.DataHash.Length)], 
                acknowledgmentDto.User.AliasOrUID);
        }
        
        // Add to processed acknowledgments cache
        _processedAcknowledgments[deduplicationKey] = DateTime.UtcNow;
        
        // Clean up old entries periodically (every 100 acknowledgments)
        if (_processedAcknowledgments.Count % 100 == 0)
        {
            CleanupOldAcknowledgments();
        }
        
        // Check if the acknowledging user is the sender themselves (self-acknowledgment)
        var currentUserUID = _apiController.Value.UID;
        
        if (!string.IsNullOrEmpty(currentUserUID) && string.Equals(acknowledgmentDto.User.UID, currentUserUID, StringComparison.Ordinal))
        {
            Logger.LogInformation("Ignoring acknowledgment from sender themselves: {user}", acknowledgmentDto.User.AliasOrUID);
            return;
        }
        
        // Use hash for acknowledgment lookup
        var sessionProcessed = _sessionAcknowledgmentManager.ProcessReceivedAcknowledgment(acknowledgmentDto.DataHash, acknowledgmentDto.User);
        

        
        // Check if this is a sender acknowledgment (we sent data and are receiving confirmation)
        if (_senderPendingAcknowledgments.TryGetValue(acknowledgmentDto.DataHash, out var pendingRecipients))
        {
            Logger.LogDebug("Found matching pending acknowledgment for Hash: {hash}", 
                acknowledgmentDto.DataHash[..Math.Min(8, acknowledgmentDto.DataHash.Length)]);
            
            // Check if any pending recipient has matching UID
            var matchingRecipient = pendingRecipients.FirstOrDefault(u => string.Equals(u.UID, acknowledgmentDto.User.UID, StringComparison.Ordinal));
            
            // Remove the acknowledging user from pending list
            var removed = pendingRecipients.Remove(acknowledgmentDto.User);
            
            // If direct removal failed but we found a matching UID, try manual removal
            if (!removed && matchingRecipient != null)
            {
                removed = pendingRecipients.Remove(matchingRecipient);
            }
            
            // Update the acknowledgment status for the specific user pair
            if (_allClientPairs.TryGetValue(acknowledgmentDto.User, out var pair))
            {
                pair.UpdateAcknowledgmentStatus(acknowledgmentDto.DataHash, acknowledgmentDto.Success, DateTimeOffset.Now);
                
                // Cancel timeout tracking since acknowledgment was received
                _acknowledgmentTimeoutManager.CancelTimeout(acknowledgmentDto.DataHash);
                
                // Cancel invalid hash timeout for this user since acknowledgment was received
                _acknowledgmentTimeoutManager.CancelInvalidHashTimeout(acknowledgmentDto.User.UID);
            }
            else
            {
                Logger.LogWarning("Could not find pair for acknowledging user: {user}", acknowledgmentDto.User.AliasOrUID);
            }
            
            // If no more pending recipients, remove the acknowledgment entirely
            if (pendingRecipients.Count == 0)
            {
                _senderPendingAcknowledgments.TryRemove(acknowledgmentDto.DataHash, out _);
            }
            
            Mediator.Publish(new EventMessage(new Event(acknowledgmentDto.User, nameof(PairManager), EventSeverity.Informational, 
                acknowledgmentDto.Success ? "Character Data Acknowledged" : "Character Data Acknowledgment Failed")));
            Mediator.Publish(new RefreshUiMessage());
        }
        else
        {
            Logger.LogWarning("Could not find sender pending acknowledgment - Hash: {hash}, acknowledging user: {user}", 
                acknowledgmentDto.DataHash[..Math.Min(8, acknowledgmentDto.DataHash.Length)], acknowledgmentDto.User.AliasOrUID);
            Logger.LogWarning("Available pending acknowledgment IDs: [{ids}]", 
                string.Join(", ", _senderPendingAcknowledgments.Keys));
        }
    }

    public void RemoveGroup(GroupData data)
    {
        _allGroups.TryRemove(data, out _);

        foreach (var item in _allClientPairs.ToList())
        {
            item.Value.UserPair.Groups.Remove(data.GID);

            if (!item.Value.HasAnyConnection())
            {
                item.Value.MarkOffline();
                _allClientPairs.TryRemove(item.Key, out _);
            }
        }

        RecreateLazy();
    }

    public void RemoveGroupPair(GroupPairDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            pair.UserPair.Groups.Remove(dto.Group.GID);

            if (!pair.HasAnyConnection())
            {
                pair.MarkOffline();
                _allClientPairs.TryRemove(dto.User, out _);
            }
        }

        RecreateLazy();
    }

    public void UpdateMutualVisibility(MutualVisibilityDto dto)
    {
        try
        {
            var currentUid = _apiController.Value.UID;
            if (string.IsNullOrEmpty(currentUid)) return;

            UserData? other = null;
            if (string.Equals(dto.UserA.UID, currentUid, StringComparison.Ordinal))
                other = dto.UserB;
            else if (string.Equals(dto.UserB.UID, currentUid, StringComparison.Ordinal))
                other = dto.UserA;
            else
                return;

            if (other != null && _allClientPairs.TryGetValue(other, out var pair))
            {
                if (_localVisibilityGateActive)
                {
                    // Gate has precedence: never allow mutual=true while gate is active
                    if (dto.IsMutuallyVisible)
                    {
                        Logger.LogDebug("Ignoring mutual=true for {user} due to local gate active", other.AliasOrUID);
                    }
                    pair.SetMutualVisibility(false);
                }
                else
                {
                    pair.SetMutualVisibility(dto.IsMutuallyVisible);
                    Logger.LogDebug("Mutual visibility updated for {user}: {state}", other.AliasOrUID, dto.IsMutuallyVisible);
                }
                Mediator.Publish(new RefreshUiMessage());
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to update mutual visibility");
        }
    }

    public void RemoveUserPair(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            pair.UserPair.IndividualPairStatus = API.Data.Enum.IndividualPairStatus.None;

            if (!pair.HasAnyConnection())
            {
                pair.MarkOffline();
                _allClientPairs.TryRemove(dto.User, out _);
            }
        }

        RecreateLazy();
    }

    public void SetGroupInfo(GroupInfoDto dto)
    {
        _allGroups[dto.Group].Group = dto.Group;
        _allGroups[dto.Group].Owner = dto.Owner;
        _allGroups[dto.Group].GroupPermissions = dto.GroupPermissions;

        RecreateLazy();
    }

    public void UpdatePairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            throw new InvalidOperationException("No such pair for " + dto);
        }

        if (pair.UserPair == null) throw new InvalidOperationException("No direct pair for " + dto);

        if (pair.UserPair.OtherPermissions.IsPaused() != dto.Permissions.IsPaused())
        {
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        }

        pair.UserPair.OtherPermissions = dto.Permissions;

        Logger.LogTrace("Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}",
            pair.UserPair.OtherPermissions.IsPaused(),
            pair.UserPair.OtherPermissions.IsDisableAnimations(),
            pair.UserPair.OtherPermissions.IsDisableSounds(),
            pair.UserPair.OtherPermissions.IsDisableVFX());

        if (!pair.IsPaused)
            pair.ApplyLastReceivedData();

        RecreateLazy();
    }

    public void UpdateSelfPairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            throw new InvalidOperationException("No such pair for " + dto);
        }

        if (pair.UserPair.OwnPermissions.IsPaused() != dto.Permissions.IsPaused())
        {
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        }

        pair.UserPair.OwnPermissions = dto.Permissions;

        Logger.LogTrace("Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}",
            pair.UserPair.OwnPermissions.IsPaused(),
            pair.UserPair.OwnPermissions.IsDisableAnimations(),
            pair.UserPair.OwnPermissions.IsDisableSounds(),
            pair.UserPair.OwnPermissions.IsDisableVFX());

        if (!pair.IsPaused)
            pair.ApplyLastReceivedData();

        RecreateLazy();
    }

    internal void ReceiveUploadStatus(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var existingPair) && existingPair.IsVisible)
        {
            existingPair.SetIsUploading();
        }
    }

    internal void SetGroupPairStatusInfo(GroupPairUserInfoDto dto)
    {
        _allGroups[dto.Group].GroupPairUserInfos[dto.UID] = dto.GroupUserInfo;
        RecreateLazy();
    }

    internal void SetGroupPermissions(GroupPermissionDto dto)
    {
        _allGroups[dto.Group].GroupPermissions = dto.Permissions;
        RecreateLazy();
    }

    internal void SetGroupStatusInfo(GroupPairUserInfoDto dto)
    {
        _allGroups[dto.Group].GroupUserInfo = dto.GroupUserInfo;
        RecreateLazy();
    }

    internal void UpdateGroupPairPermissions(GroupPairUserPermissionDto dto)
    {
        _allGroups[dto.Group].GroupUserPermissions = dto.GroupPairPermissions;
        RecreateLazy();
    }

    internal void UpdateIndividualPairStatus(UserIndividualPairStatusDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            pair.UserPair.IndividualPairStatus = dto.IndividualPairStatus;
            RecreateLazy();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _dalamudContextMenu.OnMenuOpened -= DalamudContextMenuOnOnOpenGameObjectContextMenu;

        DisposePairs();
    }

    private void DalamudContextMenuOnOnOpenGameObjectContextMenu(Dalamud.Game.Gui.ContextMenu.IMenuOpenedArgs args)
    {
        if (args.MenuType == Dalamud.Game.Gui.ContextMenu.ContextMenuType.Inventory) return;
        if (!_configurationService.Current.EnableRightClickMenus) return;

        foreach (var pair in _allClientPairs.Where((p => p.Value.IsMutuallyVisible)))
        {
            pair.Value.AddContextMenu(args);
        }
    }

    private Lazy<List<Pair>> DirectPairsLazy() => new(() => _allClientPairs.Select(k => k.Value)
        .Where(k => k.IndividualPairStatus != API.Data.Enum.IndividualPairStatus.None).ToList());

    private void DisposePairs()
    {
        Logger.LogDebug("Disposing all Pairs");
        Parallel.ForEach(_allClientPairs, item =>
        {
            item.Value.MarkOffline(wait: false);
        });

        RecreateLazy();
    }

    private Lazy<Dictionary<GroupFullInfoDto, List<Pair>>> GroupPairsLazy()
    {
        return new Lazy<Dictionary<GroupFullInfoDto, List<Pair>>>(() =>
        {
            Dictionary<GroupFullInfoDto, List<Pair>> outDict = [];
            foreach (var group in _allGroups)
            {
                // Get pairs that are official members of this group
                var memberPairs = _allClientPairs.Select(p => p.Value).Where(p => p.UserPair.Groups.Exists(g => GroupDataComparer.Instance.Equals(group.Key, new(g)))).ToList();
                
                // Also include visible users who are members of this group but not currently in memberPairs
                // This handles cases where visible users are syncshell members but not showing up
                var visibleMemberPairs = _allClientPairs.Select(p => p.Value)
                    .Where(p => p.IsMutuallyVisible && 
                               !memberPairs.Contains(p) && 
                               group.Value.GroupPairUserInfos.ContainsKey(p.UserData.UID))
                    .ToList();
                memberPairs.AddRange(visibleMemberPairs);
                
                // For area-bound syncshells with no permanent members, also include all visible users
                if (group.Value.GroupPairUserInfos.Count == 0 && _areaBoundSyncshellService.Value.IsAreaBoundSyncshell(group.Value.Group.GID))
                {
                    var areaBoundVisiblePairs = _allClientPairs.Select(p => p.Value).Where(p => p.IsMutuallyVisible && !memberPairs.Contains(p)).ToList();
                    memberPairs.AddRange(areaBoundVisiblePairs);
                }
                
                outDict[group.Value] = memberPairs;
            }
            return outDict;
        });
    }

    private Lazy<Dictionary<Pair, List<GroupFullInfoDto>>> PairsWithGroupsLazy()
    {
        return new Lazy<Dictionary<Pair, List<GroupFullInfoDto>>>(() =>
        {
            Dictionary<Pair, List<GroupFullInfoDto>> outDict = [];

            foreach (var pair in _allClientPairs.Select(k => k.Value))
            {
                // Get groups this pair is officially a member of
                var memberGroups = _allGroups.Where(k => pair.UserPair.Groups.Contains(k.Key.GID, StringComparer.Ordinal)).Select(k => k.Value).ToList();
                
                // For visible users, also check if they should be included in area-bound syncshells
                if (pair.IsMutuallyVisible)
                {
                    foreach (var group in _allGroups.Values)
                    {
                        // Skip if already a member
                        if (memberGroups.Contains(group)) continue;
                        
                        // Check if this is an area-bound syncshell with no permanent members
                        if (group.GroupPairUserInfos.Count == 0 && _areaBoundSyncshellService.Value.IsAreaBoundSyncshell(group.Group.GID))
                        {
                            memberGroups.Add(group);
                        }
                    }
                }
                
                outDict[pair] = memberGroups;
            }

            return outDict;
        });
    }

    private void ReapplyPairData()
    {
        foreach (var pair in _allClientPairs.Select(k => k.Value))
        {
            pair.ApplyLastReceivedData(forced: true);
        }
    }

    /// <summary>
    /// Apply local gate precedence: ensure mutual visibility is false server-side
    /// and proactively report proximity=false for all pairs during gate states.
    /// </summary>
    private void ApplyLocalVisibilityGate(bool hidden, string source)
    {
        try
        {
            if (!hidden) return;

            Logger.LogDebug("Applying local visibility gate from {source}", source);
            _localVisibilityGateActive = true;
            _visibilityGateService.Activate();
            foreach (var pair in _allClientPairs.Values)
            {
                // Force mutual visibility to false locally and inform the server
                pair.SetMutualVisibility(false);
                pair.ReportVisibility(false);
            }
            // Trigger immediate visibility reevaluation on handlers
            Mediator.Publish(new FrameworkUpdateMessage());
            Mediator.Publish(new RefreshUiMessage());
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to apply local visibility gate from {source}", source);
        }
    }

    private void ClearLocalVisibilityGate(string source)
    {
        try
        {
            if (!_localVisibilityGateActive) return;
            _localVisibilityGateActive = false;
            _visibilityGateService.Deactivate();
            Logger.LogDebug("Cleared local visibility gate from {source}", source);
            Mediator.Publish(new RefreshUiMessage());
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to clear local visibility gate from {source}", source);
        }
    }

    public void SetPendingAcknowledgmentForUsers(List<UserData> users, string acknowledgmentId)
    {
        foreach (var userData in users)
        {
            if (_allClientPairs.TryGetValue(userData, out var pair))
            {
                pair.SetPendingAcknowledgment(acknowledgmentId);
                Logger.LogDebug("Set pending acknowledgment for user {user} with ID {id}", userData.AliasOrUID, acknowledgmentId);
                
                // Start timeout tracking for this acknowledgment
                var currentHash = pair.GetCurrentDataHash();
                if (!string.IsNullOrEmpty(currentHash))
                {
                    _acknowledgmentTimeoutManager.StartTimeout(acknowledgmentId, userData, currentHash);
                }
            }
        }
        
        // Add notification for multiple users
        if (users.Count > 1)
        {
            _messageService.AddTaggedMessage(
                $"ack_users_{acknowledgmentId}",
                $"Waiting for acknowledgment from {users.Count} users",
                SpheneConfiguration.Models.NotificationType.Info,
                "Acknowledgment Pending",
                TimeSpan.FromSeconds(3)
            );
        }
        else if (users.Count == 1)
        {
            _messageService.AddTaggedMessage(
                $"ack_user_{acknowledgmentId}_{users[0].UID}",
                $"Waiting for acknowledgment from {users[0].AliasOrUID}",
                SpheneConfiguration.Models.NotificationType.Info,
                "Acknowledgment Pending",
                TimeSpan.FromSeconds(3)
            );
        }
        
        Mediator.Publish(new RefreshUiMessage());
    }
    
    private void CleanupOldAcknowledgments()
    {
        var cutoffTime = DateTime.UtcNow - _acknowledgmentCacheTimeout;
        var keysToRemove = _processedAcknowledgments
            .Where(kvp => kvp.Value < cutoffTime)
            .Select(kvp => kvp.Key)
            .ToList();
            
        foreach (var key in keysToRemove)
        {
            _processedAcknowledgments.TryRemove(key, out _);
        }
        
        if (keysToRemove.Count > 0)
        {
            Logger.LogDebug("Cleaned up {count} old acknowledgment entries", keysToRemove.Count);
        }
    }

    public void SetPendingAcknowledgmentForSender(List<UserData> recipients, string acknowledgmentId)
    {
        // Use hash-based acknowledgment manager for thread-safe handling
        _sessionAcknowledgmentManager.SetPendingAcknowledgmentForHashVersion(recipients, acknowledgmentId);
        
        // Keep legacy tracking for backward compatibility during transition
        _senderPendingAcknowledgments[acknowledgmentId] = new HashSet<UserData>(recipients, UserDataComparer.Instance);
        
        // Also set pending acknowledgment on individual pairs for UI display
        foreach (var recipient in recipients)
        {
            if (_allClientPairs.TryGetValue(recipient, out var pair))
            {
                pair.SetPendingAcknowledgment(acknowledgmentId);
                Logger.LogDebug("Set pending acknowledgment on pair for recipient {user} with ID {id}", recipient.AliasOrUID, acknowledgmentId);
                
                // Start timeout tracking for this acknowledgment
                var currentHash = pair.GetCurrentDataHash();
                if (!string.IsNullOrEmpty(currentHash))
                {
                    _acknowledgmentTimeoutManager.StartTimeout(acknowledgmentId, recipient, currentHash);
                }
            }
        }
            Logger.LogDebug("Total pending acknowledgments after adding: {count}", _senderPendingAcknowledgments.Count);
    }

    public bool HasPendingAcknowledgmentForUser(UserData userData)
    {
        // Check if the sender is waiting for acknowledgment from this specific user
        var hasSenderPending = _senderPendingAcknowledgments.Values.Any(recipients => recipients.Contains(userData));
        
        // Also check if the individual pair has a pending acknowledgment
        var hasIndividualPending = false;
        if (_allClientPairs.TryGetValue(userData, out var pair))
        {
            hasIndividualPending = pair.HasPendingAcknowledgment;
        }
        
        // Removed frequent debug log to reduce noise
        
        return hasSenderPending || hasIndividualPending;
    }

    public bool HasAnySenderPendingAcknowledgments()
    {
        // Check if the sender has any pending acknowledgments
        return _senderPendingAcknowledgments.Any();
    }

    public void SetPendingAcknowledgmentForBuildStart()
    {
        // Get all visible and online pairs
        var visiblePairs = _allClientPairs.Values
            .Where(p => p.IsVisible && p.IsOnline)
            .ToList();
        
        if (visiblePairs.Any())
        {
            // Set UI pending status without creating a trackable acknowledgment
            // This will show the yellow clock until real data is sent
            foreach (var pair in visiblePairs)
            {
                pair.SetBuildStartPendingStatus();

                // Immediately set our own AckYou to false so both sides reflect pending state at build start
                var permissions = pair.UserPair.OwnPermissions;
                if (permissions.IsAckYou())
                {
                    Logger.LogDebug("BuildStart: Setting Own AckYou=false for user {user}", pair.UserData.AliasOrUID);
                    permissions.SetAckYou(false);
                    pair.UserPair.OwnPermissions = permissions;

                    try
                    {
                        _ = _apiController.Value.UserSetPairPermissions(new(pair.UserData, permissions));
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "BuildStart: Failed to send Own AckYou=false for user {user}", pair.UserData.AliasOrUID);
                    }
                }
            }
            
            // Add notification for build start
            _messageService.AddTaggedMessage(
                "build_start_pending",
                "Character data build started - waiting for acknowledgments",
                SpheneConfiguration.Models.NotificationType.Info,
                "Build Started",
                TimeSpan.FromSeconds(3)
            );
        }
    }

    public Pair? GetPairForUser(UserData userData)
    {
        _allClientPairs.TryGetValue(userData, out var pair);
        return pair;
    }

    private void RecreateLazy()
    {
        _directPairsInternal = DirectPairsLazy();
        _groupPairsInternal = GroupPairsLazy();
        _pairsWithGroupsInternal = PairsWithGroupsLazy();
        Mediator.Publish(new StructuralRefreshUiMessage());
    }
}
