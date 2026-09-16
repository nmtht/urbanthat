using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>Init / apply / read zone UserText on selected closed curves.</summary>
public static class ZoneAttributeHelper
{
    public const string KeyType = "zone_type";
    public const string KeyFar = "far";
    public const string KeyHeight = "height_max";
    public const string KeySetback = "setback_m";
    public const string KeyGreen = "green_ratio";

    public static List<RhinoObject> GetSelectedCurves(RhinoDoc doc)
    {
        var list = new List<RhinoObject>();
        if (doc is null) return list;
        foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
        {
            if (obj is null || obj.IsDeleted) continue;
            if (obj.Geometry is Curve)
                list.Add(obj);
        }
        return list;
    }

    public static int InitAsZone(RhinoDoc doc, IReadOnlyList<RhinoObject> curves, string zoneType = "residential")
    {
        if (doc is null || curves.Count == 0) return 0;
        if (!ZoneTypeDefaults.IsKnown(zoneType))
            zoneType = "residential";
        var defaults = ZoneTypeDefaults.Get(zoneType);
        var layerIndex = EnsureZonesLayer(doc);
        var count = 0;

        foreach (var obj in curves)
        {
            if (obj.Geometry is Curve c && !c.IsClosed)
            {
                RhinoApp.WriteLine($"[UrbanBridge] Skip non-closed curve {obj.Id.ToString()[..8]}… for zone init.");
                continue;
            }

            var attrs = obj.Attributes.Duplicate();
            attrs.LayerIndex = layerIndex;
            var strings = attrs.GetUserStrings();

            if (string.IsNullOrWhiteSpace(strings.Get(KeyType)))
                attrs.SetUserString(KeyType, zoneType);
            if (string.IsNullOrWhiteSpace(strings.Get(KeyFar)))
                attrs.SetUserString(KeyFar, Format(defaults.Far));
            if (string.IsNullOrWhiteSpace(strings.Get(KeyHeight)))
                attrs.SetUserString(KeyHeight, Format(defaults.HeightMaxM));
            if (string.IsNullOrWhiteSpace(strings.Get(KeySetback)))
                attrs.SetUserString(KeySetback, Format(ZoneTypeDefaults.DefaultSetbackM));
            if (string.IsNullOrWhiteSpace(strings.Get(KeyGreen)))
                attrs.SetUserString(KeyGreen, Format(defaults.GreenRatio));

            if (doc.Objects.ModifyAttributes(obj, attrs, true))
                count++;
        }

        if (count > 0) doc.Views.Redraw();
        return count;
    }

    public static int ApplyAttributes(
        RhinoDoc doc,
        IReadOnlyList<RhinoObject> curves,
        string zoneType,
        double far,
        double heightMax,
        double setbackM,
        double greenRatio)
    {
        if (doc is null || curves.Count == 0) return 0;
        if (!ZoneTypeDefaults.IsKnown(zoneType))
            zoneType = "residential";
        greenRatio = Math.Clamp(greenRatio, 0, 1);

        var count = 0;
        foreach (var obj in curves)
        {
            var attrs = obj.Attributes.Duplicate();
            attrs.SetUserString(KeyType, zoneType);
            attrs.SetUserString(KeyFar, Format(far));
            attrs.SetUserString(KeyHeight, Format(heightMax));
            attrs.SetUserString(KeySetback, Format(setbackM));
            attrs.SetUserString(KeyGreen, Format(greenRatio));
            if (doc.Objects.ModifyAttributes(obj, attrs, true))
                count++;
        }

        if (count > 0) doc.Views.Redraw();
        return count;
    }

    public static (string Type, double Far, double Height, double Setback, double Green)? ReadFirst(
        IReadOnlyList<RhinoObject> curves)
    {
        if (curves.Count == 0) return null;
        var strings = curves[0].Attributes.GetUserStrings();
        var type = strings.Get(KeyType);
        if (string.IsNullOrWhiteSpace(type) || !ZoneTypeDefaults.IsKnown(type))
            type = "residential";
        var d = ZoneTypeDefaults.Get(type);

        double Parse(string key, double fallback)
        {
            var s = strings.Get(key);
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        return (type, Parse(KeyFar, d.Far), Parse(KeyHeight, d.HeightMaxM),
            Parse(KeySetback, ZoneTypeDefaults.DefaultSetbackM), Parse(KeyGreen, d.GreenRatio));
    }

    public static int EnsureZonesLayer(RhinoDoc doc)
    {
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer is null || layer.IsDeleted) continue;
            if (layer.FullPath.Equals("Zones", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        var newLayer = new Layer
        {
            Name = "Zones",
            Color = System.Drawing.Color.FromArgb(100, 160, 220),
        };
        var index = doc.Layers.Add(newLayer);
        return index >= 0 ? index : 0;
    }

    private static string Format(double v) =>
        v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
}
