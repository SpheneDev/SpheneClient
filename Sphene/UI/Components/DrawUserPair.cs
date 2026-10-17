using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
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
using Sphene.UI.Design;
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
    private readonly SpheneProfileManager _spheneProfileManager;
    private byte[] _avatarBytes = [];
    private byte[] _rowImageBytes = [];
    private byte[] _bannerBytes = [];
    private float _menuWidth = -1;
    private bool _wasHovered = false;
    private bool _avatarHovered = false;
    private static readonly Vector4 SoftToggleEnabledColor = new(0.56f, 0.82f, 0.62f, 1f);
    private static readonly Vector4 SoftToggleDisabledColor = new(0.86f, 0.58f, 0.58f, 1f);

    private readonly List<FileTransferNotificationDto> _pendingModNotifications = new();
    public DrawUserPair(string id, Pair entry, List<GroupFullInfoDto> syncedGroups,
        GroupFullInfoDto? currentGroup,
        ApiController apiController, IdDisplayHandler uIDDisplayHandler,
        SpheneMediator spheneMediator, SelectTagForPairUi selectTagForPairUi,
        ServerConfigurationManager serverConfigurationManager,
        UiSharedService uiSharedService, PlayerPerformanceConfigService performanceConfigService,
        CharaDataManager charaDataManager, PairManager pairManager, SpheneConfigService configService,
        LocalPairEmoteForceSyncService localPairEmoteForceSyncService, SpheneProfileManager spheneProfileManager)
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
        _spheneProfileManager = spheneProfileManager;
        _mediator.Subscribe<PenumbraModTransferAvailableMessage>(this, OnPenumbraModTransferAvailable);
        _mediator.Subscribe<PenumbraModTransferCompletedMessage>(this, OnPenumbraModTransferCompleted);
        _mediator.Subscribe<PenumbraModTransferDiscardedMessage>(this, OnPenumbraModTransferDiscarded);
    }
    
    public SpheneMediator Mediator => _mediator;
    
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

    public Pair Pair => _pair;
    public UserFullPairDto UserPair => _pair.UserPair!;

    public void DrawPairedClient()
    {
        using var id = ImRaii.PushId(GetType() + _id);
        var hasOfflineGrace = _pairManager.IsUserInOfflineGrace(_pair.UserData);
        // Don't show grace highlight if player is offline due to being paused by other
        var isPausedByOther = _pair.UserPair?.OtherPermissions.IsPaused() ?? false;
        if (isPausedByOther && !_pair.IsOnline)
        {
            hasOfflineGrace = false;
        }
        var hasTimingSyncTarget = _localPairEmoteForceSyncService.IsTargetSelected(_pair.UserData.UID);
        var hasTimingLeader = _localPairEmoteForceSyncService.IsLeader(_pair.UserData.UID);
        var rowTheme = SpheneTheme.Current;
        var color = ImRaii.PushColor(ImGuiCol.ChildBg,
            ImGui.ColorConvertFloat4ToU32(rowTheme.CompactPairRowHover), _wasHovered);
        var baseFolderWidth = SpheneUI.Controls.BoundedAvailX();
        var rowHeight = ImGui.GetFrameHeight() + 6f * SpheneUI.Scale;
        var rowRounding = rowTheme.CompactPairRowRounding < 0f
            ? rowTheme.CompactChildRounding
            : rowTheme.CompactPairRowRounding;
        using var roundingScope = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, rowRounding);
        using (ImRaii.Child(GetType() + _id, new System.Numerics.Vector2(baseFolderWidth, rowHeight), false, ImGuiWindowFlags.NoScrollbar))
        {
            var drawList = ImGui.GetWindowDrawList();
            var rowMin = ImGui.GetWindowPos();
            var rowMax = rowMin + new Vector2(baseFolderWidth, rowHeight);
            // the row silhouette is the child bg — backdrop and gradients must
            // share that radius or their corner arcs drift from the edge
            var rowRadius = MathF.Min(rowRounding, rowHeight * 0.5f);

            // the pair's own list-row styling: theme effects plus the
            // supporter row image strip, identical to the hub friends list
            var profile = _spheneProfileManager.GetSpheneProfile(_pair.UserData);
            var profileTheme = profile.ProfileTheme.Value;
            var accent = profileTheme.AccentColor(SpheneUI.Palette.SpheneGold);
            var hoverT = SpheneUI.Fx.HoverT(ImGui.GetID("##pairrowfx"), _wasHovered);
            var rowImage = _pair.UserData.IsSupporter && profile.RowImageData.Value.Length > 0
                ? PairRowImage(profile)
                : null;
            if (profileTheme.RowHover != ProfileRowFx.None || profileTheme.RowIdle != ProfileRowFx.None
                || rowImage != null)
            {
                SpheneUI.Profile.RowBackdrop(drawList, rowMin, rowMax, accent, _wasHovered, hoverT,
                    profileTheme.RowHover, profileTheme.RowIdle, rowImage, rowRadius);
            }

            if (hasTimingSyncTarget)
            {
                var rowStart = ImGui.GetCursorScreenPos();
                var gradientWidth = baseFolderWidth * 0.42f;
                var rowEnd = new Vector2(rowStart.X + gradientWidth, rowStart.Y + rowHeight);
                var startColor = new Vector4(0.60f, 0.34f, 1.0f, _wasHovered ? 0.42f : 0.30f);
                var endColor = new Vector4(0.60f, 0.34f, 1.0f, 0.0f);
                DrawRowGradient(rowStart, rowEnd, startColor, endColor, rowRadius);
            }

            if (hasTimingLeader)
            {
                var rowStart = ImGui.GetCursorScreenPos();
                var gradientWidth = baseFolderWidth * 0.50f;
                var rowEnd = new Vector2(rowStart.X + gradientWidth, rowStart.Y + rowHeight);
                var startColor = new Vector4(1.0f, 0.30f, 0.76f, _wasHovered ? 0.48f : 0.34f);
                var endColor = new Vector4(1.0f, 0.30f, 0.76f, 0.0f);
                DrawRowGradient(rowStart, rowEnd, startColor, endColor, rowRadius);
            }

            DrawLeftSide(profile, accent, rowHeight, hasOfflineGrace);
            ImGui.SameLine();
            var posX = ImGui.GetCursorPosX();
            DrawRightSide();
            DrawName(posX);
        }
        _wasHovered = ImGui.IsItemHovered();
        // a right-click anywhere on the row activates the profile popout; the
        // child-item hit test misses inner items, so the rect test covers
        // name/buttons/empty space — the avatar keeps its tooltip and is excluded
        var rowHovered = _wasHovered
            || (ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax())
                && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows));
        if (rowHovered && !_avatarHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _displayHandler.PairRowActivated(_pair);
        }
        color.Dispose();
    }

    /// <summary>Pair's cached avatar texture — reloads from the media cache when the profile bytes change.</summary>
    private IDalamudTextureWrap? PairAvatar(SpheneProfileData profile)
    {
        var data = profile.ImageData.Value;
        var key = $"compact:avatar:{_pair.UserData.UID}";
        if (!ReferenceEquals(_avatarBytes, data) && !_avatarBytes.AsSpan().SequenceEqual(data))
        {
            _avatarBytes = data;
            SpheneUI.Media.Evict(key);
        }
        return data.Length == 0 ? null : SpheneUI.Media.Texture(key, data);
    }

    /// <summary>Pair's cached banner texture for the tooltip's profile strip.</summary>
    private IDalamudTextureWrap? PairBanner(SpheneProfileData profile)
    {
        var data = profile.BannerImageData.Value;
        var key = $"compact:banner:{_pair.UserData.UID}";
        if (!ReferenceEquals(_bannerBytes, data) && !_bannerBytes.AsSpan().SequenceEqual(data))
        {
            _bannerBytes = data;
            SpheneUI.Media.Evict(key);
        }
        return data.Length == 0 ? null : SpheneUI.Media.Texture(key, data);
    }

    /// <summary>Pair's cached row strip image (supporter perk) for the row backdrop.</summary>
    private SpheneUI.Media.AnimatedImage? PairRowImage(SpheneProfileData profile)
    {
        var data = profile.RowImageData.Value;
        var key = $"compact:row:{_pair.UserData.UID}";
        if (!ReferenceEquals(_rowImageBytes, data) && !_rowImageBytes.AsSpan().SequenceEqual(data))
        {
            _rowImageBytes = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Animated(key, data);
    }

    private static void DrawRowGradient(Vector2 rowStart, Vector2 rowEnd, Vector4 startColor, Vector4 endColor, float rounding)
    {
        var drawList = ImGui.GetWindowDrawList();
        var width = rowEnd.X - rowStart.X;
        // the corner slice must be at least twice the row radius wide or its
        // own clamped arc falls short of the silhouette's curve
        var firstW = MathF.Min(width, MathF.Max(8f * SpheneUI.Scale, rounding * 2f));
        var restSlices = Math.Clamp((int)((width - firstW) / 8.0f), 8, 64);
        var restStep = (width - firstW) / restSlices;
        for (int i = 0; i <= restSlices; i++)
        {
            var x0 = i == 0 ? rowStart.X : rowStart.X + firstW + restStep * (i - 1);
            var x1 = i == restSlices ? rowEnd.X : x0 + (i == 0 ? firstW : restStep);
            var gradientColor = Vector4.Lerp(startColor, endColor, (x0 - rowStart.X) / width);
            var flags = i == 0 ? ImDrawFlags.RoundCornersLeft
                : i == restSlices ? ImDrawFlags.RoundCornersRight : ImDrawFlags.None;
            var cornerRadius = flags == ImDrawFlags.None ? 0.0f : rounding;
            drawList.AddRectFilled(new Vector2(x0, rowStart.Y), new Vector2(x1, rowEnd.Y), ImGui.ColorConvertFloat4ToU32(gradientColor), cornerRadius, flags);
        }
    }

    private void DrawCommonClientMenu()
    {
        if (!_pair.IsPaused)
        {
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.User, "Open Profile", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                _displayHandler.OpenProfile(_pair);
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip("Opens the profile for this user in a new window");
        }

        var canSendPenumbraMod = _pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional
                                 && _pair.OtherAllowsReceivingPenumbraMods;
        using (ImRaii.Disabled(!canSendPenumbraMod))
        {
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.BoxOpen, "Send Penumbra Mod", _menuWidth, ButtonStyleKeys.ContextMenu_Item) && canSendPenumbraMod)
            {
                _mediator.Publish(new OpenSendPenumbraModWindow(_pair));
                ImGui.CloseCurrentPopup();
            }
        }
        SpheneUI.AttachTooltip(_pair.OtherAllowsReceivingPenumbraMods
            ? "Opens a dialog to send a Penumbra mod package to this user. Available only for online, individually paired users."
            : "This user has disabled receiving Penumbra mod packages. You cannot send mods to them.");

        var pendingCount = _pendingModNotifications.Count;
        var hasPendingModTransfer = pendingCount > 0;
        using (ImRaii.Disabled(!hasPendingModTransfer))
        {
            var text = pendingCount > 1 ? $"Install received Penumbra Mods ({pendingCount})" : "Install received Penumbra Mod";
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Download, text, _menuWidth, ButtonStyleKeys.ContextMenu_Item) && hasPendingModTransfer)
            {
                _mediator.Publish(new OpenPenumbraReceiveModWindow(_pendingModNotifications.ToList()));
                ImGui.CloseCurrentPopup();
            }
        }
        SpheneUI.AttachTooltip(hasPendingModTransfer
            ? "Opens the panel to install the received Penumbra mod package from this user."
            : "No pending Penumbra mod package available from this user.");

        if (_pair.IsVisible)
        {
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Sync, "Reload last data", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                var requestToken = _pair.RequestForcedRedrawOnNextCharacterReload();
                _ = _apiController.UserRequestCharacterDataRefresh(new(_pair.UserData));
                _ = _pair.ApplyLastKnownDataIfReloadPendingAsync(requestToken);
                _mediator.Publish(new NotificationMessage("Character data refresh", $"Requested a fresh sync from {_pair.UserData.PreferredName}", Sphene.SpheneConfiguration.Models.NotificationType.Info, TimeSpan.FromSeconds(2)));
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip("Requests a fresh character data sync from this user.");
        }

        if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.PlayCircle, "Cycle pause state", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            _ = _apiController.CyclePauseAsync(_pair.UserData);
            ImGui.CloseCurrentPopup();
        }
        
        var emoteSyncEnabled =
