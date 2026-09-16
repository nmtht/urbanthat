using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Rhino;

public sealed partial class RoadSurfaceGenerator
{
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
                if (park is not null)
                    foreach (var p in BooleanDifferenceCurves(park, roadClosed))
                        created += AddPlanarBreps(doc, p, LayerParking, nodeId: node.Id);
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
        foreach (var pad in IntersectionFillet.BuildPairwiseCornerPads(nodePt, legs, useOuter: true, _docTolerance))
            created += AddPlanarBreps(doc, pad, LayerSidewalk, nodeId: node.Id);

        var roadLegs = legs.Select(l => new IntersectionFillet.HubLeg
        {
            Outbound = l.Outbound,
            OuterHalf = l.RoadHalf,
            RoadHalf = l.RoadHalf,
            Radius = Math.Min(l.Radius, Math.Max(l.RoadHalf * 1.5, _docTolerance * 10)),
            ExtLength = l.ExtLength,
        }).ToList();
        foreach (var arc in IntersectionFillet.BuildPairwiseOuterArcs(nodePt, roadLegs, _docTolerance))
            created += AddCurve(doc, arc, LayerRoadway, nodeId: node.Id);
        foreach (var pad in IntersectionFillet.BuildPairwiseCornerPads(nodePt, roadLegs, useOuter: false, _docTolerance))
            created += AddPlanarBreps(doc, pad, LayerRoadway, nodeId: node.Id);
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
}
