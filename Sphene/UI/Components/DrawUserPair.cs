using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data.Extensions;
using Sphene.API.Data.Comparer;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.Files;
using Sphene.API.Dto.User;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Events;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.SpheneConfiguration;
using Sphene.UI.Handlers;
using Sphene.UI.Theme;
using Sphene.WebAPI;
using Sphene.FileCache;
using Sphene.WebAPI.Files;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sphene.UI.Components;

public class DrawUserPair : IMediatorSubscriber, IDisposable
{
    protected readonly ApiController _apiController;
    protected readonly IdDisplayHandler _displayHandler;
    protected readonly SpheneMediator _mediator;
    protected readonly List<GroupFullInfoDto> _syncedGroups;
    private readonly GroupFullInfoDto? _currentGroup;
    protected Pair _pair;
    private readonly string _id;
    private readonly SelectTagForPairUi _selectTagForPairUi;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly UiSharedService _uiSharedService;
    private readonly PlayerPerformanceConfigService _performanceConfigService;
    private readonly CharaDataManager _charaDataManager;
    private readonly PairManager _pairManager;
    private readonly SpheneConfigService _configService;
    private readonly LocalPairEmoteForceSyncService _localPairEmoteForceSyncService;
    private float _menuWidth = -1;
    private bool _wasHovered = false;
    private static readonly Vector4 SoftToggleEnabledColor = new(0.56f, 0.82f, 0.62f, 1f);
    private static readonly Vector4 SoftToggleDisabledColor = new(0.86f, 0.58f, 0.58f, 1f);
    
    // Static dictionary to track reload timers for each user
    private static readonly Dictionary<string, System.Threading.Timer> _reloadTimers = new(StringComparer.Ordinal);
    private static readonly System.Threading.Lock _timerLock = new();
    
    // Global rate limiting and status tracking per user (static to prevent multiple instances from sending)
    private static readonly Dictionary<string, bool?> _globalLastSentAckYouStatus = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> _globalLastAckYouSentTime = new(StringComparer.Ordinal);
    private static readonly System.Threading.Lock _globalAckYouLock = new();
    
    
    private bool _cachedHasPendingAck = false;
    private DateTime _lastAckStatusPoll = DateTime.MinValue;
    private readonly List<FileTransferNotificationDto> _pendingModNotifications = new();
    public DrawUserPair(string id, Pair entry, List<GroupFullInfoDto> syncedGroups,
        GroupFullInfoDto? currentGroup,
        ApiController apiController, IdDisplayHandler uIDDisplayHandler,
        SpheneMediator spheneMediator, SelectTagForPairUi selectTagForPairUi,
        ServerConfigurationManager serverConfigurationManager,
        UiSharedService uiSharedService, PlayerPerformanceConfigService performanceConfigService,
        CharaDataManager charaDataManager, PairManager pairManager, SpheneConfigService configService,
        LocalPairEmoteForceSyncService localPairEmoteForceSyncService)
    {
        _id = id;
        _pair = entry;
        _syncedGroups = syncedGroups;
        _currentGroup = currentGroup;
        _apiController = apiController;
        _displayHandler = uIDDisplayHandler;
        _mediator = spheneMediator;
        _selectTagForPairUi = selectTagForPairUi;
        _serverConfigurationManager = serverConfigurationManager;
        _uiSharedService = uiSharedService;
        _performanceConfigService = performanceConfigService;
        _charaDataManager = charaDataManager;
        _pairManager = pairManager;
        _configService = configService;
        _localPairEmoteForceSyncService = localPairEmoteForceSyncService;
        
        // Subscribe to acknowledgment status changes to automatically update AckYou
        _mediator.Subscribe<PairAcknowledgmentStatusChangedMessage>(this, OnAcknowledgmentStatusChanged);
        _mediator.Subscribe<AcknowledgmentPendingMessage>(this, OnAcknowledgmentPending);
        _mediator.Subscribe<AcknowledgmentUiRefreshMessage>(this, OnAcknowledgmentUiRefresh);
        
        
        
        // Subscribe to selective icon updates for performance optimization
        _mediator.Subscribe<UserPairIconUpdateMessage>(this, OnIconUpdate);
        _mediator.Subscribe<PenumbraModTransferAvailableMessage>(this, OnPenumbraModTransferAvailable);
        _mediator.Subscribe<PenumbraModTransferCompletedMessage>(this, OnPenumbraModTransferCompleted);
        _mediator.Subscribe<PenumbraModTransferDiscardedMessage>(this, OnPenumbraModTransferDiscarded);
        
        // Initialize cache once during construction to avoid frequent PairManager calls
        _cachedHasPendingAck = _pairManager.HasPendingAcknowledgmentForUser(_pair.UserData);
        
        var userUID = _pair.UserData.UID ?? string.Empty;

        // Initialize global status tracking for this user to prevent initial spam
        _globalAckYouLock.Enter();
        try
        {
            if (!_globalLastSentAckYouStatus.ContainsKey(userUID))
            {
                _globalLastSentAckYouStatus[userUID] = _pair.UserPair.OwnPermissions.IsAckYou();
                _globalLastAckYouSentTime[userUID] = DateTime.MinValue;
            }
        }
        finally
        {
            _globalAckYouLock.Exit();
        }
    }
    
    public SpheneMediator Mediator => _mediator;
    
    private void OnAcknowledgmentStatusChanged(PairAcknowledgmentStatusChangedMessage message)
    {
        // Only handle events for this specific pair
        if (!string.Equals(message.User.UID, _pair.UserData.UID, StringComparison.Ordinal)) return;
        
        _cachedHasPendingAck = message.HasPendingAcknowledgment;
    }
    
    
    
    private void OnAcknowledgmentPending(AcknowledgmentPendingMessage message)
    {
        // Only handle events for this specific pair
        if (!string.Equals(message.Event.User.UID, _pair.UserData.UID, StringComparison.Ordinal)) return;
        
        // Update cached acknowledgment status to pending
        _cachedHasPendingAck = true;
        
    }
    
    private void OnAcknowledgmentUiRefresh(AcknowledgmentUiRefreshMessage message)
    {
        // Handle refresh for this specific pair or global refresh
        if (message.RefreshAll || 
            (message.User != null && string.Equals(message.User.UID, _pair.UserData.UID, StringComparison.Ordinal)))
        {
            // Force cache refresh by querying current status
            _cachedHasPendingAck = _pairManager.HasPendingAcknowledgmentForUser(_pair.UserData);
        }
    }
    
    private void OnIconUpdate(UserPairIconUpdateMessage message)
    {
        if (!string.Equals(message.User.UID, _pair.UserData.UID, StringComparison.Ordinal)) return;

        switch (message.UpdateType)
        {
            case IconUpdateType.AcknowledgmentStatus:
                if (message.UpdateData is AcknowledgmentStatusData ackData)
                {
                    _cachedHasPendingAck = ackData.HasPending;
                }
                break;

            case IconUpdateType.ConnectionStatus:
                break;

            case IconUpdateType.PermissionStatus:
                break;

            case IconUpdateType.IndividualPermissions:
                break;

            case IconUpdateType.GroupRole:
                break;

            case IconUpdateType.ReloadTimer:
                break;
        }
    }

