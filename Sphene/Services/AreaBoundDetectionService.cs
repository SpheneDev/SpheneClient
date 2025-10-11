using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Group;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using Sphene.API.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sphene.Services;

public class AreaBoundDetectionService : IDisposable
{
    private readonly ILogger<AreaBoundDetectionService> _logger;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly ApiController _apiController;
    private readonly SpheneMediator _mediator;
    private readonly IFramework _framework;
    
    private LocationInfo? _lastLocation;
    private readonly Dictionary<string, AreaBoundSyncshellDto> _areaBoundSyncshells = new();
    private readonly HashSet<string> _currentlyJoinedAreaSyncshells = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _locationMonitoringTask;
    private bool _isEnabled = true;
    
    public AreaBoundDetectionService(
        ILogger<AreaBoundDetectionService> logger,
        DalamudUtilService dalamudUtilService,
        ApiController apiController,
        SpheneMediator mediator,
        IFramework framework)
    {
        _logger = logger;
        _dalamudUtilService = dalamudUtilService;
        _apiController = apiController;
        _mediator = mediator;
        _framework = framework;
        
        StartLocationMonitoring();
    }
    
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                if (_isEnabled)
                {
                    StartLocationMonitoring();
                }
                else
                {
                    StopLocationMonitoring();
                }
            }
        }
    }
    
    public void UpdateAreaBoundSyncshells(IEnumerable<AreaBoundSyncshellDto> syncshells)
    {
        _areaBoundSyncshells.Clear();
        foreach (var syncshell in syncshells)
        {
            _areaBoundSyncshells[syncshell.GID] = syncshell;
        }
        _logger.LogDebug("Updated area-bound syncshells: {count}", _areaBoundSyncshells.Count);
    }
    
    private void StartLocationMonitoring()
    {
        if (_locationMonitoringTask != null && !_locationMonitoringTask.IsCompleted)
            return;
            
        _cancellationTokenSource = new CancellationTokenSource();
        _locationMonitoringTask = Task.Run(async () => await LocationMonitoringLoop(_cancellationTokenSource.Token));
        _logger.LogDebug("Started location monitoring");
    }
    
    private void StopLocationMonitoring()
    {
        _cancellationTokenSource?.Cancel();
        _logger.LogDebug("Stopped location monitoring");
    }
    
    private async Task LocationMonitoringLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _isEnabled)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                
                if (!_apiController.IsConnected)
                    continue;
                    
                var currentLocation = await _dalamudUtilService.GetMapDataAsync();
                
                if (_lastLocation == null || !LocationsEqual(_lastLocation.Value, currentLocation))
                {
                    _logger.LogDebug("Location changed: {location}", currentLocation);
                    await HandleLocationChange(currentLocation);
                    _lastLocation = currentLocation;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in location monitoring loop");
            }
        }
    }
    
    private async Task HandleLocationChange(LocationInfo newLocation)
    {
        // Request broadcast of area-bound syncshells for the new location
        try
        {
            await _apiController.BroadcastAreaBoundSyncshells(newLocation);
            _logger.LogDebug("Requested area-bound syncshell broadcast for location: {location}", newLocation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to request area-bound syncshell broadcast for location: {location}", newLocation);
        }

        var matchingSyncshells = GetMatchingAreaBoundSyncshells(newLocation);
        var currentlyMatching = matchingSyncshells.Select(s => s.GID).ToHashSet();
        
        // Leave syncshells that no longer match
        var toLeave = _currentlyJoinedAreaSyncshells.Except(currentlyMatching).ToList();
        foreach (var gid in toLeave)
        {
            await LeaveAreaBoundSyncshell(gid);
            _currentlyJoinedAreaSyncshells.Remove(gid);
        }
        
        // Join new matching syncshells
        var toJoin = currentlyMatching.Except(_currentlyJoinedAreaSyncshells).ToList();
        foreach (var gid in toJoin)
        {
            var syncshell = _areaBoundSyncshells[gid];
            if (await JoinAreaBoundSyncshell(syncshell))
            {
                _currentlyJoinedAreaSyncshells.Add(gid);
            }
        }
    }
    
    private List<AreaBoundSyncshellDto> GetMatchingAreaBoundSyncshells(LocationInfo location)
    {
        var matching = new List<AreaBoundSyncshellDto>();
        
        foreach (var syncshell in _areaBoundSyncshells.Values)
        {
            if (!syncshell.Settings.AutoBroadcastEnabled)
                continue;
                
            // Check if any of the syncshell's bound areas match the user's location
            bool hasMatchingArea = false;
            foreach (var boundArea in syncshell.BoundAreas)
            {
                bool matches = true;

                // Check server match (if specified)
                if (boundArea.Location.ServerId != 0 && boundArea.Location.ServerId != location.ServerId)
                    matches = false;

                // Check territory match (if specified)
                if (matches && boundArea.Location.TerritoryId != 0 && boundArea.Location.TerritoryId != location.TerritoryId)
                    matches = false;

                if (!matches) continue;

                // Apply matching mode logic
                switch (boundArea.MatchingMode)
                {
                    case AreaMatchingMode.ExactMatch:
                        matches = (boundArea.Location.MapId == 0 || boundArea.Location.MapId == location.MapId) &&
                                 (boundArea.Location.DivisionId == 0 || boundArea.Location.DivisionId == location.DivisionId) &&
                                 (boundArea.Location.WardId == 0 || boundArea.Location.WardId == location.WardId) &&
                                 (boundArea.Location.HouseId == 0 || boundArea.Location.HouseId == location.HouseId) &&
                                 (boundArea.Location.RoomId == 0 || boundArea.Location.RoomId == location.RoomId);
                        break;
                    case AreaMatchingMode.TerritoryOnly:
                        // Already checked territory above
                        break;
                    case AreaMatchingMode.ServerAndTerritory:
                        // Already checked server and territory above
                        break;
                    case AreaMatchingMode.HousingWardOnly:
                        matches = boundArea.Location.WardId == 0 || boundArea.Location.WardId == location.WardId;
                        break;
                    case AreaMatchingMode.HousingPlotOnly:
                        matches = (boundArea.Location.WardId == 0 || boundArea.Location.WardId == location.WardId) &&
                                 (boundArea.Location.HouseId == 0 || boundArea.Location.HouseId == location.HouseId);
                        break;
                }

                if (matches)
                {
                    hasMatchingArea = true;
                    break;
                }
            }
            
            if (hasMatchingArea)
            {
                matching.Add(syncshell);
            }
        }
        
        return matching;
    }
    
    private async Task<bool> JoinAreaBoundSyncshell(AreaBoundSyncshellDto syncshell)
    {
        try
        {
            _logger.LogDebug("Attempting to join area-bound syncshell: {gid}", syncshell.GID);
            await _apiController.AreaBoundJoinRequest(syncshell.GID);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to join area-bound syncshell: {gid}", syncshell.GID);
            return false;
        }
    }
    
    private async Task LeaveAreaBoundSyncshell(string gid)
    {
        try
        {
            _logger.LogDebug("Leaving area-bound syncshell: {gid}", gid);
            // Use regular group leave functionality
            await _apiController.GroupLeave(new GroupDto(new GroupData(gid)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to leave area-bound syncshell: {gid}", gid);
        }
    }
    
    private static bool LocationsEqual(LocationInfo loc1, LocationInfo loc2)
    {
        return loc1.ServerId == loc2.ServerId &&
               loc1.MapId == loc2.MapId &&
               loc1.TerritoryId == loc2.TerritoryId &&
               loc1.DivisionId == loc2.DivisionId &&
               loc1.WardId == loc2.WardId &&
               loc1.HouseId == loc2.HouseId &&
               loc1.RoomId == loc2.RoomId &&
               loc1.IsIndoor == loc2.IsIndoor;
    }
    
    public void Dispose()
    {
        StopLocationMonitoring();
        _cancellationTokenSource?.Dispose();
    }
}