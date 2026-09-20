using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace UrbanBridge.Plugin;

public sealed class ZoneValidator
{
    private const double MinAreaSqm = 25.0;
    private readonly double _tolerance;

    public ZoneValidator(double modelAbsoluteTolerance)
    {
        _tolerance = modelAbsoluteTolerance > 0 ? modelAbsoluteTolerance : 0.001;
    }

    public void Validate(ZoneAnalysis analysis)
    {
        for (var i = 0; i < analysis.Zones.Count; i++)
        {
            var zone = analysis.Zones[i];

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

            var metricsKey = zone.MetricsId != Guid.Empty ? zone.MetricsId : zone.RhinoObjectId;
            if (analysis.MetricsById.TryGetValue(metricsKey, out var m) && m.AreaSqm < MinAreaSqm)
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
                    Message = "Zone boundary self-intersects",
                    RelatedZoneId = zone.RhinoObjectId,
                });
            }

            for (var j = i + 1; j < analysis.Zones.Count; j++)
            {
                var other = analysis.Zones[j];
                // Same parent zone split into parcels — overlap expected / not an issue
                if (zone.RhinoObjectId == other.RhinoObjectId) continue;

                try
                {
                    var inter = Intersection.CurveCurve(zone.Boundary, other.Boundary, _tolerance, _tolerance);
                    if (inter is { Count: > 0 })
                    {
                        analysis.Issues.Add(new ZoneIssue
                        {
                            Type = ZoneIssueType.ZoneOverlap,
                            Severity = IssueSeverity.Warning,
                            Message = "Zone boundaries intersect",
                            RelatedZoneId = zone.RhinoObjectId,
                            RelatedZoneId2 = other.RhinoObjectId,
                        });
                    }
                }
                catch { }
            }
        }
    }
}
