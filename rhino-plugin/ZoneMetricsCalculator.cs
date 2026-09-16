using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>Area / FAR / population / jobs from zone boundary + attributes.</summary>
public static class ZoneMetricsCalculator
{
    public static ZoneMetrics Compute(ZoneRecord zone, double lengthScaleDocToM)
    {
        double areaSqm = 0;
        if (zone.Boundary is not null && zone.Boundary.IsValid)
        {
            var amp = AreaMassProperties.Compute(zone.Boundary);
            if (amp is not null)
                areaSqm = amp.Area * lengthScaleDocToM * lengthScaleDocToM;
        }

        var green = Math.Clamp(zone.GreenRatio, 0, 1) * areaSqm;
        var buildable = Math.Max(0, areaSqm - green) * Math.Max(0, zone.Far);

        // Rough occupancy heuristics for MVP dashboard
        var (popFactor, jobFactor) = zone.ZoneType.ToLowerInvariant() switch
        {
            "residential" => (0.04, 0.005),
            "commercial" => (0.005, 0.05),
            "mixed_use" => (0.025, 0.025),
            "industrial" => (0.002, 0.03),
            "green" => (0.0, 0.0),
            "public" => (0.01, 0.02),
            _ => (0.03, 0.01),
        };

        return new ZoneMetrics
        {
            AreaSqm = areaSqm,
            BuildableAreaSqm = buildable,
            EstimatedPopulation = areaSqm * popFactor,
            EstimatedJobs = areaSqm * jobFactor,
            GreenAreaSqm = green,
            RoadFrontageM = 0, // filled by ZoneRoadAccessChecker
        };
    }
}
