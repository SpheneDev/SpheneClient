using FFXIVClientStructs.FFXIV.Client.Game;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Group;
using Sphene.SpheneConfiguration.Models;
using Sphene.Services;

namespace Sphene.Services;

public partial class HousingOwnershipService
{
    public readonly record struct DetectedHousingProperty(string Source, VerifiedHousingProperty Property);

    // Public method to add verified property with outdoor/indoor preferences and syncshell preferences
    public async Task AddVerifiedOwnedPropertyWithPreferences(LocationInfo location, bool allowOutdoor, bool allowIndoor, bool preferOutdoorSyncshells = true, bool preferIndoorSyncshells = true)
    {
        await AddVerifiedOwnedProperty(location, allowOutdoor, allowIndoor, preferOutdoorSyncshells, preferIndoorSyncshells).ConfigureAwait(false);
    }
    
    // Check if a location is verified and allowed for area syncshells based on outdoor/indoor preferences
    public bool IsLocationVerifiedAndAllowed(LocationInfo location)
    {
        var verifiedProperties = GetVerifiedOwnedProperties();
        
        _logger.LogDebug("Checking location verification for {Location} (IsIndoor: {IsIndoor})", location, location.IsIndoor);
        _logger.LogDebug("Found {Count} verified properties", verifiedProperties.Count);
        
        foreach (var property in verifiedProperties)
        {
            _logger.LogDebug("Property: Ward {Ward}, House {House}, Room {Room} - AllowIndoor: {AllowIndoor}, AllowOutdoor: {AllowOutdoor}", 
                property.Location.WardId, property.Location.HouseId, property.Location.RoomId, 
                property.AllowIndoor, property.AllowOutdoor);
                
            if (property.IsLocationAllowed(location))
            {
                _logger.LogDebug("Location is allowed by property: Ward {Ward}, House {House}, Room {Room}", 
                    property.Location.WardId, property.Location.HouseId, property.Location.RoomId);
                return true;
            }
        }
        
        _logger.LogDebug("Location is not allowed by any verified property");
        return false;
    }
    
    // Get verified properties that allow a specific outdoor/indoor mode
    public List<VerifiedHousingProperty> GetVerifiedPropertiesForMode(bool isIndoor)
    {
        var verifiedProperties = GetVerifiedOwnedProperties();
        return verifiedProperties.Where(property => 
            isIndoor ? property.AllowIndoor : property.AllowOutdoor).ToList();
    }
    
    // Update outdoor/indoor preferences for an existing verified property
    public async Task UpdateVerifiedPropertyPreferences(LocationInfo location, bool allowOutdoor, bool allowIndoor, bool preferOutdoorSyncshells = true, bool preferIndoorSyncshells = true)
    {
        // Immediately update local cache for instant UI feedback
        if (_serverHousingProperties != null)
        {
            var existingProperty = _serverHousingProperties.FirstOrDefault(p => LocationsMatch(p.Location, location));
            if (existingProperty != null)
            {
                existingProperty.AllowOutdoor = allowOutdoor;
                existingProperty.AllowIndoor = allowIndoor;
                existingProperty.PreferOutdoorSyncshells = preferOutdoorSyncshells;
                existingProperty.PreferIndoorSyncshells = preferIndoorSyncshells;
                _logger.LogDebug("Immediately updated local cache for property: Ward {Ward}, House {House}, Room {Room}", 
                    location.WardId, location.HouseId, location.RoomId);
            }
        }
        
        // Use the server-first approach
        await AddVerifiedOwnedProperty(location, allowOutdoor, allowIndoor, preferOutdoorSyncshells, preferIndoorSyncshells).ConfigureAwait(false);
        
        // Force immediate cache refresh to update UI
        _lastServerSync = DateTime.MinValue;
        await SyncWithServer().ConfigureAwait(false);
        
        _logger.LogInformation("Updated verified property preferences: Ward {Ward}, House {House}, Room {Room}, Outdoor: {Outdoor}, Indoor: {Indoor}, PreferOutdoor: {PreferOutdoor}, PreferIndoor: {PreferIndoor}", 
            location.WardId, location.HouseId, location.RoomId, allowOutdoor, allowIndoor, preferOutdoorSyncshells, preferIndoorSyncshells);
    }
    
