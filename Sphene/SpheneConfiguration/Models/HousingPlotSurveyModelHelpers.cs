using System.Collections.Generic;
using System.Linq;

namespace Sphene.SpheneConfiguration.Models;

public static class HousingPlotSurveyModelHelpers
{
    public static uint ComputeHash(IEnumerable<HousingPlotSurveyPlotEntry> plots)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var p in plots)
            {
                hash = (hash ^ (uint)p.PlotNumber) * 16777619;
                hash = (hash ^ p.Size) * 16777619;
            }
            return hash;
        }
    }

    public static int CountVisitedWards(HousingPlotSurveyTerritory territory)
    {
        if (territory.Wards.Count == 0) return 0;
        return territory.Wards.Values.Count(w => w.Divisions.Values.Any(d => d.Visited));
    }
}
