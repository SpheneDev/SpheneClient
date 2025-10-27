using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;
using Dalamud.Game.Text.SeStringHandling;
using Sphene.Services.Events;
using Sphene.WebAPI.SignalR;
using Sphene.WebAPI.SignalR.Utils;
using Sphene.SpheneConfiguration.Models;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using Sphene.API.Dto;
using Sphene.API.Data;
using Sphene.UI;
using Sphene.WebAPI;
using Sphene.PlayerData.Pairs;
using Sphene.UI;

namespace Sphene.Services;

public enum DeathrollGameState
{
    Inactive,
    LobbyCreated,        // New: Lobby exists but waiting for players
    WaitingForPlayers,   // Lobby is open and accepting players
    ReadyToStart,        // Enough players joined, waiting for host to start
    InProgress,          // Game is actively running
    Finished
}

public enum LobbyVisibility
{
    Private,    // Invite-only
    Public      // Open for nearby players
}

public class DeathrollPlayer
{
    public string Name { get; set; } = string.Empty;
    public bool IsHost { get; set; }
    public bool HasAccepted { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public bool IsReady { get; set; } = false;  // New: Player ready status
}

public class DeathrollGame
{
    public string GameId { get; set; } = string.Empty;
    public DeathrollGameState State { get; set; } = DeathrollGameState.Inactive;
    public LobbyVisibility Visibility { get; set; } = LobbyVisibility.Private;  // New: Lobby visibility
    public DeathrollGameMode GameMode { get; set; } = DeathrollGameMode.Standard; // New: Game mode
    public List<DeathrollPlayer> Players { get; set; } = new();
    public string CurrentPlayerName { get; set; } = string.Empty;
    public int CurrentRollMax { get; set; } = 1000;
    public int LastRoll { get; set; }
    public string? Winner { get; set; }
    public string? Loser { get; set; }
    public DateTime? LobbyCreatedTime { get; set; }  // New: When lobby was created
    public DateTime? GameStartTime { get; set; }
    public DateTime? GameEndTime { get; set; }
    public DateTime LastRollTime { get; set; }
    public List<string> RollHistory { get; set; } = new();
    public string HostName { get; set; } = string.Empty;  // New: Explicit host tracking
    public int MaxPlayers { get; set; } = 8;  // New: Maximum players allowed
    public string LobbyName { get; set; } = string.Empty;  // New: Custom lobby name
}

public class DeathrollService : DisposableMediatorSubscriberBase
{
    private readonly ILogger<DeathrollService> _logger;
    private readonly SpheneMediator _mediator;
    private readonly IChatSender _chatSender;
    private readonly ICommandManager _commandManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly NotificationService _notificationService;
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    
    private DeathrollGame? _currentGame;
    private DeathrollTournamentStateDto? _tournamentState;
    private string? _autoOpenAttemptForGameId; // Prevent repeated auto-open attempts
    private readonly Dictionary<string, DateTime> _lastInviteTime = new();
    private readonly TimeSpan _inviteCooldown = TimeSpan.FromSeconds(30);

    public DeathrollService(
        ILogger<DeathrollService> logger,
        SpheneMediator mediator,
        IChatSender chatSender,
        ICommandManager commandManager,
        DalamudUtilService dalamudUtilService,
        ApiController apiController,
        PairManager pairManager) : base(logger, mediator)
    {
        _logger = logger;
        _mediator = mediator;
        _chatSender = chatSender;
        _commandManager = commandManager;
        _dalamudUtilService = dalamudUtilService;
        _apiController = apiController;
        _pairManager = pairManager;
        
        // Subscribe to mediator messages instead of API callbacks
        // Note: DeathrollInvitationReceivedMessage is handled by DeathrollInvitationUI, not needed here
        Mediator.Subscribe<DeathrollInvitationResponseMessage>(this, msg => OnInvitationResponse(msg.Response));
        Mediator.Subscribe<DeathrollGameStateUpdateMessage>(this, msg => OnGameStateUpdate(msg.GameState));
        Mediator.Subscribe<DeathrollInvitePairMessage>(this, async msg => await InvitePlayerToLobbyAsync(msg.PlayerName));
        Mediator.Subscribe<DeathrollPlayerReadyMessage>(this, OnPlayerReadyChanged);
        Mediator.Subscribe<DeathrollLobbyLeaveMessage>(this, OnLobbyLeave);
        Mediator.Subscribe<DeathrollTournamentStateUpdateMessage>(this, msg =>
        {
            _tournamentState = msg.TournamentState;
            _logger.LogDebug("Updated local tournament state: round {round}, matches {count}", _tournamentState.CurrentRound, _tournamentState.Matches?.Count ?? 0);
        });
 
        _logger.LogDebug("DeathrollService initialized with API support");
    }

    public DeathrollGame? GetCurrentGame() => _currentGame;

    public bool IsGameActive => _currentGame?.State == DeathrollGameState.InProgress;
    public bool IsWaitingForPlayers => _currentGame?.State == DeathrollGameState.WaitingForPlayers;
    public bool IsLobbyActive => _currentGame?.State == DeathrollGameState.LobbyCreated || 
                                _currentGame?.State == DeathrollGameState.WaitingForPlayers || 
                                _currentGame?.State == DeathrollGameState.ReadyToStart;
    public DeathrollGame? CurrentGame => _currentGame;
    public string ApiControllerUid => _apiController.UID;

    // New method to get game status string
    public string GetGameStatus()
    {
        if (_currentGame == null)
            return "No active game";
            
        return _currentGame.State switch
        {
            DeathrollGameState.LobbyCreated => $"Lobby '{_currentGame.LobbyName}' created",
            DeathrollGameState.WaitingForPlayers => $"Waiting for players ({_currentGame.Players.Count}/{_currentGame.MaxPlayers})",
            DeathrollGameState.ReadyToStart => "Ready to start",
            DeathrollGameState.InProgress => $"Game in progress - Current roll: {_currentGame.LastRoll}",
            DeathrollGameState.Finished => $"Game completed - Winner: {_currentGame.Winner}",
            _ => "Unknown status"
        };
    }

    // Helper to execute a /random roll via chat
    public void ExecuteRoll(int max, bool firstRoll)
    {
        try
        {
            var command = firstRoll ? "random" : $"random {max}";
            _chatSender.SendCommand(command);
            _logger.LogDebug("Sent roll command: {command}", command);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send roll command");
        }
    }

    // New method to cancel current game
    public void CancelCurrentGame()
    {
        if (_currentGame == null)
            return;
            
        _logger.LogDebug("Canceling current game: {gameId}", _currentGame.GameId);
        
        // If it's a lobby, try to cancel via API, then clear local state
        if (IsLobbyActive)
        {
            _ = CancelLobbyAsync();
            _currentGame = null;
            _autoOpenAttemptForGameId = null;
            _mediator.Publish(new NotificationMessage("Deathroll", "Lobby canceled", NotificationType.Info));
        }
        else
        {
            // For active games, just reset locally
            _currentGame = null;
            _autoOpenAttemptForGameId = null;
            _mediator.Publish(new NotificationMessage("Deathroll", "Game canceled", NotificationType.Info));
        }
    }

