using System.Collections.Generic;

namespace Sphene.SpheneConfiguration.Models;

public sealed class HousingPlotSurveyTerritory
{
    public uint TerritoryId { get; set; }
    public string TerritoryName { get; set; } = string.Empty;
    public int ExpectedWards { get; set; } = 30;
    public Dictionary<string, HousingPlotSurveyWard> Wards { get; set; } = new(StringComparer.Ordinal);
}
