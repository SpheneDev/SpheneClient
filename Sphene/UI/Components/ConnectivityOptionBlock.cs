using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Microsoft.AspNetCore.Http.Connections;
using Sphene.API.Dto;
using Sphene.Services;
using Sphene.Services.ServerConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.WebAPI;

namespace Sphene.UI.Components;

public static class ConnectivityOptionBlock
{
    public static void DrawSendStatisticalCensusDataOption(ServerConfigurationManager serverConfigurationManager, UiSharedService uiShared, string blockId = "SendStatisticalCensusData")
    {
        ImGui.PushID(blockId);
        try
        {
            var sendCensus = serverConfigurationManager.SendCensusData;
            if (ImGui.Checkbox("Send Statistical Census Data", ref sendCensus))
            {
                serverConfigurationManager.SendCensusData = sendCensus;
            }

            uiShared.DrawHelpText("This will allow sending census data to the currently connected service." + UiSharedService.TooltipSeparator
                + "Census data contains:" + Environment.NewLine
                + "- Current World" + Environment.NewLine
                + "- Current Gender" + Environment.NewLine
                + "- Current Race" + Environment.NewLine
                + "- Current Clan (this is not your Free Company, this is e.g. Keeper or Seeker for Miqo'te)" + UiSharedService.TooltipSeparator
                + "The census data is only saved temporarily and will be removed from the server on disconnect. It is stored temporarily associated with your UID while you are connected." + UiSharedService.TooltipSeparator
                + "If you do not wish to participate in the statistical census, untick this box and reconnect to the server.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawCharacterAutoLoginOption(Authentication authentication, ServerConfigurationManager serverConfigurationManager, UiSharedService uiShared, string blockId = "CharacterAutoLogin")
    {
        ImGui.PushID(blockId);
        try
        {
            var isAutoLogin = authentication.AutoLogin;
            if (ImGui.Checkbox("Automatically login to Sphene", ref isAutoLogin))
            {
                authentication.AutoLogin = isAutoLogin;
                serverConfigurationManager.Save();
            }

            uiShared.DrawHelpText("When enabled and logging into this character in XIV, Sphene will automatically connect to the current service.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawServerTransportTypeOption(ServerConfigurationManager serverConfigurationManager, UiSharedService uiShared, float itemWidth, string blockId = "ServerTransportType")
    {
        ImGui.PushID(blockId);
        try
        {
            ImGui.SetNextItemWidth(itemWidth);
            var serverTransport = serverConfigurationManager.GetTransport();
            uiShared.DrawCombo("Server Transport Type", Enum.GetValues<HttpTransportType>().Where(t => t != HttpTransportType.None),
                v => v.ToString(),
                onSelected: t => serverConfigurationManager.SetTransportType(t),
                serverTransport);

            uiShared.DrawHelpText("You normally do not need to change this, if you don't know what this is or what it's for, keep it to WebSockets." + Environment.NewLine
                + "If you run into connection issues with e.g. VPNs, try ServerSentEvents first before trying out LongPolling." + UiSharedService.TooltipSeparator
                + "Note: if the server does not support a specific Transport Type it will fall through to the next automatically: WebSockets > ServerSentEvents > LongPolling");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawWineForceWebSocketsOption(ServerStorage selectedServer, ServerConfigurationManager serverConfigurationManager, UiSharedService uiShared, string blockId = "WineForceWebSockets")
    {
        ImGui.PushID(blockId);
        try
        {
            var forceWebSockets = selectedServer.ForceWebSockets;
            if (ImGui.Checkbox("[wine only] Force WebSockets", ref forceWebSockets))
            {
                selectedServer.ForceWebSockets = forceWebSockets;
                serverConfigurationManager.Save();
            }

            uiShared.DrawHelpText("On wine, Sphene will automatically fall back to ServerSentEvents/LongPolling, even if WebSockets is selected. "
                + "WebSockets are known to crash XIV entirely on wine 8.5 shipped with Dalamud. "
                + "Only enable this if you are not running wine 8.5." + Environment.NewLine
                + "Note: If the issue gets resolved at some point this option will be removed.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawUseDiscordOAuthOption(ServerStorage selectedServer, bool useOauth, ServerConfigurationManager serverConfigurationManager, UiSharedService uiShared, string blockId = "UseDiscordOAuth")
    {
        ImGui.PushID(blockId);
        try
        {
            if (ImGui.Checkbox("Use Discord OAuth2 Authentication", ref useOauth))
            {
                selectedServer.UseOAuth2 = useOauth;
                serverConfigurationManager.Save();
            }

            uiShared.DrawHelpText("Use Discord OAuth2 Authentication to identify with this server instead of secret keys");
            if (!useOauth)
            {
                return;
            }

            uiShared.DrawOAuth(selectedServer);
            var discordUser = serverConfigurationManager.GetDiscordUserFromToken(selectedServer);
            if (string.IsNullOrEmpty(discordUser))
            {
                ImGuiHelpers.ScaledDummy(10f);
                UiSharedService.ColorTextWrapped("You have enabled OAuth2 but it is not linked. Press the buttons Check, then Authenticate to link properly.", ImGuiColors.DalamudRed);
            }

            if (!string.IsNullOrEmpty(discordUser) && selectedServer.Authentications.TrueForAll(u => string.IsNullOrEmpty(u.UID)))
            {
                ImGuiHelpers.ScaledDummy(10f);
                UiSharedService.ColorTextWrapped("You have enabled OAuth2 but no characters configured. Set the correct UIDs for your characters in \"Character Management\".", ImGuiColors.DalamudRed);
            }

        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawPreferredPermissionsOption(DefaultPermissionsDto permissions, ApiController apiController, UiSharedService uiShared, string blockId = "PreferredPermissions")
    {
        ImGui.PushID(blockId);
        try
        {
            var individualIsSticky = permissions.IndividualIsSticky;
            if (ImGui.Checkbox("Individually set permissions become preferred permissions", ref individualIsSticky))
            {
                permissions.IndividualIsSticky = individualIsSticky;
                _ = apiController.UserUpdateDefaultPermissions(permissions);
            }

            uiShared.DrawHelpText("The preferred attribute means that the permissions to that user will never change through any of your permission changes to Syncshells " +
                "(i.e. if you have paused one specific user in a Syncshell and they become preferred permissions, then pause and unpause the same Syncshell, the user will remain paused - " +
                "if a user does not have preferred permissions, it will follow the permissions of the Syncshell and be unpaused)." + Environment.NewLine + Environment.NewLine +
                "This setting means:" + Environment.NewLine +
                "  - All new individual pairs get their permissions defaulted to preferred permissions." + Environment.NewLine +
                "  - All individually set permissions for any pair will also automatically become preferred permissions. This includes pairs in Syncshells." + Environment.NewLine + Environment.NewLine +
                "It is possible to remove or set the preferred permission state for any pair at any time." + Environment.NewLine + Environment.NewLine +
                "If unsure, leave this setting off.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawDisableIndividualPairSoundsOption(DefaultPermissionsDto permissions, ApiController apiController, UiSharedService uiShared, string blockId = "DisableIndividualPairSounds")
    {
        ImGui.PushID(blockId);
        try
        {
            var disableIndividualSounds = permissions.DisableIndividualSounds;
            if (ImGui.Checkbox("Disable individual pair sounds", ref disableIndividualSounds))
            {
                permissions.DisableIndividualSounds = disableIndividualSounds;
                _ = apiController.UserUpdateDefaultPermissions(permissions);
            }

            uiShared.DrawHelpText("This setting will disable sound sync for all new individual pairs.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawDisableIndividualPairAnimationsOption(DefaultPermissionsDto permissions, ApiController apiController, UiSharedService uiShared, string blockId = "DisableIndividualPairAnimations")
    {
        ImGui.PushID(blockId);
        try
        {
            var disableIndividualAnimations = permissions.DisableIndividualAnimations;
            if (ImGui.Checkbox("Disable individual pair animations", ref disableIndividualAnimations))
            {
                permissions.DisableIndividualAnimations = disableIndividualAnimations;
                _ = apiController.UserUpdateDefaultPermissions(permissions);
            }

            uiShared.DrawHelpText("This setting will disable animation sync for all new individual pairs.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawDisableIndividualPairVfxOption(DefaultPermissionsDto permissions, ApiController apiController, UiSharedService uiShared, string blockId = "DisableIndividualPairVfx")
    {
        ImGui.PushID(blockId);
        try
        {
            var disableIndividualVfx = permissions.DisableIndividualVFX;
            if (ImGui.Checkbox("Disable individual pair VFX", ref disableIndividualVfx))
            {
                permissions.DisableIndividualVFX = disableIndividualVfx;
                _ = apiController.UserUpdateDefaultPermissions(permissions);
            }

            uiShared.DrawHelpText("This setting will disable VFX sync for all new individual pairs.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawDisableSyncshellPairSoundsOption(DefaultPermissionsDto permissions, ApiController apiController, UiSharedService uiShared, string blockId = "DisableSyncshellPairSounds")
    {
        ImGui.PushID(blockId);
        try
        {
            var disableGroupSounds = permissions.DisableGroupSounds;
            if (ImGui.Checkbox("Disable Syncshell pair sounds", ref disableGroupSounds))
            {
                permissions.DisableGroupSounds = disableGroupSounds;
                _ = apiController.UserUpdateDefaultPermissions(permissions);
            }

            uiShared.DrawHelpText("This setting will disable sound sync for all non-sticky pairs in newly joined syncshells.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawDisableSyncshellPairAnimationsOption(DefaultPermissionsDto permissions, ApiController apiController, UiSharedService uiShared, string blockId = "DisableSyncshellPairAnimations")
    {
        ImGui.PushID(blockId);
        try
        {
            var disableGroupAnimations = permissions.DisableGroupAnimations;
            if (ImGui.Checkbox("Disable Syncshell pair animations", ref disableGroupAnimations))
            {
                permissions.DisableGroupAnimations = disableGroupAnimations;
                _ = apiController.UserUpdateDefaultPermissions(permissions);
            }

            uiShared.DrawHelpText("This setting will disable animation sync for all non-sticky pairs in newly joined syncshells.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawDisableSyncshellPairVfxOption(DefaultPermissionsDto permissions, ApiController apiController, UiSharedService uiShared, string blockId = "DisableSyncshellPairVfx")
    {
        ImGui.PushID(blockId);
        try
        {
            var disableGroupVfx = permissions.DisableGroupVFX;
            if (ImGui.Checkbox("Disable Syncshell pair VFX", ref disableGroupVfx))
            {
                permissions.DisableGroupVFX = disableGroupVfx;
                _ = apiController.UserUpdateDefaultPermissions(permissions);
            }

            uiShared.DrawHelpText("This setting will disable VFX sync for all non-sticky pairs in newly joined syncshells.");
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
