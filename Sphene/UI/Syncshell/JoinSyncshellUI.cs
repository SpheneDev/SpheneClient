using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data.Extensions;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto;
using Sphene.API.Dto.Group;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Syncshell;

internal class JoinSyncshellUI : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private string _desiredSyncshellToJoin = string.Empty;
    private GroupJoinInfoDto? _groupJoinInfo = null;
    private DefaultPermissionsDto _ownPermissions = null!;
    private string _previousPassword = string.Empty;
    private string _syncshellPassword = string.Empty;

    public JoinSyncshellUI(ILogger<JoinSyncshellUI> logger, SpheneMediator mediator,
        ApiController apiController, PairManager pairManager, PerformanceCollectorService performanceCollectorService) 
        : base(logger, mediator, "Join existing Syncshell###SpheneJoinSyncshell", performanceCollectorService)
    {
        _apiController = apiController;
        _pairManager = pairManager;
        SizeConstraints = new()
        {
            MinimumSize = new(700, 400),
            MaximumSize = new(700, 400)
        };

        Mediator.Subscribe<DisconnectedMessage>(this, (_) => IsOpen = false);

        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize;
    }

    public override void OnOpen()
    {
        _desiredSyncshellToJoin = string.Empty;
        _syncshellPassword = string.Empty;
        _previousPassword = string.Empty;
        _groupJoinInfo = null;
        _ownPermissions = _apiController.DefaultPermissions.DeepClone()!;
    }

    protected override void DrawInternal()
    {
        if (SpheneUI.Layout.BeginScrollArea("join-content", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            DrawJoinContent();
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawJoinContent()
    {
        SpheneUI.Text.PageHeader(_groupJoinInfo == null || !_groupJoinInfo.Success ? "Join Syncshell" : "Finalize join Syncshell " + _groupJoinInfo.GroupAliasOrGID);

        if (_groupJoinInfo == null || !_groupJoinInfo.Success)
        {
            SpheneUI.Text.MutedWrapped("Here you can join existing Syncshells. " +
                "Please keep in mind that you cannot join more than " + _apiController.ServerInfo.MaxGroupsJoinedByUser + " syncshells on this server." + Environment.NewLine +
                "Joining a Syncshell will pair you implicitly with all existing users in the Syncshell." + Environment.NewLine +
                "All permissions to all users in the Syncshell will be set to the preferred Syncshell permissions on joining, excluding prior set preferred permissions.");
            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Layout.Callout("Syncshell ID and Password are case sensitive. MSS- is part of Syncshell IDs, unless using Vanity IDs.", Dalamud.Interface.FontAwesomeIcon.InfoCircle);
            SpheneUI.Layout.VerticalSpace();

            SpheneUI.Controls.InputTextWithHint("Syncshell ID##syncshellId", "Full Syncshell ID", ref _desiredSyncshellToJoin, 20);
            SpheneUI.Controls.InputTextWithHint("Syncshell Password##syncshellpw", "Password", ref _syncshellPassword, 50, ImGuiInputTextFlags.Password);
            SpheneUI.Layout.VerticalSpace();
            using (ImRaii.Disabled(string.IsNullOrEmpty(_desiredSyncshellToJoin) || string.IsNullOrEmpty(_syncshellPassword)))
            {
                if (SpheneUI.Icons.TextButton(Dalamud.Interface.FontAwesomeIcon.Plus, "Join Syncshell"))
                {
                    _groupJoinInfo = _apiController.GroupJoin(new GroupPasswordDto(new API.Data.GroupData(_desiredSyncshellToJoin), _syncshellPassword)).Result;
                    _previousPassword = _syncshellPassword;
                    _syncshellPassword = string.Empty;
                }
            }
            if (_groupJoinInfo != null && !_groupJoinInfo.Success)
            {
                SpheneUI.Layout.Callout("Failed to join the Syncshell. This is due to one of following reasons:" + Environment.NewLine +
                    "- The Syncshell does not exist or the password is incorrect" + Environment.NewLine +
                    "- You are already in that Syncshell or are banned from that Syncshell" + Environment.NewLine +
                    "- The Syncshell is at capacity or has invites disabled", Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
            }
        }
        else
        {
            SpheneUI.Text.Plain("You are about to join the Syncshell " + _groupJoinInfo.GroupAliasOrGID + " by " + _groupJoinInfo.OwnerAliasOrUID);
            SpheneUI.Layout.Space(SpheneUI.Spacing.X2);
            SpheneUI.Text.Muted("This Syncshell staff has set the following suggested Syncshell permissions:");
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("- Sounds ");
            SpheneUI.Icons.BoolIcon(!_groupJoinInfo.GroupPermissions.IsPreferDisableSounds());
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("- Animations");
            SpheneUI.Icons.BoolIcon(!_groupJoinInfo.GroupPermissions.IsPreferDisableAnimations());
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("- VFX");
            SpheneUI.Icons.BoolIcon(!_groupJoinInfo.GroupPermissions.IsPreferDisableVFX());

            if (_groupJoinInfo.GroupPermissions.IsPreferDisableSounds() != _ownPermissions.DisableGroupSounds
                || _groupJoinInfo.GroupPermissions.IsPreferDisableVFX() != _ownPermissions.DisableGroupVFX
                || _groupJoinInfo.GroupPermissions.IsPreferDisableAnimations() != _ownPermissions.DisableGroupAnimations)
            {
                SpheneUI.Layout.Space(SpheneUI.Spacing.X2);
                SpheneUI.Text.Colored("Your current preferred default Syncshell permissions deviate from the suggested permissions:", ImGuiColors.DalamudYellow);

                void SuggestedPermRow(string id, string label, bool ownDisabled, bool suggestedDisabled, Action apply)
                {
                    using var idScope = ImRaii.PushId(id);
                    var btnW = SpheneUI.Icons.TextButtonWidth(Dalamud.Interface.FontAwesomeIcon.ArrowRight, "Apply suggested");
                    ImGui.AlignTextToFramePadding();
                    SpheneUI.Text.Plain("- " + label);
                    ImGui.SameLine();
                    SpheneUI.Icons.BoolIcon(!ownDisabled);
                    ImGui.SameLine();
                    SpheneUI.Text.Muted("yours → suggested");
                    ImGui.SameLine();
                    SpheneUI.Icons.BoolIcon(!suggestedDisabled);
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - btnW);
                    if (SpheneUI.Icons.TextButton(Dalamud.Interface.FontAwesomeIcon.ArrowRight, "Apply suggested"))
                    {
                        apply();
                    }
                }

                if (_groupJoinInfo.GroupPermissions.IsPreferDisableSounds() != _ownPermissions.DisableGroupSounds)
                {
                    SuggestedPermRow("suggestedSounds", "Sounds", _ownPermissions.DisableGroupSounds,
                        _groupJoinInfo.GroupPermissions.IsPreferDisableSounds(),
                        () => _ownPermissions.DisableGroupSounds = _groupJoinInfo.GroupPermissions.IsPreferDisableSounds());
                }
                if (_groupJoinInfo.GroupPermissions.IsPreferDisableAnimations() != _ownPermissions.DisableGroupAnimations)
                {
                    SuggestedPermRow("suggestedAnims", "Animations", _ownPermissions.DisableGroupAnimations,
                        _groupJoinInfo.GroupPermissions.IsPreferDisableAnimations(),
                        () => _ownPermissions.DisableGroupAnimations = _groupJoinInfo.GroupPermissions.IsPreferDisableAnimations());
                }
                if (_groupJoinInfo.GroupPermissions.IsPreferDisableVFX() != _ownPermissions.DisableGroupVFX)
                {
                    SuggestedPermRow("suggestedVfx", "VFX", _ownPermissions.DisableGroupVFX,
                        _groupJoinInfo.GroupPermissions.IsPreferDisableVFX(),
                        () => _ownPermissions.DisableGroupVFX = _groupJoinInfo.GroupPermissions.IsPreferDisableVFX());
                }
                SpheneUI.Text.Wrapped("Note: you do not need to apply the suggested Syncshell permissions, they are solely suggestions by the staff of the Syncshell.");
            }
            else
            {
                SpheneUI.Text.Wrapped("Your default syncshell permissions on joining are in line with the suggested Syncshell permissions through the owner.");
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Section);
            if (SpheneUI.Icons.TextButton(Dalamud.Interface.FontAwesomeIcon.Plus, "Finalize and join " + _groupJoinInfo.GroupAliasOrGID))
            {
                GroupUserPreferredPermissions joinPermissions = GroupUserPreferredPermissions.NoneSet;
                joinPermissions.SetDisableSounds(_ownPermissions.DisableGroupSounds);
                joinPermissions.SetDisableAnimations(_ownPermissions.DisableGroupAnimations);
                joinPermissions.SetDisableVFX(_ownPermissions.DisableGroupVFX);
                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var joinResult = await _apiController.GroupJoinFinalize(new GroupJoinDto(_groupJoinInfo.Group, _previousPassword, joinPermissions)).ConfigureAwait(false);
                        if (joinResult)
                        {
                            // Check if there's a welcome page for this group
                            var welcomePage = await _apiController.GroupGetWelcomePage(new GroupDto(_groupJoinInfo.Group)).ConfigureAwait(false);
                            if (welcomePage != null && welcomePage.IsEnabled && welcomePage.ShowOnJoin)
                            {
                                // Get current group info from PairManager for up-to-date alias
                                var groupFullInfo = _pairManager.Groups.Values.FirstOrDefault(g => string.Equals(g.Group.GID, _groupJoinInfo.Group.GID, StringComparison.Ordinal));
                                if (groupFullInfo != null)
                                {
                                    _logger.LogDebug("Publishing OpenWelcomePageMessage with GroupFullInfo: {GroupAliasOrGID}", groupFullInfo.Group.AliasOrGID);
                                    Mediator.Publish(new OpenWelcomePageMessage(welcomePage, groupFullInfo));
                                }
                                else
                    {
                        _logger.LogWarning("Could not find GroupFullInfo for GID: {GID}, using fallback", _groupJoinInfo.Group.GID);
                        // Fallback: create a minimal GroupFullInfoDto from _groupJoinInfo
                        var fallbackGroupFullInfo = new GroupFullInfoDto(
                            _groupJoinInfo.Group,
                            _groupJoinInfo.Owner,
                            _groupJoinInfo.GroupPermissions,
                            GroupUserPreferredPermissions.NoneSet, // Default user permissions
                            new GroupPairUserInfo(), // Empty user info
                            new Dictionary<string, GroupPairUserInfo>(StringComparer.Ordinal) // Empty pair user infos
                        );
                        Mediator.Publish(new OpenWelcomePageMessage(welcomePage, fallbackGroupFullInfo));
                    }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to finalize group join");
                    }
                });
                
                IsOpen = false;
            }
        }
    }
}
