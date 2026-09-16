using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace UrbanBridge.Rhino;

/// <summary>Internal zone checks (overlap, self-intersect, degenerate, missing attrs).</summary>
public sealed class ZoneValidator
{
    private readonly double _tolerance;
    private const double MinAreaSqm = 4.0;

    public ZoneValidator(double modelAbsoluteTolerance)
    {
        _tolerance = modelAbsoluteTolerance > 0 ? modelAbsoluteTolerance : 0.001;
    }

    public void Validate(ZoneAnalysis analysis)
    {
        // missing_attributes + degenerate + self-intersect
        foreach (var zone in analysis.Zones)
        {
            if (zone.ZoneTypeWasMissing)
            {
                analysis.Issues.Add(new ZoneIssue
                {
                    Type = ZoneIssueType.MissingAttributes,
                    Severity = IssueSeverity.Info,
                    Message = "zone_type not set; defaulting to residential",
                    RelatedZoneId = zone.RhinoObjectId,
                });
            }

            if (analysis.MetricsById.TryGetValue(zone.RhinoObjectId, out var m) && m.AreaSqm < MinAreaSqm)
            {
                analysis.Issues.Add(new ZoneIssue
                {
                    Type = ZoneIssueType.DegenerateZone,
                    Severity = IssueSeverity.Error,
                    Message = $"Zone area {m.AreaSqm:F2} m² < {MinAreaSqm} m²",
                    RelatedZoneId = zone.RhinoObjectId,
                });
            }

            var self = Intersection.CurveSelf(zone.Boundary, _tolerance);
            if (self is { Count: > 0 })
            {
                analysis.Issues.Add(new ZoneIssue
                {
                    Type = ZoneIssueType.SelfIntersectingBoundary,
                    Severity = IssueSeverity.Error,
                    Message = $"Boundary has {self.Count} self-intersection(s)",
                    RelatedZoneId = zone.RhinoObjectId,
                });
            }
        }

        // pairwise overlap / containment
        for (var i = 0; i < analysis.Zones.Count; i++)
        {
            for (var j = i + 1; j < analysis.Zones.Count; j++)
            {
                var a = analysis.Zones[i];
                var b = analysis.Zones[j];
                try
                {
                    var rel = Curve.PlanarClosedCurveRelationship(a.Boundary, b.Boundary, Plane.WorldXY, _tolerance);
                    if (rel is RegionContainment.MutualIntersection
                        or RegionContainment.AInsideB
                        or RegionContainment.BInsideA)
                    {
                        analysis.Issues.Add(new ZoneIssue
                        {
                            Type = ZoneIssueType.ZoneOverlap,
                            Severity = IssueSeverity.Error,
                            Message = $"Zones overlap or nest ({rel})",
                            RelatedZoneId = a.RhinoObjectId,
                            RelatedZoneId2 = b.RhinoObjectId,
                        });
                    }
                }
                catch
                {
                    // Non-planar or invalid — skip pair; self-intersect / degenerate already flagged
                }
            }
        }
    }
}
