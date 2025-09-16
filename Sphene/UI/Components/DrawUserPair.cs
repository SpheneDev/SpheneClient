using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using Sphene.SpheneConfiguration;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Events;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI.Handlers;
using Sphene.WebAPI;
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
    private float _menuWidth = -1;
    private bool _wasHovered = false;
    
    // Static dictionary to track reload timers for each user
    private static readonly Dictionary<string, System.Threading.Timer> _reloadTimers = new();
    private static readonly object _timerLock = new();
    
    // Global rate limiting and status tracking per user (static to prevent multiple instances from sending)
    private static readonly Dictionary<string, bool?> _globalLastSentAckYouStatus = new();
    private static readonly Dictionary<string, DateTime> _globalLastAckYouSentTime = new();
    private static readonly Dictionary<string, bool?> _globalLastCalculatedAckYouStatus = new();
    private static readonly Dictionary<string, (bool HasPending, bool? LastSuccess, DateTime LastEventTime)> _globalLastEventState = new();
    private static readonly object _globalAckYouLock = new();
    private static readonly TimeSpan MinTimeBetweenAckYouCalls = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinTimeBetweenSameEvents = TimeSpan.FromMilliseconds(100);
    
    // Cache acknowledgment status - updated only via events
    private bool _cachedHasPendingAck = false;
    private bool _cacheInitialized = false;
    private bool _isProcessingServerUpdate = false;

    public DrawUserPair(string id, Pair entry, List<GroupFullInfoDto> syncedGroups,
        GroupFullInfoDto? currentGroup,
        ApiController apiController, IdDisplayHandler uIDDisplayHandler,
        SpheneMediator spheneMediator, SelectTagForPairUi selectTagForPairUi,
        ServerConfigurationManager serverConfigurationManager,
        UiSharedService uiSharedService, PlayerPerformanceConfigService performanceConfigService,
        CharaDataManager charaDataManager, PairManager pairManager)
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
        
        // Subscribe to acknowledgment status changes to automatically update AckYou
        _mediator.Subscribe<PairAcknowledgmentStatusChangedMessage>(this, OnAcknowledgmentStatusChanged);
        _mediator.Subscribe<AcknowledgmentPendingMessage>(this, OnAcknowledgmentPending);
        _mediator.Subscribe<AcknowledgmentUiRefreshMessage>(this, OnAcknowledgmentUiRefresh);
        
        // Subscribe to server permission updates to track when we're processing server pushes
        _mediator.Subscribe<RefreshUiMessage>(this, OnServerPermissionUpdate);
        
        // Subscribe to selective icon updates for performance optimization
        _mediator.Subscribe<UserPairIconUpdateMessage>(this, OnIconUpdate);
        
        // Initialize cache once during construction to avoid frequent PairManager calls
        _cachedHasPendingAck = _pairManager.HasPendingAcknowledgmentForUser(_pair.UserData);
        _cacheInitialized = true;
        
        // Initialize global status tracking for this user to prevent initial spam
        lock (_globalAckYouLock)
        {
            var userUID = _pair.UserData.UID;
            if (!_globalLastSentAckYouStatus.ContainsKey(userUID))
            {
                _globalLastSentAckYouStatus[userUID] = _pair.UserPair.OwnPermissions.IsAckYou();
                _globalLastAckYouSentTime[userUID] = DateTime.MinValue;
                _globalLastCalculatedAckYouStatus[userUID] = null;
            }
        }
    }
    
    public SpheneMediator Mediator => _mediator;
    
    private void OnAcknowledgmentStatusChanged(PairAcknowledgmentStatusChangedMessage message)
    {
        // Only handle events for this specific pair
        if (message.User.UID != _pair.UserData.UID) return;
        
        var userUID = _pair.UserData.UID;
        var now = DateTime.UtcNow;
        
        // Event deduplication: check if this is a duplicate event
        lock (_globalAckYouLock)
        {
            if (_globalLastEventState.TryGetValue(userUID, out var lastEvent))
            {
                // Skip if same event within short time window
                if (lastEvent.HasPending == message.HasPendingAcknowledgment &&
                    lastEvent.LastSuccess == message.LastAcknowledgmentSuccess &&
                    (now - lastEvent.LastEventTime) < MinTimeBetweenSameEvents)
                {
                    return;
                }
            }
            
            // Update last event state
            _globalLastEventState[userUID] = (message.HasPendingAcknowledgment, message.LastAcknowledgmentSuccess, now);
        }
        
        // Update cached acknowledgment status
        _cachedHasPendingAck = message.HasPendingAcknowledgment;
        _cacheInitialized = true;
        
        // Skip sending UserUpdateAckYou if we're processing a server update
        // This prevents feedback loops where server pushes trigger client requests
        if (_isProcessingServerUpdate)
        {
            return;
        }
        
        // Calculate new AckYou status based on acknowledgment state
        // When acknowledgment becomes pending (yellow clock), set AckYou to false
        // When acknowledgment succeeds (green checkmark), set AckYou to true
        // Only consider successful acknowledgments as true, everything else as false
        bool newAckYouStatus = !message.HasPendingAcknowledgment && (message.LastAcknowledgmentSuccess == true);
        
        // Get current status from permissions
        bool currentAckYouStatus = _pair.UserPair.OwnPermissions.IsAckYou();
        
        // Global rate limiting and change detection per user
        
        bool rateLimitExceeded;
        bool alreadySentThisStatus;
        bool calculatedStatusChanged;
        bool statusNeedsUpdate = currentAckYouStatus != newAckYouStatus;
        
        lock (_globalAckYouLock)
        {
            _globalLastAckYouSentTime.TryGetValue(userUID, out var lastSentTime);
            _globalLastSentAckYouStatus.TryGetValue(userUID, out var lastSentStatus);
            _globalLastCalculatedAckYouStatus.TryGetValue(userUID, out var lastCalculatedStatus);
            
            rateLimitExceeded = (now - lastSentTime) < MinTimeBetweenAckYouCalls;
            alreadySentThisStatus = lastSentStatus == newAckYouStatus;
            calculatedStatusChanged = lastCalculatedStatus != newAckYouStatus;
        }
        
        // Only send UserUpdateAckYou if:
        // 1. The calculated status is different from current permissions (actual change needed)
        // 2. We haven't already sent this exact status recently
        // 3. We're not rate limited
        // 4. The calculated status has actually changed from last calculation
        if (statusNeedsUpdate && 
            !alreadySentThisStatus &&
            !rateLimitExceeded &&
            calculatedStatusChanged)
        {
            lock (_globalAckYouLock)
            {
                _globalLastSentAckYouStatus[userUID] = newAckYouStatus;
                _globalLastCalculatedAckYouStatus[userUID] = newAckYouStatus;
                _globalLastAckYouSentTime[userUID] = now;
            }
            
            _ = Task.Run(async () =>
            {
                try
                {
                    await _apiController.UserUpdateAckYou(newAckYouStatus);
                }
                catch (Exception ex)
                {
                    // Reset the last sent status on error so we can retry after rate limit period
                    lock (_globalAckYouLock)
                    {
                        _globalLastSentAckYouStatus[userUID] = null;
                        _globalLastCalculatedAckYouStatus[userUID] = null;
                    }
                }
            });
        }
        
        // UI will automatically update when the pair data changes
    }
    
    private void OnServerPermissionUpdate(RefreshUiMessage message)
    {
        // Set flag to indicate we're processing a server update
        // This prevents OnAcknowledgmentStatusChanged from sending UserUpdateAckYou
        _isProcessingServerUpdate = true;
        
        // Reset the flag after a short delay to allow the acknowledgment events to process
        _ = Task.Run(async () =>
        {
            await Task.Delay(100); // Small delay to ensure all related events are processed
            _isProcessingServerUpdate = false;
        });
    }
    
    private void OnAcknowledgmentPending(AcknowledgmentPendingMessage message)
    {
        // Only handle events for this specific pair
        if (message.User.UID != _pair.UserData.UID) return;
        
        // Update cached acknowledgment status to pending
        _cachedHasPendingAck = true;
        _cacheInitialized = true;
    }
    
    private void OnAcknowledgmentUiRefresh(AcknowledgmentUiRefreshMessage message)
    {
        // Handle refresh for this specific pair or global refresh
        if (message.RefreshAll || 
            (message.User != null && message.User.UID == _pair.UserData.UID))
        {
            // Force cache refresh by querying current status
            _cachedHasPendingAck = _pairManager.HasPendingAcknowledgmentForUser(_pair.UserData);
            _cacheInitialized = true;
        }
    }
    
    private void OnIconUpdate(UserPairIconUpdateMessage message)
    {
        // Only handle events for this specific pair
        if (message.User.UID != _pair.UserData.UID) return;
        
        // Handle specific icon updates without full UI rebuild
        switch (message.UpdateType)
        {
            case IconUpdateType.AcknowledgmentStatus:
                if (message.UpdateData is AcknowledgmentStatusData ackData)
                {
                    _cachedHasPendingAck = ackData.HasPending;
                    _cacheInitialized = true;
                }
                break;
                
            case IconUpdateType.ConnectionStatus:
                // Connection status is read directly from pair data, no caching needed
                break;
                
            case IconUpdateType.PermissionStatus:
                // Permission status is read directly from pair data, no caching needed
                break;
                
            case IconUpdateType.IndividualPermissions:
                // Individual permissions are read directly from pair data, no caching needed
                break;
                
            case IconUpdateType.GroupRole:
                // Group role is read directly from group data, no caching needed
                break;
                
            case IconUpdateType.ReloadTimer:
                // Reload timer status is managed by static dictionaries, no caching needed
                break;
        }
        
        // Note: UI will automatically update on next frame since ImGui redraws continuously
        // No explicit redraw trigger needed for icon-only updates
    }
    
    private void HandleReloadTimer(bool isAckOther)
    {
        var userUID = _pair.UserData.UID;
        
        lock (_timerLock)
        {
            // If AckOther is true (green eye), stop any existing timer
            if (isAckOther)
            {
                if (_reloadTimers.TryGetValue(userUID, out var existingTimer))
                {
                    existingTimer.Dispose();
                    _reloadTimers.Remove(userUID);
                }
            }
            // 8-second timer temporarily disabled
            // If AckOther is false (yellow eye), start a 8-second timer
            /*
            else
            {
                // Only start timer if one doesn't already exist for this user
                if (!_reloadTimers.ContainsKey(userUID))
                {
                    var timer = new System.Threading.Timer(OnReloadTimerElapsed, userUID, TimeSpan.FromSeconds(8), Timeout.InfiniteTimeSpan);
                    _reloadTimers[userUID] = timer;
                }
            }
            */
        }
    }
    
    private void OnReloadTimerElapsed(object? state)
    {
        if (state is not string userUID) return;
        
        lock (_timerLock)
        {
            // Remove the timer from dictionary
            if (_reloadTimers.TryGetValue(userUID, out var timer))
            {
                timer.Dispose();
                _reloadTimers.Remove(userUID);
            }
        }
        
        // Only reload if this is still our pair and it's still visible with yellow eye
         if (_pair.UserData.UID == userUID && _pair.IsVisible && !_pair.UserPair.OwnPermissions.IsAckOther())
         {
             // Execute reload last received data
             _pair.ApplyLastReceivedData(forced: true);
         }
    }

    public Pair Pair => _pair;
    public UserFullPairDto UserPair => _pair.UserPair!;

    public void DrawPairedClient()
    {
        using var id = ImRaii.PushId(GetType() + _id);
        var color = ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.FrameBgHovered), _wasHovered);
        using (ImRaii.Child(GetType() + _id, new System.Numerics.Vector2(295, ImGui.GetFrameHeight()), false, ImGuiWindowFlags.NoScrollbar))
        {
            DrawLeftSide();
            ImGui.SameLine();
            var posX = ImGui.GetCursorPosX();
            var rightSide = DrawRightSide();
            DrawName(posX, rightSide);
        }
        _wasHovered = ImGui.IsItemHovered();
        color.Dispose();
    }

    private void DrawCommonClientMenu()
    {
        if (!_pair.IsPaused)
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.User, "Open Profile", _menuWidth, true))
            {
                _displayHandler.OpenProfile(_pair);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("Opens the profile for this user in a new window");
        }
        if (_pair.IsVisible)
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, "Reload last data", _menuWidth, true))
            {
                _pair.ApplyLastReceivedData(forced: true);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("This reapplies the last received character data to this character");
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, "Cycle pause state", _menuWidth, true))
        {
            _ = _apiController.CyclePauseAsync(_pair.UserData);
            ImGui.CloseCurrentPopup();
        }
        ImGui.Separator();

        ImGui.TextUnformatted("Pair Permission Functions");
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.WindowMaximize, "Open Permissions Window", _menuWidth, true))
        {
            _mediator.Publish(new OpenPermissionWindow(_pair));
            ImGui.CloseCurrentPopup();
        }
        UiSharedService.AttachToolTip("Opens the Permissions Window which allows you to manage multiple permissions at once.");

        var isSticky = _pair.UserPair!.OwnPermissions.IsSticky();
        string stickyText = isSticky ? "Disable Preferred Permissions" : "Enable Preferred Permissions";
        var stickyIcon = isSticky ? FontAwesomeIcon.ArrowCircleDown : FontAwesomeIcon.ArrowCircleUp;
        if (_uiSharedService.IconTextButton(stickyIcon, stickyText, _menuWidth, true))
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
        string disableSoundsText = isDisableSounds ? "Enable sound sync" : "Disable sound sync";
        var disableSoundsIcon = isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute;
        if (_uiSharedService.IconTextButton(disableSoundsIcon, disableSoundsText, _menuWidth, true))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetDisableSounds(!isDisableSounds);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
        }
        UiSharedService.AttachToolTip("Changes sound sync permissions with this user." + (individual ? individualText : string.Empty));

        var isDisableAnims = _pair.UserPair!.OwnPermissions.IsDisableAnimations();
        string disableAnimsText = isDisableAnims ? "Enable animation sync" : "Disable animation sync";
        var disableAnimsIcon = isDisableAnims ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop;
        if (_uiSharedService.IconTextButton(disableAnimsIcon, disableAnimsText, _menuWidth, true))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetDisableAnimations(!isDisableAnims);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
        }
        UiSharedService.AttachToolTip("Changes animation sync permissions with this user." + (individual ? individualText : string.Empty));

        var isDisableVFX = _pair.UserPair!.OwnPermissions.IsDisableVFX();
        string disableVFXText = isDisableVFX ? "Enable VFX sync" : "Disable VFX sync";
        var disableVFXIcon = isDisableVFX ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle;
        if (_uiSharedService.IconTextButton(disableVFXIcon, disableVFXText, _menuWidth, true))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetDisableVFX(!isDisableVFX);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
        }
        UiSharedService.AttachToolTip("Changes VFX sync permissions with this user." + (individual ? individualText : string.Empty));

        ImGui.Separator();
        ImGui.TextUnformatted("Performance Functions");
        
        // Check if user is already whitelisted
        var userIdentifier = !string.IsNullOrEmpty(_pair.UserData.Alias) ? _pair.UserData.Alias : _pair.UserData.UID;
        bool isWhitelisted = _performanceConfigService.Current.UIDsToIgnore.Contains(userIdentifier, StringComparer.Ordinal) ||
                            _performanceConfigService.Current.UIDsToIgnore.Contains(_pair.UserData.UID, StringComparer.Ordinal);
        
        if (!isWhitelisted)
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Shield, "Add to Performance Whitelist", _menuWidth, true))
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
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ShieldAlt, "Remove from Performance Whitelist", _menuWidth, true))
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

    public void Dispose()
    {
        // Unsubscribe from all events to prevent memory leaks and duplicate event handling
        _mediator.Unsubscribe<PairAcknowledgmentStatusChangedMessage>(this);
        _mediator.Unsubscribe<AcknowledgmentPendingMessage>(this);
        
        // Clean up any active reload timers for this user
        lock (_timerLock)
        {
            if (_reloadTimers.TryGetValue(_pair.UserData.UID, out var timer))
            {
                timer.Dispose();
                _reloadTimers.Remove(_pair.UserData.UID);
            }
        }
        
        // Clean up global tracking data for this user
        lock (_globalAckYouLock)
        {
            _globalLastSentAckYouStatus.Remove(_pair.UserData.UID);
            _globalLastAckYouSentTime.Remove(_pair.UserData.UID);
            _globalLastCalculatedAckYouStatus.Remove(_pair.UserData.UID);
            _globalLastEventState.Remove(_pair.UserData.UID);
        }
    }

    private void DrawIndividualMenu()
    {
        ImGui.TextUnformatted("Individual Pair Functions");
        var entryUID = _pair.UserData.AliasOrUID;

        if (_pair.IndividualPairStatus != API.Data.Enum.IndividualPairStatus.None)
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Folder, "Pair Groups", _menuWidth, true))
            {
                _selectTagForPairUi.Open(_pair);
            }
            UiSharedService.AttachToolTip("Choose pair groups for " + entryUID);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Unpair Permanently", _menuWidth, true) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.UserRemovePair(new(_pair.UserData));
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to unpair permanently from " + entryUID);
        }
        else
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, "Pair individually", _menuWidth, true))
            {
                _ = _apiController.UserAddPair(new(_pair.UserData));
            }
            UiSharedService.AttachToolTip("Pair individually with " + entryUID);
        }
    }

    private void DrawLeftSide()
    {
        string userPairText = string.Empty;

        ImGui.AlignTextToFramePadding();

        if (_pair.IsPaused)
        {
            using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            _uiSharedService.IconText(FontAwesomeIcon.PauseCircle);
            userPairText = _pair.UserData.AliasOrUID + " is paused";
        }
        else if (!_pair.IsOnline)
        {
            using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            _uiSharedService.IconText(_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.OneSided
                ? FontAwesomeIcon.ArrowsLeftRight
                : (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional
                    ? FontAwesomeIcon.User : FontAwesomeIcon.Users));
            userPairText = _pair.UserData.AliasOrUID + " is offline";
        }
        else if (_pair.IsVisible)
        {
            // Show eye icon with color based on AckOther status
            var isAckOther = _pair.UserPair.OwnPermissions.IsAckOther();
            var eyeColor = isAckOther ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudYellow;
            _uiSharedService.IconText(FontAwesomeIcon.Eye, eyeColor);
            var ackStatus = isAckOther ? "acknowledges your data" : "does not acknowledge your data";
            userPairText = _pair.UserData.AliasOrUID + " is visible: " + _pair.PlayerName + Environment.NewLine + "This user " + ackStatus + Environment.NewLine + "Click to target this player";
            
            // Handle reload timer based on AckOther status
            HandleReloadTimer(isAckOther);
            
            if (ImGui.IsItemClicked())
            {
                _mediator.Publish(new TargetPairMessage(_pair));
            }
        }
        else
        {
            using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
            _uiSharedService.IconText(_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional
                ? FontAwesomeIcon.User : FontAwesomeIcon.Users);
            userPairText = _pair.UserData.AliasOrUID + " is online";
        }

        // Add synchronization status indicator - only show for visible pairs
        if (_pair.IsOnline && _pair.IsVisible)
        {
            ImGui.SameLine();
            // Check if sender is waiting for acknowledgment from this specific user
            // Only show indicator for real Penumbra changes (with acknowledgment ID), not for build start status
            // Show clock only if the pair itself has a pending acknowledgment (not just any acknowledgment for this user)
            if (_pair.HasPendingAcknowledgment && !string.IsNullOrEmpty(_pair.LastAcknowledgmentId))
            {
                using var _ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                _uiSharedService.IconText(FontAwesomeIcon.Clock);
                UiSharedService.AttachToolTip("Waiting for acknowledgment from this user...");
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
                    UiSharedService.AttachToolTip($"Data synchronization failed{timeAgo}");
                }
            }
        }

        if (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.OneSided)
        {
            userPairText += UiSharedService.TooltipSeparator + "User has not added you back";
        }
        else if (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional)
        {
            userPairText += UiSharedService.TooltipSeparator + "You are directly Paired";
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
        if (_pair.IsOnline && _pair.IsVisible)
        {
            // Only show sync status for real Penumbra changes (with acknowledgment ID), not for build start status
            if (GetCachedHasPendingAcknowledgment() && !string.IsNullOrEmpty(_pair.LastAcknowledgmentId))
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

        UiSharedService.AttachToolTip(userPairText);

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
        var barButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.EllipsisV);
        var reloadButtonSize = _pair.IsVisible ? _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Sync) : Vector2.Zero;
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var windowEndX = ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth();
        float currentRightSide = windowEndX - barButtonSize.X;

        // Context menu button (rightmost)
        ImGui.SameLine(currentRightSide);
        ImGui.AlignTextToFramePadding();
        if (_uiSharedService.IconButton(FontAwesomeIcon.EllipsisV))
        {
            ImGui.OpenPopup("User Flyout Menu");
        }

        // Reload button (only if pair is visible)
        if (_pair.IsVisible)
        {
            currentRightSide -= (reloadButtonSize.X + spacingX);
            ImGui.SameLine(currentRightSide);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Sync))
            {
                _pair.ApplyLastReceivedData(forced: true);
            }
            UiSharedService.AttachToolTip("Reload last received character data");
        }

        // Pause/Play button (leftmost of the three)
        currentRightSide -= (pauseButtonSize.X + spacingX);
        ImGui.SameLine(currentRightSide);
        if (_uiSharedService.IconButton(pauseIcon))
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

        return currentRightSide - spacingX;
    }

    private void DrawSyncshellMenu(GroupFullInfoDto group, bool selfIsOwner, bool selfIsModerator, bool userIsPinned, bool userIsModerator)
    {
        if (selfIsOwner || ((selfIsModerator) && (!userIsModerator)))
        {
            ImGui.TextUnformatted("Syncshell Moderator Functions");
            var pinText = userIsPinned ? "Unpin user" : "Pin user";
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Thumbtack, pinText, _menuWidth, true))
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

            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Remove user", _menuWidth, true) && UiSharedService.CtrlPressed())
            {
                ImGui.CloseCurrentPopup();
                _ = _apiController.GroupRemoveUser(new(group.Group, _pair.UserData));
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to remove user " + (_pair.UserData.AliasOrUID) + " from Syncshell");

            if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserSlash, "Ban User", _menuWidth, true))
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
            string modText = userIsModerator ? "Demod user" : "Mod user";
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserShield, modText, _menuWidth, true) && UiSharedService.CtrlPressed())
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

            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Crown, "Transfer Ownership", _menuWidth, true) && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
            {
                ImGui.CloseCurrentPopup();
                _ = _apiController.GroupChangeOwnership(new(group.Group, _pair.UserData));
            }
        }
    }
    
    private bool GetCachedHasPendingAcknowledgment()
    {
        // Return cached value without initializing from PairManager to avoid frequent calls
        // Cache is updated only through event handlers
        return _cachedHasPendingAck;
    }

    public void RefreshIcon()
    {
        // Force refresh of cached icon data without recreating the entire DrawUserPair instance
        // This method is called when only icons need to be updated
        // The actual icon rendering will pick up the latest data on next draw
    }
}
