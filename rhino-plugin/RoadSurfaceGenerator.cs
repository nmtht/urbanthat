using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>
/// Generate roadway / sidewalk / greenery surfaces and lane markings.
/// Links and hubs built independently; corner fillets by road class radius.
/// </summary>
public sealed class RoadSurfaceGenerator
{
    public const string LayerRoadway = "Roads::Surface::Roadway";
    public const string LayerSidewalk = "Roads::Surface::Sidewalk";
    public const string LayerGreenery = "Roads::Surface::Greenery";
    public const string LayerMarkings = "Roads::Markings::LaneLine";

    private const double AcuteAngleDegrees = 20.0;
    private const double ExtensionFactor = 1.5;
    private const double MaxExtensionFraction = 0.40;

    private readonly double _docTolerance;

    public RoadSurfaceGenerator(RhinoDoc doc)
    {
        _docTolerance = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
    }

    public GenerationResult Generate(RhinoDoc doc, RoadNetworkGraph graph)
    {
        var result = new GenerationResult();
        var metersToDoc = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);

        result.DeletedCount = RoadSurfaceCleanup.DeleteAllGenerated(doc);

        EnsureLayerPath(doc, LayerRoadway, System.Drawing.Color.FromArgb(90, 90, 95));
        EnsureLayerPath(doc, LayerSidewalk, System.Drawing.Color.FromArgb(180, 180, 175));
        EnsureLayerPath(doc, LayerGreenery, System.Drawing.Color.FromArgb(70, 140, 70));
        EnsureLayerPath(doc, LayerMarkings, System.Drawing.Color.FromArgb(240, 240, 220));

        var edgeData = new Dictionary<Guid, EdgeGeom>();
        foreach (var edge in graph.Edges)
        {
            var obj = doc.Objects.FindId(edge.RhinoObjectId);
            if (obj?.Geometry is not Curve curve || !curve.IsValid)
                continue;

            var strings = obj.Attributes.GetUserStrings();
            var widthDoc = edge.WidthMeters * metersToDoc;
            var sidewalkDoc = GetSidewalkWidthM(obj, edge.RoadClass) * metersToDoc;
            var radiusM = ParseAttr(strings, "corner_radius_m",
                RoadAttributeHelper.DefaultCornerRadiusM(edge.RoadClass));
            var medianM = ParseAttr(strings, "median_width_m", 0);
            var sidewalkGreenM = ParseAttr(strings, "sidewalk_green_m", 0);
            var oneWay = string.Equals(strings.Get("direction"), "one_way", StringComparison.OrdinalIgnoreCase);

            edgeData[edge.RhinoObjectId] = new EdgeGeom
            {
                Edge = edge,
                Curve = curve.DuplicateCurve(),
                WidthDoc = widthDoc,
                SidewalkDoc = sidewalkDoc,
                Lanes = edge.Lanes,
                GenerateMarkings = GetGenerateMarkings(obj, edge.RoadClass),
                CornerRadiusDoc = radiusM * metersToDoc,
                MedianWidthDoc = medianM * metersToDoc,
                SidewalkGreenDoc = sidewalkGreenM * metersToDoc,
                OneWay = oneWay,
            };
        }

