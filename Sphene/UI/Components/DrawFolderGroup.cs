using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Group;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Handlers;
using Sphene.WebAPI;
using Sphene.SpheneConfiguration;
using Sphene.UI.Theme;
using System.Collections.Immutable;
using System.Numerics;

namespace Sphene.UI.Components;

public class DrawFolderGroup : DrawFolderBase
{
    private readonly ApiController _apiController;
    private readonly GroupFullInfoDto _groupFullInfoDto;
    private readonly IdDisplayHandler _idDisplayHandler;
    private readonly SpheneMediator _spheneMediator;
    private readonly AreaBoundSyncshellService _areaBoundSyncshellService;
    private readonly SpheneConfigService _configService;
    private readonly PairManager _pairManager;
    private float _menuWidth;

    public DrawFolderGroup(string id, GroupFullInfoDto groupFullInfoDto, ApiController apiController,
        IImmutableList<DrawUserPair> drawPairs, IImmutableList<Pair> allPairs, TagHandler tagHandler, IdDisplayHandler idDisplayHandler,
        SpheneMediator spheneMediator, UiSharedService uiSharedService, AreaBoundSyncshellService areaBoundSyncshellService, SpheneConfigService configService, PairManager pairManager, bool isSyncshellFolder = false) :
        base(id, drawPairs, allPairs, tagHandler, uiSharedService, 0f, isSyncshellFolder)
    {
        _groupFullInfoDto = groupFullInfoDto;
        _apiController = apiController;
        _idDisplayHandler = idDisplayHandler;
        _spheneMediator = spheneMediator;
        _areaBoundSyncshellService = areaBoundSyncshellService;
        _configService = configService;
        _pairManager = pairManager;
    }

    protected override bool RenderIfEmpty => true;
    protected override bool RenderMenu => false;
    private bool IsModerator => IsOwner || _groupFullInfoDto.GroupUserInfo.IsModerator();
    private bool IsOwner => string.Equals(_groupFullInfoDto.OwnerUID, _apiController.UID, StringComparison.Ordinal);
    private bool IsPinned => _groupFullInfoDto.GroupUserInfo.IsPinned();

    // Override OnlinePairs to show total online users in syncshell, including visible pairs
    public new int OnlinePairs
    {
        get
        {
            // For area-bound syncshells (like city syncshells), GroupPairUserInfos is empty
            // because they don't have permanent members - users auto-join based on location
            if (_groupFullInfoDto.GroupPairUserInfos.Count == 0)
            {
                // For area-bound syncshells, only count visible pairs that are online
                return DrawPairs.Count(u => u.Pair.IsOnline);
            }
            
            // For regular syncshells, count both visible and non-visible online users
            // Start with visible pairs that are online (from DrawPairs)
            var visibleOnlineCount = DrawPairs.Count(u => u.Pair.IsOnline);
            
            // Get all users in the syncshell from GroupPairUserInfos
            var allSyncshellUsers = _groupFullInfoDto.GroupPairUserInfos.Keys.ToList();
            
            // Count syncshell users that are online but not already counted in visible pairs
            var visibleUserUIDs = DrawPairs.Select(dp => dp.Pair.UserData.UID).ToHashSet();
            int additionalOnlineCount = 0;
            
            foreach (var userUID in allSyncshellUsers)
            {
                // Skip if this user is already counted in visible pairs
                if (visibleUserUIDs.Contains(userUID))
                    continue;
                    
                var pair = _pairManager.GetPairByUID(userUID);
                if (pair != null && pair.IsOnline)
                {
                    additionalOnlineCount++;
                }
            }
            
            return visibleOnlineCount + additionalOnlineCount;
        }
    }

    // Override TotalPairs to show total users (visible pairs + syncshell members)
    public new int TotalPairs
    {
        get
        {
            // For area-bound syncshells (like city syncshells), GroupPairUserInfos is empty
            // because they don't have permanent members - users auto-join based on location
            if (_groupFullInfoDto.GroupPairUserInfos.Count == 0)
            {
                // For area-bound syncshells, only count visible pairs
                return DrawPairs.Count;
            }
            
            // For regular syncshells, count both visible and non-visible users
            // Count visible pairs
            var visiblePairsCount = DrawPairs.Count;
            
            // Count syncshell members not already in visible pairs
            var visibleUserUIDs = DrawPairs.Select(dp => dp.Pair.UserData.UID).ToHashSet();
            var additionalSyncshellMembers = _groupFullInfoDto.GroupPairUserInfos.Keys
                .Count(uid => !visibleUserUIDs.Contains(uid));
            
            return visiblePairsCount + additionalSyncshellMembers;
        }
    }

