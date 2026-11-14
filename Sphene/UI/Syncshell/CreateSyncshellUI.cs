using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Syncshell;

public sealed class CreateSyncshellUI : IMediatorSubscriber, IDisposable
{
    private readonly ApiController _apiController;
    private readonly ILogger<CreateSyncshellUI> _logger;
    private bool _errorGroupCreate;
    private GroupJoinDto? _lastCreatedGroup;
    private Task<GroupJoinDto>? _createTask;
    private string _groupAlias = string.Empty;

    public SpheneMediator Mediator { get; }

    public CreateSyncshellUI(ILogger<CreateSyncshellUI> logger, SpheneMediator spheneMediator, ApiController apiController)
    {
        _logger = logger;
        Mediator = spheneMediator;
        _apiController = apiController;

        Mediator.Subscribe<DisconnectedMessage>(this, (_) => Reset());
    }

    public void Dispose() => Mediator.UnsubscribeAll(this);

    public void Reset()
    {
        _lastCreatedGroup = null;
        _groupAlias = string.Empty;
        _errorGroupCreate = false;
    }

    /// <summary>
    /// Embedded content inside the Sphene Hub's Syncshells page · the hub's
    /// own scroll region hosts it, so the action bar sits in flow at the end.
    /// </summary>
    public void DrawEmbeddedContent()
    {
        SpheneUI.Layout.PageHeader("Create a Syncshell", "createshell", () =>
        {
            SpheneUI.Text.MutedWrapped("Create your own syncshell - you become the owner with full admin control.", ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });

        if (SpheneUI.Controls.SegmentButton("Syncshells", FontAwesomeIcon.ArrowLeft))
        {
            Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Syncshells));
        }
        SpheneUI.AttachTooltip("Back to your Syncshells");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);

        DrawCreateContent();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        DrawCreateFooter();
    }

    private void DrawCreateContent()
    {
        if (_lastCreatedGroup == null)
        {
            DrawCreateForm();
        }
        else
        {
            DrawCreatedResult();
        }

        if (_errorGroupCreate)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            SpheneUI.Layout.Callout("Something went wrong during creation of a new Syncshell",
                FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
        }
    }

    private void DrawCreateForm()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Controls.InputTextWithHint("##alias", "Syncshell alias (optional, 3-50 characters)", ref _groupAlias, 50);
            SpheneUI.AttachTooltip("Shown instead of the generated ID. Leave empty to use only the ID.");
        }, "Create a Syncshell");

        if (_createTask is { IsCompleted: true })
        {
            if (_createTask.IsCompletedSuccessfully)
            {
                _lastCreatedGroup = _createTask.Result;
            }
            else
            {
                _logger.LogWarning("Failed to create syncshell");
                _errorGroupCreate = true;
            }
            _createTask = null;
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        SpheneUI.Text.SectionHeader("Details");
        SpheneUI.Layout.RowGroup(() =>
        {
            SpheneUI.Rows.Value("Syncshells you own",
                $"up to {_apiController.ServerInfo.MaxGroupsCreatedByUser}", FontAwesomeIcon.Users);
            SpheneUI.Rows.Value("Syncshells you join",
                $"up to {_apiController.ServerInfo.MaxGroupsJoinedByUser} (incl. your own)",
                FontAwesomeIcon.UserFriends);
            SpheneUI.Rows.Value("Members per shell",
                $"up to {_apiController.ServerInfo.MaxGroupUserCount}", FontAwesomeIcon.UserPlus);

            var def = _apiController.DefaultPermissions!;
            PermRow("Animation sync", !def.DisableGroupAnimations);
            PermRow("Sound sync", !def.DisableGroupSounds);
            PermRow("VFX sync", !def.DisableGroupVFX);
        }, id: "create-details");
        SpheneUI.Text.MutedWrapped(
            "New members join with these defaults · change them anytime in Settings.");
    }

    private void DrawCreateFooter()
    {
        if (_lastCreatedGroup != null)
        {
            var doneW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Check, "Done");
            SpheneUI.Layout.ActionBar(doneW, () =>
            {
                if (SpheneUI.Controls.PrimaryButton("Done", FontAwesomeIcon.Check))
                {
                    Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Syncshells));
                }
            });
            return;
        }

        var busy = _createTask is { IsCompleted: false };
        var createW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Plus, "Create Syncshell");
        SpheneUI.Layout.ActionBar(createW, () =>
        {
            if (SpheneUI.Controls.PrimaryButton("Create Syncshell", FontAwesomeIcon.Plus,
                    enabled: !busy))
            {
                var dto = !string.IsNullOrWhiteSpace(_groupAlias) ? new GroupCreateDto(_groupAlias) : null;
                _createTask = _apiController.GroupCreate(dto);
            }
        }, busy ? DrawStatus : (Action<float>?)null);

        static void DrawStatus(float avail)
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Muted(SpheneUI.Text.Ellipsize("Creating syncshell...", avail));
        }
    }

    private void DrawCreatedResult()
    {
        _errorGroupCreate = false;
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Big(_lastCreatedGroup!.Group.AliasOrGID);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("Syncshell ID:");
            ImGui.SameLine();
            SpheneUI.Text.Colored(_lastCreatedGroup.Group.GID, SpheneUI.Palette.CrystalBlue);
            ImGui.SameLine();
            CopyButton("Copy ID", _lastCreatedGroup.Group.GID);
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("Password:");
            ImGui.SameLine();
            SpheneUI.Text.Colored(_lastCreatedGroup.Password, SpheneUI.Palette.CrystalBlue);
            ImGui.SameLine();
            CopyButton("Copy password", _lastCreatedGroup.Password);
            SpheneUI.Text.MutedWrapped("Share these with the people you want to invite. The password can be changed later.");
        }, "Syncshell created");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        SpheneUI.Text.SectionHeader("Suggested defaults",
            "What your preferred syncshell permissions suggested for new members.");
        SpheneUI.Layout.RowGroup(() =>
        {
            PermRow("Animation sync", !_lastCreatedGroup!.GroupUserPreferredPermissions.IsDisableAnimations());
            PermRow("Sound sync", !_lastCreatedGroup!.GroupUserPreferredPermissions.IsDisableSounds());
            PermRow("VFX sync", !_lastCreatedGroup!.GroupUserPreferredPermissions.IsDisableVFX());
        }, id: "create-defaults");
    }

    private static void PermRow(string label, bool enabled)
    {
        SpheneUI.Rows.Value(label, enabled ? "On" : "Off",
            enabled ? FontAwesomeIcon.Check : FontAwesomeIcon.Times,
            valueColor: enabled ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted);
    }

    private static void CopyButton(string tooltip, string value)
    {
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Copy))
        {
            ImGui.SetClipboardText(value);
        }
        SpheneUI.AttachTooltip(tooltip);
    }
}
