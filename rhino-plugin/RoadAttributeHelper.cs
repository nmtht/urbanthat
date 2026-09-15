using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>Read / write road UserText on selected curves.</summary>
public static class RoadAttributeHelper
{
    public static readonly string[] RoadClasses =
    {
        "primary", "secondary", "local", "pedestrian", "bike",
    };

    public static readonly string[] Directions = { "two_way", "one_way" };

    public static readonly Dictionary<string, (int Lanes, double WidthM, double CornerRadiusM)> ClassDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["primary"] = (4, 18, 8.0),
            ["secondary"] = (2, 12, 6.0),
            ["local"] = (2, 8, 4.0),
            ["pedestrian"] = (0, 3, 2.0),
            ["bike"] = (0, 2, 2.0),
        };

    public const string KeyClass = "road_class";
    public const string KeyLanes = "lanes";
    public const string KeyWidth = "width_m";
    public const string KeyTerminal = "is_terminal";
    public const string KeyDirection = "direction";
    public const string KeyCornerRadius = "corner_radius_m";
    public const string KeyMedian = "median_width_m";
    public const string KeySidewalkGreen = "sidewalk_green_m";

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

    public static int InitAsRoad(RhinoDoc doc, IReadOnlyList<RhinoObject> curves, string roadClass = "local")
    {
        if (doc is null || curves.Count == 0) return 0;
        if (!ClassDefaults.ContainsKey(roadClass))
            roadClass = "local";
        var (defLanes, defWidth, defRadius) = ClassDefaults[roadClass];
        var layerIndex = EnsureRoadsLayer(doc);
        var count = 0;

        foreach (var obj in curves)
        {
            var attrs = obj.Attributes.Duplicate();
            attrs.LayerIndex = layerIndex;
            var strings = attrs.GetUserStrings();

            if (string.IsNullOrWhiteSpace(strings.Get(KeyClass)))
                attrs.SetUserString(KeyClass, roadClass);
            if (string.IsNullOrWhiteSpace(strings.Get(KeyLanes)))
                attrs.SetUserString(KeyLanes, defLanes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (string.IsNullOrWhiteSpace(strings.Get(KeyWidth)))
                attrs.SetUserString(KeyWidth, Format(defWidth));
            if (string.IsNullOrWhiteSpace(strings.Get(KeyTerminal)))
                attrs.SetUserString(KeyTerminal, "false");
            if (string.IsNullOrWhiteSpace(strings.Get(KeyDirection)))
                attrs.SetUserString(KeyDirection, "two_way");
            if (string.IsNullOrWhiteSpace(strings.Get(KeyCornerRadius)))
                attrs.SetUserString(KeyCornerRadius, Format(defRadius));
            if (string.IsNullOrWhiteSpace(strings.Get(KeyMedian)))
                attrs.SetUserString(KeyMedian, "0");
            if (string.IsNullOrWhiteSpace(strings.Get(KeySidewalkGreen)))
                attrs.SetUserString(KeySidewalkGreen, "0");

            if (doc.Objects.ModifyAttributes(obj, attrs, true))
                count++;
        }

        if (count > 0) doc.Views.Redraw();
        return count;
    }

    public static int ApplyAttributes(
        RhinoDoc doc,
        IReadOnlyList<RhinoObject> curves,
        string roadClass,
        int lanes,
        double widthM,
        bool isTerminal,
        string direction,
        double cornerRadiusM,
        double medianWidthM,
        double sidewalkGreenM)
    {
        if (doc is null || curves.Count == 0) return 0;
        if (!ClassDefaults.ContainsKey(roadClass))
            roadClass = "local";
        if (!string.Equals(direction, "one_way", StringComparison.OrdinalIgnoreCase))
            direction = "two_way";

        var count = 0;
        foreach (var obj in curves)
        {
            var attrs = obj.Attributes.Duplicate();
            attrs.SetUserString(KeyClass, roadClass);
            attrs.SetUserString(KeyLanes, lanes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            attrs.SetUserString(KeyWidth, Format(widthM));
            attrs.SetUserString(KeyTerminal, isTerminal ? "true" : "false");
            attrs.SetUserString(KeyDirection, direction);
            attrs.SetUserString(KeyCornerRadius, Format(Math.Max(0, cornerRadiusM)));
            attrs.SetUserString(KeyMedian, Format(Math.Max(0, medianWidthM)));
            attrs.SetUserString(KeySidewalkGreen, Format(Math.Max(0, sidewalkGreenM)));

            if (doc.Objects.ModifyAttributes(obj, attrs, true))
                count++;
        }

        if (count > 0) doc.Views.Redraw();
        return count;
    }

    public static (
        string Class,
        int Lanes,
        double Width,
        bool Terminal,
        string Direction,
        double CornerRadius,
        double Median,
        double SidewalkGreen)? ReadFirst(IReadOnlyList<RhinoObject> curves)
    {
        if (curves.Count == 0) return null;
        var strings = curves[0].Attributes.GetUserStrings();
        var cls = strings.Get(KeyClass);
        if (string.IsNullOrWhiteSpace(cls) || !ClassDefaults.ContainsKey(cls))
            cls = "local";
        var defaults = ClassDefaults[cls];

        double Parse(string key, double fallback)
        {
            var s = strings.Get(key);
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        var lanes = int.TryParse(strings.Get(KeyLanes), out var l) ? l : defaults.Lanes;
        var dir = strings.Get(KeyDirection);
        if (!string.Equals(dir, "one_way", StringComparison.OrdinalIgnoreCase))
            dir = "two_way";

        return (
            cls,
            lanes,
            Parse(KeyWidth, defaults.WidthM),
            string.Equals(strings.Get(KeyTerminal), "true", StringComparison.OrdinalIgnoreCase),
            dir,
            Parse(KeyCornerRadius, defaults.CornerRadiusM),
            Parse(KeyMedian, 0),
            Parse(KeySidewalkGreen, 0));
    }

    public static double DefaultCornerRadiusM(string roadClass)
    {
        if (ClassDefaults.TryGetValue(roadClass, out var d))
            return d.CornerRadiusM;
        return 4.0;
    }

    public static int EnsureRoadsLayer(RhinoDoc doc)
    {
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer is null || layer.IsDeleted) continue;
            if (layer.FullPath.Equals("Roads", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        var newLayer = new Layer
        {
            Name = "Roads",
            Color = System.Drawing.Color.FromArgb(255, 200, 80),
        };
        var index = doc.Layers.Add(newLayer);
        return index >= 0 ? index : 0;
    }

    private static string Format(double v) =>
        v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
}
