using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services.CharaData.Models;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.CharaDataHub;

internal sealed partial class CharaDataHubUi
{
    private string _joinLobbyId = string.Empty;

    private void DrawGposeTogether()
    {
        DrawPageTitle("GPose Together",
            "Multiplayer GPose sessions — create or join a lobby and pose together.");

        var brioReady = _charaDataManager.BrioAvailable;
        var connected = _uiSharedService.ApiController.IsConnected;
        if (!brioReady)
        {
            SpheneUI.Layout.Callout("GPose Together runs on Brio — install and enable it to use lobbies.",
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed, "Brio required");
        }
        if (!connected)
        {
            SpheneUI.Layout.Callout("Lobbies sync through the Sphene server — reconnect to use GPose Together.",
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed, "Disconnected");
        }
        if (!brioReady || !connected)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        DrawHelpFoldout("GPose together is a way to do multiplayer GPose sessions and collaborations." + UiSharedService.DoubleNewLine
            + "GPose together requires Brio to function. Only Brio is also supported for the actual posing interactions. Attempting to pose using other tools will lead to conflicts and exploding characters." + UiSharedService.DoubleNewLine
            + "To use GPose together you either create or join a GPose Together Lobby. After you and other people have joined, make sure that everyone is on the same map. "
            + "It is not required for you to be on the same server, DC or instance. Users that are on the same map will be drawn as moving purple wisps in the overworld, so you can easily find each other." + UiSharedService.DoubleNewLine
            + "Once you are close to each other you can initiate GPose. You must either assign or spawn characters for each of the lobby users. Their own poses and positions to their character will be automatically applied." + Environment.NewLine
            + "Pose and location data during GPose are updated approximately every 10-20s.");

        using var disabled = ImRaii.Disabled(!brioReady || !connected);
        var lobbyId = _charaDataGposeTogetherManager.CurrentGPoseLobbyId;
        var accent = SpheneUI.Palette.EtherealPurple;

        if (string.IsNullOrEmpty(lobbyId))
        {
            DrawGposeLobbyJoin(accent);
        }
        else
        {
            DrawGposeLobbySession(lobbyId, accent);
        }
    }

    /// <summary>The lobby entry state: create a fresh lobby or join by ID.</summary>
    private void DrawGposeLobbyJoin(Vector4 accent)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.MutedWrapped("Start a fresh lobby for your group, or join an existing one with its lobby ID.");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

            if (SpheneUI.Controls.SegmentButton("Create New Lobby", FontAwesomeIcon.Plus, accent: accent))
            {
                _charaDataGposeTogetherManager.CreateNewLobby();
            }

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            SpheneUI.Text.Muted("Join an existing lobby");
            SpheneUI.Layout.ScaledNextItemWidth(250);
            SpheneUI.Controls.InputTextWithHint("##lobbyId", "GPose Lobby ID", ref _joinLobbyId, 30);
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            if (SpheneUI.Controls.SegmentButton("Join", FontAwesomeIcon.ArrowRight,
                    accent: accent, enabled: !string.IsNullOrWhiteSpace(_joinLobbyId)))
            {
                _charaDataGposeTogetherManager.JoinGPoseLobby(_joinLobbyId);
                _joinLobbyId = string.Empty;
            }

