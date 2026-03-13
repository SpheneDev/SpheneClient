using Dalamud.Bindings.ImGui;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;

namespace Sphene.UI.Components;

public static class AcknowledgmentOptionBlock
{
    public static bool DrawShowAcknowledgmentNotificationsOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowAcknowledgmentNotifications")
    {
        ImGui.PushID(blockId);
        try
        {
            var showPopups = configService.Current.ShowAcknowledgmentPopups;
            if (ImGui.Checkbox("Show Acknowledgment Notifications", ref showPopups))
            {
                configService.Current.ShowAcknowledgmentPopups = showPopups;
                configService.Save();
            }

            uiShared.DrawHelpText("Enable or disable notifications for acknowledgment requests. Disable to prevent spam when receiving many requests.");
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
            if (ImGui.Checkbox("Show 'Waiting for Acknowledgment' Popups", ref showWaitingPopups))
            {
                configService.Current.ShowWaitingForAcknowledgmentPopups = showWaitingPopups;
                configService.Save();
            }

            uiShared.DrawHelpText("Enable or disable 'waiting for acknowledgment' popups. Success notifications show regardless of this setting.");
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
            if (ImGui.Combo("Notification Location", ref notificationLocation, notificationOptions, notificationOptions.Length))
            {
                configService.Current.AcknowledgmentNotification = (NotificationLocation)notificationLocation;
                configService.Save();
            }

            uiShared.DrawHelpText("Choose where acknowledgment notifications should appear.");
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
            if (ImGui.Checkbox("Enable Batching", ref enableBatching))
            {
                configService.Current.EnableAcknowledgmentBatching = enableBatching;
                configService.Save();
            }

            uiShared.DrawHelpText("Group multiple acknowledgments for better performance. Recommended with many active connections.");
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
            if (ImGui.Checkbox("Enable Auto Retry", ref enableAutoRetry))
            {
                configService.Current.EnableAcknowledgmentAutoRetry = enableAutoRetry;
                configService.Save();
            }

            uiShared.DrawHelpText("Automatically retry failed acknowledgments to improve reliability.");
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
            if (ImGui.SliderInt("Acknowledgment Timeout (seconds)", ref timeoutSeconds, 5, 120))
            {
                configService.Current.AcknowledgmentTimeoutSeconds = timeoutSeconds;
                configService.Save();
            }

            uiShared.DrawHelpText("How long to wait for acknowledgment responses before timing out.");
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
            if (ImGui.Button("Reset Acknowledgment Settings to Defaults"))
            {
                configService.Current.ShowAcknowledgmentPopups = false;
                configService.Current.ShowWaitingForAcknowledgmentPopups = false;
                configService.Current.EnableAcknowledgmentBatching = true;
                configService.Current.EnableAcknowledgmentAutoRetry = true;
                configService.Current.AcknowledgmentTimeoutSeconds = 30;
                configService.Current.AcknowledgmentNotification = NotificationLocation.Chat;
                configService.Save();
            }

            uiShared.DrawHelpText("Reset all acknowledgment settings to their default values.");
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
