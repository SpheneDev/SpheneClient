using System;
using System.Collections.Generic;

namespace Sphene.SpheneConfiguration.Models;

public sealed class HousingPlotSurveyDivision
{
    public int DivisionId { get; set; }
    public bool Visited { get; set; }
    public DateTime? FirstSeenUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public List<HousingPlotSurveyPlotEntry> Plots { get; set; } = new();
}
