using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Group;

namespace Sphene.Services;

public static class LocationDisplayService
{
    public static string GetLocationDisplayText(LocationInfo location, AreaMatchingMode? matchingMode = null)
    {
        var locationText = $"Server: {location.ServerId}, Territory: {location.TerritoryId}, Map: {location.MapId}";
        
        if (location.WardId > 0) locationText += $", Ward: {location.WardId}";
        if (location.HouseId > 0) locationText += $", House: {location.HouseId}";
        if (location.RoomId > 0) locationText += $", Room: {location.RoomId}";
        
        return locationText;
    }

    public static string GetLocationDisplayTextWithNames(LocationInfo location, DalamudUtilService dalamudUtilService, AreaMatchingMode? matchingMode = null)
    {
        // Get names for display
        var serverName = dalamudUtilService.WorldData.Value.TryGetValue((ushort)location.ServerId, out var sName) ? sName : $"Server {location.ServerId}";
        
        // For territory, get only the region name (first part before " - ")
        var territoryName = "Territory " + location.TerritoryId;
        if (dalamudUtilService.TerritoryData.Value.TryGetValue(location.TerritoryId, out var tName))
        {
            var regionName = tName.Split(" - ")[0]; // Take only the region part
            territoryName = regionName;
        }
        
        // For map, get the specific location name (everything after the region)
        var mapName = "Map " + location.MapId;
        if (dalamudUtilService.MapData.Value.TryGetValue(location.MapId, out var mData))
        {
            var fullMapName = mData.MapName;
            var parts = fullMapName.Split(" - ");
            if (parts.Length > 1)
            {
                // Skip the region part and join the rest (could be "PlaceName" or "PlaceName - PlaceNameSub")
                mapName = string.Join(" - ", parts.Skip(1));
            }
            else
            {
                mapName = fullMapName;
            }
        }

        var locationText = $"Server: {serverName}, Territory: {territoryName}, Map: {mapName}";
        
        if (location.WardId > 0) locationText += $", Ward: {location.WardId}";
        if (location.HouseId > 0) locationText += $", House: {location.HouseId}";
        if (location.RoomId > 0) locationText += $", Room: {location.RoomId}";
        
        return locationText;
    }
    
    public static string GetLocationDescriptiveName(LocationInfo location, AreaMatchingMode matchingMode)
    {
        return matchingMode switch
        {
            AreaMatchingMode.HousingPlotOutdoor => "Housing Plot (Outdoor)",
            AreaMatchingMode.HousingPlotIndoor => "House Interior",
            AreaMatchingMode.HousingPlotOnly => "Housing Plot (Any)",
            AreaMatchingMode.HousingWardOnly => "Housing Ward",
            AreaMatchingMode.TerritoryOnly => "Territory",
            AreaMatchingMode.ServerAndTerritory => "Server & Territory",
            AreaMatchingMode.ExactMatch => "Exact Location",
            _ => "Unknown"
        };
    }
    
    public static string GetAutoLocationName(LocationInfo location)
    {
        // Generate automatic location name based on location data
        if (location.WardId > 0 && location.HouseId > 0)
        {
            var houseName = location.HouseId == 100 ? "Apartments" : $"House {location.HouseId}";
            var indoorStatus = location.IsIndoor ? " (Interior)" : " (Plot)";
            return $"Ward {location.WardId}, {houseName}{indoorStatus}";
        }
        
        if (location.WardId > 0)
        {
            return $"Ward {location.WardId}";
        }
        
        return $"Territory {location.TerritoryId}";
    }
}
