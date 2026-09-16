namespace UrbanBridge.Plugin;

public enum NodeType
{
    Intersection,
    DeadEnd,
    Terminal,
    Junction,
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

public sealed class RoadNode
{
    public required string Id { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public int Degree { get; set; }
    public NodeType Type { get; set; }
}

public sealed class RoadEdge
{
    public Guid RhinoObjectId { get; init; }
    public required string StartNodeId { get; init; }
    public required string EndNodeId { get; init; }
    public double LengthMeters { get; init; }
    public double WidthMeters { get; init; }
    public int Lanes { get; init; }
    public required string RoadClass { get; init; }
    public bool IsTerminal { get; init; }
}

public sealed class NetworkIssue
{
    public required string Type { get; init; }
    public IssueSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? RelatedNodeId { get; init; }
    public List<Guid> RelatedEdgeIds { get; init; } = new();
}

public sealed class NetworkStats
{
    public double TotalLengthM { get; set; }
    public int IntersectionCount { get; set; }
    public int DeadEndCount { get; set; }
    public int ComponentCount { get; set; }
}

public sealed class RoadNetworkGraph
{
    public List<RoadNode> Nodes { get; } = new();
    public List<RoadEdge> Edges { get; } = new();
    public List<NetworkIssue> Issues { get; } = new();
    public NetworkStats Stats { get; } = new();
}
