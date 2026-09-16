using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Builds an in-memory road graph from curves on the Roads / Roads::* layers.
/// Uses union-find for endpoint clustering within SnapTolerance.
/// </summary>
public sealed class RoadNetworkGraphBuilder
{
    /// <summary>Endpoints within this distance (metres) are merged into one node.</summary>
    public const double DefaultSnapToleranceMeters = 0.5;

    private static readonly Dictionary<string, (int Lanes, double WidthM)> ClassDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["primary"] = (4, 18),
        ["secondary"] = (2, 12),
        ["local"] = (2, 8),
        ["pedestrian"] = (0, 3),
        ["bike"] = (0, 2),
    };

    private readonly double _snapTolerance;

    public RoadNetworkGraphBuilder(double snapToleranceMeters = DefaultSnapToleranceMeters)
    {
        _snapTolerance = snapToleranceMeters > 0 ? snapToleranceMeters : DefaultSnapToleranceMeters;
    }

    public RoadNetworkGraph Build(RhinoDoc document)
    {
        var graph = new RoadNetworkGraph();
        var scale = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);

        var roadObjects = CollectRoadCurves(document);
        if (roadObjects.Count == 0) return graph;

        var endpoints = new List<(Point3d Pos, Guid ObjectId, bool IsStart)>();
        var curveById = new Dictionary<Guid, (Curve Curve, RhinoObject Obj)>();

        foreach (var rhinoObject in roadObjects)
        {
            if (rhinoObject.Geometry is not Curve curve || !curve.IsValid) continue;
            var start = curve.PointAtStart;
            var end = curve.PointAtEnd;
            var startM = new Point3d(start.X * scale, start.Y * scale, start.Z * scale);
            var endM = new Point3d(end.X * scale, end.Y * scale, end.Z * scale);
            endpoints.Add((startM, rhinoObject.Id, true));
            endpoints.Add((endM, rhinoObject.Id, false));
            curveById[rhinoObject.Id] = (curve, rhinoObject);
        }

        if (endpoints.Count == 0) return graph;

        var parent = Enumerable.Range(0, endpoints.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }
        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b) parent[b] = a;
        }

        for (var i = 0; i < endpoints.Count; i++)
        {
            for (var j = i + 1; j < endpoints.Count; j++)
            {
                if (endpoints[i].Pos.DistanceTo(endpoints[j].Pos) <= _snapTolerance)
                    Union(i, j);
            }
        }

        var clusters = new Dictionary<int, List<int>>();
        for (var i = 0; i < endpoints.Count; i++)
        {
            var root = Find(i);
            if (!clusters.TryGetValue(root, out var list))
            {
                list = new List<int>();
                clusters[root] = list;
            }
            list.Add(i);
        }

        var endpointIndexToNodeId = new string[endpoints.Count];
        var nodeById = new Dictionary<string, RoadNode>(StringComparer.Ordinal);

        foreach (var (_, indices) in clusters)
        {
            var sum = Point3d.Origin;
            foreach (var idx in indices) sum += endpoints[idx].Pos;
            var avg = sum / indices.Count;

            var nodeId = MakeStableNodeId(avg);
            if (!nodeById.TryGetValue(nodeId, out var node))
            {
                node = new RoadNode
                {
                    Id = nodeId,
                    Position = avg,
                };
                nodeById[nodeId] = node;
                graph.Nodes.Add(node);
            }

            foreach (var idx in indices)
                endpointIndexToNodeId[idx] = nodeId;
        }

        var edgeStartEnd = new Dictionary<Guid, (string Start, string End)>();
        for (var i = 0; i < endpoints.Count; i++)
        {
            var (pos, objectId, isStart) = endpoints[i];
            var nodeId = endpointIndexToNodeId[i];
            if (!edgeStartEnd.TryGetValue(objectId, out var pair))
                pair = (string.Empty, string.Empty);

            if (isStart) pair.Start = nodeId;
            else pair.End = nodeId;
            edgeStartEnd[objectId] = pair;
        }

        foreach (var (objectId, (startId, endId)) in edgeStartEnd)
        {
            if (string.IsNullOrEmpty(startId) || string.IsNullOrEmpty(endId)) continue;
            if (!curveById.TryGetValue(objectId, out var entry)) continue;

            var (curve, rhinoObject) = entry;
            var lengthM = curve.GetLength() * scale;
            var attrs = rhinoObject.Attributes.GetUserStrings();

            var roadClass = attrs.Get("road_class") ?? "local";
            if (!ClassDefaults.ContainsKey(roadClass))
                roadClass = "local";

            var defaults = ClassDefaults[roadClass];
            var lanes = int.TryParse(attrs.Get("lanes"), out var l) ? l : defaults.Lanes;
            var width = double.TryParse(attrs.Get("width_m"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : defaults.WidthM;

            var edge = new RoadEdge
            {
                RhinoObjectId = objectId,
                StartNodeId = startId,
                EndNodeId = endId,
                LengthMeters = lengthM,
                RoadClass = roadClass,
                Lanes = lanes,
                WidthMeters = width,
            };
            graph.Edges.Add(edge);

            if (nodeById.TryGetValue(startId, out var startNode))
            {
                startNode.ConnectedEdgeIds.Add(objectId);
                startNode.Degree++;
            }
            if (nodeById.TryGetValue(endId, out var endNode))
            {
                endNode.ConnectedEdgeIds.Add(objectId);
                endNode.Degree++;
            }
        }

        foreach (var node in graph.Nodes)
        {
            node.Type = node.Degree switch
            {
                1 => NodeType.DeadEnd,
                2 => NodeType.Through,
                _ => NodeType.Intersection,
            };
        }

        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
            adjacency[node.Id] = new List<string>();
        foreach (var edge in graph.Edges)
        {
            adjacency[edge.StartNodeId].Add(edge.EndNodeId);
            adjacency[edge.EndNodeId].Add(edge.StartNodeId);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var componentCount = 0;
        foreach (var node in graph.Nodes)
        {
            if (visited.Contains(node.Id)) continue;
            componentCount++;
            var queue = new Queue<string>();
            queue.Enqueue(node.Id);
            visited.Add(node.Id);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var neighbour in adjacency[current])
                {
                    if (visited.Add(neighbour))
                        queue.Enqueue(neighbour);
                }
            }
        }
        graph.Stats.ComponentCount = componentCount == 0 ? 0 : componentCount;

        graph.Stats.TotalLengthM = graph.Edges.Sum(e => e.LengthMeters);
        foreach (var edge in graph.Edges)
        {
            if (!graph.Stats.LengthByClass.ContainsKey(edge.RoadClass))
                graph.Stats.LengthByClass[edge.RoadClass] = 0;
            graph.Stats.LengthByClass[edge.RoadClass] += edge.LengthMeters;
        }
        graph.Stats.IntersectionCount = graph.Nodes.Count(n => n.Type == NodeType.Intersection);
        graph.Stats.DeadEndCount = graph.Nodes.Count(n => n.Type == NodeType.DeadEnd);

        return graph;
    }

    private static string MakeStableNodeId(Point3d position)
    {
        const double tol = DefaultSnapToleranceMeters;
        var rx = (int)Math.Round(position.X / tol);
        var ry = (int)Math.Round(position.Y / tol);
        var rz = (int)Math.Round(position.Z / tol);
        return $"n_{rx}_{ry}_{rz}";
    }

    private static List<RhinoObject> CollectRoadCurves(RhinoDoc document)
    {
        var result = new List<RhinoObject>();
        foreach (var rhinoObject in document.Objects)
        {
            if (rhinoObject.IsDeleted) continue;
            if (rhinoObject.Geometry is not Curve) continue;
            var layer = document.Layers[rhinoObject.Attributes.LayerIndex];
            if (layer is null) continue;
            var fullPath = layer.FullPath;
            if (fullPath.Equals("Roads", StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith("Roads::", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(rhinoObject);
            }
        }
        return result;
    }
}
