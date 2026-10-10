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
using Sphene.UI.Design;
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
                return DrawPairs.Count(u => !u.Pair.IsEffectivelyOffline);
            }
            
            // For regular syncshells, count both visible and non-visible online users
            // Start with visible pairs that are online (from DrawPairs)
            var visibleOnlineCount = DrawPairs.Count(u => !u.Pair.IsEffectivelyOffline);
            
            // Get all users in the syncshell from GroupPairUserInfos
            var allSyncshellUsers = _groupFullInfoDto.GroupPairUserInfos.Keys.ToList();
            
            // Count syncshell users that are online but not already counted in visible pairs
            var visibleUserUIDs = DrawPairs.Select(dp => dp.Pair.UserData.UID).ToHashSet(StringComparer.Ordinal);
            int additionalOnlineCount = 0;
            
            foreach (var userUID in allSyncshellUsers)
            {
                // Skip if this user is already counted in visible pairs
                if (visibleUserUIDs.Contains(userUID))
                    continue;
                    
                var pair = _pairManager.GetPairByUID(userUID);
                if (pair != null && !pair.IsEffectivelyOffline)
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
            var visibleUserUIDs = DrawPairs.Select(dp => dp.Pair.UserData.UID).ToHashSet(StringComparer.Ordinal);
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

            SpheneUI.Icons.Icon(icon);
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

        SpheneUI.Layout.ScrollAreaSeparator(
            SpheneTheme.Current.CompactPairListSeparatorPadding);

        // if opened draw content
        if (_tagHandler.IsTagOpen(_id))
        {
            var baseIndent = SpheneUI.Icons.Size(FontAwesomeIcon.EllipsisV).X + ImGui.GetStyle().ItemSpacing.X;
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
                    SpheneUI.Text.Plain($"No visible users ({OnlinePairs} online, {TotalPairs} total)");
                }
                else
                {
                    SpheneUI.Text.Plain("No users in syncshell");
                }
            }

            SpheneUI.Layout.ScrollAreaSeparator(
                SpheneTheme.Current.CompactPairListSeparatorPadding);
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
                SpheneUI.Icons.Icon(FontAwesomeIcon.MapMarkerAlt);
                SpheneUI.AttachTooltip("This is an area-bound syncshell");
            }
            else
            {
                // Clickable indicator when welcome messages are disabled - looks like normal icon but is clickable
                SpheneUI.Icons.Icon(FontAwesomeIcon.MapMarkerAlt);
                if (ImGui.IsItemClicked())
                {
                    // Show welcome message on demand
                    _ = ShowWelcomeMessageOnDemand();
                }
                SpheneUI.AttachTooltip("This is an area-bound syncshell\nClick to view welcome message");
            }
        }
        else
        {
            // For normal Syncshells: Keep original order with group emoji first
            SpheneUI.Icons.Icon(_groupFullInfoDto.GroupPermissions.IsDisableInvites() ? FontAwesomeIcon.Lock : FontAwesomeIcon.Users);
            if (_groupFullInfoDto.GroupPermissions.IsDisableInvites())
            {
                SpheneUI.AttachTooltip("Syncshell " + _groupFullInfoDto.GroupAliasOrGID + " is closed for invites");
            }
        }

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing with { X = ImGui.GetStyle().ItemSpacing.X / 2f }))
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();

            SpheneUI.Text.Plain("[" + OnlinePairs.ToString() + "]");
        }
        SpheneUI.AttachTooltip(OnlinePairs + " online" + Environment.NewLine + TotalPairs + " total");

        ImGui.SameLine();
        if (IsOwner)
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(FontAwesomeIcon.Crown);
            SpheneUI.AttachTooltip("You are the owner of " + _groupFullInfoDto.GroupAliasOrGID);
        }
        else if (IsModerator)
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(FontAwesomeIcon.UserShield);
            SpheneUI.AttachTooltip("You are a moderator in " + _groupFullInfoDto.GroupAliasOrGID);
        }
        else if (IsPinned)
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(FontAwesomeIcon.Thumbtack);
            SpheneUI.AttachTooltip("You are pinned in " + _groupFullInfoDto.GroupAliasOrGID);
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
                SpheneUI.Icons.Icon(FontAwesomeIcon.MapMarkerAlt);
                SpheneUI.AttachTooltip("This is an area-bound syncshell");
            }
            else
            {
                // Clickable indicator when welcome messages are disabled - looks like normal icon but is clickable
                SpheneUI.Icons.Icon(FontAwesomeIcon.MapMarkerAlt);
                if (ImGui.IsItemClicked())
                {
                    // Show welcome message on demand
                    _ = ShowWelcomeMessageOnDemand();
                }
                SpheneUI.AttachTooltip("This is an area-bound syncshell\nClick to view welcome message");
            }
        }

        ImGui.SameLine();
        return ImGui.GetCursorPosX();
    }

    protected override void DrawMenu(float menuWidth)
    {
        SpheneUI.Text.Plain("Syncshell Menu (" + _groupFullInfoDto.GroupAliasOrGID + ")");
        SpheneUI.Layout.Separator();

        SpheneUI.Text.Plain("General Syncshell Actions");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Copy, "Copy ID", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            ImGui.CloseCurrentPopup();
            ImGui.SetClipboardText(_groupFullInfoDto.GroupAliasOrGID);
        }
        SpheneUI.AttachTooltip("Copy Syncshell ID to Clipboard");

        if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.StickyNote, "Copy Notes", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            ImGui.CloseCurrentPopup();
            ImGui.SetClipboardText(UiSharedService.GetNotes(DrawPairs.Select(k => k.Pair).ToList()));
        }
        SpheneUI.AttachTooltip("Copies all your notes for all users in this Syncshell to the clipboard." + Environment.NewLine + "They can be imported via Settings -> General -> Notes -> Import notes from clipboard");

        // Check if this is an area-bound syncshell to show appropriate leave button
        bool isAreaBound = _areaBoundSyncshellService.IsAreaBoundSyncshell(_groupFullInfoDto.Group.GID);
        bool isOwner = string.Equals(_groupFullInfoDto.OwnerUID, _apiController.UID, StringComparison.Ordinal);
        
        if (isOwner)
        {
            // Show "Delete Syncshell" button for owners
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Trash, "Delete Syncshell", menuWidth, ButtonStyleKeys.ContextMenu_Item) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.GroupDelete(new(_groupFullInfoDto.Group));
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip("Hold CTRL and click to delete this Syncshell" + Environment.NewLine + 
                "WARNING: This action is irreversible and will permanently delete the Syncshell for all members.");
        }
        else if (isAreaBound)
        {
            // Show "Leave Area Syncshell" button for non-owners in area-bound syncshells
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.MapMarkerAlt, "Leave Area Syncshell", menuWidth, ButtonStyleKeys.ContextMenu_Item) && UiSharedService.CtrlPressed())
            {
                _ = LeaveAreaSyncshellWithConsentReset();
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip("Hold CTRL and click to leave this Area Syncshell" + Environment.NewLine + 
                "This will reset your consent and you'll need to accept again when re-entering the area.");
        }
        else
        {
            // Show regular "Leave Syncshell" button for non-owners in regular syncshells
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.ArrowCircleLeft, "Leave Syncshell", menuWidth, ButtonStyleKeys.ContextMenu_Item) && UiSharedService.CtrlPressed())
            {
                _ = _apiController.GroupLeave(_groupFullInfoDto);
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip("Hold CTRL and click to leave this Syncshell");
        }

        SpheneUI.Layout.Separator();
        SpheneUI.Text.Plain("Permission Settings");
        var perm = _groupFullInfoDto.GroupUserPermissions;
        bool disableSounds = perm.IsDisableSounds();
        bool disableAnims = perm.IsDisableAnimations();
        bool disableVfx = perm.IsDisableVFX();

        if ((_groupFullInfoDto.GroupPermissions.IsPreferDisableAnimations() != disableAnims
            || _groupFullInfoDto.GroupPermissions.IsPreferDisableSounds() != disableSounds
            || _groupFullInfoDto.GroupPermissions.IsPreferDisableVFX() != disableVfx)
            && SpheneUI.Icons.ActionButton(FontAwesomeIcon.Check, "Align with suggested permissions", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            perm.SetDisableVFX(_groupFullInfoDto.GroupPermissions.IsPreferDisableVFX());
            perm.SetDisableSounds(_groupFullInfoDto.GroupPermissions.IsPreferDisableSounds());
            perm.SetDisableAnimations(_groupFullInfoDto.GroupPermissions.IsPreferDisableAnimations());
            _ = _apiController.GroupChangeIndividualPermissionState(new(_groupFullInfoDto.Group, new(_apiController.UID), perm));
            ImGui.CloseCurrentPopup();
        }

        if (SpheneUI.Icons.ActionButton(disableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeOff, disableSounds ? "Enable Sound Sync" : "Disable Sound Sync", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            perm.SetDisableSounds(!disableSounds);
            _ = _apiController.GroupChangeIndividualPermissionState(new(_groupFullInfoDto.Group, new(_apiController.UID), perm));
            ImGui.CloseCurrentPopup();
        }

        if (SpheneUI.Icons.ActionButton(disableAnims ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop, disableAnims ? "Enable Animation Sync" : "Disable Animation Sync", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            perm.SetDisableAnimations(!disableAnims);
            _ = _apiController.GroupChangeIndividualPermissionState(new(_groupFullInfoDto.Group, new(_apiController.UID), perm));
            ImGui.CloseCurrentPopup();
        }

        if (SpheneUI.Icons.ActionButton(disableVfx ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle, disableVfx ? "Enable VFX Sync" : "Disable VFX Sync", menuWidth, ButtonStyleKeys.ContextMenu_Item))
        {
            perm.SetDisableVFX(!disableVfx);
            _ = _apiController.GroupChangeIndividualPermissionState(new(_groupFullInfoDto.Group, new(_apiController.UID), perm));
            ImGui.CloseCurrentPopup();
        }

        if (IsModerator || IsOwner)
        {
            SpheneUI.Layout.Separator();
            SpheneUI.Text.Plain("Syncshell Admin Functions");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Cog, "Open Admin Panel", menuWidth, ButtonStyleKeys.ContextMenu_Item))
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
        var pauseButtonSize = SpheneUI.Icons.ButtonSize(pauseIcon);
        var menuButtonSize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.EllipsisV);

        var userCogButtonSize = SpheneUI.Icons.Size(FontAwesomeIcon.UsersCog);

        var individualSoundsDisabled = _groupFullInfoDto.GroupUserPermissions.IsDisableSounds();
        var individualAnimDisabled = _groupFullInfoDto.GroupUserPermissions.IsDisableAnimations();
        var individualVFXDisabled = _groupFullInfoDto.GroupUserPermissions.IsDisableVFX();

        // Use container-relative positioning for buttons
        var containerWidth = SpheneUI.Controls.BoundedAvailX();
        var actualWindowEndX = ImGui.GetCursorPosX() + containerWidth;
        
        var menuButtonOffset = actualWindowEndX - menuButtonSize.X;
        ImGui.SameLine(menuButtonOffset);
        
        using var menuButtonColor = ImRaii.PushColor(ImGuiCol.Button, SpheneTheme.Current.CompactSyncshellButton);
        using var menuButtonHoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, SpheneTheme.Current.CompactSyncshellButtonHovered);
        using var menuButtonActiveColor = ImRaii.PushColor(ImGuiCol.ButtonActive, SpheneTheme.Current.CompactSyncshellButtonActive);
        
        if (SpheneUI.Icons.Button(FontAwesomeIcon.EllipsisV, menuButtonSize.Y, null, null, menuButtonSize.X, ButtonStyleKeys.GroupSyncshell_Menu))
        {
            ImGui.OpenPopup("User Flyout Menu");
        }
        using (SpheneUI.Theme.ContextMenuScope())
        {
            if (SpheneUI.Popups.BeginStyled("User Flyout Menu"))
            {
                using (ImRaii.PushId($"buttons-{_id}")) DrawMenu(_menuWidth);
                _menuWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                SpheneUI.Popups.EndStyled();
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
        using var pauseButtonColor = ImRaii.PushColor(ImGuiCol.Button, SpheneTheme.Current.CompactSyncshellButton);
        using var pauseButtonHoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, SpheneTheme.Current.CompactSyncshellButtonHovered);
        using var pauseButtonActiveColor = ImRaii.PushColor(ImGuiCol.ButtonActive, SpheneTheme.Current.CompactSyncshellButtonActive);
        
        if (SpheneUI.Icons.Button(pauseIcon, pauseButtonSize.Y, null, null, pauseButtonSize.X, ButtonStyleKeys.GroupSyncshell_Pause))
        {
            var perm = _groupFullInfoDto.GroupUserPermissions;
            perm.SetPaused(!perm.IsPaused());
            _ = _apiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(_groupFullInfoDto.Group, new(_apiController.UID), perm));
        }

        // Calculate info icon position (left of pause button) - MUST match other button positions
        var infoIconOffset = actualWindowEndX - userCogButtonSize.X - spacingX - pauseButtonSize.X - spacingX - menuButtonSize.X;
        ImGui.SameLine(infoIconOffset);

        ImGui.AlignTextToFramePadding();

        SpheneUI.Icons.Icon(FontAwesomeIcon.UsersCog, (_groupFullInfoDto.GroupPermissions.IsPreferDisableAnimations() != individualAnimDisabled
            || _groupFullInfoDto.GroupPermissions.IsPreferDisableSounds() != individualSoundsDisabled
            || _groupFullInfoDto.GroupPermissions.IsPreferDisableVFX() != individualVFXDisabled) ? ImGuiColors.DalamudYellow : null);
        if (ImGui.IsItemHovered())
        {
            using (SpheneUI.Theme.TooltipScope())
            {
                ImGui.BeginTooltip();

                SpheneUI.Text.Plain("Syncshell Permissions");
                SpheneUI.Layout.Space(SpheneUI.Spacing.X2);

                SpheneUI.Icons.BoolIcon(!individualSoundsDisabled, inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("Sound Sync");

                SpheneUI.Icons.BoolIcon(!individualAnimDisabled, inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("Animation Sync");

                SpheneUI.Icons.BoolIcon(!individualVFXDisabled, inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("VFX Sync");

                ImGui.Separator();

                SpheneUI.Layout.Space(SpheneUI.Spacing.X2);
                SpheneUI.Text.Plain("Suggested Permissions");
                SpheneUI.Layout.Space(SpheneUI.Spacing.X2);

                SpheneUI.Icons.BoolIcon(!_groupFullInfoDto.GroupPermissions.IsPreferDisableSounds(), inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("Sound Sync");

                SpheneUI.Icons.BoolIcon(!_groupFullInfoDto.GroupPermissions.IsPreferDisableAnimations(), inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("Animation Sync");

                SpheneUI.Icons.BoolIcon(!_groupFullInfoDto.GroupPermissions.IsPreferDisableVFX(), inline: false);
                ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("VFX Sync");

                ImGui.EndTooltip();
            }
        }

        // Return leftmost button position like DrawFolderTag.cs
        return infoIconOffset;
    }

    private async Task LeaveAreaSyncshellWithConsentReset()
    {
        await _areaBoundSyncshellService.ResetAreaBoundConsent(_groupFullInfoDto.Group.GID).ConfigureAwait(false);
        await _areaBoundSyncshellService.LeaveAreaSyncshell(_groupFullInfoDto.Group.GID).ConfigureAwait(false);
    }

    private async Task ShowWelcomeMessageOnDemand()
    {
        var welcomePage = await _apiController.GroupGetWelcomePage(new GroupDto(_groupFullInfoDto.Group)).ConfigureAwait(false);
        if (welcomePage != null && welcomePage.IsEnabled)
        {
            _spheneMediator.Publish(new OpenWelcomePageMessage(welcomePage, _groupFullInfoDto));
        }
    }
}
