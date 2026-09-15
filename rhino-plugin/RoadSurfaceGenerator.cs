using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>Road surfaces + pairwise street-corner fillets at hubs.</summary>
public sealed class RoadSurfaceGenerator
{
    public const string LayerRoadway = "Roads::Surface::Roadway";
    public const string LayerSidewalk = "Roads::Surface::Sidewalk";
    public const string LayerGreenery = "Roads::Surface::Greenery";
    public const string LayerParking = "Roads::Surface::Parking";
    public const string LayerMarkings = "Roads::Markings::LaneLine";
    public const string LayerCrossing = "Roads::Markings::Crossing";
    public const string LayerArrows = "Roads::Markings::Arrow";

    private const double AcuteAngleDegrees = 20.0;
    private const double ExtensionFactor = 1.5;
    private const double MaxExtensionFraction = 0.40;
    private const double ArrowSpacingMeters = 18.0;
    private const double ArrowLengthMeters = 3.0;
    private const double ArrowHalfWidthMeters = 1.0;
    private const double StopLineOffsetMeters = 1.5;
    private const double CrosswalkDepthMeters = 3.0;
    private const double CrosswalkStripeMeters = 0.5;

    private readonly double _docTolerance;
    private readonly double _metersToDoc;

    public RoadSurfaceGenerator(RhinoDoc doc)
    {
        _docTolerance = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
        _metersToDoc = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
    }

