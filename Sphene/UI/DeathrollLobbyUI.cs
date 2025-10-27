using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.API.Dto;
using Sphene.UI;
using Sphene.WebAPI;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.UI.Components;

namespace Sphene.UI;

public class DeathrollLobbyUI : WindowMediatorSubscriberBase
{
    private readonly ILogger<DeathrollLobbyUI> _logger;
    private readonly DeathrollService _deathrollService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly ApiController _apiController;
    
    private List<DeathrollInvitationDto> _pendingInvitations = new();
    private List<DeathrollLobbyEntry> _activeLobbies = new();
    private string _statusMessage = "";
    private bool _showCreateGameSection = false;
    private DateTime _lastCurrentLobbyRefreshUtc = DateTime.MinValue;
    private readonly TimeSpan _currentLobbyRefreshInterval = TimeSpan.FromSeconds(3);
    private bool _selectCurrentLobbyTabNext = false;
    private bool _selectGameTabNext = false;
    private readonly DeathrollGameView _gameView;
    
    // New lobby creation fields
    private string _newLobbyName = "";
    private int _maxPlayers = 8;
    private bool _isPublicLobby = false;
    private string _invitePlayerName = "";
    private DeathrollGameMode _selectedGameMode = DeathrollGameMode.Standard;
    public DeathrollLobbyUI(
        ILogger<DeathrollLobbyUI> logger,
        SpheneMediator mediator,
        DeathrollService deathrollService,
        DalamudUtilService dalamudUtilService,
        ApiController apiController,
        PerformanceCollectorService performanceCollectorService) : base(logger, mediator, "Deathroll Lobby", performanceCollectorService)
    {
        _logger = logger;
        _deathrollService = deathrollService;
        _dalamudUtilService = dalamudUtilService;
        _apiController = apiController;
        
        Size = new Vector2(600, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
        _gameView = new DeathrollGameView(_deathrollService, _dalamudUtilService, _logger);
        
        // Subscribe to relevant messages
        Mediator.Subscribe<DeathrollInvitationReceivedMessage>(this, OnInvitationReceived);
        Mediator.Subscribe<DeathrollInvitationResponseMessage>(this, OnInvitationResponse);
        Mediator.Subscribe<DeathrollGameStateUpdateMessage>(this, OnGameStateUpdate);
        Mediator.Subscribe<DeathrollLobbyAnnouncementMessage>(this, OnLobbyAnnouncement);
        Mediator.Subscribe<DeathrollLobbyJoinRequestMessage>(this, OnLobbyJoinRequest);
        Mediator.Subscribe<DeathrollLobbyCanceledMessage>(this, OnLobbyCanceled);
        Mediator.Subscribe<OpenDeathrollLobbyMessage>(this, (msg) =>
        {
            IsOpen = true;
            if (msg.SelectCurrentLobbyTab)
            {
                _selectCurrentLobbyTabNext = true;
            }
            if (msg.SelectGameTab)
            {
                _selectGameTabNext = true;
            }
        });
        
        _logger.LogDebug("DeathrollLobbyUI initialized");
    }
    
    public override void OnOpen()
    {
        try
        {
            var currentGame = _deathrollService.GetCurrentGame();
            var currentPlayerName = GetCurrentPlayerName();
            var isHost = currentGame != null && string.Equals(currentGame.HostName, currentPlayerName, StringComparison.OrdinalIgnoreCase);
            _selectCurrentLobbyTabNext = isHost && _deathrollService.IsLobbyActive;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error determining Current Lobby selection on open");
        }
    }
    
    protected override void DrawInternal()
    {
        try
        {
            if (ImGui.BeginTabBar("DeathrollLobbyTabs"))
            {
                var currentGameForTabs = _deathrollService.GetCurrentGame();
                var currentPlayerNameForTabs = GetCurrentPlayerName();
                var isHostForTabs = currentGameForTabs != null
                    && string.Equals(currentGameForTabs.HostName, currentPlayerNameForTabs, StringComparison.OrdinalIgnoreCase);
                var isParticipantForTabs = currentGameForTabs != null
                    && currentGameForTabs.Players.Any(p => string.Equals(p.Name, currentPlayerNameForTabs, StringComparison.OrdinalIgnoreCase));
                var showCurrentLobby = currentGameForTabs != null
                   && _deathrollService.IsLobbyActive;
                var onlyShowGameTab = _deathrollService.IsGameActive;

                if (showCurrentLobby && !onlyShowGameTab)
                {
                    using var currentLobbyTab = ImRaii.TabItem("Current Lobby", _selectCurrentLobbyTabNext ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
                    if (currentLobbyTab)
                    {
                        if (_selectCurrentLobbyTabNext)
                            _selectCurrentLobbyTabNext = false;

                        // Throttled refresh: if host, broadcast lobby state to sync clients
                        if (DateTime.UtcNow - _lastCurrentLobbyRefreshUtc > _currentLobbyRefreshInterval)
                        {
                            _lastCurrentLobbyRefreshUtc = DateTime.UtcNow;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _deathrollService.BroadcastLobbyStateIfHostAsync();
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Error broadcasting lobby state on Current Lobby tab open");
                                }
                            });
                        }
                        DrawCurrentLobbyTab();
                    }
                }

                using (var gameTab = ImRaii.TabItem("Game", _selectGameTabNext ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                {
                    if (gameTab)
                    {
                        if (_selectGameTabNext)
                            _selectGameTabNext = false;
                        _gameView.Draw(embedded: true);
                    }
                }
                
                // Show Create Lobby first and hide it when a lobby is active
                if (!_deathrollService.IsLobbyActive && !onlyShowGameTab && ImGui.BeginTabItem("Create Lobby"))
                {
                    DrawCreateLobbyTab();
                    ImGui.EndTabItem();
                }

                if (!onlyShowGameTab && ImGui.BeginTabItem("Invitations"))
                {
                    DrawInvitationsTab();
                    ImGui.EndTabItem();
                }

                if (!onlyShowGameTab && ImGui.BeginTabItem("Active Games"))
                {
                    DrawActiveGamesTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            // Status message at bottom
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), _statusMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error drawing Deathroll lobby UI");
        }
    }
    
    private void DrawCurrentLobbyTab()
    {
        var currentGame = _deathrollService.GetCurrentGame();
        
        if (currentGame == null)
        {
            ImGui.Text("No active lobby or game.");
            ImGui.Separator();
            
            // Show quick create lobby option
            ImGui.Text("Quick Actions:");
            if (ImGui.Button("Create Public Lobby"))
            {
                _ = CreateLobbyAsync("Public Lobby", Services.LobbyVisibility.Public, 8);
            }
            ImGui.SameLine();
            if (ImGui.Button("Create Private Lobby"))
            {
                _ = CreateLobbyAsync("Private Lobby", Services.LobbyVisibility.Private, 8);
            }
            return;
        }
        
        // Display current lobby/game info
        ImGui.Text($"Lobby: {currentGame.LobbyName}");
        ImGui.Text($"Host: {currentGame.HostName}");
        ImGui.Text($"Status: {_deathrollService.GetGameStatus()}");
        ImGui.Text($"Players: {currentGame.Players.Count}/{currentGame.MaxPlayers}");
        ImGui.Text($"Mode: {currentGame.GameMode}");
        
        ImGui.Separator();
        
        // Player list as table
        using (var table = ImRaii.Table("DR_CurrentLobbyPlayers", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Player");
                ImGui.TableSetupColumn("Ready");
                ImGui.TableSetupColumn("Role");
                ImGui.TableHeadersRow();

                foreach (var player in currentGame.Players)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(player.Name);
                    ImGui.TableNextColumn();
                    var readyColor = player.IsReady ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f) : new Vector4(1.0f, 0.8f, 0.2f, 1.0f);
                    ImGui.TextColored(readyColor, player.IsReady ? "Ready" : "Not Ready");
                    ImGui.TableNextColumn();
                    ImGui.Text(player.IsHost ? "Host" : "Player");
                }
            }
        }
        
        ImGui.Separator();
        
        // Action buttons based on game state and player role
        var currentPlayerName = GetCurrentPlayerName();
        var isHost = string.Equals(currentGame.HostName, currentPlayerName, StringComparison.OrdinalIgnoreCase);
        var currentPlayer = currentGame.Players.FirstOrDefault(p => string.Equals(p.Name, currentPlayerName, StringComparison.OrdinalIgnoreCase));
        
        if (currentGame.State == Services.DeathrollGameState.LobbyCreated || currentGame.State == Services.DeathrollGameState.WaitingForPlayers || currentGame.State == Services.DeathrollGameState.ReadyToStart)
        {
            // Ready toggle for both host and non-host
            var isReady = currentPlayer?.IsReady ?? false;
            if (ImGui.Button(isReady ? "Not Ready" : "Ready"))
            {
                _ = SetPlayerReadyAsync(!isReady);
            }
            
            if (isHost)
            {
                ImGui.SameLine();
                if (ImGui.Button("Start Game"))
                {
                    _ = StartGameFromLobbyAsync();
                }
                
                // Open/Close is automatic; no manual toggle button

                ImGui.SameLine();
                var canCancel = currentGame.State == Services.DeathrollGameState.LobbyCreated || currentGame.State == Services.DeathrollGameState.WaitingForPlayers;
                if (canCancel && ImGui.Button("Cancel Lobby"))
                {
                    _ = Task.Run(async () =>
                    {
                        var success = await _deathrollService.CancelLobbyAsync();
                        _statusMessage = success ? "Lobby canceled." : "Failed to cancel lobby.";
                        if (success)
                            IsOpen = false;
                    });
                }
            }
            else
            {
                ImGui.SameLine();
                if (ImGui.Button("Leave Lobby"))
                {
                    _ = Task.Run(async () =>
                    {
                        var success = await _deathrollService.LeaveLobbyAsync();
                        _statusMessage = success ? "Left lobby." : "Failed to leave lobby.";
                        if (success)
                            IsOpen = false;
                    });
                }
            }
        }
        else if (currentGame.State == Services.DeathrollGameState.InProgress)
        {
            ImGui.Text("Game is in progress!");
            if (ImGui.Button("Go to Game Tab"))
            {
                _selectGameTabNext = true;
            }
        }
    }
    
