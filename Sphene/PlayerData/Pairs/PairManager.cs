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
    private readonly SessionAcknowledgmentManager _sessionAcknowledgmentManager;
    private readonly SpheneConfigService _configurationService;
    private readonly IContextMenu _dalamudContextMenu;
    private readonly PairFactory _pairFactory;
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
                IContextMenu dalamudContextMenu,
                Lazy<ApiController> apiController, SessionAcknowledgmentManager sessionAcknowledgmentManager,
                MessageService messageService, AcknowledgmentTimeoutManager acknowledgmentTimeoutManager,
                Lazy<AreaBoundSyncshellService> areaBoundSyncshellService,
                VisibilityGateService visibilityGateService) : base(logger, mediator)
    {
        _pairFactory = pairFactory;
        _configurationService = configurationService;
        _dalamudContextMenu = dalamudContextMenu;
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
        Mediator.Subscribe<GposeStartMessage>(this, (msg) => { _ = _apiController.Value.UserUpdateGposeState(true); });
        Mediator.Subscribe<GposeEndMessage>(this, (msg) => { _ = _apiController.Value.UserUpdateGposeState(false); });
        Mediator.Subscribe<PenumbraModTransferCompletedMessage>(this, OnPenumbraModTransferCompleted);
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
        var existingPair = _allClientPairs.FirstOrDefault(f => string.Equals(f.Key.UID, uid, StringComparison.Ordinal));
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


    public void ReceiveCharacterDataAcknowledgment(CharacterDataAcknowledgmentDto acknowledgmentDto)
    {

        var currentUserUID = _apiController.Value.UID;
        if (!string.IsNullOrEmpty(currentUserUID) && string.Equals(acknowledgmentDto.User.UID, currentUserUID, StringComparison.Ordinal))
        {
            Logger.LogDebug("Ignoring acknowledgment from sender themselves: {user}", acknowledgmentDto.User.AliasOrUID);
            return;
        }

        // Process via session acknowledgment manager (single source of truth)
        var processedBySession = _sessionAcknowledgmentManager.ProcessReceivedAcknowledgment(acknowledgmentDto.DataHash, acknowledgmentDto.User);
        if (!processedBySession)
        {
            Logger.LogDebug("Ignoring non-latest acknowledgment for user {user}", acknowledgmentDto.User.AliasOrUID);
            return;
        }

        // Cancel timeout tracking since acknowledgment was received
        _acknowledgmentTimeoutManager.CancelTimeout(acknowledgmentDto.DataHash);
        _acknowledgmentTimeoutManager.CancelInvalidHashTimeout(acknowledgmentDto.User.UID);

        Mediator.Publish(new EventMessage(new Event(acknowledgmentDto.User, nameof(PairManager), EventSeverity.Informational,
            acknowledgmentDto.Success ? "Character Data Acknowledged" : "Character Data Acknowledgment Failed")));
        Mediator.Publish(new RefreshUiMessage());
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

    public void UpdateGposeState(UserGposeStateDto dto)
    {
        try
        {
            if (dto == null) return;

            if (!_allClientPairs.TryGetValue(dto.User, out var pair))
            {
                pair = GetPairByUID(dto.User.UID);
            }

            if (pair == null) return;

            pair.SetGposeState(dto.IsInGpose);
            Mediator.Publish(new RefreshUiMessage());
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to update GPose state");
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

        var currentOtherAckYou = pair.UserPair.OtherPermissions.IsAckYou();
        if (currentOtherAckYou && pair.HasPendingAcknowledgment)
        {
            _sessionAcknowledgmentManager.ResolvePendingAcknowledgmentFromRemoteAckYou(dto.User, pair.LastAcknowledgmentId);
            pair.ResolvePendingAcknowledgmentFromRemoteAckYou();
        }

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

    public void UpdatePenumbraReceivePreference(UserPenumbraReceivePreferenceDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            return;
        }

        if (pair.UserPair.OtherAllowsReceivingPenumbraMods == dto.AllowReceivingPenumbraMods)
        {
            return;
        }

        pair.UserPair.OtherAllowsReceivingPenumbraMods = dto.AllowReceivingPenumbraMods;
        Mediator.Publish(new StructuralRefreshUiMessage());
    }

    internal void ReceiveUploadStatus(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var existingPair) && existingPair.IsVisible)
        {
            existingPair.SetIsUploading();
        }
    }

    private void OnPenumbraModTransferCompleted(PenumbraModTransferCompletedMessage message)
    {
        var senderUid = message.Notification.Sender?.UID ?? string.Empty;
        if (string.IsNullOrWhiteSpace(senderUid))
        {
            return;
        }

        var pair = GetPairByUID(senderUid);
        if (pair == null)
        {
            return;
        }

        pair.SetIsUploading(isUploading: false);
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
        foreach (var item in _allClientPairs)
        {
            item.Value.MarkOffline(wait: true);
        }

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
            pair.ApplyLastReceivedData(forced: false);
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
                _ = pair.SetPendingAcknowledgment(acknowledgmentId);
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
    

    public void SetPendingAcknowledgmentForSender(List<UserData> recipients, string acknowledgmentId)
    {
        // Use hash-based acknowledgment manager for thread-safe handling
        _sessionAcknowledgmentManager.SetPendingAcknowledgmentForHashVersion(recipients, acknowledgmentId);

        // Also set pending acknowledgment on individual pairs for UI display
        foreach (var recipient in recipients)
        {
            if (_allClientPairs.TryGetValue(recipient, out var pair))
            {
                _ = pair.SetPendingAcknowledgment(acknowledgmentId);
                Logger.LogDebug("Set pending acknowledgment on pair for recipient {user} with ID {id}", recipient.AliasOrUID, acknowledgmentId);
                
                // Start timeout tracking for this acknowledgment
                var currentHash = pair.GetCurrentDataHash();
                if (!string.IsNullOrEmpty(currentHash))
                {
                    _acknowledgmentTimeoutManager.StartTimeout(acknowledgmentId, recipient, currentHash);
                }
            }
        }
        Mediator.Publish(new RefreshUiMessage());
    }

    public bool HasPendingAcknowledgmentForUser(UserData userData)
    {
        var hasIndividualPending = false;
        if (_allClientPairs.TryGetValue(userData, out var pair))
        {
            hasIndividualPending = pair.HasPendingAcknowledgment;
        }
        return hasIndividualPending;
    }

    public bool HasAnySenderPendingAcknowledgments()
    {
        // Check if there are any pending acknowledgments tracked by session manager or pairs
        var anyPairPending = _allClientPairs.Values.Any(p => p.HasPendingAcknowledgment);
        return anyPairPending || _sessionAcknowledgmentManager.HasPendingAcknowledgments();
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