    private void OnPenumbraModTransferAvailable(PenumbraModTransferAvailableMessage message)
    {
        var senderUid = message.Notification.Sender?.UID ?? string.Empty;
        var pairUid = _pair.UserData.UID ?? string.Empty;
        if (!string.Equals(senderUid, pairUid, StringComparison.Ordinal))
        {
            return;
        }

        if (!_pendingModNotifications.Any(n => string.Equals(n.Hash, message.Notification.Hash, StringComparison.Ordinal)))
        {
            _pendingModNotifications.Add(message.Notification);
        }
    }

    private void OnPenumbraModTransferCompleted(PenumbraModTransferCompletedMessage message)
    {
        var senderUid = message.Notification.Sender?.UID ?? string.Empty;
        var pairUid = _pair.UserData.UID ?? string.Empty;
        if (!string.Equals(senderUid, pairUid, StringComparison.Ordinal))
        {
            return;
        }

        if (message.Success)
        {
            _pendingModNotifications.RemoveAll(n => string.Equals(n.Hash, message.Notification.Hash, StringComparison.Ordinal));
        }
    }

    private void OnPenumbraModTransferDiscarded(PenumbraModTransferDiscardedMessage message)
    {
        var senderUid = message.Notification.Sender?.UID ?? string.Empty;
        var pairUid = _pair.UserData.UID ?? string.Empty;
        if (!string.Equals(senderUid, pairUid, StringComparison.Ordinal))
        {
            return;
        }

        _pendingModNotifications.RemoveAll(n => string.Equals(n.Hash, message.Notification.Hash, StringComparison.Ordinal));
    }

    private void HandleReloadTimer(bool isAckYou)
    {
        var userUID = _pair.UserData.UID;
        
        _timerLock.Enter();
        try
        {
            // If AckYou is true (green eye), stop any existing timer
            if (isAckYou && _reloadTimers.TryGetValue(userUID, out var existingTimer))
            {
                existingTimer.Dispose();
                _reloadTimers.Remove(userUID);
            }
            // 8-second timer temporarily disabled
        }
        finally
        {
            _timerLock.Exit();
        }
    }
    
    

    public Pair Pair => _pair;
    public UserFullPairDto UserPair => _pair.UserPair!;

