using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;

namespace Sphene.Services;

public class DeathrollChatHandler : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ILogger<DeathrollChatHandler> _logger;
    private readonly DeathrollService _deathrollService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IChatGui _chatGui;
    
    // Regex patterns for detecting dice rolls and commands
    private static readonly Regex DiceRollPattern = new(@"^Random! You roll a? (\d+)\.?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeathrollCommandPattern = new(@"^/deathroll\s+(accept|start|cancel|status)(?:\s+(.*))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    public DeathrollChatHandler(
        ILogger<DeathrollChatHandler> logger,
        SpheneMediator mediator,
        DeathrollService deathrollService,
        DalamudUtilService dalamudUtilService,
        IChatGui chatGui) : base(logger, mediator)
    {
        _logger = logger;
        _deathrollService = deathrollService;
        _dalamudUtilService = dalamudUtilService;
        _chatGui = chatGui;
        
        _logger.LogDebug("DeathrollChatHandler initialized");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _chatGui.ChatMessage += OnChatMessage;
        _logger.LogDebug("DeathrollChatHandler started and subscribed to chat messages");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _chatGui.ChatMessage -= OnChatMessage;
        _logger.LogDebug("DeathrollChatHandler stopped and unsubscribed from chat messages");
        return Task.CompletedTask;
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        try
        {
            var messageText = message.TextValue;
            var senderName = sender.TextValue;
            
            // Debug logging to see all chat messages
            _logger.LogDebug("Chat message received - Type: {type}, Sender: {sender}, Message: {message}", type, senderName, messageText);
            
            // Only process certain chat types
            if (!IsRelevantChatType(type))
            {
                _logger.LogDebug("Ignoring chat type: {type}", type);
                return;
            }

            // Process dice rolls and commands asynchronously without blocking the chat handler
            _ = Task.Run(async () =>
            {
                try
                {
                    // Check for dice roll patterns
                    if (await ProcessDiceRollAsync(messageText, senderName))
                        return;

                    // Check for deathroll commands
                    await ProcessDeathrollCommandAsync(messageText, senderName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing chat message asynchronously");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnChatMessage handler");
        }
    }

    private async Task<bool> ProcessDiceRollAsync(string messageText, string senderName)
    {
        var match = DiceRollPattern.Match(messageText);
        if (!match.Success)
            return false;

        // Only process own rolls - "Random! You roll a X."
        if (!int.TryParse(match.Groups[1].Value, out int rollResult))
            return false;
            
        // Get the current player name from Dalamud
        var playerName = await _dalamudUtilService.RunOnFrameworkThread(() =>
            _dalamudUtilService.GetPlayerName()).ConfigureAwait(false);
            
        _logger.LogDebug("Detected own dice roll: {player} rolled {result}", playerName, rollResult);

        // Get the expected roll max from the current game state
        var currentGame = _deathrollService.GetCurrentGame();
        if (currentGame?.State != DeathrollGameState.InProgress)
        {
            _logger.LogDebug("Ignoring dice roll - no active game");
            return false;
        }

        var rollMax = currentGame.CurrentRollMax;

        _logger.LogDebug("Processing dice roll: {player} rolled {result} (expected max: {max})", playerName, rollResult, rollMax);

        // Process the roll through the deathroll service
        return await _deathrollService.ProcessDiceRollAsync(playerName, rollResult, rollMax);
    }

    private async Task<bool> ProcessDeathrollCommandAsync(string messageText, string senderName)
    {
        var match = DeathrollCommandPattern.Match(messageText);
        if (!match.Success)
            return false;

        var command = match.Groups[1].Value.ToLowerInvariant();
        var args = match.Groups[2].Value;

        _logger.LogDebug("Detected deathroll command: {command} from {player}", command, senderName);

        switch (command)
        {
            case "accept":
                await _deathrollService.AcceptInvitationAsync(senderName);
                return true;
                
            case "start":
                return await HandleStartCommandAsync(senderName, args);
                
            case "cancel":
                _deathrollService.CancelCurrentGame();
                return true;
                
            case "status":
                await HandleStatusCommandAsync();
                return true;
                
            default:
                return false;
        }
    }

    private async Task<bool> HandleStartCommandAsync(string senderName, string args)
    {
        // Get nearby players
        var nearbyPlayers = await GetNearbyPlayersAsync();
        
        if (nearbyPlayers.Count < 2)
        {
            _logger.LogDebug("Not enough nearby players for Deathroll game");
            return false;
        }

        return await _deathrollService.StartNewGameAsync(nearbyPlayers);
    }

    private async Task HandleStatusCommandAsync()
    {
        var status = _deathrollService.GetGameStatus();
        foreach (var line in status)
        {
            _logger.LogInformation("Deathroll Status: {status}", line);
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
                var distance = System.Numerics.Vector3.Distance(playerPosition, character.Position);
                if (distance <= maxDistance)
                {
                    nearbyPlayers.Add(characterName);
                }
            }
            
            _logger.LogDebug("Found {count} nearby players for Deathroll", nearbyPlayers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting nearby players for Deathroll");
        }
        
        return nearbyPlayers;
    }

    private static bool IsRelevantChatType(XivChatType type)
    {
        return type switch
        {
            XivChatType.Say => true,
            XivChatType.Yell => true,
            XivChatType.Shout => true,
            XivChatType.Party => true,
            XivChatType.Alliance => true,
            XivChatType.FreeCompany => true,
            XivChatType.TellIncoming => true,
            XivChatType.TellOutgoing => true,
            XivChatType.Echo => true,  // For /random command output
            XivChatType.SystemMessage => true,  // Alternative for /random command output
            (XivChatType)2122 => true,  // Actual chat type for /random command output (own rolls)
            (XivChatType)8266 => true,  // Chat type for other players' roll messages
            _ => false
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chatGui.ChatMessage -= OnChatMessage;
            _logger.LogDebug("DeathrollChatHandler disposed and unsubscribed from chat messages");
        }
        base.Dispose(disposing);
    }
}