using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data.Extensions;
using Sphene.PlayerData.Pairs;
using Sphene.UI.Handlers;
using Sphene.WebAPI;
using Sphene.SpheneConfiguration;
using System.Collections.Immutable;
using System.Numerics;

namespace Sphene.UI.Components;

public class DrawFolderTag : DrawFolderBase
{
    private readonly ApiController _apiController;
    private readonly SelectPairForTagUi _selectPairForTagUi;
    private readonly SpheneConfigService _configService;

    public DrawFolderTag(string id, IImmutableList<DrawUserPair> drawPairs, IImmutableList<Pair> allPairs,
        TagHandler tagHandler, ApiController apiController, SelectPairForTagUi selectPairForTagUi, UiSharedService uiSharedService, SpheneConfigService configService)
        : base(id, drawPairs, allPairs, tagHandler, uiSharedService, 0f, false) // Consistent width with other containers, not a syncshell folder
    {
        _apiController = apiController;
        _selectPairForTagUi = selectPairForTagUi;
        _configService = configService;
    }

    protected override bool RenderIfEmpty => _id switch
    {
        TagHandler.CustomUnpairedTag => false,
        TagHandler.CustomOnlineTag => false,
        TagHandler.CustomOfflineTag => false,
        TagHandler.CustomPausedTag => false,
        TagHandler.CustomVisibleTag => false,
        TagHandler.CustomAllTag => true,
        TagHandler.CustomOfflineSyncshellTag => false,
        _ => true,
    };

    protected override bool RenderMenu => false; // We'll draw the menu manually in DrawRightSide

    public override void Draw()
    {
        if (!RenderIfEmpty && !DrawPairs.Any()) return;

        using var id = ImRaii.PushId("folder_" + _id);
        var folderWidth = FolderWidth + 38.0f;
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
                ImGui.TextUnformatted("No users (online)");
            }

