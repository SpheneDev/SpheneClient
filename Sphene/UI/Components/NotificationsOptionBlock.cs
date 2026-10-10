using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Sphene.API.Data;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public static class NotificationsOptionBlock
{
    public static void DrawInfoNotificationDisplayOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "InfoNotificationDisplay")
    {
        SpheneUI.Controls.Combo("Info Notification Display##" + blockId, (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), i => i.ToString(),
        i =>
        {
            configService.Current.InfoNotification = i;
            configService.Save();
        }, configService.Current.InfoNotification);

        SpheneUI.Controls.HelpText("The location where 'Info' notifications will display. Nowhere will not show any Info notifications; Chat prints in chat; Toast shows a toast; Both shows chat and toast.");
    }

    public static void DrawWarningNotificationDisplayOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "WarningNotificationDisplay")
    {
        SpheneUI.Controls.Combo("Warning Notification Display##" + blockId, (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), i => i.ToString(),
        i =>
        {
            configService.Current.WarningNotification = i;
            configService.Save();
        }, configService.Current.WarningNotification);

        SpheneUI.Controls.HelpText("The location where 'Warning' notifications will display. Nowhere, Chat, Toast, or Both.");
    }

    public static void DrawErrorNotificationDisplayOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ErrorNotificationDisplay")
    {
        SpheneUI.Controls.Combo("Error Notification Display##" + blockId, (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), i => i.ToString(),
        i =>
        {
            configService.Current.ErrorNotification = i;
            configService.Save();
        }, configService.Current.ErrorNotification);

        SpheneUI.Controls.HelpText("The location where 'Error' notifications will display. Nowhere, Chat, Toast, or Both.");
    }

    public static void DrawDisableOptionalPluginWarningsOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "DisableOptionalPluginWarnings")
    {
        var disableOptionalPluginWarnings = configService.Current.DisableOptionalPluginWarnings;
        if (SpheneUI.Controls.Toggle("Disable optional plugin warnings##" + blockId, ref disableOptionalPluginWarnings))
        {
            configService.Current.DisableOptionalPluginWarnings = disableOptionalPluginWarnings;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("Suppress 'Warning' messages for missing optional plugins.");
    }

    public static void DrawEnableOnlineNotificationsOption(SpheneConfigService configService, string blockId = "EnableOnlineNotifications")
    {
        var onlineNotifs = configService.Current.ShowOnlineNotifications;
        if (SpheneUI.Controls.Toggle("Enable online notifications##" + blockId, ref onlineNotifs))
        {
            configService.Current.ShowOnlineNotifications = onlineNotifs;
            configService.Save();
        }
    }

    public static void DrawOnlyForIndividualPairsOption(SpheneConfigService configService, string blockId = "OnlyForIndividualPairs")
    {
        var onlineNotifsPairsOnly = configService.Current.ShowOnlineNotificationsOnlyForIndividualPairs;
        if (SpheneUI.Controls.Toggle("Only for individual pairs##" + blockId, ref onlineNotifsPairsOnly))
        {
            configService.Current.ShowOnlineNotificationsOnlyForIndividualPairs = onlineNotifsPairsOnly;
            configService.Save();
        }
    }

    public static void DrawOnlyForNamedPairsOption(SpheneConfigService configService, string blockId = "OnlyForNamedPairs")
    {
        var onlineNotifsNamedOnly = configService.Current.ShowOnlineNotificationsOnlyForNamedPairs;
        if (SpheneUI.Controls.Toggle("Only for named pairs##" + blockId, ref onlineNotifsNamedOnly))
        {
            configService.Current.ShowOnlineNotificationsOnlyForNamedPairs = onlineNotifsNamedOnly;
            configService.Save();
        }
    }

    public static void DrawShowTestBuildUpdatesOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowTestBuildUpdates")
    {
        var showTestBuildUpdates = configService.Current.ShowTestBuildUpdates;
        if (SpheneUI.Controls.Toggle("Show testbuild update hints##" + blockId, ref showTestBuildUpdates))
        {
            configService.Current.ShowTestBuildUpdates = showTestBuildUpdates;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("When enabled, update checks include testbuild versions and show update hints for them.");
    }

    public static bool DrawEnableAreaBoundSyncshellNotificationsOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "EnableAreaBoundSyncshellNotifications")
    {
        var areaBoundNotifs = configService.Current.ShowAreaBoundSyncshellNotifications;
        if (SpheneUI.Controls.Toggle("Enable area-bound syncshell notifications##" + blockId, ref areaBoundNotifs))
        {
            configService.Current.ShowAreaBoundSyncshellNotifications = areaBoundNotifs;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("Show notifications when area-bound syncshells become available to join in your current location.");
        return configService.Current.ShowAreaBoundSyncshellNotifications;
    }

    public static void DrawAreaBoundNotificationDisplayOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "AreaBoundNotificationDisplay")
    {
        SpheneUI.Controls.Combo("Area-bound Notification Display##" + blockId, (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), i => i.ToString(),
        i =>
        {
            configService.Current.AreaBoundSyncshellNotification = i;
            configService.Save();
        }, configService.Current.AreaBoundSyncshellNotification);

        SpheneUI.Controls.HelpText("Choose where area-bound syncshell notifications should appear. Nowhere, Chat, Toast, or Both.");
    }

    public static void DrawShowAreaBoundSyncshellWelcomeMessagesOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowAreaBoundSyncshellWelcomeMessages")
    {
        var showWelcomeMessages = configService.Current.ShowAreaBoundSyncshellWelcomeMessages;
        if (SpheneUI.Controls.Toggle("Show area-bound syncshell welcome messages##" + blockId, ref showWelcomeMessages))
        {
            configService.Current.ShowAreaBoundSyncshellWelcomeMessages = showWelcomeMessages;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("Automatically show welcome messages when joining area-bound syncshells. When disabled, you can still view welcome messages by clicking the area-bound indicator next to the syncshell name.");
    }

    public static void DrawAutomaticallyShowAreaBoundSyncshellConsentOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "AutomaticallyShowAreaBoundSyncshellConsent")
    {
        var autoShowConsent = configService.Current.AutoShowAreaBoundSyncshellConsent;
        if (SpheneUI.Controls.Toggle("Automatically show area-bound syncshell consent##" + blockId, ref autoShowConsent))
        {
            configService.Current.AutoShowAreaBoundSyncshellConsent = autoShowConsent;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("When enabled, consent dialogs for area-bound syncshells will appear automatically when entering areas. When disabled, you can manually trigger consent using the button in the Compact UI. This setting also controls city syncshell join requests.");
    }

    public static void DrawSpheneDefaultSoundModeOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "SpheneDefaultSoundMode")
    {
        SpheneUI.Controls.HelpText("Notification sounds can use Game System SCD sounds, Sphene Default built-in WAV sounds, or Custom Sound files. Each notification type has a preset default.");
        SpheneUI.Layout.VerticalSpace();

        void DrawNotificationSection(string label, NotificationSoundConfig soundConfig, string defaultText, string sectionId)
        {
            if (SpheneUI.Controls.CollapsibleSection(label))
            {
                SpheneUI.Text.ColoredWrapped($"Default: {defaultText}", ImGuiColors.DalamudGrey);

                if (NotificationSoundOptionBlock.DrawNotificationSoundConfig(configService, uiShared, soundConfig, onTestSound: config => mediator.Publish(new PlayNotificationSoundTestMessage(config)), blockId: sectionId))
                {
                    configService.Save();
                }
            }
        }

        DrawNotificationSection("Info Notifications", configService.Current.InfoNotificationSound, "Sphene Default – Default sound, Volume 0.30", blockId + "Info");
        DrawNotificationSection("Warning Notifications", configService.Current.WarningNotificationSound, "Sphene Default – Error sound, Volume 0.00 (silent until changed)", blockId + "Warning");
        DrawNotificationSection("Error Notifications", configService.Current.ErrorNotificationSound, "Sphene Default – Error sound, Volume 0.30", blockId + "Error");
        DrawNotificationSection("Success Notifications", configService.Current.SuccessNotificationSound, "Sphene Default – Default sound, Volume 0.30", blockId + "Success");
        DrawNotificationSection("Area-bound Syncshell Notifications", configService.Current.AreaBoundNotificationSound, "Sphene Default – Attention sound, Volume 0.30", blockId + "AreaBound");
        DrawNotificationSection("File Transfer Notifications", configService.Current.FileTransferNotificationSound, "Sphene Default – Attention sound, Volume 0.30", blockId + "FileTransfer");
    }
}