    public override void Draw()
    {
        if (!RenderIfEmpty && !DrawPairs.Any()) return;

        using var id = ImRaii.PushId("folder_" + _id);
        var folderWidth = FolderWidth + 0.0f;
        using (ImRaii.Child("folder__" + _id, new System.Numerics.Vector2(folderWidth, ImGui.GetFrameHeight()), false, ImGuiWindowFlags.NoScrollbar))
        {
            
            // draw opener
            var icon = _tagHandler.IsTagOpen(_id) ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight;

            ImGui.AlignTextToFramePadding();

            _uiSharedService.IconText(icon);
            if (ImGui.IsItemClicked())
            {
                _tagHandler.SetTagOpen(_id, !_tagHandler.IsTagOpen(_id));
            }

            ImGui.SameLine();
            var leftSideEnd = DrawIcon();

            ImGui.SameLine();
            var rightSideStart = DrawRightSideInternal();

            // draw name
            ImGui.SameLine(leftSideEnd);
            DrawName(rightSideStart - leftSideEnd);
        }

        ImGui.Separator();

        // if opened draw content
        if (_tagHandler.IsTagOpen(_id))
        {
            var baseIndent = _uiSharedService.GetIconSize(FontAwesomeIcon.EllipsisV).X + ImGui.GetStyle().ItemSpacing.X;
            var indentAmount = baseIndent;
            using var indent = ImRaii.PushIndent(indentAmount, false);
            if (DrawPairs.Any())
            {
                foreach (var item in DrawPairs)
                {
                    item.DrawPairedClient();
                }
            }
            else
            {
                // Show more informative message for syncshells
                if (TotalPairs > 0)
                {
                    ImGui.TextUnformatted($"No visible users ({OnlinePairs} online, {TotalPairs} total)");
                }
                else
                {
                    ImGui.TextUnformatted("No users in syncshell");
                }
            }

            ImGui.Separator();
        }
    }

