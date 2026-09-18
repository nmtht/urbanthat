using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Collect planar horizontal road footprints (XY) for clipping zones/massing.
/// Includes a small outward buffer so buildings never touch the curb.
/// </summary>
public static class RoadOutlineHelper
{
    /// <summary>Extra setback from roadway edge when clipping massing (meters).</summary>
    public const double MassingRoadBufferM = 1.5;

    public static List<Brep> CollectPlanarRoadBreps(
        RhinoDoc doc,
        double z,
        double tol,
        bool includeSidewalkAndParking = true,
        double bufferMeters = 0)
    {
        var m2d = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        var bufferDoc = Math.Max(0, bufferMeters) * m2d;

        var curves = new List<Curve>();
        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            var layer = doc.Layers[obj.Attributes.LayerIndex];
            if (layer is null) continue;
            var path = layer.FullPath;

            var isRoadway = path.Equals(RoadSurfaceGenerator.LayerRoadway, StringComparison.OrdinalIgnoreCase);
            var isSide = path.Equals(RoadSurfaceGenerator.LayerSidewalk, StringComparison.OrdinalIgnoreCase);
            var isPark = path.Equals(RoadSurfaceGenerator.LayerParking, StringComparison.OrdinalIgnoreCase);
            if (!isRoadway && !(includeSidewalkAndParking && (isSide || isPark)))
                continue;

            Brep? brep = obj.Geometry switch
            {
                Brep b => b,
                Extrusion e => e.ToBrep(),
                _ => null,
            };
            if (brep is null) continue;

            foreach (var face in brep.Faces)
            {
                try
                {
                    if (!face.FrameAt(face.Domain(0).Mid, face.Domain(1).Mid, out var frame))
                        continue;
                    if (Math.Abs(frame.ZAxis.Z) < 0.85)
                        continue;

                    var loop = face.OuterLoop?.To3dCurve();
                    if (loop is null || !loop.IsValid) continue;
                    var flat = loop.DuplicateCurve();
                    if (flat is null) continue;
                    flat.Transform(Transform.PlanarProjection(new Plane(new Point3d(0, 0, z), Vector3d.ZAxis)));
                    if (!flat.IsClosed)
                        flat.MakeClosed(tol * 10);
                    if (!flat.IsClosed || !flat.IsValid) continue;

                    if (bufferDoc > tol)
                    {
                        var buffered = OffsetOutward(flat, bufferDoc, tol);
                        if (buffered is not null)
                            flat = buffered;
                    }

                    var amp = AreaMassProperties.Compute(flat);
                    if (amp is null || amp.Area < tol * tol * 10)
                        continue;

                    curves.Add(flat);
                }
                catch { }
            }
        }

        var result = new List<Brep>();
        foreach (var c in curves)
        {
            try
            {
                var pieces = Brep.CreatePlanarBreps(c, tol);
                if (pieces is null) continue;
                foreach (var p in pieces)
                {
                    if (p is not null && p.IsValid)
                        result.Add(p);
                }
            }
            catch { }
        }

        return result;
    }

    /// <summary>Split a closed curve by road footprints → multiple residual parcels.</summary>
    public static List<Curve> SplitCurveByRoads(
        RhinoDoc doc, Curve boundary, double z, double tol, double bufferMeters = MassingRoadBufferM)
    {
        var roads = CollectPlanarRoadBreps(doc, z, tol, includeSidewalkAndParking: true, bufferMeters: bufferMeters);
        if (roads.Count == 0)
        {
            var d = boundary.DuplicateCurve();
            return d is null ? new List<Curve>() : new List<Curve> { d };
        }

        var c = boundary.DuplicateCurve();
        if (c is null) return new List<Curve>();
        c.Transform(Transform.PlanarProjection(new Plane(new Point3d(0, 0, z), Vector3d.ZAxis)));
        if (!c.IsClosed) c.MakeClosed(tol * 10);

        Brep[]? subject;
        try { subject = Brep.CreatePlanarBreps(c, tol); }
        catch { return new List<Curve> { c }; }
        if (subject is null || subject.Length == 0) return new List<Curve> { c };

        var remaining = subject.ToList();
        foreach (var road in roads)
        {
            var next = new List<Brep>();
            foreach (var piece in remaining)
            {
                try
                {
                    var diff = Brep.CreateBooleanDifference(new[] { piece }, new[] { road }, tol);
                    if (diff is { Length: > 0 })
                        next.AddRange(diff);
                    else if (diff is null)
                        next.Add(piece);
                    // Length 0 = fully covered by road — drop
                }
                catch
                {
                    next.Add(piece);
                }
            }
            remaining = next;
            if (remaining.Count == 0) break;
        }

        var loops = new List<Curve>();
        var m2d = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        var minArea = 25.0 * m2d * m2d; // drop scraps &lt; 25 m²

        foreach (var b in remaining)
        {
            if (b is null || !b.IsValid) continue;
            foreach (var face in b.Faces)
            {
                var loop = face.OuterLoop?.To3dCurve();
                if (loop is null || !loop.IsValid) continue;
                if (!loop.IsClosed) loop.MakeClosed(tol * 10);
                if (!loop.IsClosed) continue;
                var amp = AreaMassProperties.Compute(loop);
                if (amp is null || amp.Area < minArea) continue;
                loops.Add(loop);
            }
        }

        if (loops.Count == 0)
        {
            // entire zone under road — keep original so analysis still works
            return new List<Curve> { c };
        }

        return loops;
    }

    public static List<Brep> Subtract(Brep subject, List<Brep> cutters, double tol)
    {
        var current = new List<Brep> { subject };
        foreach (var cutter in cutters)
        {
            var next = new List<Brep>();
            foreach (var piece in current)
            {
                try
                {
                    var diff = Brep.CreateBooleanDifference(new[] { piece }, new[] { cutter }, tol);
                    if (diff is { Length: > 0 })
                        next.AddRange(diff);
                    else if (diff is { Length: 0 })
                    {
                        // fully inside cutter — drop (NO fallback to original)
                    }
                    else
                    {
                        // null = boolean failed — keep
                        next.Add(piece);
                    }
                }
                catch
                {
                    next.Add(piece);
                }
            }
            current = next;
            if (current.Count == 0) break;
        }

        return current;
    }

    private static Curve? OffsetOutward(Curve curve, double distance, double tol)
    {
        var c = curve.DuplicateCurve();
        if (c is null) return null;
        var plane = Plane.WorldXY;
        if (c.TryGetPlane(out var cp, tol * 10)) plane = cp;
        if (c.ClosedCurveOrientation(plane) == CurveOrientation.Clockwise)
            c.Reverse();

        try
        {
            // CCW curve: positive offset is outward
            var offs = c.Offset(plane, distance, tol, CurveOffsetCornerStyle.Sharp);
            if (offs is null || offs.Length == 0) return null;
            Curve? best = null;
            double bestA = 0;
            foreach (var o in offs)
            {
                if (o is null || !o.IsValid) continue;
                if (!o.IsClosed) o.MakeClosed(tol * 10);
                if (!o.IsClosed) continue;
                var amp = AreaMassProperties.Compute(o);
                var a = amp?.Area ?? 0;
                if (a > bestA)
                {
                    bestA = a;
                    best = o;
                }
            }
            return best;
        }
        catch { return null; }
    }
}
