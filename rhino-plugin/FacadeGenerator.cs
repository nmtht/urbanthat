using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Simple generative facade grid on massing slabs (window outlines as curves).
/// </summary>
public sealed class FacadeGenerator
{
    public const string LayerFacades = "Buildings::Facades";

    private const double WindowW = 1.2; // m
    private const double WindowH = 1.5; // m
    private const double Mullion = 0.8; // m spacing extra
    private const double Margin = 0.6; // m from edges

    private readonly double _tol;
    private readonly double _m2d;

    public FacadeGenerator(RhinoDoc doc)
    {
        _tol = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
        _m2d = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
    }

    public int GenerateFromMassing(RhinoDoc doc)
    {
        FacadeCleanup.DeleteAllGenerated(doc);
        var layer = EnsureLayer(doc, LayerFacades, System.Drawing.Color.FromArgb(200, 220, 240));
        var created = 0;

        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            if (!string.Equals(obj.Attributes.GetUserString(MassingCleanup.GeneratedByKey),
                    MassingCleanup.GeneratedByValue, StringComparison.Ordinal))
                continue;
            if (obj.Geometry is not Brep brep) continue;

            var zoneId = obj.Attributes.GetUserString(MassingCleanup.SourceZoneKey) ?? "";
            created += AddWindowsForBrep(doc, brep, layer, zoneId);
        }

        doc.Views.Redraw();
        return created;
    }

    private int AddWindowsForBrep(RhinoDoc doc, Brep brep, int layerIndex, string zoneId)
    {
        var bb = brep.GetBoundingBox(true);
        var z0 = bb.Min.Z;
        var z1 = bb.Max.Z;
        var height = z1 - z0;
        if (height < _tol * 10) return 0;

        var wWin = WindowW * _m2d;
        var hWin = WindowH * _m2d;
        var gap = Mullion * _m2d;
        var margin = Margin * _m2d;

        // Four vertical bbox faces as facade planes
        var faces = new (Point3d origin, Vector3d u, Vector3d v, double uLen, double vLen)[]
        {
            // south (-Y)
            (new Point3d(bb.Min.X, bb.Min.Y, z0), Vector3d.XAxis, Vector3d.ZAxis, bb.Max.X - bb.Min.X, height),
            // north (+Y)
            (new Point3d(bb.Min.X, bb.Max.Y, z0), Vector3d.XAxis, Vector3d.ZAxis, bb.Max.X - bb.Min.X, height),
            // west (-X)
            (new Point3d(bb.Min.X, bb.Min.Y, z0), Vector3d.YAxis, Vector3d.ZAxis, bb.Max.Y - bb.Min.Y, height),
            // east (+X)
            (new Point3d(bb.Max.X, bb.Min.Y, z0), Vector3d.YAxis, Vector3d.ZAxis, bb.Max.Y - bb.Min.Y, height),
        };

        var n = 0;
        foreach (var (origin, u, v, uLen, vLen) in faces)
        {
            if (uLen < margin * 2 + wWin || vLen < margin * 2 + hWin) continue;

            var uu = u; uu.Unitize();
            var vv = v; vv.Unitize();
            // slight outward offset for visibility
            var outward = Vector3d.CrossProduct(uu, vv);
            if (!outward.Unitize()) continue;
            // orient outward relative to bbox center
            var faceCenter = origin + uu * (uLen * 0.5) + vv * (vLen * 0.5);
            if ((faceCenter - bb.Center) * outward < 0) outward = -outward;
            var o = origin + outward * (_tol * 20);

            for (var x = margin; x + wWin <= uLen - margin; x += wWin + gap)
            {
                for (var y = margin; y + hWin <= vLen - margin; y += hWin + gap)
                {
                    var p0 = o + uu * x + vv * y;
                    var p1 = o + uu * (x + wWin) + vv * y;
                    var p2 = o + uu * (x + wWin) + vv * (y + hWin);
                    var p3 = o + uu * x + vv * (y + hWin);
                    var poly = new PolylineCurve(new[] { p0, p1, p2, p3, p0 });
                    var attrs = new ObjectAttributes { LayerIndex = layerIndex };
                    attrs.SetUserString(FacadeCleanup.GeneratedByKey, FacadeCleanup.GeneratedByValue);
                    if (!string.IsNullOrEmpty(zoneId))
                        attrs.SetUserString(FacadeCleanup.SourceZoneKey, zoneId);
                    if (doc.Objects.AddCurve(poly, attrs) != Guid.Empty)
                        n++;
                }
            }
        }

        return n;
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
