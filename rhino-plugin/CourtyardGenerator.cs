using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// In-block green spaces generated together with massing.
/// Layout depends on massing_type:
///   solid     — setback ring between zone boundary and building envelope
///   perimeter — inner courtyard (hole of the perimeter ring)
///   point     — residual yard around the single tower
///   random    — residual patches between pads
///   row       — residual strips between bars
/// Surfaces are planar meshes on Landscape::Courtyard; optional lawn trees.
/// </summary>
public sealed class CourtyardGenerator
{
    public const string LayerCourtyard = "Landscape::Courtyard";

    private const double LawnTreeGapM = 10.0;
    private const int MaxLawnTreesPerZone = 12;

    private readonly double _tol;
    private readonly double _m2d;
    private readonly double _docToM;

    public CourtyardGenerator(RhinoDoc doc)
    {
        _tol = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
        _m2d = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        _docToM = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
    }

    /// <summary>
    /// Build green regions for one zone given massing footprints already computed.
    /// </summary>
    public int GenerateForZone(
        RhinoDoc doc,
        ZoneRecord zone,
        string massingType,
        Curve envelope,
        IReadOnlyList<Curve> footprints,
        double z0)
    {
        CourtyardCleanup.DeleteForZone(doc, zone.RhinoObjectId);

        var ztype = zone.ZoneType?.ToLowerInvariant() ?? "";
        if (ztype is "green" or "public")
            return 0;

        massingType = (massingType ?? "solid").Trim().ToLowerInvariant();
        var layer = EnsureLayer(doc, LayerCourtyard, System.Drawing.Color.FromArgb(70, 150, 80));
        var created = 0;

        List<Curve> greenLoops;
        try
        {
            greenLoops = massingType switch
            {
                "perimeter" => BuildPerimeterCourtyard(envelope),
                "solid" => BuildSetbackRing(zone.Boundary, envelope, z0),
                "point" or "random" or "row" => BuildResidual(envelope, footprints, z0),
                _ => BuildSetbackRing(zone.Boundary, envelope, z0),
            };
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Courtyard layout failed: {ex.Message}");
            return 0;
        }

        // Soft target from green_ratio (info only — we still emit residual geometry)
        var greenRatio = Math.Clamp(zone.GreenRatio, 0, 1);

        foreach (var loop in greenLoops)
        {
            if (loop is null || !loop.IsValid) continue;
            AlignZ(loop, z0);
            if (!loop.IsClosed)
                loop.MakeClosed(_tol * 10);
            if (!loop.IsClosed) continue;

            var amp = AreaMassProperties.Compute(loop);
            var areaSqm = amp is null ? 0 : amp.Area * _docToM * _docToM;
            if (areaSqm < 2.0) continue; // skip tiny scraps

            created += AddGreenMesh(doc, loop, layer, zone.RhinoObjectId, massingType);

            // Sparse lawn trees on larger patches
            if (areaSqm >= 80)
                created += ScatterLawnTrees(doc, loop, zone.RhinoObjectId, layer);
        }

        if (created > 0)
        {
            RhinoApp.WriteLine(
                $"[UrbanBridge] Courtyard/{massingType}: zone {zone.RhinoObjectId.ToString()[..8]}… " +
                $"{created} green object(s), green_ratio={greenRatio:F2}");
        }

        return created;
    }

    /// <summary>Inner courtyard of perimeter massing = envelope offset inward by block depth.</summary>
    private List<Curve> BuildPerimeterCourtyard(Curve envelope)
    {
        var depth = MassingGenerator.DefaultBlockDepthM * _m2d;
        var inner = OffsetInward(envelope, depth);
        if (inner is null) return new List<Curve>();
        return new List<Curve> { inner };
    }

