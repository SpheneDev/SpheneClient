using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.Services.Mediator;
using Sphene.PlayerData.Pairs;
using Sphene.PlayerData.Factories;
using Sphene.PlayerData.Handlers;
using Sphene.API.Data.Enum;
using Dalamud.Game.ClientState.Objects.SubKinds;
using System.Collections.Concurrent;

namespace Sphene.Services;

public sealed class CharacterHashTracker : IHostedService, IDisposable
{
    private readonly ILogger<CharacterHashTracker> _logger;
    private readonly SpheneMediator _mediator;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly PlayerDataFactory _playerDataFactory;
    private readonly ConcurrentDictionary<string, string> _lastKnownHashes = new();
    private readonly ConcurrentDictionary<string, DateTime> _hashChangeTimestamps = new();
    private readonly Timer _hashCheckTimer;
    private string? _currentPlayerHash;
    private bool _disposed;

    public CharacterHashTracker(ILogger<CharacterHashTracker> logger, SpheneMediator mediator, PairManager pairManager, 
        DalamudUtilService dalamudUtilService, GameObjectHandlerFactory gameObjectHandlerFactory, PlayerDataFactory playerDataFactory)
    {
        _logger = logger;
        _mediator = mediator;
        _pairManager = pairManager;
        _dalamudUtilService = dalamudUtilService;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _playerDataFactory = playerDataFactory;
        _hashCheckTimer = new Timer(CheckForHashChanges, null, Timeout.Infinite, Timeout.Infinite);
    }

    public string? CurrentPlayerHash => _currentPlayerHash;
    
    public bool HasHashChanged(string playerIdentifier, string newHash)
    {
        if (!_lastKnownHashes.TryGetValue(playerIdentifier, out var lastHash))
        {
            _lastKnownHashes[playerIdentifier] = newHash;
            return true;
        }
        
        if (!string.Equals(lastHash, newHash, StringComparison.Ordinal))
        {
            _lastKnownHashes[playerIdentifier] = newHash;
            _hashChangeTimestamps[playerIdentifier] = DateTime.UtcNow;
            return true;
        }
        
        return false;
    }
    
    public void UpdateCurrentPlayerHash(CharacterData characterData)
    {
        var newHash = characterData.DataHash.Value;
        if (!string.Equals(_currentPlayerHash, newHash, StringComparison.Ordinal))
        {
            var oldHash = _currentPlayerHash;
            _currentPlayerHash = newHash;
            _hashChangeTimestamps["self"] = DateTime.UtcNow;
            
            _logger.LogDebug("Player hash changed from {oldHash} to {newHash}", oldHash ?? "null", newHash);
            
            // Publish hash change event
            _mediator.Publish(new PlayerHashChangedMessage(oldHash, newHash));
        }
    }
    
    public DateTime? GetLastHashChangeTime(string playerIdentifier)
    {
        return _hashChangeTimestamps.TryGetValue(playerIdentifier, out var timestamp) ? timestamp : null;
    }
    
    // Gets the current hash for a specific player
    public string GetPlayerHash(string playerIdentifier)
    {
        return _lastKnownHashes.TryGetValue(playerIdentifier, out var hash) ? hash : string.Empty;
    }

    // Gets the current player's hash
    public string GetCurrentPlayerHash()
    {
        // TODO: Get current player identifier from game state
        var currentPlayerIdentifier = "current_player"; // Placeholder
        return GetPlayerHash(currentPlayerIdentifier);
    }
    
    public bool IsHashRecentlyChanged(string playerIdentifier, TimeSpan threshold)
    {
        var changeTime = GetLastHashChangeTime(playerIdentifier);
        return changeTime.HasValue && DateTime.UtcNow - changeTime.Value < threshold;
    }
    
