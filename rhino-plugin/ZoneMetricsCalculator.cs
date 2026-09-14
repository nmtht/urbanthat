using Rhino;
using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>Computes area-based metrics for a zone (document units → m²).</summary>
public static class ZoneMetricsCalculator
{
    public static ZoneMetrics Compute(ZoneRecord zone, double metersToDocScale)
    {
        // AreaMassProperties returns area in document units²; convert to m².
        var amp = AreaMassProperties.Compute(zone.Boundary);
        var areaDoc = amp?.Area ?? 0.0;
        // doc_unit → meters: 1 doc unit = metersToDocScale meters? 
        // RhinoMath.UnitScale(ModelUnit, Meters) converts length doc→m, so area factor is scale².
        var scaleToMeters = metersToDocScale; // length scale doc→m
        var areaSqm = Math.Abs(areaDoc) * scaleToMeters * scaleToMeters;

        var metrics = new ZoneMetrics
        {
            AreaSqm = areaSqm,
            BuildableAreaSqm = areaSqm * zone.Far,
            GreenAreaSqm = areaSqm * zone.GreenRatio,
        };

        var hectares = areaSqm / 10_000.0;
        var defaults = ZoneTypeDefaults.Get(zone.ZoneType);

        // mixed_use simplification (TZ §3): 50% residential density + 50% commercial jobs density
        if (string.Equals(zone.ZoneType, "mixed_use", StringComparison.OrdinalIgnoreCase))
        {
            metrics.EstimatedPopulation = hectares * 0.5 * defaults.PopulationPerHa;
            metrics.EstimatedJobs = hectares * 0.5 * defaults.JobsPerHa;
        }
        else
        {
            metrics.EstimatedPopulation = hectares * defaults.PopulationPerHa;
            metrics.EstimatedJobs = hectares * defaults.JobsPerHa;
        }

        return metrics;
    }
}
