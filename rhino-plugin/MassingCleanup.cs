using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Rhino;

/// <summary>Removes generated building massing by UserText marker.</summary>
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
}
