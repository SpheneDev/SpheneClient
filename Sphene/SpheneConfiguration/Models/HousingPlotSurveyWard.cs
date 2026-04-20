using System.Collections.Generic;

namespace Sphene.SpheneConfiguration.Models;

public sealed class HousingPlotSurveyWard
{
    public int WardId { get; set; }
    public Dictionary<string, HousingPlotSurveyDivision> Divisions { get; set; } = new(StringComparer.Ordinal);
}
