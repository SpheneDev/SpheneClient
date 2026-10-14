using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.PlayerData.Pairs;
using Sphene.UI.Handlers;
using System.Collections.Immutable;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public abstract class DrawFolderBase : IDrawFolder
{
    public IImmutableList<DrawUserPair> DrawPairs { get; init; }
    protected readonly string _id;
    protected readonly IImmutableList<Pair> _allPairs;
    protected readonly TagHandler _tagHandler;
    protected readonly UiSharedService _uiSharedService;
    protected readonly float _widthOffset;
    protected readonly bool _isSyncshellFolder;
    private float _menuWidth = -1;
    public virtual int OnlinePairs => DrawPairs.Count(u => u.Pair.IsOnline);
    public virtual int TotalPairs => _allPairs.Count;

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

    /// <summary>Whether the header shows the online/total count pill.</summary>
    protected virtual bool ShowCountBadge => true;

    public virtual void Draw()
    {
        if (!RenderIfEmpty && !DrawPairs.Any()) return;

        using var id = ImRaii.PushId("folder_" + _id);
        DrawFolderHeader();

        SpheneUI.Layout.ScrollAreaSeparator(
            SpheneTheme.Current.CompactPairListSeparatorPadding);

        // if opened draw content
        if (_tagHandler.IsTagOpen(_id))
        {
            using var indent = ImRaii.PushIndent(SpheneUI.Icons.Size(FontAwesomeIcon.EllipsisV).X + ImGui.GetStyle().ItemSpacing.X, false);
            if (DrawPairs.Any())
            {
                foreach (var item in DrawPairs)
                {
                    item.DrawPairedClient();
                }
            }
            else
            {
                DrawEmptyFolderMessage();
            }

            SpheneUI.Layout.ScrollAreaSeparator(
                SpheneTheme.Current.CompactPairListSeparatorPadding);
        }
    }

    /// <summary>Text rendered inside an expanded but empty folder.</summary>
    protected virtual void DrawEmptyFolderMessage()
    {
        SpheneUI.Text.Muted("No users (online)");
    }

    /// <summary>
    /// Draws the collapsible folder header band: a rounded, hover-reactive
    /// surface spanning the padded content bound with an accent bar while
    /// open, the caret, folder icons, count pill, name and right-side
    /// buttons. Free areas of the band toggle the folder; real items keep
    /// their own interactions.
    /// </summary>
    protected void DrawFolderHeader()
    {
        var theme = SpheneTheme.Current;
        var scale = SpheneUI.Scale;
        var width = MathF.Max(1f, SpheneUI.Controls.BoundedAvailX());
        var headerH = ImGui.GetFrameHeight() + 4f * scale;

        using (ImRaii.Child("folder__" + _id, new Vector2(width, headerH), false, ImGuiWindowFlags.NoScrollbar))
        {
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(width, headerH);
            var rowHovered = ImGui.IsWindowHovered();
            var open = _tagHandler.IsTagOpen(_id);

            var rounding = MathF.Min(theme.CompactChildRounding, headerH * 0.5f);
            dl.AddRectFilled(min, max,
                SpheneUI.ColorUtils.ToU32(rowHovered
                    ? SpheneUI.ColorUtils.WithAlpha(theme.CompactHover, 0.30f)
                    : open
                        ? SpheneUI.ColorUtils.WithAlpha(theme.CompactAccent, 0.08f)
                        : SpheneUI.ColorUtils.WithAlpha(theme.CompactFrameBg, 0.45f)), rounding);

            var lineTop = min.Y + MathF.Max(0f, (headerH - ImGui.GetFrameHeight()) * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(min.X + SpheneUI.Spacing.X8.Scaled, lineTop));
            SpheneUI.Icons.Icon(open ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight,
                theme.CompactTextSecondary);

            ImGui.SameLine();
            var leftSideEnd = DrawIcon();

            if (ShowCountBadge)
            {
                leftSideEnd = DrawCountBadge();
            }

            ImGui.SameLine();
            var rightSideStart = DrawRightSideInternal();

            // draw name
            ImGui.SameLine(leftSideEnd);
            DrawName(rightSideStart - leftSideEnd);
            var nameMin = ImGui.GetItemRectMin();
            var nameMax = ImGui.GetItemRectMax();

            // row-wide toggle on free header areas only — the name keeps its
            // own click (uid/alias toggle, note edit) and real buttons hover
            // via IsAnyItemHovered
            var mouse = ImGui.GetMousePos();
            var overName = mouse.X >= nameMin.X && mouse.X <= nameMax.X
                && mouse.Y >= nameMin.Y && mouse.Y <= nameMax.Y;
            if (rowHovered && !overName && !ImGui.IsAnyItemHovered()
                && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _tagHandler.SetTagOpen(_id, !open);
            }
        }
    }

    /// <summary>Count pill drawn after the folder icon; returns the new name start X.</summary>
    private float DrawCountBadge()
    {
        var countText = $"{OnlinePairs}/{TotalPairs}";
        var pillW = SpheneUI.Layout.BadgeWidth(countText);
        var pillH = SpheneUI.Layout.BadgeHeight;
        var pillPos = ImGui.GetCursorScreenPos()
            + new Vector2(0f, MathF.Max(0f, (ImGui.GetFrameHeight() - pillH) * 0.5f));
        ImGui.Dummy(new Vector2(pillW, pillH));
        ImGui.SetCursorScreenPos(pillPos);
        SpheneUI.Layout.Badge(countText, SpheneTheme.Current.CompactAccent);
        SpheneUI.AttachTooltip(OnlinePairs + " online" + Environment.NewLine + TotalPairs + " total");
        return ImGui.GetCursorPosX() + ImGui.GetStyle().ItemSpacing.X;
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            foreach (var drawPair in DrawPairs)
            {
                drawPair.Dispose();
            }
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected abstract float DrawIcon();

    protected abstract void DrawMenu(float menuWidth);

    protected abstract void DrawName(float width);

    protected abstract float DrawRightSide(float currentRightSideX);

    protected float DrawRightSideInternal()
    {
        var barButtonSize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.EllipsisV);
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        // Use container-relative positioning for buttons
        var containerWidth = SpheneUI.Controls.BoundedAvailX();
        var actualWindowEndX = ImGui.GetCursorPosX() + containerWidth;

        // Flyout Menu
        var rightSideStart = actualWindowEndX - (RenderMenu ? (barButtonSize.X + spacingX) : spacingX);

        if (RenderMenu)
        {
            ImGui.SameLine(actualWindowEndX - barButtonSize.X);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.EllipsisV))
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
