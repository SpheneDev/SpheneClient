using Dalamud.Bindings.ImGui;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public static class AcknowledgmentOptionBlock
{
    public static bool DrawShowAcknowledgmentNotificationsOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowAcknowledgmentNotifications")
    {
        ImGui.PushID(blockId);
        try
        {
            var showPopups = configService.Current.ShowAcknowledgmentPopups;
            if (SpheneUI.Controls.Toggle("Show Acknowledgment Notifications", ref showPopups))
            {
                configService.Current.ShowAcknowledgmentPopups = showPopups;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("Enable or disable notifications for acknowledgment requests. Disable to prevent spam when receiving many requests.");
            return showPopups;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawShowWaitingForAcknowledgmentPopupsOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowWaitingForAcknowledgmentPopups")
    {
        ImGui.PushID(blockId);
        try
        {
            var showWaitingPopups = configService.Current.ShowWaitingForAcknowledgmentPopups;
            if (SpheneUI.Controls.Toggle("Show 'Waiting for Acknowledgment' Popups", ref showWaitingPopups))
            {
                configService.Current.ShowWaitingForAcknowledgmentPopups = showWaitingPopups;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("Enable or disable 'waiting for acknowledgment' popups. Success notifications show regardless of this setting.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawAcknowledgmentNotificationLocationOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "AcknowledgmentNotificationLocation")
    {
        ImGui.PushID(blockId);
        try
        {
            var notificationLocation = (int)configService.Current.AcknowledgmentNotification;
            var notificationOptions = new[] { "None", "Chat", "Toast", "Both" };
            if (SpheneUI.Controls.LabeledCombo("Notification Location", ref notificationLocation, notificationOptions, notificationOptions.Length))
            {
                configService.Current.AcknowledgmentNotification = (NotificationLocation)notificationLocation;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("Choose where acknowledgment notifications should appear.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawEnableBatchingOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "EnableBatching")
    {
        ImGui.PushID(blockId);
        try
        {
            var enableBatching = configService.Current.EnableAcknowledgmentBatching;
            if (SpheneUI.Controls.Toggle("Enable Batching", ref enableBatching))
            {
                configService.Current.EnableAcknowledgmentBatching = enableBatching;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("Group multiple acknowledgments for better performance. Recommended with many active connections.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawEnableAutoRetryOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "EnableAutoRetry")
    {
        ImGui.PushID(blockId);
        try
        {
            var enableAutoRetry = configService.Current.EnableAcknowledgmentAutoRetry;
            if (SpheneUI.Controls.Toggle("Enable Auto Retry", ref enableAutoRetry))
            {
                configService.Current.EnableAcknowledgmentAutoRetry = enableAutoRetry;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("Automatically retry failed acknowledgments to improve reliability.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawAcknowledgmentTimeoutOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "AcknowledgmentTimeout")
    {
        ImGui.PushID(blockId);
        try
        {
            var timeoutSeconds = configService.Current.AcknowledgmentTimeoutSeconds;
            if (SpheneUI.Controls.SliderInt("Acknowledgment Timeout (seconds)", ref timeoutSeconds, 5, 120))
            {
                configService.Current.AcknowledgmentTimeoutSeconds = timeoutSeconds;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("How long to wait for acknowledgment responses before timing out.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawResetAcknowledgmentSettingsToDefaultsOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ResetAcknowledgmentSettingsToDefaults")
    {
        ImGui.PushID(blockId);
        try
        {
            if (SpheneUI.Controls.Button("Reset Acknowledgment Settings to Defaults"))
            {
                configService.Current.ShowAcknowledgmentPopups = false;
                configService.Current.ShowWaitingForAcknowledgmentPopups = false;
                configService.Current.EnableAcknowledgmentBatching = true;
                configService.Current.EnableAcknowledgmentAutoRetry = true;
                configService.Current.AcknowledgmentTimeoutSeconds = 30;
                configService.Current.AcknowledgmentNotification = NotificationLocation.Chat;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("Reset all acknowledgment settings to their default values.");
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
