using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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

internal sealed class JoinSyncshellUI : IMediatorSubscriber, IDisposable
{
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly ILogger<JoinSyncshellUI> _logger;
    private string _desiredSyncshellToJoin = string.Empty;
    private GroupJoinInfoDto? _groupJoinInfo = null;
    private Task<GroupJoinInfoDto>? _joinTask;
    private bool _joinRequestFailed;
    private DefaultPermissionsDto _ownPermissions;
    private string _previousPassword = string.Empty;
    private string _syncshellPassword = string.Empty;
    private string _applicationMessage = string.Empty;
    private MembershipApplicationDto? _myApplication;
    private Task? _applyTask;
    private bool _applyRequestFailed;
    private readonly AreaBoundSyncshellService _areaBoundSyncshellService;

    public SpheneMediator Mediator { get; }

    public JoinSyncshellUI(ILogger<JoinSyncshellUI> logger, SpheneMediator mediator,
        ApiController apiController, PairManager pairManager,
        AreaBoundSyncshellService areaBoundSyncshellService)
    {
        _logger = logger;
        Mediator = mediator;
        _apiController = apiController;
        _pairManager = pairManager;
        _areaBoundSyncshellService = areaBoundSyncshellService;
        _ownPermissions = _apiController.DefaultPermissions.DeepClone()!;

        Mediator.Subscribe<DisconnectedMessage>(this, (_) => Reset());
    }

    public void Dispose() => Mediator.UnsubscribeAll(this);

    public void Reset()
    {
        _desiredSyncshellToJoin = string.Empty;
        _syncshellPassword = string.Empty;
        _previousPassword = string.Empty;
        _groupJoinInfo = null;
        _joinRequestFailed = false;
        _applicationMessage = string.Empty;
        _myApplication = null;
        _applyRequestFailed = false;
        _ownPermissions = _apiController.DefaultPermissions.DeepClone()!;
    }

    /// <summary>Prefills the join target - discovery CTAs hand the shell gid
    /// over instead of making the user retype it. Resets only the target;
    /// in-flight state stays intact.</summary>
    public void PrefillTarget(string gid)
    {
        _desiredSyncshellToJoin = gid;
        _groupJoinInfo = null;
        _joinRequestFailed = false;
        _joinTask = null;
        _myApplication = null;
    }

