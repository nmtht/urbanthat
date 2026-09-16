using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>Node type derived from edge degree.</summary>
public enum NodeType
{
    DeadEnd,
    Through,
    Intersection
}

/// <summary>Severity of a network validation issue.</summary>
public enum IssueSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>A computed intersection / endpoint in the road graph.</summary>
public sealed class RoadNode
{
    public required string Id { get; init; }
    public required Point3d Position { get; init; }
    public int Degree { get; set; }
    public NodeType Type { get; set; }
    public List<Guid> ConnectedEdgeIds { get; } = new();
}

/// <summary>A road segment corresponding to one Rhino curve.</summary>
public sealed class RoadEdge
{
    public required Guid RhinoObjectId { get; init; }
    public required string StartNodeId { get; init; }
    public required string EndNodeId { get; init; }
    public double LengthMeters { get; init; }
    public string RoadClass { get; init; } = "local";
    public int Lanes { get; init; }
    public double WidthMeters { get; init; }
}

/// <summary>A validation finding about the road network.</summary>
public sealed class NetworkIssue
{
    public required string Type { get; init; }
    public required IssueSeverity Severity { get; init; }
    public required string Message { get; init; }
    public List<Guid> RelatedEdgeIds { get; init; } = new();
    public string? RelatedNodeId { get; init; }
}

/// <summary>Aggregated statistics for the current road network.</summary>
public sealed class NetworkStats
{
    public double TotalLengthM { get; set; }
    public Dictionary<string, double> LengthByClass { get; } = new(StringComparer.Ordinal);
    public int IntersectionCount { get; set; }
    public int DeadEndCount { get; set; }
    public int ComponentCount { get; set; }
}

/// <summary>In-memory graph built from all curves on the Roads layer(s).</summary>
public sealed class RoadNetworkGraph
{
    public List<RoadNode> Nodes { get; } = new();
    public List<RoadEdge> Edges { get; } = new();
    public List<NetworkIssue> Issues { get; } = new();
    public NetworkStats Stats { get; } = new();
}
