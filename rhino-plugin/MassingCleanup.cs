using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Plugin;

/// <summary>Deletes previously generated massing objects tagged with UserText markers.</summary>
public static class MassingCleanup
{
    public const string GeneratedByKey = "generated_by";
    public const string GeneratedByValue = "building_massing_generator";
    public const string SourceZoneKey = "source_zone_id";
    public const string FloorsKey = "floors";
    public const string FarActualKey = "far_actual";
    public const string FarTargetKey = "far_target";

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