    private void DrawInvitationsTab()
    {
        ImGui.Text("Pending Invitations:");
        ImGui.Separator();
        
        if (_pendingInvitations.Count == 0)
        {
            ImGui.Text("No pending invitations.");
            return;
        }
        
        for (int i = _pendingInvitations.Count - 1; i >= 0; i--)
        {
            var invitation = _pendingInvitations[i];
            
            // Check if invitation has expired
            if (DateTime.UtcNow > invitation.ExpiresAt)
            {
                _pendingInvitations.RemoveAt(i);
                continue;
            }
            
            ImGui.PushID($"invitation_{invitation.InvitationId}");
            
            // Invitation info
            ImGui.Text($"From: {invitation.Sender.AliasOrUID}");
            ImGui.SameLine();
            
            var timeLeft = invitation.ExpiresAt - DateTime.UtcNow;
            ImGui.Text($"(Expires in {timeLeft.Minutes}:{timeLeft.Seconds:D2})");
            
            // Action buttons
            ImGui.SameLine();
            if (ImGui.Button("Accept"))
            {
                AcceptInvitation(invitation);
                _pendingInvitations.RemoveAt(i);
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Decline"))
            {
                DeclineInvitation(invitation);
                _pendingInvitations.RemoveAt(i);
            }
            
            ImGui.PopID();
            ImGui.Separator();
        }
    }
    
    private void DrawActiveGamesTab()
    {
        ImGui.Text("Active Game Lobbies:");
        ImGui.Separator();
        
        if (_activeLobbies.Count == 0)
        {
            ImGui.Text("No active game lobbies.");
            return;
        }

        using var table = ImRaii.Table("DR_ActiveLobbies", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame);
        if (table)
        {
            ImGui.TableSetupColumn("Game ID");
            ImGui.TableSetupColumn("Host");
            ImGui.TableSetupColumn("Players");
            ImGui.TableSetupColumn("Status");
            ImGui.TableSetupColumn("Action");
            ImGui.TableHeadersRow();

            foreach (var lobby in _activeLobbies)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(lobby.GameId.Length >= 8 ? lobby.GameId[..8] + "..." : lobby.GameId);
                ImGui.TableNextColumn();
                ImGui.Text(lobby.Host);
                ImGui.TableNextColumn();
                ImGui.Text(string.Join(", ", lobby.Players));
                ImGui.TableNextColumn();
                ImGui.Text(lobby.Status);
                ImGui.TableNextColumn();

                if (lobby.Status is "Available" or "WaitingForPlayers")
                {
                    if (ImGui.Button($"Join##{lobby.GameId}"))
                    {
                        JoinGame(lobby);
                    }
                }
                else if (lobby.Status == "InProgress" && lobby.Players.Contains(GetCurrentPlayerName()))
                {
                    if (ImGui.Button($"Open##{lobby.GameId}"))
                    {
                        OpenGameWindow(lobby);
                    }
                }
                else
                {
                    ImGui.TextDisabled("-");
                }
            }
        }
    }
    
