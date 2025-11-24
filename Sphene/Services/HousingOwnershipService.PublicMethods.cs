using Microsoft.Extensions.Logging;
using Sphene.API.Dto.CharaData;
using Sphene.SpheneConfiguration.Models;
using Sphene.Services;

namespace Sphene.Services;

public partial class HousingOwnershipService
{
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
}
