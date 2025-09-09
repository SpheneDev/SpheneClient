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
using Sphene.API.Data.Comparer;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Dalamud.Interface.ImGuiNotification;

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

    private Lazy<List<Pair>> _directPairsInternal;
    private Lazy<Dictionary<GroupFullInfoDto, List<Pair>>> _groupPairsInternal;
    private Lazy<Dictionary<Pair, List<GroupFullInfoDto>>> _pairsWithGroupsInternal;

    public PairManager(ILogger<PairManager> logger, PairFactory pairFactory,
                SpheneConfigService configurationService, SpheneMediator mediator,
                IContextMenu dalamudContextMenu, ServerConfigurationManager serverConfigurationManager,
                Lazy<ApiController> apiController, SessionAcknowledgmentManager sessionAcknowledgmentManager,
                MessageService messageService) : base(logger, mediator)
    {
        _pairFactory = pairFactory;
        _configurationService = configurationService;
        _dalamudContextMenu = dalamudContextMenu;
        _serverConfigurationManager = serverConfigurationManager;
        _apiController = apiController;
        _sessionAcknowledgmentManager = sessionAcknowledgmentManager;
        _messageService = messageService;

        Mediator.Subscribe<DisconnectedMessage>(this, (_) => ClearPairs());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => ReapplyPairData());
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

    public int GetVisibleUserCount() => _allClientPairs.Count(p => p.Value.IsVisible);

    public List<UserData> GetVisibleUsers() => [.. _allClientPairs.Where(p => p.Value.IsVisible).Select(p => p.Key)];

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
        Logger.LogInformation("ReceiveCharaData called - User: {user}, AckId: {acknowledgmentId}, RequiresAck: {requiresAck}", 
            dto.User.AliasOrUID, dto.AcknowledgmentId, dto.RequiresAcknowledgment);
        
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            Logger.LogWarning("Received character data for user {User} who is not in paired users list. This can happen during connection setup.", dto.User.AliasOrUID);
            return;
        }

        Mediator.Publish(new EventMessage(new Event(pair.UserData, nameof(PairManager), EventSeverity.Informational, "Received Character Data")));
        Logger.LogInformation("Calling ApplyData for user {user} with AckId {acknowledgmentId}", dto.User.AliasOrUID, dto.AcknowledgmentId);
        pair.ApplyData(dto);
    }

    public void ReceiveCharacterDataAcknowledgment(CharacterDataAcknowledgmentDto acknowledgmentDto)
    {
        Logger.LogInformation("ReceiveCharacterDataAcknowledgment called - AckId: {acknowledgmentId}, User: {user}, Success: {success}", 
            acknowledgmentDto.AcknowledgmentId, acknowledgmentDto.User.AliasOrUID, acknowledgmentDto.Success);
        
        // Check if the acknowledging user is the sender themselves (self-acknowledgment)
        var currentUserUID = _apiController.Value.UID;
        Logger.LogInformation("Self-acknowledgment check - Current user UID: {CurrentUID}, Acknowledging user UID: {AckUID}", 
            currentUserUID, acknowledgmentDto.User.UID);
        
        if (!string.IsNullOrEmpty(currentUserUID) && string.Equals(acknowledgmentDto.User.UID, currentUserUID, StringComparison.Ordinal))
        {
            Logger.LogInformation("Ignoring acknowledgment from sender themselves: {user}", acknowledgmentDto.User.AliasOrUID);
            return;
        }
        
        // Try session-aware acknowledgment processing first
        var sessionProcessed = _sessionAcknowledgmentManager.ProcessReceivedAcknowledgment(acknowledgmentDto.AcknowledgmentId, acknowledgmentDto.User);
        
        // Debug: Log all current pending acknowledgments
        Logger.LogInformation("Current pending acknowledgments count: {count}, Session processed: {sessionProcessed}", _senderPendingAcknowledgments.Count, sessionProcessed);
        foreach (var kvp in _senderPendingAcknowledgments)
        {
            Logger.LogInformation("Pending AckId: {ackId}, Recipients: [{recipients}]", 
                kvp.Key, string.Join(", ", kvp.Value.Select(u => u.AliasOrUID)));
        }
        
        // Check if this is a sender acknowledgment (we sent data and are receiving confirmation)
        if (_senderPendingAcknowledgments.TryGetValue(acknowledgmentDto.AcknowledgmentId, out var pendingRecipients))
        {
            Logger.LogInformation("Found matching pending acknowledgment for AckId: {acknowledgmentId}", acknowledgmentDto.AcknowledgmentId);
            
            // Debug: Log the UIDs of all pending recipients
            Logger.LogInformation("Pending recipients UIDs: [{uids}]", string.Join(", ", pendingRecipients.Select(u => u.UID)));
            Logger.LogInformation("Acknowledging user UID: {ackUID}", acknowledgmentDto.User.UID);
            
            // Debug: Check if any pending recipient has matching UID
            var matchingRecipient = pendingRecipients.FirstOrDefault(u => string.Equals(u.UID, acknowledgmentDto.User.UID, StringComparison.Ordinal));
            Logger.LogInformation("Found matching recipient by UID: {found}, Recipient: {recipient}", 
                matchingRecipient != null, matchingRecipient?.AliasOrUID ?? "null");
            
            // Remove the acknowledging user from pending list
            var removed = pendingRecipients.Remove(acknowledgmentDto.User);
            Logger.LogInformation("Attempted to remove user {user} from pending acknowledgments. Removed: {removed}, Remaining: {count}", 
                acknowledgmentDto.User.AliasOrUID, removed, pendingRecipients.Count);
            
            // If direct removal failed but we found a matching UID, try manual removal
            if (!removed && matchingRecipient != null)
            {
                Logger.LogInformation("Direct removal failed, attempting manual removal of matching recipient");
                var manualRemoved = pendingRecipients.Remove(matchingRecipient);
                Logger.LogInformation("Manual removal result: {manualRemoved}, Remaining: {count}", 
                    manualRemoved, pendingRecipients.Count);
                removed = manualRemoved;
            }
            
            // Update the acknowledgment status for the specific user pair
            if (_allClientPairs.TryGetValue(acknowledgmentDto.User, out var pair))
            {
                pair.UpdateAcknowledgmentStatus(acknowledgmentDto.AcknowledgmentId, acknowledgmentDto.Success, DateTimeOffset.Now);
                Logger.LogInformation("Updated acknowledgment status for user {user} - Success: {success}", 
                    acknowledgmentDto.User.AliasOrUID, acknowledgmentDto.Success);
            }
            else
            {
                Logger.LogWarning("Could not find pair for acknowledging user: {user}", acknowledgmentDto.User.AliasOrUID);
            }
            
            // If no more pending recipients, remove the acknowledgment entirely
            if (pendingRecipients.Count == 0)
            {
                _senderPendingAcknowledgments.TryRemove(acknowledgmentDto.AcknowledgmentId, out _);
                Logger.LogInformation("All acknowledgments received for ID {acknowledgmentId}, removed from pending list", acknowledgmentDto.AcknowledgmentId);
            }
            
            Mediator.Publish(new EventMessage(new Event(acknowledgmentDto.User, nameof(PairManager), EventSeverity.Informational, 
                acknowledgmentDto.Success ? "Character Data Acknowledged" : "Character Data Acknowledgment Failed")));
            Mediator.Publish(new RefreshUiMessage());
            Logger.LogInformation("Published UI refresh message for acknowledgment");
        }
        else
        {
            Logger.LogWarning("Could not find sender pending acknowledgment - AckId: {acknowledgmentId}, acknowledging user: {user}", 
                acknowledgmentDto.AcknowledgmentId, acknowledgmentDto.User.AliasOrUID);
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

        foreach (var pair in _allClientPairs.Where((p => p.Value.IsVisible)))
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
                outDict[group.Value] = _allClientPairs.Select(p => p.Value).Where(p => p.UserPair.Groups.Exists(g => GroupDataComparer.Instance.Equals(group.Key, new(g)))).ToList();
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
                outDict[pair] = _allGroups.Where(k => pair.UserPair.Groups.Contains(k.Key.GID, StringComparer.Ordinal)).Select(k => k.Value).ToList();
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

    public void SetPendingAcknowledgmentForUsers(List<UserData> users, string acknowledgmentId)
    {
        foreach (var userData in users)
        {
            if (_allClientPairs.TryGetValue(userData, out var pair))
            {
                pair.SetPendingAcknowledgment(acknowledgmentId);
                Logger.LogDebug("Set pending acknowledgment for user {user} with ID {id}", userData.AliasOrUID, acknowledgmentId);
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
        // Use session-aware acknowledgment manager for thread-safe handling
        _sessionAcknowledgmentManager.SetPendingAcknowledgmentForSession(recipients, acknowledgmentId);
        
        // Keep legacy tracking for backward compatibility during transition
        _senderPendingAcknowledgments[acknowledgmentId] = new HashSet<UserData>(recipients, UserDataComparer.Instance);
        
        // Also set pending acknowledgment on individual pairs for UI display
        foreach (var recipient in recipients)
        {
            if (_allClientPairs.TryGetValue(recipient, out var pair))
            {
                pair.SetPendingAcknowledgment(acknowledgmentId);
                Logger.LogDebug("Set pending acknowledgment on pair for recipient {user} with ID {id}", recipient.AliasOrUID, acknowledgmentId);
            }
        }
        
        Logger.LogInformation("Set pending acknowledgment for sender with ID {id} waiting for {count} recipients: [{recipients}]", 
            acknowledgmentId, recipients.Count, string.Join(", ", recipients.Select(r => r.AliasOrUID)));
        Logger.LogInformation("Total pending acknowledgments after adding: {count}", _senderPendingAcknowledgments.Count);
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
        
        Logger.LogDebug("HasPendingAcknowledgmentForUser {user} - SenderPending: {senderPending}, IndividualPending: {individualPending}", 
            userData.AliasOrUID, hasSenderPending, hasIndividualPending);
        
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
            }
            
            // Add notification for build start
            _messageService.AddTaggedMessage(
                "build_start_pending",
                $"Character data build started - waiting for {visiblePairs.Count} pairs",
                SpheneConfiguration.Models.NotificationType.Info,
                "Build Started",
                TimeSpan.FromSeconds(2)
            );
            
            Logger.LogInformation("Set build start pending status for {count} visible pairs", visiblePairs.Count);
            Mediator.Publish(new RefreshUiMessage());
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
        Mediator.Publish(new RefreshUiMessage());
    }
}