    private void DrawCreateLobbyTab()
     {
         ImGui.Text("Create New Deathroll Lobby:");
         ImGui.Separator();
         
         // Lobby name
         ImGui.InputText("Lobby Name", ref _newLobbyName, 100);
         if (string.IsNullOrEmpty(_newLobbyName))
         {
             ImGui.SameLine();
             ImGui.TextDisabled("(will use your name + 's Lobby')");
         }
         
         // Max players
         ImGui.SliderInt("Max Players", ref _maxPlayers, 2, 16);
         
         // Game Mode
         int modeIndex = _selectedGameMode == DeathrollGameMode.Standard ? 0 : 1;
         string[] modeItems = new[] { "Standard", "Tournament" };
         if (ImGui.Combo("Game Mode", ref modeIndex, modeItems, modeItems.Length))
         {
             _selectedGameMode = modeIndex == 0 ? DeathrollGameMode.Standard : DeathrollGameMode.Tournament;
         }
         
         // Visibility
         ImGui.Checkbox("Public Lobby (nearby players can join)", ref _isPublicLobby);
         if (!_isPublicLobby)
         {
             ImGui.TextDisabled("Private lobby (invite-only)");
         }
         
         ImGui.Separator();
         
         // Create button
         if (_deathrollService.IsLobbyActive || _deathrollService.IsGameActive)
         {
             ImGui.TextDisabled("You already have an active lobby or game");
         }
         else
         {
             if (ImGui.Button("Create Lobby"))
             {
                 var visibility = _isPublicLobby ? Services.LobbyVisibility.Public : Services.LobbyVisibility.Private;
                 _ = _deathrollService.CreateLobbyAsync(_newLobbyName, visibility, _maxPlayers, _selectedGameMode);
                 
                 // Reset form
                 _newLobbyName = "";
                 _maxPlayers = 8;
                 _isPublicLobby = false;
                 _selectedGameMode = DeathrollGameMode.Standard;
             }
         }
         
         ImGui.Separator();
         ImGui.Text("After creating a lobby:");
         ImGui.BulletText("Lobby opens immediately; nearby players can join if public");
         ImGui.BulletText("Invite specific players using the 'Current Lobby' tab");
         ImGui.BulletText("Start the game when you have enough players");
     }
    
