using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Group;

namespace Sphene.Interop.Ipc;

/// <summary>
/// Groups a Syncshell's bound-area rows into the distinct travel properties
/// they describe. Interior floors and extra rooms of one house collapse to a
/// single candidate (a house destination is the plot); each apartment unit -
/// identified by its room - stays a separate candidate. Rows that cannot
/// resolve to a valid housing destination are dropped.
/// </summary>
public static class EventDestinationGrouping
{
    /// <summary>Bound locations → validated property candidates, deduped and
    /// ordered deterministically.</summary>
    public static List<EventDestinationDto> PropertyCandidates(
        IEnumerable<LocationInfo> locations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<EventDestinationDto>();
        foreach (var loc in locations)
        {
            // Houses share one address for every interior floor/room - the
            // plot is the destination. Apartment units differ per room.
            var key = loc.HouseId == LifestreamAddressMapping.ApartmentHouseId
                ? $"a:{loc.ServerId}:{loc.TerritoryId}:{loc.WardId}:{loc.RoomId}"
                : $"h:{loc.ServerId}:{loc.TerritoryId}:{loc.WardId}:{loc.HouseId}";
            if (!seen.Add(key)) continue;
            var dest = LifestreamAddressMapping.FromLocationInfo(loc);
            if (dest != null)
            {
                result.Add(dest);
            }
        }
        result.Sort(static (a, b) => a.WorldId != b.WorldId
            ? a.WorldId.CompareTo(b.WorldId)
            : a.Ward != b.Ward ? a.Ward.CompareTo(b.Ward)
            : a.Plot != b.Plot ? a.Plot.CompareTo(b.Plot)
            : a.Room.CompareTo(b.Room));
        return result;
    }
}
