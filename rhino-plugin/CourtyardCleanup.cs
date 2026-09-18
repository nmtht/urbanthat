using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Plugin;

/// <summary>Deletes generated courtyard / in-block green surfaces.</summary>
public static class CourtyardCleanup
{
    public const string GeneratedByKey = "generated_by";
    public const string GeneratedByValue = "courtyard_generator";
    public const string SourceZoneKey = "source_zone_id";
    public const string MassingTypeKey = "massing_type";

    public static int DeleteAllGenerated(RhinoDoc doc)
    {
        var ids = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            if (string.Equals(obj.Attributes.GetUserString(GeneratedByKey), GeneratedByValue, StringComparison.Ordinal))
                ids.Add(obj.Id);
        }

        var n = 0;
        foreach (var id in ids)
        {
            if (doc.Objects.Delete(id, true)) n++;
        }
        return n;
    }

    public static int DeleteForZone(RhinoDoc doc, Guid zoneId)
    {
        var idStr = zoneId.ToString();
        var ids = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            if (!string.Equals(obj.Attributes.GetUserString(GeneratedByKey), GeneratedByValue, StringComparison.Ordinal))
                continue;
            if (!string.Equals(obj.Attributes.GetUserString(SourceZoneKey), idStr, StringComparison.OrdinalIgnoreCase))
                continue;
            ids.Add(obj.Id);
        }

        var n = 0;
        foreach (var id in ids)
        {
            if (doc.Objects.Delete(id, true)) n++;
        }
        return n;
    }
}
