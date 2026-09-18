using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Mesh window panels on vertical massing faces + optional green-roof mesh on top.
/// Orientation follows actual Brep faces (works with rotated random pads).
/// </summary>
public sealed class FacadeGenerator
{
    public const string LayerFacades = "Buildings::Facades";
    public const string LayerGreenRoof = "Buildings::GreenRoof";

    private const double WindowW = 1.2;
    private const double WindowH = 1.5;
    private const double Mullion = 0.8;
    private const double Margin = 0.5;
    private const double PanelDepth = 0.08;

    private readonly double _tol;
    private readonly double _m2d;

    public FacadeGenerator(RhinoDoc doc)
    {
        _tol = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
        _m2d = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
    }

    public int GenerateFromMassing(RhinoDoc doc, bool greenRoof)
    {
        FacadeCleanup.DeleteAllGenerated(doc);
        var facadeLayer = EnsureLayer(doc, LayerFacades, System.Drawing.Color.FromArgb(180, 200, 220));
        var roofLayer = EnsureLayer(doc, LayerGreenRoof, System.Drawing.Color.FromArgb(60, 140, 70));
        var created = 0;

        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            if (!string.Equals(obj.Attributes.GetUserString(MassingCleanup.GeneratedByKey),
                    MassingCleanup.GeneratedByValue, StringComparison.Ordinal))
                continue;
            if (obj.Geometry is not Brep brep) continue;

            var zoneId = obj.Attributes.GetUserString(MassingCleanup.SourceZoneKey) ?? "";
            created += AddWindowsOnVerticalFaces(doc, brep, facadeLayer, zoneId);
            if (greenRoof)
                created += AddGreenRoof(doc, brep, roofLayer, zoneId);
        }

        doc.Views.Redraw();
        return created;
    }

    private int AddWindowsOnVerticalFaces(RhinoDoc doc, Brep brep, int layerIndex, string zoneId)
    {
        var wWin = WindowW * _m2d;
        var hWin = WindowH * _m2d;
        var gap = Mullion * _m2d;
        var margin = Margin * _m2d;
        var depth = PanelDepth * _m2d;
        var n = 0;

        foreach (var face in brep.Faces)
        {
            if (!face.IsValid) continue;
            var frame = face.FrameAt(face.Domain(0).Mid, face.Domain(1).Mid);
            if (!frame.IsValid) continue;
            var normal = frame.ZAxis;
            // vertical faces only
            if (Math.Abs(normal.Z) > 0.3) continue;

            var uDom = face.Domain(0);
            var vDom = face.Domain(1);
            // estimate sizes via frame evaluation
            var p00 = face.PointAt(uDom.Min, vDom.Min);
            var p10 = face.PointAt(uDom.Max, vDom.Min);
            var p01 = face.PointAt(uDom.Min, vDom.Max);
            var uLen = p00.DistanceTo(p10);
            var vLen = p00.DistanceTo(p01);
            if (uLen < margin * 2 + wWin || vLen < margin * 2 + hWin) continue;

            // map window grid in UV of face; prefer longer as horizontal
            var uIsHoriz = Math.Abs(p10.Z - p00.Z) < Math.Abs(p01.Z - p00.Z);
            var horizLen = uIsHoriz ? uLen : vLen;
            var vertLen = uIsHoriz ? vLen : uLen;

            for (var x = margin; x + wWin <= horizLen - margin; x += wWin + gap)
            {
                for (var y = margin; y + hWin <= vertLen - margin; y += hWin + gap)
                {
                    double u0, u1, v0, v1;
                    if (uIsHoriz)
                    {
                        u0 = uDom.ParameterAt(x / horizLen);
                        u1 = uDom.ParameterAt((x + wWin) / horizLen);
                        v0 = vDom.ParameterAt(y / vertLen);
                        v1 = vDom.ParameterAt((y + hWin) / vertLen);
                    }
                    else
                    {
                        u0 = uDom.ParameterAt(y / vertLen);
                        u1 = uDom.ParameterAt((y + hWin) / vertLen);
                        v0 = vDom.ParameterAt(x / horizLen);
                        v1 = vDom.ParameterAt((x + wWin) / horizLen);
                    }

                    var a = face.PointAt(u0, v0) + normal * depth;
                    var b = face.PointAt(u1, v0) + normal * depth;
                    var c = face.PointAt(u1, v1) + normal * depth;
                    var d = face.PointAt(u0, v1) + normal * depth;

                    var mesh = new Mesh();
                    mesh.Vertices.Add(a);
                    mesh.Vertices.Add(b);
                    mesh.Vertices.Add(c);
                    mesh.Vertices.Add(d);
                    mesh.Faces.AddFace(0, 1, 2, 3);
                    mesh.Normals.ComputeNormals();

                    n += AddMesh(doc, mesh, layerIndex, zoneId, System.Drawing.Color.FromArgb(160, 190, 210));
                }
            }
        }

        return n;
    }

    private int AddGreenRoof(RhinoDoc doc, Brep brep, int layerIndex, string zoneId)
    {
        var topZ = brep.GetBoundingBox(true).Max.Z;
        var n = 0;
        foreach (var face in brep.Faces)
        {
            var frame = face.FrameAt(face.Domain(0).Mid, face.Domain(1).Mid);
            if (!frame.IsValid) continue;
            if (frame.ZAxis.Z < 0.7) continue; // upward-ish
            var center = face.PointAt(face.Domain(0).Mid, face.Domain(1).Mid);
            if (Math.Abs(center.Z - topZ) > _tol * 50) continue;

            try
            {
                var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                // simpler: mesh just this face via Extract
                var faceBrep = face.DuplicateFace(false)?.DuplicateShallow();
                // Use planar mesh from outer loop
                var loop = face.OuterLoop?.To3dCurve();
                if (loop is null) continue;
                var raised = loop.DuplicateCurve();
                if (raised is null) continue;
                raised.Translate(0, 0, _tol * 5);
                var planar = Brep.CreatePlanarBreps(raised, _tol);
                if (planar is null) continue;
                foreach (var pb in planar)
                {
                    var ms = Mesh.CreateFromBrep(pb, MeshingParameters.FastRenderMesh);
                    if (ms is null) continue;
                    foreach (var m in ms)
                        n += AddMesh(doc, m, layerIndex, zoneId, System.Drawing.Color.FromArgb(55, 130, 60));
                }
            }
            catch { }
        }
        return n;
    }

    private static int AddMesh(RhinoDoc doc, Mesh? mesh, int layerIndex, string zoneId, System.Drawing.Color color)
    {
        if (mesh is null || !mesh.IsValid) return 0;
        var attrs = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = color,
        };
        attrs.SetUserString(FacadeCleanup.GeneratedByKey, FacadeCleanup.GeneratedByValue);
        if (!string.IsNullOrEmpty(zoneId))
            attrs.SetUserString(FacadeCleanup.SourceZoneKey, zoneId);
        return doc.Objects.AddMesh(mesh, attrs) != Guid.Empty ? 1 : 0;
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
