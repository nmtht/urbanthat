using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Primitive trees (trunk cylinder + oval crown) in green zones and along median greenery strips.
/// Roadside trees are placed on the strip centerline, only if point is inside the greenery outline.
/// </summary>
public sealed class TreeGenerator
{
    public const string LayerTrees = "Landscape::Trees";

    private const double MinGapM = 6.0;
    private const double TrunkRadiusM = 0.22;
    private const double TrunkHeightM = 2.2;
    private const double CrownRadiusM = 2.2;
    private const int MaxTreesPerZone = 40;
    private const double RoadsideGapM = 8.0;

    private readonly double _tol;
    private readonly double _m2d;

    public TreeGenerator(RhinoDoc doc)
    {
        _tol = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
        _m2d = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
    }

    public int GenerateForGreenZones(RhinoDoc doc, ZoneAnalysis analysis)
    {
        TreeCleanup.DeleteAllGenerated(doc);
        var layer = EnsureLayer(doc, LayerTrees, System.Drawing.Color.FromArgb(40, 120, 50));
        var created = 0;

        foreach (var zone in analysis.Zones)
        {
            if (!string.Equals(zone.ZoneType, "green", StringComparison.OrdinalIgnoreCase))
                continue;
            created += ScatterInCurve(doc, zone.Boundary, zone.RhinoObjectId, layer, MaxTreesPerZone);
        }

        created += ScatterAlongGreeneryStrips(doc, layer);

        RhinoApp.WriteLine($"[UrbanBridge] Trees: {created} mesh parts.");
        doc.Views.Redraw();
        return created;
    }

