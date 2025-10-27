using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.Services;

namespace Sphene.UI.Components;

public class DeathrollGameView
{
    private readonly DeathrollService _deathrollService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly ILogger? _logger;

    public DeathrollGameView(DeathrollService deathrollService, DalamudUtilService dalamudUtilService, ILogger? logger = null)
    {
        _deathrollService = deathrollService;
        _dalamudUtilService = dalamudUtilService;
        _logger = logger;
    }

    public void Draw(bool embedded = false)
    {
        try
        {
            DrawGameStatus();
            ImGui.Separator();

            var game = _deathrollService.CurrentGame;
            if (game == null)
            {
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1.0f), embedded
                    ? "No active game. Create or join one in the other tabs."
                    : "No active game. Use the Deathroll Lobby to create or join games.");
                return;
            }

            DrawActiveGameSection();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error drawing embedded Deathroll game view");
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "Error displaying Deathroll Game");
        }
    }

    private void DrawGameStatus()
    {
        ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Deathroll Game");
        var status = _deathrollService.GetGameStatus();

        var game = _deathrollService.CurrentGame;
        if (game != null)
        {
            var playersInfo = $"Players: {game.Players.Count}/{game.MaxPlayers}";
            var modeInfo = $"Mode: {game.GameMode}";
            var lobbyInfo = !string.IsNullOrEmpty(game.LobbyName) ? $"Lobby: {game.LobbyName}" : "";
            var turnInfo = game.State == Sphene.Services.DeathrollGameState.InProgress ? $" | Turn: {game.CurrentPlayerName}" : string.Empty;

            var durationText = string.Empty;
            if (game.State == Sphene.Services.DeathrollGameState.InProgress && game.GameStartTime.HasValue)
                durationText = $" | Time: {(DateTime.UtcNow - game.GameStartTime.Value):mm\\:ss}";
            else if (game.State == Sphene.Services.DeathrollGameState.Finished && game.GameStartTime.HasValue && game.GameEndTime.HasValue)
                durationText = $" | Time: {(game.GameEndTime.Value - game.GameStartTime.Value):mm\\:ss}";

            var info = string.Join("  |  ", new[] { lobbyInfo, modeInfo, playersInfo }.Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(info))
                ImGui.Text(info + turnInfo + durationText);

            ImGui.Text($"Status: {status}");
        }
        else
        {
            ImGui.Text($"Status: {status}");
        }
    }

    private void DrawActiveGameSection()
    {
        var game = _deathrollService.CurrentGame;
        if (game == null) return;

        ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "Active Deathroll Game");

        switch (game.State)
        {
            case Sphene.Services.DeathrollGameState.WaitingForPlayers:
                DrawWaitingForPlayersSection();
                break;
            case Sphene.Services.DeathrollGameState.InProgress:
                DrawInProgressSection();
                break;
            case Sphene.Services.DeathrollGameState.Finished:
                DrawFinishedSection();
                break;
        }
    }

    private void DrawWaitingForPlayersSection()
    {
        var game = _deathrollService.CurrentGame!;
        ImGui.Text("Waiting for players to accept invitations...");
        ImGui.Text($"Players joined: {game.Players.Count(p => p.HasAccepted)}/{game.Players.Count}");

        if (ImGui.CollapsingHeader("Player List"))
        {
            using var table = ImRaii.Table("DR_PlayerTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame);
            if (table)
            {
                ImGui.TableSetupColumn("Player Name");
                ImGui.TableSetupColumn("Status");
                ImGui.TableSetupColumn("Role");
                ImGui.TableHeadersRow();

                foreach (var player in game.Players)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(player.Name);

                    ImGui.TableNextColumn();
                    var statusColor = player.HasAccepted ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f) : new Vector4(1.0f, 0.8f, 0.2f, 1.0f);
                    var statusText = player.HasAccepted ? "Accepted" : "Pending";
                    ImGui.TextColored(statusColor, statusText);

                    ImGui.TableNextColumn();
                    ImGui.Text(player.IsHost ? "Host" : "Player");
                }
            }
        }
    }

    private void DrawInProgressSection()
    {
        var game = _deathrollService.CurrentGame!;
        ImGui.Text($"Current Turn: {game.CurrentPlayerName}");
        ImGui.Text($"Roll Range: 1-{game.CurrentRollMax}");

        if (game.LastRoll > 0)
        {
            ImGui.Text($"Last Roll: {game.LastRoll}");
        }

        string timerText = game.State == Sphene.Services.DeathrollGameState.Finished
            ? (game.GameStartTime.HasValue && game.GameEndTime.HasValue
                ? $"Game Duration: {(game.GameEndTime.Value - game.GameStartTime.Value):mm\\:ss}"
                : "Game Duration: 00:00")
            : (game.GameStartTime.HasValue
                ? $"Game Duration: {(DateTime.UtcNow - game.GameStartTime.Value):mm\\:ss}"
                : "Game Duration: 00:00");
        ImGui.Text(timerText);

        ImGui.Separator();
        var currentPlayerName = _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? string.Empty;
        var isCurrentPlayerTurn = string.Equals(game.CurrentPlayerName, currentPlayerName, StringComparison.OrdinalIgnoreCase);
        using (ImRaii.Disabled(!isCurrentPlayerTurn))
        {
            var buttonText = isCurrentPlayerTurn ? $"Roll 1-{game.CurrentRollMax}" : $"Waiting for {game.CurrentPlayerName} to roll";
            if (ImGui.Button(buttonText, new Vector2(-1, 30)))
            {
                var firstRoll = game.CurrentRollMax == 1000 && game.RollHistory.Count == 0;
                _deathrollService.ExecuteRoll(game.CurrentRollMax, firstRoll);
                _logger?.LogDebug("Triggered roll via service. FirstRoll={firstRoll}, Max={max}", firstRoll, game.CurrentRollMax);
            }
        }

        if (ImGui.CollapsingHeader("Roll History"))
        {
            using var child = ImRaii.Child("DR_RollHistory", new Vector2(-1, 100), true);
            if (child)
            {
                var rollsToShow = game.RollHistory.TakeLast(10).ToList();
                foreach (var roll in rollsToShow)
                {
                    ImGui.Text(roll);
                }

                if (rollsToShow.Count > 0)
                {
                    var scrollY = ImGui.GetScrollY();
                    var scrollMaxY = ImGui.GetScrollMaxY();
                    if (scrollMaxY <= 0 || scrollY >= scrollMaxY - 20)
                    {
                        ImGui.SetScrollHereY(1.0f);
                    }
                }
            }
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1.0f), "Use /random in chat to roll!");
    }

    private void DrawFinishedSection()
    {
        var game = _deathrollService.CurrentGame!;
        ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), $"Winner: {game.Winner}");
        ImGui.TextColored(new Vector4(1.0f, 0.2f, 0.2f, 1.0f), $"Loser: {game.Loser}");

        var duration = game.GameStartTime.HasValue && game.GameEndTime.HasValue
            ? game.GameEndTime.Value - game.GameStartTime.Value
            : TimeSpan.Zero;
        ImGui.Text($"Game Duration: {duration:mm\\:ss}");
        ImGui.Text($"Total Rolls: {game.RollHistory.Count}");

        if (ImGui.CollapsingHeader("Game Roll History"))
        {
            using var child = ImRaii.Child("DR_FinishedRollHistory", new Vector2(-1, 150), true);
            if (child)
            {
                foreach (var roll in game.RollHistory)
                {
                    ImGui.Text(roll);
                }
            }
        }
    }
}