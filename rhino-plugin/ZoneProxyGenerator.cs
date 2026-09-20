using System.Drawing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Planar proxy fills for zones (color by zone_type), clipped by planar roadway outlines.
/// If road-clip removes everything or fails, keeps the uncut proxy so generation never yields zero silently.
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

        if (analysis.Zones.Count == 0)
        {
            RhinoApp.WriteLine("[UrbanBridge] ZoneProxy: no zones in analysis.");
            return 0;
        }

        var created = 0;
        foreach (var zone in analysis.Zones)
            created += CreateProxy(doc, zone);

        RhinoApp.WriteLine($"[UrbanBridge] ZoneProxy: {created} piece(s) from {analysis.Zones.Count} zone(s).");
        doc.Views.Redraw();
        return created;
    }

    public int CreateProxy(RhinoDoc doc, ZoneRecord zone)
    {
        // RegenerateAll already wiped all proxies — do not DeleteForZone here:
        // multi-parcel zones share RhinoObjectId and would erase sibling parcels.

        var boundary = zone.Boundary?.DuplicateCurve();
        if (boundary is null || !boundary.IsValid)
        {
            RhinoApp.WriteLine($"[UrbanBridge] ZoneProxy: invalid boundary for {zone.RhinoObjectId.ToString()[..8]}…");
            return 0;
        }

        if (!boundary.IsClosed)
            boundary.MakeClosed(_tolerance * 10);
        if (!boundary.IsClosed)
        {
            RhinoApp.WriteLine($"[UrbanBridge] ZoneProxy: could not close boundary for {zone.RhinoObjectId.ToString()[..8]}…");
            return 0;
        }

        var z = boundary.GetBoundingBox(true).Min.Z;
        // Flatten boundary to z for stable planar brep
        boundary.Transform(Transform.PlanarProjection(new Plane(new Point3d(0, 0, z), Vector3d.ZAxis)));

        var pieces = Brep.CreatePlanarBreps(boundary, _tolerance);
        if (pieces is null || pieces.Length == 0)
        {
            RhinoApp.WriteLine($"[UrbanBridge] ZoneProxy: CreatePlanarBreps failed for {zone.RhinoObjectId.ToString()[..8]}…");
            return 0;
        }

        // Roadway only for clip — sidewalk/parking leave a visual margin under the zone color
        var roads = RoadOutlineHelper.CollectPlanarRoadBreps(doc, z, _tolerance, includeSidewalkAndParking: false);
        var color = ColorForType(zone.ZoneType);
        var layerIndex = EnsureLayer(doc, LayerProxy, color);
        var created = 0;

        foreach (var piece in pieces)
        {
            if (piece is null || !piece.IsValid) continue;

            List<Brep> remaining;
            if (roads.Count > 0)
            {
                remaining = RoadOutlineHelper.Subtract(piece, roads, _tolerance);
                // If clip wiped everything, keep uncut so user still sees the zone plane
                if (remaining.Count == 0)
                {
                    RhinoApp.WriteLine(
                        $"[UrbanBridge] ZoneProxy: road clip emptied zone {zone.RhinoObjectId.ToString()[..8]}… — keeping uncut.");
                    remaining = new List<Brep> { piece };
                }
            }
            else
            {
                remaining = new List<Brep> { piece };
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
