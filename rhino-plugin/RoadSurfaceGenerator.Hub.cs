using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

public sealed partial class RoadSurfaceGenerator
{
    /// <summary>
    /// Hub = boolean-union of incident road tails, then fillet only concave corners.
    /// Sidewalk: same on the outer (road+parking+sidewalk) strip, radius ≈ R + sidewalk.
    /// </summary>
    private int BuildHub(RhinoDoc doc, RoadNode node, List<EdgeGeom> incident, double hubRadiusDoc)
    {
        if (incident.Count == 1 || node.Degree == 1)
            return BuildDeadEndHub(doc, node, incident[0]);

        var roadTails = new List<Curve>();
        var fullTails = new List<Curve>();
        var avgSidewalk = 0.0;
        var swCount = 0;

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

            var fullHalf = halfW + eg.ParkingWidthDoc + eg.SidewalkDoc;
            var fullClosed = BuildOffsetStrip(tail, Math.Max(fullHalf, halfW));
            if (fullClosed is not null)
                fullTails.Add(fullClosed);
            else if (roadClosed is not null)
                fullTails.Add(roadClosed.DuplicateCurve());

            if (eg.SidewalkDoc > _docTolerance)
            {
                avgSidewalk += eg.SidewalkDoc;
                swCount++;
            }

            tail.Dispose();
        }

        if (roadTails.Count == 0)
            return 0;

        if (swCount > 0)
            avgSidewalk /= swCount;

        var created = 0;
        var radius = Math.Max(hubRadiusDoc, _docTolerance * 10);

        // --- Roadway: union → fillet concave corners ---
        var roadUnion = UnionCurves(roadTails);
        if (roadUnion.Count == 0)
            roadUnion = roadTails.Select(c => c.DuplicateCurve()).ToList();

        var roadFilleted = new List<Curve>();
        foreach (var c in roadUnion)
        {
            var f = IntersectionFillet.FilletConcaveCorners(c, radius, _docTolerance);
            roadFilleted.Add(f);
            created += AddPlanarBreps(doc, f, LayerRoadway, nodeId: node.Id);
        }

        // --- Sidewalk: outer union filleted with R + sidewalk, minus roadway ---
        if (fullTails.Count > 0 && avgSidewalk > _docTolerance)
        {
            var fullUnion = UnionCurves(fullTails);
            if (fullUnion.Count == 0)
                fullUnion = fullTails.Select(c => c.DuplicateCurve()).ToList();

            var outerRadius = radius + avgSidewalk;
            foreach (var outer in fullUnion)
            {
                var outerF = IntersectionFillet.FilletConcaveCorners(outer, outerRadius, _docTolerance);
                foreach (var inner in roadFilleted)
                {
                    foreach (var sw in BooleanDifferenceCurves(outerF, inner))
                        created += AddPlanarBreps(doc, sw, LayerSidewalk, nodeId: node.Id);
                }
                if (!ReferenceEquals(outerF, outer))
                    outerF.Dispose();
            }

            foreach (var c in fullUnion)
                c.Dispose();
        }

        foreach (var c in roadTails) c.Dispose();
        foreach (var c in fullTails) c.Dispose();
        foreach (var c in roadUnion) c.Dispose();
        foreach (var c in roadFilleted) c.Dispose();

        return created;
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
        if (roadClosed is not null)
            created += AddPlanarBreps(doc, roadClosed, LayerRoadway, nodeId: node.Id);

        var after = halfW + eg.ParkingWidthDoc;
        if (eg.ParkingWidthDoc > _docTolerance && roadClosed is not null)
        {
            var park = BuildOffsetStrip(tail, after);
            if (park is not null)
            {
                foreach (var p in BooleanDifferenceCurves(park, roadClosed))
                    created += AddPlanarBreps(doc, p, LayerParking, nodeId: node.Id);
            }
        }

        if (eg.SidewalkDoc > _docTolerance)
        {
            var outer = BuildOffsetStrip(tail, after + eg.SidewalkDoc);
            var inner = BuildOffsetStrip(tail, after);
            if (outer is not null && inner is not null)
            {
                foreach (var sw in BooleanDifferenceCurves(outer, inner))
                    created += AddPlanarBreps(doc, sw, LayerSidewalk, nodeId: node.Id);
            }
        }

        tail.Dispose();
        return created;
    }

    /// <summary>Keep radius from attributes; light clamp so fillet fits on short edges.</summary>
    private double ClampHubRadius(RoadNode node, List<EdgeGeom> incident, double requested)
    {
        if (requested <= 0 || incident.Count < 2)
            return requested;

        var minHalf = double.MaxValue;
        foreach (var eg in incident)
            minHalf = Math.Min(minHalf, eg.WidthDoc * 0.5);

        // Fillet arm ≈ R for 90°; keep arm under ~half of typical half-width budget
        var maxR = minHalf * 2.5;
        if (maxR < _docTolerance * 10)
            return requested;
        return Math.Min(requested, maxR);
    }
}
