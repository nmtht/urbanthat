using Rhino.Geometry;

namespace UrbanBridge.Plugin;

public enum ZoneIssueType
{
    ZoneOverlap,
    SelfIntersectingBoundary,
    DegenerateZone,
    MissingAttributes,
    ZoneNoRoadAccess,
}

/// <summary>One closed curve on Zones / Zones::* with resolved attributes.</summary>
public sealed class ZoneRecord
{
    public required Guid RhinoObjectId { get; init; }
    public required Curve Boundary { get; init; }
    public required string ZoneType { get; init; }
    public double Far { get; init; }
    public double HeightMax { get; init; }
    public double SetbackM { get; init; }
    public double GreenRatio { get; init; }
    public bool ZoneTypeWasMissing { get; init; }
}

public sealed class ZoneMetrics
{
    public double AreaSqm { get; set; }
    public double BuildableAreaSqm { get; set; }
    public double GreenAreaSqm { get; set; }
    public double EstimatedPopulation { get; set; }
    public double EstimatedJobs { get; set; }
    /// <summary>Estimated frontage (m) within road-access threshold — sample-based MVP.</summary>
    public double RoadFrontageM { get; set; }
}

public sealed class ZoneIssue
{
    public required ZoneIssueType Type { get; init; }
    public required IssueSeverity Severity { get; init; }
    public required string Message { get; init; }
    public Guid RelatedZoneId { get; init; }
    public Guid? RelatedZoneId2 { get; init; }
}

/// <summary>Full analysis snapshot for the Zones layers.</summary>
public sealed class ZoneAnalysis
{
    public List<ZoneRecord> Zones { get; } = new();
    public Dictionary<Guid, ZoneMetrics> MetricsById { get; } = new();
    public List<ZoneIssue> Issues { get; } = new();

    public double TotalAreaSqm { get; set; }
    public double TotalPopulation { get; set; }
    public double TotalJobs { get; set; }
    public double TotalGreenAreaSqm { get; set; }
    public Dictionary<string, double> AreaByType { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when road surfaces may be older than the last road-centerline edit.</summary>
    public bool RoadSurfacesStale { get; set; }

    public DateTime? LastRoadGraphChangeUtc { get; set; }
    public DateTime? LastRoadSurfaceGenUtc { get; set; }
}