    private void OnLobbyAnnouncement(DeathrollLobbyAnnouncementMessage message)
    {
        _logger.LogDebug("Received lobby announcement for game {gameId}: {message}", message.LobbyId, message.Message);
        
        // Add to active lobbies if not already present
        var existingLobby = _activeLobbies.FirstOrDefault(l => l.GameId == message.LobbyId);
        if (existingLobby == null)
        {
            _activeLobbies.Add(new DeathrollLobbyEntry
            {
                GameId = message.LobbyId,
                Host = message.HostName ?? "Unknown", // Use actual host name from message
                Players = new List<string>(),
                Status = "Available"
            });
            
            _statusMessage = $"New public lobby available: {message.Message}";
            _logger.LogInformation("Added new lobby to active lobbies list: {lobbyName}", message.Message);
            
            // Open lobby window to show the new lobby
            IsOpen = true;
        }
        else
        {
            _logger.LogDebug("Lobby {gameId} already exists in active lobbies", message.LobbyId);
        }
    }

    private void OnInvitationReceived(DeathrollInvitationReceivedMessage message)
    {
        _logger.LogDebug("Lobby received deathroll invitation from {sender}", message.Invitation.Sender.AliasOrUID);
        
        // Add to pending invitations if not already present
        if (!_pendingInvitations.Any(i => i.InvitationId == message.Invitation.InvitationId))
        {
            _pendingInvitations.Add(message.Invitation);
            _statusMessage = $"New invitation from {message.Invitation.Sender.AliasOrUID}!";
            
            // Open lobby window to show the invitation
            IsOpen = true;
        }
    }
    
