// Mirrors Lifestream's `AddressBookEntryTuple` field order exactly
// (Lifestream/Data/AddressBookEntry.cs) - the IPC boundary is the ValueTuple
// shape, never the field names.
global using LifestreamAddressTuple = (string Name, int World, int City, int Ward,
    int PropertyType, int Plot, int Apartment, bool ApartmentSubdivision,
    bool AliasEnabled, string Alias);

using Sphene.API.Data.Enum;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Group;

namespace Sphene.Interop.Ipc;

/// <summary>
/// Pure conversion between Sphene location data and Lifestream's
/// <c>AddressBookEntryTuple</c>. Fails closed: unknown districts, incomplete
/// addresses and non-housing destinations produce null, never guessed values.
/// No Dalamud dependencies - the unit tests exercise this file directly.
/// </summary>
public static class LifestreamAddressMapping
{
    /// <summary>Lifestream <c>ResidentialAetheryteKind</c> int values
    /// (aetheryte row ids, not a sequential district index).</summary>
    public static int CityCodeFor(ResidentialDistrict district) => district switch
    {
        ResidentialDistrict.LavenderBeds => 2,
        ResidentialDistrict.Mist => 8,
        ResidentialDistrict.Goblet => 9,
        ResidentialDistrict.Empyreum => 70,
        ResidentialDistrict.Shirogane => 111,
        _ => -1,
    };

    /// <summary>Lifestream <c>PropertyType</c>: House = 0, Apartment = 1.</summary>
    public const int PropertyTypeHouse = 0;
    public const int PropertyTypeApartment = 1;

    /// <summary>Sphene's apartment-building marker in <see cref="LocationInfo.HouseId"/>.</summary>
    public const uint ApartmentHouseId = 100;

    /// <summary>
    /// Converts a normalized <see cref="HousingDestinationAddress"/> to the
    /// IPC tuple - null when the district is unmapped.
    /// </summary>
    public static LifestreamAddressTuple? ToTuple(HousingDestinationAddress address,
        string? name = null)
    {
        var city = CityCodeFor(address.District);
        if (city < 0 || address.Ward is < EventDestinationValidator.MinWard
            or > EventDestinationValidator.MaxWard)
        {
            return null;
        }
        return ("", (int)address.WorldId, city, address.Ward,
            address.IsApartment ? PropertyTypeApartment : PropertyTypeHouse,
            address.IsApartment ? 0 : address.Plot,
            address.IsApartment ? address.Room : 0,
            address.IsApartment && address.ApartmentSubdivision,
            false, name ?? "");
    }

    /// <summary>
    /// Converts a validated <see cref="EventDestinationDto"/> to the IPC
    /// tuple - null for non-housing kinds or invalid data.
    /// </summary>
    public static LifestreamAddressTuple? ToTuple(EventDestinationDto? destination,
        string? name = null)
        => EventDestinationValidator.TryResolveHousingAddress(destination, out var address)
            ? ToTuple(address, name)
            : null;

    /// <summary>
    /// Builds a structured destination snapshot from a game-side
    /// <see cref="LocationInfo"/> (e.g. a bound-area row or the current map
    /// read). Returns null for non-housing locations (WardId == 0), unknown
    /// districts or addresses without a resolvable plot/room.
    /// FC-chamber/interior rooms keep their room number - travel only reaches
    /// the plot, the room is display metadata.
    /// </summary>
    public static EventDestinationDto? FromLocationInfo(LocationInfo location)
    {
        if (location.WardId == 0 || location.ServerId == 0) return null;
        var district = EventDestinationValidator.DistrictFromTerritoryId(location.TerritoryId);
        if (district == ResidentialDistrict.Unknown) return null;

        if (location.HouseId == ApartmentHouseId)
        {
            if (location.RoomId == 0 || location.RoomId > EventDestinationValidator.MaxRoom) return null;
            return EventDestinationValidator.Sanitize(new EventDestinationDto
            {
                Kind = (int)EventDestinationKind.HousingAddress,
                WorldId = location.ServerId,
                DistrictTerritoryId = location.TerritoryId,
                Ward = checked((int)location.WardId),
                IsApartment = true,
                Room = checked((int)location.RoomId),
                // LocationInfo.DivisionId holds HouseId.ApartmentDivision for
                // apartments - Lifestream treats division 1 as the
                // subdivision wing.
                ApartmentSubdivision = location.DivisionId == 1,
            });
        }

        if (location.HouseId == 0 || location.HouseId > EventDestinationValidator.MaxPlot) return null;
        return EventDestinationValidator.Sanitize(new EventDestinationDto
        {
            Kind = (int)EventDestinationKind.HousingAddress,
            WorldId = location.ServerId,
            DistrictTerritoryId = location.TerritoryId,
            Ward = checked((int)location.WardId),
            Plot = checked((int)location.HouseId),
            // Interior room (FC chambers) is display-only - clamp instead of
            // rejecting the whole plot address when the game reports a
            // surprising value.
            Room = location.RoomId > EventDestinationValidator.MaxRoom
                ? 0 : checked((int)location.RoomId),
        });
    }
}
