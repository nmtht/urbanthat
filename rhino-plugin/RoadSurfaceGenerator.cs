using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>Road surfaces + pairwise street-corner fillets at hubs.</summary>
public sealed partial class RoadSurfaceGenerator
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
}