    private void OnInvitationResponse(DeathrollInvitationResponseMessage message)
    {
        _logger.LogDebug("Lobby received invitation response from {responder}: {accepted}", 
            message.Response.Responder.AliasOrUID, message.Response.Accepted);
        
        if (message.Response.Accepted)
        {
            // Find the original invitation to get the GameId
            var originalInvitation = _pendingInvitations.FirstOrDefault(i => i.InvitationId == message.Response.InvitationId);
            if (originalInvitation != null)
            {
                // Create or update lobby entry using the original invitation's GameId
                var existingLobby = _activeLobbies.FirstOrDefault(l => l.GameId == originalInvitation.GameId);
                if (existingLobby == null)
                {
                    _activeLobbies.Add(new DeathrollLobbyEntry
                    {
                        GameId = originalInvitation.GameId,
                        Host = originalInvitation.Sender.AliasOrUID,
                        Players = new List<string> { originalInvitation.Sender.AliasOrUID, message.Response.Responder.AliasOrUID },
                        Status = "WaitingForPlayers"
                    });
                }
                else
                {
                    if (!existingLobby.Players.Contains(message.Response.Responder.AliasOrUID))
                    {
                        existingLobby.Players.Add(message.Response.Responder.AliasOrUID);
                    }
                }
            }
            _statusMessage = $"{message.Response.Responder.AliasOrUID} accepted invitation!";
            // Ensure lobby UI opens and switch to Current Lobby
            IsOpen = true;
            _selectCurrentLobbyTabNext = true;
        }
        else
        {
            _statusMessage = $"{message.Response.Responder.AliasOrUID} declined invitation";
        }
        
        // Remove from pending invitations
        _pendingInvitations.RemoveAll(i => i.InvitationId == message.Response.InvitationId);
    }
    