            ImGui.Separator();
        }
    }

    private bool RenderPause => _id switch
    {
        TagHandler.CustomUnpairedTag => false,
        TagHandler.CustomOnlineTag => false,
        TagHandler.CustomOfflineTag => false,
        TagHandler.CustomPausedTag => false,
        TagHandler.CustomVisibleTag => false,
        TagHandler.CustomAllTag => false,
        TagHandler.CustomOfflineSyncshellTag => false,
        _ => true,
    } && _allPairs.Any();

    private bool RenderCount => _id switch
    {
        TagHandler.CustomUnpairedTag => false,
        TagHandler.CustomOnlineTag => false,
        TagHandler.CustomOfflineTag => false,
        TagHandler.CustomPausedTag => false,
        TagHandler.CustomVisibleTag => false,
        TagHandler.CustomAllTag => false,
        TagHandler.CustomOfflineSyncshellTag => false,
        _ => true
    };

    protected override float DrawIcon()
    {
        var icon = _id switch
        {
            TagHandler.CustomUnpairedTag => FontAwesomeIcon.ArrowsLeftRight,
            TagHandler.CustomOnlineTag => FontAwesomeIcon.Link,
            TagHandler.CustomOfflineTag => FontAwesomeIcon.Unlink,
            TagHandler.CustomPausedTag => FontAwesomeIcon.Pause,
            TagHandler.CustomOfflineSyncshellTag => FontAwesomeIcon.Unlink,
            TagHandler.CustomVisibleTag => FontAwesomeIcon.Eye,
            TagHandler.CustomAllTag => FontAwesomeIcon.User,
            _ => FontAwesomeIcon.UserFriends
        };

        ImGui.AlignTextToFramePadding();
        _uiSharedService.IconText(icon);

        if (RenderCount)
        {
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing with { X = ImGui.GetStyle().ItemSpacing.X / 2f }))
            {
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();

                ImGui.TextUnformatted("[" + OnlinePairs.ToString() + "]");
            }
            UiSharedService.AttachToolTip(OnlinePairs + " online" + Environment.NewLine + TotalPairs + " total");
        }
        ImGui.SameLine();
        return ImGui.GetCursorPosX();
    }

    protected override void DrawMenu(float menuWidth)
    {
        ImGui.TextUnformatted("Group Menu");
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Users, "Select Pairs", menuWidth, true))
        {
            _selectPairForTagUi.Open(_id);
        }
        UiSharedService.AttachToolTip("Select Individual Pairs for this Pair Group");
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Delete Pair Group", menuWidth, true) && UiSharedService.CtrlPressed())
        {
            _tagHandler.RemoveTag(_id);
        }
        UiSharedService.AttachToolTip("Hold CTRL to remove this Group permanently." + Environment.NewLine +
            "Note: this will not unpair with users in this Group.");
    }

    protected override void DrawName(float width)
    {
        ImGui.AlignTextToFramePadding();

        string name = _id switch
        {
            TagHandler.CustomUnpairedTag => "One-sided Individual Pairs",
            TagHandler.CustomOnlineTag => "Online",
            TagHandler.CustomOfflineTag => "Offline / Paused by other",
            TagHandler.CustomPausedTag => "Paused by you",
            TagHandler.CustomOfflineSyncshellTag => "Offline Syncshell Users",
            TagHandler.CustomVisibleTag => "Visible",
            TagHandler.CustomAllTag => "Users",
            _ => _id
        };

        var textColor = _id switch
        {
            TagHandler.CustomOfflineTag => SpheneCustomTheme.CurrentTheme.CompactOfflinePausedText,
            TagHandler.CustomOfflineSyncshellTag => SpheneCustomTheme.CurrentTheme.CompactOfflineSyncshellText,
            TagHandler.CustomVisibleTag => SpheneCustomTheme.CurrentTheme.CompactVisibleText,
            TagHandler.CustomUnpairedTag => SpheneCustomTheme.CurrentTheme.CompactPairsText,
            TagHandler.CustomOnlineTag => SpheneCustomTheme.CurrentTheme.CompactPairsText,
            TagHandler.CustomPausedTag => SpheneCustomTheme.CurrentTheme.CompactPairsText,
            TagHandler.CustomAllTag => SpheneCustomTheme.CurrentTheme.CompactPairsText,
            _ => SpheneCustomTheme.CurrentTheme.CompactPairsText
        };

        UiSharedService.ColorText(name, textColor);
    }

    protected override float DrawRightSide(float currentRightSideX)
    {
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        // Check if we should render menu for this tag
        var shouldRenderMenu = _id switch
        {
            TagHandler.CustomUnpairedTag => false,
            TagHandler.CustomOnlineTag => false,
            TagHandler.CustomOfflineTag => false,
            TagHandler.CustomPausedTag => false,
            TagHandler.CustomVisibleTag => false,
            TagHandler.CustomAllTag => false,
            TagHandler.CustomOfflineSyncshellTag => false,
            _ => true,
        };

        // Draw menu button first (rightmost position)
        var menuButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.EllipsisV);
        var menuButtonOffset = currentRightSideX - menuButtonSize.X;
        
        if (shouldRenderMenu)
        {
            ImGui.SameLine(menuButtonOffset);
            // Apply brighter blue tint to tag buttons for better visibility
            using var menuButtonColor = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.3f, 0.5f, 0.9f, 0.4f));
            using var menuButtonHoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 1.0f, 0.6f));
            using var menuButtonActiveColor = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.7f, 1.0f, 0.8f));
            
            if (_uiSharedService.IconButton(FontAwesomeIcon.EllipsisV, menuButtonSize.Y, null, null, menuButtonSize.X, ButtonStyleKeys.PairTag_Menu))
            {
                ImGui.OpenPopup("User Flyout Menu");
            }
            using (SpheneCustomTheme.ApplyContextMenuTheme())
            {
                if (ImGui.BeginPopup("User Flyout Menu"))
                {
                    using (ImRaii.PushId($"buttons-{_id}")) DrawMenu(200f); // Use fixed width for menu
                    ImGui.EndPopup();
                }
            }
        }

        if (!RenderPause) return shouldRenderMenu ? menuButtonOffset : currentRightSideX;

        var allArePaused = _allPairs.All(pair => pair.UserPair!.OwnPermissions.IsPaused());
        var pauseButton = allArePaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var pauseButtonSize = _uiSharedService.GetIconButtonSize(pauseButton);

        // Position pause button to the left of menu button
        var buttonPauseOffset = (shouldRenderMenu ? menuButtonOffset : currentRightSideX) - pauseButtonSize.X - spacingX;
        ImGui.SameLine(buttonPauseOffset);
        
        // Apply brighter blue tint to pause button for better visibility
        using var pauseButtonColor = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.3f, 0.5f, 0.9f, 0.4f));
        using var pauseButtonHoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 1.0f, 0.6f));
        using var pauseButtonActiveColor = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.7f, 1.0f, 0.8f));
        
        if (_uiSharedService.IconButton(pauseButton, pauseButtonSize.Y, null, null, pauseButtonSize.X, ButtonStyleKeys.PairTag_Pause))
        {
            if (allArePaused)
            {
                ResumeAllPairs(_allPairs);
            }
            else
            {
                PauseRemainingPairs(_allPairs);
            }
        }
        if (allArePaused)
        {
            UiSharedService.AttachToolTip($"Resume pairing with all pairs in {_id}");
        }
        else
        {
            UiSharedService.AttachToolTip($"Pause pairing with all pairs in {_id}");
        }

        return buttonPauseOffset;
    }

    private void PauseRemainingPairs(IEnumerable<Pair> availablePairs)
    {
        _ = _apiController.SetBulkPermissions(new(availablePairs
            .ToDictionary(g => g.UserData.UID, g =>
        {
            var perm = g.UserPair.OwnPermissions;
            perm.SetPaused(paused: true);
            return perm;
        }, StringComparer.Ordinal), new(StringComparer.Ordinal)))
            .ConfigureAwait(false);
    }

    private void ResumeAllPairs(IEnumerable<Pair> availablePairs)
    {
        _ = _apiController.SetBulkPermissions(new(availablePairs
            .ToDictionary(g => g.UserData.UID, g =>
            {
                var perm = g.UserPair.OwnPermissions;
                perm.SetPaused(paused: false);
                return perm;
            }, StringComparer.Ordinal), new(StringComparer.Ordinal)))
            .ConfigureAwait(false);
    }
}
