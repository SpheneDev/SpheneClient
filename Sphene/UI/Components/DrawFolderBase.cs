using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.PlayerData.Pairs;
using Sphene.UI.Handlers;
using System.Collections.Immutable;

namespace Sphene.UI.Components;

public abstract class DrawFolderBase : IDrawFolder, IDisposable
{
    public IImmutableList<DrawUserPair> DrawPairs { get; init; }
    protected readonly string _id;
    protected readonly IImmutableList<Pair> _allPairs;
    protected readonly TagHandler _tagHandler;
    protected readonly UiSharedService _uiSharedService;
    protected readonly float _widthOffset;
    protected readonly bool _isSyncshellFolder;
    private float _menuWidth = -1;
    public int OnlinePairs => DrawPairs.Count(u => u.Pair.IsOnline);
    public int TotalPairs => _allPairs.Count;

    protected DrawFolderBase(string id, IImmutableList<DrawUserPair> drawPairs,
        IImmutableList<Pair> allPairs, TagHandler tagHandler, UiSharedService uiSharedService, float widthOffset = 0f, bool isSyncshellFolder = false)
    {
        _id = id;
        DrawPairs = drawPairs;
        _allPairs = allPairs;
        _tagHandler = tagHandler;
        _uiSharedService = uiSharedService;
        _widthOffset = widthOffset;
        _isSyncshellFolder = isSyncshellFolder;
    }

    protected abstract bool RenderIfEmpty { get; }
    protected abstract bool RenderMenu { get; }

    protected float FolderWidth => _isSyncshellFolder ? UiSharedService.GetSyncshellFolderWidth(_widthOffset) : UiSharedService.GetBaseFolderWidth(_widthOffset);

    public virtual void Draw()
    {
        if (!RenderIfEmpty && !DrawPairs.Any()) return;

        using var id = ImRaii.PushId("folder_" + _id);
        var folderWidth = FolderWidth + (_isSyncshellFolder ? 0f : 30f);
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
            using var indent = ImRaii.PushIndent(_uiSharedService.GetIconSize(FontAwesomeIcon.EllipsisV).X + ImGui.GetStyle().ItemSpacing.X, false);
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

    public virtual void Dispose()
    {
        // Dispose all DrawUserPair instances to clean up event subscriptions
        foreach (var drawPair in DrawPairs)
        {
            drawPair.Dispose();
        }
    }

    protected abstract float DrawIcon();

    protected abstract void DrawMenu(float menuWidth);

    protected abstract void DrawName(float width);

    protected abstract float DrawRightSide(float currentRightSideX);

    protected float DrawRightSideInternal()
    {
        var barButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.EllipsisV);
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        // Use container-relative positioning for buttons
        var containerWidth = ImGui.GetContentRegionAvail().X;
        var actualWindowEndX = ImGui.GetCursorPosX() + containerWidth;

        // Flyout Menu
        var rightSideStart = actualWindowEndX - (RenderMenu ? (barButtonSize.X + spacingX) : spacingX);

        if (RenderMenu)
        {
            ImGui.SameLine(actualWindowEndX - barButtonSize.X);
            if (_uiSharedService.IconButton(FontAwesomeIcon.EllipsisV))
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
        }

        return DrawRightSide(rightSideStart);
    }

    public void RefreshIcons()
    {
        // Refresh icons for all DrawUserPair instances without recreating them
        foreach (var drawPair in DrawPairs)
        {
            drawPair.RefreshIcon();
        }
    }
}