    /// <summary>Ring between original zone boundary and setback envelope.</summary>
    private List<Curve> BuildSetbackRing(Curve boundary, Curve envelope, double z0)
    {
        var outer = boundary.DuplicateCurve();
        var inner = envelope.DuplicateCurve();
        if (outer is null || inner is null) return new List<Curve>();
        AlignZ(outer, z0);
        AlignZ(inner, z0);
        if (!outer.IsClosed) outer.MakeClosed(_tol * 10);
        if (!inner.IsClosed) inner.MakeClosed(_tol * 10);

        try
        {
            var outerB = Brep.CreatePlanarBreps(outer, _tol);
            var innerB = Brep.CreatePlanarBreps(inner, _tol);
            if (outerB is null || innerB is null || outerB.Length == 0 || innerB.Length == 0)
                return new List<Curve>();

            var rings = Brep.CreateBooleanDifference(outerB, innerB, _tol);
            if (rings is null || rings.Length == 0)
                return new List<Curve>();

            return LoopsFromBreps(rings);
        }
        catch
        {
            return new List<Curve>();
        }
    }

    /// <summary>Envelope minus building footprints → residual yards.</summary>
    private List<Curve> BuildResidual(Curve envelope, IReadOnlyList<Curve> footprints, double z0)
    {
        var env = envelope.DuplicateCurve();
        if (env is null) return new List<Curve>();
        AlignZ(env, z0);
        if (!env.IsClosed) env.MakeClosed(_tol * 10);

        var envBreps = Brep.CreatePlanarBreps(env, _tol);
        if (envBreps is null || envBreps.Length == 0) return new List<Curve>();

        var remaining = envBreps.ToList();
        foreach (var fp in footprints)
        {
            if (fp is null || !fp.IsValid) continue;
            var f = fp.DuplicateCurve();
            if (f is null) continue;
            AlignZ(f, z0);
            if (!f.IsClosed) f.MakeClosed(_tol * 10);
            if (!f.IsClosed) continue;

            Brep[]? cutters;
            try { cutters = Brep.CreatePlanarBreps(f, _tol); }
            catch { continue; }
            if (cutters is null || cutters.Length == 0) continue;

            var next = new List<Brep>();
            foreach (var piece in remaining)
            {
                try
                {
                    // Overload requires IEnumerable + IEnumerable (not Brep + Brep[])
                    var diff = Brep.CreateBooleanDifference(
                        new[] { piece }, cutters, _tol);
                    if (diff is { Length: > 0 })
                        next.AddRange(diff);
                    else if (diff is null)
                        next.Add(piece); // failed — keep
                    // Length 0 = fully covered — drop
                }
                catch
                {
                    next.Add(piece);
                }
            }
            remaining = next;
            if (remaining.Count == 0) break;
        }

        return LoopsFromBreps(remaining.ToArray());
    }

    private List<Curve> LoopsFromBreps(Brep[]? breps)
    {
        var list = new List<Curve>();
        if (breps is null) return list;
        foreach (var b in breps)
        {
            if (b is null || !b.IsValid) continue;
            foreach (var face in b.Faces)
            {
                var loop = face.OuterLoop?.To3dCurve();
                if (loop is null || !loop.IsValid) continue;
                if (!loop.IsClosed) loop.MakeClosed(_tol * 10);
                if (loop.IsClosed)
                    list.Add(loop);
            }
        }
        return list;
    }

    private Curve? OffsetInward(Curve curve, double distance)
    {
        if (distance <= _tol) return null;
        var c = curve.DuplicateCurve();
        if (c is null) return null;
        if (!c.IsClosed) c.MakeClosed(_tol * 10);

        var plane = Plane.WorldXY;
        if (c.TryGetPlane(out var cp, _tol * 10)) plane = cp;
        if (c.ClosedCurveOrientation(plane) == CurveOrientation.Clockwise)
            c.Reverse();

        Curve[]? offsets;
        try
        {
            offsets = c.Offset(plane, -distance, _tol, CurveOffsetCornerStyle.Sharp);
        }
        catch { return null; }

        if (offsets is null || offsets.Length == 0) return null;

        Curve? best = null;
        double bestLen = 0;
        foreach (var o in offsets)
        {
            if (o is null || !o.IsValid) continue;
            if (!o.IsClosed) o.MakeClosed(_tol * 10);
            if (!o.IsClosed) continue;
            var len = o.GetLength();
            if (len > bestLen)
            {
                bestLen = len;
                best = o;
            }
        }
        return best;
    }