    private void CheckForHashChanges(object? state)
    {
        try
        {
            // Check if we need to trigger acknowledgment requests for recently changed hashes
            var recentlyChangedPlayers = _hashChangeTimestamps
                .Where(kvp => DateTime.UtcNow - kvp.Value < TimeSpan.FromSeconds(30))
                .Select(kvp => kvp.Key)
                .ToList();
                
            if (recentlyChangedPlayers.Any())
            {
                _mediator.Publish(new TriggerAcknowledgmentRequestMessage(recentlyChangedPlayers));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during hash change check");
        }
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting CharacterHashTracker");
        _hashCheckTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }
    
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping CharacterHashTracker");
        _hashCheckTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _hashCheckTimer?.Dispose();
            _disposed = true;
        }
    }
    
    // Manually triggers a hash change check
    public void CheckForHashChanges()
    {
        _logger.LogInformation("Manual hash change check triggered");
        
        // Get current character data and calculate hash - run on framework thread
        _ = Task.Run(async () =>
        {
            try
            {
                var currentCharacterDataTask = await _dalamudUtilService.RunOnFrameworkThread(() =>
                {
                    return GetCurrentCharacterDataAsync();
                }).ConfigureAwait(false);
                
                var currentCharacterData = await currentCharacterDataTask.ConfigureAwait(false);
                
                if (currentCharacterData != null)
                {
                    var newHash = currentCharacterData.DataHash.Value;
                    UpdateCurrentPlayerHash(currentCharacterData);
                }
                else
                {
                    _logger.LogWarning("Could not retrieve current character data for manual hash check");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during manual hash change check");
            }
        });
    }
    
    // Helper method to get current character data
    private async Task<CharacterData?> GetCurrentCharacterDataAsync()
    {
        try
        {
            var chara = await _dalamudUtilService.GetPlayerCharacterAsync().ConfigureAwait(false);
            if (_dalamudUtilService.IsInGpose)
            {
                chara = (IPlayerCharacter?)(await _dalamudUtilService.GetGposeCharacterFromObjectTableByNameAsync(chara.Name.TextValue, _dalamudUtilService.IsInGpose).ConfigureAwait(false));
            }

            if (chara == null)
                return null;

            using var tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player,
                            () => _dalamudUtilService.GetCharacterFromObjectTableByIndex(chara.ObjectIndex)?.Address ?? IntPtr.Zero, isWatched: false).ConfigureAwait(false);
            
            PlayerData.Data.CharacterData newCdata = new();
            var fragment = await _playerDataFactory.BuildCharacterData(tempHandler, CancellationToken.None).ConfigureAwait(false);
            
            if (fragment == null)
                return null;
                
            newCdata.SetFragment(ObjectKind.Player, fragment);
            
            // Filter out unwanted file types (same as in CharaDataFileHandler)
            if (newCdata.FileReplacements.TryGetValue(ObjectKind.Player, out var playerData) && playerData != null)
            {
                foreach (var data in playerData.Select(g => g.GamePaths))
                {
                    data.RemoveWhere(g => g.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)
                        || g.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)
                        || g.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)
                        || (g.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)
                            && !g.Contains("/weapon/", StringComparison.OrdinalIgnoreCase)
                            && !g.Contains("/equipment/", StringComparison.OrdinalIgnoreCase))
                        || (g.EndsWith(".atex", StringComparison.OrdinalIgnoreCase)
                            && !g.Contains("/weapon/", StringComparison.OrdinalIgnoreCase)
                            && !g.Contains("/equipment/", StringComparison.OrdinalIgnoreCase)));
                }

                playerData.RemoveWhere(g => g.GamePaths.Count == 0);
            }
            
            // Use the ToAPI() method to convert to API.Data.CharacterData
            return newCdata.ToAPI();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current character data");
            return null;
        }
    }
    

}

// Message classes for mediator pattern
public record PlayerHashChangedMessage(string? OldHash, string NewHash) : MessageBase;
public record TriggerAcknowledgmentRequestMessage(List<string> PlayerIdentifiers) : MessageBase;