    /// <summary>
    /// Embedded content inside the Sphene Hub's Syncshells page · the hub's
    /// own scroll region hosts it, so the action bar sits in flow at the end.
    /// </summary>
    public void DrawEmbeddedContent()
    {
        SpheneUI.Layout.PageHeader("Join a Syncshell", "joinshell", () =>
        {
            SpheneUI.Text.MutedWrapped("Enter the ID and password a member gave you - suggested permissions can be adopted on the next step.", ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });

        if (SpheneUI.Controls.SegmentButton("Syncshells", FontAwesomeIcon.ArrowLeft))
        {
            Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Syncshells));
        }
        SpheneUI.AttachTooltip("Back to your Syncshells");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);

        DrawJoinContent();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        DrawJoinFooter();
    }

    private void DrawJoinContent()
    {
        if (_groupJoinInfo == null || !_groupJoinInfo.Success)
        {
            DrawLookupStep();
        }
        else
        {
            DrawFinalizeStep();
        }
    }

    private void DrawLookupStep()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Controls.InputTextWithHint("##syncshellId", "Syncshell ID (MSS-…)", ref _desiredSyncshellToJoin, 20);
            SpheneUI.Controls.InputTextWithHint("##syncshellpw", "Password or invite (optional)", ref _syncshellPassword, 50, ImGuiInputTextFlags.Password);
            SpheneUI.AttachTooltip("ID and password are case sensitive. MSS- is part of the ID unless a vanity ID is used."
                + " Leave the password empty for public or approval-gated syncshells.");
        }, "Join a Syncshell", icon: FontAwesomeIcon.UserPlus, accentColor: SpheneUI.Palette.CrystalBlue);

        if (_joinTask is { IsCompleted: true })
        {
            _groupJoinInfo = _joinTask.IsCompletedSuccessfully ? _joinTask.Result : null;
            _joinRequestFailed = !_joinTask.IsCompletedSuccessfully;
            _joinTask = null;
            // approval-gated shells surface an application path instead of an error
            if (_groupJoinInfo is { Success: false, JoinPolicy: SyncshellJoinPolicy.ApprovalRequired })
            {
                _ = RefreshMyApplication();
            }
        }

        if (ShowsApplicationStep())
        {
            DrawApplicationStep();
            return;
        }

        if (_joinRequestFailed || (_groupJoinInfo != null && !_groupJoinInfo.Success))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            SpheneUI.Layout.Callout("Could not join · wrong ID or password, already a member, banned, at capacity or invites disabled.",
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
        }
    }

    /// <summary>True when the probed shell is approval-gated - the apply card replaces the error.</summary>
    private bool ShowsApplicationStep()
        => _groupJoinInfo is { Success: false, JoinPolicy: SyncshellJoinPolicy.ApprovalRequired }
            || _myApplication is { Status: MembershipApplicationStatus.Pending };

    private void DrawApplicationStep()
    {
        var pending = _myApplication is { Status: MembershipApplicationStatus.Pending };
        var rejected = _myApplication is { Status: MembershipApplicationStatus.Rejected };
        var withdrawn = _myApplication is { Status: MembershipApplicationStatus.Withdrawn };

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        SpheneUI.Layout.SectionCard(() =>
        {
            if (pending)
            {
                SpheneUI.Layout.Callout("Your application is pending - the syncshell staff will review it.",
                    FontAwesomeIcon.Clock, SpheneUI.Palette.CrystalBlue);
                if (!string.IsNullOrEmpty(_myApplication!.Message))
                {
                    SpheneUI.Text.MutedWrapped($"Your message: {_myApplication.Message}",
                        ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
                }
            }
            else
            {
                if (rejected)
                {
                    SpheneUI.Layout.Callout("Your previous request was declined - you can apply again.",
                        FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
                }
                else if (withdrawn)
                {
                    SpheneUI.Text.MutedWrapped("You withdrew your previous application - you can apply again.",
                        ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
                }
                else
                {
                    SpheneUI.Text.MutedWrapped("This syncshell accepts new members by staff approval only.",
                        ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
                }
                SpheneUI.Controls.InputTextWithHint("##applymsg", "Message for the staff (optional)",
                    ref _applicationMessage, 400);
            }

            if (_applyRequestFailed)
            {
                SpheneUI.Layout.Callout("The request could not be sent - you may already be a member, banned, or the policy changed.",
                    FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
            }
        }, "Approval required", icon: FontAwesomeIcon.UserClock, accentColor: SpheneUI.Palette.EtherealPurple);
    }

    /// <summary>Pulls the caller's application state for the probed shell.</summary>
    private async Task RefreshMyApplication()
    {
        try
        {
            _myApplication = await _apiController.GroupGetMyMembershipApplication(
                new GroupDto(new API.Data.GroupData(_desiredSyncshellToJoin))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load membership application state");
        }
    }

    private void DrawFinalizeStep()
    {
        var info = _groupJoinInfo!;
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Big(info.GroupAliasOrGID);
            ImGui.SameLine();
            SpheneUI.Text.Muted($"· hosted by {info.OwnerAliasOrUID}");
        }, "You're about to join", icon: FontAwesomeIcon.Check, accentColor: SpheneUI.Palette.CrystalBlue);

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        SpheneUI.Layout.HeadedRowGroup("Suggested Permissions", () =>
        {
            PermSuggestionRow("sounds", "Sound sync",
                info.GroupPermissions.IsPreferDisableSounds(), _ownPermissions.DisableGroupSounds,
                v => _ownPermissions.DisableGroupSounds = v);
            PermSuggestionRow("anims", "Animation sync",
                info.GroupPermissions.IsPreferDisableAnimations(), _ownPermissions.DisableGroupAnimations,
                v => _ownPermissions.DisableGroupAnimations = v);
            PermSuggestionRow("vfx", "VFX sync",
                info.GroupPermissions.IsPreferDisableVFX(), _ownPermissions.DisableGroupVFX,
                v => _ownPermissions.DisableGroupVFX = v);
        }, FontAwesomeIcon.Lightbulb,
            description: "Suggestions only · anything you don't adopt keeps your own default.",
            id: "join-suggested-perms");

    }

    private void DrawJoinFooter()
    {
        var finalize = _groupJoinInfo is { Success: true };
        var applyMode = !finalize && ShowsApplicationStep();
        var pendingApp = _myApplication is { Status: MembershipApplicationStatus.Pending };
        var label = finalize ? "Join " + _groupJoinInfo!.GroupAliasOrGID
            : applyMode ? (pendingApp ? "Withdraw application" : "Request to join") : "Join";
        var icon = finalize ? FontAwesomeIcon.Check
            : applyMode ? (pendingApp ? FontAwesomeIcon.Times : FontAwesomeIcon.PaperPlane)
            : FontAwesomeIcon.ArrowRight;
        var busy = _joinTask is { IsCompleted: false } || _applyTask is { IsCompleted: false };
        var width = SpheneUI.Icons.TextButtonWidth(icon, label);

        // Area-bound shells join exclusively through the area flow - typing
        // the GID here must not bypass the location gate. Inside bounds the
        // button routes into the same consent prompt the detector fires.
        var areaBound = !finalize
            && _areaBoundSyncshellService.IsAreaBoundSyncshell(_desiredSyncshellToJoin);
        var areaInBounds = areaBound && _areaBoundSyncshellService.GetAvailableAreaSyncshells()
            .Any(a => string.Equals(a.GID, _desiredSyncshellToJoin, StringComparison.Ordinal));

        if (_applyTask is { IsCompleted: true })
        {
            _applyRequestFailed = !_applyTask.IsCompletedSuccessfully
                || (_applyTask is Task<bool> result && !result.Result);
            _applyTask = null;
            _ = RefreshMyApplication();
        }

        if (areaBound && !areaInBounds)
        {
            SpheneUI.Text.Muted("Area-bound · enter its bound area to join - the prompt appears there automatically.");
        }
        else if (areaBound)
        {
            SpheneUI.Text.Muted("Area-bound · joining runs through the area consent prompt.");
        }

        SpheneUI.Layout.ActionBar(width, () =>
        {
            if (SpheneUI.Controls.PrimaryButton(label, icon,
                    enabled: !areaBound ? (finalize || applyMode || !(busy || string.IsNullOrEmpty(_desiredSyncshellToJoin)))
                        : areaInBounds && !busy))
            {
                if (areaBound)
                {
                    var bound = _areaBoundSyncshellService.GetAvailableAreaSyncshells()
                        .FirstOrDefault(a => string.Equals(a.GID, _desiredSyncshellToJoin, StringComparison.Ordinal));
                    if (bound != null)
                    {
                        var needsRules = bound.Settings.RequireRulesAcceptance
                            && !string.IsNullOrEmpty(bound.Settings.JoinRules);
                        Mediator.Publish(new AreaBoundSyncshellConsentRequestMessage(bound, needsRules));
                    }
                }
                else if (finalize)
                {
                    FinalizeJoin(_groupJoinInfo!);
                }
                else if (applyMode)
                {
                    _applyRequestFailed = false;
                    if (pendingApp)
                    {
                        _applyTask = _apiController.GroupWithdrawMembershipApplication(
                            new GroupDto(new API.Data.GroupData(_desiredSyncshellToJoin)));
                    }
                    else
                    {
                        _applyTask = _apiController.GroupApplyForMembership(new GroupApplyMembershipDto(
                            new API.Data.GroupData(_desiredSyncshellToJoin),
                            string.IsNullOrWhiteSpace(_applicationMessage) ? null : _applicationMessage));
                    }
                }
                else
                {
                    _joinTask = _apiController.GroupJoin(new GroupPasswordDto(
                        new API.Data.GroupData(_desiredSyncshellToJoin), _syncshellPassword));
                    _previousPassword = _syncshellPassword;
                    _syncshellPassword = string.Empty;
                    _joinRequestFailed = false;
                }
            }
        }, busy ? DrawStatus : (Action<float>?)null);

        static void DrawStatus(float avail)
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Muted(SpheneUI.Text.Ellipsize("Contacting the syncshell...", avail));
        }
    }

    private static void PermSuggestionRow(string id, string label, bool suggestedDisabled, bool ownDisabled, Action<bool> apply)
    {
        SpheneUI.GroupState.RowStart();
        try
        {
            using var idScope = ImRaii.PushId(id);
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.BoolIcon(!suggestedDisabled);
            ImGui.SameLine();
            SpheneUI.Text.Plain(label);
            if (suggestedDisabled != ownDisabled)
            {
                ImGui.SameLine();
                SpheneUI.Text.Muted($"yours: {(ownDisabled ? "off" : "on")}");
                var btnW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.ArrowRight, "Use suggested");
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - btnW);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowRight, "Use suggested"))
                {
                    apply(suggestedDisabled);
                }
                SpheneUI.AttachTooltip($"Set your join permission to the staff suggestion ({(suggestedDisabled ? "off" : "on")})");
            }
        }
        finally
        {
            SpheneUI.GroupState.RowEnd();
        }
    }

    private void FinalizeJoin(GroupJoinInfoDto info)
    {
        GroupUserPreferredPermissions joinPermissions = GroupUserPreferredPermissions.NoneSet;
        joinPermissions.SetDisableSounds(_ownPermissions.DisableGroupSounds);
        joinPermissions.SetDisableAnimations(_ownPermissions.DisableGroupAnimations);
        joinPermissions.SetDisableVFX(_ownPermissions.DisableGroupVFX);

        _ = Task.Run(async () =>
        {
            try
            {
                var joinResult = await _apiController.GroupJoinFinalize(
                    new GroupJoinDto(info.Group, _previousPassword, joinPermissions)).ConfigureAwait(false);
                if (joinResult)
                {
                    var community = await _apiController.GroupGetCommunity(new GroupDto(info.Group)).ConfigureAwait(false);
                    if (community?.Profile is { IsEnabled: true, ShowOnJoin: true })
                    {
                        var groupFullInfo = _pairManager.Groups.Values.FirstOrDefault(
                            g => string.Equals(g.Group.GID, info.Group.GID, StringComparison.Ordinal));
                        if (groupFullInfo != null)
                        {
                            _logger.LogDebug("Publishing OpenSyncshellCommunityMessage with GroupFullInfo: {GroupAliasOrGID}",
                                groupFullInfo.Group.AliasOrGID);
                            Mediator.Publish(new OpenSyncshellCommunityMessage(community, groupFullInfo, JoinPopup: true));
                        }
                        else
                        {
                            _logger.LogWarning("Could not find GroupFullInfo for GID: {GID}, using fallback", info.Group.GID);
                            var fallbackGroupFullInfo = new GroupFullInfoDto(
                                info.Group,
                                info.Owner,
                                info.GroupPermissions,
                                GroupUserPreferredPermissions.NoneSet,
                                new GroupPairUserInfo(),
                                new Dictionary<string, GroupPairUserInfo>(StringComparer.Ordinal)
                            );
                            Mediator.Publish(new OpenSyncshellCommunityMessage(community, fallbackGroupFullInfo, JoinPopup: true));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to finalize group join");
            }
        });

        Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Syncshells));
    }
}