    private int AddGreenMesh(RhinoDoc doc, Curve loop, int layerIndex, Guid zoneId, string massingType)
    {
        try
        {
            var planar = Brep.CreatePlanarBreps(loop, _tol);
            if (planar is null || planar.Length == 0) return 0;
            var n = 0;
            foreach (var pb in planar)
            {
                var meshes = Mesh.CreateFromBrep(pb, MeshingParameters.FastRenderMesh);
                if (meshes is null) continue;
                foreach (var mesh in meshes)
                {
                    if (mesh is null || !mesh.IsValid) continue;
                    // Lift slightly above ground to avoid z-fighting with zone proxy
                    mesh.Transform(Transform.Translation(0, 0, _tol * 3));
                    var attrs = new ObjectAttributes
                    {
                        LayerIndex = layerIndex,
                        ColorSource = ObjectColorSource.ColorFromObject,
                        ObjectColor = System.Drawing.Color.FromArgb(75, 155, 85),
                    };
                    attrs.SetUserString(CourtyardCleanup.GeneratedByKey, CourtyardCleanup.GeneratedByValue);
                    attrs.SetUserString(CourtyardCleanup.SourceZoneKey, zoneId.ToString());
                    attrs.SetUserString(CourtyardCleanup.MassingTypeKey, massingType);
                    if (doc.Objects.AddMesh(mesh, attrs) != Guid.Empty)
                        n++;
                }
            }
            return n;
        }
        catch { return 0; }
    }

    private int ScatterLawnTrees(RhinoDoc doc, Curve boundary, Guid zoneId, int layerIndex)
    {
        // Reuse TreeGenerator placement style: oval crown meshes tagged as trees
        var gap = LawnTreeGapM * _m2d;
        var bbox = boundary.GetBoundingBox(true);
        var z0 = bbox.Min.Z;
        var rng = new Random(HashSeed(zoneId) ^ 0xC0FFEE);
        var points = new List<Point3d>();
        var attempts = MaxLawnTreesPerZone * 20;

        for (var a = 0; a < attempts && points.Count < MaxLawnTreesPerZone; a++)
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
        foreach (var p in points)
        {
            var scale = 0.65 + rng.NextDouble() * 0.4;
            n += PlaceSimpleTree(doc, p, layerIndex, zoneId, scale);
        }
        return n;
    }

    private int PlaceSimpleTree(RhinoDoc doc, Point3d p, int layerIndex, Guid zoneId, double scale)
    {
        var trunkR = 0.2 * _m2d * scale;
        var trunkH = 2.0 * _m2d * scale;
        var crownR = 2.0 * _m2d * scale;

        var trunk = Mesh.CreateFromCylinder(
            new Cylinder(new Circle(new Plane(p, Vector3d.ZAxis), trunkR), trunkH), 6, 8);
        var crownCenter = p + Vector3d.ZAxis * (trunkH * 0.85 + crownR * 0.3);
        var sphere = Mesh.CreateFromSphere(new Sphere(crownCenter, crownR), 8, 8);
        if (sphere is not null)
            sphere.Transform(Transform.Scale(new Plane(crownCenter, Vector3d.ZAxis), 1.0, 1.0, 0.75));

        var n = 0;
        n += AddTreeMesh(doc, trunk, layerIndex, zoneId, System.Drawing.Color.FromArgb(90, 60, 30));
        n += AddTreeMesh(doc, sphere, layerIndex, zoneId, System.Drawing.Color.FromArgb(45, 135, 55));
        return n;
    }

    private static int AddTreeMesh(RhinoDoc doc, Mesh? mesh, int layerIndex, Guid zoneId, System.Drawing.Color color)
    {
        if (mesh is null || !mesh.IsValid) return 0;
        var attrs = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = color,
        };
        // Tag as courtyard (same cleanup) so regenerating massing removes them
        attrs.SetUserString(CourtyardCleanup.GeneratedByKey, CourtyardCleanup.GeneratedByValue);
        attrs.SetUserString(CourtyardCleanup.SourceZoneKey, zoneId.ToString());
        return doc.Objects.AddMesh(mesh, attrs) != Guid.Empty ? 1 : 0;
    }

    private static void AlignZ(Curve c, double z0)
    {
        var bb = c.GetBoundingBox(true);
        var dz = z0 - bb.Min.Z;
        if (Math.Abs(dz) > 1e-9)
            c.Translate(0, 0, dz);
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
