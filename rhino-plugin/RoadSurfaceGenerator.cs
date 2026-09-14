using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace UrbanBridge.Rhino;

/// <summary>
/// Stage 2.1 — generate roadway / sidewalk surfaces and lane markings from the road graph.
/// Links (mid-edge strips) and hubs (intersection pads) are built independently;
/// boolean ops stay local to a single hub.
/// </summary>
public sealed class RoadSurfaceGenerator
{
    public const string LayerRoadway = "Roads::Surface::Roadway";
    public const string LayerSidewalk = "Roads::Surface::Sidewalk";
    public const string LayerMarkings = "Roads::Markings::LaneLine";

    private const double AcuteAngleDegrees = 20.0;
    private const double ExtensionFactor = 1.5;
    private const double MaxExtensionFraction = 0.40;

    private readonly double _docTolerance;

    public RoadSurfaceGenerator(RhinoDoc doc)
    {
        _docTolerance = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
    }

    /// <summary>
    /// Full generation pass: cleanup previous surfaces, rebuild graph geometry, add objects.
    /// Extra issues (acute_angle / geometry_generation_failed) are appended to <paramref name="graph"/>.
    /// </summary>
    public GenerationResult Generate(RhinoDoc doc, RoadNetworkGraph graph)
    {
        var result = new GenerationResult();
        var metersToDoc = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);

        // 1. Cleanup previous generation
        result.DeletedCount = RoadSurfaceCleanup.DeleteAllGenerated(doc);

        EnsureLayerPath(doc, LayerRoadway, System.Drawing.Color.FromArgb(90, 90, 95));
        EnsureLayerPath(doc, LayerSidewalk, System.Drawing.Color.FromArgb(180, 180, 175));
        EnsureLayerPath(doc, LayerMarkings, System.Drawing.Color.FromArgb(240, 240, 220));

        // Resolve curves + edge params in document units
        var edgeData = new Dictionary<Guid, EdgeGeom>();
        foreach (var edge in graph.Edges)
        {
            var obj = doc.Objects.FindId(edge.RhinoObjectId);
            if (obj?.Geometry is not Curve curve || !curve.IsValid)
                continue;

            var widthDoc = edge.WidthMeters * metersToDoc;
            var sidewalkDoc = GetSidewalkWidthM(obj, edge.RoadClass) * metersToDoc;
            var markings = GetGenerateMarkings(obj, edge.RoadClass);
            edgeData[edge.RhinoObjectId] = new EdgeGeom
            {
                Edge = edge,
                Curve = curve.DuplicateCurve(),
                WidthDoc = widthDoc,
                SidewalkDoc = sidewalkDoc,
                Lanes = edge.Lanes,
                GenerateMarkings = markings,
            };
        }