    // New method to create a lobby without starting the game
    public async Task<bool> CreateLobbyAsync(string lobbyName, LobbyVisibility visibility, int maxPlayers = 8, DeathrollGameMode gameMode = DeathrollGameMode.Standard)
    {
        if (IsLobbyActive || IsGameActive)
        {
            _mediator.Publish(new NotificationMessage("Deathroll", "A Deathroll lobby or game is already active!", NotificationType.Error));
            return false;
        }

        var playerName = await _dalamudUtilService.RunOnFrameworkThread(() => 
            _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? "Unknown");

        if (string.IsNullOrEmpty(playerName))
        {
            _mediator.Publish(new NotificationMessage("Deathroll", "Could not determine player name!", NotificationType.Error));
            return false;
        }

        _currentGame = new DeathrollGame
        {
            GameId = Guid.NewGuid().ToString(),
            State = DeathrollGameState.LobbyCreated,
            Visibility = visibility,
            LobbyName = string.IsNullOrEmpty(lobbyName) ? $"{playerName}'s Lobby" : lobbyName,
            HostName = playerName,
            MaxPlayers = maxPlayers,
            LobbyCreatedTime = DateTime.UtcNow,
            GameMode = gameMode
        };

        // Add host player
        _currentGame.Players.Add(new DeathrollPlayer
        {
            Name = playerName,
            IsHost = true,
            HasAccepted = true,
            IsReady = true
        });

        _logger.LogDebug("Created new Deathroll lobby: {lobbyName} by {host}", _currentGame.LobbyName, playerName);
        _mediator.Publish(new NotificationMessage("Deathroll", $"Lobby '{_currentGame.LobbyName}' created successfully!", NotificationType.Info));

        // Publish a local game state update so UI and context menu react immediately
        var lobbyStateDto = new DeathrollGameStateDto
        {
            GameId = _currentGame.GameId,
            State = Sphene.API.Dto.DeathrollGameState.LobbyCreated,
            GameMode = _currentGame.GameMode,
            Players = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList(),
            ReadyPlayers = _currentGame.Players.Where(p => p.IsReady).Select(p => new UserData(string.Empty, p.Name)).ToList(),
            LobbyName = _currentGame.LobbyName,
            Host = new UserData(_apiController.UID, _currentGame.HostName),
            MaxPlayers = _currentGame.MaxPlayers,
            Visibility = _currentGame.Visibility == LobbyVisibility.Public ? Sphene.API.Dto.LobbyVisibility.Public : Sphene.API.Dto.LobbyVisibility.Private,
            LobbyCreatedTime = _currentGame.LobbyCreatedTime
        };
        _mediator.Publish(new DeathrollGameStateUpdateMessage(lobbyStateDto));

        // Send lobby creation to server with selected game mode
        var createLobbyDto = new DeathrollCreateLobbyDto
        {
            LobbyName = _currentGame.LobbyName,
            Host = new UserData(_apiController.UID, playerName),
            MaxPlayers = maxPlayers,
            Visibility = visibility == Sphene.Services.LobbyVisibility.Public ? Sphene.API.Dto.LobbyVisibility.Public : Sphene.API.Dto.LobbyVisibility.Private,
            GameMode = gameMode,
            CreatedAt = _currentGame.LobbyCreatedTime ?? DateTime.UtcNow
        };
        var sent = await _apiController.DeathrollCreateLobby(createLobbyDto).ConfigureAwait(false);
        if (!sent)
        {
            _logger.LogDebug("Failed to send lobby creation to server for lobby {lobbyName}", _currentGame.LobbyName);
        }
        // Auto-open will be triggered when we receive the server GameId via game state update

        return true;
    }

