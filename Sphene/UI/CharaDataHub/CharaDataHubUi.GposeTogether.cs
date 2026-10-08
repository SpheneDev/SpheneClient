using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services.CharaData.Models;
using Sphene.UI.Design;

namespace Sphene.UI.CharaDataHub;

internal sealed partial class CharaDataHubUi
{
    private string _joinLobbyId = string.Empty;
    private void DrawGposeTogether()
    {
        if (!_charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(5);
            SpheneUI.Text.GroupedCentered("BRIO IS MANDATORY FOR GPOSE TOGETHER.", ImGuiColors.DalamudRed);
            ImGuiHelpers.ScaledDummy(5);
        }

        if (!_uiSharedService.ApiController.IsConnected)
        {
            ImGuiHelpers.ScaledDummy(5);
            SpheneUI.Text.GroupedCentered("CANNOT USE GPOSE TOGETHER WHILE DISCONNECTED FROM THE SERVER.", ImGuiColors.DalamudRed);
            ImGuiHelpers.ScaledDummy(5);
        }

        SpheneUI.Text.Big("GPose Together");
        DrawHelpFoldout("GPose together is a way to do multiplayer GPose sessions and collaborations." + UiSharedService.DoubleNewLine
            + "GPose together requires Brio to function. Only Brio is also supported for the actual posing interactions. Attempting to pose using other tools will lead to conflicts and exploding characters." + UiSharedService.DoubleNewLine
            + "To use GPose together you either create or join a GPose Together Lobby. After you and other people have joined, make sure that everyone is on the same map. "
            + "It is not required for you to be on the same server, DC or instance. Users that are on the same map will be drawn as moving purple wisps in the overworld, so you can easily find each other." + UiSharedService.DoubleNewLine
            + "Once you are close to each other you can initiate GPose. You must either assign or spawn characters for each of the lobby users. Their own poses and positions to their character will be automatically applied." + Environment.NewLine
            + "Pose and location data during GPose are updated approximately every 10-20s.");

        using var disabled = ImRaii.Disabled(!_charaDataManager.BrioAvailable || !_uiSharedService.ApiController.IsConnected);

        SpheneUI.Layout.DistanceSeparator();
        SpheneUI.Text.Big("Lobby Controls");
        if (string.IsNullOrEmpty(_charaDataGposeTogetherManager.CurrentGPoseLobbyId))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Create New GPose Together Lobby"))
            {
                _charaDataGposeTogetherManager.CreateNewLobby();
            }
            ImGuiHelpers.ScaledDummy(5);
            SpheneUI.Layout.ScaledNextItemWidth(250);
            SpheneUI.Controls.InputTextWithHint("##lobbyId", "GPose Lobby Id", ref _joinLobbyId, 30);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowRight, "Join GPose Together Lobby"))
            {
                _charaDataGposeTogetherManager.JoinGPoseLobby(_joinLobbyId);
                _joinLobbyId = string.Empty;
            }
            if (!string.IsNullOrEmpty(_charaDataGposeTogetherManager.LastGPoseLobbyId)
                && SpheneUI.Icons.TextButton(FontAwesomeIcon.LongArrowAltRight, $"Rejoin Last Lobby {_charaDataGposeTogetherManager.LastGPoseLobbyId}"))
            {
                _charaDataGposeTogetherManager.JoinGPoseLobby(_charaDataGposeTogetherManager.LastGPoseLobbyId);
            }
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("GPose Lobby");
            ImGui.SameLine();
            SpheneUI.Text.ColoredWrapped(_charaDataGposeTogetherManager.CurrentGPoseLobbyId, ImGuiColors.ParsedGreen);
            ImGui.SameLine();
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Clipboard))
            {
                ImGui.SetClipboardText(_charaDataGposeTogetherManager.CurrentGPoseLobbyId);
            }
            SpheneUI.AttachTooltip("Copy Lobby ID to clipboard.");
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowLeft, "Leave GPose Lobby"))
                {
                    _charaDataGposeTogetherManager.LeaveGPoseLobby();
                }
            }
            SpheneUI.AttachTooltip("Leave the current GPose lobby." + SpheneUI.TooltipSeparator + "Hold CTRL and click to leave.");
        }
        SpheneUI.Layout.DistanceSeparator();
        using (ImRaii.Disabled(string.IsNullOrEmpty(_charaDataGposeTogetherManager.CurrentGPoseLobbyId)))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowUp, "Send Updated Character Data"))
            {
                _ = _charaDataGposeTogetherManager.PushCharacterDownloadDto();
            }
            SpheneUI.AttachTooltip("This will send your current appearance, pose and world data to all users in the lobby.");
            if (!_uiSharedService.IsInGpose)
            {
                ImGuiHelpers.ScaledDummy(5);
                SpheneUI.Text.GroupedCentered("Assigning users to characters is only available in GPose.", ImGuiColors.DalamudYellow, 300);
            }
            SpheneUI.Layout.DistanceSeparator();
            SpheneUI.Text.Plain("Users In Lobby");
            var gposeCharas = _dalamudUtilService.GetGposeCharactersFromObjectTable();
            var self = _dalamudUtilService.GetPlayerCharacter();
            gposeCharas = gposeCharas.Where(c => c != null && !string.Equals(c.Name.TextValue, self.Name.TextValue, StringComparison.Ordinal)).ToList();

            using (ImRaii.Child("charaChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGuiHelpers.ScaledDummy(3);

                if (!_charaDataGposeTogetherManager.UsersInLobby.Any() && !string.IsNullOrEmpty(_charaDataGposeTogetherManager.CurrentGPoseLobbyId))
                {
                    SpheneUI.Text.GroupedCentered("No other users in current GPose lobby", ImGuiColors.DalamudYellow);
                }
                else
                {
                    foreach (var user in _charaDataGposeTogetherManager.UsersInLobby)
                    {
                        DrawLobbyUser(user, gposeCharas);
                    }
                }
            }
        }
    }

    private void DrawLobbyUser(GposeLobbyUserData user,
        IEnumerable<Dalamud.Game.ClientState.Objects.Types.ICharacter?> gposeCharas)
    {
        using var id = ImRaii.PushId(user.UserData.UID);
        using var indent = ImRaii.PushIndent(5f);
        var sameMapAndServer = _charaDataGposeTogetherManager.IsOnSameMapAndServer(user);
        var width = ImGui.GetContentRegionAvail().X - 5;
        SpheneUI.Layout.Group(() =>
        {
            var availWidth = ImGui.GetContentRegionAvail().X;
            ImGui.AlignTextToFramePadding();
            var note = _serverConfigurationManager.GetNoteForUid(user.UserData.UID);
            var userText = note == null ? user.UserData.AliasOrUID : $"{note} ({user.UserData.AliasOrUID})";
            SpheneUI.Text.Colored(userText, ImGuiColors.ParsedGreen);

            var buttonsize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ArrowRight).X;
            var buttonsize2 = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Plus).X;
            ImGui.SameLine();
            ImGui.SetCursorPosX(availWidth - (buttonsize + buttonsize2 + ImGui.GetStyle().ItemSpacing.X));
            using (ImRaii.Disabled(!_uiSharedService.IsInGpose || user.CharaData == null || user.Address == nint.Zero))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowRight))
                {
                    _ = _charaDataGposeTogetherManager.ApplyCharaData(user);
                }
            }
            SpheneUI.AttachTooltip("Apply newly received character data to selected actor." + SpheneUI.TooltipSeparator + "Note: If the button is grayed out, the latest data has already been applied.");
            ImGui.SameLine();
            using (ImRaii.Disabled(!_uiSharedService.IsInGpose || user.CharaData == null || sameMapAndServer.SameEverything))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Plus))
                {
                    _ = _charaDataGposeTogetherManager.SpawnAndApplyData(user);
                }
            }
            SpheneUI.AttachTooltip("Spawn new actor, apply character data and and assign it to this user." + SpheneUI.TooltipSeparator + "Note: If the button is grayed out, " +
                "the user has not sent any character data or you are on the same map, server and instance. If the latter is the case, join a group with that user and assign the character to them.");


            using (ImRaii.Group())
            {
                SpheneUI.Text.Colored("Map Info", ImGuiColors.DalamudGrey);
                ImGui.SameLine();
                SpheneUI.Icons.Icon(FontAwesomeIcon.ExternalLinkSquareAlt, ImGuiColors.DalamudGrey);
            }
            SpheneUI.AttachTooltip(user.WorldDataDescriptor + SpheneUI.TooltipSeparator);

            ImGui.SameLine();
            SpheneUI.Icons.Icon(FontAwesomeIcon.Map, sameMapAndServer.SameMap ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && user.WorldData != null)
            {
                _dalamudUtilService.SetMarkerAndOpenMap(new(user.WorldData.Value.PositionX, user.WorldData.Value.PositionY, user.WorldData.Value.PositionZ), user.Map);
            }
            SpheneUI.AttachTooltip((sameMapAndServer.SameMap ? "You are on the same map." : "You are not on the same map.") + SpheneUI.TooltipSeparator
                + "Note: Click to open the users location on your map." + Environment.NewLine
                + "Note: For GPose synchronization to work properly, you must be on the same map.");

            ImGui.SameLine();
            SpheneUI.Icons.Icon(FontAwesomeIcon.Globe, sameMapAndServer.SameServer ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
            SpheneUI.AttachTooltip((sameMapAndServer.SameMap ? "You are on the same server." : "You are not on the same server.") + SpheneUI.TooltipSeparator
                + "Note: GPose synchronization is not dependent on the current server, but you will have to spawn a character for the other lobby users.");

            ImGui.SameLine();
            SpheneUI.Icons.Icon(FontAwesomeIcon.Running, sameMapAndServer.SameEverything ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
            SpheneUI.AttachTooltip(sameMapAndServer.SameEverything ? "You are in the same instanced area." : "You are not the same instanced area." + SpheneUI.TooltipSeparator +
                "Note: Users not in your instance, but on the same map, will be drawn as floating wisps." + Environment.NewLine
                + "Note: GPose synchronization is not dependent on the current instance, but you will have to spawn a character for the other lobby users.");

            using (ImRaii.Disabled(!_uiSharedService.IsInGpose))
            {
                SpheneUI.Layout.ScaledNextItemWidth(200);
                using (var combo = ImRaii.Combo("##character", string.IsNullOrEmpty(user.AssociatedCharaName) ? "No character assigned" : CharaName(user.AssociatedCharaName)))
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
                using (ImRaii.Disabled(user.Address == nint.Zero))
                {
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                    {
                        user.AssociatedCharaName = string.Empty;
                        user.Address = nint.Zero;
                    }
                }
                SpheneUI.AttachTooltip("Unassign Actor for this user");
                if (_uiSharedService.IsInGpose && user.Address == nint.Zero)
                {
                    ImGui.SameLine();
                    SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed);
                    SpheneUI.AttachTooltip("No valid character assigned for this user. Pose data will not be applied.");
                }
            }
        }, 5, width);
        ImGuiHelpers.ScaledDummy(5);
    }
}
