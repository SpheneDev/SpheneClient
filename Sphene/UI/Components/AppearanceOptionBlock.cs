using Dalamud.Bindings.ImGui;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using System.Numerics;

namespace Sphene.UI.Components;

public static class AppearanceOptionBlock
{
    public static void DrawShowSpheneIconOption(SpheneConfigService configService, string blockId = "ShowSpheneIcon")
    {
        ImGui.PushID(blockId);
        try
        {
            var showSpheneIcon = configService.Current.ShowSpheneIcon;
            if (ImGui.Checkbox("Show Sphene Icon", ref showSpheneIcon))
            {
                configService.Current.ShowSpheneIcon = showSpheneIcon;
                configService.Save();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawLockSpheneIconPositionOption(SpheneConfigService configService, string blockId = "LockSpheneIconPosition")
    {
        ImGui.PushID(blockId);
        try
        {
            var lockSpheneIcon = configService.Current.LockSpheneIcon;
            if (ImGui.Checkbox("Lock Sphene Icon position", ref lockSpheneIcon))
            {
                configService.Current.LockSpheneIcon = lockSpheneIcon;
                configService.Save();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawEnableGameRightClickMenusOption(SpheneConfigService configService, string blockId = "EnableGameRightClickMenus")
    {
        ImGui.PushID(blockId);
        try
        {
            var enableRightClickMenu = configService.Current.EnableRightClickMenus;
            if (ImGui.Checkbox("Enable game right-click menus", ref enableRightClickMenu))
            {
                configService.Current.EnableRightClickMenus = enableRightClickMenu;
                configService.Save();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static bool DrawShowStatusInServerInfoBarOption(SpheneConfigService configService, string blockId = "ShowStatusInServerInfoBar")
    {
        ImGui.PushID(blockId);
        try
        {
            var enableDtrEntry = configService.Current.EnableDtrEntry;
            if (ImGui.Checkbox("Show status in Server Info Bar", ref enableDtrEntry))
            {
                configService.Current.EnableDtrEntry = enableDtrEntry;
                configService.Save();
            }

            return enableDtrEntry;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawShowUidInTooltipOption(SpheneConfigService configService, string blockId = "ShowUidInTooltip")
    {
        ImGui.PushID(blockId);
        try
        {
            var showUidInDtrTooltip = configService.Current.ShowUidInDtrTooltip;
            if (ImGui.Checkbox("Show UID in tooltip", ref showUidInDtrTooltip))
            {
                configService.Current.ShowUidInDtrTooltip = showUidInDtrTooltip;
                configService.Save();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawPreferNotesInTooltipOption(SpheneConfigService configService, string blockId = "PreferNotesInTooltip")
    {
        ImGui.PushID(blockId);
        try
        {
            var preferNoteInDtrTooltip = configService.Current.PreferNoteInDtrTooltip;
            if (ImGui.Checkbox("Prefer notes in tooltip", ref preferNoteInDtrTooltip))
            {
                configService.Current.PreferNoteInDtrTooltip = preferNoteInDtrTooltip;
                configService.Save();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawUseStatusColorsOption(SpheneConfigService configService, string blockId = "UseStatusColors")
    {
        ImGui.PushID(blockId);
        try
        {
            var useColorsInDtr = configService.Current.UseColorsInDtr;
            if (ImGui.Checkbox("Use status colors", ref useColorsInDtr))
            {
                configService.Current.UseColorsInDtr = useColorsInDtr;
                configService.Save();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawSetVisiblePairsAsFocusTargetsOption(SpheneConfigService configService, string blockId = "SetVisiblePairsAsFocusTargets")
    {
        ImGui.PushID(blockId);
        try
        {
            var useFocusTarget = configService.Current.UseFocusTarget;
            if (ImGui.Checkbox("Set visible pairs as focus targets when clicking the eye", ref useFocusTarget))
            {
                configService.Current.UseFocusTarget = useFocusTarget;
                configService.Save();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawShowCharacterNameInsteadOfNotesOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "ShowCharacterNameInsteadOfNotes")
    {
        ImGui.PushID(blockId);
        try
        {
            var showNameInsteadOfNotes = configService.Current.ShowCharacterNameInsteadOfNotesForVisible;
            if (ImGui.Checkbox("Show character name instead of notes", ref showNameInsteadOfNotes))
            {
                configService.Current.ShowCharacterNameInsteadOfNotesForVisible = showNameInsteadOfNotes;
                configService.Save();
                mediator.Publish(new RefreshUiMessage());
            }

            uiShared.DrawHelpText("When enabled, visible users will display their character name instead of your custom notes for them.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawShowVisibleUsersSeparatelyOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "ShowVisibleUsersSeparately")
    {
        ImGui.PushID(blockId);
        try
        {
            var showVisibleSeparate = configService.Current.ShowVisibleUsersSeparately;
            if (ImGui.Checkbox("Show visible users separately", ref showVisibleSeparate))
            {
                configService.Current.ShowVisibleUsersSeparately = showVisibleSeparate;
                configService.Save();
                mediator.Publish(new StructuralRefreshUiMessage());
            }

            uiShared.DrawHelpText("Visible users will appear in a separate 'Visible' group instead of being mixed with other users.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawShowVisibleSyncshellUsersOnlyInSyncshellsOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "ShowVisibleSyncshellUsersOnlyInSyncshells")
    {
        ImGui.PushID(blockId);
        try
        {
            var showVisibleSyncshellUsersOnlyInSyncshells = configService.Current.ShowVisibleSyncshellUsersOnlyInSyncshells;
            if (ImGui.Checkbox("Show visible Syncshell users only in Syncshells", ref showVisibleSyncshellUsersOnlyInSyncshells))
            {
                configService.Current.ShowVisibleSyncshellUsersOnlyInSyncshells = showVisibleSyncshellUsersOnlyInSyncshells;
                configService.Save();
                mediator.Publish(new StructuralRefreshUiMessage());
            }

            uiShared.DrawHelpText("When enabled, visible users who are only connected through Syncshells will only appear in their respective Syncshells and not in the separate 'Visible' group.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawShowOfflineUsersSeparatelyOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "ShowOfflineUsersSeparately")
    {
        ImGui.PushID(blockId);
        try
        {
            var showOfflineSeparate = configService.Current.ShowOfflineUsersSeparately;
            if (ImGui.Checkbox("Show offline users separately", ref showOfflineSeparate))
            {
                configService.Current.ShowOfflineUsersSeparately = showOfflineSeparate;
                configService.Save();
                mediator.Publish(new StructuralRefreshUiMessage());
            }

            uiShared.DrawHelpText("Directly paired offline users will appear in a separate 'Offline' group. Offline syncshell members remain in their syncshells.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawAlsoShowOfflineSyncshellUsersSeparatelyOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "AlsoShowOfflineSyncshellUsersSeparately")
    {
        ImGui.PushID(blockId);
        try
        {
            var showSyncshellOfflineSeparate = configService.Current.ShowSyncshellOfflineUsersSeparately;
            if (ImGui.Checkbox("Also show offline Syncshell users separately", ref showSyncshellOfflineSeparate))
            {
                configService.Current.ShowSyncshellOfflineUsersSeparately = showSyncshellOfflineSeparate;
                configService.Save();
                mediator.Publish(new StructuralRefreshUiMessage());
            }

            uiShared.DrawHelpText("When enabled, offline syncshell members will also appear in a separate 'Offline Syncshell' group instead of remaining in their syncshells.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawGroupUpAllSyncshellsInOneFolderOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "GroupUpAllSyncshellsInOneFolder")
    {
        ImGui.PushID(blockId);
        try
        {
            var groupUpSyncshells = configService.Current.GroupUpSyncshells;
            if (ImGui.Checkbox("Group up all syncshells in one folder", ref groupUpSyncshells))
            {
                configService.Current.GroupUpSyncshells = groupUpSyncshells;
                configService.Save();
                mediator.Publish(new StructuralRefreshUiMessage());
            }

            uiShared.DrawHelpText("This will group up all Syncshells in a special 'All Syncshells' folder in the main UI.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static bool DrawShowSpheneProfilesOnHoverOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "ShowSpheneProfilesOnHover")
    {
        ImGui.PushID(blockId);
        try
        {
            var showProfiles = configService.Current.ProfilesShow;
            if (ImGui.Checkbox("Show Sphene Profiles on Hover", ref showProfiles))
            {
                mediator.Publish(new ClearProfileDataMessage());
                configService.Current.ProfilesShow = showProfiles;
                configService.Save();
            }

            uiShared.DrawHelpText("This will show the configured user profile after a set delay");
            return showProfiles;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawPopoutProfilesOnTheRightOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "PopoutProfilesOnTheRight")
    {
        ImGui.PushID(blockId);
        try
        {
            var profileOnRight = configService.Current.ProfilePopoutRight;
            if (ImGui.Checkbox("Popout profiles on the right", ref profileOnRight))
            {
                configService.Current.ProfilePopoutRight = profileOnRight;
                configService.Save();
                mediator.Publish(new CompactUiChange(Vector2.Zero, Vector2.Zero));
            }

            uiShared.DrawHelpText("Will show profiles on the right side of the main UI");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawHoverDelayOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "HoverDelay")
    {
        ImGui.PushID(blockId);
        try
        {
            var profileDelay = configService.Current.ProfileDelay;
            if (ImGui.SliderFloat("Hover Delay", ref profileDelay, 1, 10))
            {
                configService.Current.ProfileDelay = profileDelay;
                configService.Save();
            }

            uiShared.DrawHelpText("Delay until the profile should be displayed");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawShowProfilesMarkedAsNsfwOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "ShowProfilesMarkedAsNsfw")
    {
        ImGui.PushID(blockId);
        try
        {
            var showNsfwProfiles = configService.Current.ProfilesAllowNsfw;
            if (ImGui.Checkbox("Show profiles marked as NSFW", ref showNsfwProfiles))
            {
                mediator.Publish(new ClearProfileDataMessage());
                configService.Current.ProfilesAllowNsfw = showNsfwProfiles;
                configService.Save();
            }

            uiShared.DrawHelpText("Will show profiles that have the NSFW tag enabled");
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