#if IS_TEST_BUILD
            true;
#else
            false;
#endif


        if (emoteSyncEnabled)
        {
        SpheneUI.Layout.Separator();
        SpheneUI.Text.Plain("Emote Sync");

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
                $"{_pair.UserData.PreferredName} was {statusText} local timing-sync targets.{exclusivityInfo}",
                Sphene.SpheneConfiguration.Models.NotificationType.Info,
                TimeSpan.FromSeconds(3)));
        }
        SpheneUI.AttachTooltip("Marks this pair as a local timing-sync target. A pair cannot be both timing leader and target.");

        var isLeader = _localPairEmoteForceSyncService.IsLeader(_pair.UserData.UID);
        var leaderIcon = isLeader ? FontAwesomeIcon.Crown : FontAwesomeIcon.User;
        if (DrawStateColoredToggleButton(leaderIcon, "Set as timing leader", isLeader))
        {
            var wasTargetBefore = _localPairEmoteForceSyncService.IsTargetSelected(_pair.UserData.UID);
            var isLeaderNow = _localPairEmoteForceSyncService.ToggleLeader(_pair.UserData.UID);
            _mediator.Publish(new NotificationMessage(
                "Local Timing Sync",
                isLeaderNow
                    ? $"{_pair.UserData.PreferredName} is now your local timing leader. Auto reset on your emote was disabled.{(wasTargetBefore ? " This pair was removed from timing-sync targets." : string.Empty)}"
                    : $"{_pair.UserData.PreferredName} is no longer your local timing leader.",
                Sphene.SpheneConfiguration.Models.NotificationType.Info,
                TimeSpan.FromSeconds(3)));
        }
        SpheneUI.AttachTooltip("When this leader triggers an emote update, your timing and selected target timing are reset to 0. Setting a leader disables auto reset on your emote and removes this pair from targets.");

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
        SpheneUI.AttachTooltip("Enables or disables automatic timing follow when your selected leader emotes.");

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
        SpheneUI.AttachTooltip("When enabled, selected visible targets are reset to animation time 0 whenever your emote state updates. Enabling this clears the timing leader.");

        var selectedCount = _localPairEmoteForceSyncService.GetSelectedTargetCount();
        using (ImRaii.Disabled(selectedCount <= 0))
        {
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Sync, "Reset emote timing for selected", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
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
        
        SpheneUI.AttachTooltip(selectedCount > 0
            ? $"Resets current animation time to 0 for you and {selectedCount} selected mutually visible target(s), without changing which emote each player is using."
            : "Select at least one target to reset emote timing.");
        SpheneUI.Layout.Separator();
        }

        SpheneUI.Text.Plain("Pair Permission Functions");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.WindowMaximize, "Open Permissions Window", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            _mediator.Publish(new OpenPermissionWindow(_pair));
            ImGui.CloseCurrentPopup();
        }
        SpheneUI.AttachTooltip("Opens the Permissions Window which allows you to manage multiple permissions at once.");

        var isSticky = _pair.UserPair!.OwnPermissions.IsSticky();
        string stickyText = "Preferred Permissions";
        var stickyIcon = isSticky ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff;
        if (DrawStateColoredToggleButton(stickyIcon, stickyText, isSticky))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetSticky(!isSticky);
            _ = _apiController.UserSetPairPermissions(new(_pair.UserData, permissions));
        }
        SpheneUI.AttachTooltip("Preferred permissions means that this pair will not" + Environment.NewLine + " be affected by any syncshell permission changes through you.");

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
        SpheneUI.AttachTooltip("Changes sound sync permissions with this user." + (individual ? individualText : string.Empty));

        var isDisableAnims = _pair.UserPair!.OwnPermissions.IsDisableAnimations();
        string disableAnimsText = "Animation sync";
        var disableAnimsIcon = isDisableAnims ? FontAwesomeIcon.ToggleOff : FontAwesomeIcon.ToggleOn;
        if (DrawStateColoredToggleButton(disableAnimsIcon, disableAnimsText, !isDisableAnims))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetDisableAnimations(!isDisableAnims);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
        }
        SpheneUI.AttachTooltip("Changes animation sync permissions with this user." + (individual ? individualText : string.Empty));

        var isDisableVFX = _pair.UserPair!.OwnPermissions.IsDisableVFX();
        string disableVFXText = "VFX sync";
        var disableVFXIcon = isDisableVFX ? FontAwesomeIcon.ToggleOff : FontAwesomeIcon.ToggleOn;
        if (DrawStateColoredToggleButton(disableVFXIcon, disableVFXText, !isDisableVFX))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetDisableVFX(!isDisableVFX);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
        }
        SpheneUI.AttachTooltip("Changes VFX sync permissions with this user." + (individual ? individualText : string.Empty));

        var isDisableVfxInDuty = _pair.UserPair!.OwnPermissions.IsDisableVFXInDuty();
        string disableVfxInDutyText = "VFX sync in duty";
        var disableVfxInDutyIcon = isDisableVfxInDuty ? FontAwesomeIcon.ToggleOff : FontAwesomeIcon.ToggleOn;
        if (DrawStateColoredToggleButton(disableVfxInDutyIcon, disableVfxInDutyText, !isDisableVfxInDuty))
        {
            var permissions = _pair.UserPair.OwnPermissions;
            permissions.SetDisableVFXInDuty(!isDisableVfxInDuty);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
        }
        SpheneUI.AttachTooltip("When enabled, VFX files from this user will be removed while you are in duty." + (individual ? individualText : string.Empty));

        SpheneUI.Layout.Separator();
        SpheneUI.Text.Plain("Performance Functions");
        
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
            SpheneUI.AttachTooltip("Adds this user to the performance whitelist to ignore all performance warnings and auto-pause operations." + Environment.NewLine +
                                        "Will use: " + _pair.UserData.AliasOrUID);
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
            SpheneUI.AttachTooltip("Removes this user from the performance whitelist.");
        }
    }

    private bool DrawStateColoredToggleButton(FontAwesomeIcon icon, string text, bool enabledState)
    {
        using var _ = ImRaii.PushColor(ImGuiCol.Text, enabledState ? SoftToggleEnabledColor : SoftToggleDisabledColor);
        return SpheneUI.Icons.ActionButton(icon, text, _menuWidth, ButtonStyleKeys.ContextMenu_Item);
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _mediator.Unsubscribe<PenumbraModTransferAvailableMessage>(this);
            _mediator.Unsubscribe<PenumbraModTransferCompletedMessage>(this);
            _mediator.Unsubscribe<PenumbraModTransferDiscardedMessage>(this);
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
        SpheneUI.Text.Plain("Individual Pair Functions");
        var entryName = _pair.UserData.PreferredName;

        if (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional)
        {
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Folder, "Pair Groups", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                _selectTagForPairUi.Open(_pair);
            }
            SpheneUI.AttachTooltip("Choose pair groups for " + entryName);
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Trash, "Unpair Permanently", _menuWidth, ButtonStyleKeys.ContextMenu_Item) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.UserRemovePair(new(_pair.UserData));
            }
            SpheneUI.AttachTooltip("Hold CTRL and click to unpair permanently from " + entryName);
        }
        else if (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.OneSided)
        {
            if (!_pair.UserPair.IsOutgoingIndividualPair && SpheneUI.Icons.ActionButton(FontAwesomeIcon.Plus, "Pair individually", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                _ = _apiController.UserAddPair(new(_pair.UserData));
            }
            if (!_pair.UserPair.IsOutgoingIndividualPair)
            {
                SpheneUI.AttachTooltip("Complete one-sided pairing with " + entryName);
            }

            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Trash, "Unpair Permanently", _menuWidth, ButtonStyleKeys.ContextMenu_Item) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.UserRemovePair(new(_pair.UserData));
            }
            SpheneUI.AttachTooltip("Hold CTRL and click to unpair permanently from " + entryName);
        }
        else
        {
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Plus, "Pair individually", _menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                _ = _apiController.UserAddPair(new(_pair.UserData));
            }
            SpheneUI.AttachTooltip("Pair individually with " + entryName);
        }
    }

    private void DrawLeftSide(SpheneProfileData profile, Vector4 accent, float rowHeight, bool hasOfflineGrace)
    {
        var tooltipTitle = _pair.UserData.PreferredName;
        string? tooltipMeta = null;
        var tooltipLines = new List<TooltipSpecRow>(8);

        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var contentMin = ImGui.GetCursorScreenPos();
        var rowCenterY = contentMin.Y + rowHeight * 0.5f;

        var isVisibleForIcon = _pair.IsMutuallyVisible || (_uiSharedService.IsInGpose && _pair.WasMutuallyVisibleInGpose);
        var suppressAckUi = _pair.IsInDuty;
        var isTimingSyncTarget = _localPairEmoteForceSyncService.IsTargetSelected(_pair.UserData.UID);
        var isAutoResetActiveForTargets = _localPairEmoteForceSyncService.AutoResetOnLocalEmoteEnabled;

        // the avatar ring colors by sync state: incoming pending (their change
        // still applying on this client) pulses yellow, any failed direction
        // reads red — outgoing pending stays green since the icon covers it
        var (incomingStateForIcon, outgoingStateForIcon) = _pair.GetCombinedAckV3State();

        var showPerfWarning = _performanceConfigService.Current.ShowPerformanceIndicator
            && !_performanceConfigService.Current.UIDsToIgnore
                .Exists(uid => string.Equals(uid, UserPair.User.Alias, StringComparison.Ordinal) || string.Equals(uid, UserPair.User.UID, StringComparison.Ordinal))
            && ((_performanceConfigService.Current.VRAMSizeWarningThresholdMiB > 0 && _performanceConfigService.Current.VRAMSizeWarningThresholdMiB * 1024 * 1024 < _pair.LastAppliedApproximateVRAMBytes)
                || (_performanceConfigService.Current.TrisWarningThresholdThousands > 0 && _performanceConfigService.Current.TrisWarningThresholdThousands * 1000 < _pair.LastAppliedDataTris))
            && (!_pair.UserPair.OwnPermissions.IsSticky()
                || _performanceConfigService.Current.WarnOnPreferredPermissionsExceedingThresholds);

        // grace period reads as an orange dot — "offline, but may return" —
        // instead of the former full-row orange highlight. The pair keeps its
        // CachedPlayer during grace so IsOnline stays true the whole window.
        var offlineGrace = hasOfflineGrace;
        var presenceText = _pair.IsPaused ? "Paused"
            : offlineGrace ? "Offline (grace)"
            : !_pair.IsOnline ? "Offline"
            : isVisibleForIcon ? (string.IsNullOrWhiteSpace(_pair.PlayerName) ? "Visible" : $"Visible: {_pair.PlayerName}")
            : _pair.IsInGpose ? "GPose (no data shared)"
            : "Online";
        var presenceColor = _pair.IsPaused ? ImGuiColors.DalamudYellow
            : offlineGrace ? new Vector4(1.0f, 0.66f, 0.26f, 1f)
            : !_pair.IsOnline ? ImGuiColors.DalamudRed
            : isVisibleForIcon ? ImGuiColors.ParsedGreen
            : ImGuiColors.HealerGreen;

        // compact presence block: the avatar carries online state (dot,
        // bottom-right), mutual visibility (ring) and gpose context (camera
        // pip, bottom-left) — one slot instead of a row of state icons
        var avatarSize = MathF.Max(12f * scale, rowHeight - 4f * scale);
        var avatarPos = contentMin + new Vector2(
            SpheneTheme.Current.CompactPairRowContentPadding + 1f * scale,
            MathF.Max(0f, (rowHeight - avatarSize) * 0.5f));
        ImGui.SetCursorScreenPos(avatarPos);
        var avatarWrap = PairAvatar(profile);
        SpheneUI.Media.Avatar(avatarWrap, avatarSize);
        var showUserPairTooltip = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        _avatarHovered = showUserPairTooltip;
        if (ImGui.IsItemClicked() && isVisibleForIcon)
        {
            _mediator.Publish(new TargetPairMessage(_pair));
        }

        var avatarCenter = avatarPos + new Vector2(avatarSize * 0.5f);
        // ring = visibility + sync state: accent while the pair isn't sharing
        // data; once mutually visible it colors by acknowledgment outcome —
        // green synced, yellow while their change still applies here, red failed
        var ringAlpha = 0.30f;
        var ringWidth = 1f;
        var ringColor = accent;
        if (_pair.IsOnline && isVisibleForIcon && !suppressAckUi)
        {
            var syncFailed = incomingStateForIcon.Outcome == Pair.AckV3Outcome.Fail
                || outgoingStateForIcon.Outcome == Pair.AckV3Outcome.Fail;
            var applyingTheirChange = incomingStateForIcon.Outcome == Pair.AckV3Outcome.Pending;
            ringColor = syncFailed ? ImGuiColors.DalamudRed
                : applyingTheirChange ? ImGuiColors.DalamudYellow
                : ImGuiColors.ParsedGreen;
            ringAlpha = applyingTheirChange
                ? 0.55f + 0.35f * MathF.Sin((float)ImGui.GetTime() * 4f)
                : 0.9f;
            ringWidth = syncFailed ? 1.8f : 1.4f;
        }
        else if (isVisibleForIcon)
        {
            ringColor = ImGuiColors.ParsedGreen;
            ringAlpha = 0.9f;
            ringWidth = 1.4f;
        }
        drawList.AddCircle(avatarCenter, avatarSize * 0.5f - 0.5f * scale,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(ringColor, ringAlpha)), 0,
            MathF.Max(1f, ringWidth * scale));
        SpheneUI.Media.PresenceDot(drawList, avatarPos, avatarSize, presenceColor);

        var gposeContext = _pair.IsOnline
            && (_pair.IsInGpose || (_uiSharedService.IsInGpose && isVisibleForIcon));
        if (gposeContext)
        {
            var pipRadius = MathF.Max(4.5f * scale, avatarSize * 0.16f);
            var pipCenter = avatarPos + new Vector2(avatarSize * 0.1464f, avatarSize * 0.8536f);
            drawList.AddCircleFilled(pipCenter, pipRadius + 1.5f * scale,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.Zero, 0.75f)));
            var camGlyph = FontAwesomeIcon.Camera.ToIconString();
            using (SpheneUI.Icons.PushFont())
            {
                ImGui.SetWindowFontScale(0.62f);
                var glyphSize = ImGui.CalcTextSize(camGlyph);
                drawList.AddText(pipCenter - glyphSize * 0.5f,
                    SpheneUI.ColorUtils.ToU32(ImGuiColors.DalamudWhite), camGlyph);
                ImGui.SetWindowFontScale(1f);
            }
        }

        // outgoing sync rides as a tiny top-right pip in the camera pip's
        // language — only attention states get one: a synced pair already
        // reads green via the ring and needs no extra chrome
        if (_pair.IsOnline && _pair.IsMutuallyVisible && !suppressAckUi
            && outgoingStateForIcon.Outcome is Pair.AckV3Outcome.Pending or Pair.AckV3Outcome.Fail)
        {
            var syncPending = outgoingStateForIcon.Outcome == Pair.AckV3Outcome.Pending;
            var pipRadius = MathF.Max(4.5f * scale, avatarSize * 0.16f);
            var pipCenter = avatarPos + new Vector2(avatarSize * 0.8536f, avatarSize * 0.1464f);
            var pipColor = syncPending ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudRed;
            var pipAlpha = syncPending
                ? 0.60f + 0.30f * MathF.Sin((float)ImGui.GetTime() * 4f)
                : 0.9f;
            drawList.AddCircleFilled(pipCenter, pipRadius + 1.5f * scale,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.Zero, 0.75f)));
            var syncGlyph = (syncPending ? FontAwesomeIcon.Clock : FontAwesomeIcon.ExclamationTriangle).ToIconString();
            using (SpheneUI.Icons.PushFont())
            {
                ImGui.SetWindowFontScale(0.52f);
                var glyphSize = ImGui.CalcTextSize(syncGlyph);
                drawList.AddText(pipCenter - glyphSize * 0.5f,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(pipColor, pipAlpha)), syncGlyph);
                ImGui.SetWindowFontScale(1f);
            }
        }


        // syncshell-only membership rides as a tiny top-left link pip in the
        // avatar chrome's language — a synced bidirectional pair needs no extra
        // marker since the green ring already reads "connected"
        if (isVisibleForIcon && _pair.IsOnline
            && _pair.UserPair.Groups.Any()
            && _pair.IndividualPairStatus != API.Data.Enum.IndividualPairStatus.Bidirectional)
        {
            var linkPipRadius = MathF.Max(4.5f * scale, avatarSize * 0.16f);
            var linkPipCenter = avatarPos + new Vector2(avatarSize * 0.1464f, avatarSize * 0.1464f);
            drawList.AddCircleFilled(linkPipCenter, linkPipRadius + 1.5f * scale,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.Zero, 0.75f)));
            var linkGlyph = FontAwesomeIcon.Link.ToIconString();
            using (SpheneUI.Icons.PushFont())
            {
                ImGui.SetWindowFontScale(0.5f);
                var linkGlyphSize = ImGui.CalcTextSize(linkGlyph);
                drawList.AddText(linkPipCenter - linkGlyphSize * 0.5f,
                    SpheneUI.ColorUtils.ToU32(ImGuiColors.ParsedGreen), linkGlyph);
                ImGui.SetWindowFontScale(1f);
            }
        }

        // status glyphs sit on an explicit X cursor drawn vertically centered
        // on the row — icon-font baseline metrics otherwise offset them from
        // the name's text line and the avatar
        var glyphGap = ImGui.GetStyle().ItemSpacing.X;
        var iconsX = avatarPos.X + avatarSize + glyphGap;



        if (isTimingSyncTarget && isAutoResetActiveForTargets)
        {
            iconsX = DrawCenteredGlyph(iconsX, rowCenterY, FontAwesomeIcon.PersonCircleCheck,
                ImGuiColors.DalamudYellow,
                "Auto reset on your emote is active for this timing-sync target. Their current emote timing resets to 0 when your emote updates.") + glyphGap;
        }

        if (showPerfWarning)
        {
            string userWarningText = "WARNING: This user exceeds one or more of your defined thresholds:" + SpheneUI.TooltipSeparator;
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

            iconsX = DrawCenteredGlyph(iconsX, rowCenterY, FontAwesomeIcon.ExclamationTriangle,
                ImGuiColors.DalamudYellow, userWarningText) + glyphGap;
        }

        // park a zero-size item at the pseudo line top: the following SameLine
        // restores this Y so frame-height buttons and name text stay centered
        var lineTopY = contentMin.Y + MathF.Max(0f, (rowHeight - ImGui.GetFrameHeight()) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(iconsX, lineTopY));
        ImGui.Dummy(Vector2.Zero);

        if (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.OneSided)
        {
            tooltipLines.Add(new(FontAwesomeIcon.UserSlash, "Pairing", "One-sided", null));
        }
        else if (_pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional)
        {
            tooltipLines.Add(new(FontAwesomeIcon.Link, "Pairing", "Direct pair", ImGuiColors.HealerGreen));
        }
        else if (_syncedGroups.Any())
        {
            if (_syncedGroups.Count == 1)
            {
                var entry = _syncedGroups[0];
                var groupNote = _serverConfigurationManager.GetNoteForGid(entry.GID);
                var groupString = string.IsNullOrEmpty(groupNote) ? entry.GroupAliasOrGID : $"{groupNote} ({entry.GroupAliasOrGID})";
                tooltipLines.Add(new(FontAwesomeIcon.Users, "Syncshell", groupString, null));
            }
            else
            {
                tooltipLines.Add(new(FontAwesomeIcon.Users, "Syncshells", _syncedGroups.Count.ToString(), null));
            }
        }

        if (!string.IsNullOrWhiteSpace(UserPair.RemoteClientVersion))
        {
            tooltipMeta = $"v{FormatRemoteClientVersion(UserPair.RemoteClientVersion)}";
        }

        if (_pair.LastAppliedDataBytes >= 0)
        {
            var labelPrefix = _pair.IsPaired ? string.Empty : "Last ";
            tooltipLines.Add(new(FontAwesomeIcon.BoxOpen, $"{labelPrefix}Mods",
                UiSharedService.ByteToString(_pair.LastAppliedDataBytes, true), null));

            if (_pair.LastAppliedApproximateVRAMBytes >= 0)
            {
                tooltipLines.Add(new(FontAwesomeIcon.Hdd, "VRAM",
                    UiSharedService.ByteToString(_pair.LastAppliedApproximateVRAMBytes, true), null));
            }

            if (_pair.LastAppliedDataTris >= 0)
            {
                tooltipLines.Add(new(FontAwesomeIcon.ChartLine, "Tris",
                    FormatTriangleCountCompact(_pair.LastAppliedDataTris), null));
            }
        }

        if (_pair.IsOnline && _pair.IsMutuallyVisible && !suppressAckUi)
        {
            var incoming = incomingStateForIcon;
            var outgoing = outgoingStateForIcon;
            if (incoming.Outcome != Pair.AckV3Outcome.Success || outgoing.Outcome != Pair.AckV3Outcome.Success)
            {
                var (summary, summaryTint, summaryIcon) = GetCompactSyncSummary(incoming, outgoing);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    tooltipLines.Add(new(summaryIcon, "Sync", summary, summaryTint));
                }
            }
            else if (outgoing.Time.HasValue)
            {
                tooltipLines.Add(new(FontAwesomeIcon.CheckCircle, "Synced",
                    $"{(DateTimeOffset.UtcNow - outgoing.Time.Value).TotalSeconds:F0}s ago", ImGuiColors.ParsedGreen));
            }
        }

        if (_pair.IsInDuty)
        {
            tooltipLines.Add(new(FontAwesomeIcon.ShieldAlt, "In duty", "sync hidden", ImGuiColors.DalamudYellow));
        }

        // presence rides as the first spec row — frees the header for the handle
        tooltipLines.Insert(0, new(FontAwesomeIcon.Circle, "Status", presenceText, presenceColor));
        var handleText = string.IsNullOrWhiteSpace(_pair.UserData.Alias) ? null : "@" + _pair.UserData.Alias;
        DrawTooltipForHoveredItem(showUserPairTooltip, avatarWrap, PairBanner(profile),
            tooltipTitle, handleText, presenceColor, profile, tooltipMeta, tooltipLines);
    }

    /// <summary>
    /// Draws a FontAwesome glyph vertically centered on the row via the draw
    /// list with a Dummy hitbox for hover/tooltips — icon-font baseline
    /// metrics otherwise shift glyphs off the name's text line.
    /// </summary>
    private static float DrawCenteredGlyph(float x, float centerY, FontAwesomeIcon icon, Vector4 color, string tooltip)
    {
        var glyph = icon.ToIconString();
        Vector2 glyphSize;
        using (SpheneUI.Icons.PushFont())
        {
            glyphSize = ImGui.CalcTextSize(glyph);
        }
        var pos = new Vector2(x, centerY - glyphSize.Y * 0.5f);
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(glyphSize);
        using (SpheneUI.Icons.PushFont())
        {
            ImGui.GetWindowDrawList().AddText(pos, SpheneUI.ColorUtils.ToU32(color), glyph);
        }
        SpheneUI.AttachTooltip(tooltip);
        return x + glyphSize.X;
    }

    private static string GetFriendlyAckFailureReason(Sphene.API.Dto.User.AcknowledgmentErrorCode code, string? message)
    {
        var baseReason = code switch
        {
            Sphene.API.Dto.User.AcknowledgmentErrorCode.DownloadFailed => "Couldn't download required files.",
            Sphene.API.Dto.User.AcknowledgmentErrorCode.ApplyFailed => "Couldn't apply the update.",
            Sphene.API.Dto.User.AcknowledgmentErrorCode.MismatchFailed => "Data mismatch detected. It will retry automatically.",
            Sphene.API.Dto.User.AcknowledgmentErrorCode.HashVerificationFailed => "Data integrity check failed.",
            Sphene.API.Dto.User.AcknowledgmentErrorCode.NotArrivedTimeout => "No response received in time.",
            Sphene.API.Dto.User.AcknowledgmentErrorCode.Timeout => "No response received in time.",
            _ => "Something went wrong."
        };

        if (string.IsNullOrWhiteSpace(message))
        {
            return baseReason;
        }

        var hasDutyContext = message.Contains("localInDuty=True", StringComparison.OrdinalIgnoreCase);
        var hasCombatContext = message.Contains("localInCombat=True", StringComparison.OrdinalIgnoreCase);
        if (hasDutyContext || hasCombatContext)
        {
            if (hasDutyContext && hasCombatContext)
            {
                return $"{baseReason} You're currently in duty/combat; sync may be delayed.";
            }
            if (hasDutyContext)
            {
                return $"{baseReason} You're currently in duty; sync may be delayed.";
            }
            return $"{baseReason} You're currently in combat; sync may be delayed.";
        }

        var cleaned = message;
        var idx = cleaned.IndexOf("(ctx:", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            cleaned = cleaned[..idx].Trim();
        }
        else
        {
            idx = cleaned.IndexOf("ctx:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                cleaned = cleaned[..idx].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return baseReason;
        }

        return $"{baseReason} {cleaned}";
    }

    private readonly record struct TooltipSpecRow(FontAwesomeIcon Icon, string Label, string? Value, Vector4? Tint);

    private static void DrawTooltipForHoveredItem(bool isHovered, IDalamudTextureWrap? avatar,
        IDalamudTextureWrap? banner, string title, string? handle, Vector4 presenceColor,
        SpheneProfileData profile, string? meta, IReadOnlyList<TooltipSpecRow> rows)
    {
        if (!isHovered) return;
        var scale = SpheneUI.Scale;
        // fixed width: an auto-sized tooltip re-wraps while the cursor moves
        // across the avatar, so the card visibly grows and shrinks
        var tipW = 340f * scale;
        var bannerH = 32f * scale;
        var avatarSize = 30f * scale;
        using (SpheneUI.Theme.TooltipScope())
        {
            ImGui.SetNextWindowSizeConstraints(new Vector2(tipW, 0f),
                new Vector2(tipW, float.MaxValue));
            ImGui.BeginTooltip();
            var drawList = ImGui.GetWindowDrawList();
            var winMin = ImGui.GetWindowPos();
            var pad = ImGui.GetStyle().WindowPadding;
            var rounding = ImGui.GetStyle().PopupRounding;
            var contentW = tipW - pad.X * 2f;
            var rightEdgeX = winMin.X + tipW - pad.X;
            ImGui.PushTextWrapPos(tipW - pad.X);

            // slim profile banner strip bleeding to the edges — same renderer
            // as the profile page (cover image or theme gradient + blends),
            // dissolved into the popup background over its lower half
            var theme = profile.ProfileTheme.Value;
            var accent = theme.AccentColor(SpheneTheme.Current.CompactAccent);
            var bMin = winMin;
            var bMax = new Vector2(winMin.X + tipW, bMin.Y + bannerH);
            // the window's default clip insets by ~half the padding — replace it
            // for the strip so the banner truly bleeds to the window edges
            ImGui.PushClipRect(winMin, winMin + ImGui.GetWindowSize(), false);
            SpheneUI.Profile.Banner(drawList, bMin, bMax, banner, theme,
                accent, accent, rounding: rounding, corners: ImDrawFlags.RoundCornersTop);
            var bg = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.PopupBg));
            var fadeH = (bMax.Y - bMin.Y) * 0.55f;
            drawList.AddRectFilledMultiColor(new Vector2(bMin.X, bMax.Y - fadeH), bMax,
                ImGui.ColorConvertFloat4ToU32(SpheneUI.ColorUtils.WithAlpha(bg, 0f)),
                ImGui.ColorConvertFloat4ToU32(SpheneUI.ColorUtils.WithAlpha(bg, 0f)),
                ImGui.ColorConvertFloat4ToU32(SpheneUI.ColorUtils.WithAlpha(bg, 0.85f)),
                ImGui.ColorConvertFloat4ToU32(SpheneUI.ColorUtils.WithAlpha(bg, 0.85f)));
            drawList.AddLine(new Vector2(bMin.X, bMax.Y), new Vector2(bMax.X, bMax.Y),
                ImGui.ColorConvertFloat4ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.30f)));
            ImGui.PopClipRect();

            // avatar rides half on the banner edge like the profile hero
            var overlap = 8f * scale;
            var avPos = new Vector2(winMin.X + pad.X, bMax.Y - overlap);
            ImGui.SetCursorScreenPos(avPos);
            SpheneUI.Media.Avatar(avatar, avatarSize);
            SpheneUI.Media.PresenceDot(drawList, avPos, avatarSize, presenceColor);
            var avatarBottom = avPos.Y + avatarSize;

            var textX = winMin.X + pad.X + avatarSize + SpheneUI.Spacing.X10.Scaled;
            ImGui.SetCursorScreenPos(new Vector2(textX, bMax.Y + SpheneUI.Spacing.X4.Scaled));
            using (ImRaii.Group())
            {
                var nameW = rightEdgeX - textX;
                if (profile.IsNSFW)
                {
                    nameW -= SpheneUI.Layout.BadgeWidth("NSFW") + SpheneUI.Spacing.X8.Scaled;
                }
                SpheneUI.Text.Plain(SpheneUI.Text.Ellipsize(title, nameW));
                if (profile.IsNSFW)
                {
                    ImGui.SameLine(0, SpheneUI.Spacing.X8.Scaled);
                    SpheneUI.Layout.Badge("NSFW", ImGuiColors.DalamudYellow);
                    // Badge draws via the draw list and parks the cursor on the
                    // chip's end — the dummy turns it into a proper line break
                    ImGui.Dummy(new Vector2(0f, SpheneUI.Layout.BadgeHeight));
                }
                if (!string.IsNullOrWhiteSpace(handle))
                {
                    SpheneUI.Text.Colored(SpheneUI.Text.Ellipsize(handle, rightEdgeX - textX), SpheneUI.Palette.CrystalWhite);
                }
                if (!string.IsNullOrWhiteSpace(profile.StatusMessage))
                {
                    SpheneUI.Text.MutedWrapped($"\"{profile.StatusMessage.Trim()}\"");
                }
            }
            var contentBottom = MathF.Max(ImGui.GetCursorScreenPos().Y, avatarBottom + SpheneUI.Spacing.X4.Scaled);

            if (rows.Count > 0)
            {
                var iconWidth = 0f;
                var labelWidth = 0f;
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    iconWidth = MathF.Max(iconWidth, SpheneUI.Icons.Size(row.Icon).X);
                    if (row.Value != null)
                    {
                        labelWidth = MathF.Max(labelWidth, ImGui.CalcTextSize(row.Label).X);
                    }
                }
                labelWidth = MathF.Min(labelWidth, contentW * 0.45f);

                var labelColX = pad.X + iconWidth + SpheneUI.Spacing.Inline.Scaled;
                var valueColX = labelColX + labelWidth + SpheneUI.Spacing.X10.Scaled;
                var valueW = MathF.Max(0f, rightEdgeX - (winMin.X + valueColX));

                ImGui.SetCursorScreenPos(new Vector2(winMin.X + pad.X, contentBottom + SpheneUI.Spacing.Inline.Scaled));
                SpheneUI.Layout.Separator();

                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    SpheneUI.Icons.Icon(row.Icon, row.Tint ?? ImGuiColors.DalamudGrey);
                    ImGui.SameLine(labelColX);
                    if (row.Value == null)
                    {
                        SpheneUI.Text.Colored(SpheneUI.Text.Ellipsize(row.Label, rightEdgeX - (winMin.X + labelColX)), row.Tint);
                    }
                    else
                    {
                        SpheneUI.Text.Muted(SpheneUI.Text.Ellipsize(row.Label, labelWidth));
                        ImGui.SameLine(valueColX);
                        SpheneUI.Text.Colored(SpheneUI.Text.Ellipsize(row.Value, valueW), row.Tint);
                    }
                }
            }

            // client version footer — pinned to the bottom-right corner so a
            // longer name never pushes it around
            if (!string.IsNullOrWhiteSpace(meta))
            {
                var metaW = ImGui.CalcTextSize(meta).X;
                var footerY = MathF.Max(ImGui.GetCursorScreenPos().Y, contentBottom)
                    + SpheneUI.Spacing.X2.Scaled;
                ImGui.SetCursorScreenPos(new Vector2(rightEdgeX - metaW, footerY));
                SpheneUI.Text.Muted(meta);
            }

            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private static string FormatRemoteClientVersion(string? remoteClientVersion)
    {
        if (string.IsNullOrWhiteSpace(remoteClientVersion))
        {
            return "Unknown";
        }

        var formatted = remoteClientVersion.Trim();
        if (Version.TryParse(formatted, out var parsedRemoteClientVersion))
        {
            var normalizedVersion = parsedRemoteClientVersion.Build >= 0
                ? $"{parsedRemoteClientVersion.Major}.{parsedRemoteClientVersion.Minor}.{parsedRemoteClientVersion.Build}"
                : $"{parsedRemoteClientVersion.Major}.{parsedRemoteClientVersion.Minor}";
            formatted = parsedRemoteClientVersion.Revision >= 0
                ? $"{normalizedVersion}.{parsedRemoteClientVersion.Revision}"
                : normalizedVersion;
        }

        return formatted;
    }

    private static (string Text, Vector4 Tint, FontAwesomeIcon Icon) GetCompactSyncSummary(
        Pair.AckV3State incoming, Pair.AckV3State outgoing)
    {
        if (incoming.Outcome == Pair.AckV3Outcome.Fail || outgoing.Outcome == Pair.AckV3Outcome.Fail)
        {
            var outgoingFailed = outgoing.Outcome == Pair.AckV3Outcome.Fail;
            var failedState = outgoingFailed ? outgoing : incoming;
            var reason = GetFriendlyAckFailureReason(failedState.ErrorCode, failedState.ErrorMessage);
            var direction = outgoingFailed ? "Sending" : "Applying";
            return (TrimWithEllipsis($"{direction} failed — {reason}", 110),
                ImGuiColors.DalamudRed, FontAwesomeIcon.ExclamationTriangle);
        }

        if (outgoing.Outcome == Pair.AckV3Outcome.Pending)
        {
            return ("Sending your update", ImGuiColors.DalamudYellow, FontAwesomeIcon.Sync);
        }

        if (incoming.Outcome == Pair.AckV3Outcome.Pending)
        {
            return ("Applying their update", ImGuiColors.DalamudYellow, FontAwesomeIcon.Sync);
        }

        return (string.Empty, Vector4.One, FontAwesomeIcon.Sync);
    }

    private static string TrimWithEllipsis(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 1)] + "…";
    }

    private static string FormatTriangleCountCompact(long triangleCount)
    {
        if (triangleCount >= 1000)
        {
            return (triangleCount / 1000d).ToString("0.0'k'");
        }

        return triangleCount.ToString();
    }

    private void DrawName(float leftSide)
    {
        _displayHandler.DrawPairText(_id, _pair, leftSide);
    }

    private void DrawPairedClientMenu()
    {
        DrawIndividualMenu();

        if (_syncedGroups.Any()) SpheneUI.Layout.Separator();
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

    private void DrawRightSide()
    {
        var pauseIcon = _pair.UserPair!.OwnPermissions.IsPaused() ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var pauseButtonSize = SpheneUI.Icons.ButtonSize(pauseIcon);
        var isOneSidedIndividualPair = _pair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.OneSided;
        var isOneSidedIndividualListEntry = isOneSidedIndividualPair
            && _currentGroup == null
            && _id.StartsWith(TagHandler.CustomUnpairedTag, StringComparison.Ordinal);
        var rightmostIcon = isOneSidedIndividualListEntry ? FontAwesomeIcon.User : FontAwesomeIcon.EllipsisV;
        var rightmostButtonSize = SpheneUI.Icons.ButtonSize(rightmostIcon);
        var reloadButtonSize = _pair.IsVisible ? SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Sync) : Vector2.Zero;
        var pairButtonSize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Plus);
        var unpairButtonSize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Trash);
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        // Use container-relative positioning for buttons
        var containerWidth = SpheneUI.Controls.BoundedAvailX();
        var actualWindowEndX = ImGui.GetCursorPosX() + containerWidth;
        
        // Adjust button positioning for grouped syncshell folders
        var buttonOffset = (_currentGroup != null && _configService.Current.GroupUpSyncshells) ? 20f : 0f;
        var rowInset = SpheneTheme.Current.CompactPairRowContentPadding;
        float currentRightSide = actualWindowEndX - rightmostButtonSize.X - buttonOffset - rowInset;

        // Rightmost action button
        ImGui.SameLine(currentRightSide);
        ImGui.AlignTextToFramePadding();
        if (SpheneUI.Icons.Button(rightmostIcon, null, null, null, null, ButtonStyleKeys.Pair_Menu))
        {
            if (isOneSidedIndividualListEntry)
            {
                _mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Friends, null, _pair));
            }
            else
            {
                ImGui.OpenPopup("User Flyout Menu");
            }
        }
        SpheneUI.AttachTooltip(isOneSidedIndividualListEntry
            ? "Open profile of " + _pair.UserData.PreferredName + " in the Sphene Hub"
            : "Open user menu");

        if (!isOneSidedIndividualListEntry)
        {
            var profileButtonSize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.IdCard);
            currentRightSide -= (profileButtonSize.X + spacingX);
            ImGui.SameLine(currentRightSide);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.IdCard, null, null, null, null, ButtonStyleKeys.Pair_Profile))
            {
                _mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Friends, null, _pair));
            }
            SpheneUI.AttachTooltip("Open profile of " + _pair.UserData.PreferredName + " in the Sphene Hub");
        }

        // Reload button (only if pair is visible)
        if (_pair.IsVisible)
        {
            currentRightSide -= (reloadButtonSize.X + spacingX);
            ImGui.SameLine(currentRightSide);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Sync, null, null, null, null, ButtonStyleKeys.Pair_Reload))
            {
                var requestToken = _pair.RequestForcedRedrawOnNextCharacterReload();
                _ = _apiController.UserRequestCharacterDataRefresh(new(_pair.UserData));
                _ = _pair.ApplyLastKnownDataIfReloadPendingAsync(requestToken);
                _mediator.Publish(new NotificationMessage("Character data refresh", $"Requested a fresh sync from {_pair.UserData.PreferredName}", Sphene.SpheneConfiguration.Models.NotificationType.Info, TimeSpan.FromSeconds(2)));
            }
            SpheneUI.AttachTooltip("Request fresh character data from server");
        }

        if (isOneSidedIndividualListEntry && !_pair.UserPair.IsOutgoingIndividualPair)
        {
            currentRightSide -= (pairButtonSize.X + spacingX);
            ImGui.SameLine(currentRightSide);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Plus))
            {
                _ = _apiController.UserAddPair(new(_pair.UserData));
            }
            SpheneUI.AttachTooltip("Pair individually with " + _pair.UserData.PreferredName);

        }

        if (isOneSidedIndividualListEntry)
        {
            currentRightSide -= (unpairButtonSize.X + spacingX);
            ImGui.SameLine(currentRightSide);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.UserRemovePair(new(_pair.UserData));
            }
            SpheneUI.AttachTooltip("Hold CTRL and click to unpair permanently from " + _pair.UserData.PreferredName);
        }

        if (!isOneSidedIndividualListEntry)
        {
            // Pause/Play button (leftmost of the three)
            currentRightSide -= (pauseButtonSize.X + spacingX);
            ImGui.SameLine(currentRightSide);
            if (SpheneUI.Icons.Button(pauseIcon, null, null, null, null, ButtonStyleKeys.Pair_Pause))
            {
                var perm = _pair.UserPair!.OwnPermissions;

                if (UiSharedService.CtrlPressed() && !perm.IsPaused())
                {
                    perm.SetSticky(true);
                }
                perm.SetPaused(!perm.IsPaused());
                _ = _apiController.UserSetPairPermissions(new(_pair.UserData, perm));
            }
            SpheneUI.AttachTooltip(!_pair.UserPair!.OwnPermissions.IsPaused()
                ? ("Pause pairing with " + _pair.UserData.PreferredName
                    + (_pair.UserPair!.OwnPermissions.IsSticky()
                        ? string.Empty
                        : SpheneUI.TooltipSeparator + "Hold CTRL to enable preferred permissions while pausing." + Environment.NewLine + "This will leave this pair paused even if unpausing syncshells including this pair."))
                : "Resume pairing with " + _pair.UserData.PreferredName);
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
                currentRightSide -= (SpheneUI.Icons.Size(individualIcon).X + spacingX);

                ImGui.SameLine(currentRightSide);
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled))
                    SpheneUI.Icons.Icon(individualIcon);
                if (ImGui.IsItemHovered())
                {
                    using (SpheneUI.Theme.TooltipScope())
                    {
                        ImGui.BeginTooltip();

                        SpheneUI.Text.Plain("Individual User permissions");
                        ImGui.Separator();

                        if (individualIsSticky)
                        {
                            SpheneUI.Icons.Icon(individualIcon);
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Plain("Preferred permissions enabled");
                            if (individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled)
                                ImGui.Separator();
                        }

                        if (individualSoundsDisabled)
                        {
                            var userSoundsText = "Sound sync";
                            SpheneUI.Icons.Icon(FontAwesomeIcon.VolumeOff);
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Plain(userSoundsText);
                            ImGui.NewLine();
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Plain("You");
                            SpheneUI.Icons.BoolIcon(!_pair.UserPair!.OwnPermissions.IsDisableSounds());
                            ImGui.SameLine();
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Plain("They");
                            SpheneUI.Icons.BoolIcon(!_pair.UserPair!.OtherPermissions.IsDisableSounds());
                        }

                        if (individualAnimDisabled)
                        {
                            var userAnimText = "Animation sync";
                            SpheneUI.Icons.Icon(FontAwesomeIcon.Stop);
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Plain(userAnimText);
                            ImGui.NewLine();
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Plain("You");
                            SpheneUI.Icons.BoolIcon(!_pair.UserPair!.OwnPermissions.IsDisableAnimations());
                            ImGui.SameLine();
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Plain("They");
                            SpheneUI.Icons.BoolIcon(!_pair.UserPair!.OtherPermissions.IsDisableAnimations());
                        }

                        if (individualVFXDisabled)
                        {
                            var userVFXText = "VFX sync";
                            SpheneUI.Icons.Icon(FontAwesomeIcon.Circle);
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Plain(userVFXText);
                            ImGui.NewLine();
                            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Plain("You");
                            SpheneUI.Icons.BoolIcon(!_pair.UserPair!.OwnPermissions.IsDisableVFX());
                            ImGui.SameLine();
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Plain("They");
                            SpheneUI.Icons.BoolIcon(!_pair.UserPair!.OtherPermissions.IsDisableVFX());
                        }

                        ImGui.EndTooltip();
                    }
                }
            }

            if (_pendingModNotifications.Count > 0)
            {
                var modIconWidth = SpheneUI.Icons.Size(FontAwesomeIcon.BoxOpen).X;
                currentRightSide -= (modIconWidth + spacingX);
                ImGui.SameLine(currentRightSide);
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGreen))
                    SpheneUI.Icons.Icon(FontAwesomeIcon.BoxOpen);
                
                var tooltip = _pendingModNotifications.Count > 1 
                    ? $"This user has {_pendingModNotifications.Count} Penumbra mod packages ready to install."
                    : "This user has a Penumbra mod package ready to install.";
                SpheneUI.AttachTooltip(tooltip);
            }
        }

        if (_charaDataManager.SharedWithYouData.TryGetValue(_pair.UserData, out var sharedData))
        {
            currentRightSide -= (SpheneUI.Icons.Size(FontAwesomeIcon.Running).X + (spacingX / 2f));
            ImGui.SameLine(currentRightSide);
            SpheneUI.Icons.Icon(FontAwesomeIcon.Running);
            SpheneUI.AttachTooltip($"This user has shared {sharedData.Count} Character Data Sets with you." + SpheneUI.TooltipSeparator
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
                currentRightSide -= (SpheneUI.Icons.Size(icon).X + spacingX);
                ImGui.SameLine(currentRightSide);
                SpheneUI.Icons.Icon(icon);
                SpheneUI.AttachTooltip(text);
            }
        }

        using (SpheneUI.Theme.ContextMenuScope())
        {
            if (SpheneUI.Popups.BeginStyled("User Flyout Menu"))
            {
                using (ImRaii.PushId($"buttons-{_pair.UserData.UID}"))
                {
                    SpheneUI.Text.Plain("Common Pair Functions");
                    DrawCommonClientMenu();
                    SpheneUI.Layout.Separator();
                    DrawPairedClientMenu();
                    if (_menuWidth <= 0)
                    {
                        _menuWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                    }
                }
                SpheneUI.Popups.EndStyled();
            }
        }

    }

    private void DrawSyncshellMenu(GroupFullInfoDto group, bool selfIsOwner, bool selfIsModerator, bool userIsPinned, bool userIsModerator)
    {
        if (selfIsOwner || ((selfIsModerator) && (!userIsModerator)))
        {
            SpheneUI.Text.Plain("Syncshell Moderator Functions");
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
            SpheneUI.AttachTooltip("Pin this user to the Syncshell. Pinned users will not be deleted in case of a manually initiated Syncshell clean");

            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Remove user", _menuWidth, true, ButtonStyleKeys.Pair_Remove) && UiSharedService.CtrlPressed())
            {
                ImGui.CloseCurrentPopup();
                _ = _apiController.GroupRemoveUser(new(group.Group, _pair.UserData));
            }
            SpheneUI.AttachTooltip("Hold CTRL and click to remove user " + (_pair.UserData.PreferredName) + " from Syncshell");

            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.UserSlash, "Ban User", _menuWidth, true, ButtonStyleKeys.Pair_Ban))
            {
                _mediator.Publish(new OpenBanUserPopupMessage(_pair, group));
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip("Ban user from this Syncshell");

            SpheneUI.Layout.Separator();
        }

        if (selfIsOwner)
        {
            SpheneUI.Text.Plain("Syncshell Owner Functions");
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
            SpheneUI.AttachTooltip("Hold CTRL to change the moderator status for " + (_pair.UserData.PreferredName) + Environment.NewLine +
                "Moderators can kick, ban/unban, pin/unpin users and clear the Syncshell.");

            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Crown, "Transfer Ownership", _menuWidth, true, ButtonStyleKeys.Pair_Transfer) && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
            {
                ImGui.CloseCurrentPopup();
                _ = _apiController.GroupChangeOwnership(new(group.Group, _pair.UserData));
            }
        }
    }
    
    public void RefreshIcon()
    {
        _ = _pair.GetOutgoingAckV3State();
        _ = _pair.GetIncomingAckV3State();
    }
}
