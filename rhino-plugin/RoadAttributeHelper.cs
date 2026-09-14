using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>
/// Read / write road User Text on selected curve objects.
/// Keys match TZ §2: road_class, lanes, width_m, is_terminal.
/// </summary>
public static class RoadAttributeHelper
{
    public static readonly string[] RoadClasses =
    {
        "primary", "secondary", "local", "pedestrian", "bike",
    };

    public static readonly Dictionary<string, (int Lanes, double WidthM)> ClassDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["primary"] = (4, 18),
            ["secondary"] = (2, 12),
            ["local"] = (2, 8),
            ["pedestrian"] = (0, 3),
            ["bike"] = (0, 2),
        };

    public const string KeyClass = "road_class";
    public const string KeyLanes = "lanes";
    public const string KeyWidth = "width_m";
    public const string KeyTerminal = "is_terminal";

    /// <summary>Selected curve objects in the active document (non-deleted).</summary>
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

    /// <summary>
    /// Ensure layer <c>Roads</c> exists and move objects onto it, then set default
    /// User Text if keys are missing. Returns number of curves processed.
    /// </summary>
    public static int InitAsRoad(RhinoDoc doc, IReadOnlyList<RhinoObject> curves, string roadClass = "local")
    {
        if (doc is null || curves.Count == 0) return 0;

        if (!ClassDefaults.ContainsKey(roadClass))
            roadClass = "local";
        var (defLanes, defWidth) = ClassDefaults[roadClass];

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
                attrs.SetUserString(KeyWidth, defWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (string.IsNullOrWhiteSpace(strings.Get(KeyTerminal)))
                attrs.SetUserString(KeyTerminal, "false");

            if (doc.Objects.ModifyAttributes(obj, attrs, true))
                count++;
        }

        if (count > 0)
            doc.Views.Redraw();
        return count;
    }

    /// <summary>Write the given attribute values onto all selected curves.</summary>
    public static int ApplyAttributes(
        RhinoDoc doc,
        IReadOnlyList<RhinoObject> curves,
        string roadClass,
        int lanes,
        double widthM,
        bool isTerminal)
    {
        if (doc is null || curves.Count == 0) return 0;

        if (!ClassDefaults.ContainsKey(roadClass))
            roadClass = "local";

        var count = 0;
        foreach (var obj in curves)
        {
            var attrs = obj.Attributes.Duplicate();
            attrs.SetUserString(KeyClass, roadClass);
            attrs.SetUserString(KeyLanes, lanes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            attrs.SetUserString(KeyWidth, widthM.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            attrs.SetUserString(KeyTerminal, isTerminal ? "true" : "false");

            if (doc.Objects.ModifyAttributes(obj, attrs, true))
                count++;
        }

        if (count > 0)
            doc.Views.Redraw();
        return count;
    }

    /// <summary>
    /// Read attributes from the first selected curve (for UI display).
    /// Returns null if nothing selected.
    /// </summary>
    public static (string Class, int Lanes, double Width, bool Terminal)? ReadFirst(IReadOnlyList<RhinoObject> curves)
    {
        if (curves.Count == 0) return null;
        var strings = curves[0].Attributes.GetUserStrings();
        var cls = strings.Get(KeyClass);
        if (string.IsNullOrWhiteSpace(cls) || !ClassDefaults.ContainsKey(cls))
            cls = "local";
        var defaults = ClassDefaults[cls];
        var lanes = int.TryParse(strings.Get(KeyLanes), out var l) ? l : defaults.Lanes;
        var width = double.TryParse(
            strings.Get(KeyWidth),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var w) ? w : defaults.WidthM;
        var terminal = string.Equals(strings.Get(KeyTerminal), "true", StringComparison.OrdinalIgnoreCase);
        return (cls, lanes, width, terminal);
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
}
