using Sphene.API.Dto.CharaData;

namespace Sphene.SpheneConfiguration.Models;

[Serializable]
public class VerifiedHousingProperty
{
    public LocationInfo Location { get; set; } = new();
    public bool AllowOutdoor { get; set; } = true;
    public bool AllowIndoor { get; set; } = true;
    public bool PreferOutdoorSyncshells { get; set; } = true;
    public bool PreferIndoorSyncshells { get; set; } = true;
    
    public VerifiedHousingProperty()
    {
    }
    
    public VerifiedHousingProperty(LocationInfo location, bool allowOutdoor = true, bool allowIndoor = true, bool preferOutdoorSyncshells = true, bool preferIndoorSyncshells = true)
    {
        Location = location;
        AllowOutdoor = allowOutdoor;
        AllowIndoor = allowIndoor;
        PreferOutdoorSyncshells = preferOutdoorSyncshells;
        PreferIndoorSyncshells = preferIndoorSyncshells;
    }
}