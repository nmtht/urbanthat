using System.Drawing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Planar proxy fills for zones (color by zone_type), optionally clipped by roadway surfaces.
/// </summary>
public sealed class ZoneProxyGenerator
{
    public const string LayerProxy = "Zones::Proxy";

    private readonly double _tolerance;

    public ZoneProxyGenerator(RhinoDoc doc)
    {
        _tolerance = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
    }

    public int RegenerateAll(RhinoDoc doc, ZoneAnalysis analysis)
    {
        ZoneProxyCleanup.DeleteAllGenerated(doc);
        EnsureLayer(doc, LayerProxy, Color.FromArgb(120, 160, 200));

        var roadways = CollectRoadwayBreps(doc);
        var created = 0;
        foreach (var zone in analysis.Zones)
            created += CreateProxy(doc, zone, roadways);

        doc.Views.Redraw();
        return created;
    }

    public int CreateProxy(RhinoDoc doc, ZoneRecord zone, List<Brep>? roadways = null)
    {
        ZoneProxyCleanup.DeleteForZone(doc, zone.RhinoObjectId);

        var boundary = zone.Boundary.DuplicateCurve();
        if (boundary is null || !boundary.IsValid) return 0;
        if (!boundary.IsClosed)
            boundary.MakeClosed(_tolerance * 10);
        if (!boundary.IsClosed) return 0;

        var pieces = Brep.CreatePlanarBreps(boundary, _tolerance);
        if (pieces is null || pieces.Length == 0) return 0;

        roadways ??= CollectRoadwayBreps(doc);
        var color = ColorForType(zone.ZoneType);
        var layerIndex = EnsureLayer(doc, LayerProxy, color);
        var created = 0;

        foreach (var piece in pieces)
        {
            var remaining = new List<Brep> { piece };
            if (roadways.Count > 0)
            {
                var next = new List<Brep>();
                foreach (var b in remaining)
                {
                    var clipped = SubtractRoadways(b, roadways);
                    if (clipped.Count == 0)
                        next.Add(b); // keep uncut if boolean fails
                    else
                        next.AddRange(clipped);
                }
                remaining = next;
            }

            foreach (var brep in remaining)
            {
                if (brep is null || !brep.IsValid) continue;
                var attrs = new ObjectAttributes
                {
                    LayerIndex = layerIndex,
                    ColorSource = ObjectColorSource.ColorFromObject,
                    ObjectColor = color,
                };
                attrs.SetUserString(ZoneProxyCleanup.GeneratedByKey, ZoneProxyCleanup.GeneratedByValue);
                attrs.SetUserString(ZoneProxyCleanup.SourceZoneKey, zone.RhinoObjectId.ToString());
                attrs.SetUserString("zone_type", zone.ZoneType);
                if (doc.Objects.AddBrep(brep, attrs) != Guid.Empty)
                    created++;
            }
        }

        return created;
    }

    private List<Brep> SubtractRoadways(Brep zoneBrep, List<Brep> roadways)
    {
        var current = new List<Brep> { zoneBrep };
        foreach (var road in roadways)
        {
            var next = new List<Brep>();
            foreach (var piece in current)
            {
                try
                {
                    var diff = Brep.CreateBooleanDifference(piece, road, _tolerance);
                    if (diff is { Length: > 0 })
                        next.AddRange(diff);
                    else
                        next.Add(piece);
                }
                catch
                {
                    next.Add(piece);
                }
            }
            current = next;
            if (current.Count == 0) break;
        }
        return current;
    }

    private static List<Brep> CollectRoadwayBreps(RhinoDoc doc)
    {
        var list = new List<Brep>();
        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            var layer = doc.Layers[obj.Attributes.LayerIndex];
            if (layer is null) continue;
            if (!layer.FullPath.Equals(RoadSurfaceGenerator.LayerRoadway, StringComparison.OrdinalIgnoreCase))
                continue;

            if (obj.Geometry is Brep brep)
                list.Add(brep);
            else if (obj.Geometry is Extrusion ext)
            {
                var b = ext.ToBrep();
                if (b is not null) list.Add(b);
            }
        }
        return list;
    }

    public static Color ColorForType(string zoneType) => zoneType.ToLowerInvariant() switch
    {
        "residential" => Color.FromArgb(180, 220, 140),
        "commercial" => Color.FromArgb(230, 170, 120),
        "mixed_use" => Color.FromArgb(200, 180, 220),
        "industrial" => Color.FromArgb(170, 170, 180),
        "green" => Color.FromArgb(90, 160, 90),
        "public" => Color.FromArgb(120, 170, 210),
        _ => Color.FromArgb(160, 180, 200),
    };

    private static int EnsureLayer(RhinoDoc doc, string fullPath, Color color)
    {
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer is null || layer.IsDeleted) continue;
            if (layer.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        var parts = fullPath.Split(new[] { "::" }, StringSplitOptions.None);
        var parentIndex = -1;
        var built = "";
        for (var p = 0; p < parts.Length; p++)
        {
            built = p == 0 ? parts[0] : built + "::" + parts[p];
            var found = -1;
            for (var i = 0; i < doc.Layers.Count; i++)
            {
                var layer = doc.Layers[i];
                if (layer is null || layer.IsDeleted) continue;
                if (layer.FullPath.Equals(built, StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }

            if (found >= 0)
            {
                parentIndex = found;
                continue;
            }

            var newLayer = new Layer { Name = parts[p] };
            if (parentIndex >= 0)
                newLayer.ParentLayerId = doc.Layers[parentIndex].Id;
            if (p == parts.Length - 1)
                newLayer.Color = color;
            parentIndex = doc.Layers.Add(newLayer);
        }

        return parentIndex >= 0 ? parentIndex : 0;
    }
}
