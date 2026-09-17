using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Plugin;

/// <summary>Deletes generated zone proxy planes.</summary>
public static class ZoneProxyCleanup
{
    public const string GeneratedByKey = "generated_by";
    public const string GeneratedByValue = "zone_proxy_generator";
    public const string SourceZoneKey = "source_zone_id";

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

        var deleted = 0;
        foreach (var id in ids)
        {
            if (doc.Objects.Delete(id, true)) deleted++;
        }
        return deleted;
    }
}