    public GenerationResult Generate(RhinoDoc doc, RoadNetworkGraph graph)
    {
        var result = new GenerationResult();
        result.DeletedCount = RoadSurfaceCleanup.DeleteAllGenerated(doc);
        EnsureLayerPath(doc, LayerRoadway, System.Drawing.Color.FromArgb(90, 90, 95));
        EnsureLayerPath(doc, LayerSidewalk, System.Drawing.Color.FromArgb(180, 180, 175));
        EnsureLayerPath(doc, LayerGreenery, System.Drawing.Color.FromArgb(70, 140, 70));
        EnsureLayerPath(doc, LayerParking, System.Drawing.Color.FromArgb(110, 110, 120));
        EnsureLayerPath(doc, LayerMarkings, System.Drawing.Color.FromArgb(240, 240, 220));
        EnsureLayerPath(doc, LayerCrossing, System.Drawing.Color.FromArgb(250, 250, 250));
        EnsureLayerPath(doc, LayerArrows, System.Drawing.Color.FromArgb(250, 250, 230));

        var edgeData = new Dictionary<Guid, EdgeGeom>();
        foreach (var edge in graph.Edges)
        {
            var obj = doc.Objects.FindId(edge.RhinoObjectId);
            if (obj?.Geometry is not Curve curve || !curve.IsValid) continue;
            var strings = obj.Attributes.GetUserStrings();
            edgeData[edge.RhinoObjectId] = new EdgeGeom
            {
                Edge = edge,
                Curve = curve.DuplicateCurve(),
                WidthDoc = edge.WidthMeters * _metersToDoc,
                SidewalkDoc = GetSidewalkWidthM(obj, edge.RoadClass) * _metersToDoc,
                Lanes = edge.Lanes,
                GenerateMarkings = GetGenerateMarkings(obj, edge.RoadClass),
                CornerRadiusDoc = ParseAttr(strings, "corner_radius_m", RoadAttributeHelper.DefaultCornerRadiusM(edge.RoadClass)) * _metersToDoc,
                MedianWidthDoc = ParseAttr(strings, "median_width_m", 0) * _metersToDoc,
                SidewalkGreenDoc = ParseAttr(strings, "sidewalk_green_m", 0) * _metersToDoc,
                ParkingWidthDoc = ParseAttr(strings, "parking_width_m", 0) * _metersToDoc,
                OneWay = string.Equals(strings.Get("direction"), "one_way", StringComparison.OrdinalIgnoreCase),
            };
        }

        var maxWidthAtNode = new Dictionary<string, double>(StringComparer.Ordinal);
        var maxRadiusAtNode = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var eg in edgeData.Values)
        {
            var w = eg.WidthDoc + 2.0 * (eg.SidewalkDoc + eg.ParkingWidthDoc);
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
                extStart *= scale; extEnd *= scale;
            }
            eg.ExtStart = extStart; eg.ExtEnd = extEnd;
            edgeData[kv.Key] = eg;
        }

        foreach (var eg in edgeData.Values)
        {
            try { result.CreatedCount += BuildLink(doc, eg); }
            catch (Exception ex)
            {
                graph.Issues.Add(new NetworkIssue { Type = "geometry_generation_failed", Severity = IssueSeverity.Error,
                    Message = $"Link failed: {ex.Message}", RelatedEdgeIds = new List<Guid> { eg.Edge.RhinoObjectId } });
                result.FailedLinks++;
            }
        }

        var edgesByNode = new Dictionary<string, List<EdgeGeom>>(StringComparer.Ordinal);
        foreach (var eg in edgeData.Values)
        {
            void Add(string id) { if (!edgesByNode.TryGetValue(id, out var list)) { list = new List<EdgeGeom>(); edgesByNode[id] = list; } list.Add(eg); }
            Add(eg.Edge.StartNodeId); Add(eg.Edge.EndNodeId);
        }

        foreach (var node in graph.Nodes)
        {
            if (!edgesByNode.TryGetValue(node.Id, out var incident) || incident.Count == 0) continue;
            try
            {
                var hubRadius = ClampHubRadius(node, incident, maxRadiusAtNode.GetValueOrDefault(node.Id));
                result.CreatedCount += BuildHub(doc, node, incident, hubRadius);
                if (node.Type == NodeType.Intersection || node.Degree >= 3)
                    result.CreatedCount += AddCrossingMarkings(doc, node, incident);
            }
            catch (Exception ex)
            {
                graph.Issues.Add(new NetworkIssue { Type = "geometry_generation_failed", Severity = IssueSeverity.Error,
                    Message = $"Hub failed at {node.Id}: {ex.Message}", RelatedNodeId = node.Id });
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
        if (len <= eg.ExtStart + eg.ExtEnd + _docTolerance * 5) return 0;
        if (!curve.LengthParameter(eg.ExtStart, out var t0) || !curve.LengthParameter(len - eg.ExtEnd, out var t1) || t1 <= t0) return 0;
        var mid = curve.Trim(t0, t1);
        if (mid is null || !mid.IsValid) return 0;
        var created = 0;
        var halfW = eg.WidthDoc * 0.5;
        var edgeId = eg.Edge.RhinoObjectId.ToString();
        var medianHalf = eg.MedianWidthDoc * 0.5;

        if (medianHalf > _docTolerance && medianHalf < halfW - _docTolerance)
        {
            var m = BuildOffsetStrip(mid, medianHalf);
            if (m is not null) created += AddPlanarBreps(doc, m, LayerGreenery, edgeId: edgeId);
        }
        var roadwayClosed = BuildOffsetStrip(mid, halfW);
        if (roadwayClosed is not null)
        {
            if (medianHalf > _docTolerance && medianHalf < halfW - _docTolerance)
            {
                var mi = BuildOffsetStrip(mid, medianHalf);
                if (mi is not null) foreach (var ring in BooleanDifferenceCurves(roadwayClosed, mi)) created += AddPlanarBreps(doc, ring, LayerRoadway, edgeId: edgeId);
                else created += AddPlanarBreps(doc, roadwayClosed, LayerRoadway, edgeId: edgeId);
            }
            else created += AddPlanarBreps(doc, roadwayClosed, LayerRoadway, edgeId: edgeId);
        }
        var afterRoad = halfW;
        if (eg.ParkingWidthDoc > _docTolerance && roadwayClosed is not null)
        {
            var park = BuildOffsetStrip(mid, halfW + eg.ParkingWidthDoc);
            if (park is not null) { foreach (var p in BooleanDifferenceCurves(park, roadwayClosed)) created += AddPlanarBreps(doc, p, LayerParking, edgeId: edgeId); afterRoad = halfW + eg.ParkingWidthDoc; }
        }
        if (eg.SidewalkDoc > _docTolerance)
        {
            var outer = BuildOffsetStrip(mid, afterRoad + eg.SidewalkDoc);
            var inner = BuildOffsetStrip(mid, afterRoad);
            if (outer is not null && inner is not null)
                foreach (var sw in BooleanDifferenceCurves(outer, inner))
                    created += AddPlanarBreps(doc, sw, LayerSidewalk, edgeId: edgeId);
        }
        if (eg.GenerateMarkings && eg.Lanes > 0) created += AddLaneMarkings(doc, mid, eg.WidthDoc, eg.Lanes, eg.OneWay, edgeId);
        if (eg.OneWay) created += AddOneWayArrows(doc, mid, edgeId);
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
            if (roadClosed is not null) created += AddPlanarBreps(doc, roadClosed, LayerRoadway, nodeId: node.Id);
            var after = halfW + eg.ParkingWidthDoc;
            if (eg.ParkingWidthDoc > _docTolerance && roadClosed is not null)
            {
                var park = BuildOffsetStrip(tail, after);
                if (park is not null) foreach (var p in BooleanDifferenceCurves(park, roadClosed)) created += AddPlanarBreps(doc, p, LayerParking, nodeId: node.Id);
            }
            if (eg.SidewalkDoc > _docTolerance)
            {
                var outer = BuildOffsetStrip(tail, after + eg.SidewalkDoc);
                var inner = BuildOffsetStrip(tail, after);
                if (outer is not null && inner is not null)
                    foreach (var sw in BooleanDifferenceCurves(outer, inner))
                        created += AddPlanarBreps(doc, sw, LayerSidewalk, nodeId: node.Id);
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
            if (roadClosed is not null) roadTails.Add(roadClosed);
            var fullHalf = halfW + eg.ParkingWidthDoc + eg.SidewalkDoc;
            var fullClosed = BuildOffsetStrip(tail, Math.Max(fullHalf, halfW));
            if (fullClosed is not null) fullTails.Add(fullClosed);
            else if (roadClosed is not null) fullTails.Add(roadClosed.DuplicateCurve());
            tail.Dispose();
        }
        if (roadTails.Count == 0) return 0;

        var createdHub = 0;
        var roadUnion = UnionCurves(roadTails);
        if (roadUnion is null || roadUnion.Count == 0)
        {
            foreach (var c in roadTails) createdHub += AddPlanarBreps(doc, c, LayerRoadway, nodeId: node.Id);
            foreach (var c in roadTails) c.Dispose();
            foreach (var c in fullTails) c.Dispose();
            throw new InvalidOperationException("CreateBooleanUnion failed for roadway tails");
        }
        foreach (var c in roadUnion) createdHub += AddPlanarBreps(doc, c, LayerRoadway, nodeId: node.Id);

        if (fullTails.Count > 0)
        {
            var fullUnion = UnionCurves(fullTails);
            if (fullUnion is not null && fullUnion.Count > 0)
            {
                foreach (var outer in fullUnion)
                    foreach (var inner in roadUnion)
                        foreach (var sw in BooleanDifferenceCurves(outer, inner))
                            createdHub += AddPlanarBreps(doc, sw, LayerSidewalk, nodeId: node.Id);
                foreach (var c in fullUnion) c.Dispose();
            }
        }

        // KEY: pairwise outer-curb fillets between consecutive roads (reference sketch)
        createdHub += AddPairwiseCornerFillets(doc, node, incident, hubRadiusDoc);

        foreach (var c in roadTails) c.Dispose();
        foreach (var c in fullTails) c.Dispose();
        foreach (var c in roadUnion) c.Dispose();
        return createdHub;
    }

    private int AddPairwiseCornerFillets(RhinoDoc doc, RoadNode node, List<EdgeGeom> incident, double hubRadiusDoc)
    {
        var sorted = IntersectionFillet.SortByOutboundAngle(incident, eg => DirectionFromNode(eg, node.Id));
        if (sorted.Count < 2) return 0;
        var eg0 = sorted[0];
        var nodePt = IsStart(eg0, node.Id) ? eg0.Curve.PointAtStart : eg0.Curve.PointAtEnd;

        var legs = new List<IntersectionFillet.HubLeg>();
        foreach (var eg in sorted)
        {
            var dir = DirectionFromNode(eg, node.Id);
            if (!dir.Unitize()) continue;
            var ext = IsStart(eg, node.Id) ? eg.ExtStart : eg.ExtEnd;
            var outerHalf = eg.WidthDoc * 0.5 + eg.ParkingWidthDoc + eg.SidewalkDoc;
            var radius = eg.CornerRadiusDoc > 0 ? eg.CornerRadiusDoc : hubRadiusDoc;
            legs.Add(new IntersectionFillet.HubLeg
            {
                Outbound = dir,
                OuterHalf = outerHalf,
                RoadHalf = eg.WidthDoc * 0.5,
                Radius = radius,
                ExtLength = Math.Max(ext, outerHalf + radius),
            });
        }
        if (legs.Count < 2) return 0;

        var created = 0;
        foreach (var arc in IntersectionFillet.BuildPairwiseOuterArcs(nodePt, legs, _docTolerance))
            created += AddCurve(doc, arc, LayerSidewalk, nodeId: node.Id);

        var roadLegs = legs.Select(l => new IntersectionFillet.HubLeg
        {
            Outbound = l.Outbound,
            OuterHalf = l.RoadHalf,
            RoadHalf = l.RoadHalf,
            Radius = Math.Min(l.Radius, Math.Max(l.RoadHalf * 1.2, _docTolerance * 10)),
            ExtLength = l.ExtLength,
        }).ToList();
        foreach (var arc in IntersectionFillet.BuildPairwiseOuterArcs(nodePt, roadLegs, _docTolerance))
            created += AddCurve(doc, arc, LayerRoadway, nodeId: node.Id);
        return created;
    }

    private double ClampHubRadius(RoadNode node, List<EdgeGeom> incident, double requested)
    {
        if (requested <= 0 || incident.Count < 2) return requested;
        var sorted = IntersectionFillet.SortByOutboundAngle(incident, eg => DirectionFromNode(eg, node.Id));
        var minClamp = requested;
        for (var i = 0; i < sorted.Count; i++)
        {
            var a = sorted[i]; var b = sorted[(i + 1) % sorted.Count];
            var da = DirectionFromNode(a, node.Id); var db = DirectionFromNode(b, node.Id);
            if (!da.Unitize() || !db.Unitize()) continue;
            var turn = Vector3d.VectorAngle(da, db);
            var halfA = a.WidthDoc * 0.5 + a.ParkingWidthDoc + a.SidewalkDoc;
            var halfB = b.WidthDoc * 0.5 + b.ParkingWidthDoc + b.SidewalkDoc;
            var clamped = IntersectionFillet.ClampRadius(requested, halfA, halfB, turn);
            if (clamped < minClamp) minClamp = clamped;
        }
        return minClamp;
    }

    private int AddCrossingMarkings(RhinoDoc doc, RoadNode node, List<EdgeGeom> incident)
    {
        var created = 0;
        var stopOffset = StopLineOffsetMeters * _metersToDoc;
        var crossDepth = CrosswalkDepthMeters * _metersToDoc;
        foreach (var eg in incident)
        {
            var dir = DirectionFromNode(eg, node.Id);
            if (!dir.Unitize()) continue;
            var nodePt = IsStart(eg, node.Id) ? eg.Curve.PointAtStart : eg.Curve.PointAtEnd;
            var along = Math.Max(eg.ExtStart, eg.ExtEnd);
            if (along < _docTolerance) along = stopOffset;
            var stopCenter = nodePt + dir * (along + stopOffset);
            var perp = Vector3d.CrossProduct(dir, Vector3d.ZAxis);
            if (!perp.Unitize()) { perp = Vector3d.CrossProduct(dir, Vector3d.XAxis); perp.Unitize(); }
            var halfW = eg.WidthDoc * 0.5;
            created += AddCurve(doc, new LineCurve(stopCenter + perp * halfW, stopCenter - perp * halfW), LayerCrossing, nodeId: node.Id);
            var crossStart = nodePt + dir * Math.Max(along * 0.3, _docTolerance * 10);
            var nStripes = Math.Max(2, (int)(crossDepth / Math.Max(CrosswalkStripeMeters * _metersToDoc * 2, _docTolerance)));
            for (var i = 0; i < nStripes; i++)
            {
                var c = crossStart + dir * (crossDepth * ((i + 0.5) / nStripes));
                created += AddCurve(doc, new LineCurve(c + perp * halfW, c - perp * halfW), LayerCrossing, nodeId: node.Id);
            }
        }
        return created;
    }

    private int AddOneWayArrows(RhinoDoc doc, Curve center, string edgeId)
    {
        var len = center.GetLength();
        var spacing = ArrowSpacingMeters * _metersToDoc;
        var arrowLen = ArrowLengthMeters * _metersToDoc;
        var halfW = ArrowHalfWidthMeters * _metersToDoc;
        if (len < arrowLen * 2) return 0;
        var created = 0;
        var plane = Plane.WorldXY;
        if (center.TryGetPlane(out var cp, _docTolerance * 10)) plane = cp;
        for (var d = spacing; d < len - arrowLen; d += spacing)
        {
            if (!center.LengthParameter(d, out var t0) || !center.LengthParameter(d + arrowLen, out var t1)) continue;
            var tip = center.PointAt(t1); var tail = center.PointAt(t0);
            var dir = tip - tail; if (!dir.Unitize()) continue;
            var perp = Vector3d.CrossProduct(dir, plane.ZAxis); if (!perp.Unitize()) continue;
            created += AddCurve(doc, new PolylineCurve(new[] { tail + perp * halfW, tip, tail - perp * halfW }), LayerArrows, edgeId: edgeId);
        }
        return created;
    }

    private Vector3d DirectionFromNode(EdgeGeom eg, string nodeId)
    {
        var curve = eg.Curve;
        if (IsStart(eg, nodeId))
        {
            curve.LengthParameter(Math.Min(1.0 * _metersToDoc, curve.GetLength() * 0.1), out var t);
            return curve.PointAt(t) - curve.PointAtStart;
        }
        var len = curve.GetLength();
        curve.LengthParameter(Math.Max(len - 1.0 * _metersToDoc, len * 0.9), out var t2);
        return curve.PointAt(t2) - curve.PointAtEnd;
    }

    private static double ComputeExtension(double maxWidthAtNode, double edgeLength)
    {
        var ext = (maxWidthAtNode * 0.5) * ExtensionFactor;
        return Math.Max(0, Math.Min(ext, edgeLength * MaxExtensionFraction));
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
        if (halfWidth <= _docTolerance || center is null || !center.IsValid) return null;
        var plane = Plane.WorldXY;
        if (center.TryGetPlane(out var curvePlane, _docTolerance * 10)) plane = curvePlane;
        var left = center.Offset(plane, halfWidth, _docTolerance, CurveOffsetCornerStyle.Sharp);
        var right = center.Offset(plane, -halfWidth, _docTolerance, CurveOffsetCornerStyle.Sharp);
        if (left is null || left.Length == 0 || right is null || right.Length == 0) return null;
        var l = left[0]; var r = right[0]; r.Reverse();
        var parts = new List<Curve> { l, new LineCurve(l.PointAtEnd, r.PointAtStart), r, new LineCurve(r.PointAtEnd, l.PointAtStart) };
        var joined = Curve.JoinCurves(parts, _docTolerance * 10);
        if (joined is null || joined.Length == 0) return null;
        var loop = joined[0];
        if (!loop.IsClosed) loop.MakeClosed(_docTolerance * 10);
        return loop.IsValid ? loop : null;
    }

    private List<Curve> UnionCurves(List<Curve> curves)
    {
        if (curves.Count == 0) return new List<Curve>();
        if (curves.Count == 1) return new List<Curve> { curves[0].DuplicateCurve() };
        try { var result = Curve.CreateBooleanUnion(curves, _docTolerance); return result is null || result.Length == 0 ? new List<Curve>() : result.ToList(); }
        catch { return new List<Curve>(); }
    }

    private List<Curve> BooleanDifferenceCurves(Curve outer, Curve inner)
    {
        try { var result = Curve.CreateBooleanDifference(outer, inner, _docTolerance); return result is null || result.Length == 0 ? new List<Curve>() : result.ToList(); }
        catch { return new List<Curve>(); }
    }

    private int AddPlanarBreps(RhinoDoc doc, Curve closed, string layerPath, string? edgeId = null, string? nodeId = null)
    {
        if (closed is null || !closed.IsValid) return 0;
        if (!closed.IsClosed) closed.MakeClosed(_docTolerance * 10);
        var breps = Brep.CreatePlanarBreps(closed, _docTolerance);
        if (breps is null || breps.Length == 0) return 0;
        var layerIndex = EnsureLayerPath(doc, layerPath, null);
        var count = 0;
        foreach (var brep in breps)
        {
            var attrs = new ObjectAttributes { LayerIndex = layerIndex };
            attrs.SetUserString(RoadSurfaceCleanup.GeneratedByKey, RoadSurfaceCleanup.GeneratedByValue);
            if (edgeId is not null) attrs.SetUserString(RoadSurfaceCleanup.SourceEdgeKey, edgeId);
            if (nodeId is not null) attrs.SetUserString(RoadSurfaceCleanup.SourceNodeKey, nodeId);
            if (doc.Objects.AddBrep(brep, attrs) != Guid.Empty) count++;
        }
        return count;
    }

    private int AddCurve(RhinoDoc doc, Curve c, string layerPath, string? edgeId = null, string? nodeId = null)
    {
        if (c is null || !c.IsValid) return 0;
        var layerIndex = EnsureLayerPath(doc, layerPath, null);
        var attrs = new ObjectAttributes { LayerIndex = layerIndex };
        attrs.SetUserString(RoadSurfaceCleanup.GeneratedByKey, RoadSurfaceCleanup.GeneratedByValue);
        if (edgeId is not null) attrs.SetUserString(RoadSurfaceCleanup.SourceEdgeKey, edgeId);
        if (nodeId is not null) attrs.SetUserString(RoadSurfaceCleanup.SourceNodeKey, nodeId);
        return doc.Objects.AddCurve(c, attrs) != Guid.Empty ? 1 : 0;
    }

    private int AddLaneMarkings(RhinoDoc doc, Curve center, double widthDoc, int lanes, bool oneWay, string edgeId)
    {
        if (lanes < 1) return 0;
        var plane = Plane.WorldXY;
        if (center.TryGetPlane(out var cp, _docTolerance * 10)) plane = cp;
        if (oneWay) return AddCurve(doc, center.DuplicateCurve(), LayerMarkings, edgeId: edgeId);
        if (lanes < 2) return 0;
        var count = 0;
        for (var i = 1; i < lanes; i++)
        {
            var offset = -widthDoc * 0.5 + (widthDoc * i / lanes);
            var offs = center.Offset(plane, offset, _docTolerance, CurveOffsetCornerStyle.Smooth);
            if (offs is null) continue;
            foreach (var c in offs) count += AddCurve(doc, c, LayerMarkings, edgeId: edgeId);
        }
        return count;
    }

    private bool HasAcuteAngle(RoadNode node, List<EdgeGeom> incident)
    {
        var dirs = new List<Vector3d>();
        foreach (var eg in incident) { var v = DirectionFromNode(eg, node.Id); if (v.Unitize()) dirs.Add(v); }
        for (var i = 0; i < dirs.Count; i++)
            for (var j = i + 1; j < dirs.Count; j++)
            {
                var angle = Vector3d.VectorAngle(dirs[i], dirs[j]) * (180.0 / Math.PI);
                if (angle > 90) angle = 180 - angle;
                if (angle < AcuteAngleDegrees) return true;
            }
        return false;
    }

    private static double GetSidewalkWidthM(RhinoObject obj, string roadClass)
    {
        var s = obj.Attributes.GetUserString("sidewalk_width_m");
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) && w >= 0) return w;
        return roadClass.ToLowerInvariant() switch { "primary" or "secondary" => 2.5, "local" => 1.8, _ => 0.0 };
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
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static int EnsureLayerPath(RhinoDoc doc, string fullPath, System.Drawing.Color? color)
    {
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer is null || layer.IsDeleted) continue;
            if (layer.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) return i;
        }
        var parts = fullPath.Split(new[] { "::" }, StringSplitOptions.None);
        var parentIndex = -1; var built = "";
        for (var p = 0; p < parts.Length; p++)
        {
            built = p == 0 ? parts[0] : built + "::" + parts[p];
            var found = -1;
            for (var i = 0; i < doc.Layers.Count; i++)
            {
                var layer = doc.Layers[i];
                if (layer is null || layer.IsDeleted) continue;
                if (layer.FullPath.Equals(built, StringComparison.OrdinalIgnoreCase)) { found = i; break; }
            }
            if (found >= 0) { parentIndex = found; continue; }
            var newLayer = new Layer { Name = parts[p] };
            if (parentIndex >= 0) newLayer.ParentLayerId = doc.Layers[parentIndex].Id;
            if (p == parts.Length - 1 && color.HasValue) newLayer.Color = color.Value;
            parentIndex = doc.Layers.Add(newLayer);
        }
        return parentIndex >= 0 ? parentIndex : 0;
    }

    private struct EdgeGeom
    {
        public RoadEdge Edge; public Curve Curve; public double WidthDoc; public double SidewalkDoc;
        public int Lanes; public bool GenerateMarkings; public double ExtStart; public double ExtEnd;
        public double CornerRadiusDoc; public double MedianWidthDoc; public double SidewalkGreenDoc;
        public double ParkingWidthDoc; public bool OneWay;
    }

    public sealed class GenerationResult
    {
        public int DeletedCount { get; set; }
        public int CreatedCount { get; set; }
        public int FailedLinks { get; set; }
        public int FailedHubs { get; set; }
    }
}