    protected override float DrawIcon()
    {
        ImGui.AlignTextToFramePadding();

        // Check if this is an area-bound syncshell to determine display order
        bool isAreaBound = _areaBoundSyncshellService.IsAreaBoundSyncshell(_groupFullInfoDto.Group.GID);
        
        if (isAreaBound)
        {
            // For Area Syncshells: Location emoji first, then count, then owner indicator
            // Make the area-bound indicator clickable to show welcome message on demand
            if (_configService.Current.ShowAreaBoundSyncshellWelcomeMessages)
            {
                // Normal non-clickable indicator when welcome messages are enabled
                _uiSharedService.IconText(FontAwesomeIcon.MapMarkerAlt);
                UiSharedService.AttachToolTip("This is an area-bound syncshell");
            }
            else
            {
                // Clickable indicator when welcome messages are disabled - looks like normal icon but is clickable
                _uiSharedService.IconText(FontAwesomeIcon.MapMarkerAlt);
                if (ImGui.IsItemClicked())
                {
                    // Show welcome message on demand
                    _ = ShowWelcomeMessageOnDemand();
                }
                UiSharedService.AttachToolTip("This is an area-bound syncshell\nClick to view welcome message");
            }
        }
        else
        {
            // For normal Syncshells: Keep original order with group emoji first
            _uiSharedService.IconText(_groupFullInfoDto.GroupPermissions.IsDisableInvites() ? FontAwesomeIcon.Lock : FontAwesomeIcon.Users);
            if (_groupFullInfoDto.GroupPermissions.IsDisableInvites())
            {
                UiSharedService.AttachToolTip("Syncshell " + _groupFullInfoDto.GroupAliasOrGID + " is closed for invites");
            }
        }

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing with { X = ImGui.GetStyle().ItemSpacing.X / 2f }))
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();

            ImGui.TextUnformatted("[" + OnlinePairs.ToString() + "]");
        }
        UiSharedService.AttachToolTip(OnlinePairs + " online" + Environment.NewLine + TotalPairs + " total");

        ImGui.SameLine();
        if (IsOwner)
        {
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.Crown);
            UiSharedService.AttachToolTip("You are the owner of " + _groupFullInfoDto.GroupAliasOrGID);
        }
        else if (IsModerator)
        {
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.UserShield);
            UiSharedService.AttachToolTip("You are a moderator in " + _groupFullInfoDto.GroupAliasOrGID);
        }
        else if (IsPinned)
        {
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.Thumbtack);
            UiSharedService.AttachToolTip("You are pinned in " + _groupFullInfoDto.GroupAliasOrGID);
        }

        // For normal syncshells, show area-bound indicator at the end (if somehow it's area-bound but not detected above)
        if (!isAreaBound && _areaBoundSyncshellService.IsAreaBoundSyncshell(_groupFullInfoDto.Group.GID))
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            
            // Make the area-bound indicator clickable to show welcome message on demand
            if (_configService.Current.ShowAreaBoundSyncshellWelcomeMessages)
            {
                // Normal non-clickable indicator when welcome messages are enabled
                _uiSharedService.IconText(FontAwesomeIcon.MapMarkerAlt);
                UiSharedService.AttachToolTip("This is an area-bound syncshell");
            }
            else
            {
                // Clickable indicator when welcome messages are disabled - looks like normal icon but is clickable
                _uiSharedService.IconText(FontAwesomeIcon.MapMarkerAlt);
                if (ImGui.IsItemClicked())
                {
                    // Show welcome message on demand
                    _ = ShowWelcomeMessageOnDemand();
                }
                UiSharedService.AttachToolTip("This is an area-bound syncshell\nClick to view welcome message");
            }
        }

        ImGui.SameLine();
        return ImGui.GetCursorPosX();
    }

    protected override void DrawMenu(float menuWidth)
    {
        ImGui.TextUnformatted("Syncshell Menu (" + _groupFullInfoDto.GroupAliasOrGID + ")");
        ImGui.Separator();

        ImGui.TextUnformatted("General Syncshell Actions");
        if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Copy, "Copy ID", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            ImGui.CloseCurrentPopup();
            ImGui.SetClipboardText(_groupFullInfoDto.GroupAliasOrGID);
        }
        UiSharedService.AttachToolTip("Copy Syncshell ID to Clipboard");

        if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.StickyNote, "Copy Notes", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            ImGui.CloseCurrentPopup();
            ImGui.SetClipboardText(UiSharedService.GetNotes(DrawPairs.Select(k => k.Pair).ToList()));
        }
        UiSharedService.AttachToolTip("Copies all your notes for all users in this Syncshell to the clipboard." + Environment.NewLine + "They can be imported via Settings -> General -> Notes -> Import notes from clipboard");

        // Check if this is an area-bound syncshell to show appropriate leave button
        bool isAreaBound = _areaBoundSyncshellService.IsAreaBoundSyncshell(_groupFullInfoDto.Group.GID);
        bool isOwner = string.Equals(_groupFullInfoDto.OwnerUID, _apiController.UID, StringComparison.Ordinal);
        
        if (isOwner)
        {
            // Show "Delete Syncshell" button for owners
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Trash, "Delete Syncshell", menuWidth, ButtonStyleKeys.ContextMenu_Item) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.GroupDelete(new(_groupFullInfoDto.Group));
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to delete this Syncshell" + Environment.NewLine + 
                "WARNING: This action is irreversible and will permanently delete the Syncshell for all members.");
        }
        else if (isAreaBound)
        {
            // Show "Leave Area Syncshell" button for non-owners in area-bound syncshells
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.MapMarkerAlt, "Leave Area Syncshell", menuWidth, ButtonStyleKeys.ContextMenu_Item) && UiSharedService.CtrlPressed())
            {
                _ = LeaveAreaSyncshellWithConsentReset();
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to leave this Area Syncshell" + Environment.NewLine + 
                "This will reset your consent and you'll need to accept again when re-entering the area.");
        }
        else
        {
            // Show regular "Leave Syncshell" button for non-owners in regular syncshells
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.ArrowCircleLeft, "Leave Syncshell", menuWidth, ButtonStyleKeys.ContextMenu_Item) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.GroupLeave(_groupFullInfoDto);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to leave this Syncshell");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Permission Settings");
        var perm = _groupFullInfoDto.GroupUserPermissions;
        bool disableSounds = perm.IsDisableSounds();
        bool disableAnims = perm.IsDisableAnimations();
        bool disableVfx = perm.IsDisableVFX();

        if ((_groupFullInfoDto.GroupPermissions.IsPreferDisableAnimations() != disableAnims
            || _groupFullInfoDto.GroupPermissions.IsPreferDisableSounds() != disableSounds
            || _groupFullInfoDto.GroupPermissions.IsPreferDisableVFX() != disableVfx)
            && _uiSharedService.IconTextActionButton(FontAwesomeIcon.Check, "Align with suggested permissions", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            perm.SetDisableVFX(_groupFullInfoDto.GroupPermissions.IsPreferDisableVFX());
            perm.SetDisableSounds(_groupFullInfoDto.GroupPermissions.IsPreferDisableSounds());
            perm.SetDisableAnimations(_groupFullInfoDto.GroupPermissions.IsPreferDisableAnimations());
            _ = _apiController.GroupChangeIndividualPermissionState(new(_groupFullInfoDto.Group, new(_apiController.UID), perm));
            ImGui.CloseCurrentPopup();
        }

        if (_uiSharedService.IconTextActionButton(disableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeOff, disableSounds ? "Enable Sound Sync" : "Disable Sound Sync", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            perm.SetDisableSounds(!disableSounds);
            _ = _apiController.GroupChangeIndividualPermissionState(new(_groupFullInfoDto.Group, new(_apiController.UID), perm));
            ImGui.CloseCurrentPopup();
        }

        if (_uiSharedService.IconTextActionButton(disableAnims ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop, disableAnims ? "Enable Animation Sync" : "Disable Animation Sync", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            perm.SetDisableAnimations(!disableAnims);
            _ = _apiController.GroupChangeIndividualPermissionState(new(_groupFullInfoDto.Group, new(_apiController.UID), perm));
            ImGui.CloseCurrentPopup();
        }

        if (_uiSharedService.IconTextActionButton(disableVfx ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle, disableVfx ? "Enable VFX Sync" : "Disable VFX Sync", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            perm.SetDisableVFX(!disableVfx);
            _ = _apiController.GroupChangeIndividualPermissionState(new(_groupFullInfoDto.Group, new(_apiController.UID), perm));
            ImGui.CloseCurrentPopup();
        }

        if (IsModerator || IsOwner)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Syncshell Admin Functions");
            if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Cog, "Open Admin Panel", menuWidth, ButtonStyleKeys.ContextMenu_Item))
            {
                ImGui.CloseCurrentPopup();
                _spheneMediator.Publish(new OpenSyncshellAdminPanel(_groupFullInfoDto));
            }
        }
    }

    protected override void DrawName(float width)
    {
        _idDisplayHandler.DrawGroupText(_id, _groupFullInfoDto, ImGui.GetCursorPosX(), () => width);
    }

    protected override float DrawRightSide(float currentRightSideX)
    {
        var spacingX = ImGui.GetStyle().ItemSpacing.X;

        FontAwesomeIcon pauseIcon = _groupFullInfoDto.GroupUserPermissions.IsPaused() ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var pauseButtonSize = _uiSharedService.GetIconButtonSize(pauseIcon);
        var menuButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.EllipsisV);

        var userCogButtonSize = _uiSharedService.GetIconSize(FontAwesomeIcon.UsersCog);

        var individualSoundsDisabled = _groupFullInfoDto.GroupUserPermissions.IsDisableSounds();
        var individualAnimDisabled = _groupFullInfoDto.GroupUserPermissions.IsDisableAnimations();
        var individualVFXDisabled = _groupFullInfoDto.GroupUserPermissions.IsDisableVFX();

        // Use container-relative positioning for buttons
        var containerWidth = ImGui.GetContentRegionAvail().X;
        var actualWindowEndX = ImGui.GetCursorPosX() + containerWidth;
        
        var menuButtonOffset = actualWindowEndX - menuButtonSize.X;
        ImGui.SameLine(menuButtonOffset);
        
        using var menuButtonColor = ImRaii.PushColor(ImGuiCol.Button, SpheneCustomTheme.CurrentTheme.CompactSyncshellButton);
        using var menuButtonHoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, SpheneCustomTheme.CurrentTheme.CompactSyncshellButtonHovered);
        using var menuButtonActiveColor = ImRaii.PushColor(ImGuiCol.ButtonActive, SpheneCustomTheme.CurrentTheme.CompactSyncshellButtonActive);
        
        if (_uiSharedService.IconButton(FontAwesomeIcon.EllipsisV, menuButtonSize.Y, null, null, menuButtonSize.X, ButtonStyleKeys.GroupSyncshell_Menu))
        {
            ImGui.OpenPopup("User Flyout Menu");
        }
        using (SpheneCustomTheme.ApplyContextMenuTheme())
        {
            if (ImGui.BeginPopup("User Flyout Menu"))
            {
                using (ImRaii.PushId($"buttons-{_id}")) DrawMenu(_menuWidth);
                _menuWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                ImGui.EndPopup();
            }
            else
            {
                _menuWidth = 0;
            }
        }

        // Calculate pause button position (left of menu button) - MUST match menu button position
        var pauseButtonOffset = actualWindowEndX - pauseButtonSize.X - spacingX - menuButtonSize.X;
        ImGui.SameLine(pauseButtonOffset);
        
        // Draw pause button with brighter blue color styling
        using var pauseButtonColor = ImRaii.PushColor(ImGuiCol.Button, SpheneCustomTheme.CurrentTheme.CompactSyncshellButton);
        using var pauseButtonHoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, SpheneCustomTheme.CurrentTheme.CompactSyncshellButtonHovered);
        using var pauseButtonActiveColor = ImRaii.PushColor(ImGuiCol.ButtonActive, SpheneCustomTheme.CurrentTheme.CompactSyncshellButtonActive);
        
        if (_uiSharedService.IconButton(pauseIcon, pauseButtonSize.Y, null, null, pauseButtonSize.X, ButtonStyleKeys.GroupSyncshell_Pause))
        {
            var perm = _groupFullInfoDto.GroupUserPermissions;
            perm.SetPaused(!perm.IsPaused());
            _ = _apiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(_groupFullInfoDto.Group, new(_apiController.UID), perm));
        }

        // Calculate info icon position (left of pause button) - MUST match other button positions
        var infoIconOffset = actualWindowEndX - userCogButtonSize.X - spacingX - pauseButtonSize.X - spacingX - menuButtonSize.X;
        ImGui.SameLine(infoIconOffset);

        ImGui.AlignTextToFramePadding();

        _uiSharedService.IconText(FontAwesomeIcon.UsersCog, (_groupFullInfoDto.GroupPermissions.IsPreferDisableAnimations() != individualAnimDisabled
            || _groupFullInfoDto.GroupPermissions.IsPreferDisableSounds() != individualSoundsDisabled
            || _groupFullInfoDto.GroupPermissions.IsPreferDisableVFX() != individualVFXDisabled) ? ImGuiColors.DalamudYellow : null);
        if (ImGui.IsItemHovered())
        {
            using (SpheneCustomTheme.ApplyTooltipTheme())
            {
                ImGui.BeginTooltip();

                ImGui.TextUnformatted("Syncshell Permissions");
                ImGuiHelpers.ScaledDummy(2f);

                _uiSharedService.BooleanToColoredIcon(!individualSoundsDisabled, inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Sound Sync");

                _uiSharedService.BooleanToColoredIcon(!individualAnimDisabled, inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Animation Sync");

                _uiSharedService.BooleanToColoredIcon(!individualVFXDisabled, inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("VFX Sync");

                ImGui.Separator();

                ImGuiHelpers.ScaledDummy(2f);
                ImGui.TextUnformatted("Suggested Permissions");
                ImGuiHelpers.ScaledDummy(2f);

                _uiSharedService.BooleanToColoredIcon(!_groupFullInfoDto.GroupPermissions.IsPreferDisableSounds(), inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Sound Sync");

                _uiSharedService.BooleanToColoredIcon(!_groupFullInfoDto.GroupPermissions.IsPreferDisableAnimations(), inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Animation Sync");

                _uiSharedService.BooleanToColoredIcon(!_groupFullInfoDto.GroupPermissions.IsPreferDisableVFX(), inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("VFX Sync");

                ImGui.EndTooltip();
            }
        }

        // Return leftmost button position like DrawFolderTag.cs
        return infoIconOffset;
    }

    private async Task LeaveAreaSyncshellWithConsentReset()
    {
        try
        {
            // First reset the consent
            await _areaBoundSyncshellService.ResetAreaBoundConsent(_groupFullInfoDto.Group.GID);
            
            // Then leave the syncshell using the service method to ensure proper cleanup
            await _areaBoundSyncshellService.LeaveAreaSyncshell(_groupFullInfoDto.Group.GID);
        }
        catch (Exception ex)
        {
            // Log error but don't show to user as this is a background operation
            // The UI will handle any error feedback through normal channels
        }
    }

    private async Task ShowWelcomeMessageOnDemand()
    {
        try
        {
            // Get the welcome page for this syncshell
            var welcomePage = await _apiController.GroupGetWelcomePage(new GroupDto(_groupFullInfoDto.Group));
            
            if (welcomePage != null && welcomePage.IsEnabled)
            {
                // Publish the OpenWelcomePageMessage to show the welcome page
                _spheneMediator.Publish(new OpenWelcomePageMessage(welcomePage, _groupFullInfoDto));
            }
        }
        catch (Exception ex)
        {
            // Log error but don't show to user as this is an optional feature
            // Could add a subtle notification here if needed
        }
    }
}