            var lastLobby = _charaDataGposeTogetherManager.LastGPoseLobbyId;
            if (!string.IsNullOrEmpty(lastLobby))
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                if (SpheneUI.Controls.SmallButton($"Rejoin last lobby · {lastLobby}"))
                {
                    _charaDataGposeTogetherManager.JoinGPoseLobby(lastLobby);
                }
            }
        }, title: "Lobby", accentColor: accent);
    }

    /// <summary>The active lobby: ID + actions on top, member roster below.</summary>
    private void DrawGposeLobbySession(string lobbyId, Vector4 accent)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Muted("Lobby ID");
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            SpheneUI.Text.Colored(lobbyId, ImGuiColors.ParsedGreen);
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            if (SpheneUI.Controls.IconButton("##copyLobby", FontAwesomeIcon.Clipboard,
                    size: ImGui.GetFrameHeight()))
            {
                ImGui.SetClipboardText(lobbyId);
            }
            SpheneUI.AttachTooltip("Copy Lobby ID to clipboard.");

            ImGui.SameLine();
            // SegAutoWidth accounts for the chevron zone + paddings that a
            // bare text-size estimate misses — the button was clipping off
            // the card edge without it.
            var leaveW = SpheneUI.Controls.SegAutoWidth("Leave Lobby", null, ImGui.GetFrameHeight())
                + ImGui.GetStyle().ItemInnerSpacing.X;
            SpheneUI.Controls.DockToCardEdge(leaveW);
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (SpheneUI.Controls.SmallButton("Leave Lobby"))
                {
                    _charaDataGposeTogetherManager.LeaveGPoseLobby();
                }
            }
            SpheneUI.AttachTooltip("Leave the current GPose lobby." + SpheneUI.TooltipSeparator + "Hold CTRL and click to leave.");

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            if (SpheneUI.Controls.SegmentButton("Send Updated Character Data", FontAwesomeIcon.ArrowUp, accent: accent))
            {
                _ = _charaDataGposeTogetherManager.PushCharacterDownloadDto();
            }
            SpheneUI.AttachTooltip("Send your current appearance, pose and world data to everyone in the lobby.");
            if (!_uiSharedService.IsInGpose)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Text.MutedWrapped("Assigning users to characters is only available in GPose.");
            }
        }, title: "Lobby", accentColor: accent);

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        var gposeCharas = _dalamudUtilService.GetGposeCharactersFromObjectTable();
        var selfName = _dalamudUtilService.GetPlayerCharacter()?.Name.TextValue;
        gposeCharas = gposeCharas.Where(c => c != null
            && (selfName == null || !string.Equals(c!.Name.TextValue, selfName, StringComparison.Ordinal))).ToList();
        var users = _charaDataGposeTogetherManager.UsersInLobby;

        SpheneUI.Layout.SectionCard(() =>
        {
            if (!users.Any())
            {
                SpheneUI.Text.Muted("No other users in this lobby yet — share the lobby ID to invite them.");
            }
            else
            {
                foreach (var user in users)
                {
                    DrawLobbyUser(user, gposeCharas);
                }
            }
        }, title: $"Members ({users.Count()})", accentColor: accent);
    }

    private void DrawLobbyUser(GposeLobbyUserData user,
        IEnumerable<Dalamud.Game.ClientState.Objects.Types.ICharacter?> gposeCharas)
    {
        using var id = ImRaii.PushId(user.UserData.UID);
        var sameMapAndServer = _charaDataGposeTogetherManager.IsOnSameMapAndServer(user);
        var width = ImGui.GetContentRegionAvail().X;
        var scale = SpheneUI.Scale;
        var iconBtn = ImGui.GetFrameHeight();

        SpheneUI.Layout.Group(() =>
        {
            // Line 1: user name, sync-status icon cluster, apply/spawn docked right.
            ImGui.AlignTextToFramePadding();
            var note = _serverConfigurationManager.GetNoteForUid(user.UserData.UID);
            var userText = note == null ? user.UserData.AliasOrUID : $"{note} ({user.UserData.AliasOrUID})";
            SpheneUI.Text.Colored(userText, ImGuiColors.ParsedGreen);

            var iconW = ImGui.CalcTextSize(FontAwesomeIcon.Map.ToIconString()).X;
            var spacingX = ImGui.GetStyle().ItemSpacing.X;
            var trailW = iconW * 3f + iconBtn * 2f + spacingX * 4f
                + ImGui.GetStyle().ItemInnerSpacing.X;
            SpheneUI.Controls.DockToCardEdge(trailW);

            SpheneUI.Icons.Icon(FontAwesomeIcon.Map, sameMapAndServer.SameMap ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && user.WorldData != null)
            {
                _dalamudUtilService.SetMarkerAndOpenMap(new(user.WorldData.Value.PositionX, user.WorldData.Value.PositionY, user.WorldData.Value.PositionZ), user.Map);
            }
            SpheneUI.AttachTooltip((sameMapAndServer.SameMap ? "You are on the same map." : "You are not on the same map.")
                + SpheneUI.TooltipSeparator + user.WorldDataDescriptor + Environment.NewLine
                + "Click to open the user's location on your map. Same map is required for pose sync.");

            ImGui.SameLine();
            SpheneUI.Icons.Icon(FontAwesomeIcon.Globe, sameMapAndServer.SameServer ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
            SpheneUI.AttachTooltip((sameMapAndServer.SameServer ? "You are on the same server." : "You are not on the same server.")
                + SpheneUI.TooltipSeparator + "Same server is not required — users on other servers must be spawned as actors.");

            ImGui.SameLine();
            SpheneUI.Icons.Icon(FontAwesomeIcon.Running, sameMapAndServer.SameEverything ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
            SpheneUI.AttachTooltip(sameMapAndServer.SameEverything ? "You are in the same instanced area." : "You are not in the same instanced area."
                + SpheneUI.TooltipSeparator + "Users not in your instance appear as floating wisps on the same map.");

            ImGui.SameLine();
            using (ImRaii.Disabled(!_uiSharedService.IsInGpose || user.CharaData == null || user.Address == nint.Zero))
            {
                if (SpheneUI.Controls.IconButton("##apply", FontAwesomeIcon.ArrowRight, size: iconBtn))
                {
                    _ = _charaDataGposeTogetherManager.ApplyCharaData(user);
                }
            }
            SpheneUI.AttachTooltip("Apply newly received character data to the assigned actor." + SpheneUI.TooltipSeparator + "Grayed out when there is nothing new to apply.");
            ImGui.SameLine();
            using (ImRaii.Disabled(!_uiSharedService.IsInGpose || user.CharaData == null || sameMapAndServer.SameEverything))
            {
                if (SpheneUI.Controls.IconButton("##spawn", FontAwesomeIcon.Plus, size: iconBtn))
                {
                    _ = _charaDataGposeTogetherManager.SpawnAndApplyData(user);
                }
            }
            SpheneUI.AttachTooltip("Spawn a new actor, apply character data and assign it to this user." + SpheneUI.TooltipSeparator +
                "Grayed out when the user has sent no data, or you are already on the same map, server and instance — then join a group and assign instead.");

            // Line 2: actor assignment.
            using (ImRaii.Disabled(!_uiSharedService.IsInGpose))
            {
                var unassigned = user.Address == nint.Zero;
                var comboW = MathF.Min(240f * scale, ImGui.GetContentRegionAvail().X - iconBtn - spacingX * 2f - (unassigned ? iconW + spacingX : 0f));
                ImGui.SetNextItemWidth(MathF.Max(80f * scale, comboW));
                using (var ctl = SpheneUI.Controls.PushValueControlStyle())
                using (var combo = ImRaii.Combo("##character", unassigned ? "No character assigned" : CharaName(user.AssociatedCharaName)))
                {
                    if (combo)
                    {
                        foreach (var chara in gposeCharas)
                        {
                            if (chara == null) continue;

                            if (SpheneUI.Controls.Selectable(CharaName(chara.Name.TextValue), chara.Address == user.Address))
                            {
                                user.AssociatedCharaName = chara.Name.TextValue;
                                user.Address = chara.Address;
                            }
                        }
                    }
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(unassigned))
                {
                    if (SpheneUI.Controls.IconButton("##unassign", FontAwesomeIcon.Trash, size: iconBtn))
                    {
                        user.AssociatedCharaName = string.Empty;
                        user.Address = nint.Zero;
                    }
                }
                SpheneUI.AttachTooltip("Unassign the actor for this user");
                if (_uiSharedService.IsInGpose && unassigned)
                {
                    ImGui.SameLine();
                    SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed);
                    SpheneUI.AttachTooltip("No valid character assigned for this user — pose data will not be applied.");
                }
            }
        }, 5, width);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
    }
}
