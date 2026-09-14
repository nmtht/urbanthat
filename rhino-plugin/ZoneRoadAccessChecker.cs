using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>
/// Checks zone boundary proximity to generated roadway surfaces (Stage 2.1).
/// Sample-based MVP — not exact curve/brep intersection (TZ §5, §10).
/// </summary>
public sealed class ZoneRoadAccessChecker
{
    public const double DefaultThresholdMeters = 5.0;
    public const double SampleStepMeters = 2.0;

    private readonly double _thresholdDoc;
    private readonly double _sampleStepDoc;
    private readonly double _tolerance;

    public ZoneRoadAccessChecker(RhinoDoc doc, double thresholdMeters = DefaultThresholdMeters)
    {
        var toDoc = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        _thresholdDoc = thresholdMeters * toDoc;
        _sampleStepDoc = SampleStepMeters * toDoc;
        _tolerance = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
    }

    public void Check(RhinoDoc doc, ZoneAnalysis analysis)
    {
        var roadways = CollectRoadwayBreps(doc);
        if (roadways.Count == 0)
        {
            // No generated surfaces — every zone is unreachable by definition
            foreach (var zone in analysis.Zones)
            {
                if (analysis.MetricsById.TryGetValue(zone.RhinoObjectId, out var m))
                    m.RoadFrontageM = 0;

                analysis.Issues.Add(new ZoneIssue
                {
                    Type = ZoneIssueType.ZoneNoRoadAccess,
                    Severity = IssueSeverity.Warning,
                    Message = "No Roads::Surface::Roadway geometry — run Generate Road Surfaces",
                    RelatedZoneId = zone.RhinoObjectId,
                });
            }
            return;
        }

        var metersFromDoc = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);

        foreach (var zone in analysis.Zones)
        {
            var curve = zone.Boundary;
            var length = curve.GetLength();
            if (length < _tolerance)
                continue;

            var step = Math.Max(_sampleStepDoc, _tolerance * 10);
            var points = curve.DivideByLength(step, true);
            if (points is null || points.Length == 0)
            {
                // Fallback: endpoints + mid
                points = new[] { curve.Domain.Min, curve.Domain.Mid, curve.Domain.Max };
            }

            var accessibleSamples = 0;
            var totalSamples = 0;

            foreach (var t in points)
            {
                var pt = curve.PointAt(t);
                totalSamples++;
                if (IsNearRoadway(pt, roadways, _thresholdDoc))
                    accessibleSamples++;
            }

            var frontageDoc = totalSamples > 0
                ? length * (accessibleSamples / (double)totalSamples)
                : 0.0;

            if (analysis.MetricsById.TryGetValue(zone.RhinoObjectId, out var metrics))
                metrics.RoadFrontageM = frontageDoc * metersFromDoc;

            if (accessibleSamples == 0)
            {
                analysis.Issues.Add(new ZoneIssue
                {
                    Type = ZoneIssueType.ZoneNoRoadAccess,
                    Severity = IssueSeverity.Warning,
                    Message = $"No boundary sample within {DefaultThresholdMeters} m of roadway surface",
                    RelatedZoneId = zone.RhinoObjectId,
                });
            }
        }
    }

    private static List<Brep> CollectRoadwayBreps(RhinoDoc doc)
    {
        var list = new List<Brep>();
        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            var layer = doc.Layers[obj.Attributes.LayerIndex];
            if (layer is null) continue;
            if (!layer.FullPath.Equals(RoadSurfaceGenerator.LayerRoadway, StringComparison.OrdinalIgnoreCase))
                continue;
            if (obj.Geometry is Brep brep)
                list.Add(brep);
            else if (obj.Geometry is Extrusion ext)
            {
                var b = ext.ToBrep();
                if (b is not null) list.Add(b);
            }
        }
        return list;
    }

    private static bool IsNearRoadway(Point3d pt, List<Brep> roadways, double threshold)
    {
        // RhinoCommon 8: ClosestPoint requires the full out-parameter signature.
        // maximumDistance limits the search; success implies a hit within threshold.
        var maxDist = threshold > 0 ? threshold : 1e9;
        foreach (var brep in roadways)
        {
            if (brep.ClosestPoint(
                    pt,
                    out Point3d closest,
                    out ComponentIndex _,
                    out double _,
                    out double _,
                    maxDist,
                    out Vector3d _))
            {
                if (pt.DistanceTo(closest) <= threshold)
                    return true;
            }
        }
        return false;
    }
}
