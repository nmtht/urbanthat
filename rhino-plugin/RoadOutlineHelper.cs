using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Collect planar horizontal road footprints (XY) for clipping zones/massing.
/// Only nearly-horizontal faces on Roadway (and optionally Sidewalk/Parking).
/// </summary>
public static class RoadOutlineHelper
{
    public static List<Brep> CollectPlanarRoadBreps(
        RhinoDoc doc,
        double z,
        double tol,
        bool includeSidewalkAndParking = true)
    {
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
                    // Only top/bottom-ish faces — skip vertical walls
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

                    // Skip degenerate tiny loops
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
                    var diff = Brep.CreateBooleanDifference(piece, cutter, tol);
                    if (diff is { Length: > 0 })
                        next.AddRange(diff);
                    else if (diff is { Length: 0 })
                    {
                        // fully inside cutter — drop
                    }
                    else
                    {
                        // null = boolean failed — keep original
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
}
