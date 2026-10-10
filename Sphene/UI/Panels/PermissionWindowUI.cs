using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Panels;

public class PermissionWindowUI : WindowMediatorSubscriberBase
{
    public Pair Pair { get; init; }
    private readonly ApiController _apiController;
    private UserPermissions _ownPermissions;

    public PermissionWindowUI(ILogger<PermissionWindowUI> logger, Pair pair, SpheneMediator mediator, ApiController apiController, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Permissions for " + pair.UserData.AliasOrUID + "###SphenePermissions" + pair.UserData.UID, performanceCollectorService)
    {
        Pair = pair;
        _apiController = apiController;
        _ownPermissions = pair.UserPair.OwnPermissions.DeepClone();
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize;
        SizeConstraints = new()
        {
            MinimumSize = new(450, 100),
            MaximumSize = new(450, 500)
        };
        IsOpen = true;
    }

    protected override void DrawInternal()
    {
        if (SpheneUI.Layout.BeginScrollArea("permissions-content", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            DrawPermissionsContent();
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawPermissionsContent()
    {
        var sticky = _ownPermissions.IsSticky();
        var paused = _ownPermissions.IsPaused();
        var disableSounds = _ownPermissions.IsDisableSounds();
        var disableAnimations = _ownPermissions.IsDisableAnimations();
        var disableVfx = _ownPermissions.IsDisableVFX();
        var disableVfxInDuty = _ownPermissions.IsDisableVFXInDuty();
        var style = ImGui.GetStyle();
        var indentSize = ImGui.GetFrameHeight() + style.ItemSpacing.X;

        SpheneUI.Text.PageHeader("Permissions for " + Pair.UserData.AliasOrUID);

        if (SpheneUI.Controls.Toggle("Preferred Permissions", ref sticky))
        {
            _ownPermissions.SetSticky(sticky);
        }
        SpheneUI.Controls.HelpText("Preferred Permissions, when enabled, will exclude this user from any permission changes on any syncshells you share with this user.");

        SpheneUI.Layout.Space(SpheneUI.Spacing.Hairline);


        if (SpheneUI.Controls.Toggle("Pause Sync", ref paused))
        {
            _ownPermissions.SetPaused(paused);
        }
        SpheneUI.Controls.HelpText("Pausing will completely cease any sync with this user." + SpheneUI.TooltipSeparator
            + "Note: this is bidirectional, either user pausing will cease sync completely.");
        var otherPerms = Pair.UserPair.OtherPermissions;

        var otherIsPaused = otherPerms.IsPaused();
        var otherDisableSounds = otherPerms.IsDisableSounds();
        var otherDisableAnimations = otherPerms.IsDisableAnimations();
        var otherDisableVFX = otherPerms.IsDisableVFX();
        var otherDisableVfxInDuty = otherPerms.IsDisableVFXInDuty();

        using (ImRaii.PushIndent(indentSize, false))
        {
            SpheneUI.Icons.BoolIcon(!otherIsPaused, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain(Pair.UserData.AliasOrUID + " has " + (!otherIsPaused ? "not " : string.Empty) + "paused you");
        }

        SpheneUI.Layout.SectionSeparator("Sync Permissions");

        if (SpheneUI.Controls.Toggle("Disable Sounds", ref disableSounds))
        {
            _ownPermissions.SetDisableSounds(disableSounds);
        }
        SpheneUI.Controls.HelpText("Disabling sounds will remove all sounds synced with this user on both sides." + SpheneUI.TooltipSeparator
            + "Note: this is bidirectional, either user disabling sound sync will stop sound sync on both sides.");
        using (ImRaii.PushIndent(indentSize, false))
        {
            SpheneUI.Icons.BoolIcon(!otherDisableSounds, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain(Pair.UserData.AliasOrUID + " has " + (!otherDisableSounds ? "not " : string.Empty) + "disabled sound sync with you");
        }

        if (SpheneUI.Controls.Toggle("Disable Animations", ref disableAnimations))
        {
            _ownPermissions.SetDisableAnimations(disableAnimations);
        }
        SpheneUI.Controls.HelpText("Disabling sounds will remove all animations synced with this user on both sides." + SpheneUI.TooltipSeparator
            + "Note: this is bidirectional, either user disabling animation sync will stop animation sync on both sides.");
        using (ImRaii.PushIndent(indentSize, false))
        {
            SpheneUI.Icons.BoolIcon(!otherDisableAnimations, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain(Pair.UserData.AliasOrUID + " has " + (!otherDisableAnimations ? "not " : string.Empty) + "disabled animation sync with you");
        }

        if (SpheneUI.Controls.Toggle("Disable VFX", ref disableVfx))
        {
            _ownPermissions.SetDisableVFX(disableVfx);
        }
        SpheneUI.Controls.HelpText("Disabling sounds will remove all VFX synced with this user on both sides." + SpheneUI.TooltipSeparator
            + "Note: this is bidirectional, either user disabling VFX sync will stop VFX sync on both sides.");
        using (ImRaii.PushIndent(indentSize, false))
        {
            SpheneUI.Icons.BoolIcon(!otherDisableVFX, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain(Pair.UserData.AliasOrUID + " has " + (!otherDisableVFX ? "not " : string.Empty) + "disabled VFX sync with you");
        }

        if (SpheneUI.Controls.Toggle("Disable VFX in Duty", ref disableVfxInDuty))
        {
            _ownPermissions.SetDisableVFXInDuty(disableVfxInDuty);
        }
        SpheneUI.Controls.HelpText("When enabled, VFX files from this user will be removed while you are in duty." + SpheneUI.TooltipSeparator
            + "Note: this is bidirectional, either user enabling this will stop duty VFX sync on both sides.");
        using (ImRaii.PushIndent(indentSize, false))
        {
            SpheneUI.Icons.BoolIcon(!otherDisableVfxInDuty, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain(Pair.UserData.AliasOrUID + " has " + (!otherDisableVfxInDuty ? "not " : string.Empty) + "disabled VFX sync in duty with you");
        }

        SpheneUI.Layout.Separator();

        bool hasChanges = _ownPermissions != Pair.UserPair.OwnPermissions;

        using (ImRaii.Disabled(!hasChanges))
            if (SpheneUI.Icons.TextButton(Dalamud.Interface.FontAwesomeIcon.Save, "Save"))
            {
                _ = _apiController.SetBulkPermissions(new(
                    new(StringComparer.Ordinal)
                    {
                        { Pair.UserData.UID, _ownPermissions }
                    },
                    new(StringComparer.Ordinal)
                ));
            }
        SpheneUI.AttachTooltip("Save and apply all changes");

        var rightSideButtons = SpheneUI.Icons.TextButtonWidth(Dalamud.Interface.FontAwesomeIcon.Undo, "Revert") +
            SpheneUI.Icons.TextButtonWidth(Dalamud.Interface.FontAwesomeIcon.ArrowsSpin, "Reset to Default");
        var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;

        ImGui.SameLine(availableWidth - rightSideButtons);

        using (ImRaii.Disabled(!hasChanges))
            if (SpheneUI.Icons.TextButton(Dalamud.Interface.FontAwesomeIcon.Undo, "Revert"))
            {
                _ownPermissions = Pair.UserPair.OwnPermissions.DeepClone();
            }
        SpheneUI.AttachTooltip("Revert all changes");

        ImGui.SameLine();
        if (SpheneUI.Icons.TextButton(Dalamud.Interface.FontAwesomeIcon.ArrowsSpin, "Reset to Default"))
        {
            var defaultPermissions = _apiController.DefaultPermissions!;
            _ownPermissions.SetSticky(Pair.IsDirectlyPaired || defaultPermissions.IndividualIsSticky);
            _ownPermissions.SetPaused(false);
            _ownPermissions.SetDisableVFX(Pair.IsDirectlyPaired ? defaultPermissions.DisableIndividualVFX : defaultPermissions.DisableGroupVFX);
            _ownPermissions.SetDisableVFXInDuty(false);
            _ownPermissions.SetDisableSounds(Pair.IsDirectlyPaired ? defaultPermissions.DisableIndividualSounds : defaultPermissions.DisableGroupSounds);
            _ownPermissions.SetDisableAnimations(Pair.IsDirectlyPaired ? defaultPermissions.DisableIndividualAnimations : defaultPermissions.DisableGroupAnimations);
            _ = _apiController.SetBulkPermissions(new(
                new(StringComparer.Ordinal)
                {
                    { Pair.UserData.UID, _ownPermissions }
                },
                new(StringComparer.Ordinal)
            ));
        }
        SpheneUI.AttachTooltip("This will set all permissions to your defined default permissions in the Sphene Settings");

        var ySize = ImGui.GetCursorPosY() + style.FramePadding.Y * ImGuiHelpers.GlobalScale + style.FrameBorderSize;
        ImGui.SetWindowSize(new(400, ySize));
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}
