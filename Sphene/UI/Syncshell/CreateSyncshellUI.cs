using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Syncshell;

public class CreateSyncshellUI : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private bool _errorGroupCreate;
    private GroupJoinDto? _lastCreatedGroup;
    private string _groupAlias = string.Empty;

    public CreateSyncshellUI(ILogger<CreateSyncshellUI> logger, SpheneMediator spheneMediator, ApiController apiController, PerformanceCollectorService performanceCollectorService)
        : base(logger, spheneMediator, "Create new Syncshell###SpheneCreateSyncshell", performanceCollectorService)
    {
        _apiController = apiController;
        SizeConstraints = new()
        {
            MinimumSize = new(550, 380),
            MaximumSize = new(550, 380)
        };

        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;

        Mediator.Subscribe<DisconnectedMessage>(this, (_) => IsOpen = false);
    }

    protected override void DrawInternal()
    {
        if (SpheneUI.Layout.BeginScrollArea("create-content", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            DrawCreateContent();
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawCreateContent()
    {
        SpheneUI.Text.PageHeader("Create new Syncshell");

        if (_lastCreatedGroup == null)
        {
            SpheneUI.Controls.InputTextWithHint("Syncshell Alias (optional)##alias", "3-50 characters", ref _groupAlias, 50);
            SpheneUI.AttachTooltip("Optional alias for your Syncshell (3-50 characters). Leave empty to use only the generated ID.");
            SpheneUI.Layout.VerticalSpace();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Create Syncshell"))
            {
                try
                {
                    var dto = !string.IsNullOrWhiteSpace(_groupAlias) ? new GroupCreateDto(_groupAlias) : null;
                    _lastCreatedGroup = _apiController.GroupCreate(dto).Result;
                }
                catch
                {
                    _lastCreatedGroup = null;
                    _errorGroupCreate = true;
                }
            }

            SpheneUI.Layout.Section("Details", () =>
            {
                SpheneUI.Text.Wrapped("Creating a new Syncshell will create it with your current preferred permissions for Syncshells as default suggested permissions." + Environment.NewLine +
                    "- You can own up to " + _apiController.ServerInfo.MaxGroupsCreatedByUser + " Syncshells on this server." + Environment.NewLine +
                    "- You can join up to " + _apiController.ServerInfo.MaxGroupsJoinedByUser + " Syncshells on this server (including your own)" + Environment.NewLine +
                    "- Syncshells on this server can have a maximum of " + _apiController.ServerInfo.MaxGroupUserCount + " users");
                SpheneUI.Text.Muted("Your current Syncshell preferred permissions are:");
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("- Animations");
                SpheneUI.Icons.BoolIcon(!_apiController.DefaultPermissions!.DisableGroupAnimations);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("- Sounds");
                SpheneUI.Icons.BoolIcon(!_apiController.DefaultPermissions!.DisableGroupSounds);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("- VFX");
                SpheneUI.Icons.BoolIcon(!_apiController.DefaultPermissions!.DisableGroupVFX);
                SpheneUI.Text.MutedWrapped("(Those preferred permissions can be changed anytime after Syncshell creation, your defaults can be changed anytime in the Sphene Settings)");
            });
        }
        else
        {
            _errorGroupCreate = false;
            SpheneUI.Layout.SectionCard(() =>
            {
                SpheneUI.Text.Plain("Syncshell ID: " + _lastCreatedGroup.Group.GID);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("Syncshell Password: " + _lastCreatedGroup.Password);
                ImGui.SameLine();
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Copy))
                {
                    ImGui.SetClipboardText(_lastCreatedGroup.Password);
                }
                SpheneUI.Text.MutedWrapped("You can change the Syncshell password later at any time.");
            }, "Syncshell created");
            SpheneUI.Text.Muted("These settings were set based on your preferred syncshell permissions:");
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("Suggest Animation sync:");
            SpheneUI.Icons.BoolIcon(!_lastCreatedGroup.GroupUserPreferredPermissions.IsDisableAnimations());
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("Suggest Sounds sync:");
            SpheneUI.Icons.BoolIcon(!_lastCreatedGroup.GroupUserPreferredPermissions.IsDisableSounds());
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("Suggest VFX sync:");
            SpheneUI.Icons.BoolIcon(!_lastCreatedGroup.GroupUserPreferredPermissions.IsDisableVFX());
        }

        if (_errorGroupCreate)
        {
            SpheneUI.Layout.Callout("Something went wrong during creation of a new Syncshell", FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
        }
    }

    public override void OnOpen()
    {
        _lastCreatedGroup = null;
        _groupAlias = string.Empty;
    }
}
