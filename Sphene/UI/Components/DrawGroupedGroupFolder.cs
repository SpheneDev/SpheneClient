using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.UI.Handlers;
using System.Collections.Immutable;
using System.Numerics;
using Sphene.UI.Theme;

namespace Sphene.UI.Components;

public class DrawGroupedGroupFolder : IDrawFolder
{
    private readonly IEnumerable<IDrawFolder> _groups;
    private readonly TagHandler _tagHandler;
    private readonly UiSharedService _uiSharedService;

    public IImmutableList<DrawUserPair> DrawPairs => throw new NotSupportedException();
    public int OnlinePairs => _groups.SelectMany(g => g.DrawPairs).Where(g => g.Pair.IsOnline).DistinctBy(g => g.Pair.UserData.UID).Count();
    public int TotalPairs => _groups.Sum(g => g.TotalPairs);

    public DrawGroupedGroupFolder(IEnumerable<IDrawFolder> groups, TagHandler tagHandler, UiSharedService uiSharedService)
    {
        _groups = groups;
        _tagHandler = tagHandler;
        _uiSharedService = uiSharedService;
    }

    public void Draw()
    {
        if (!_groups.Any()) return;

        string _id = "__folder_syncshells";
        using var id = ImRaii.PushId(_id);
        // Use the same width calculation as DrawFolderBase for consistency
        var baseFolderWidth = UiSharedService.GetBaseFolderWidth();
        using (ImRaii.Child("folder__" + _id, new System.Numerics.Vector2(baseFolderWidth, ImGui.GetFrameHeight()), false, ImGuiWindowFlags.NoScrollbar))
        {
            
            ImGui.Dummy(new Vector2(0f, ImGui.GetFrameHeight()));
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 0f)))
                ImGui.SameLine();

            var icon = _tagHandler.IsTagOpen(_id) ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight;
            ImGui.AlignTextToFramePadding();

            _uiSharedService.IconText(icon);
            if (ImGui.IsItemClicked())
            {
                _tagHandler.SetTagOpen(_id, !_tagHandler.IsTagOpen(_id));
            }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.UsersRectangle);
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing with { X = ImGui.GetStyle().ItemSpacing.X / 2f }))
            {
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("[" + OnlinePairs.ToString() + "]");
            }
            UiSharedService.AttachToolTip(OnlinePairs + " online in all of your joined syncshells" + Environment.NewLine +
                TotalPairs + " pairs combined in all of your joined syncshells");
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            UiSharedService.ColorText("All Syncshells", SpheneCustomTheme.CurrentTheme.CompactAllSyncshellsText);
        }

        ImGui.Separator();

        if (_tagHandler.IsTagOpen(_id))
        {
            using var indent = ImRaii.PushIndent(20f);
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
