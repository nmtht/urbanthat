using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Collects zones from the document, splits parcels cut by roads,
/// computes metrics, validates, checks road access.
/// </summary>
public sealed class ZoneAnalysisService
{
    public ZoneAnalysis Analyze(
        RhinoDoc doc,
        DateTime? lastRoadGraphChangeUtc,
        DateTime? lastRoadSurfaceGenUtc)
    {
        var analysis = new ZoneAnalysis
        {
            LastRoadGraphChangeUtc = lastRoadGraphChangeUtc,
            LastRoadSurfaceGenUtc = lastRoadSurfaceGenUtc,
            RoadSurfacesStale = IsStale(lastRoadGraphChangeUtc, lastRoadSurfaceGenUtc),
        };

        var lengthScaleDocToM = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
        var tol = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
        var boundary = ResolveProjectBoundary(doc);

        foreach (var obj in doc.Objects)
        {
            if (obj is null || obj.IsDeleted) continue;
            if (obj.Geometry is not Curve curve) continue;
            if (!IsZonesLayer(doc, obj)) continue;
            if (!curve.IsClosed) continue;

            if (boundary is not null)
            {
                var center = curve.GetBoundingBox(true).Center;
                if (boundary.Contains(center, Plane.WorldXY, tol) != PointContainment.Inside)
                    continue;
            }

            var baseRecord = ReadZone(obj, curve.DuplicateCurve()!);
            var z0 = curve.GetBoundingBox(true).Min.Z;

            // Split zone by road surfaces → one ZoneRecord per parcel
            List<Curve> parcels;
            try
            {
                parcels = RoadOutlineHelper.SplitCurveByRoads(
                    doc, baseRecord.Boundary, z0, tol, RoadOutlineHelper.MassingRoadBufferM);
            }
            catch
            {
                parcels = new List<Curve> { baseRecord.Boundary };
            }

            if (parcels.Count == 0)
                parcels.Add(baseRecord.Boundary);

            if (parcels.Count > 1)
            {
                RhinoApp.WriteLine(
                    $"[UrbanBridge] Zone {obj.Id.ToString()[..8]}… split into {parcels.Count} parcels by roads.");
            }

            for (var pi = 0; pi < parcels.Count; pi++)
            {
                var parcel = parcels[pi];
                if (parcel is null || !parcel.IsValid) continue;
                if (!parcel.IsClosed) parcel.MakeClosed(tol * 10);
                if (!parcel.IsClosed) continue;

                var record = new ZoneRecord
                {
                    RhinoObjectId = baseRecord.RhinoObjectId,
                    Boundary = parcel,
                    ZoneType = baseRecord.ZoneType,
                    Far = baseRecord.Far,
                    HeightMax = baseRecord.HeightMax,
                    SetbackM = baseRecord.SetbackM,
                    GreenRatio = baseRecord.GreenRatio,
                    MassingType = baseRecord.MassingType,
                    ZoneTypeWasMissing = baseRecord.ZoneTypeWasMissing,
                };

                // Unique key for multi-parcel: Guid composite via Metrics key
                // Metrics dictionary needs unique key — use piece index hash
                var metricsKey = parcels.Count == 1
                    ? record.RhinoObjectId
                    : DeterministicPieceId(record.RhinoObjectId, pi);

                analysis.Zones.Add(record);

                var metrics = ZoneMetricsCalculator.Compute(record, lengthScaleDocToM);
                analysis.MetricsById[metricsKey] = metrics;
                // Also store under original id for first piece so single-zone lookups still work
                if (pi == 0)
                    analysis.MetricsById[record.RhinoObjectId] = metrics;

                analysis.TotalAreaSqm += metrics.AreaSqm;
                analysis.TotalPopulation += metrics.EstimatedPopulation;
                analysis.TotalJobs += metrics.EstimatedJobs;
                analysis.TotalGreenAreaSqm += metrics.GreenAreaSqm;

                if (!analysis.AreaByType.ContainsKey(record.ZoneType))
                    analysis.AreaByType[record.ZoneType] = 0;
                analysis.AreaByType[record.ZoneType] += metrics.AreaSqm;
            }
        }

        new ZoneValidator(doc.ModelAbsoluteTolerance).Validate(analysis);
        new ZoneRoadAccessChecker(doc).Check(doc, analysis);

        return analysis;
    }

    private static Guid DeterministicPieceId(Guid parent, int index)
    {
        var bytes = parent.ToByteArray();
        bytes[0] ^= (byte)(index + 1);
        bytes[1] ^= (byte)((index + 1) * 17);
        return new Guid(bytes);
    }

    private static Curve? ResolveProjectBoundary(RhinoDoc doc)
    {
        if (PluginSettings.ProjectBoundaryId is not { } id) return null;
        var obj = doc.Objects.FindId(id);
        if (obj?.Geometry is Curve c && c.IsClosed)
            return c;
        return null;
    }

    private static bool IsStale(DateTime? graphChange, DateTime? surfaceGen)
    {
        if (graphChange is null) return false;
        if (surfaceGen is null) return true;
        return graphChange > surfaceGen;
    }

    private static bool IsZonesLayer(RhinoDoc doc, RhinoObject obj)
    {
        var layer = doc.Layers[obj.Attributes.LayerIndex];
        if (layer is null) return false;
        var path = layer.FullPath;
        if (path.StartsWith("Zones::Proxy", StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Equals("Zones", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("Zones::", StringComparison.OrdinalIgnoreCase);
    }

    private static ZoneRecord ReadZone(RhinoObject obj, Curve boundary)
    {
        var strings = obj.Attributes.GetUserStrings();
        var typeRaw = strings.Get("zone_type");
        var missing = string.IsNullOrWhiteSpace(typeRaw);
        var zoneType = missing || !ZoneTypeDefaults.IsKnown(typeRaw!) ? "residential" : typeRaw!;
        var defaults = ZoneTypeDefaults.Get(zoneType);

        double Parse(string key, double fallback)
        {
            var s = strings.Get(key);
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        var massingRaw = strings.Get(ZoneAttributeHelper.KeyMassingType);
        var massingType = "solid";
        if (!string.IsNullOrWhiteSpace(massingRaw))
        {
            var m = massingRaw!.Trim().ToLowerInvariant();
            if (MassingGenerator.MassingTypes.Any(t => t == m))
                massingType = m;
        }

        return new ZoneRecord
        {
            RhinoObjectId = obj.Id,
            Boundary = boundary,
            ZoneType = zoneType,
            Far = Parse("far", defaults.Far),
            HeightMax = Parse("height_max", defaults.HeightMaxM),
            SetbackM = Parse("setback_m", ZoneTypeDefaults.DefaultSetbackM),
            GreenRatio = Math.Clamp(Parse("green_ratio", defaults.GreenRatio), 0, 1),
            MassingType = massingType,
            ZoneTypeWasMissing = missing,
        };
    }
}
