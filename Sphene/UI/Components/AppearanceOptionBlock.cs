using Dalamud.Bindings.ImGui;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public static class AppearanceOptionBlock
{
    public static void DrawShowSpheneIconOption(SpheneConfigService configService, string blockId = "ShowSpheneIcon")
    {
        ImGui.PushID(blockId);
        try
        {
            var showSpheneIcon = configService.Current.ShowSpheneIcon;
            if (SpheneUI.Controls.Toggle("Show Sphene Icon", ref showSpheneIcon))
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

    public static void DrawHideSpheneIconInGposeOption(SpheneConfigService configService, string blockId = "HideSpheneIconInGpose")
    {
        ImGui.PushID(blockId);
        try
        {
            var hideInGpose = configService.Current.HideSpheneIconInGpose;
            if (SpheneUI.Controls.Toggle("Hide Sphene Icon in GPose", ref hideInGpose))
            {
                configService.Current.HideSpheneIconInGpose = hideInGpose;
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
            if (SpheneUI.Controls.Toggle("Lock Sphene Icon position", ref lockSpheneIcon))
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

    /// <summary>Toggle: restore the Sphene Hub window on startup when it was open last session.</summary>
    public static void DrawReopenSpheneHubOnStartupOption(SpheneConfigService configService, string blockId = "ReopenSpheneHubOnStartup")
    {
        ImGui.PushID(blockId);
        try
        {
            var reopen = configService.Current.ReopenSpheneHubOnStartup;
            if (SpheneUI.Controls.Toggle("Reopen Sphene Hub on startup if it was open", ref reopen))
            {
                configService.Current.ReopenSpheneHubOnStartup = reopen;
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
            if (SpheneUI.Controls.Toggle("Enable game right-click menus", ref enableRightClickMenu))
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
            if (SpheneUI.Controls.Toggle("Show status in Server Info Bar", ref enableDtrEntry))
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
            if (SpheneUI.Controls.Toggle("Show display name in tooltip", ref showUidInDtrTooltip))
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
            if (SpheneUI.Controls.Toggle("Prefer notes in tooltip", ref preferNoteInDtrTooltip))
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
            if (SpheneUI.Controls.Toggle("Use status colors", ref useColorsInDtr))
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
            if (SpheneUI.Controls.Toggle("Set visible pairs as focus targets when clicking the eye", ref useFocusTarget))
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

    public static void DrawShowVisibleUsersSeparatelyOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "ShowVisibleUsersSeparately")
    {
        ImGui.PushID(blockId);
        try
        {
            var showVisibleSeparate = configService.Current.ShowVisibleUsersSeparately;
            if (SpheneUI.Controls.Toggle("Show visible users separately", ref showVisibleSeparate))
            {
                configService.Current.ShowVisibleUsersSeparately = showVisibleSeparate;
                configService.Save();
                mediator.Publish(new StructuralRefreshUiMessage());
            }

            SpheneUI.Controls.HelpText("Visible users will appear in a separate 'Visible' group instead of being mixed with other users.");
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
            if (SpheneUI.Controls.Toggle("Show visible Syncshell users only in Syncshells", ref showVisibleSyncshellUsersOnlyInSyncshells))
            {
                configService.Current.ShowVisibleSyncshellUsersOnlyInSyncshells = showVisibleSyncshellUsersOnlyInSyncshells;
                configService.Save();
                mediator.Publish(new StructuralRefreshUiMessage());
            }

            SpheneUI.Controls.HelpText("When enabled, visible users who are only connected through Syncshells will only appear in their respective Syncshells and not in the separate 'Visible' group.");
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
            if (SpheneUI.Controls.Toggle("Show offline users separately", ref showOfflineSeparate))
            {
                configService.Current.ShowOfflineUsersSeparately = showOfflineSeparate;
                configService.Save();
                mediator.Publish(new StructuralRefreshUiMessage());
            }

            SpheneUI.Controls.HelpText("Directly paired offline users will appear in a separate 'Offline' group. Offline syncshell members remain in their syncshells.");
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
            if (SpheneUI.Controls.Toggle("Also show offline Syncshell users separately", ref showSyncshellOfflineSeparate))
            {
                configService.Current.ShowSyncshellOfflineUsersSeparately = showSyncshellOfflineSeparate;
                configService.Save();
                mediator.Publish(new StructuralRefreshUiMessage());
            }

            SpheneUI.Controls.HelpText("When enabled, offline syncshell members will also appear in a separate 'Offline Syncshell' group instead of remaining in their syncshells.");
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
            if (SpheneUI.Controls.Toggle("Group up all syncshells in one folder", ref groupUpSyncshells))
            {
                configService.Current.GroupUpSyncshells = groupUpSyncshells;
                configService.Save();
                mediator.Publish(new StructuralRefreshUiMessage());
            }

            SpheneUI.Controls.HelpText("This will group up all Syncshells in a special 'All Syncshells' folder in the main UI.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static bool DrawShowSpheneProfilesOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "ShowSpheneProfiles")
    {
        ImGui.PushID(blockId);
        try
        {
            var showProfiles = configService.Current.ProfilesShow;
            if (SpheneUI.Controls.Toggle("Show Sphene Profiles", ref showProfiles))
            {
                mediator.Publish(new ClearProfileDataMessage());
                configService.Current.ProfilesShow = showProfiles;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("Right-click a pair row to pop out the configured user profile");
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
            if (SpheneUI.Controls.Toggle("Popout profiles on the right", ref profileOnRight))
            {
                configService.Current.ProfilePopoutRight = profileOnRight;
                configService.Save();
                mediator.Publish(new CompactUiChange(Vector2.Zero, Vector2.Zero));
            }

            SpheneUI.Controls.HelpText("Will show profiles on the right side of the main UI");
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
            if (SpheneUI.Controls.Toggle("Show profiles marked as NSFW", ref showNsfwProfiles))
            {
                mediator.Publish(new ClearProfileDataMessage());
                configService.Current.ProfilesAllowNsfw = showNsfwProfiles;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("Will show profiles that have the NSFW tag enabled");
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
