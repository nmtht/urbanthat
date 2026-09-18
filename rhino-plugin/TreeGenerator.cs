using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>Primitive trees (trunk + crown) scattered in green zones.</summary>
public sealed class TreeGenerator
{
    public const string LayerTrees = "Landscape::Trees";

    private const double MinGapM = 6.0;
    private const double TrunkRadiusM = 0.25;
    private const double TrunkHeightM = 2.5;
    private const double CrownRadiusM = 2.0;
    private const double CrownHeightM = 4.0;
    private const int MaxTreesPerZone = 40;

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
            created += ScatterInZone(doc, zone, layer);
        }

        doc.Views.Redraw();
        return created;
    }

    private int ScatterInZone(RhinoDoc doc, ZoneRecord zone, int layerIndex)
    {
        var boundary = zone.Boundary;
        if (boundary is null || !boundary.IsClosed) return 0;

        var bbox = boundary.GetBoundingBox(true);
        var z0 = bbox.Min.Z;
        var gap = MinGapM * _m2d;
        var rng = new Random(HashSeed(zone.RhinoObjectId));
        var points = new List<Point3d>();
        var attempts = MaxTreesPerZone * 25;

        for (var a = 0; a < attempts && points.Count < MaxTreesPerZone; a++)
        {
            var x = bbox.Min.X + rng.NextDouble() * (bbox.Max.X - bbox.Min.X);
            var y = bbox.Min.Y + rng.NextDouble() * (bbox.Max.Y - bbox.Min.Y);
            var p = new Point3d(x, y, z0);
            if (boundary.Contains(p, Plane.WorldXY, _tol) != PointContainment.Inside)
                continue;

            var ok = true;
            foreach (var q in points)
            {
                if (p.DistanceTo(q) < gap) { ok = false; break; }
            }
            if (!ok) continue;
            points.Add(p);
        }

        var n = 0;
        var trunkR = TrunkRadiusM * _m2d;
        var trunkH = TrunkHeightM * _m2d;
        var crownR = CrownRadiusM * _m2d;
        var crownH = CrownHeightM * _m2d;

        foreach (var p in points)
        {
            // size jitter
            var scale = 0.75 + (HashSeed(zone.RhinoObjectId) % 50) / 100.0 * 0.5;
            scale = 0.75 + new Random(p.GetHashCode()).NextDouble() * 0.5;

            var trunk = Mesh.CreateFromCylinder(
                new Cylinder(new Circle(new Plane(p, Vector3d.ZAxis), trunkR * scale), trunkH * scale),
                6, 1);
            var crownBase = p + Vector3d.ZAxis * (trunkH * scale * 0.7);
            var crown = Mesh.CreateFromCone(
                new Cone(new Plane(crownBase, Vector3d.ZAxis), crownH * scale, crownR * scale),
                8, true);

            n += AddMesh(doc, trunk, layerIndex, zone.RhinoObjectId, System.Drawing.Color.FromArgb(90, 60, 30));
            n += AddMesh(doc, crown, layerIndex, zone.RhinoObjectId, System.Drawing.Color.FromArgb(40, 130, 55));
        }

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
            if (found >= 0) { parentIndex = found; continue; }
            var newLayer = new Layer { Name = parts[p] };
            if (parentIndex >= 0) newLayer.ParentLayerId = doc.Layers[parentIndex].Id;
            if (p == parts.Length - 1) newLayer.Color = color;
            parentIndex = doc.Layers.Add(newLayer);
        }
        return parentIndex >= 0 ? parentIndex : 0;
    }
}
