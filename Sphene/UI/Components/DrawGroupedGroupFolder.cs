using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.UI.Handlers;
using System.Collections.Immutable;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public class DrawGroupedGroupFolder : IDrawFolder
{
    private readonly IEnumerable<IDrawFolder> _groups;
    private readonly TagHandler _tagHandler;
    public IImmutableList<DrawUserPair> DrawPairs => throw new NotSupportedException();
    public int OnlinePairs => _groups.SelectMany(g => g.DrawPairs).Where(g => !g.Pair.IsEffectivelyOffline).DistinctBy(g => g.Pair.UserData.UID).Count();
    public int TotalPairs => _groups.Sum(g => g.TotalPairs);

    public DrawGroupedGroupFolder(IEnumerable<IDrawFolder> groups, TagHandler tagHandler)
    {
        _groups = groups;
        _tagHandler = tagHandler;
    }

    public void Draw()
    {
        if (!_groups.Any()) return;

        string _id = "__folder_syncshells";
        using var id = ImRaii.PushId(_id);
        var theme = SpheneTheme.Current;
        var scale = SpheneUI.Scale;
        var width = MathF.Max(1f, SpheneUI.Controls.BoundedAvailX());
        var headerH = ImGui.GetFrameHeight() + 4f * scale;

        using (ImRaii.Child("folder__" + _id, new Vector2(width, headerH), false, ImGuiWindowFlags.NoScrollbar))
        {
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            var rowHovered = ImGui.IsWindowHovered();
            var open = _tagHandler.IsTagOpen(_id);

            var rounding = MathF.Min(theme.CompactChildRounding, headerH * 0.5f);
            dl.AddRectFilled(min, min + new Vector2(width, headerH),
                SpheneUI.ColorUtils.ToU32(rowHovered
                    ? SpheneUI.ColorUtils.WithAlpha(theme.CompactHover, 0.30f)
                    : open
                        ? SpheneUI.ColorUtils.WithAlpha(theme.CompactAccent, 0.08f)
                        : SpheneUI.ColorUtils.WithAlpha(theme.CompactFrameBg, 0.45f)), rounding);
            dl.AddRect(min, min + new Vector2(width, headerH),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(theme.CompactBorder,
                    open ? 0.35f : 0.14f)), rounding);

            var lineTop = min.Y + MathF.Max(0f, (headerH - ImGui.GetFrameHeight()) * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(min.X + SpheneUI.Spacing.X8.Scaled, lineTop));
            SpheneUI.Icons.Icon(open ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight,
                open ? theme.CompactAccent : theme.CompactTextSecondary);

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(FontAwesomeIcon.UsersRectangle);
            ImGui.SameLine();

            var countText = $"{OnlinePairs}/{TotalPairs}";
            var pillW = SpheneUI.Layout.BadgeWidth(countText);
            var pillH = SpheneUI.Layout.BadgeHeight;
            var pillPos = ImGui.GetCursorScreenPos()
                + new Vector2(0f, MathF.Max(0f, (ImGui.GetFrameHeight() - pillH) * 0.5f));
            ImGui.Dummy(new Vector2(pillW, pillH));
            ImGui.SetCursorScreenPos(pillPos);
            SpheneUI.Layout.Badge(countText, theme.CompactAccent);
            SpheneUI.AttachTooltip(OnlinePairs + " online in all of your joined syncshells" + Environment.NewLine +
                TotalPairs + " pairs combined in all of your joined syncshells");

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Colored("All Syncshells", theme.CompactAllSyncshellsText);

            if (rowHovered && !ImGui.IsAnyItemHovered()
                && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _tagHandler.SetTagOpen(_id, !open);
            }
        }

        SpheneUI.Layout.ScrollAreaSeparator(
            SpheneTheme.Current.CompactPairListSeparatorPadding);

        if (_tagHandler.IsTagOpen(_id))
        {
            // same indent the base folders use — nested group headers align
            // with the pair rows inside tag folders
            using var indent = ImRaii.PushIndent(
                SpheneUI.Icons.Size(FontAwesomeIcon.EllipsisV).X + ImGui.GetStyle().ItemSpacing.X, false);
            foreach (var entry in _groups)
            {
                entry.Draw();
            }
        }
    }

    public void RefreshIcons()
    {
        foreach (var group in _groups)
        {
            group.RefreshIcons();
        }
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            foreach (var group in _groups)
            {
                group.Dispose();
            }
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