    private async void OnLobbyJoinRequest(DeathrollLobbyJoinRequestMessage message)
    {
        _logger.LogDebug("Received lobby join request for game {gameId} from player {player}", message.LobbyId, message.PlayerName);
        
        // Check if this is for our current lobby and we are the host
        var currentGame = _deathrollService.GetCurrentGame();
        if (currentGame != null && currentGame.GameId == message.LobbyId)
        {
            var currentPlayerName = await _dalamudUtilService.GetPlayerNameAsync();
            if (string.Equals(currentGame.HostName, currentPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Processing join request from {player} for our lobby {lobbyName}", message.PlayerName, currentGame.LobbyName);
                
                // Check if lobby has space
                if (currentGame.Players.Count >= currentGame.MaxPlayers)
                {
                    _logger.LogWarning("Lobby {gameId} is full, cannot accept join request from {player}", message.LobbyId, message.PlayerName);
                    _statusMessage = $"Lobby is full! Cannot accept {message.PlayerName}";
                    return;
                }
                
                // Check if player is already in lobby
                if (currentGame.Players.Any(p => string.Equals(p.Name, message.PlayerName, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("Player {player} is already in lobby {gameId}", message.PlayerName, message.LobbyId);
                    return;
                }
                
                // Auto-accept the join request by adding the player to the lobby
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var success = await _deathrollService.AcceptPlayerJoinAsync(message.PlayerName);
                        _statusMessage = success ? $"{message.PlayerName} joined the lobby!" : $"Failed to add {message.PlayerName} to lobby";
                        if (success)
                        {
                            _logger.LogInformation("Successfully added {player} to lobby {gameId}", message.PlayerName, message.LobbyId);
                        }
                        else
                        {
                            _logger.LogWarning("AcceptPlayerJoinAsync returned false for {player} in lobby {gameId}", message.PlayerName, message.LobbyId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to add player {player} to lobby {gameId}", message.PlayerName, message.LobbyId);
                        _statusMessage = $"Failed to add {message.PlayerName} to lobby";
                    }
                });
            }
            else
            {
                _logger.LogDebug("Received join request for our lobby but we are not the host");
            }
        }
        else
        {
            _logger.LogDebug("Received join request for lobby {gameId} but it's not our current lobby", message.LobbyId);
        }
    }
 
    private void OnGameStateUpdate(DeathrollGameStateUpdateMessage message)
    {
        _logger.LogDebug("Lobby received game state update for game {gameId}, state: {state}", 
            message.GameState.GameId, message.GameState.State);
        
        // Update or create lobby entry based on game state
        var existingLobby = _activeLobbies.FirstOrDefault(l => l.GameId == message.GameState.GameId);
        
        if (existingLobby != null)
        {
            existingLobby.Status = message.GameState.State.ToString();
            existingLobby.Players = message.GameState.Players?.Select(p => p.AliasOrUID).ToList() ?? new List<string>();
        }
        else if (message.GameState.State != Sphene.API.Dto.DeathrollGameState.Finished)
        {
            // Create new lobby entry for games we're not tracking yet
            _activeLobbies.Add(new DeathrollLobbyEntry
            {
                GameId = message.GameState.GameId,
                Host = message.GameState.Players?.FirstOrDefault()?.AliasOrUID ?? "Unknown",
                Players = message.GameState.Players?.Select(p => p.AliasOrUID).ToList() ?? new List<string>(),
                Status = message.GameState.State.ToString()
            });
        }
        
        // Remove finished games after a delay
        if (message.GameState.State == Sphene.API.Dto.DeathrollGameState.Finished)
        {
            _statusMessage = $"Game {message.GameState.GameId[..8]}... finished!";
            // Remove from active lobbies after showing the message
            _activeLobbies.RemoveAll(l => l.GameId == message.GameState.GameId);
        }
    }
 
     private void OnLobbyCanceled(DeathrollLobbyCanceledMessage message)
     {
         _logger.LogInformation("Received lobby canceled for {lobbyId}", message.LobbyId);
         // Remove lobby from active list
         _activeLobbies.RemoveAll(l => l.GameId == message.LobbyId);
         
         var currentGame = _deathrollService.GetCurrentGame();
         if (currentGame != null && currentGame.GameId == message.LobbyId)
         {
             // Reset local state for the current game/lobby
             _deathrollService.CancelCurrentGame();
             _statusMessage = "Lobby wurde geschlossen. Spiel beendet.";
             // Close lobby UI to reflect closure
             IsOpen = false;
         }
         else
         {
             _statusMessage = "Lobby wurde geschlossen.";
         }
     }
     
    private void AcceptInvitation(DeathrollInvitationDto invitation)
    {
        _logger.LogDebug("Accepting invitation from {sender}", invitation.Sender.AliasOrUID);
        _ = _deathrollService.AcceptInvitationAsync(invitation);
        _statusMessage = $"Accepted invitation from {invitation.Sender.AliasOrUID}";
        IsOpen = true; // Ensure lobby window opens so Current Lobby tab becomes visible
        _selectCurrentLobbyTabNext = true; // Select Current Lobby once available
    }
    
    private void DeclineInvitation(DeathrollInvitationDto invitation)
    {
        _logger.LogDebug("Declining invitation from {sender}", invitation.Sender.AliasOrUID);
        _ = _deathrollService.DeclineInvitationAsync(invitation);
        _statusMessage = $"Declined invitation from {invitation.Sender.AliasOrUID}";
    }
    
    private void JoinGame(DeathrollLobbyEntry lobby)
    {
        _logger.LogDebug("Joining game {gameId}", lobby.GameId);
        
        // Use DeathrollService to join the public lobby
        _ = Task.Run(async () =>
        {
            try
            {
                var success = await _deathrollService.JoinPublicLobbyAsync(lobby.GameId);
                if (success)
                {
                    _statusMessage = $"Successfully joined lobby hosted by {lobby.Host}!";
                    _logger.LogInformation("Successfully joined lobby {gameId}", lobby.GameId);
                }
                else
                {
                    _statusMessage = $"Failed to join lobby {lobby.GameId[..8]}...";
                    _logger.LogWarning("Failed to join lobby {gameId}", lobby.GameId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error joining lobby {gameId}", lobby.GameId);
                _statusMessage = "Error joining lobby.";
            }
        });
    }
    
    private void OpenGameWindow(DeathrollLobbyEntry lobby)
    {
        _logger.LogDebug("Opening game window for {gameId}", lobby.GameId);
        Mediator.Publish(new OpenDeathrollLobbyMessage(SelectCurrentLobbyTab: false, SelectGameTab: true));
    }
    
    // Helper methods for lobby operations
    private async Task CreateLobbyAsync(string lobbyName, Services.LobbyVisibility visibility, int maxPlayers)
    {
        try
        {
            var success = await _deathrollService.CreateLobbyAsync(lobbyName, visibility, maxPlayers);
            if (success)
            {
                _statusMessage = $"Lobby '{lobbyName}' created successfully!";
                _selectCurrentLobbyTabNext = true; // Switch to Current Lobby after creation
            }
            else
            {
                _statusMessage = "Failed to create lobby.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating lobby");
            _statusMessage = "Error creating lobby.";
        }
    }
    
    private async Task StartGameFromLobbyAsync()
    {
        try
        {
            var currentGame = _deathrollService.GetCurrentGame();
            if (currentGame != null)
            {
                var success = await _apiController.DeathrollStartGameFromLobby(currentGame.GameId);
                if (success)
                {
                    var localStarted = await _deathrollService.StartGameFromLobbyAsync();
                    _statusMessage = localStarted ? "Game started!" : "Game started remotely, but local state failed.";
                    Mediator.Publish(new OpenDeathrollLobbyMessage(SelectCurrentLobbyTab: false, SelectGameTab: true));
                }
                else
                {
                    _statusMessage = "Failed to start game.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting game from lobby");
            _statusMessage = "Error starting game.";
        }
    }
    
    private async Task CancelLobbyAsync()
    {
        try
        {
            var success = await _deathrollService.CancelLobbyAsync();
            if (success)
            {
                _statusMessage = "Lobby canceled.";
            }
            else
            {
                _statusMessage = "Failed to cancel lobby.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error canceling lobby");
            _statusMessage = "Error canceling lobby.";
        }
    }
    
    private async Task OpenCloseLobbyAsync(bool isOpen)
    {
        try
        {
            var success = await _deathrollService.OpenCloseLobbyAsync(isOpen);
            if (success)
            {
                _statusMessage = isOpen ? "Lobby opened." : "Lobby closed.";
            }
            else
            {
                _statusMessage = $"Failed to {(isOpen ? "open" : "close")} lobby.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening/closing lobby");
            _statusMessage = "Error updating lobby status.";
        }
    }
    
    private async Task SetPlayerReadyAsync(bool isReady)
    {
        try
        {
            var currentGame = _deathrollService.GetCurrentGame();
            var currentPlayerName = GetCurrentPlayerName();
            
            if (currentGame != null && !string.IsNullOrEmpty(currentPlayerName))
            {
                var success = await _apiController.DeathrollSetPlayerReady(currentGame.GameId, currentPlayerName, isReady);
                if (success)
                {
                    _statusMessage = isReady ? "You are ready!" : "You are not ready.";
                    
                    // Update local player state
                    var player = currentGame.Players.FirstOrDefault(p => string.Equals(p.Name, currentPlayerName, StringComparison.OrdinalIgnoreCase));
                    if (player != null)
                    {
                        player.IsReady = isReady;
                    }
                }
                else
                {
                    _statusMessage = "Failed to update ready status.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting player ready status");
            _statusMessage = "Error updating ready status.";
        }
    }
    
    private async void CreateNewGame()
    {
        _logger.LogDebug("Creating new game from lobby");
        
        try
        {
            // Get nearby players
            var nearbyPlayers = await GetNearbyPlayersAsync();
            
            if (nearbyPlayers.Count < 2)
            {
                _statusMessage = "Need at least 2 players nearby to start a game!";
                return;
            }
            
            // Start the game using DeathrollService
            var success = await _deathrollService.StartNewGameAsync(nearbyPlayers);
            
            if (success)
            {
                _statusMessage = $"Game started successfully! Invitations sent to {nearbyPlayers.Count - 1} nearby players.";
                
                // Create a lobby entry for the new game
                var currentPlayerName = GetCurrentPlayerName();
                var gameId = Guid.NewGuid().ToString(); // Generate a temporary game ID
                
                _activeLobbies.Add(new DeathrollLobbyEntry
                {
                    GameId = gameId,
                    Host = currentPlayerName,
                    Players = new List<string> { currentPlayerName },
                    Status = "WaitingForPlayers"
                });
            }
            else
            {
                _statusMessage = "Failed to start game. Check if another game is already active.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating new game from lobby");
            _statusMessage = "Error starting game.";
        }
    }
    
    private async Task<List<string>> GetNearbyPlayersAsync()
    {
        var nearbyPlayers = new List<string>();
        
        try
        {
            var playerCharacter = await _dalamudUtilService.RunOnFrameworkThread(() => 
                _dalamudUtilService.GetPlayerCharacter());
            
            if (playerCharacter == null)
                return nearbyPlayers;

            var playerPosition = playerCharacter.Position;
            var currentPlayerName = playerCharacter.Name?.TextValue ?? "Unknown";
            
            // Add current player
            nearbyPlayers.Add(currentPlayerName);
            
            // Get party members first
            var partyMembers = await _dalamudUtilService.RunOnFrameworkThread(() => 
                _dalamudUtilService.GetPartyMemberNames());
            
            nearbyPlayers.AddRange(partyMembers);
            
            // Get nearby characters from object table
            var nearbyCharacters = await _dalamudUtilService.RunOnFrameworkThread(() => 
                _dalamudUtilService.GetAllPlayersFromObjectTable());
            
            const float maxDistance = 30.0f; // Maximum distance for nearby detection
            
            foreach (var character in nearbyCharacters)
            {
                if (character?.Name?.TextValue == null)
                    continue;
                    
                var characterName = character.Name.TextValue;
                
                // Skip if already in list
                if (nearbyPlayers.Contains(characterName, StringComparer.OrdinalIgnoreCase))
                    continue;
                
                // Check distance
                var distance = Vector3.Distance(playerPosition, character.Position);
                if (distance <= maxDistance)
                {
                    nearbyPlayers.Add(characterName);
                }
            }
            
            _logger.LogDebug("Found {count} nearby players", nearbyPlayers.Count);
            return nearbyPlayers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting nearby players");
            return nearbyPlayers;
        }
    }
    
    private string GetCurrentPlayerName()
    {
        try
        {
            return _dalamudUtilService.GetPlayerName();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current player name");
            return "Unknown";
        }
    }
}

// Helper classes
public class DeathrollLobbyEntry
{
    public string GameId { get; set; } = "";
    public string Host { get; set; } = "";
    public List<string> Players { get; set; } = new();
    public string Status { get; set; } = "";
}

// New message for opening the main game window
public record OpenDeathrollGameMessage(string? GameId) : MessageBase;

// Message to open the Deathroll Lobby UI, optionally selecting tabs
public record OpenDeathrollLobbyMessage(bool SelectCurrentLobbyTab = false, bool SelectGameTab = false) : MessageBase;