    public void DrawPairedClient()
    {
        RefreshAckStatusIfDue();
        using var id = ImRaii.PushId(GetType() + _id);
        var hasOfflineGrace = _pairManager.IsUserInOfflineGrace(_pair.UserData);
        var hasTimingSyncTarget = _localPairEmoteForceSyncService.IsTargetSelected(_pair.UserData.UID);
        var hasTimingLeader = _localPairEmoteForceSyncService.IsLeader(_pair.UserData.UID);
        var color = ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.FrameBgHovered), _wasHovered && !hasOfflineGrace);
        var baseFolderWidth = UiSharedService.GetBaseFolderWidth() + 9.0f;
        using (ImRaii.Child(GetType() + _id, new System.Numerics.Vector2(baseFolderWidth, ImGui.GetFrameHeight()), false, ImGuiWindowFlags.NoScrollbar))
        {
            if (hasOfflineGrace)
            {
                var rowStart = ImGui.GetCursorScreenPos();
                var gradientWidth = baseFolderWidth * 0.42f;
                var rowEnd = new Vector2(rowStart.X + gradientWidth, rowStart.Y + ImGui.GetFrameHeight());
                var startColor = new Vector4(1.0f, 0.66f, 0.26f, _wasHovered ? 0.46f : 0.36f);
                var endColor = new Vector4(1.0f, 0.66f, 0.26f, 0.0f);
                DrawRowGradient(rowStart, rowEnd, startColor, endColor);
            }

            if (hasTimingSyncTarget)
            {
                var rowStart = ImGui.GetCursorScreenPos();
                var gradientWidth = baseFolderWidth * 0.42f;
                var rowEnd = new Vector2(rowStart.X + gradientWidth, rowStart.Y + ImGui.GetFrameHeight());
                var startColor = new Vector4(0.60f, 0.34f, 1.0f, _wasHovered ? 0.42f : 0.30f);
                var endColor = new Vector4(0.60f, 0.34f, 1.0f, 0.0f);
                DrawRowGradient(rowStart, rowEnd, startColor, endColor);
            }

            if (hasTimingLeader)
            {
                var rowStart = ImGui.GetCursorScreenPos();
                var gradientWidth = baseFolderWidth * 0.50f;
                var rowEnd = new Vector2(rowStart.X + gradientWidth, rowStart.Y + ImGui.GetFrameHeight());
                var startColor = new Vector4(1.0f, 0.30f, 0.76f, _wasHovered ? 0.48f : 0.34f);
                var endColor = new Vector4(1.0f, 0.30f, 0.76f, 0.0f);
                DrawRowGradient(rowStart, rowEnd, startColor, endColor);
            }

            DrawLeftSide();
            ImGui.SameLine();
            var posX = ImGui.GetCursorPosX();
            var rightSide = DrawRightSide();
            DrawName(posX, rightSide);
        }
        _wasHovered = ImGui.IsItemHovered();
        color.Dispose();
    }

    private static void DrawRowGradient(Vector2 rowStart, Vector2 rowEnd, Vector4 startColor, Vector4 endColor)
    {
        var drawList = ImGui.GetWindowDrawList();
        var width = rowEnd.X - rowStart.X;
        var slices = Math.Clamp((int)(width / 8.0f), 8, 64);
        var step = width / slices;
        var rounding = ImGui.GetFrameHeight() * 0.38f;
        for (int i = 0; i < slices; i++)
        {
            var x0 = rowStart.X + step * i;
            var x1 = i == slices - 1 ? rowEnd.X : rowStart.X + step * (i + 1);
            var t = (float)i / (float)(slices - 1);
            var gradientColor = Vector4.Lerp(startColor, endColor, t);
            var flags = ImDrawFlags.None;
            var cornerRadius = 0.0f;
            if (i == 0)
            {
                flags = ImDrawFlags.RoundCornersLeft;
                cornerRadius = rounding;
            }
            else if (i == slices - 1)
            {
                flags = ImDrawFlags.RoundCornersRight;
                cornerRadius = rounding;
            }
            drawList.AddRectFilled(new Vector2(x0, rowStart.Y), new Vector2(x1, rowEnd.Y), ImGui.ColorConvertFloat4ToU32(gradientColor), cornerRadius, flags);
        }
    }

    private void DrawCommonClientMenu()
    {
        if (!_pair.IsPaused)
        {
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.User, "Open Profile", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                _displayHandler.OpenProfile(_pair);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("Opens the profile for this user in a new window");
        }

        var canSendPenumbraMod = _pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional
                                 && _pair.OtherAllowsReceivingPenumbraMods;
        using (ImRaii.Disabled(!canSendPenumbraMod))
        {
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.BoxOpen, "Send Penumbra Mod", _menuWidth, ButtonStyleKeys.ContextMenu_Item) && canSendPenumbraMod)
            {
                _mediator.Publish(new OpenSendPenumbraModWindow(_pair));
                ImGui.CloseCurrentPopup();
            }
        }
        UiSharedService.AttachToolTip(_pair.OtherAllowsReceivingPenumbraMods
            ? "Opens a dialog to send a Penumbra mod package to this user. Available only for online, individually paired users."
            : "This user has disabled receiving Penumbra mod packages. You cannot send mods to them.");

        var pendingCount = _pendingModNotifications.Count;
        var hasPendingModTransfer = pendingCount > 0;
        using (ImRaii.Disabled(!hasPendingModTransfer))
        {
            var text = pendingCount > 1 ? $"Install received Penumbra Mods ({pendingCount})" : "Install received Penumbra Mod";
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Download, text, _menuWidth, ButtonStyleKeys.ContextMenu_Item) && hasPendingModTransfer)
            {
                _mediator.Publish(new OpenPenumbraReceiveModWindow(_pendingModNotifications.ToList()));
                ImGui.CloseCurrentPopup();
            }
        }
        UiSharedService.AttachToolTip(hasPendingModTransfer
            ? "Opens the panel to install the received Penumbra mod package from this user."
            : "No pending Penumbra mod package available from this user.");

        if (_pair.IsVisible)
        {
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Sync, "Reload last data", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                _pair.ApplyLastReceivedData(forced: true);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("This reapplies the last received character data to this character");
        }

        if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.PlayCircle, "Cycle pause state", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            _ = _apiController.CyclePauseAsync(_pair.UserData);
            ImGui.CloseCurrentPopup();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Emote Sync");

        var selectedForForceSync = _localPairEmoteForceSyncService.IsTargetSelected(_pair.UserData.UID);
        var selectionIcon = selectedForForceSync ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff;
        if (DrawStateColoredToggleButton(selectionIcon, "Local timing sync target", selectedForForceSync))
        {
            var wasLeaderBefore = _localPairEmoteForceSyncService.IsLeader(_pair.UserData.UID);
            var isSelectedNow = _localPairEmoteForceSyncService.ToggleTargetSelection(_pair.UserData.UID);
            var statusText = isSelectedNow ? "added to" : "removed from";
            var exclusivityInfo = isSelectedNow && wasLeaderBefore ? " Timing leader was cleared for this pair." : string.Empty;
            _mediator.Publish(new NotificationMessage(
                "Emote Sync",
                $"{_pair.UserData.AliasOrUID} was {statusText} local timing-sync targets.{exclusivityInfo}",
                Sphene.SpheneConfiguration.Models.NotificationType.Info,
                TimeSpan.FromSeconds(3)));
        }
        UiSharedService.AttachToolTip("Marks this pair as a local timing-sync target. A pair cannot be both timing leader and target.");

        var isLeader = _localPairEmoteForceSyncService.IsLeader(_pair.UserData.UID);
        var leaderIcon = isLeader ? FontAwesomeIcon.Crown : FontAwesomeIcon.User;
        if (DrawStateColoredToggleButton(leaderIcon, "Set as timing leader", isLeader))
        {
            var wasTargetBefore = _localPairEmoteForceSyncService.IsTargetSelected(_pair.UserData.UID);
            var isLeaderNow = _localPairEmoteForceSyncService.ToggleLeader(_pair.UserData.UID);
            _mediator.Publish(new NotificationMessage(
                "Local Timing Sync",
                isLeaderNow
                    ? $"{_pair.UserData.AliasOrUID} is now your local timing leader. Auto reset on your emote was disabled.{(wasTargetBefore ? " This pair was removed from timing-sync targets." : string.Empty)}"
                    : $"{_pair.UserData.AliasOrUID} is no longer your local timing leader.",
                Sphene.SpheneConfiguration.Models.NotificationType.Info,
                TimeSpan.FromSeconds(3)));
        }
        UiSharedService.AttachToolTip("When this leader triggers an emote update, your timing and selected target timing are reset to 0. Setting a leader disables auto reset on your emote and removes this pair from targets.");

        var followLeaderEnabled = _localPairEmoteForceSyncService.FollowLeaderOnLeaderEmoteEnabled;
        var followLeaderIcon = followLeaderEnabled ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff;
        if (DrawStateColoredToggleButton(followLeaderIcon, "Follow leader emote timing", followLeaderEnabled))
        {
            var enabledNow = _localPairEmoteForceSyncService.ToggleFollowLeaderOnLeaderEmote();
            _mediator.Publish(new NotificationMessage(
                "Emote sync",
                enabledNow ? "Leader-follow emote sync is now enabled." : "Leader-follow emote sync is now disabled.",
                Sphene.SpheneConfiguration.Models.NotificationType.Info,
                TimeSpan.FromSeconds(3)));
        }
        UiSharedService.AttachToolTip("Enables or disables automatic timing follow when your selected leader emotes.");

        var autoResetEnabled = _localPairEmoteForceSyncService.AutoResetOnLocalEmoteEnabled;
        var autoResetIcon = autoResetEnabled ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff;
        if (DrawStateColoredToggleButton(autoResetIcon, "Auto reset selected on my emote", autoResetEnabled))
        {
            var enabledNow = _localPairEmoteForceSyncService.ToggleAutoResetOnLocalEmote();
            _mediator.Publish(new NotificationMessage(
                "Local Timing Sync",
                enabledNow
                    ? "Automatic reset on your emote is now enabled. The timing leader was cleared."
                    : "Automatic reset on your emote is now disabled.",
                Sphene.SpheneConfiguration.Models.NotificationType.Info,
                TimeSpan.FromSeconds(3)));
        }
        UiSharedService.AttachToolTip("When enabled, selected visible targets are reset to animation time 0 whenever your emote state updates. Enabling this clears the timing leader.");

        var selectedCount = _localPairEmoteForceSyncService.GetSelectedTargetCount();
        using (ImRaii.Disabled(selectedCount <= 0))
        {
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Sync, "Reset emote timing for selected", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                _ = Task.Run(async () =>
                {
                    var result = await _localPairEmoteForceSyncService.ForceSyncToSelectedVisibleTargetsAsync().ConfigureAwait(false);
                    var notificationType = result.Success
                        ? Sphene.SpheneConfiguration.Models.NotificationType.Success
                        : Sphene.SpheneConfiguration.Models.NotificationType.Warning;
                    _mediator.Publish(new NotificationMessage(
                        "Local Timing Sync",
                        $"{result.Message} Skipped invisible: {result.SkippedNotVisibleCount}, missing: {result.SkippedMissingCount}.",
                        notificationType,
                        TimeSpan.FromSeconds(5)));
                });
                ImGui.CloseCurrentPopup();
            }
        }
        UiSharedService.AttachToolTip(selectedCount > 0
            ? $"Resets current animation time to 0 for you and {selectedCount} selected mutually visible target(s), without changing which emote each player is using."
            : "Select at least one target to reset emote timing.");
        ImGui.Separator();

        ImGui.TextUnformatted("Pair Permission Functions");
        if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.WindowMaximize, "Open Permissions Window", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            _mediator.Publish(new OpenPermissionWindow(_pair));
            ImGui.CloseCurrentPopup();
        }
        UiSharedService.AttachToolTip("Opens the Permissions Window which allows you to manage multiple permissions at once.");

        var isSticky = _pair.UserPair!.OwnPermissions.IsSticky();
        string stickyText = "Preferred Permissions";
        var stickyIcon = isSticky ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff;
        if (DrawStateColoredToggleButton(stickyIcon, stickyText, isSticky))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetSticky(!isSticky);
            _ = _apiController.UserSetPairPermissions(new(_pair.UserData, permissions));
        }
        UiSharedService.AttachToolTip("Preferred permissions means that this pair will not" + Environment.NewLine + " be affected by any syncshell permission changes through you.");

        string individualText = Environment.NewLine + Environment.NewLine + "Note: changing this permission will turn the permissions for this"
            + Environment.NewLine + "user to preferred permissions. You can change this behavior"
            + Environment.NewLine + "in the permission settings.";
        bool individual = !_pair.IsDirectlyPaired && _apiController.DefaultPermissions!.IndividualIsSticky;

        var isDisableSounds = _pair.UserPair!.OwnPermissions.IsDisableSounds();
        string disableSoundsText = "Sound sync";
        var disableSoundsIcon = isDisableSounds ? FontAwesomeIcon.ToggleOff : FontAwesomeIcon.ToggleOn;
        if (DrawStateColoredToggleButton(disableSoundsIcon, disableSoundsText, !isDisableSounds))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetDisableSounds(!isDisableSounds);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
        }
        UiSharedService.AttachToolTip("Changes sound sync permissions with this user." + (individual ? individualText : string.Empty));

        var isDisableAnims = _pair.UserPair!.OwnPermissions.IsDisableAnimations();
        string disableAnimsText = "Animation sync";
        var disableAnimsIcon = isDisableAnims ? FontAwesomeIcon.ToggleOff : FontAwesomeIcon.ToggleOn;
        if (DrawStateColoredToggleButton(disableAnimsIcon, disableAnimsText, !isDisableAnims))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetDisableAnimations(!isDisableAnims);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
        }
        UiSharedService.AttachToolTip("Changes animation sync permissions with this user." + (individual ? individualText : string.Empty));

        var isDisableVFX = _pair.UserPair!.OwnPermissions.IsDisableVFX();
        string disableVFXText = "VFX sync";
        var disableVFXIcon = isDisableVFX ? FontAwesomeIcon.ToggleOff : FontAwesomeIcon.ToggleOn;
        if (DrawStateColoredToggleButton(disableVFXIcon, disableVFXText, !isDisableVFX))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetDisableVFX(!isDisableVFX);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
        }
        UiSharedService.AttachToolTip("Changes VFX sync permissions with this user." + (individual ? individualText : string.Empty));

        var isDisableVfxInDuty = _pair.UserPair!.OwnPermissions.IsDisableVFXInDuty();
        string disableVfxInDutyText = "VFX sync in duty";
        var disableVfxInDutyIcon = isDisableVfxInDuty ? FontAwesomeIcon.ToggleOff : FontAwesomeIcon.ToggleOn;
        if (DrawStateColoredToggleButton(disableVfxInDutyIcon, disableVfxInDutyText, !isDisableVfxInDuty))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetDisableVFXInDuty(!isDisableVfxInDuty);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
        }
        UiSharedService.AttachToolTip("When enabled, VFX files from this user will be removed while you are in duty." + (individual ? individualText : string.Empty));

        ImGui.Separator();
        ImGui.TextUnformatted("Performance Functions");
        
        // Check if user is already whitelisted
        var userIdentifier = !string.IsNullOrEmpty(_pair.UserData.Alias) ? _pair.UserData.Alias : _pair.UserData.UID;
        bool isWhitelisted = _performanceConfigService.Current.UIDsToIgnore.Contains(userIdentifier, StringComparer.Ordinal) ||
                            _performanceConfigService.Current.UIDsToIgnore.Contains(_pair.UserData.UID, StringComparer.Ordinal);
        
        if (!isWhitelisted)
        {
            if (DrawStateColoredToggleButton(FontAwesomeIcon.Shield, "Add to Performance Whitelist", false))
            {
                // Use alias if available, otherwise use UID
                var identifierToAdd = !string.IsNullOrEmpty(_pair.UserData.Alias) ? _pair.UserData.Alias : _pair.UserData.UID;
                
                if (!_performanceConfigService.Current.UIDsToIgnore.Contains(identifierToAdd, StringComparer.Ordinal))
                {
                    _performanceConfigService.Current.UIDsToIgnore.Add(identifierToAdd);
                    _performanceConfigService.Save();
                }
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("Adds this user to the performance whitelist to ignore all performance warnings and auto-pause operations." + Environment.NewLine + 
                                        "Will use: " + (!string.IsNullOrEmpty(_pair.UserData.Alias) ? _pair.UserData.Alias : _pair.UserData.UID));
        }
        else
        {
            if (DrawStateColoredToggleButton(FontAwesomeIcon.ShieldAlt, "Remove from Performance Whitelist", true))
            {
                // Remove both alias and UID if they exist in the list
                _performanceConfigService.Current.UIDsToIgnore.RemoveAll(uid => 
                    string.Equals(uid, _pair.UserData.Alias, StringComparison.Ordinal) || 
                    string.Equals(uid, _pair.UserData.UID, StringComparison.Ordinal));
                _performanceConfigService.Save();
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("Removes this user from the performance whitelist.");
        }
    }

    private bool DrawStateColoredToggleButton(FontAwesomeIcon icon, string text, bool enabledState)
    {
        using var _ = ImRaii.PushColor(ImGuiCol.Text, enabledState ? SoftToggleEnabledColor : SoftToggleDisabledColor);
        return _uiSharedService.IconTextActionButton(icon, text, _menuWidth, ButtonStyleKeys.ContextMenu_Item);
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _mediator.Unsubscribe<UserPairIconUpdateMessage>(this);
            _mediator.Unsubscribe<PenumbraModTransferAvailableMessage>(this);
            _mediator.Unsubscribe<PenumbraModTransferCompletedMessage>(this);
            _mediator.Unsubscribe<PenumbraModTransferDiscardedMessage>(this);
            _mediator.Unsubscribe<PairAcknowledgmentStatusChangedMessage>(this);
            _mediator.Unsubscribe<AcknowledgmentPendingMessage>(this);
            _mediator.Unsubscribe<AcknowledgmentUiRefreshMessage>(this);
            _timerLock.Enter();
            try
            {
                if (_reloadTimers.TryGetValue(_pair.UserData.UID, out var timer))
                {
                    timer.Dispose();
                    _reloadTimers.Remove(_pair.UserData.UID);
                }
            }
            finally
            {
                _timerLock.Exit();
            }
            _globalAckYouLock.Enter();
            try
            {
                _globalLastSentAckYouStatus.Remove(_pair.UserData.UID);
                _globalLastAckYouSentTime.Remove(_pair.UserData.UID);
            }
            finally
            {
                _globalAckYouLock.Exit();
            }
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void DrawIndividualMenu()
    {
        ImGui.TextUnformatted("Individual Pair Functions");
        var entryUID = _pair.UserData.AliasOrUID;

        if (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional)
        {
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Folder, "Pair Groups", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                _selectTagForPairUi.Open(_pair);
            }
            UiSharedService.AttachToolTip("Choose pair groups for " + entryUID);
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Trash, "Unpair Permanently", _menuWidth, ButtonStyleKeys.ContextMenu_Item) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.UserRemovePair(new(_pair.UserData));
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to unpair permanently from " + entryUID);
        }
        else if (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.OneSided)
        {
            if (!_pair.UserPair.IsOutgoingIndividualPair && _uiSharedService.IconTextActionButton(FontAwesomeIcon.Plus, "Pair individually", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                _ = _apiController.UserAddPair(new(_pair.UserData));
            }
            if (!_pair.UserPair.IsOutgoingIndividualPair)
            {
                UiSharedService.AttachToolTip("Complete one-sided pairing with " + entryUID);
            }

            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Trash, "Unpair Permanently", _menuWidth, ButtonStyleKeys.ContextMenu_Item) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.UserRemovePair(new(_pair.UserData));
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to unpair permanently from " + entryUID);
        }
        else
        {
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Plus, "Pair individually", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                _ = _apiController.UserAddPair(new(_pair.UserData));
            }
            UiSharedService.AttachToolTip("Pair individually with " + entryUID);
        }
    }

    private void DrawLeftSide()
    {
        string userPairText = string.Empty;
        var showUserPairTooltip = false;

        ImGui.AlignTextToFramePadding();

        var isVisibleForIcon = _pair.IsMutuallyVisible || (_uiSharedService.IsInGpose && _pair.WasMutuallyVisibleInGpose);
        var partnerAckYou = _pair.UserPair.OtherPermissions.IsAckYou();
        var ownAckYou = _pair.UserPair.OwnPermissions.IsAckYou();
        var latestDataApplied = _pair.IsLatestReceivedDataApplied();
        var effectiveOwnAckYou = _pair.LastReceivedCharacterData != null ? latestDataApplied : ownAckYou;
        var suppressAckUi = _pair.IsInDuty;
        var isTimingSyncTarget = _localPairEmoteForceSyncService.IsTargetSelected(_pair.UserData.UID);
        var isAutoResetActiveForTargets = _localPairEmoteForceSyncService.AutoResetOnLocalEmoteEnabled;

        if (_pair.IsPaused)
        {
            using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            _uiSharedService.IconText(FontAwesomeIcon.PauseCircle);
            showUserPairTooltip = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            userPairText = _pair.UserData.AliasOrUID + " is paused";
        }
        else if (!_pair.IsOnline)
        {
            using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            _uiSharedService.IconText(_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.OneSided
                ? FontAwesomeIcon.ArrowsLeftRight
                : (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional
                    ? FontAwesomeIcon.User : FontAwesomeIcon.Users));
            showUserPairTooltip = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            userPairText = _pair.UserData.AliasOrUID + " is offline";
        }
        else if (isVisibleForIcon)
        {
            if (_syncedGroups.Any() && _pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.None)
            {
                using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGreen);
                _uiSharedService.IconText(FontAwesomeIcon.Link);
                ImGui.SameLine();
            }
            
            var iconColor = suppressAckUi ? ImGuiColors.ParsedGreen : (effectiveOwnAckYou ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudYellow);
            var icon = (_uiSharedService.IsInGpose || _pair.IsInGpose) ? FontAwesomeIcon.Camera : FontAwesomeIcon.Eye;
            _uiSharedService.IconText(icon, iconColor);
            showUserPairTooltip = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            
            if (suppressAckUi)
            {
                userPairText = _pair.UserData.AliasOrUID + " is visible: " + _pair.PlayerName + Environment.NewLine + "Click to target this player";
            }
            else
            {
                var ackStatus = effectiveOwnAckYou ? "You acknowledge this user's data" : "You do not acknowledge this user's data";
                if (partnerAckYou != effectiveOwnAckYou)
                {
                    var partnerStatus = partnerAckYou ? "This user acknowledges your data" : "This user does not acknowledge your data";
                    ackStatus += Environment.NewLine + partnerStatus;
                }
                userPairText = _pair.UserData.AliasOrUID + " is visible: " + _pair.PlayerName + Environment.NewLine + ackStatus + Environment.NewLine + "Click to target this player";
            }
            
            HandleReloadTimer(effectiveOwnAckYou);
            
            if (ImGui.IsItemClicked())
            {
                _mediator.Publish(new TargetPairMessage(_pair));
            }
        }
        else
        {
            using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
            if (_pair.IsInGpose)
            {
                _uiSharedService.IconText(FontAwesomeIcon.Camera);
                showUserPairTooltip = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                userPairText = _pair.UserData.AliasOrUID + " is in GPose" + Environment.NewLine + "No data is shared while in GPose";
            }
            else
            {
                _uiSharedService.IconText(_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional
                    ? FontAwesomeIcon.User : FontAwesomeIcon.Users);
                showUserPairTooltip = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                userPairText = _pair.UserData.AliasOrUID + " is online";
            }
        }

        if (_pair.IsOnline && _pair.IsMutuallyVisible && !suppressAckUi)
        {
            ImGui.SameLine();
            if (_pair.HasPendingAcknowledgment)
            {
                using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                _uiSharedService.IconText(FontAwesomeIcon.Clock);
                UiSharedService.AttachToolTip("Processing character data...");
            }
            else if (_pair.LastAcknowledgmentSuccess.HasValue)
            {
                if (_pair.LastAcknowledgmentSuccess.Value)
                {
                    using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGreen);
                    _uiSharedService.IconText(FontAwesomeIcon.CheckCircle);
                    var timeAgo = _pair.LastAcknowledgmentTime.HasValue 
                        ? $" ({(DateTimeOffset.UtcNow - _pair.LastAcknowledgmentTime.Value).TotalSeconds:F0}s ago)"
                        : string.Empty;
                    UiSharedService.AttachToolTip($"Data synchronized successfully{timeAgo}");
                }
                else
                {
                    using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                    _uiSharedService.IconText(FontAwesomeIcon.ExclamationTriangle);
                    var timeAgo = _pair.LastAcknowledgmentTime.HasValue 
                        ? $" ({(DateTimeOffset.UtcNow - _pair.LastAcknowledgmentTime.Value).TotalSeconds:F0}s ago)"
                        : string.Empty;
                    UiSharedService.AttachToolTip($"Synchronization failed{timeAgo}");
                }
            }
        }

        if (isTimingSyncTarget && isAutoResetActiveForTargets)
        {
            ImGui.SameLine();
            using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            _uiSharedService.IconText(FontAwesomeIcon.PersonCircleCheck);
            UiSharedService.AttachToolTip("Auto reset on your emote is active for this timing-sync target. Their current emote timing resets to 0 when your emote updates.");
        }

        if (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.OneSided)
        {
            userPairText += UiSharedService.TooltipSeparator + "One-sided individual pair";
        }
        else if (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional)
        {
            userPairText += UiSharedService.TooltipSeparator + "You are directly Paired";
        }

        if (_pair.IsOnline && !string.IsNullOrWhiteSpace(UserPair.RemoteClientVersion))
        {
            var remoteClientVersion = UserPair.RemoteClientVersion;
            if (Version.TryParse(remoteClientVersion, out var parsedRemoteClientVersion))
            {
                var normalizedVersion = parsedRemoteClientVersion.Build >= 0
                    ? $"{parsedRemoteClientVersion.Major}.{parsedRemoteClientVersion.Minor}.{parsedRemoteClientVersion.Build}"
                    : $"{parsedRemoteClientVersion.Major}.{parsedRemoteClientVersion.Minor}";
                if (parsedRemoteClientVersion.Revision >= 0)
                    remoteClientVersion = $"{normalizedVersion} rev.{parsedRemoteClientVersion.Revision}";
                else
                    remoteClientVersion = normalizedVersion;
            }

            userPairText += UiSharedService.TooltipSeparator + $"Client Version: {remoteClientVersion}";
        }

        if (_pair.LastAppliedDataBytes >= 0)
        {
            userPairText += UiSharedService.TooltipSeparator;
            userPairText += ((!_pair.IsPaired) ? "(Last) " : string.Empty) + "Mods Info" + Environment.NewLine;
            userPairText += "Files Size: " + UiSharedService.ByteToString(_pair.LastAppliedDataBytes, true);
            if (_pair.LastAppliedApproximateVRAMBytes >= 0)
            {
                userPairText += Environment.NewLine + "Approx. VRAM Usage: " + UiSharedService.ByteToString(_pair.LastAppliedApproximateVRAMBytes, true);
            }
            if (_pair.LastAppliedDataTris >= 0)
            {
                userPairText += Environment.NewLine + "Approx. Triangle Count (excl. Vanilla): "
                    + (_pair.LastAppliedDataTris > 1000 ? (_pair.LastAppliedDataTris / 1000d).ToString("0.0'k'") : _pair.LastAppliedDataTris);
            }
        }

        // Add synchronization status information - only show for visible pairs
        if (_pair.IsOnline && _pair.IsVisible && !suppressAckUi)
        {
            // Show sync status for any pending acknowledgment (including build start)
            if (GetCachedHasPendingAcknowledgment())
            {
                userPairText += UiSharedService.TooltipSeparator + "Data Sync: Waiting for acknowledgment from this user...";
            }
            else if (_pair.LastAcknowledgmentSuccess.HasValue)
            {
                var syncStatus = _pair.LastAcknowledgmentSuccess.Value ? "Successfully synchronized" : "Synchronization failed";
                var timeAgo = _pair.LastAcknowledgmentTime.HasValue 
                    ? $" ({(DateTimeOffset.UtcNow - _pair.LastAcknowledgmentTime.Value).TotalSeconds:F0}s ago)"
                    : string.Empty;
                userPairText += UiSharedService.TooltipSeparator + $"Data Sync: {syncStatus}{timeAgo}";
            }
        }

        if (_pair.IsInDuty)
        {
            userPairText += UiSharedService.TooltipSeparator + "Info: In duty, acknowledgment display is hidden.";
        }

        if (_syncedGroups.Any())
        {
            userPairText += UiSharedService.TooltipSeparator + string.Join(Environment.NewLine,
                _syncedGroups.Select(g =>
                {
                    var groupNote = _serverConfigurationManager.GetNoteForGid(g.GID);
                    var groupString = string.IsNullOrEmpty(groupNote) ? g.GroupAliasOrGID : $"{groupNote} ({g.GroupAliasOrGID})";
                    return "Paired through " + groupString;
                }));
        }

        DrawTooltipForHoveredItem(showUserPairTooltip, userPairText);

        if (_performanceConfigService.Current.ShowPerformanceIndicator
            && !_performanceConfigService.Current.UIDsToIgnore
                .Exists(uid => string.Equals(uid, UserPair.User.Alias, StringComparison.Ordinal) || string.Equals(uid, UserPair.User.UID, StringComparison.Ordinal))
            && ((_performanceConfigService.Current.VRAMSizeWarningThresholdMiB > 0 && _performanceConfigService.Current.VRAMSizeWarningThresholdMiB * 1024 * 1024 < _pair.LastAppliedApproximateVRAMBytes)
                || (_performanceConfigService.Current.TrisWarningThresholdThousands > 0 && _performanceConfigService.Current.TrisWarningThresholdThousands * 1000 < _pair.LastAppliedDataTris))
            && (!_pair.UserPair.OwnPermissions.IsSticky()
                || _performanceConfigService.Current.WarnOnPreferredPermissionsExceedingThresholds))
        {
            ImGui.SameLine();

            _uiSharedService.IconText(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);

            string userWarningText = "WARNING: This user exceeds one or more of your defined thresholds:" + UiSharedService.TooltipSeparator;
            bool shownVram = false;
            if (_performanceConfigService.Current.VRAMSizeWarningThresholdMiB > 0
                && _performanceConfigService.Current.VRAMSizeWarningThresholdMiB * 1024 * 1024 < _pair.LastAppliedApproximateVRAMBytes)
            {
                shownVram = true;
                userWarningText += $"Approx. VRAM Usage: Used: {UiSharedService.ByteToString(_pair.LastAppliedApproximateVRAMBytes)}, Threshold: {_performanceConfigService.Current.VRAMSizeWarningThresholdMiB} MiB";
            }
            if (_performanceConfigService.Current.TrisWarningThresholdThousands > 0
                && _performanceConfigService.Current.TrisWarningThresholdThousands * 1024 < _pair.LastAppliedDataTris)
            {
                if (shownVram) userWarningText += Environment.NewLine;
                userWarningText += $"Approx. Triangle count: Used: {_pair.LastAppliedDataTris}, Threshold: {_performanceConfigService.Current.TrisWarningThresholdThousands * 1000}";
            }

            UiSharedService.AttachToolTip(userWarningText);
        }

        ImGui.SameLine();
    }

    private static void DrawTooltipForHoveredItem(bool isHovered, string text)
    {
        if (!isHovered) return;
        using (SpheneCustomTheme.ApplyTooltipTheme())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
            if (text.IndexOf(UiSharedService.TooltipSeparator, StringComparison.Ordinal) >= 0)
            {
                var splitText = text.Split(UiSharedService.TooltipSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (int i = 0; i < splitText.Length; i++)
                {
                    ImGui.TextUnformatted(splitText[i]);
                    if (i != splitText.Length - 1) ImGui.Separator();
                }
            }
            else
            {
                ImGui.TextUnformatted(text);
            }
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private void DrawName(float leftSide, float rightSide)
    {
        _displayHandler.DrawPairText(_id, _pair, leftSide, () => rightSide - leftSide);
    }

    private void DrawPairedClientMenu()
    {
        DrawIndividualMenu();

        if (_syncedGroups.Any()) ImGui.Separator();
        foreach (var entry in _syncedGroups)
        {
            bool selfIsOwner = string.Equals(_apiController.UID, entry.Owner.UID, StringComparison.Ordinal);
            bool selfIsModerator = entry.GroupUserInfo.IsModerator();
            bool userIsModerator = entry.GroupPairUserInfos.TryGetValue(_pair.UserData.UID, out var modinfo) && modinfo.IsModerator();
            bool userIsPinned = entry.GroupPairUserInfos.TryGetValue(_pair.UserData.UID, out var info) && info.IsPinned();
            if (selfIsOwner || selfIsModerator)
            {
                var groupNote = _serverConfigurationManager.GetNoteForGid(entry.GID);
                var groupString = string.IsNullOrEmpty(groupNote) ? entry.GroupAliasOrGID : $"{groupNote} ({entry.GroupAliasOrGID})";

                if (ImGui.BeginMenu(groupString + " Moderation Functions"))
                {
                    DrawSyncshellMenu(entry, selfIsOwner, selfIsModerator, userIsPinned, userIsModerator);
                    ImGui.EndMenu();
                }
            }
        }
    }

    private float DrawRightSide()
    {
        var pauseIcon = _pair.UserPair!.OwnPermissions.IsPaused() ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var pauseButtonSize = _uiSharedService.GetIconButtonSize(pauseIcon);
        var isOneSidedIndividualPair = _pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.OneSided;
        var isOneSidedIndividualListEntry = isOneSidedIndividualPair
            && _currentGroup == null
            && _id.StartsWith(TagHandler.CustomUnpairedTag, StringComparison.Ordinal);
        var rightmostIcon = isOneSidedIndividualListEntry ? FontAwesomeIcon.User : FontAwesomeIcon.EllipsisV;
        var rightmostButtonSize = _uiSharedService.GetIconButtonSize(rightmostIcon);
        var reloadButtonSize = _pair.IsVisible ? _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Sync) : Vector2.Zero;
        var pairButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus);
        var unpairButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Trash);
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        // Use container-relative positioning for buttons
        var containerWidth = ImGui.GetContentRegionAvail().X;
        var actualWindowEndX = ImGui.GetCursorPosX() + containerWidth;
        
        // Adjust button positioning for grouped syncshell folders
        var buttonOffset = (_currentGroup != null && _configService.Current.GroupUpSyncshells) ? 20f : 0f;
        float currentRightSide = actualWindowEndX - rightmostButtonSize.X - buttonOffset;

        // Rightmost action button
        ImGui.SameLine(currentRightSide);
        ImGui.AlignTextToFramePadding();
        if (_uiSharedService.IconButton(rightmostIcon, null, null, null, null, ButtonStyleKeys.Pair_Menu))
        {
            if (isOneSidedIndividualListEntry)
            {
                _displayHandler.OpenProfile(_pair);
            }
            else
            {
                ImGui.OpenPopup("User Flyout Menu");
            }
        }
        UiSharedService.AttachToolTip(isOneSidedIndividualListEntry
            ? "Open profile of " + _pair.UserData.AliasOrUID
            : "Open user menu");

        // Reload button (only if pair is visible)
        if (_pair.IsVisible)
        {
            currentRightSide -= (reloadButtonSize.X + spacingX);
            ImGui.SameLine(currentRightSide);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Sync, null, null, null, null, ButtonStyleKeys.Pair_Reload))
            {
                _pair.ApplyLastReceivedData(forced: true);
            }
            UiSharedService.AttachToolTip("Reload last received character data");
        }

        if (isOneSidedIndividualListEntry && !_pair.UserPair.IsOutgoingIndividualPair)
        {
            currentRightSide -= (pairButtonSize.X + spacingX);
            ImGui.SameLine(currentRightSide);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
            {
                _ = _apiController.UserAddPair(new(_pair.UserData));
            }
            UiSharedService.AttachToolTip("Pair individually with " + _pair.UserData.AliasOrUID);

        }

        if (isOneSidedIndividualListEntry)
        {
            currentRightSide -= (unpairButtonSize.X + spacingX);
            ImGui.SameLine(currentRightSide);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Trash) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.UserRemovePair(new(_pair.UserData));
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to unpair permanently from " + _pair.UserData.AliasOrUID);
        }

        if (!isOneSidedIndividualListEntry)
        {
            // Pause/Play button (leftmost of the three)
            currentRightSide -= (pauseButtonSize.X + spacingX);
            ImGui.SameLine(currentRightSide);
            if (_uiSharedService.IconButton(pauseIcon, null, null, null, null, ButtonStyleKeys.Pair_Pause))
            {
                var perm = _pair.UserPair!.OwnPermissions;

                if (UiSharedService.CtrlPressed() && !perm.IsPaused())
                {
                    perm.SetSticky(true);
                }
                perm.SetPaused(!perm.IsPaused());
                _ = _apiController.UserSetPairPermissions(new(_pair.UserData, perm));
            }
            UiSharedService.AttachToolTip(!_pair.UserPair!.OwnPermissions.IsPaused()
                ? ("Pause pairing with " + _pair.UserData.AliasOrUID
                    + (_pair.UserPair!.OwnPermissions.IsSticky()
                        ? string.Empty
                        : UiSharedService.TooltipSeparator + "Hold CTRL to enable preferred permissions while pausing." + Environment.NewLine + "This will leave this pair paused even if unpausing syncshells including this pair."))
                : "Resume pairing with " + _pair.UserData.AliasOrUID);
        }

        if (_pair.IsPaired)
        {
            var individualSoundsDisabled = (_pair.UserPair?.OwnPermissions.IsDisableSounds() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableSounds() ?? false);
            var individualAnimDisabled = (_pair.UserPair?.OwnPermissions.IsDisableAnimations() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableAnimations() ?? false);
            var individualVFXDisabled = (_pair.UserPair?.OwnPermissions.IsDisableVFX() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableVFX() ?? false);
            var individualIsSticky = _pair.UserPair!.OwnPermissions.IsSticky();
            var individualIcon = individualIsSticky ? FontAwesomeIcon.ArrowCircleUp : FontAwesomeIcon.InfoCircle;

            if (individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled || individualIsSticky)
            {
                currentRightSide -= (_uiSharedService.GetIconSize(individualIcon).X + spacingX);

                ImGui.SameLine(currentRightSide);
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled))
                    _uiSharedService.IconText(individualIcon);
                if (ImGui.IsItemHovered())
                {
                    using (SpheneCustomTheme.ApplyTooltipTheme())
                    {
                        ImGui.BeginTooltip();

                        ImGui.TextUnformatted("Individual User permissions");
                        ImGui.Separator();

                        if (individualIsSticky)
                        {
                            _uiSharedService.IconText(individualIcon);
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("Preferred permissions enabled");
                            if (individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled)
                                ImGui.Separator();
                        }

                        if (individualSoundsDisabled)
                        {
                            var userSoundsText = "Sound sync";
                            _uiSharedService.IconText(FontAwesomeIcon.VolumeOff);
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted(userSoundsText);
                            ImGui.NewLine();
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("You");
                            _uiSharedService.BooleanToColoredIcon(!_pair.UserPair!.OwnPermissions.IsDisableSounds());
                            ImGui.SameLine();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("They");
                            _uiSharedService.BooleanToColoredIcon(!_pair.UserPair!.OtherPermissions.IsDisableSounds());
                        }

                        if (individualAnimDisabled)
                        {
                            var userAnimText = "Animation sync";
                            _uiSharedService.IconText(FontAwesomeIcon.Stop);
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted(userAnimText);
                            ImGui.NewLine();
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("You");
                            _uiSharedService.BooleanToColoredIcon(!_pair.UserPair!.OwnPermissions.IsDisableAnimations());
                            ImGui.SameLine();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("They");
                            _uiSharedService.BooleanToColoredIcon(!_pair.UserPair!.OtherPermissions.IsDisableAnimations());
                        }

                        if (individualVFXDisabled)
                        {
                            var userVFXText = "VFX sync";
                            _uiSharedService.IconText(FontAwesomeIcon.Circle);
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted(userVFXText);
                            ImGui.NewLine();
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("You");
                            _uiSharedService.BooleanToColoredIcon(!_pair.UserPair!.OwnPermissions.IsDisableVFX());
                            ImGui.SameLine();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("They");
                            _uiSharedService.BooleanToColoredIcon(!_pair.UserPair!.OtherPermissions.IsDisableVFX());
                        }

                        ImGui.EndTooltip();
                    }
                }
            }

            if (_pendingModNotifications.Count > 0)
            {
                var modIconWidth = _uiSharedService.GetIconSize(FontAwesomeIcon.BoxOpen).X;
                currentRightSide -= (modIconWidth + spacingX);
                ImGui.SameLine(currentRightSide);
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGreen))
                    _uiSharedService.IconText(FontAwesomeIcon.BoxOpen);
                
                var tooltip = _pendingModNotifications.Count > 1 
                    ? $"This user has {_pendingModNotifications.Count} Penumbra mod packages ready to install."
                    : "This user has a Penumbra mod package ready to install.";
                UiSharedService.AttachToolTip(tooltip);
            }
        }

        if (_charaDataManager.SharedWithYouData.TryGetValue(_pair.UserData, out var sharedData))
        {
            currentRightSide -= (_uiSharedService.GetIconSize(FontAwesomeIcon.Running).X + (spacingX / 2f));
            ImGui.SameLine(currentRightSide);
            _uiSharedService.IconText(FontAwesomeIcon.Running);
            UiSharedService.AttachToolTip($"This user has shared {sharedData.Count} Character Data Sets with you." + UiSharedService.TooltipSeparator
                + "Click to open the Character Data Hub and show the entries.");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _mediator.Publish(new OpenCharaDataHubWithFilterMessage(_pair.UserData));
            }
        }

        if (_currentGroup != null)
        {
            var icon = FontAwesomeIcon.None;
            var text = string.Empty;
            if (string.Equals(_currentGroup.OwnerUID, _pair.UserData.UID, StringComparison.Ordinal))
            {
                icon = FontAwesomeIcon.Crown;
                text = "User is owner of this syncshell";
            }
            else if (_currentGroup.GroupPairUserInfos.TryGetValue(_pair.UserData.UID, out var userinfo))
            {
                if (userinfo.IsModerator())
                {
                    icon = FontAwesomeIcon.UserShield;
                    text = "User is moderator in this syncshell";
                }
                else if (userinfo.IsPinned())
                {
                    icon = FontAwesomeIcon.Thumbtack;
                    text = "User is pinned in this syncshell";
                }
            }

            if (!string.IsNullOrEmpty(text))
            {
                currentRightSide -= (_uiSharedService.GetIconSize(icon).X + spacingX);
                ImGui.SameLine(currentRightSide);
                _uiSharedService.IconText(icon);
                UiSharedService.AttachToolTip(text);
            }
        }

        using (SpheneCustomTheme.ApplyContextMenuTheme())
        {
            if (ImGui.BeginPopup("User Flyout Menu"))
            {
                using (ImRaii.PushId($"buttons-{_pair.UserData.UID}"))
                {
                    ImGui.TextUnformatted("Common Pair Functions");
                    DrawCommonClientMenu();
                    ImGui.Separator();
                    DrawPairedClientMenu();
                    if (_menuWidth <= 0)
                    {
                        _menuWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                    }
                }
                ImGui.EndPopup();
            }
        }

        return currentRightSide - spacingX;
    }

    private void DrawSyncshellMenu(GroupFullInfoDto group, bool selfIsOwner, bool selfIsModerator, bool userIsPinned, bool userIsModerator)
    {
        if (selfIsOwner || ((selfIsModerator) && (!userIsModerator)))
        {
            ImGui.TextUnformatted("Syncshell Moderator Functions");
            var pinText = "Pinned in syncshell";
            var pinIcon = userIsPinned ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff;
            if (DrawStateColoredToggleButton(pinIcon, pinText, userIsPinned))
            {
                ImGui.CloseCurrentPopup();
                if (!group.GroupPairUserInfos.TryGetValue(_pair.UserData.UID, out var userinfo))
                {
                    userinfo = API.Data.Enum.GroupPairUserInfo.IsPinned;
                }
                else
                {
                    userinfo.SetPinned(!userinfo.IsPinned());
                }
                _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(group.Group, _pair.UserData, userinfo));
            }
            UiSharedService.AttachToolTip("Pin this user to the Syncshell. Pinned users will not be deleted in case of a manually initiated Syncshell clean");

            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Remove user", _menuWidth, true, ButtonStyleKeys.Pair_Remove) && UiSharedService.CtrlPressed())
            {
                ImGui.CloseCurrentPopup();
                _ = _apiController.GroupRemoveUser(new(group.Group, _pair.UserData));
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to remove user " + (_pair.UserData.AliasOrUID) + " from Syncshell");

            if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserSlash, "Ban User", _menuWidth, true, ButtonStyleKeys.Pair_Ban))
            {
                _mediator.Publish(new OpenBanUserPopupMessage(_pair, group));
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("Ban user from this Syncshell");

            ImGui.Separator();
        }

        if (selfIsOwner)
        {
            ImGui.TextUnformatted("Syncshell Owner Functions");
            var modText = "Syncshell moderator";
            var modIcon = userIsModerator ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff;
            if (DrawStateColoredToggleButton(modIcon, modText, userIsModerator) && UiSharedService.CtrlPressed())
            {
                ImGui.CloseCurrentPopup();
                if (!group.GroupPairUserInfos.TryGetValue(_pair.UserData.UID, out var userinfo))
                {
                    userinfo = API.Data.Enum.GroupPairUserInfo.IsModerator;
                }
                else
                {
                    userinfo.SetModerator(!userinfo.IsModerator());
                }

                _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(group.Group, _pair.UserData, userinfo));
            }
            UiSharedService.AttachToolTip("Hold CTRL to change the moderator status for " + (_pair.UserData.AliasOrUID) + Environment.NewLine +
                "Moderators can kick, ban/unban, pin/unpin users and clear the Syncshell.");

            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Crown, "Transfer Ownership", _menuWidth, true, ButtonStyleKeys.Pair_Transfer) && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
            {
                ImGui.CloseCurrentPopup();
                _ = _apiController.GroupChangeOwnership(new(group.Group, _pair.UserData));
            }
        }
    }
    
    private bool GetCachedHasPendingAcknowledgment()
    {
        return _cachedHasPendingAck;
    }

    private void RefreshAckStatusIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - _lastAckStatusPoll < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        _lastAckStatusPoll = now;
        _cachedHasPendingAck = _pairManager.HasPendingAcknowledgmentForUser(_pair.UserData);
    }

    public void RefreshIcon()
    {
        _cachedHasPendingAck = _pairManager.HasPendingAcknowledgmentForUser(_pair.UserData);
        _lastAckStatusPoll = DateTime.UtcNow;
    }
}
