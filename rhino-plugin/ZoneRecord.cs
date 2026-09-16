using Rhino.Geometry;

namespace UrbanBridge.Plugin;

public enum ZoneIssueType
{
    MissingAttributes,
    DegenerateZone,
    OverlappingZones,
    NoRoadAccess,
}

public sealed class ZoneIssue
{
    public required ZoneIssueType Type { get; init; }
    public required IssueSeverity Severity { get; init; }
    public required string Message { get; init; }
    public Guid RelatedZoneId { get; init; }
}

public sealed class ZoneRecord
{
    public Guid RhinoObjectId { get; init; }
    public Curve? Boundary { get; init; }
    public required string ZoneType { get; init; }
    public double Far { get; init; }
    public double HeightMax { get; init; }
    public double SetbackM { get; init; }
    public double GreenRatio { get; init; }
    public bool ZoneTypeWasMissing { get; init; }
}

public sealed class ZoneMetrics
{
    public double AreaSqm { get; init; }
    public double BuildableAreaSqm { get; init; }
    public double EstimatedPopulation { get; init; }
    public double EstimatedJobs { get; init; }
    public double GreenAreaSqm { get; init; }
    public double RoadFrontageM { get; init; }
}

public sealed class ZoneAnalysis
{
    public List<ZoneRecord> Zones { get; } = new();
    public Dictionary<Guid, ZoneMetrics> MetricsById { get; } = new();
    public List<ZoneIssue> Issues { get; } = new();
    public Dictionary<string, double> AreaByType { get; } = new(StringComparer.OrdinalIgnoreCase);
    public double TotalAreaSqm { get; set; }
    public double TotalPopulation { get; set; }
    public double TotalJobs { get; set; }
    public double TotalGreenAreaSqm { get; set; }
    public DateTime? LastRoadGraphChangeUtc { get; set; }
    public DateTime? LastRoadSurfaceGenUtc { get; set; }
    public bool RoadSurfacesStale { get; set; }
}
