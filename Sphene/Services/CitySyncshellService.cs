using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Group;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.WebAPI;
using Sphene.WebAPI.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sphene.Services;

public class CitySyncshellService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ILogger<CitySyncshellService> _logger;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SpheneMediator _mediator;
    private readonly IFramework _framework;
    private readonly SpheneConfigService _configService;
    private readonly ApiController _apiController;
    private readonly IServiceProvider _serviceProvider;
    private readonly AreaBoundSyncshellService _areaBoundSyncshellService;
    
    private LocationInfo? _lastLocation;
    private readonly Timer _locationCheckTimer;
    private readonly bool _isEnabled = true;
    
    // Define the main cities with their territory IDs
    private readonly Dictionary<uint, string> _mainCities = new()
    {
        { 129, "Limsa Lominsa" },
        { 132, "New Gridania" },
        { 130, "Ul'dah" }
    };

    public CitySyncshellService(ILogger<CitySyncshellService> logger, 
        SpheneMediator mediator, 
        DalamudUtilService dalamudUtilService, 
        IFramework framework,
        SpheneConfigService configService,
        ApiController apiController,
        IServiceProvider serviceProvider,
        AreaBoundSyncshellService areaBoundSyncshellService) : base(logger, mediator)
    {
        _logger = logger;
        _mediator = mediator;
        _dalamudUtilService = dalamudUtilService;
        _framework = framework;
        _configService = configService;
        _apiController = apiController;
        _serviceProvider = serviceProvider;
        _areaBoundSyncshellService = areaBoundSyncshellService;
        
        _locationCheckTimer = new Timer(CheckLocationChange, null, Timeout.Infinite, Timeout.Infinite);
        
        Mediator.Subscribe<CitySyncshellExplanationResponseMessage>(this, OnExplanationResponse);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting CitySyncshellService...");
        
        // Delay the start of location monitoring to allow UI components to initialize
        _locationCheckTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _locationCheckTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    private async void CheckLocationChange(object? state)
    {
        if (!_isEnabled) 
        {
            return;
        }

        try
        {
            var currentLocation = await _dalamudUtilService.GetMapDataAsync().ConfigureAwait(false);
            
            
            
            // Check if location changed
            if (_lastLocation == null || !LocationsEqual(_lastLocation.Value, currentLocation))
            {
                await HandleLocationChange(currentLocation).ConfigureAwait(false);
                _lastLocation = currentLocation;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking location change");
        }
    }

    private async Task HandleLocationChange(LocationInfo newLocation)
    {
        // Check if we entered a major city
        if (_mainCities.TryGetValue(newLocation.TerritoryId, out var cityName))
        {
            // Only show city syncshell explanation and join requests when connected to server
            if (!_apiController.IsConnected)
            {
                _logger.LogDebug("Not connected to server, skipping city syncshell processing for {cityName}", cityName);
                return;
            }
            
            // Always show explanation on first city entry, regardless of settings
            if (!_configService.Current.HasSeenCitySyncshellExplanation)
            {
                await _framework.RunOnFrameworkThread(() =>
                {
                    // Get the UI instance and directly open it (don't use toggle)
                    var windows = _serviceProvider.GetServices<WindowMediatorSubscriberBase>();
                    var citySyncshellUI = windows.OfType<Sphene.UI.Syncshell.CitySyncshellExplanationUI>().FirstOrDefault();
                    if (citySyncshellUI != null)
                    {
                        citySyncshellUI.ShowForCity(cityName);
                    }
                    else
                    {
                        _logger.LogWarning("CitySyncshellExplanationUI not found in service collection");
                    }
                }).ConfigureAwait(false);
                return; // Don't proceed with join logic until explanation is handled
            }
            
            // After explanation has been seen, check if user has area syncshell consent popups enabled
            if (!_configService.Current.AutoShowAreaBoundSyncshellConsent)
            {
                return;
            }
            
            // Proceed with city syncshell join logic
            await HandleCitySyncshellJoin(cityName).ConfigureAwait(false);
        }
    }

    private void OnExplanationResponse(CitySyncshellExplanationResponseMessage message)
    {
        _logger.LogDebug("OnExplanationResponse called for city: {cityName}, ShouldJoin: {shouldJoin}", message.CityName, message.ShouldJoin);
        
        if (message.ShouldJoin)
        {
            _logger.LogDebug("Calling TriggerAreaSyncshellSelection from CitySyncshellService");
            // Trigger area syncshell selection instead of directly joining a specific city syncshell
            _areaBoundSyncshellService.TriggerAreaSyncshellSelection();
            _logger.LogDebug("TriggerAreaSyncshellSelection call completed");
        }
    }

    private async Task HandleCitySyncshellJoin(string cityName)
    {
        try
        {
            // Get the city alias that matches the server's naming convention (now includes full server name)
            var cityAlias = await GetCityAliasAsync(cityName).ConfigureAwait(false);
            
            // Get all area-bound syncshells to find the city syncshell
            var areaBoundSyncshells = await _apiController.GroupGetAreaBoundSyncshells().ConfigureAwait(false);
            
            // Find the city syncshell by alias
            var citySyncshell = areaBoundSyncshells.FirstOrDefault(s => 
                string.Equals(s.Group.Alias, cityAlias, StringComparison.OrdinalIgnoreCase));
            
            if (citySyncshell != null)
            {
                // Check if user consent is required
                bool requiresRulesAcceptance = citySyncshell.Settings.RequireRulesAcceptance && 
                                             !string.IsNullOrEmpty(citySyncshell.Settings.JoinRules);
                
                if (requiresRulesAcceptance)
                {
                    try
                    {
                        // Check if user already has valid consent
                        var hasValidConsent = await _apiController.GroupCheckAreaBoundConsent(citySyncshell.GID).ConfigureAwait(false);
                        
                        if (hasValidConsent)
                        {
                            // Auto-join without showing consent UI
                            await _apiController.AreaBoundJoinRequest(citySyncshell.GID).ConfigureAwait(false);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error checking consent for city syncshell {SyncshellId}", citySyncshell.GID);
                    }
                    
                    // Show consent UI for rules acceptance
                    var consentMessage = new AreaBoundSyncshellConsentRequestMessage(citySyncshell, requiresRulesAcceptance);
                    await _framework.RunOnFrameworkThread(() =>
                    {
                        _mediator.Publish(consentMessage);
                    }).ConfigureAwait(false);
                    
                }
                else
                {
                    // No rules acceptance required - join directly
                    _logger.LogDebug("No rules acceptance required for city syncshell {cityName}, joining directly", cityName);
                    await _apiController.AreaBoundJoinRequest(citySyncshell.GID).ConfigureAwait(false);
                }
            }
            else
            {
                _logger.LogWarning("Could not find city syncshell for {cityName} (alias: {alias})", cityName, cityAlias);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining city syncshell for {cityName}", cityName);
        }
    }
    
    private async Task<string> GetCityAliasAsync(string cityName)
    {
        // Get the current world name
        var worldId = await _dalamudUtilService.GetWorldIdAsync().ConfigureAwait(false);
        var worldName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)worldId, out var name) ? name : $"World{worldId}";
        
        // Get the base city alias
        var baseAlias = cityName switch
        {
            "Limsa Lominsa" => "Limsa",
            "New Gridania" => "Gridania", 
            "Ul'dah" => "Uldah",
            _ => cityName.Substring(0, Math.Min(10, cityName.Length))
        };
        
        // Return the full alias with server name (matching server's new naming pattern)
        return $"{baseAlias} {worldName}";
    }

    private static bool LocationsEqual(LocationInfo loc1, LocationInfo loc2)
    {
        return loc1.ServerId == loc2.ServerId &&
               loc1.TerritoryId == loc2.TerritoryId &&
               loc1.MapId == loc2.MapId &&
               loc1.WardId == loc2.WardId &&
               loc1.HouseId == loc2.HouseId &&
               loc1.RoomId == loc2.RoomId &&
               loc1.IsIndoor == loc2.IsIndoor;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _locationCheckTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
