using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>Validates a built road network and populates Issues + refined Stats.</summary>
public sealed class RoadNetworkValidator
{
    private readonly double _snapTolerance;

    public RoadNetworkValidator(double snapToleranceMeters = RoadNetworkGraphBuilder.DefaultSnapToleranceMeters)
    {
        _snapTolerance = snapToleranceMeters;
    }

    public void Validate(RhinoDoc document, RoadNetworkGraph graph)
    {
        graph.Issues.Clear();
        var scale = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);

        var objectById = new Dictionary<Guid, RhinoObject>();
        foreach (var obj in document.Objects)
        {
            if (!obj.IsDeleted)
                objectById[obj.Id] = obj;
        }

        CheckMissingAttributes(graph, objectById);
        CheckDisconnectedComponents(graph);
        CheckShortEdges(graph);
        CheckDanglingEnds(graph);

        // Refresh aggregate stats from graph structure
        graph.Stats.TotalLengthM = graph.Edges.Sum(e => e.LengthMeters);
        graph.Stats.IntersectionCount = graph.Nodes.Count(n => n.Type == NodeType.Intersection);
        graph.Stats.DeadEndCount = graph.Nodes.Count(n => n.Type == NodeType.DeadEnd);
    }

    private static void CheckMissingAttributes(RoadNetworkGraph graph, Dictionary<Guid, RhinoObject> objectById)
    {
        foreach (var edge in graph.Edges)
        {
            if (!objectById.TryGetValue(edge.RhinoObjectId, out var obj))
                continue;
            var strings = obj.Attributes.GetUserStrings();
            if (string.IsNullOrWhiteSpace(strings.Get("road_class")))
            {
                graph.Issues.Add(new NetworkIssue
                {
                    Type = "missing_attributes",
                    Severity = IssueSeverity.Info,
                    Message = "road_class not set; defaulting to local",
                    RelatedEdgeIds = new List<Guid> { edge.RhinoObjectId },
                });
            }
        }
    }

    private static void CheckDisconnectedComponents(RoadNetworkGraph graph)
    {
        if (graph.Stats.ComponentCount > 1)
        {
            graph.Issues.Add(new NetworkIssue
            {
                Type = "disconnected_components",
                Severity = IssueSeverity.Warning,
                Message = $"Road network has {graph.Stats.ComponentCount} disconnected components",
            });
        }
    }

    private static void CheckShortEdges(RoadNetworkGraph graph)
    {
        const double minLengthM = 1.0;
        foreach (var edge in graph.Edges)
        {
            if (edge.LengthMeters < minLengthM)
            {
                graph.Issues.Add(new NetworkIssue
                {
                    Type = "short_edge",
                    Severity = IssueSeverity.Warning,
                    Message = $"Edge length {edge.LengthMeters:F2} m < {minLengthM} m",
                    RelatedEdgeIds = new List<Guid> { edge.RhinoObjectId },
                });
            }
        }
    }

    private static void CheckDanglingEnds(RoadNetworkGraph graph)
    {
        foreach (var node in graph.Nodes.Where(n => n.Type == NodeType.DeadEnd))
        {
            graph.Issues.Add(new NetworkIssue
            {
                Type = "dead_end",
                Severity = IssueSeverity.Info,
                Message = $"Dead-end node {node.Id}",
                RelatedNodeId = node.Id,
            });
        }
    }
}
