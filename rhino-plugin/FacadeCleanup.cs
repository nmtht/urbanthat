using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Plugin;

public static class FacadeCleanup
{
    public const string GeneratedByKey = "generated_by";
    public const string GeneratedByValue = "facade_generator";
    public const string SourceZoneKey = "source_zone_id";

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
            if (doc.Objects.Delete(id, true)) n++;
        return n;
    }
}
