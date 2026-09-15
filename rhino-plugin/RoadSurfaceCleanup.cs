using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Rhino;

/// <summary>Removes previously generated road-surface objects by UserText marker.</summary>
public static class RoadSurfaceCleanup
{
    public const string GeneratedByKey = "generated_by";
    public const string GeneratedByValue = "road_surface_generator";
    public const string SourceEdgeKey = "source_edge_id";
    public const string SourceNodeKey = "source_node_id";

    /// <summary>Delete all objects marked as produced by the surface generator.</summary>
    public static int DeleteAllGenerated(RhinoDoc doc)
    {
        if (doc is null) return 0;
        var toDelete = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            var marker = obj.Attributes.GetUserString(GeneratedByKey);
            if (string.Equals(marker, GeneratedByValue, StringComparison.Ordinal))
                toDelete.Add(obj.Id);
        }

        var deleted = 0;
        foreach (var id in toDelete)
        {
            if (doc.Objects.Delete(id, true))
                deleted++;
        }

        return deleted;
    }

    /// <summary>Delete generated objects tied to a specific edge or node (partial regen).</summary>
    public static int DeleteForSource(RhinoDoc doc, string? edgeId, string? nodeId)
    {
        if (doc is null) return 0;
        var toDelete = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            var marker = obj.Attributes.GetUserString(GeneratedByKey);
            if (!string.Equals(marker, GeneratedByValue, StringComparison.Ordinal))
                continue;

            if (edgeId is not null &&
                string.Equals(obj.Attributes.GetUserString(SourceEdgeKey), edgeId, StringComparison.OrdinalIgnoreCase))
            {
                toDelete.Add(obj.Id);
                continue;
            }

            if (nodeId is not null &&
                string.Equals(obj.Attributes.GetUserString(SourceNodeKey), nodeId, StringComparison.OrdinalIgnoreCase))
            {
                toDelete.Add(obj.Id);
            }
        }

        var deleted = 0;
        foreach (var id in toDelete)
        {
            if (doc.Objects.Delete(id, true))
                deleted++;
        }

        return deleted;
    }
}
