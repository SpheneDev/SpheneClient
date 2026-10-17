using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Dto.Group;
using Sphene.SpheneConfiguration;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI.Design;



namespace Sphene.UI.Handlers;

public class IdDisplayHandler
{
    private readonly SpheneConfigService _SpheneConfigService;
    private readonly SpheneMediator _mediator;
    private readonly ServerConfigurationManager _serverManager;
    private readonly Dictionary<string, bool> _showIdForEntry = new(StringComparer.Ordinal);
    private string _editComment = string.Empty;
    private string _editEntry = string.Empty;

    public IdDisplayHandler(SpheneMediator mediator, ServerConfigurationManager serverManager, SpheneConfigService SpheneConfigService)
    {
        _mediator = mediator;
        _serverManager = serverManager;
        _SpheneConfigService = SpheneConfigService;
    }

    public void DrawGroupText(string id, GroupFullInfoDto group, float textPosX, Func<float> editBoxWidth)
    {
        ImGui.SameLine(textPosX);
        (bool textIsUid, string playerText) = GetGroupText(group);
        if (!string.Equals(_editEntry, group.GID, StringComparison.Ordinal))
        {
            ImGui.AlignTextToFramePadding();

            using (ImRaii.PushFont(UiBuilder.MonoFont, textIsUid))
                SpheneUI.Text.Plain(playerText);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                var prevState = textIsUid;
                if (_showIdForEntry.ContainsKey(group.GID))
                {
                    prevState = _showIdForEntry[group.GID];
                }
                _showIdForEntry[group.GID] = !prevState;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                if (!string.IsNullOrEmpty(_editEntry))
                {
                    _serverManager.SetNoteForGid(_editEntry, _editComment, save: true);
                }

                _editComment = _serverManager.GetNoteForGid(group.GID) ?? string.Empty;
                _editEntry = group.GID;
            }
        }
        else
        {
            ImGui.AlignTextToFramePadding();

            ImGui.SetNextItemWidth(editBoxWidth.Invoke());
            if (SpheneUI.Controls.InputTextWithHint("", "Name/Notes", ref _editComment, 255, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                _serverManager.SetNoteForGid(group.GID, _editComment, save: true);
                _editEntry = string.Empty;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _editEntry = string.Empty;
            }
            SpheneUI.AttachTooltip("Hit ENTER to save\nRight click to cancel");
        }
    }

    public void DrawPairText(string id, Pair pair, float textPosX)
    {
        ImGui.SameLine(textPosX);
        ImGui.AlignTextToFramePadding();

        using (ImRaii.PushColor(ImGuiCol.Text, SpheneTheme.Current.CompactPairsText))
        {
            SpheneUI.Text.Plain(GetPlayerText(pair));
        }

        if (ImGui.IsItemHovered())
        {
            var note = pair.GetNote();
            var noteLine = string.IsNullOrEmpty(note)
                ? string.Empty
                : $"Note: {note}{Environment.NewLine}";
            ImGui.SetTooltip(noteLine
                + "Middle Mouse Button to open their profile in the hub");
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Middle))
        {
            _mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Friends, Friend: pair));
        }
    }

    /// <summary>
    /// Row-level activation for the profile popout: a right-click anywhere on the
    /// pair row opens it (the name text alone is too small a target, so
    /// DrawUserPair reports the row click here). The preload warms the profile
    /// fetch and texture decode in the same frame the toggle opens the strip.
    /// Avatar areas are excluded by the caller — those keep the pair tooltip.
    /// </summary>
    public void PairRowActivated(Pair pair)
    {
        if (pair == null || !_SpheneConfigService.Current.ProfilesShow
            || string.Equals(_editEntry, pair.UserData.UID, StringComparison.Ordinal))
        {
            return;
        }

        _mediator.Publish(new ProfilePopoutPreload(pair));
        _mediator.Publish(new ProfilePopoutToggle(pair));
    }

    public (bool isGid, string text) GetGroupText(GroupFullInfoDto group)
    {
        var textIsGid = true;
        bool showUidInsteadOfName = ShowGidInsteadOfName(group);
        string? groupText = _serverManager.GetNoteForGid(group.GID);
        if (!showUidInsteadOfName && groupText != null)
        {
            if (string.IsNullOrEmpty(groupText))
            {
                groupText = group.GroupAliasOrGID;
            }
            else
            {
                textIsGid = false;
            }
        }
        else
        {
            groupText = group.GroupAliasOrGID;
        }

        return (textIsGid, groupText!);
    }

    public static string GetPlayerText(Pair pair)
    {
        // the row name is always the display name — notes and character
        // names never take over, and the raw UID is not a user-facing
        // identity anymore (handle/display name are)
        return pair.UserData.PreferredName;
    }

    internal void Clear()
    {
        _editEntry = string.Empty;
        _editComment = string.Empty;
    }

    internal void OpenProfile(Pair entry)
    {
        _mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Friends, Friend: entry));
    }

    private bool ShowGidInsteadOfName(GroupFullInfoDto group)
    {
        _showIdForEntry.TryGetValue(group.GID, out var showidInsteadOfName);

        return showidInsteadOfName;
    }
}
