using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace UrbanBridge.Rhino;

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
        CheckDegenerateSegments(graph, objectById, scale);
        CheckSelfIntersections(graph, objectById, scale);
        CheckDanglingEnds(graph, objectById);
        CheckDisconnectedComponents(graph);
        CheckDuplicateOverlap(graph, objectById, scale);
        CheckCrossingWithoutNode(graph, objectById, scale);

        // Refresh stats that depend on final graph
        graph.Stats.IntersectionCount = graph.Nodes.Count(n => n.Type == NodeType.Intersection);
        graph.Stats.DeadEndCount = graph.Nodes.Count(n => n.Type == NodeType.DeadEnd);
    }

    private void CheckMissingAttributes(RoadNetworkGraph graph, Dictionary<Guid, RhinoObject> objectById)
    {
        foreach (var edge in graph.Edges)
        {
            if (!objectById.TryGetValue(edge.RhinoObjectId, out var obj)) continue;
            var attrs = obj.Attributes.GetUserStrings();
            if (string.IsNullOrWhiteSpace(attrs.Get("road_class")))
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

    private void CheckDegenerateSegments(RoadNetworkGraph graph, Dictionary<Guid, RhinoObject> objectById, double scale)
    {
        foreach (var edge in graph.Edges)
        {
            if (!objectById.TryGetValue(edge.RhinoObjectId, out var obj) || obj.Geometry is not Curve curve)
                continue;

            if (!curve.IsValid || edge.LengthMeters < 0.1)
            {
                graph.Issues.Add(new NetworkIssue
                {
                    Type = "degenerate_segment",
                    Severity = IssueSeverity.Error,
                    Message = curve.IsValid
                        ? $"Segment length {edge.LengthMeters:F3} m < 0.1 m"
                        : "Curve is invalid",
                    RelatedEdgeIds = new List<Guid> { edge.RhinoObjectId },
                });
            }
        }
    }

    private void CheckSelfIntersections(RoadNetworkGraph graph, Dictionary<Guid, RhinoObject> objectById, double scale)
    {
        var tol = _snapTolerance / scale; // document units
        foreach (var edge in graph.Edges)
        {
            if (!objectById.TryGetValue(edge.RhinoObjectId, out var obj) || obj.Geometry is not Curve curve)
                continue;

            var events = Intersection.CurveSelf(curve, tol);
            if (events is { Count: > 0 })
            {
                graph.Issues.Add(new NetworkIssue
                {
                    Type = "self_intersecting",
                    Severity = IssueSeverity.Error,
                    Message = $"Curve has {events.Count} self-intersection(s)",
                    RelatedEdgeIds = new List<Guid> { edge.RhinoObjectId },
                });
            }
        }
    }

    private void CheckDanglingEnds(RoadNetworkGraph graph, Dictionary<Guid, RhinoObject> objectById)
    {
        foreach (var node in graph.Nodes.Where(n => n.Degree == 1))
        {
            // Find the single connected edge and check is_terminal
            var edgeId = node.ConnectedEdgeIds.FirstOrDefault();
            if (edgeId == Guid.Empty) continue;
            if (!objectById.TryGetValue(edgeId, out var obj)) continue;

            var attrs = obj.Attributes.GetUserStrings();
            var isTerminal = string.Equals(attrs.Get("is_terminal"), "true", StringComparison.OrdinalIgnoreCase);

            if (!isTerminal)
            {
                graph.Issues.Add(new NetworkIssue
                {
                    Type = "dangling_end",
                    Severity = IssueSeverity.Warning,
                    Message = "Тупик без флага is_terminal",
                    RelatedNodeId = node.Id,
                    RelatedEdgeIds = new List<Guid> { edgeId },
                });
            }
        }
    }

    private void CheckDisconnectedComponents(RoadNetworkGraph graph)
    {
        if (graph.Stats.ComponentCount > 1)
        {
            graph.Issues.Add(new NetworkIssue
            {
                Type = "disconnected_component",
                Severity = IssueSeverity.Warning,
                Message = $"Network has {graph.Stats.ComponentCount} disconnected components",
            });
        }
    }

    private void CheckDuplicateOverlap(RoadNetworkGraph graph, Dictionary<Guid, RhinoObject> objectById, double scale)
    {
        // Heuristic: midpoints and both endpoints of two curves within SnapTolerance
        var mids = new List<(Guid Id, Point3d Mid, Point3d Start, Point3d End)>();
        foreach (var edge in graph.Edges)
        {
            if (!objectById.TryGetValue(edge.RhinoObjectId, out var obj) || obj.Geometry is not Curve curve)
                continue;
            var mid = curve.PointAt(curve.Domain.Mid);
            var start = curve.PointAtStart;
            var end = curve.PointAtEnd;
            mids.Add((
                edge.RhinoObjectId,
                new Point3d(mid.X * scale, mid.Y * scale, mid.Z * scale),
                new Point3d(start.X * scale, start.Y * scale, start.Z * scale),
                new Point3d(end.X * scale, end.Y * scale, end.Z * scale)
            ));
        }

        for (var i = 0; i < mids.Count; i++)
        {
            for (var j = i + 1; j < mids.Count; j++)
            {
                var a = mids[i];
                var b = mids[j];
                if (a.Mid.DistanceTo(b.Mid) <= _snapTolerance &&
                    ((a.Start.DistanceTo(b.Start) <= _snapTolerance && a.End.DistanceTo(b.End) <= _snapTolerance) ||
                     (a.Start.DistanceTo(b.End) <= _snapTolerance && a.End.DistanceTo(b.Start) <= _snapTolerance)))
                {
                    graph.Issues.Add(new NetworkIssue
                    {
                        Type = "duplicate_overlap",
                        Severity = IssueSeverity.Warning,
                        Message = "Possible duplicate/overlapping curves (heuristic — false positives possible)",
                        RelatedEdgeIds = new List<Guid> { a.Id, b.Id },
                    });
                }
            }
        }
    }

    private void CheckCrossingWithoutNode(RoadNetworkGraph graph, Dictionary<Guid, RhinoObject> objectById, double scale)
    {
        var tolDoc = _snapTolerance / scale;
        var edges = graph.Edges.ToList();

        for (var i = 0; i < edges.Count; i++)
        {
            if (!objectById.TryGetValue(edges[i].RhinoObjectId, out var objA) || objA.Geometry is not Curve curveA)
                continue;

            for (var j = i + 1; j < edges.Count; j++)
            {
                if (!objectById.TryGetValue(edges[j].RhinoObjectId, out var objB) || objB.Geometry is not Curve curveB)
                    continue;

                // Skip if they already share a node (connected by design)
                var shareNode =
                    edges[i].StartNodeId == edges[j].StartNodeId ||
                    edges[i].StartNodeId == edges[j].EndNodeId ||
                    edges[i].EndNodeId == edges[j].StartNodeId ||
                    edges[i].EndNodeId == edges[j].EndNodeId;
                if (shareNode) continue;

                var intersections = Intersection.CurveCurve(curveA, curveB, tolDoc, tolDoc);
                if (intersections is null || intersections.Count == 0) continue;

                // Any intersection that is not near an endpoint of both curves is a crossing without node
                var hasInterior = false;
                foreach (var ev in intersections)
                {
                    var ptA = curveA.PointAt(ev.ParameterA);
                    var ptB = curveB.PointAt(ev.ParameterB);
                    var nearEndA =
                        ptA.DistanceTo(curveA.PointAtStart) <= tolDoc ||
                        ptA.DistanceTo(curveA.PointAtEnd) <= tolDoc;
                    var nearEndB =
                        ptB.DistanceTo(curveB.PointAtStart) <= tolDoc ||
                        ptB.DistanceTo(curveB.PointAtEnd) <= tolDoc;
                    if (!(nearEndA && nearEndB))
                    {
                        hasInterior = true;
                        break;
                    }
                }

                if (hasInterior)
                {
                    graph.Issues.Add(new NetworkIssue
                    {
                        Type = "crossing_without_node",
                        Severity = IssueSeverity.Warning,
                        Message = "Curves cross without a shared endpoint node",
                        RelatedEdgeIds = new List<Guid> { edges[i].RhinoObjectId, edges[j].RhinoObjectId },
                    });
                }
            }
        }
    }
}
