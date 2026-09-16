using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

public sealed partial class RoadSurfaceGenerator
{
    private int BuildHub(RhinoDoc doc, RoadNode node, List<EdgeGeom> incident, double hubRadiusDoc)
    {
        if (incident.Count == 1 || node.Degree == 1)
            return BuildDeadEndHub(doc, node, incident[0]);

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

        // Sharp hub union — expected failure is fine: fall back to individual tails (no throw)
        var roadUnion = UnionCurves(roadTails);
        var roadUnionOk = roadUnion is { Count: > 0 };
        if (!roadUnionOk)
            roadUnion = roadTails.Select(c => c.DuplicateCurve()).ToList();

        // Corner pads merged into one solid outline (avoids overlapping Breps for DWG)
        var roadPads = CollectCornerPads(node, incident, hubRadiusDoc, useOuter: false);
        var roadPieces = new List<Curve>();
        roadPieces.AddRange(roadUnion);
        roadPieces.AddRange(roadPads);
        var roadMerged = UnionCurves(roadPieces);
        if (roadMerged is { Count: > 0 })
        {
            foreach (var c in roadMerged)
                createdHub += AddPlanarBreps(doc, c, LayerRoadway, nodeId: node.Id);
            foreach (var c in roadMerged) c.Dispose();
        }
        else
        {
            foreach (var c in roadPieces)
                createdHub += AddPlanarBreps(doc, c, LayerRoadway, nodeId: node.Id);
        }

        // Sidewalk: (full outer − roadway) ∪ outer pads
        if (fullTails.Count > 0)
        {
            var fullUnion = UnionCurves(fullTails);
            var sidewalkPieces = new List<Curve>();
            if (fullUnion is { Count: > 0 })
            {
                foreach (var outer in fullUnion)
                {
                    foreach (var inner in roadUnion)
                        sidewalkPieces.AddRange(BooleanDifferenceCurves(outer, inner));
                }
                foreach (var c in fullUnion) c.Dispose();
            }

            var sidewalkPads = CollectCornerPads(node, incident, hubRadiusDoc, useOuter: true);
            sidewalkPieces.AddRange(sidewalkPads);

            var swMerged = UnionCurves(sidewalkPieces);
            if (swMerged is { Count: > 0 })
            {
                foreach (var c in swMerged)
                    createdHub += AddPlanarBreps(doc, c, LayerSidewalk, nodeId: node.Id);
                foreach (var c in swMerged) c.Dispose();
            }
            else
            {
                foreach (var c in sidewalkPieces)
                    createdHub += AddPlanarBreps(doc, c, LayerSidewalk, nodeId: node.Id);
            }

            foreach (var c in sidewalkPieces) c.Dispose();
        }

        // Decorative outer arcs only (no second Brep layer)
        createdHub += AddCornerArcCurves(doc, node, incident, hubRadiusDoc);

        foreach (var c in roadTails) c.Dispose();
        foreach (var c in fullTails) c.Dispose();
        if (roadUnionOk)
            foreach (var c in roadUnion!) c.Dispose();
        foreach (var c in roadPads) c.Dispose();
        return createdHub;
    }

    private int BuildDeadEndHub(RhinoDoc doc, RoadNode node, EdgeGeom eg)
    {
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

    private List<Curve> CollectCornerPads(RoadNode node, List<EdgeGeom> incident, double hubRadiusDoc, bool useOuter)
    {
        var result = new List<Curve>();
        var sorted = IntersectionFillet.SortByOutboundAngle(incident, eg => DirectionFromNode(eg, node.Id));
        if (sorted.Count < 2) return result;
        var eg0 = sorted[0];
        var nodePt = IsStart(eg0, node.Id) ? eg0.Curve.PointAtStart : eg0.Curve.PointAtEnd;
        var legs = BuildLegs(sorted, node, hubRadiusDoc, forOuter: useOuter);
        if (legs.Count < 2) return result;
        foreach (var pad in IntersectionFillet.BuildPairwiseCornerPads(nodePt, legs, useOuter, _docTolerance))
            result.Add(pad);
        return result;
    }

    private int AddCornerArcCurves(RhinoDoc doc, RoadNode node, List<EdgeGeom> incident, double hubRadiusDoc)
    {
        var sorted = IntersectionFillet.SortByOutboundAngle(incident, eg => DirectionFromNode(eg, node.Id));
        if (sorted.Count < 2) return 0;
        var eg0 = sorted[0];
        var nodePt = IsStart(eg0, node.Id) ? eg0.Curve.PointAtStart : eg0.Curve.PointAtEnd;
        var created = 0;

        var outerLegs = BuildLegs(sorted, node, hubRadiusDoc, forOuter: true);
        foreach (var arc in IntersectionFillet.BuildPairwiseOuterArcs(nodePt, outerLegs, _docTolerance))
            created += AddCurve(doc, arc, LayerSidewalk, nodeId: node.Id);

        var roadLegs = BuildLegs(sorted, node, hubRadiusDoc, forOuter: false);
        foreach (var arc in IntersectionFillet.BuildPairwiseOuterArcs(nodePt, roadLegs, _docTolerance))
            created += AddCurve(doc, arc, LayerRoadway, nodeId: node.Id);

        return created;
    }

    private List<IntersectionFillet.HubLeg> BuildLegs(
        List<EdgeGeom> sorted, RoadNode node, double hubRadiusDoc, bool forOuter)
    {
        var legs = new List<IntersectionFillet.HubLeg>();
        foreach (var eg in sorted)
        {
            var dir = DirectionFromNode(eg, node.Id);
            if (!dir.Unitize()) continue;
            var ext = IsStart(eg, node.Id) ? eg.ExtStart : eg.ExtEnd;
            var outerHalf = eg.WidthDoc * 0.5 + eg.ParkingWidthDoc + eg.SidewalkDoc;
            var roadHalf = eg.WidthDoc * 0.5;
            var radius = eg.CornerRadiusDoc > 0 ? eg.CornerRadiusDoc : hubRadiusDoc;
            if (!forOuter)
                radius = Math.Min(radius, Math.Max(roadHalf * 1.5, _docTolerance * 10));
            legs.Add(new IntersectionFillet.HubLeg
            {
                Outbound = dir,
                OuterHalf = forOuter ? outerHalf : roadHalf,
                RoadHalf = roadHalf,
                Radius = radius,
                ExtLength = Math.Max(ext, outerHalf + radius),
            });
        }
        return legs;
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
