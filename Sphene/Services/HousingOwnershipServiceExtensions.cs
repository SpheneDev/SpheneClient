using Sphene.API.Dto.CharaData;
using Sphene.SpheneConfiguration.Models;

namespace Sphene.Services;

public static class HousingOwnershipServiceExtensions
{
    // Check if a location is allowed based on outdoor/indoor preferences
    public static bool IsLocationAllowed(this VerifiedHousingProperty property, LocationInfo currentLocation)
    {
        // First check if the locations match (same housing area)
        if (!LocationsMatch(property.Location, currentLocation))
        {
            return false;
        }
            
        // Then check outdoor/indoor preferences
        if (currentLocation.IsIndoor && !property.AllowIndoor)
        {
            return false;
        }
            
        if (!currentLocation.IsIndoor && !property.AllowOutdoor)
        {
            return false;
        }
            
        return true;
    }
    
    // Check if a verified property allows the current location based on outdoor/indoor status
    public static bool IsLocationAllowedForAreaSyncshell(this VerifiedHousingProperty property, LocationInfo syncshellLocation)
    {
        // Check if the locations match (same housing area)
        if (!LocationsMatch(property.Location, syncshellLocation))
            return false;
            
        // Check outdoor/indoor preferences for the syncshell location
        if (syncshellLocation.IsIndoor && !property.AllowIndoor)
            return false;
            
        if (!syncshellLocation.IsIndoor && !property.AllowOutdoor)
            return false;
            
        return true;
    }

    private static bool LocationsMatch(LocationInfo loc1, LocationInfo loc2)
    {
        return loc1.ServerId == loc2.ServerId &&
               loc1.TerritoryId == loc2.TerritoryId &&
               loc1.WardId == loc2.WardId &&
               loc1.HouseId == loc2.HouseId &&
               loc1.RoomId == loc2.RoomId;
    }
}
