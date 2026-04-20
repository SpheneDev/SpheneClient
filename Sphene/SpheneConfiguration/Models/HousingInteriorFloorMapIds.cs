using System.Collections.Generic;

namespace Sphene.SpheneConfiguration.Models;

public sealed class HousingInteriorFloorMapIds
{
    public uint GroundMapId { get; set; }
    public uint BasementMapId { get; set; }
    public uint SecondFloorMapId { get; set; }
    public List<uint> ObservedMapIds { get; set; } = new();
}