        var maxWidthAtNode = new Dictionary<string, double>(StringComparer.Ordinal);
        var maxRadiusAtNode = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var eg in edgeData.Values)
        {
            var w = eg.WidthDoc + 2.0 * eg.SidewalkDoc;
            maxWidthAtNode[eg.Edge.StartNodeId] = Math.Max(maxWidthAtNode.GetValueOrDefault(eg.Edge.StartNodeId), w);
            maxWidthAtNode[eg.Edge.EndNodeId] = Math.Max(maxWidthAtNode.GetValueOrDefault(eg.Edge.EndNodeId), w);
            maxRadiusAtNode[eg.Edge.StartNodeId] = Math.Max(maxRadiusAtNode.GetValueOrDefault(eg.Edge.StartNodeId), eg.CornerRadiusDoc);
            maxRadiusAtNode[eg.Edge.EndNodeId] = Math.Max(maxRadiusAtNode.GetValueOrDefault(eg.Edge.EndNodeId), eg.CornerRadiusDoc);
        }

        foreach (var kv in edgeData.ToList())
        {
            var eg = kv.Value;
            var len = eg.Curve.GetLength();
            if (len < _docTolerance * 10) continue;

            var extStart = ComputeExtension(maxWidthAtNode.GetValueOrDefault(eg.Edge.StartNodeId), len);
            var extEnd = ComputeExtension(maxWidthAtNode.GetValueOrDefault(eg.Edge.EndNodeId), len);
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

        foreach (var eg in edgeData.Values)
        {
            try
            {
                result.CreatedCount += BuildLink(doc, eg);
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
                var hubRadius = maxRadiusAtNode.GetValueOrDefault(node.Id);
                result.CreatedCount += BuildHub(doc, node, incident, hubRadius);
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
            return 0;

        if (!curve.LengthParameter(eg.ExtStart, out var t0)) return 0;
        if (!curve.LengthParameter(len - eg.ExtEnd, out var t1)) return 0;
        if (t1 <= t0) return 0;

        var mid = curve.Trim(t0, t1);
        if (mid is null || !mid.IsValid) return 0;

        var created = 0;
        var halfW = eg.WidthDoc * 0.5;
        var edgeId = eg.Edge.RhinoObjectId.ToString();

        // Optional center median (green) — carve from roadway conceptually as separate strip
        var medianHalf = eg.MedianWidthDoc * 0.5;
        var roadwayHalf = halfW;
        if (medianHalf > _docTolerance && medianHalf < halfW - _docTolerance)
        {
            var medianClosed = BuildOffsetStrip(mid, medianHalf);
            if (medianClosed is not null)
            {
                var filleted = TryFillet(medianClosed, eg.CornerRadiusDoc * 0.5);
                created += AddPlanarBreps(doc, filleted ?? medianClosed, LayerGreenery, edgeId: edgeId);
            }
        }

        var roadwayClosed = BuildOffsetStrip(mid, roadwayHalf);
        if (roadwayClosed is not null)
        {
            // If median exists, roadway is ring: outer road − median
            if (medianHalf > _docTolerance && medianHalf < halfW - _docTolerance)
            {
                var medianInner = BuildOffsetStrip(mid, medianHalf);
                if (medianInner is not null)
                {
                    foreach (var ring in BooleanDifferenceCurves(roadwayClosed, medianInner))
                    {
                        var f = TryFillet(ring, eg.CornerRadiusDoc * 0.35);
                        created += AddPlanarBreps(doc, f ?? ring, LayerRoadway, edgeId: edgeId);
                    }
                }
                else
                {
                    var f = TryFillet(roadwayClosed, eg.CornerRadiusDoc * 0.35);
                    created += AddPlanarBreps(doc, f ?? roadwayClosed, LayerRoadway, edgeId: edgeId);
                }
            }
            else
            {
                var f = TryFillet(roadwayClosed, eg.CornerRadiusDoc * 0.35);
                created += AddPlanarBreps(doc, f ?? roadwayClosed, LayerRoadway, edgeId: edgeId);
            }
        }

        // Sidewalks + optional sidewalk green strip
        if (eg.SidewalkDoc > _docTolerance)
        {
            var green = Math.Min(eg.SidewalkGreenDoc, eg.SidewalkDoc * 0.9);
            var walkHalf = halfW + eg.SidewalkDoc;
            var outerClosed = BuildOffsetStrip(mid, walkHalf);

            if (green > _docTolerance && outerClosed is not null && roadwayClosed is not null)
            {
                // green band: from roadway edge outward by `green`
                var greenOuterHalf = halfW + green;
                var greenOuter = BuildOffsetStrip(mid, greenOuterHalf);
                if (greenOuter is not null)
                {
                    foreach (var g in BooleanDifferenceCurves(greenOuter, roadwayClosed))
                    {
                        var f = TryFillet(g, eg.CornerRadiusDoc * 0.25);
                        created += AddPlanarBreps(doc, f ?? g, LayerGreenery, edgeId: edgeId);
                    }

                    // remaining sidewalk: outer − greenOuter
                    foreach (var sw in BooleanDifferenceCurves(outerClosed, greenOuter))
                    {
                        var f = TryFillet(sw, eg.CornerRadiusDoc * 0.25);
                        created += AddPlanarBreps(doc, f ?? sw, LayerSidewalk, edgeId: edgeId);
                    }
                }
            }
            else if (outerClosed is not null && roadwayClosed is not null)
            {
                foreach (var sw in BooleanDifferenceCurves(outerClosed, roadwayClosed))
                {
                    var f = TryFillet(sw, eg.CornerRadiusDoc * 0.25);
                    created += AddPlanarBreps(doc, f ?? sw, LayerSidewalk, edgeId: edgeId);
                }
            }
        }

        if (eg.GenerateMarkings && eg.Lanes > 0)
            created += AddLaneMarkings(doc, mid, eg.WidthDoc, eg.Lanes, eg.OneWay, edgeId);

        mid.Dispose();
        return created;
    }

    private int BuildHub(RhinoDoc doc, RoadNode node, List<EdgeGeom> incident, double hubRadiusDoc)
    {
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
            {
                var f = TryFillet(roadClosed, hubRadiusDoc > 0 ? hubRadiusDoc : eg.CornerRadiusDoc);
                created += AddPlanarBreps(doc, f ?? roadClosed, LayerRoadway, nodeId: node.Id);
            }

            if (eg.SidewalkDoc > _docTolerance)
            {
                var outer = BuildOffsetStrip(tail, halfW + eg.SidewalkDoc);
                if (outer is not null && roadClosed is not null)
                {
                    foreach (var sw in BooleanDifferenceCurves(outer, roadClosed))
                    {
                        var f = TryFillet(sw, hubRadiusDoc > 0 ? hubRadiusDoc : eg.CornerRadiusDoc);
                        created += AddPlanarBreps(doc, f ?? sw, LayerSidewalk, nodeId: node.Id);
                    }
                }
            }

            tail.Dispose();
            return created;
        }

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
        var roadUnion = UnionCurves(roadTails);
        if (roadUnion is null || roadUnion.Count == 0)
        {
            foreach (var c in roadTails)
                createdHub += AddPlanarBreps(doc, c, LayerRoadway, nodeId: node.Id);
            foreach (var c in roadTails) c.Dispose();
            foreach (var c in fullTails) c.Dispose();
            throw new InvalidOperationException("CreateBooleanUnion failed for roadway tails");
        }

        foreach (var c in roadUnion)
        {
            var f = TryFillet(c, hubRadiusDoc);
            createdHub += AddPlanarBreps(doc, f ?? c, LayerRoadway, nodeId: node.Id);
        }

        if (fullTails.Count > 0)
        {
            var fullUnion = UnionCurves(fullTails);
            if (fullUnion is not null && fullUnion.Count > 0)
            {
                foreach (var outer in fullUnion)
                {
                    foreach (var inner in roadUnion)
                    {
                        foreach (var sw in BooleanDifferenceCurves(outer, inner))
                        {
                            var f = TryFillet(sw, hubRadiusDoc);
                            createdHub += AddPlanarBreps(doc, f ?? sw, LayerSidewalk, nodeId: node.Id);
                        }
                    }
                }

                foreach (var c in fullUnion) c.Dispose();
            }
        }

        foreach (var c in roadTails) c.Dispose();
        foreach (var c in fullTails) c.Dispose();
        foreach (var c in roadUnion) c.Dispose();

        return createdHub;
    }

    /// <summary>Round sharp corners of a closed planar curve; returns original on failure.</summary>
    private Curve? TryFillet(Curve closed, double radiusDoc)
    {
        if (closed is null || !closed.IsValid || radiusDoc <= _docTolerance * 2)
            return null;

        try
        {
            // RhinoCommon: fillet all corners of a polycurve / polyline-like curve
            var filleted = Curve.CreateFilletCornersCurve(closed, radiusDoc, _docTolerance, Math.PI / 180.0);
            if (filleted is not null && filleted.IsValid)
            {
                if (!filleted.IsClosed)
                    filleted.MakeClosed(_docTolerance * 10);
                return filleted;
            }
        }
        catch
        {
            // keep unfilleted geometry
        }

        return null;
    }

    private static double ComputeExtension(double maxWidthAtNode, double edgeLength)
    {
        var ext = (maxWidthAtNode * 0.5) * ExtensionFactor;
        var cap = edgeLength * MaxExtensionFraction;
        if (ext > cap) ext = cap;
        return Math.Max(0, ext);
    }

    private static bool IsStart(EdgeGeom eg, string nodeId) =>
        string.Equals(eg.Edge.StartNodeId, nodeId, StringComparison.Ordinal);

    private Curve? ExtractTail(Curve full, string nodeId, EdgeGeom eg, double extLen)
    {
        var len = full.GetLength();
        if (extLen <= 0 || len < extLen) return full.DuplicateCurve();

        if (IsStart(eg, nodeId))
        {
            if (!full.LengthParameter(extLen, out var t)) return null;
            return full.Trim(full.Domain.Min, t);
        }

        if (!full.LengthParameter(len - extLen, out var t2)) return null;
        return full.Trim(t2, full.Domain.Max);
    }

    private Curve? BuildOffsetStrip(Curve center, double halfWidth)
    {
        if (halfWidth <= _docTolerance || center is null || !center.IsValid)
            return null;

        var plane = Plane.WorldXY;
        if (center.TryGetPlane(out var curvePlane, _docTolerance * 10))
            plane = curvePlane;

        var left = center.Offset(plane, halfWidth, _docTolerance, CurveOffsetCornerStyle.Sharp);
        var right = center.Offset(plane, -halfWidth, _docTolerance, CurveOffsetCornerStyle.Sharp);
        if (left is null || left.Length == 0 || right is null || right.Length == 0)
            return null;

        var l = left[0];
        var r = right[0];
        r.Reverse();

        var parts = new List<Curve>
        {
            l,
            new LineCurve(l.PointAtEnd, r.PointAtStart),
            r,
            new LineCurve(r.PointAtEnd, l.PointAtStart),
        };

        var joined = Curve.JoinCurves(parts, _docTolerance * 10);
        if (joined is null || joined.Length == 0) return null;

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
            return result is null || result.Length == 0 ? new List<Curve>() : result.ToList();
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
            return result is null || result.Length == 0 ? new List<Curve>() : result.ToList();
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
        if (breps is null || breps.Length == 0) return 0;

        var layerIndex = EnsureLayerPath(doc, layerPath, null);
        var count = 0;
        foreach (var brep in breps)
        {
            var attrs = new ObjectAttributes { LayerIndex = layerIndex };
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

    private int AddLaneMarkings(RhinoDoc doc, Curve center, double widthDoc, int lanes, bool oneWay, string edgeId)
    {
        if (lanes < 1) return 0;
        var plane = Plane.WorldXY;
        if (center.TryGetPlane(out var cp, _docTolerance * 10))
            plane = cp;

        var layerIndex = EnsureLayerPath(doc, LayerMarkings, null);
        var count = 0;

        // one_way: single center line if lanes>=1; two_way: lanes-1 dividers
        if (oneWay)
        {
            // centerline only
            var attrs = new ObjectAttributes { LayerIndex = layerIndex };
            attrs.SetUserString(RoadSurfaceCleanup.GeneratedByKey, RoadSurfaceCleanup.GeneratedByValue);
            attrs.SetUserString(RoadSurfaceCleanup.SourceEdgeKey, edgeId);
            if (doc.Objects.AddCurve(center.DuplicateCurve(), attrs) != Guid.Empty)
                count++;
            return count;
        }

        if (lanes < 2) return 0;

        for (var i = 1; i < lanes; i++)
        {
            var offset = -widthDoc * 0.5 + (widthDoc * i / lanes);
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
            _ => 0.0,
        };
    }

    private static bool GetGenerateMarkings(RhinoObject obj, string roadClass)
    {
        var s = obj.Attributes.GetUserString("generate_markings");
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return roadClass.ToLowerInvariant() is "primary" or "secondary" or "local";
    }

    private static double ParseAttr(System.Collections.Specialized.NameValueCollection strings, string key, double fallback)
    {
        var s = strings.Get(key);
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static int EnsureLayerPath(RhinoDoc doc, string fullPath, System.Drawing.Color? color)
    {
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
        public double CornerRadiusDoc;
        public double MedianWidthDoc;
        public double SidewalkGreenDoc;
        public bool OneWay;
    }

    public sealed class GenerationResult
    {
        public int DeletedCount { get; set; }
        public int CreatedCount { get; set; }
        public int FailedLinks { get; set; }
        public int FailedHubs { get; set; }
    }
}
