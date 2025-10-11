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
    
    private LocationInfo? _lastLocation;
    private readonly Timer _locationCheckTimer;
    private bool _isEnabled = true;
    
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
        IServiceProvider serviceProvider) : base(logger, mediator)
    {
        _logger = logger;
        _dalamudUtilService = dalamudUtilService;
        _mediator = mediator;
        _framework = framework;
        _configService = configService;
        _apiController = apiController;
        _serviceProvider = serviceProvider;
        
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
            
            // Skip if current location is null
            if (currentLocation == null) 
            {
                return;
            }
            
            // Check if location changed
            if (_lastLocation == null || !LocationsEqual(_lastLocation.Value, currentLocation))
            {
                await HandleLocationChange(_lastLocation, currentLocation).ConfigureAwait(false);
                _lastLocation = currentLocation;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking location change");
        }
    }

    private async Task HandleLocationChange(LocationInfo? oldLocation, LocationInfo newLocation)
    {
        // Check if we entered a major city
        if (_mainCities.TryGetValue(newLocation.TerritoryId, out var cityName))
        {
            // Always show explanation on first city entry, regardless of settings
            if (!_configService.Current.HasSeenCitySyncshellExplanation)
            {
                await _framework.RunOnFrameworkThread(() =>
                {
                    // Get the UI instance and directly open it (don't use toggle)
                    var windows = _serviceProvider.GetServices<WindowMediatorSubscriberBase>();
                    var citySyncshellUI = windows.OfType<UI.CitySyncshellExplanationUI>().FirstOrDefault();
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
            
            // After explanation has been seen, check if user has city syncshell requests enabled
            if (!_configService.Current.EnableCitySyncshellJoinRequests)
            {
                return;
            }
            
            // Proceed with city syncshell join logic
            await HandleCitySyncshellJoin(cityName).ConfigureAwait(false);
        }
    }

    private void OnExplanationResponse(CitySyncshellExplanationResponseMessage message)
    {
        if (message.ShouldJoin)
        {
            _ = Task.Run(async () => await HandleCitySyncshellJoin(message.CityName).ConfigureAwait(false));
        }
    }

    private async Task HandleCitySyncshellJoin(string cityName)
    {
        try
        {
            // Get the city alias that matches the server's naming convention
            var cityAlias = GetCityAlias(cityName);
            
            // Get all area-bound syncshells to find the city syncshell
            var areaBoundSyncshells = await _apiController.GroupGetAreaBoundSyncshells();
            
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
                        var hasValidConsent = await _apiController.GroupCheckAreaBoundConsent(citySyncshell.GID);
                        
                        if (hasValidConsent)
                        {
                            // Auto-join without showing consent UI
                            await _apiController.AreaBoundJoinRequest(citySyncshell.GID);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error checking consent for city syncshell {SyncshellId}", citySyncshell.GID);
                    }
                }
                
                // Send consent request to show the normal area-bound syncshell UI
                var consentMessage = new AreaBoundSyncshellConsentRequestMessage(citySyncshell, requiresRulesAcceptance);
                await _framework.RunOnFrameworkThread(() =>
                {
                    _mediator.Publish(consentMessage);
                }).ConfigureAwait(false);
                
                _logger.LogInformation("Sent consent request for city syncshell {cityName}", cityName);
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
    
    private static string GetCityAlias(string cityName)
    {
        return cityName switch
        {
            "Limsa Lominsa" => "Limsa",
            "New Gridania" => "Gridania",
            "Ul'dah" => "Uldah",
            _ => cityName.Substring(0, Math.Min(10, cityName.Length))
        };
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

public record CitySyncshellExplanationRequestMessage(string CityName) : MessageBase;
public record CitySyncshellExplanationResponseMessage(string CityName, bool ShouldJoin) : MessageBase;