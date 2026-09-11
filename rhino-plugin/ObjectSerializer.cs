using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>Converts supported Rhino geometry to the meter-based wire representation.</summary>
public static class ObjectSerializer
{
    private const double MaximumPolylineSegmentMeters = 0.5;

    public static bool TrySerialize(RhinoDoc document, RhinoObject rhinoObject, out ObjectPayload? payload)
    {
        payload = null;
        var scale = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);
        var layer = document.Layers[rhinoObject.Attributes.LayerIndex]?.FullPath ?? "Default";
        var userStrings = rhinoObject.Attributes.GetUserStrings();
        var attributes = userStrings.AllKeys
            .Where(key => key is not null)
            .ToDictionary(key => key!, key => userStrings[key!] ?? string.Empty, StringComparer.Ordinal);

        switch (rhinoObject.Geometry)
        {
            case Curve curve:
                payload = new ObjectPayload(rhinoObject.Id.ToString(), layer, "polyline", null,
                    new PolylinePayload(SerializeCurve(curve, scale)), attributes);
                return true;
            case Mesh mesh:
                payload = new ObjectPayload(rhinoObject.Id.ToString(), layer, "mesh", SerializeMesh(mesh, scale), null, attributes);
                return true;
            case Brep brep:
                return TrySerializeBrep(rhinoObject.Id, layer, attributes, brep, scale, out payload);
            case Extrusion extrusion:
                return TrySerializeBrep(rhinoObject.Id, layer, attributes, extrusion.ToBrep(), scale, out payload);
            case Surface surface:
                return TrySerializeBrep(rhinoObject.Id, layer, attributes, surface.ToBrep(), scale, out payload);
            default:
                RhinoApp.WriteLine($"[UrbanBridge] Skipping unsupported {rhinoObject.ObjectType}: {rhinoObject.Id}");
                return false;
        }
    }

    private static bool TrySerializeBrep(Guid id, string layer, IReadOnlyDictionary<string, string> attributes, Brep brep, double scale, out ObjectPayload? payload)
    {
        payload = null;
        var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
        if (meshes is null || meshes.Length == 0)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Could not mesh Brep: {id}");
            return false;
        }

        var combined = new Mesh();
        foreach (var mesh in meshes) combined.Append(mesh);
        payload = new ObjectPayload(id.ToString(), layer, "mesh", SerializeMesh(combined, scale), null, attributes);
        return true;
    }

    private static MeshPayload SerializeMesh(Mesh source, double scale)
    {
        var mesh = source.DuplicateMesh();
        mesh.Faces.ConvertQuadsToTriangles();
        var vertices = new List<double>(mesh.Vertices.Count * 3);
        foreach (var vertex in mesh.Vertices)
        {
            vertices.Add(vertex.X * scale);
            vertices.Add(vertex.Y * scale);
            vertices.Add(vertex.Z * scale);
        }

        var indices = new List<int>(mesh.Faces.Count * 3);
        foreach (var face in mesh.Faces)
        {
            indices.Add(face.A);
            indices.Add(face.B);
            indices.Add(face.C);
        }

        IReadOnlyList<double>? normals = null;
        if (mesh.Normals.Count == mesh.Vertices.Count)
        {
            var values = new List<double>(mesh.Normals.Count * 3);
            foreach (var normal in mesh.Normals)
            {
                values.Add(normal.X);
                values.Add(normal.Y);
                values.Add(normal.Z);
            }
            normals = values;
        }

        return new MeshPayload(vertices, indices, normals);
    }

    private static IReadOnlyList<double> SerializeCurve(Curve curve, double scale)
    {
        var maxLengthInDocumentUnits = MaximumPolylineSegmentMeters / scale;
        var segmentCount = Math.Max(1, (int)Math.Ceiling(curve.GetLength() / maxLengthInDocumentUnits));
        var points = new List<double>((segmentCount + 1) * 3);
        var domain = curve.Domain;
        for (var index = 0; index <= segmentCount; index++)
        {
            var point = curve.PointAt(domain.ParameterAt((double)index / segmentCount));
            points.Add(point.X * scale);
            points.Add(point.Y * scale);
            points.Add(point.Z * scale);
        }
        return points;
    }
}