    // New method to open lobby for players to join
    public async Task<bool> OpenLobbyAsync()
    {
        if (_currentGame?.State != DeathrollGameState.LobbyCreated)
        {
            return false;
        }

        _currentGame.State = DeathrollGameState.WaitingForPlayers;
        
        _logger.LogDebug("Opened lobby for players to join: {lobbyName}", _currentGame.LobbyName);
        _mediator.Publish(new NotificationMessage("Deathroll", "Lobby is now open for players to join!", NotificationType.Info));
        
        // Broadcast current lobby state (including ReadyPlayers) so potential joiners see accurate readiness
        try
        {
            var lobbyStateDto = new DeathrollGameStateDto
            {
                GameId = _currentGame.GameId,
                State = Sphene.API.Dto.DeathrollGameState.WaitingForPlayers,
                GameMode = _currentGame.GameMode,
                Players = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList(),
                ReadyPlayers = _currentGame.Players.Where(p => p.IsReady).Select(p => new UserData(string.Empty, p.Name)).ToList(),
                LobbyName = _currentGame.LobbyName,
                Host = new UserData(_apiController.UID, _currentGame.HostName),
                MaxPlayers = _currentGame.MaxPlayers,
                Visibility = _currentGame.Visibility == LobbyVisibility.Public ? Sphene.API.Dto.LobbyVisibility.Public : Sphene.API.Dto.LobbyVisibility.Private,
                LobbyCreatedTime = _currentGame.LobbyCreatedTime
            };
            var sentState = await _apiController.DeathrollUpdateLobbyState(lobbyStateDto);
            if (!sentState)
            {
                _logger.LogDebug("Failed to broadcast lobby state on open for lobby {lobbyName}", _currentGame.LobbyName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error broadcasting lobby state on open for lobby {lobbyName}", _currentGame.LobbyName);
        }

        // Announce to nearby players if public
        if (_currentGame.Visibility == LobbyVisibility.Public)
        {
            await AnnounceLobbyToNearbyPlayersAsync();
        }
        
        return true;
    }

    // New method to invite specific players to the lobby
    public async Task<bool> InvitePlayerToLobbyAsync(string playerName)
    {
        if (!IsLobbyActive)
        {
            return false;
        }

        var currentPlayerName = await _dalamudUtilService.RunOnFrameworkThread(() => 
            _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? "Unknown");

        // Only host can invite players
        if (!string.Equals(_currentGame.HostName, currentPlayerName, StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new NotificationMessage("Deathroll", "Only the host can invite players!", NotificationType.Error));
            return false;
        }

        // Check if player is already in the lobby
        if (_currentGame.Players.Any(p => string.Equals(p.Name, playerName, StringComparison.OrdinalIgnoreCase)))
        {
            _mediator.Publish(new NotificationMessage("Deathroll", $"{playerName} is already in the lobby!", NotificationType.Warning));
            return false;
        }

        // Check if lobby is full
        if (_currentGame.Players.Count >= _currentGame.MaxPlayers)
        {
            _mediator.Publish(new NotificationMessage("Deathroll", "Lobby is full!", NotificationType.Error));
            return false;
        }

        var currentPlayer = await GetCurrentPlayerDataAsync();
        var invitation = new DeathrollInvitationDto
        {
            InvitationId = Guid.NewGuid().ToString(),
            Sender = new UserData(_apiController.UID, currentPlayer.Name),
            Recipient = ResolveRecipientUserData(playerName),
            GameId = _currentGame.GameId,
            SentAt = DateTime.UtcNow
        };
        
        await _apiController.DeathrollSendInvitation(invitation);
        _logger.LogDebug("Invited player {player} to lobby {lobbyName}", playerName, _currentGame.LobbyName);
        
        return true;
    }

    // New method for players to join public lobbies
    public async Task<bool> JoinPublicLobbyAsync(string gameId)
    {
        var playerName = await _dalamudUtilService.RunOnFrameworkThread(() => 
            _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? "Unknown");

        if (string.IsNullOrEmpty(playerName))
        {
            return false;
        }

        // Send join request via API
        try
        {
            var playerData = await GetCurrentPlayerDataAsync();
            var joinRequest = new DeathrollJoinLobbyDto
            {
                GameId = gameId,
                PlayerName = playerName,
                PlayerData = new UserData(_apiController.UID, playerData.Name)
            };
            
            var success = await _apiController.DeathrollJoinLobby(joinRequest);
            
            if (success)
            {
                _logger.LogDebug("Successfully requested to join public lobby {gameId} as {player}", gameId, playerName);
            }
            else
            {
                _logger.LogWarning("Failed to join public lobby {gameId}", gameId);
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join public lobby {gameId}", gameId);
            return false;
        }
    }

    // New method for players to leave the current lobby
    public async Task<bool> LeaveLobbyAsync()
    {
        if (_currentGame == null || !IsLobbyActive)
        {
            return false;
        }

        try
        {
            var currentPlayer = await GetCurrentPlayerDataAsync();
            var leaveDto = new DeathrollLeaveLobbyDto
            {
                GameId = _currentGame.GameId,
                PlayerName = currentPlayer.Name,
                PlayerData = new UserData(_apiController.UID, currentPlayer.Name),
                LeftAt = DateTime.UtcNow
            };

            var sent = await _apiController.DeathrollLeaveLobby(leaveDto);
            if (!sent)
            {
                _logger.LogDebug("Failed to send leave lobby for {gameId}", _currentGame.GameId);
                return false;
            }

            // Clear local state for the leaver
            _currentGame = null;
            _autoOpenAttemptForGameId = null;
            _mediator.Publish(new NotificationMessage("Deathroll", "You left the lobby.", NotificationType.Info));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while leaving lobby {gameId}", _currentGame?.GameId);
            return false;
        }
    }

    // New method to open/close lobby
    public async Task<bool> OpenCloseLobbyAsync(bool isOpen)
    {
        if (_currentGame?.State != DeathrollGameState.LobbyCreated && 
            _currentGame?.State != DeathrollGameState.WaitingForPlayers)
        {
            return false;
        }

        var currentPlayerName = await _dalamudUtilService.RunOnFrameworkThread(() => 
            _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? "Unknown");

        // Only host can open/close lobby
        if (!string.Equals(_currentGame.HostName, currentPlayerName, StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new NotificationMessage("Deathroll", "Only the host can open/close the lobby!", NotificationType.Error));
            return false;
        }

        try
        {
            var success = await _apiController.DeathrollOpenCloseLobby(_currentGame.GameId, isOpen);
            
            if (success)
            {
                _logger.LogDebug("Successfully {action} lobby {gameId}", isOpen ? "opened" : "closed", _currentGame.GameId);
                
                // Update local state
                _currentGame.State = isOpen ? DeathrollGameState.WaitingForPlayers : DeathrollGameState.LobbyCreated;
                _logger.LogDebug("Local lobby state set to {state}", _currentGame.State);
                
                if (isOpen && _currentGame.Visibility == LobbyVisibility.Public)
                {
                    await AnnounceLobbyToNearbyPlayersAsync();
                }
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {action} lobby {gameId}", isOpen ? "open" : "close", _currentGame.GameId);
            return false;
        }
    }

    // New method to cancel lobby
    public async Task<bool> CancelLobbyAsync()
    {
        if (_currentGame?.State != DeathrollGameState.LobbyCreated && 
            _currentGame?.State != DeathrollGameState.WaitingForPlayers)
        {
            return false;
        }

        var currentPlayerName = await _dalamudUtilService.RunOnFrameworkThread(() => 
            _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? "Unknown");

        // Only host can cancel lobby
        if (!string.Equals(_currentGame.HostName, currentPlayerName, StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new NotificationMessage("Deathroll", "Only the host can cancel the lobby!", NotificationType.Error));
            return false;
        }

        try
        {
            var success = await _apiController.DeathrollCancelLobby(_currentGame.GameId);
            
            if (success)
            {
                _logger.LogDebug("Successfully canceled lobby {gameId}", _currentGame.GameId);
                
                // Reset local state
                _currentGame = null;
                _mediator.Publish(new NotificationMessage("Deathroll", "Lobby canceled", NotificationType.Info));
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel lobby {gameId}", _currentGame.GameId);
            return false;
        }
    }

    // New method to accept a player join request
    public async Task<bool> AcceptPlayerJoinAsync(string playerName)
    {
        if (_currentGame?.State != DeathrollGameState.WaitingForPlayers)
        {
            return false;
        }

        var currentPlayerName = await _dalamudUtilService.RunOnFrameworkThread(() => 
            _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? "Unknown");

        // Only host can accept players
        if (!string.Equals(_currentGame.HostName, currentPlayerName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Only the host can accept players. Current player: {currentPlayer}, Host: {host}", 
                currentPlayerName, _currentGame.HostName);
            return false;
        }

        // Check if player is already in the lobby
        if (_currentGame.Players.Any(p => string.Equals(p.Name, playerName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Player {player} is already in lobby {gameId}", playerName, _currentGame.GameId);
            return false;
        }

        // Check if lobby is full
        if (_currentGame.Players.Count >= _currentGame.MaxPlayers)
        {
            _logger.LogWarning("Lobby {gameId} is full, cannot accept player {player}", _currentGame.GameId, playerName);
            return false;
        }

        // Add the player to the lobby
        var newPlayer = new DeathrollPlayer
        {
            Name = playerName,
            IsHost = false,
            HasAccepted = true,
            IsReady = false,
            LastActivity = DateTime.UtcNow
        };

        _currentGame.Players.Add(newPlayer);
        
        _logger.LogInformation("Successfully added player {player} to lobby {gameId}. Current players: {playerCount}/{maxPlayers}", 
            playerName, _currentGame.GameId, _currentGame.Players.Count, _currentGame.MaxPlayers);

        // Send lobby state update to server to broadcast to all clients
        try
        {
            var lobbyStateDto = new DeathrollGameStateDto
            {
                GameId = _currentGame.GameId,
                State = Sphene.API.Dto.DeathrollGameState.WaitingForPlayers,
                GameMode = _currentGame.GameMode,
                Players = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList(),
                ReadyPlayers = _currentGame.Players.Where(p => p.IsReady).Select(p => new UserData(string.Empty, p.Name)).ToList(),
                LobbyName = _currentGame.LobbyName,
                Host = new UserData(_apiController.UID, _currentGame.HostName),
                MaxPlayers = _currentGame.MaxPlayers,
                Visibility = _currentGame.Visibility == LobbyVisibility.Public ? Sphene.API.Dto.LobbyVisibility.Public : Sphene.API.Dto.LobbyVisibility.Private,
                LobbyCreatedTime = _currentGame.LobbyCreatedTime
            };

            var success = await _apiController.DeathrollUpdateLobbyState(lobbyStateDto);
            if (success)
            {
                _logger.LogDebug("Successfully sent lobby state update after adding player {player}", playerName);
                try
                {
                    // Explicitly announce current ready players so the new joiner receives their ready state immediately
                    foreach (var p in _currentGame.Players.Where(p => p.IsReady))
                    {
                        await _apiController.DeathrollSetPlayerReady(_currentGame.GameId, p.Name, true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error broadcasting ready states after adding player {player}", playerName);
                }
            }
            else
            {
                _logger.LogWarning("Failed to send lobby state update after adding player {player}", playerName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending lobby state update after adding player {player}", playerName);
        }

        // Notify about the player joining
        _mediator.Publish(new NotificationMessage("Deathroll", $"{playerName} joined the lobby!", NotificationType.Success));

        return true;
    }

    // New: cancel an in-progress game for all clients and close lobby
    public async Task<bool> CancelInProgressGameAsync()
    {
        if (_currentGame == null)
            return false;

        var wasInProgress = _currentGame.State == DeathrollGameState.InProgress;

        try
        {
            var currentPlayer = await GetCurrentPlayerDataAsync();

            var finalDto = new DeathrollGameStateDto
            {
                GameId = _currentGame.GameId,
                State = Sphene.API.Dto.DeathrollGameState.Finished,
                GameMode = _currentGame.GameMode,
                Players = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList(),
                CurrentPlayer = null,
                CurrentRollMax = _currentGame.CurrentRollMax,
                LastRoll = _currentGame.LastRoll,
                LastRollTime = DateTime.UtcNow,
                RollHistory = new List<string>(_currentGame.RollHistory.Append($"Game canceled by {currentPlayer.Name}")),
                GameStartTime = _currentGame.GameStartTime,
                GameEndTime = DateTime.UtcNow,
                LobbyName = _currentGame.LobbyName,
                Host = new UserData(_apiController.UID, _currentGame.HostName),
                MaxPlayers = _currentGame.MaxPlayers,
                Visibility = (Sphene.API.Dto.LobbyVisibility)_currentGame.Visibility,
                LobbyCreatedTime = _currentGame.LobbyCreatedTime
            };

            var sent = await _apiController.DeathrollUpdateGameState(finalDto);
            if (!sent)
            {
                _logger.LogWarning("Failed to broadcast cancel state for {gameId}", _currentGame.GameId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting cancel state for {gameId}", _currentGame.GameId);
        }

        try
        {
            // Broadcast lobby cancellation so all clients reset/close the lobby
            await _apiController.DeathrollCancelLobby(_currentGame.GameId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error canceling lobby for {gameId}", _currentGame.GameId);
        }

        _currentGame = null;
        _mediator.Publish(new NotificationMessage("Deathroll", "Game canceled", NotificationType.Info));
        return wasInProgress;
    }

    // New method to set player ready status
    public async Task<bool> SetPlayerReadyAsync(bool isReady)
    {
        if (_currentGame?.State != DeathrollGameState.WaitingForPlayers)
        {
            return false;
        }

        try
        {
            var currentPlayerName = await _dalamudUtilService.RunOnFrameworkThread(() => 
                _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? "Unknown");
                
            var success = await _apiController.DeathrollSetPlayerReady(_currentGame.GameId, currentPlayerName, isReady);
            
            if (success)
            {
                _logger.LogDebug("Successfully set player ready status to {isReady} in lobby {gameId}", isReady, _currentGame.GameId);
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set player ready status in lobby {gameId}", _currentGame.GameId);
            return false;
        }
    }

    // Modified method - now only starts the game, doesn't create lobby
    public async Task<bool> StartGameFromLobbyAsync()
    {
        if (_currentGame?.State != DeathrollGameState.WaitingForPlayers && 
            _currentGame?.State != DeathrollGameState.ReadyToStart)
        {
            return false;
        }

        var currentPlayerName = await _dalamudUtilService.RunOnFrameworkThread(() => 
            _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? "Unknown");

        // Only host can start the game
        if (!string.Equals(_currentGame.HostName, currentPlayerName, StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new NotificationMessage("Deathroll", "Only the host can start the game!", NotificationType.Error));
            return false;
        }

        // Check if we have enough players
        var acceptedPlayers = _currentGame.Players.Where(p => p.HasAccepted).ToList();
        if (acceptedPlayers.Count < 2)
        {
            _mediator.Publish(new NotificationMessage("Deathroll", "Need at least 2 players to start the game!", NotificationType.Error));
            return false;
        }

        // Require all accepted players to be ready
        if (acceptedPlayers.Any(p => !p.IsReady))
        {
            _mediator.Publish(new NotificationMessage("Deathroll", "All players must set Ready before starting!", NotificationType.Error));
            return false;
        }

        await StartGameAsync();
        return true;
    }

    private async Task<DeathrollPlayer> GetCurrentPlayerDataAsync()
    {
        var playerCharacter = await _dalamudUtilService.RunOnFrameworkThread(() => 
            _dalamudUtilService.GetPlayerCharacter());
            
        if (playerCharacter == null)
        {
            return new DeathrollPlayer
            {
                Name = "Unknown",
                IsReady = false
            };
        }

        return new DeathrollPlayer
        {
            Name = playerCharacter.Name?.TextValue ?? "Unknown",
            IsReady = false
        };
    }

    private UserData ResolveRecipientUserData(string playerName)
    {
        try
        {
            var visibleUsers = _pairManager.GetVisibleUsers();
            var match = visibleUsers?.FirstOrDefault(u =>
                string.Equals(u.AliasOrUID, playerName, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(u.Alias) && string.Equals(u.Alias, playerName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(u.UID) && string.Equals(u.UID, playerName, StringComparison.OrdinalIgnoreCase))
            );

            if (match != null)
            {
                if (!string.IsNullOrEmpty(match.UID))
                    return new UserData(match.UID, match.AliasOrUID ?? match.Alias ?? playerName);
                return new UserData(string.Empty, match.AliasOrUID ?? match.Alias ?? playerName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve recipient UID for {playerName}", playerName);
        }

        return new UserData(string.Empty, playerName);
    }

    private async Task AnnounceLobbyToNearbyPlayersAsync()
    {
        try
        {
            var nearbyPlayers = await GetNearbyPlayersAsync();
            if (nearbyPlayers.Any() && _currentGame != null)
            {
                // Send one announcement that will be broadcasted to all clients
                await SendLobbyAnnouncementAsync();
                _logger.LogDebug("Announced lobby {lobbyName} to {playerCount} nearby players", 
                    _currentGame.LobbyName, nearbyPlayers.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to announce lobby to nearby players");
        }
    }

    private async Task SendLobbyAnnouncementAsync()
    {
        if (_currentGame == null) return;
        
        try
        {
            var currentPlayer = await GetCurrentPlayerDataAsync();
            var announcement = new DeathrollLobbyAnnouncementDto
            {
                GameId = _currentGame.GameId,
                LobbyName = _currentGame.LobbyName,
                Host = new UserData(_apiController.UID, currentPlayer.Name),
                CurrentPlayers = _currentGame.Players.Count,
                MaxPlayers = _currentGame.MaxPlayers,
                Visibility = (API.Dto.LobbyVisibility)_currentGame.Visibility,
                CreatedAt = _currentGame.LobbyCreatedTime ?? DateTime.UtcNow
            };
            
            _logger.LogInformation("Creating DeathrollLobbyAnnouncementDto: GameId={gameId}, LobbyName={lobbyName}, Host={host}, Players={currentPlayers}/{maxPlayers}, Visibility={visibility}, CreatedAt={createdAt}", 
                announcement.GameId, announcement.LobbyName, announcement.Host.AliasOrUID, 
                announcement.CurrentPlayers, announcement.MaxPlayers, announcement.Visibility, announcement.CreatedAt);
            
            _logger.LogInformation("Sending lobby announcement for {lobbyName} (GameId: {gameId}) to server with {currentPlayers}/{maxPlayers} players", 
                _currentGame.LobbyName, _currentGame.GameId, _currentGame.Players.Count, _currentGame.MaxPlayers);
            
            await _apiController.DeathrollAnnounceLobby(announcement);
            _logger.LogInformation("Successfully sent lobby announcement for {lobbyName} to server", _currentGame.LobbyName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send lobby announcement for {lobbyName}", _currentGame?.LobbyName);
        }
    }

    // Tournament helpers
    private DeathrollTournamentStateDto BuildInitialTournamentState(List<UserData> participants, string gameId)
    {
        // Ensure deterministic ordering
        var ordered = participants.OrderBy(u => u.AliasOrUID).ToList();
        var matches = new List<DeathrollTournamentMatchDto>();
        for (int i = 0; i < ordered.Count; i += 2)
        {
            var a = ordered[i];
            var b = i + 1 < ordered.Count ? ordered[i + 1] : null;
            var match = new DeathrollTournamentMatchDto
            {
                MatchId = $"{gameId}-R1M{(i / 2) + 1}",
                Round = 1,
                PlayerA = a,
                PlayerB = b,
                Winner = null,
                Loser = null,
                IsCompleted = false
            };
            // Auto-complete byes
            if (b == null)
            {
                match.Winner = a;
                match.IsCompleted = true;
            }
            matches.Add(match);
        }

        var dto = new DeathrollTournamentStateDto
        {
            GameId = gameId,
            TournamentId = $"{gameId}-tournament",
            Stage = DeathrollTournamentStage.InProgress,
            CurrentRound = 1,
            Participants = ordered,
            Matches = matches,
            Champion = null
        };

        // If round 1 is fully complete due to byes, cascade to next rounds
        EnsureNextRoundIfReady(dto, 1);
        return dto;
    }

    private void EnsureNextRoundIfReady(DeathrollTournamentStateDto state, int round)
    {
        // Get matches of the given round
        var roundMatches = state.Matches.Where(m => m.Round == round).ToList();
        if (roundMatches.Count == 0) return;

        // If any match incomplete, the current round stands
        if (roundMatches.Any(m => !m.IsCompleted))
        {
            state.CurrentRound = round;
            return;
        }

        // Winners of the completed round
        var winners = roundMatches.Where(m => m.Winner != null).Select(m => m.Winner!).ToList();
        if (winners.Count <= 1)
        {
            // We have a champion
            state.Stage = DeathrollTournamentStage.Completed;
            state.Champion = winners.FirstOrDefault();
            state.CurrentRound = round;
            return;
        }

        // Build next round matches
        var nextRound = round + 1;
        var nextMatches = new List<DeathrollTournamentMatchDto>();
        for (int i = 0; i < winners.Count; i += 2)
        {
            var a = winners[i];
            var b = i + 1 < winners.Count ? winners[i + 1] : null;
            var match = new DeathrollTournamentMatchDto
            {
                MatchId = $"{state.GameId}-R{nextRound}M{(i / 2) + 1}",
                Round = nextRound,
                PlayerA = a,
                PlayerB = b,
                Winner = null,
                Loser = null,
                IsCompleted = false
            };
            if (b == null)
            {
                match.Winner = a;
                match.IsCompleted = true;
            }
            nextMatches.Add(match);
        }
        state.Matches.AddRange(nextMatches);

        // If the newly created round auto-completed due to byes, continue cascading
        EnsureNextRoundIfReady(state, nextRound);
    }

    private bool TryAdvanceTournamentWithResult(string winnerName, string? loserName)
    {
        if (_tournamentState == null) return false;
        // Normalize names for lookup
        Func<UserData?, bool> isWinner = u => u != null && string.Equals(u.AliasOrUID, winnerName, StringComparison.OrdinalIgnoreCase);
        Func<UserData?, bool> isLoser = u => !string.IsNullOrEmpty(loserName) && u != null && string.Equals(u.AliasOrUID, loserName, StringComparison.OrdinalIgnoreCase);

        // Prefer current round, otherwise find earliest round with an incomplete eligible match
        var targetMatch = _tournamentState.Matches
            .Where(m => !m.IsCompleted)
            .OrderBy(m => m.Round)
            .ThenBy(m => m.MatchId)
            .FirstOrDefault(m => isWinner(m.PlayerA) || isWinner(m.PlayerB) || isLoser(m.PlayerA) || isLoser(m.PlayerB));

        if (targetMatch == null)
        {
            // Fall back to first incomplete match of current round
            targetMatch = _tournamentState.Matches
                .Where(m => m.Round == _tournamentState.CurrentRound && !m.IsCompleted)
                .OrderBy(m => m.MatchId)
                .FirstOrDefault();
        }

        if (targetMatch == null) return false;

        // Set winner/loser and complete the match
        var aName = targetMatch.PlayerA?.AliasOrUID;
        var bName = targetMatch.PlayerB?.AliasOrUID;
        var winner = _tournamentState.Participants.FirstOrDefault(p => string.Equals(p.AliasOrUID, winnerName, StringComparison.OrdinalIgnoreCase))
                     ?? new UserData(string.Empty, winnerName);
        UserData? loser = string.IsNullOrEmpty(loserName)
            ? null
            : _tournamentState.Participants.FirstOrDefault(p => string.Equals(p.AliasOrUID, loserName, StringComparison.OrdinalIgnoreCase))
              ?? new UserData(string.Empty, loserName!);
        targetMatch.Winner = winner;
        targetMatch.Loser = loser;
        targetMatch.IsCompleted = true;

        // After completing a match, check if the round is ready to advance
        EnsureNextRoundIfReady(_tournamentState, targetMatch.Round);
        return true;
    }

    // Keep the old method for backward compatibility but mark as obsolete
    [Obsolete("Use CreateLobbyAsync and StartGameFromLobbyAsync instead")]
    public async Task<bool> StartNewGameAsync(List<string> nearbyPlayers)
    {
        // Create lobby and immediately start inviting players (old behavior)
        if (await CreateLobbyAsync("Quick Game", LobbyVisibility.Private))
        {
            await OpenLobbyAsync();
            
            // Send invitations to nearby players
            foreach (var nearbyPlayer in nearbyPlayers.Where(p => !string.Equals(p, _currentGame.HostName, StringComparison.OrdinalIgnoreCase)))
            {
                await InvitePlayerToLobbyAsync(nearbyPlayer);
            }
            
            return true;
        }
        
        return false;
    }

    public async Task AcceptInvitationAsync(DeathrollInvitationDto invitation)
    {
        await RespondToInvitationAsync(invitation.InvitationId, true);
        
        try
        {
            var currentPlayer = await GetCurrentPlayerDataAsync();
            var joinRequest = new DeathrollJoinLobbyDto
            {
                GameId = invitation.GameId,
                PlayerName = currentPlayer.Name,
                PlayerData = new UserData(_apiController.UID, currentPlayer.Name)
            };
            
            var joined = await _apiController.DeathrollJoinLobby(joinRequest);
            if (joined)
            {
                _logger.LogDebug("Accepted invitation and sent join request for lobby {gameId}", invitation.GameId);
                
                // Ensure local lobby state so UI shows Current Lobby
                if (_currentGame == null || !string.Equals(_currentGame.GameId, invitation.GameId, StringComparison.Ordinal))
                {
                    _currentGame = new DeathrollGame
                    {
                        GameId = invitation.GameId,
                        State = DeathrollGameState.LobbyCreated,
                        HostName = invitation.Sender.AliasOrUID,
                        LobbyName = invitation.Sender.AliasOrUID + "'s Lobby",
                        MaxPlayers = 8,
                        Visibility = LobbyVisibility.Private,
                    };
                }
                
                // Ensure host is present locally
                var hostName = invitation.Sender.AliasOrUID;
                if (!_currentGame.Players.Any(p => string.Equals(p.Name, hostName, StringComparison.OrdinalIgnoreCase)))
                {
                    _currentGame.Players.Add(new DeathrollPlayer
                    {
                        Name = hostName,
                        IsHost = true,
                        HasAccepted = true,
                        IsReady = false
                    });
                }
                
                // Add self to local players if not present
                if (!_currentGame.Players.Any(p => string.Equals(p.Name, currentPlayer.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _currentGame.Players.Add(new DeathrollPlayer
                    {
                        Name = currentPlayer.Name,
                        IsHost = false,
                        HasAccepted = true,
                        IsReady = false
                    });
                }
                
                _mediator.Publish(new NotificationMessage("Deathroll", $"Joined lobby hosted by {invitation.Sender.AliasOrUID}.", NotificationType.Success));
            }
            else
            {
                _mediator.Publish(new NotificationMessage("Deathroll", "Failed to join lobby.", NotificationType.Error));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining lobby after accepting invitation {invitationId}", invitation.InvitationId);
        }
    }

    public async Task AcceptInvitationAsync(string senderName)
    {
        // Find the invitation from the sender
        // Since invitations are handled by UI, we need to get it from there or use the API
        // For now, we'll just log that we're accepting from this sender
        _logger.LogDebug("Accepting invitation from sender: {senderName}", senderName);
        
        // This should ideally find the actual invitation and call the main AcceptInvitationAsync method
        // For now, we'll implement a simple acceptance that joins the sender's game
        await JoinGameBySenderAsync(senderName);
    }

    private async Task AcceptInvitationBySenderAsync(string senderName)
    {
        _logger.LogDebug("Accepting invitation by sender: {senderName}", senderName);
        await JoinGameBySenderAsync(senderName);
    }

    private async Task JoinGameBySenderAsync(string senderName)
    {
        _logger.LogDebug("Attempting to join game by sender: {senderName}", senderName);
        // This is a simplified implementation - in a full implementation,
        // we would need to find the actual game ID from the sender
    }

    public async Task DeclineInvitationAsync(DeathrollInvitationDto invitation)
    {
        await RespondToInvitationAsync(invitation.InvitationId, false);
    }

    // Handle invitation response messages
    private void OnInvitationResponse(DeathrollInvitationResponseDto response)
    {
        _logger.LogDebug("Received invitation response from {responder}: {accepted}", 
            response.Responder.AliasOrUID, response.Accepted);
        
        if (response.Accepted && _currentGame != null)
        {
            // Add player to current game if they accepted
            var existingPlayer = _currentGame.Players.FirstOrDefault(p => 
                string.Equals(p.Name, response.Responder.AliasOrUID, StringComparison.OrdinalIgnoreCase));
            
            if (existingPlayer == null)
            {
                _currentGame.Players.Add(new DeathrollPlayer
                {
                    Name = response.Responder.AliasOrUID,
                    IsHost = false,
                    HasAccepted = true,
                    IsReady = false
                });
                
                _logger.LogDebug("Added player {player} to game {gameId}", 
                    response.Responder.AliasOrUID, _currentGame.GameId);
            }
        }
    }

    // Handle game state update messages
    private void OnGameStateUpdate(DeathrollGameStateDto gameState)
    {
        _logger.LogDebug("Received game state update for game {gameId}, state: {state}", 
            gameState.GameId, gameState.State);
        
        var mappedState = gameState.State switch
        {
            Sphene.API.Dto.DeathrollGameState.LobbyCreated => DeathrollGameState.LobbyCreated,
            Sphene.API.Dto.DeathrollGameState.WaitingForPlayers => DeathrollGameState.WaitingForPlayers,
            Sphene.API.Dto.DeathrollGameState.ReadyToStart => DeathrollGameState.ReadyToStart,
            Sphene.API.Dto.DeathrollGameState.InProgress => DeathrollGameState.InProgress,
            Sphene.API.Dto.DeathrollGameState.Finished => DeathrollGameState.Finished,
            _ => _currentGame?.State ?? DeathrollGameState.Inactive
        };
        
        // Create or update local game state
        if (_currentGame == null || _currentGame.GameId != gameState.GameId)
        {
            _logger.LogDebug("Initializing local game state from server update for {gameId}", gameState.GameId);
            _currentGame = new DeathrollGame
            {
                GameId = gameState.GameId,
                State = mappedState,
                GameMode = gameState.GameMode,
                LobbyName = gameState.LobbyName ?? string.Empty,
                HostName = gameState.Host?.AliasOrUID ?? string.Empty,
                MaxPlayers = gameState.MaxPlayers,
                Visibility = (LobbyVisibility)gameState.Visibility,
                LobbyCreatedTime = gameState.LobbyCreatedTime,
                GameStartTime = gameState.GameStartTime,
                GameEndTime = gameState.GameEndTime,
                CurrentRollMax = gameState.CurrentRollMax,
                CurrentPlayerName = gameState.CurrentPlayer?.AliasOrUID ?? string.Empty
            };
        }
        else
        {
            _currentGame.State = mappedState;
            _currentGame.GameMode = gameState.GameMode;
            if (!string.IsNullOrEmpty(gameState.LobbyName)) _currentGame.LobbyName = gameState.LobbyName;
            if (gameState.Host != null) _currentGame.HostName = gameState.Host.AliasOrUID;
            if (gameState.MaxPlayers > 0) _currentGame.MaxPlayers = gameState.MaxPlayers;
            _currentGame.Visibility = (LobbyVisibility)gameState.Visibility;
            _currentGame.LobbyCreatedTime = gameState.LobbyCreatedTime ?? _currentGame.LobbyCreatedTime;
            _currentGame.GameStartTime = gameState.GameStartTime ?? _currentGame.GameStartTime;
            _currentGame.GameEndTime = gameState.GameEndTime ?? _currentGame.GameEndTime;
            _currentGame.CurrentRollMax = gameState.CurrentRollMax;
            _currentGame.LastRoll = gameState.LastRoll;
            _currentGame.LastRollTime = gameState.LastRollTime;
            _currentGame.Winner = gameState.Winner?.AliasOrUID;
            _currentGame.Loser = gameState.Loser?.AliasOrUID;
            if (gameState.CurrentPlayer != null)
            {
                _currentGame.CurrentPlayerName = gameState.CurrentPlayer.AliasOrUID;
            }
        }
        
        // Update players list if provided
        if (gameState.Players != null)
        {
            // Use DTO ReadyPlayers if provided; otherwise preserve existing readiness
            var hasReadyList = gameState.ReadyPlayers != null;
            var readySet = hasReadyList
                ? new HashSet<string>(gameState.ReadyPlayers!.Select(u => u.AliasOrUID), StringComparer.OrdinalIgnoreCase)
                : null;

            // Preserve existing readiness as fallback
            var existingByName = _currentGame.Players.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            _currentGame.Players.Clear();
            foreach (var player in gameState.Players)
            {
                var name = player.AliasOrUID;
                existingByName.TryGetValue(name, out var prev);
                _currentGame.Players.Add(new DeathrollPlayer
                {
                    Name = name,
                    IsHost = string.Equals(name, _currentGame.HostName, StringComparison.OrdinalIgnoreCase),
                    HasAccepted = true,
                    IsReady = hasReadyList ? readySet!.Contains(name) : (prev?.IsReady ?? false),
                    LastActivity = prev?.LastActivity ?? DateTime.UtcNow
                });
            }
            
            // Ensure host stays visible locally even if missing from DTO
            if (!string.IsNullOrEmpty(_currentGame.HostName) &&
                !_currentGame.Players.Any(p => string.Equals(p.Name, _currentGame.HostName, StringComparison.OrdinalIgnoreCase)))
            {
                existingByName.TryGetValue(_currentGame.HostName, out var prevHost);
                _currentGame.Players.Add(new DeathrollPlayer
                {
                    Name = _currentGame.HostName,
                    IsHost = true,
                    HasAccepted = true,
                    IsReady = hasReadyList ? readySet!.Contains(_currentGame.HostName) : (prevHost?.IsReady ?? false),
                    LastActivity = prevHost?.LastActivity ?? DateTime.UtcNow
                });
            }
        }

        // Update roll history if provided
        if (gameState.RollHistory != null && gameState.RollHistory.Count > 0)
        {
            _currentGame.RollHistory.Clear();
            _currentGame.RollHistory.AddRange(gameState.RollHistory);
        }
        
        _logger.LogDebug("Updated local game state for {gameId} to {state}. Players: {count}", 
            gameState.GameId, _currentGame.State, _currentGame.Players.Count);

        // Auto-open newly created lobby when we are the host and server sent the definitive GameId
        try
        {
            var shouldAutoOpen = _currentGame.State == DeathrollGameState.LobbyCreated
                                 && !string.IsNullOrEmpty(_currentGame.HostName)
                                 && (_autoOpenAttemptForGameId != _currentGame.GameId);

            if (shouldAutoOpen)
            {
                // Avoid framework-thread access by checking host via UID from server update
                var isHost = gameState.Host?.UID != null && string.Equals(gameState.Host.UID, _apiController.UID, StringComparison.Ordinal);
                if (isHost)
                {
                    var gameIdLocal = _currentGame.GameId;
                    _autoOpenAttemptForGameId = gameIdLocal;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var opened = await OpenCloseLobbyAsync(true);
                            if (!opened)
                            {
                                _logger.LogDebug("Auto-open attempt failed for lobby {gameId}", gameIdLocal);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error auto-opening lobby {gameId}", gameIdLocal);
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error deciding auto-open state for lobby {gameId}", _currentGame.GameId);
        }

        // Ensure participants auto-open the game window when the game starts
        try
        {
            if (mappedState == DeathrollGameState.InProgress)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var isHost = gameState.Host?.UID != null && string.Equals(gameState.Host.UID, _apiController.UID, StringComparison.Ordinal);
                        var isParticipant = isHost;
                        if (!isParticipant)
                        {
                            var localName = await _dalamudUtilService.RunOnFrameworkThread(() =>
                                _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? string.Empty);
                            if (!string.IsNullOrEmpty(localName))
                            {
                                var players = gameState.Players ?? new List<UserData>();
                                isParticipant = players.Any(p => string.Equals(p.AliasOrUID, localName, StringComparison.OrdinalIgnoreCase));
                            }
                        }

                        if (isParticipant)
                        {
                            _mediator.Publish(new OpenDeathrollLobbyMessage(SelectCurrentLobbyTab: false, SelectGameTab: true));
                            _logger.LogDebug("Auto-selected Game tab in Lobby for participant on game start: {gameId}", gameState.GameId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error auto-opening Deathroll UI on game start");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error scheduling auto-open for Deathroll UI on game start");
        }
    }

    // Handle lobby leave messages
    private void OnLobbyLeave(DeathrollLobbyLeaveMessage msg)
    {
        try
        {
            if (_currentGame == null || !string.Equals(_currentGame.GameId, msg.LobbyId, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogDebug("Processing lobby leave for player {player} in lobby {gameId}", msg.PlayerName, msg.LobbyId);

            // Remove the player locally if present
            var removed = _currentGame.Players.RemoveAll(p => string.Equals(p.Name, msg.PlayerName, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                _logger.LogDebug("Removed player {player} from local lobby state {gameId}", msg.PlayerName, _currentGame.GameId);

                // If fewer than 2 accepted players remain, ensure state is WaitingForPlayers
                var acceptedPlayers = _currentGame.Players.Where(p => p.HasAccepted).ToList();
                if (acceptedPlayers.Count < 2 && (_currentGame.State == DeathrollGameState.ReadyToStart || _currentGame.State == DeathrollGameState.WaitingForPlayers))
                {
                    _currentGame.State = DeathrollGameState.WaitingForPlayers;
                }

                // If we are the host, broadcast updated lobby state so all clients sync
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var currentPlayerName = await _dalamudUtilService.RunOnFrameworkThread(() =>
                            _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? "Unknown");

                        if (string.Equals(_currentGame.HostName, currentPlayerName, StringComparison.OrdinalIgnoreCase))
                        {
                            var dto = new DeathrollGameStateDto
                            {
                                GameId = _currentGame.GameId,
                                State = _currentGame.State switch
                                {
                                    DeathrollGameState.LobbyCreated => Sphene.API.Dto.DeathrollGameState.LobbyCreated,
                                    DeathrollGameState.WaitingForPlayers => Sphene.API.Dto.DeathrollGameState.WaitingForPlayers,
                                    DeathrollGameState.ReadyToStart => Sphene.API.Dto.DeathrollGameState.ReadyToStart,
                                    _ => Sphene.API.Dto.DeathrollGameState.WaitingForPlayers
                                },
                                GameMode = _currentGame.GameMode,
                                Players = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList(),
                                ReadyPlayers = _currentGame.Players.Where(p => p.IsReady).Select(p => new UserData(string.Empty, p.Name)).ToList(),
                                LobbyName = _currentGame.LobbyName,
                                Host = new UserData(_apiController.UID, _currentGame.HostName),
                                MaxPlayers = _currentGame.MaxPlayers,
                                Visibility = _currentGame.Visibility == LobbyVisibility.Public ? Sphene.API.Dto.LobbyVisibility.Public : Sphene.API.Dto.LobbyVisibility.Private,
                                LobbyCreatedTime = _currentGame.LobbyCreatedTime
                            };
                            await _apiController.DeathrollUpdateLobbyState(dto);
                            _logger.LogDebug("Broadcasted lobby state after player leave: {player}", msg.PlayerName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error broadcasting lobby state after leave for {gameId}", _currentGame.GameId);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling lobby leave message");
        }
    }

    // Broadcast current lobby state if we are host (used for UI refresh)
    public async Task<bool> BroadcastLobbyStateIfHostAsync()
    {
        if (_currentGame == null || !IsLobbyActive)
            return false;

        var currentPlayerName = await _dalamudUtilService.RunOnFrameworkThread(() =>
            _dalamudUtilService.GetPlayerCharacter()?.Name?.TextValue ?? "Unknown");

        if (!string.Equals(_currentGame.HostName, currentPlayerName, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var dto = new DeathrollGameStateDto
            {
                GameId = _currentGame.GameId,
                State = _currentGame.State switch
                {
                    DeathrollGameState.LobbyCreated => Sphene.API.Dto.DeathrollGameState.LobbyCreated,
                    DeathrollGameState.WaitingForPlayers => Sphene.API.Dto.DeathrollGameState.WaitingForPlayers,
                    DeathrollGameState.ReadyToStart => Sphene.API.Dto.DeathrollGameState.ReadyToStart,
                    DeathrollGameState.InProgress => Sphene.API.Dto.DeathrollGameState.InProgress,
                    DeathrollGameState.Finished => Sphene.API.Dto.DeathrollGameState.Finished,
                    _ => Sphene.API.Dto.DeathrollGameState.LobbyCreated
                },
                GameMode = _currentGame.GameMode,
                Players = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList(),
                ReadyPlayers = _currentGame.Players.Where(p => p.IsReady).Select(p => new UserData(string.Empty, p.Name)).ToList(),
                LobbyName = _currentGame.LobbyName,
                Host = new UserData(_apiController.UID, _currentGame.HostName),
                MaxPlayers = _currentGame.MaxPlayers,
                Visibility = _currentGame.Visibility == LobbyVisibility.Public ? Sphene.API.Dto.LobbyVisibility.Public : Sphene.API.Dto.LobbyVisibility.Private,
                LobbyCreatedTime = _currentGame.LobbyCreatedTime
            };
            var success = await _apiController.DeathrollUpdateLobbyState(dto);
            if (!success)
            {
                _logger.LogDebug("Failed to broadcast lobby state on UI refresh for {gameId}", _currentGame.GameId);
            }
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting lobby state on UI refresh for {gameId}", _currentGame.GameId);
            return false;
        }
    }
    private void OnPlayerReadyChanged(DeathrollPlayerReadyMessage msg)
    {
        try
        {
            if (_currentGame == null || !string.Equals(_currentGame.GameId, msg.LobbyId, StringComparison.Ordinal))
            {
                return;
            }

            var player = _currentGame.Players.FirstOrDefault(p => string.Equals(p.Name, msg.PlayerId, StringComparison.OrdinalIgnoreCase));
            if (player != null)
            {
                player.IsReady = msg.IsReady;
                _logger.LogDebug("Updated ready state for player {player} in lobby {gameId} to {ready}", player.Name, _currentGame.GameId, player.IsReady);

                // If all accepted players are ready and at least 2, mark lobby ReadyToStart and broadcast
                var acceptedPlayers = _currentGame.Players.Where(p => p.HasAccepted).ToList();
                if (acceptedPlayers.Count >= 2 && acceptedPlayers.All(p => p.IsReady))
                {
                    _currentGame.State = DeathrollGameState.ReadyToStart;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var lobbyStateDto = new DeathrollGameStateDto
                            {
                                GameId = _currentGame.GameId,
                                State = Sphene.API.Dto.DeathrollGameState.ReadyToStart,
                                GameMode = _currentGame.GameMode,
                                Players = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList(),
                                ReadyPlayers = _currentGame.Players.Where(p => p.IsReady).Select(p => new UserData(string.Empty, p.Name)).ToList(),
                                LobbyName = _currentGame.LobbyName,
                                Host = new UserData(_apiController.UID, _currentGame.HostName),
                                MaxPlayers = _currentGame.MaxPlayers,
                                Visibility = (Sphene.API.Dto.LobbyVisibility)_currentGame.Visibility,
                                LobbyCreatedTime = _currentGame.LobbyCreatedTime
                            };
                            await _apiController.DeathrollUpdateLobbyState(lobbyStateDto);
                            _mediator.Publish(new NotificationMessage("Deathroll", "All players are ready. Host can start the game.", NotificationType.Success));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error broadcasting ReadyToStart state for {gameId}", _currentGame.GameId);
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating player ready state from message");
        }
    }

    // Missing methods that are referenced in the code
    public async Task<bool> ProcessDiceRollAsync(string playerName, int rollResult, int rollMax)
    {
        if (_currentGame == null || _currentGame.State != DeathrollGameState.InProgress)
        {
            _logger.LogDebug("Ignoring dice roll - no active game");
            return false;
        }

        // Enforce turn order: only accept roll from current player
        if (!string.Equals(_currentGame.CurrentPlayerName, playerName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Ignoring roll from {player} - not their turn. Current turn: {current}", playerName, _currentGame.CurrentPlayerName);
            return false;
        }

        _logger.LogDebug("Processing dice roll: {player} rolled {result} (expected max: {max})", playerName, rollResult, rollMax);

        // Update local state
        _currentGame.LastRoll = rollResult;
        _currentGame.LastRollTime = DateTime.UtcNow;
        _currentGame.RollHistory.Add($"{playerName} rolled {rollResult} (1-{rollMax})");

        // Game ends if 1 is rolled
        if (rollResult == 1)
        {
            _currentGame.State = DeathrollGameState.Finished;
            _currentGame.GameEndTime = DateTime.UtcNow;
            _currentGame.Loser = playerName;

            // Determine winner (for 2-player games pick the other player; otherwise leave null)
            var other = _currentGame.Players.FirstOrDefault(p => !string.Equals(p.Name, playerName, StringComparison.OrdinalIgnoreCase))?.Name;
            if (!string.IsNullOrEmpty(other) && _currentGame.Players.Count == 2)
            {
                _currentGame.Winner = other;
            }

            _mediator.Publish(new NotificationMessage("Deathroll", $"{playerName} rolled 1 and lost the game!", NotificationType.Success));

            // Broadcast final game state
            try
            {
                var finalDto = new DeathrollGameStateDto
                {
                    GameId = _currentGame.GameId,
                    State = Sphene.API.Dto.DeathrollGameState.Finished,
                    GameMode = _currentGame.GameMode,
                    Players = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList(),
                    ReadyPlayers = _currentGame.Players.Where(p => p.IsReady).Select(p => new UserData(string.Empty, p.Name)).ToList(),
                    CurrentPlayer = null,
                    CurrentRollMax = rollResult,
                    LastRoll = rollResult,
                    LastRollTime = _currentGame.LastRollTime,
                    Winner = string.IsNullOrEmpty(_currentGame.Winner) ? null : new UserData(string.Empty, _currentGame.Winner),
                    Loser = new UserData(string.Empty, _currentGame.Loser!),
                    GameStartTime = _currentGame.GameStartTime,
                    GameEndTime = _currentGame.GameEndTime,
                    RollHistory = new List<string>(_currentGame.RollHistory),
                    LobbyName = _currentGame.LobbyName,
                    Host = new UserData(_apiController.UID, _currentGame.HostName),
                    MaxPlayers = _currentGame.MaxPlayers,
                    Visibility = (Sphene.API.Dto.LobbyVisibility)_currentGame.Visibility,
                    LobbyCreatedTime = _currentGame.LobbyCreatedTime
                };

                var sent = await _apiController.DeathrollUpdateGameState(finalDto);
                if (!sent)
                {
                    _logger.LogWarning("Failed to broadcast finished game state for {gameId}", _currentGame.GameId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting finished game state for {gameId}", _currentGame.GameId);
            }

            // For tournament mode, advance the bracket with the match result
            try
            {
                if (_currentGame.GameMode == DeathrollGameMode.Tournament)
                {
                    var winnerName = _currentGame.Winner;
                    var loserName = _currentGame.Loser;
                    if (!string.IsNullOrEmpty(winnerName))
                    {
                        var advanced = TryAdvanceTournamentWithResult(winnerName!, loserName);
                        if (!advanced)
                        {
                            _logger.LogWarning("Could not apply tournament result to a match for {gameId}", _currentGame.GameId);
                        }

                        if (_tournamentState != null)
                        {
                            var tourSent = await _apiController.DeathrollUpdateTournamentState(_tournamentState);
                            if (tourSent)
                            {
                                _logger.LogDebug("Broadcasted tournament update after match completion for {gameId}", _currentGame.GameId);
                            }
                            else
                            {
                                _logger.LogWarning("Failed to broadcast tournament update after match completion for {gameId}", _currentGame.GameId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error advancing tournament bracket for {gameId}", _currentGame.GameId);
            }

            return true;
        }

        // Continue game: set new max and switch to next player
        _currentGame.CurrentRollMax = rollResult;

        var currentIndex = _currentGame.Players.FindIndex(p => string.Equals(p.Name, _currentGame.CurrentPlayerName, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0 && _currentGame.Players.Count > 0)
        {
            currentIndex = 0;
        }
        var nextIndex = _currentGame.Players.Count > 0 ? (currentIndex + 1) % _currentGame.Players.Count : 0;
        var nextPlayerName = _currentGame.Players.Count > 0 ? _currentGame.Players[nextIndex].Name : _currentGame.CurrentPlayerName;
        _currentGame.CurrentPlayerName = nextPlayerName;

        _mediator.Publish(new NotificationMessage("Deathroll", $"{playerName} rolled {rollResult}. Next: {nextPlayerName} (1-{rollResult})", NotificationType.Info));

        // Broadcast updated in-progress state
        try
        {
        var updateDto = new DeathrollGameStateDto
        {
            GameId = _currentGame.GameId,
            State = Sphene.API.Dto.DeathrollGameState.InProgress,
            GameMode = _currentGame.GameMode,
            Players = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList(),
            ReadyPlayers = _currentGame.Players.Where(p => p.IsReady).Select(p => new UserData(string.Empty, p.Name)).ToList(),
            CurrentPlayer = new UserData(string.Empty, _currentGame.CurrentPlayerName),
            CurrentRollMax = _currentGame.CurrentRollMax,
            LastRoll = _currentGame.LastRoll,
            LastRollTime = _currentGame.LastRollTime,
            RollHistory = new List<string>(_currentGame.RollHistory),
                GameStartTime = _currentGame.GameStartTime,
                LobbyName = _currentGame.LobbyName,
                Host = new UserData(_apiController.UID, _currentGame.HostName),
                MaxPlayers = _currentGame.MaxPlayers,
                Visibility = (Sphene.API.Dto.LobbyVisibility)_currentGame.Visibility,
                LobbyCreatedTime = _currentGame.LobbyCreatedTime
            };

            var sent = await _apiController.DeathrollUpdateGameState(updateDto);
            if (!sent)
            {
                _logger.LogWarning("Failed to broadcast in-progress game state for {gameId}", _currentGame.GameId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting in-progress game state for {gameId}", _currentGame.GameId);
        }

        return true;
    }

    public async Task<bool> StartGameAsync()
    {
        if (_currentGame?.State != DeathrollGameState.ReadyToStart && 
            _currentGame?.State != DeathrollGameState.WaitingForPlayers)
        {
            return false;
        }

        _currentGame.State = DeathrollGameState.InProgress;
        _currentGame.GameStartTime = DateTime.UtcNow;
        if (string.IsNullOrEmpty(_currentGame.CurrentPlayerName))
        {
            _currentGame.CurrentPlayerName = _currentGame.Players.FirstOrDefault()?.Name ?? _currentGame.HostName;
        }
        _currentGame.CurrentRollMax = 1000;
        
        _logger.LogDebug("Started deathroll game {gameId}", _currentGame.GameId);
        _mediator.Publish(new NotificationMessage("Deathroll", "Game started! Roll /random to begin.", NotificationType.Success));
        
        try
        {
        var gameStateDto = new DeathrollGameStateDto
        {
            GameId = _currentGame.GameId,
            State = Sphene.API.Dto.DeathrollGameState.InProgress,
            GameMode = _currentGame.GameMode,
            Players = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList(),
            ReadyPlayers = _currentGame.Players.Where(p => p.IsReady).Select(p => new UserData(string.Empty, p.Name)).ToList(),
            CurrentPlayer = new UserData(string.Empty, _currentGame.CurrentPlayerName),
            CurrentRollMax = _currentGame.CurrentRollMax,
            GameStartTime = _currentGame.GameStartTime,
            LobbyName = _currentGame.LobbyName,
            Host = new UserData(_apiController.UID, _currentGame.HostName),
                MaxPlayers = _currentGame.MaxPlayers,
                Visibility = (Sphene.API.Dto.LobbyVisibility)_currentGame.Visibility,
                LobbyCreatedTime = _currentGame.LobbyCreatedTime
            };
            var sent = await _apiController.DeathrollUpdateGameState(gameStateDto);
            if (sent)
            {
                _logger.LogDebug("Broadcasted game start state for {gameId}", _currentGame.GameId);
            }
            else
            {
                _logger.LogWarning("Failed to broadcast game start state for {gameId}", _currentGame.GameId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting game start state for {gameId}", _currentGame.GameId);
        }
        
        // If tournament mode, broadcast initial bracket state (multi-round with byes)
        try
        {
            if (_currentGame.GameMode == DeathrollGameMode.Tournament)
            {
                var participants = _currentGame.Players.Select(p => new UserData(string.Empty, p.Name)).ToList();
                _tournamentState = BuildInitialTournamentState(participants, _currentGame.GameId);

                var tourSent = await _apiController.DeathrollUpdateTournamentState(_tournamentState);
                if (tourSent)
                {
                    _logger.LogDebug("Broadcasted initial multi-round tournament bracket for {gameId}", _currentGame.GameId);
                }
                else
                {
                    _logger.LogWarning("Failed to broadcast initial tournament bracket for {gameId}", _currentGame.GameId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error broadcasting initial tournament bracket for {gameId}", _currentGame.GameId);
        }
        
        return true;
    }

    public async Task<List<string>> GetNearbyPlayersAsync()
    {
        try
        {
            var nearbyPlayers = new List<string>();
            
            // Get nearby players from Dalamud
            var players = await _dalamudUtilService.RunOnFrameworkThread(() =>
            {
                var playerList = new List<string>();
                var allPlayers = _dalamudUtilService.GetAllPlayersFromObjectTable();
                
                foreach (var player in allPlayers)
                {
                    if (player?.Name?.TextValue != null)
                    {
                        playerList.Add(player.Name.TextValue);
                    }
                }
                
                return playerList;
            });
            
            return players ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get nearby players");
            return new List<string>();
        }
    }

    public async Task<bool> RespondToInvitationAsync(string invitationId, bool accepted)
    {
        try
        {
            var currentPlayer = await GetCurrentPlayerDataAsync();
            var response = new DeathrollInvitationResponseDto
            {
                InvitationId = invitationId,
                Responder = new UserData(_apiController.UID, currentPlayer.Name),
                Accepted = accepted,
                RespondedAt = DateTime.UtcNow
            };

            var success = await _apiController.DeathrollRespondToInvitation(response);
            
            if (success)
            {
                _logger.LogDebug("Successfully responded to invitation {invitationId}: {accepted}", invitationId, accepted);
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to respond to invitation {invitationId}", invitationId);
            return false;
        }
    }
}