    /// <summary>
    /// Place trees in a row along the center of each Roads::Surface::Greenery planar strip.
    /// Point must lie Inside the outer loop of the greenery face.
    /// </summary>
    private int ScatterAlongGreeneryStrips(RhinoDoc doc, int layerIndex)
    {
        var n = 0;
        var gap = RoadsideGapM * _m2d;
        var placed = new List<Point3d>();

        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            var layer = doc.Layers[obj.Attributes.LayerIndex];
            if (layer is null) continue;
            if (!layer.FullPath.Equals(RoadSurfaceGenerator.LayerGreenery, StringComparison.OrdinalIgnoreCase))
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
                if (!face.FrameAt(face.Domain(0).Mid, face.Domain(1).Mid, out var frame))
                    continue;
                if (Math.Abs(frame.ZAxis.Z) < 0.85) continue;

                var loop = face.OuterLoop?.To3dCurve();
                if (loop is null || !loop.IsValid) continue;
                if (!loop.IsClosed)
                    loop.MakeClosed(_tol * 10);
                if (!loop.IsClosed) continue;

                n += PlaceAlongStripCenterline(doc, loop, layerIndex, obj.Id, gap, placed);
            }
        }

        return n;
    }

    private int PlaceAlongStripCenterline(
        RhinoDoc doc, Curve outline, int layerIndex, Guid sourceId, double gap, List<Point3d> placed)
    {
        var bb = outline.GetBoundingBox(true);
        var z0 = bb.Min.Z;
        var dx = bb.Max.X - bb.Min.X;
        var dy = bb.Max.Y - bb.Min.Y;
        if (dx < _tol * 10 && dy < _tol * 10) return 0;

        // Centerline along the longer axis of the strip bbox
        Point3d a, b;
        if (dx >= dy)
        {
            var midY = (bb.Min.Y + bb.Max.Y) * 0.5;
            a = new Point3d(bb.Min.X, midY, z0);
            b = new Point3d(bb.Max.X, midY, z0);
        }
        else
        {
            var midX = (bb.Min.X + bb.Max.X) * 0.5;
            a = new Point3d(midX, bb.Min.Y, z0);
            b = new Point3d(midX, bb.Max.Y, z0);
        }

        var axis = b - a;
        var length = axis.Length;
        if (length < gap) return 0;
        axis.Unitize();

        var rng = new Random(sourceId.GetHashCode());
        var count = 0;
        // Start half-gap from ends so trees stay on the strip
        for (var d = gap * 0.5; d <= length - gap * 0.5; d += gap)
        {
            var p = a + axis * d;
            p.Z = z0;

            if (outline.Contains(p, Plane.WorldXY, _tol) != PointContainment.Inside)
                continue;

            var ok = true;
            foreach (var q in placed)
            {
                if (p.DistanceTo(q) < gap * 0.9)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;

            placed.Add(p);
            var scale = 0.7 + rng.NextDouble() * 0.35;
            count += PlaceTree(doc, p, layerIndex, sourceId, scale);
        }

        return count;
    }

    private int ScatterInCurve(RhinoDoc doc, Curve boundary, Guid zoneId, int layerIndex, int maxTrees)
    {
        if (boundary is null || !boundary.IsClosed) return 0;

        var bbox = boundary.GetBoundingBox(true);
        var z0 = bbox.Min.Z;
        var gap = MinGapM * _m2d;
        var rng = new Random(HashSeed(zoneId));
        var points = new List<Point3d>();
        var attempts = maxTrees * 25;

        for (var a = 0; a < attempts && points.Count < maxTrees; a++)
        {
            var x = bbox.Min.X + rng.NextDouble() * (bbox.Max.X - bbox.Min.X);
            var y = bbox.Min.Y + rng.NextDouble() * (bbox.Max.Y - bbox.Min.Y);
            var p = new Point3d(x, y, z0);
            if (boundary.Contains(p, Plane.WorldXY, _tol) != PointContainment.Inside)
                continue;

            var ok = true;
            foreach (var q in points)
            {
                if (p.DistanceTo(q) < gap)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;
            points.Add(p);
        }

        var n = 0;
        foreach (var p in points)
        {
            var scale = 0.75 + new Random(p.GetHashCode()).NextDouble() * 0.5;
            n += PlaceTree(doc, p, layerIndex, zoneId, scale);
        }
        return n;
    }

    private int PlaceTree(RhinoDoc doc, Point3d p, int layerIndex, Guid sourceId, double scale)
    {
        var trunkR = TrunkRadiusM * _m2d * scale;
        var trunkH = TrunkHeightM * _m2d * scale;
        var crownR = CrownRadiusM * _m2d * scale;

        var trunk = Mesh.CreateFromCylinder(
            new Cylinder(new Circle(new Plane(p, Vector3d.ZAxis), trunkR), trunkH),
            6, 8);

        var crownCenter = p + Vector3d.ZAxis * (trunkH * 0.85 + crownR * 0.35);
        var sphere = Mesh.CreateFromSphere(new Sphere(crownCenter, crownR), 10, 10);
        if (sphere is not null)
        {
            var xform = Transform.Scale(new Plane(crownCenter, Vector3d.ZAxis), 1.0, 1.0, 0.75);
            sphere.Transform(xform);
        }

        var n = 0;
        n += AddMesh(doc, trunk, layerIndex, sourceId, System.Drawing.Color.FromArgb(90, 60, 30));
        n += AddMesh(doc, sphere, layerIndex, sourceId, System.Drawing.Color.FromArgb(40, 130, 55));
        return n;
    }

    private static int AddMesh(RhinoDoc doc, Mesh? mesh, int layerIndex, Guid zoneId, System.Drawing.Color color)
    {
        if (mesh is null || !mesh.IsValid) return 0;
        var attrs = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = color,
        };
        attrs.SetUserString(TreeCleanup.GeneratedByKey, TreeCleanup.GeneratedByValue);
        attrs.SetUserString(TreeCleanup.SourceZoneKey, zoneId.ToString());
        return doc.Objects.AddMesh(mesh, attrs) != Guid.Empty ? 1 : 0;
    }

    private static int HashSeed(Guid id)
    {
        var b = id.ToByteArray();
        var h = 17;
        foreach (var x in b) h = h * 31 + x;
        return h;
    }

    private static int EnsureLayer(RhinoDoc doc, string fullPath, System.Drawing.Color color)
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
            if (p == parts.Length - 1)
                newLayer.Color = color;
            parentIndex = doc.Layers.Add(newLayer);
        }

        return parentIndex >= 0 ? parentIndex : 0;
    }
}
