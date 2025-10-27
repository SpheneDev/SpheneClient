using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto;
using Sphene.API.Data;

namespace Sphene.UI.Components;

public class TournamentBracketView
{
    private readonly ILogger? _logger;

    public TournamentBracketView(ILogger? logger = null)
    {
        _logger = logger;
    }

    public void Draw(DeathrollTournamentStateDto state)
    {
        try
        {
            if (state == null)
            {
                ImGui.Text("No tournament data available.");
                return;
            }

            // Header
            ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Tournament Bracket");
            var participants = state.Participants ?? new List<UserData>();
            var matches = state.Matches ?? new List<DeathrollTournamentMatchDto>();
            ImGui.Text($"Stage: {state.Stage}  |  Round: {state.CurrentRound}  |  Participants: {participants.Count}");
            if (state.Champion != null)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), $"Champion: {state.Champion.AliasOrUID}");
            }
            else if (state.Stage == DeathrollTournamentStage.Completed)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("Champion: TBD");
            }

            ImGui.Separator();

            if (participants.Count == 0)
            {
                ImGui.TextDisabled("No participants yet.");
            }

            var safeMatches = matches.Where(m => m != null).ToList();
            var rounds = safeMatches
                .GroupBy(m => m.Round)
                .OrderBy(g => g.Key)
                .ToList();

            if (rounds.Count == 0)
            {
                ImGui.Text("No matches yet.");
                return;
            }

            using var table = ImRaii.Table("DR_TournamentBracket", rounds.Count, ImGuiTableFlags.SizingStretchSame);
            if (!table)
                return;

            foreach (var round in rounds)
            {
                ImGui.TableNextColumn();
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), $"Round {round.Key}");

                using var child = ImRaii.Child($"DR_Round_{round.Key}", new Vector2(-1, 0), true);
                if (!child)
                    continue;

                foreach (var match in round)
                {
                    DrawMatch(match);
                    ImGui.Separator();
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error drawing tournament bracket");
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "Error displaying Tournament Bracket");
        }
    }

    private void DrawMatch(DeathrollTournamentMatchDto match)
    {
        if (match == null)
        {
            ImGui.TextDisabled("Match data unavailable");
            return;
        }

        var a = match.PlayerA?.AliasOrUID ?? "TBD";
        var b = match.PlayerB?.AliasOrUID ?? "TBD";
        var winner = match.Winner?.AliasOrUID;
        var loser = match.Loser?.AliasOrUID;

        var idText = string.IsNullOrEmpty(match.MatchId)
            ? "unknown"
            : (match.MatchId.Length > 6 ? match.MatchId.Substring(0, 6) : match.MatchId);
        ImGui.TextDisabled($"Match {idText}");

        var aColor = winner != null && string.Equals(winner, a, StringComparison.OrdinalIgnoreCase)
            ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f)
            : (loser != null && string.Equals(loser, a, StringComparison.OrdinalIgnoreCase)
                ? new Vector4(1.0f, 0.4f, 0.4f, 1.0f)
                : new Vector4(0.9f, 0.9f, 0.9f, 1.0f));

        var bColor = winner != null && string.Equals(winner, b, StringComparison.OrdinalIgnoreCase)
            ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f)
            : (loser != null && string.Equals(loser, b, StringComparison.OrdinalIgnoreCase)
                ? new Vector4(1.0f, 0.4f, 0.4f, 1.0f)
                : new Vector4(0.9f, 0.9f, 0.9f, 1.0f));

        ImGui.TextColored(aColor, a);
        ImGui.Text("vs");
        ImGui.TextColored(bColor, b);

        if (match.IsCompleted && winner != null)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.9f, 0.6f, 1.0f), $"Winner: {winner}");
        }
    }
}