        // Precompute extension distances per edge end (document units)
        var maxWidthAtNode = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (!edgeData.ContainsKey(edge.RhinoObjectId)) continue;
            var w = edgeData[edge.RhinoObjectId].WidthDoc +
                    2.0 * edgeData[edge.RhinoObjectId].SidewalkDoc;
            maxWidthAtNode[edge.StartNodeId] = Math.Max(
                maxWidthAtNode.GetValueOrDefault(edge.StartNodeId), w);
            maxWidthAtNode[edge.EndNodeId] = Math.Max(
                maxWidthAtNode.GetValueOrDefault(edge.EndNodeId), w);
        }

        foreach (var kv in edgeData)
        {
            var eg = kv.Value;
            var len = eg.Curve.GetLength();
            if (len < _docTolerance * 10) continue;

            var extStart = ComputeExtension(maxWidthAtNode.GetValueOrDefault(eg.Edge.StartNodeId), len);
            var extEnd = ComputeExtension(maxWidthAtNode.GetValueOrDefault(eg.Edge.EndNodeId), len);
            // If both ends would consume the whole segment, shrink proportionally
            if (extStart + extEnd >= len * 0.95)
            {
                var scale = (len * MaxExtensionFraction * 2) / Math.Max(extStart + extEnd, _docTolerance);
                extStart *= scale;
                extEnd *= scale;
            }

            eg.ExtStart = extStart;
            eg.ExtEnd = extEnd;
            edgeData[kv.Key] = eg;
        }

        // 2. Links
        foreach (var eg in edgeData.Values)
        {
            try
            {
                var n = BuildLink(doc, eg);
                result.CreatedCount += n;
            }
            catch (Exception ex)
            {
                graph.Issues.Add(new NetworkIssue
                {
                    Type = "geometry_generation_failed",
                    Severity = IssueSeverity.Error,
                    Message = $"Link failed for edge {eg.Edge.RhinoObjectId.ToString()[..8]}…: {ex.Message}",
                    RelatedEdgeIds = new List<Guid> { eg.Edge.RhinoObjectId },
                });
                result.FailedLinks++;
            }
        }

        // 3. Hubs
        var edgesByNode = new Dictionary<string, List<EdgeGeom>>(StringComparer.Ordinal);
        foreach (var eg in edgeData.Values)
        {
            void Add(string nodeId)
            {
                if (!edgesByNode.TryGetValue(nodeId, out var list))
                {
                    list = new List<EdgeGeom>();
                    edgesByNode[nodeId] = list;
                }
                list.Add(eg);
            }
            Add(eg.Edge.StartNodeId);
            Add(eg.Edge.EndNodeId);
        }

        foreach (var node in graph.Nodes)
        {
            if (!edgesByNode.TryGetValue(node.Id, out var incident) || incident.Count == 0)
                continue;

            // Acute-angle detection among consecutive incident directions
            if (incident.Count >= 2 && HasAcuteAngle(node, incident))
            {
                graph.Issues.Add(new NetworkIssue
                {
                    Type = "acute_angle_intersection",
                    Severity = IssueSeverity.Warning,
                    Message = $"Node {node.Id} has edge angle < {AcuteAngleDegrees}° — hub geometry may self-intersect",
                    RelatedNodeId = node.Id,
                    RelatedEdgeIds = incident.Select(e => e.Edge.RhinoObjectId).ToList(),
                });
            }

            try
            {
                var n = BuildHub(doc, node, incident);
                result.CreatedCount += n;
            }
            catch (Exception ex)
            {
                graph.Issues.Add(new NetworkIssue
                {
                    Type = "geometry_generation_failed",
                    Severity = IssueSeverity.Error,
                    Message = $"Hub failed at {node.Id}: {ex.Message}",
                    RelatedNodeId = node.Id,
                    RelatedEdgeIds = incident.Select(e => e.Edge.RhinoObjectId).ToList(),
                });
                result.FailedHubs++;
            }
        }

        doc.Views.Redraw();
        return result;
    }

    private int BuildLink(RhinoDoc doc, EdgeGeom eg)
    {
        var curve = eg.Curve;
        var len = curve.GetLength();
        if (len <= eg.ExtStart + eg.ExtEnd + _docTolerance * 5)
            return 0; // fully consumed by hubs — no link body

        if (!curve.LengthParameter(eg.ExtStart, out var t0))
            return 0;
        if (!curve.LengthParameter(len - eg.ExtEnd, out var t1))
            return 0;
        if (t1 <= t0)
            return 0;

        var mid = curve.Trim(t0, t1);
        if (mid is null || !mid.IsValid)
            return 0;

        var created = 0;
        var halfW = eg.WidthDoc * 0.5;

        // Roadway strip
        var roadwayClosed = BuildOffsetStrip(mid, halfW);
        if (roadwayClosed is not null)
        {
            created += AddPlanarBreps(doc, roadwayClosed, LayerRoadway, edgeId: eg.Edge.RhinoObjectId.ToString());
        }

        // Sidewalks via outer − roadway
        if (eg.SidewalkDoc > _docTolerance)
        {
            var outerHalf = halfW + eg.SidewalkDoc;
            var outerClosed = BuildOffsetStrip(mid, outerHalf);
            if (outerClosed is not null && roadwayClosed is not null)
            {
                var sidewalks = BooleanDifferenceCurves(outerClosed, roadwayClosed);
                foreach (var sw in sidewalks)
                    created += AddPlanarBreps(doc, sw, LayerSidewalk, edgeId: eg.Edge.RhinoObjectId.ToString());
            }
        }

        // Lane markings — only on link mid section
        if (eg.GenerateMarkings && eg.Lanes > 1)
        {
            created += AddLaneMarkings(doc, mid, eg.WidthDoc, eg.Lanes, eg.Edge.RhinoObjectId.ToString());
        }

        mid.Dispose();
        return created;
    }

    private int BuildHub(RhinoDoc doc, RoadNode node, List<EdgeGeom> incident)
    {
        // Degree-1: simple end cap from the single edge tail
        if (incident.Count == 1 || node.Degree == 1)
        {
            var eg = incident[0];
            var ext = IsStart(eg, node.Id) ? eg.ExtStart : eg.ExtEnd;
            if (ext < _docTolerance) return 0;

            var tail = ExtractTail(eg.Curve, node.Id, eg, ext);
            if (tail is null) return 0;

            var created = 0;
            var halfW = eg.WidthDoc * 0.5;
            var roadClosed = BuildOffsetStrip(tail, halfW);
            if (roadClosed is not null)
                created += AddPlanarBreps(doc, roadClosed, LayerRoadway, nodeId: node.Id);

            if (eg.SidewalkDoc > _docTolerance)
            {
                var outer = BuildOffsetStrip(tail, halfW + eg.SidewalkDoc);
                if (outer is not null && roadClosed is not null)
                {
                    foreach (var sw in BooleanDifferenceCurves(outer, roadClosed))
                        created += AddPlanarBreps(doc, sw, LayerSidewalk, nodeId: node.Id);
                }
            }

            tail.Dispose();
            return created;
        }

        // Multi-edge hub: union of rectangular tails
        var roadTails = new List<Curve>();
        var fullTails = new List<Curve>();

        foreach (var eg in incident)
        {
            var ext = IsStart(eg, node.Id) ? eg.ExtStart : eg.ExtEnd;
            if (ext < _docTolerance) continue;

            var tail = ExtractTail(eg.Curve, node.Id, eg, ext);
            if (tail is null) continue;

            var halfW = eg.WidthDoc * 0.5;
            var roadClosed = BuildOffsetStrip(tail, halfW);
            if (roadClosed is not null)
                roadTails.Add(roadClosed);

            if (eg.SidewalkDoc > _docTolerance)
            {
                var fullClosed = BuildOffsetStrip(tail, halfW + eg.SidewalkDoc);
                if (fullClosed is not null)
                    fullTails.Add(fullClosed);
            }
            else if (roadClosed is not null)
            {
                fullTails.Add(roadClosed.DuplicateCurve());
            }

            tail.Dispose();
        }

        if (roadTails.Count == 0)
            return 0;

        var createdHub = 0;

        // Roadway union
        var roadUnion = UnionCurves(roadTails);
        if (roadUnion is null || roadUnion.Count == 0)
        {
            // Fallback: add individual tails without union
            foreach (var c in roadTails)
                createdHub += AddPlanarBreps(doc, c, LayerRoadway, nodeId: node.Id);
            foreach (var c in roadTails) c.Dispose();
            foreach (var c in fullTails) c.Dispose();
            throw new InvalidOperationException("CreateBooleanUnion failed for roadway tails");
        }

        foreach (var c in roadUnion)
            createdHub += AddPlanarBreps(doc, c, LayerRoadway, nodeId: node.Id);

        // Sidewalk = full union − road union
        if (fullTails.Count > 0)
        {
            var fullUnion = UnionCurves(fullTails);
            if (fullUnion is not null && fullUnion.Count > 0 && roadUnion.Count > 0)
            {
                foreach (var outer in fullUnion)
                {
                    foreach (var inner in roadUnion)
                    {
                        foreach (var sw in BooleanDifferenceCurves(outer, inner))
                            createdHub += AddPlanarBreps(doc, sw, LayerSidewalk, nodeId: node.Id);
                    }
                }

                foreach (var c in fullUnion) c.Dispose();
            }
            else
            {
                // Soft failure on sidewalk only — still keep roadway
                RhinoApp.WriteLine($"[UrbanBridge] Hub sidewalk union failed at {node.Id}");
            }
        }

        foreach (var c in roadTails) c.Dispose();
        foreach (var c in fullTails) c.Dispose();
        foreach (var c in roadUnion) c.Dispose();

        return createdHub;
    }

    private static double ComputeExtension(double maxWidthAtNode, double edgeLength)
    {
        // max(widths)/2 * 1.5, clamp to 40% of edge length
        var ext = (maxWidthAtNode * 0.5) * ExtensionFactor;
        var cap = edgeLength * MaxExtensionFraction;
        if (ext > cap) ext = cap;
        if (ext < 0) ext = 0;
        return ext;
    }

    private static bool IsStart(EdgeGeom eg, string nodeId) =>
        string.Equals(eg.Edge.StartNodeId, nodeId, StringComparison.Ordinal);

    private Curve? ExtractTail(Curve full, string nodeId, EdgeGeom eg, double extLen)
    {
        var len = full.GetLength();
        if (extLen <= 0 || len < extLen) return full.DuplicateCurve();

        if (IsStart(eg, nodeId))
        {
            if (!full.LengthParameter(extLen, out var t))
                return null;
            return full.Trim(full.Domain.Min, t);
        }
        else
        {
            if (!full.LengthParameter(len - extLen, out var t))
                return null;
            return full.Trim(t, full.Domain.Max);
        }
    }

    /// <summary>Offset centerline left/right by halfWidth and close into a single planar loop.</summary>
    private Curve? BuildOffsetStrip(Curve center, double halfWidth)
    {
        if (halfWidth <= _docTolerance || center is null || !center.IsValid)
            return null;

        var plane = Plane.WorldXY;
        // Prefer plane of the curve if mostly planar
        if (center.TryGetPlane(out var curvePlane, _docTolerance * 10))
            plane = curvePlane;

        var left = center.Offset(plane, halfWidth, _docTolerance, CurveOffsetCornerStyle.Sharp);
        var right = center.Offset(plane, -halfWidth, _docTolerance, CurveOffsetCornerStyle.Sharp);
        if (left is null || left.Length == 0 || right is null || right.Length == 0)
            return null;

        var l = left[0];
        var r = right[0];
        // Reverse right so we walk l start→end, then to r end→start
        r.Reverse();

        var parts = new List<Curve> { l };
        var closeStart = new LineCurve(l.PointAtEnd, r.PointAtStart);
        parts.Add(closeStart);
        parts.Add(r);
        var closeEnd = new LineCurve(r.PointAtEnd, l.PointAtStart);
        parts.Add(closeEnd);

        var joined = Curve.JoinCurves(parts, _docTolerance * 10);
        if (joined is null || joined.Length == 0)
            return null;

        var loop = joined[0];
        if (!loop.IsClosed)
            loop.MakeClosed(_docTolerance * 10);

        return loop.IsValid ? loop : null;
    }

    private List<Curve> UnionCurves(List<Curve> curves)
    {
        if (curves.Count == 0) return new List<Curve>();
        if (curves.Count == 1) return new List<Curve> { curves[0].DuplicateCurve() };

        try
        {
            var result = Curve.CreateBooleanUnion(curves, _docTolerance);
            if (result is null || result.Length == 0)
                return new List<Curve>();
            return result.ToList();
        }
        catch
        {
            return new List<Curve>();
        }
    }

    private List<Curve> BooleanDifferenceCurves(Curve outer, Curve inner)
    {
        try
        {
            var result = Curve.CreateBooleanDifference(outer, inner, _docTolerance);
            if (result is null || result.Length == 0)
                return new List<Curve>();
            return result.ToList();
        }
        catch
        {
            return new List<Curve>();
        }
    }

    private int AddPlanarBreps(RhinoDoc doc, Curve closed, string layerPath, string? edgeId = null, string? nodeId = null)
    {
        if (closed is null || !closed.IsValid) return 0;
        if (!closed.IsClosed)
            closed.MakeClosed(_docTolerance * 10);

        var breps = Brep.CreatePlanarBreps(closed, _docTolerance);
        if (breps is null || breps.Length == 0)
            return 0;

        var layerIndex = EnsureLayerPath(doc, layerPath, null);
        var count = 0;
        foreach (var brep in breps)
        {
            var attrs = new ObjectAttributes
            {
                LayerIndex = layerIndex,
            };
            attrs.SetUserString(RoadSurfaceCleanup.GeneratedByKey, RoadSurfaceCleanup.GeneratedByValue);
            if (edgeId is not null)
                attrs.SetUserString(RoadSurfaceCleanup.SourceEdgeKey, edgeId);
            if (nodeId is not null)
                attrs.SetUserString(RoadSurfaceCleanup.SourceNodeKey, nodeId);

            if (doc.Objects.AddBrep(brep, attrs) != Guid.Empty)
                count++;
        }

        return count;
    }

    private int AddLaneMarkings(RhinoDoc doc, Curve center, double widthDoc, int lanes, string edgeId)
    {
        if (lanes < 2) return 0;
        var plane = Plane.WorldXY;
        if (center.TryGetPlane(out var cp, _docTolerance * 10))
            plane = cp;

        var layerIndex = EnsureLayerPath(doc, LayerMarkings, null);
        var count = 0;
        // lanes-1 dividers evenly spaced across roadway (not including sidewalks)
        for (var i = 1; i < lanes; i++)
        {
            var offset = -widthDoc * 0.5 + (widthDoc * i / lanes);
            if (Math.Abs(offset) < _docTolerance) continue; // skip exact center if even lanes? still draw
            var offs = center.Offset(plane, offset, _docTolerance, CurveOffsetCornerStyle.Smooth);
            if (offs is null) continue;
            foreach (var c in offs)
            {
                var attrs = new ObjectAttributes { LayerIndex = layerIndex };
                attrs.SetUserString(RoadSurfaceCleanup.GeneratedByKey, RoadSurfaceCleanup.GeneratedByValue);
                attrs.SetUserString(RoadSurfaceCleanup.SourceEdgeKey, edgeId);
                if (doc.Objects.AddCurve(c, attrs) != Guid.Empty)
                    count++;
            }
        }

        return count;
    }

    private bool HasAcuteAngle(RoadNode node, List<EdgeGeom> incident)
    {
        // Direction from node into each edge
        var dirs = new List<Vector3d>();
        foreach (var eg in incident)
        {
            var curve = eg.Curve;
            Point3d p0, p1;
            if (IsStart(eg, node.Id))
            {
                p0 = curve.PointAtStart;
                curve.LengthParameter(Math.Min(eg.ExtStart + _docTolerance, curve.GetLength() * 0.5), out var t);
                p1 = curve.PointAt(t);
            }
            else
            {
                p0 = curve.PointAtEnd;
                var len = curve.GetLength();
                curve.LengthParameter(Math.Max(len - eg.ExtEnd - _docTolerance, len * 0.5), out var t);
                p1 = curve.PointAt(t);
            }

            var v = p1 - p0;
            if (v.Unitize())
                dirs.Add(v);
        }

        for (var i = 0; i < dirs.Count; i++)
        {
            for (var j = i + 1; j < dirs.Count; j++)
            {
                var angle = Vector3d.VectorAngle(dirs[i], dirs[j]) * (180.0 / Math.PI);
                // Smallest angle between directions (also consider opposite fold)
                if (angle > 90) angle = 180 - angle;
                if (angle < AcuteAngleDegrees)
                    return true;
            }
        }

        return false;
    }

    private static double GetSidewalkWidthM(RhinoObject obj, string roadClass)
    {
        var s = obj.Attributes.GetUserString("sidewalk_width_m");
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var w) && w >= 0)
            return w;

        return roadClass.ToLowerInvariant() switch
        {
            "primary" or "secondary" => 2.5,
            "local" => 1.8,
            _ => 0.0, // pedestrian / bike — no separate sidewalk
        };
    }

    private static bool GetGenerateMarkings(RhinoObject obj, string roadClass)
    {
        var s = obj.Attributes.GetUserString("generate_markings");
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;

        return roadClass.ToLowerInvariant() is "primary" or "secondary" or "local";
    }

    private static int EnsureLayerPath(RhinoDoc doc, string fullPath, System.Drawing.Color? color)
    {
        // fullPath like Roads::Surface::Roadway
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer is null || layer.IsDeleted) continue;
            if (layer.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        var parts = fullPath.Split(new[] { "::" }, StringSplitOptions.None);
        var parentIndex = -1;
        var built = "";
        for (var p = 0; p < parts.Length; p++)
        {
            built = p == 0 ? parts[0] : built + "::" + parts[p];
            var found = -1;
            for (var i = 0; i < doc.Layers.Count; i++)
            {
                var layer = doc.Layers[i];
                if (layer is null || layer.IsDeleted) continue;
                if (layer.FullPath.Equals(built, StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }

            if (found >= 0)
            {
                parentIndex = found;
                continue;
            }

            var newLayer = new Layer { Name = parts[p] };
            if (parentIndex >= 0)
                newLayer.ParentLayerId = doc.Layers[parentIndex].Id;
            if (p == parts.Length - 1 && color.HasValue)
                newLayer.Color = color.Value;

            parentIndex = doc.Layers.Add(newLayer);
        }

        return parentIndex >= 0 ? parentIndex : 0;
    }

    private struct EdgeGeom
    {
        public RoadEdge Edge;
        public Curve Curve;
        public double WidthDoc;
        public double SidewalkDoc;
        public int Lanes;
        public bool GenerateMarkings;
        public double ExtStart;
        public double ExtEnd;
    }

    public sealed class GenerationResult
    {
        public int DeletedCount { get; set; }
        public int CreatedCount { get; set; }
        public int FailedLinks { get; set; }
        public int FailedHubs { get; set; }
    }
}