    // Public method to add verified room property without outdoor/indoor preferences
    public async Task AddVerifiedOwnedRoom(LocationInfo location)
    {
        await AddVerifiedOwnedPropertyForRoom(location).ConfigureAwait(false);
    }
    
    // Force a refresh of verified properties from the server
    public async Task ForceRefreshFromServer()
    {
        _logger.LogDebug("Forcing refresh of verified properties from server");
        _lastServerSync = DateTime.MinValue; // Reset sync time to force refresh
        await SyncWithServer().ConfigureAwait(false);
        _logger.LogDebug("Forced refresh completed. Properties count: {Count}", _serverHousingProperties?.Count ?? 0);
    }

    public bool IsLocationVerified(LocationInfo location)
    {
        var verifiedProperties = GetVerifiedOwnedProperties();
        foreach (var property in verifiedProperties)
        {
            if (LocationsMatchPublic(property.Location, location))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<List<DetectedHousingProperty>> GetDetectedOwnedHousingPropertiesAsync()
    {
        return await _dalamudUtilService.RunOnFrameworkThread(GetDetectedOwnedHousingProperties).ConfigureAwait(false);
    }

    public void SuspendAreaBindingLocations(string groupGid, LocationInfo propertyLocation, List<AreaBoundLocationDto> locations)
    {
        if (string.IsNullOrWhiteSpace(groupGid) || locations.Count == 0)
        {
            return;
        }

        var propKey = GetPropertyKey(propertyLocation);
        if (!_configService.Current.SuspendedAreaBoundLocationsByGroupAndProperty.TryGetValue(groupGid, out var groupMap))
        {
            groupMap = new Dictionary<string, List<AreaBoundLocationDto>>(StringComparer.Ordinal);
            _configService.Current.SuspendedAreaBoundLocationsByGroupAndProperty[groupGid] = groupMap;
        }

        if (!groupMap.TryGetValue(propKey, out var list))
        {
            list = new List<AreaBoundLocationDto>();
            groupMap[propKey] = list;
        }

        foreach (var loc in locations)
        {
            if (!list.Any(x => LocationsEqualForSuspend(x.Location, loc.Location) && x.MatchingMode == loc.MatchingMode))
            {
                list.Add(loc);
            }
        }

        _configService.Save();
    }

    public List<AreaBoundLocationDto> RestoreSuspendedAreaBindingLocations(string groupGid, LocationInfo propertyLocation)
    {
        if (string.IsNullOrWhiteSpace(groupGid))
        {
            return [];
        }

        var propKey = GetPropertyKey(propertyLocation);
        if (!_configService.Current.SuspendedAreaBoundLocationsByGroupAndProperty.TryGetValue(groupGid, out var groupMap))
        {
            return [];
        }

        if (!groupMap.TryGetValue(propKey, out var list) || list.Count == 0)
        {
            return [];
        }

        groupMap.Remove(propKey);
        if (groupMap.Count == 0)
        {
            _configService.Current.SuspendedAreaBoundLocationsByGroupAndProperty.Remove(groupGid);
        }

        _configService.Save();
        return list.ToList();
    }

    public List<AreaBoundLocationDto> GetSuspendedAreaBindingLocations(string groupGid, LocationInfo propertyLocation)
    {
        if (string.IsNullOrWhiteSpace(groupGid))
        {
            return [];
        }

        var propKey = GetPropertyKey(propertyLocation);
        if (!_configService.Current.SuspendedAreaBoundLocationsByGroupAndProperty.TryGetValue(groupGid, out var groupMap))
        {
            return [];
        }

        if (!groupMap.TryGetValue(propKey, out var list) || list.Count == 0)
        {
            return [];
        }

        return list.ToList();
    }

    private static List<DetectedHousingProperty> GetDetectedOwnedHousingProperties()
    {
        var result = new List<DetectedHousingProperty>();

        AddDetected(result, "FreeCompanyEstate", HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate));
        AddDetected(result, "PersonalChambers", HousingManager.GetOwnedHouseId(EstateType.PersonalChambers));
        AddDetected(result, "PersonalEstate", HousingManager.GetOwnedHouseId(EstateType.PersonalEstate));
        AddDetected(result, "SharedEstate #0", HousingManager.GetOwnedHouseId(EstateType.SharedEstate, 0));
        AddDetected(result, "SharedEstate #1", HousingManager.GetOwnedHouseId(EstateType.SharedEstate, 1));
        AddDetected(result, "ApartmentRoom", HousingManager.GetOwnedHouseId(EstateType.ApartmentRoom));

        var dedup = new Dictionary<string, DetectedHousingProperty>(StringComparer.Ordinal);
        foreach (var entry in result)
        {
            var key = $"{entry.Property.Location.ServerId}_{entry.Property.Location.TerritoryId}_{entry.Property.Location.WardId}_{entry.Property.Location.HouseId}_{entry.Property.Location.RoomId}";
            dedup.TryAdd(key, entry);
        }

        return dedup.Values.ToList();
    }

    private static void AddDetected(List<DetectedHousingProperty> list, string source, HouseId houseId)
    {
        if (houseId.Id == 0 || houseId.WorldId == 65535)
        {
            return;
        }

        var wardId = (uint)(houseId.WardIndex + 1);
        var territoryId = (uint)houseId.TerritoryTypeId;
        var serverId = (uint)houseId.WorldId;

        var isApartment = houseId.IsApartment;
        var isRoom = !houseId.IsWorkshop && houseId.RoomNumber > 0;

        var divisionId = isApartment ? (uint)houseId.ApartmentDivision : 0u;
        var housePlotId = isApartment ? 100u : (uint)(houseId.PlotIndex + 1);
        var roomId = isRoom ? (uint)houseId.RoomNumber : 0u;

        var location = new LocationInfo
        {
            ServerId = serverId,
            TerritoryId = territoryId,
            WardId = wardId,
            HouseId = housePlotId,
            RoomId = roomId,
            DivisionId = divisionId,
            IsIndoor = isApartment || isRoom
        };

        var allowOutdoor = !(isApartment || isRoom);
        var allowIndoor = true;

        list.Add(new DetectedHousingProperty(source, new VerifiedHousingProperty(location, allowOutdoor, allowIndoor)));
    }

    private static bool LocationsMatchPublic(LocationInfo loc1, LocationInfo loc2)
    {
        return loc1.ServerId == loc2.ServerId
               && loc1.TerritoryId == loc2.TerritoryId
               && loc1.WardId == loc2.WardId
               && loc1.HouseId == loc2.HouseId
               && loc1.RoomId == loc2.RoomId;
    }

    private static string GetPropertyKey(LocationInfo location)
    {
        return $"{location.ServerId}_{location.TerritoryId}_{location.WardId}_{location.HouseId}_{location.RoomId}";
    }

    private static bool LocationsEqualForSuspend(LocationInfo loc1, LocationInfo loc2)
    {
        return loc1.ServerId == loc2.ServerId
               && loc1.MapId == loc2.MapId
               && loc1.TerritoryId == loc2.TerritoryId
               && loc1.DivisionId == loc2.DivisionId
               && loc1.WardId == loc2.WardId
               && loc1.HouseId == loc2.HouseId
               && loc1.RoomId == loc2.RoomId
               && loc1.IsIndoor == loc2.IsIndoor;
    }
}
