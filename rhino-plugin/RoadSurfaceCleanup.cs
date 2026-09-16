using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Plugin;

/// <summary>Deletes previously generated road surface objects tagged with UserText markers.</summary>
public static class RoadSurfaceCleanup
{
    public const string GeneratedByKey = "generated_by";
    public const string GeneratedByValue = "road_surface_generator";
    public const string SourceEdgeKey = "source_edge_id";
    public const string SourceNodeKey = "source_node_id";

    public static int DeleteAllGenerated(RhinoDoc doc)
    {
        var ids = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            var v = obj.Attributes.GetUserString(GeneratedByKey);
            if (string.Equals(v, GeneratedByValue, StringComparison.Ordinal))
                ids.Add(obj.Id);
        }
        var deleted = 0;
        foreach (var id in ids)
        {
            if (doc.Objects.Delete(id, true)) deleted++;
        }
        return deleted;
    }
}
