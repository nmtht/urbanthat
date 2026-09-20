using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

public sealed class MassingResult
{
    public Guid SourceZoneId { get; init; }
    public Guid MetricsId { get; init; }
    public string MassingType { get; init; } = "solid";
    public int Floors { get; init; }
    public int FloorsMin { get; init; }
    public int FloorsMax { get; init; }
    public double HeightM { get; init; }
    public double FarActual { get; init; }
    public double FarTarget { get; init; }
    public double FootprintAreaSqm { get; init; }
    public double BuiltGfaSqm { get; init; }
    public int VolumeCount { get; init; }
    public int BuildingCount { get; init; }
    public int CourtyardObjectCount { get; init; }
}

public enum MassingIssueType
{
    EnvelopeGenerationFailed,
    FarNotAchievableWithinHeightLimit,
    ZoneTooSmall,
    SkippedGreenOrPublic,
    PerimeterBooleanFailed,
    PointMassingNotFeasible,
    MassingClamped,
    FootprintRejected,
}

public sealed class MassingIssue
{
    public required MassingIssueType Type { get; init; }
    public required IssueSeverity Severity { get; init; }
    public required string Message { get; init; }
    public Guid RelatedZoneId { get; init; }
}

public sealed class MassingBatchResult
{
    public int DeletedCount { get; set; }
    public int CreatedCount { get; set; }
    public int CourtyardCount { get; set; }
    public List<MassingResult> Buildings { get; } = new();
    public List<MassingIssue> Issues { get; } = new();
    public double TotalBuiltFloorAreaSqm { get; set; }
}

public sealed partial class MassingGenerator
{
    public const string LayerMassing = "Buildings::Massing";
    public const double FloorHeightM = 3.3;
    public const double DefaultBlockDepthM = 14.0;
    public const double DefaultPadSizeM = 18.0;
    public const double DefaultMinGapM = 14.0;
    public const double DefaultCoverageMax = 0.40;
    public const int DefaultMaxBuildings = 10;
    public const double DefaultMaxBarLengthM = 48.0;
    public const double DefaultHeightJitter = 0.55;
    public const double DefaultRotationJitterDeg = 25.0;

    public static readonly string[] MassingTypes =
    {
        "solid", "perimeter", "point", "random", "row",
    };

    private readonly double _docTolerance;
    private readonly double _metersToDoc;
    private readonly double _docToMeters;

    private sealed class FootprintPad
    {
        public required Curve Curve;
        public double AreaSqm;
        public string UseTag = "residential";
    }

    public MassingGenerator(RhinoDoc doc)
    {
        _docTolerance = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.001;
        _metersToDoc = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        _docToMeters = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
    }

    public MassingBatchResult Generate(RhinoDoc doc, ZoneAnalysis analysis)
    {
        var batch = new MassingBatchResult();
        batch.DeletedCount = MassingCleanup.DeleteAllGenerated(doc);
        batch.DeletedCount += CourtyardCleanup.DeleteAllGenerated(doc);
        EnsureLayer(doc, LayerMassing, System.Drawing.Color.FromArgb(160, 160, 170));

        var courtyard = new CourtyardGenerator(doc);

        foreach (var zone in analysis.Zones)
        {
            try
            {
                var result = GenerateOne(doc, zone, analysis, courtyard);
                if (result is null) continue;
                batch.Buildings.Add(result);
                batch.CreatedCount += result.VolumeCount;
                batch.CourtyardCount += result.CourtyardObjectCount;
                batch.TotalBuiltFloorAreaSqm += result.BuiltGfaSqm;
            }
            catch (Exception ex)
            {
                batch.Issues.Add(new MassingIssue
                {
                    Type = MassingIssueType.EnvelopeGenerationFailed,
                    Severity = IssueSeverity.Error,
                    Message = $"Massing failed for zone {zone.RhinoObjectId.ToString()[..8]}…: {ex.Message}",
                    RelatedZoneId = zone.RhinoObjectId,
                });
            }
        }

        RhinoApp.WriteLine(
            $"[UrbanBridge] Massing batch: {batch.CreatedCount} slabs, {batch.CourtyardCount} courtyard objects.");
        doc.Views.Redraw();
        return batch;
    }

    private MassingResult? GenerateOne(
        RhinoDoc doc, ZoneRecord zone, ZoneAnalysis analysis, CourtyardGenerator courtyard)
    {
        var ztype = zone.ZoneType?.ToLowerInvariant() ?? "";
        if (ztype is "green" or "public")
        {
            AddIssue(analysis, zone, MassingIssueType.SkippedGreenOrPublic, IssueSeverity.Info,
                $"Massing skipped for {ztype} zone.");
            return null;
        }

        // Metrics keyed by MetricsId (unique per road-split parcel)
        var metricsKey = zone.MetricsId != Guid.Empty ? zone.MetricsId : zone.RhinoObjectId;
        analysis.MetricsById.TryGetValue(metricsKey, out var metrics);
        var zoneAreaSqm = metrics?.AreaSqm ?? 0;
        var targetGfa = metrics?.BuildableAreaSqm ?? (zoneAreaSqm * zone.Far);
        if (zoneAreaSqm <= 1e-6)
        {
            AddIssue(analysis, zone, MassingIssueType.ZoneTooSmall, IssueSeverity.Error, "Zone area is zero.");
            return null;
        }

        var z0 = zone.Boundary.GetBoundingBox(true).Min.Z;
        var setbackDoc = Math.Max(0, zone.SetbackM) * _metersToDoc;
        var envelope = BuildEnvelope(zone.Boundary, setbackDoc);
        if (envelope is null)
        {
            AddIssue(analysis, zone, MassingIssueType.EnvelopeGenerationFailed, IssueSeverity.Error,
                "Setback offset failed or self-intersects.");
            return null;
        }

        AlignCurveToZ(envelope, z0);

        // Safety clip: setback envelope must stay clear of roads
        var parcels = RoadOutlineHelper.SplitCurveByRoads(
            doc, envelope, z0, _docTolerance, RoadOutlineHelper.MassingRoadBufferM);
        if (parcels.Count == 0)
        {
            AddIssue(analysis, zone, MassingIssueType.FootprintRejected, IssueSeverity.Warning,
                "Envelope fully covered by roads after buffer clip.");
            return null;
        }
        envelope = parcels.OrderByDescending(p =>
        {
            var a = AreaMassProperties.Compute(p);
            return a?.Area ?? 0;
        }).First();
        AlignCurveToZ(envelope, z0);

        var massingType = NormalizeMassingType(zone.MassingType);
        var seed = StableSeed(metricsKey);
        var rng = new Random(seed);
        var isMixed = ztype == "mixed_use";

        if (massingType == "perimeter")
            return GeneratePerimeterHollow(doc, zone, analysis, courtyard, envelope, z0, zoneAreaSqm, targetGfa, rng, isMixed);

        var pads = BuildPads(envelope, massingType, zone, analysis, rng);
        if (pads is null || pads.Count == 0)
            return null;

        if (isMixed)
        {
            for (var i = 0; i < pads.Count; i++)
                pads[i].UseTag = rng.NextDouble() < 0.45 ? "commercial" : "residential";
        }

        pads = ClipPadsAwayFromRoads(doc, pads, z0);
        if (pads.Count == 0)
        {
            AddIssue(analysis, zone, MassingIssueType.FootprintRejected, IssueSeverity.Warning,
                "All footprints overlap roads after clip.");
            return null;
        }

        if (massingType is "random" or "row")
            pads = EnforceMinGap(pads, zone.GreenRatio);

        var footprintAreaSqm = pads.Sum(p => p.AreaSqm);
        if (footprintAreaSqm <= 1e-6)
        {
            AddIssue(analysis, zone, MassingIssueType.ZoneTooSmall, IssueSeverity.Error, "Footprint area too small.");
            return null;
        }

        var maxFloorsByHeight = (int)Math.Floor(zone.HeightMax / FloorHeightM);
        if (maxFloorsByHeight < 1)
        {
            AddIssue(analysis, zone, MassingIssueType.ZoneTooSmall, IssueSeverity.Error,
                $"height_max={zone.HeightMax:F1} m < one floor ({FloorHeightM} m).");
            return null;
        }

        var baseFloors = (int)Math.Ceiling(targetGfa / footprintAreaSqm);
        if (baseFloors < 1) baseFloors = 1;
        if (baseFloors > maxFloorsByHeight)
        {
            baseFloors = maxFloorsByHeight;
            AddIssue(analysis, zone, MassingIssueType.FarNotAchievableWithinHeightLimit, IssueSeverity.Warning,
                $"FAR {zone.Far:F2} not achievable within height_max; base floors={baseFloors}.");
        }

        var jitter = massingType == "solid" ? 0.2 : DefaultHeightJitter;
        var floorCounts = new int[pads.Count];
        var totalGfa = 0.0;
        for (var i = 0; i < pads.Count; i++)
        {
            var bias = pads[i].UseTag == "commercial" ? 1.25 : 0.9;
            if (!isMixed) bias = 1.0;
            var u = (rng.NextDouble() * 2 - 1) * jitter;
            var f = (int)Math.Round(baseFloors * bias * (1.0 + u));
            f = Math.Clamp(f, 1, maxFloorsByHeight);
            if (pads.Count > 1 && i == 0)
                f = Math.Max(1, Math.Min(maxFloorsByHeight, baseFloors - 1));
            if (pads.Count > 1 && i == 1)
                f = Math.Min(maxFloorsByHeight, baseFloors + 1 + rng.Next(0, 2));
            floorCounts[i] = f;
            totalGfa += pads[i].AreaSqm * f;
        }

        if (targetGfa > 0 && totalGfa > targetGfa * 1.2)
        {
            while (totalGfa > targetGfa * 1.08)
            {
                var hi = 0;
                for (var i = 1; i < floorCounts.Length; i++)
                    if (floorCounts[i] > floorCounts[hi]) hi = i;
                if (floorCounts[hi] <= 1) break;
                floorCounts[hi]--;
                totalGfa -= pads[hi].AreaSqm;
            }
        }

        var floorHeightDoc = FloorHeightM * _metersToDoc;
        var farActual = totalGfa / zoneAreaSqm;
        var layerIndex = EnsureLayer(doc, LayerMassing, System.Drawing.Color.FromArgb(160, 160, 170));
        var volumeCount = 0;
        var floorsMin = floorCounts.Min();
        var floorsMax = floorCounts.Max();
        var avgFloors = floorCounts.Average();

        for (var i = 0; i < pads.Count; i++)
        {
            var fp = pads[i].Curve;
            AlignCurveToZ(fp, z0);
            var nFloors = floorCounts[i];
            var color = pads[i].UseTag == "commercial"
                ? System.Drawing.Color.FromArgb(170, 155, 140)
                : System.Drawing.Color.FromArgb(155, 160, 170);

            for (var f = 0; f < nFloors; f++)
            {
                var baseZ = z0 + f * floorHeightDoc;
                var slab = ExtrudeFloor(fp, floorHeightDoc, baseZ);
                if (slab is null || !slab.IsValid) continue;

                var attrs = new ObjectAttributes
                {
                    LayerIndex = layerIndex,
                    ColorSource = ObjectColorSource.ColorFromObject,
                    ObjectColor = color,
                };
                attrs.SetUserString(MassingCleanup.GeneratedByKey, MassingCleanup.GeneratedByValue);
                attrs.SetUserString(MassingCleanup.SourceZoneKey, zone.RhinoObjectId.ToString());
                attrs.SetUserString(MassingCleanup.MetricsIdKey, metricsKey.ToString());
                attrs.SetUserString(MassingCleanup.FloorsKey, nFloors.ToString(System.Globalization.CultureInfo.InvariantCulture));
                attrs.SetUserString(MassingCleanup.FloorIndexKey, (f + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                attrs.SetUserString(MassingCleanup.MassingTypeKey, massingType);
                attrs.SetUserString("use_tag", pads[i].UseTag);
                attrs.SetUserString(MassingCleanup.FarActualKey,
                    farActual.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
                attrs.SetUserString(MassingCleanup.FarTargetKey,
                    zone.Far.ToString("G", System.Globalization.CultureInfo.InvariantCulture));

                if (doc.Objects.AddBrep(slab, attrs) != Guid.Empty)
                    volumeCount++;
            }
        }

        var padCurves = pads.Select(p => p.Curve).ToList();
        var courtyardCount = 0;
        try
        {
            courtyardCount = courtyard.GenerateForZone(doc, zone, massingType, envelope, padCurves, z0);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Courtyard for zone failed: {ex.Message}");
        }

        RhinoApp.WriteLine(
            $"[UrbanBridge] Massing {massingType}: zone {zone.RhinoObjectId.ToString()[..8]}… " +
            $"parcel {zone.ParcelIndex}, {pads.Count} building(s), floors {floorsMin}–{floorsMax}, " +
            $"FAR {farActual:F2}/{zone.Far:F2}, slabs {volumeCount}, courtyard {courtyardCount}");

        return new MassingResult
        {
            SourceZoneId = zone.RhinoObjectId,
            MetricsId = metricsKey,
            MassingType = massingType,
            Floors = (int)Math.Round(avgFloors),
            FloorsMin = floorsMin,
            FloorsMax = floorsMax,
            HeightM = floorsMax * FloorHeightM,
            FarActual = farActual,
            FarTarget = zone.Far,
            FootprintAreaSqm = footprintAreaSqm,
            BuiltGfaSqm = totalGfa,
            VolumeCount = volumeCount,
            BuildingCount = pads.Count,
            CourtyardObjectCount = courtyardCount,
        };
    }

    private MassingResult? GeneratePerimeterHollow(
        RhinoDoc doc, ZoneRecord zone, ZoneAnalysis analysis, CourtyardGenerator courtyard,
        Curve envelope, double z0, double zoneAreaSqm, double targetGfa, Random rng, bool isMixed)
    {
        var metricsKey = zone.MetricsId != Guid.Empty ? zone.MetricsId : zone.RhinoObjectId;
        var green = Math.Clamp(zone.GreenRatio, 0, 0.85);
        var depthM = DefaultBlockDepthM * (0.5 + 0.5 * (1.0 - green));
        depthM = Math.Clamp(depthM, 8.0, DefaultBlockDepthM);
        var depthDoc = depthM * _metersToDoc;

        var inner = BuildEnvelope(envelope, depthDoc);
        if (inner is null)
        {
            AddIssue(analysis, zone, MassingIssueType.PerimeterBooleanFailed, IssueSeverity.Warning,
                "Perimeter inner offset failed.");
            return null;
        }
        AlignCurveToZ(inner, z0);

        var outerAmp = AreaMassProperties.Compute(envelope);
        var innerAmp = AreaMassProperties.Compute(inner);
        var outerSqm = outerAmp is null ? 0 : outerAmp.Area * _docToMeters * _docToMeters;
        var innerSqm = innerAmp is null ? 0 : innerAmp.Area * _docToMeters * _docToMeters;
        var footprintAreaSqm = Math.Max(0, outerSqm - innerSqm);
        if (footprintAreaSqm < 5)
        {
            AddIssue(analysis, zone, MassingIssueType.PerimeterBooleanFailed, IssueSeverity.Warning,
                "Perimeter ring area too small.");
            return null;
        }

        var maxFloorsByHeight = (int)Math.Floor(zone.HeightMax / FloorHeightM);
        if (maxFloorsByHeight < 1) maxFloorsByHeight = 1;

        var baseFloors = (int)Math.Ceiling(targetGfa / footprintAreaSqm);
        if (baseFloors < 1) baseFloors = 1;
        if (baseFloors > maxFloorsByHeight)
        {
            baseFloors = maxFloorsByHeight;
            AddIssue(analysis, zone, MassingIssueType.FarNotAchievableWithinHeightLimit, IssueSeverity.Warning,
                $"FAR {zone.Far:F2} not achievable; base floors={baseFloors}.");
        }

        var floors = Math.Clamp((int)Math.Round(baseFloors * (1 + (rng.NextDouble() * 2 - 1) * 0.25)), 1, maxFloorsByHeight);

        var floorHeightDoc = FloorHeightM * _metersToDoc;
        var totalGfa = footprintAreaSqm * floors;
        var farActual = totalGfa / zoneAreaSqm;
        var layerIndex = EnsureLayer(doc, LayerMassing, System.Drawing.Color.FromArgb(150, 155, 165));
        var volumeCount = 0;
        var booleanFailures = 0;

        for (var f = 0; f < floors; f++)
        {
            var baseZ = z0 + f * floorHeightDoc;
            var hollow = ExtrudeHollowFloor(envelope, inner, floorHeightDoc, baseZ);
            if (hollow is null)
            {
                booleanFailures++;
                continue;
            }
            volumeCount += AddMassingBrep(doc, hollow, layerIndex, zone, metricsKey, "perimeter", floors, f + 1, farActual, "residential");
        }

        if (volumeCount == 0)
        {
            AddIssue(analysis, zone, MassingIssueType.PerimeterBooleanFailed, IssueSeverity.Warning,
                "Perimeter boolean difference failed for all floors — no solid box fallback.");
            return null;
        }

        if (booleanFailures > 0)
        {
            RhinoApp.WriteLine(
                $"[UrbanBridge] Perimeter: {booleanFailures}/{floors} floors skipped (boolean failed).");
        }

        var courtyardCount = courtyard.GenerateForZone(
            doc, zone, "perimeter", envelope, new List<Curve> { inner }, z0);

        RhinoApp.WriteLine(
            $"[UrbanBridge] Massing perimeter: floors {floors}, FAR {farActual:F2}/{zone.Far:F2}, " +
            $"ring {footprintAreaSqm:F0} m², courtyard {courtyardCount}, green_ratio={green:F2}");

        return new MassingResult
        {
            SourceZoneId = zone.RhinoObjectId,
            MetricsId = metricsKey,
            MassingType = "perimeter",
            Floors = floors,
            FloorsMin = floors,
            FloorsMax = floors,
            HeightM = floors * FloorHeightM,
            FarActual = farActual,
            FarTarget = zone.Far,
            FootprintAreaSqm = footprintAreaSqm,
            BuiltGfaSqm = totalGfa,
            VolumeCount = volumeCount,
            BuildingCount = 1,
            CourtyardObjectCount = courtyardCount,
        };
    }

    private Brep? ExtrudeHollowFloor(Curve outer, Curve inner, double heightDoc, double baseZ)
    {
        var outerSolid = ExtrudeFloor(outer, heightDoc, baseZ);
        var innerSolid = ExtrudeFloor(inner, heightDoc, baseZ);
        if (outerSolid is null || innerSolid is null) return null;

        try
        {
            var diff = Brep.CreateBooleanDifference(outerSolid, innerSolid, _docTolerance);
            if (diff is { Length: > 0 })
            {
                if (diff.Length == 1) return diff[0];
                var joined = Brep.JoinBreps(diff, _docTolerance);
                if (joined is { Length: > 0 }) return joined[0];
                return diff[0];
            }
        }
        catch { }

        return null;
    }

    private int AddMassingBrep(
        RhinoDoc doc, Brep brep, int layerIndex, ZoneRecord zone, Guid metricsId,
        string massingType, int nFloors, int floorIndex, double farActual, string useTag,
        System.Drawing.Color? color = null)
    {
        var attrs = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = color ?? System.Drawing.Color.FromArgb(155, 160, 170),
        };
        attrs.SetUserString(MassingCleanup.GeneratedByKey, MassingCleanup.GeneratedByValue);
        attrs.SetUserString(MassingCleanup.SourceZoneKey, zone.RhinoObjectId.ToString());
        attrs.SetUserString(MassingCleanup.MetricsIdKey, metricsId.ToString());
        attrs.SetUserString(MassingCleanup.FloorsKey, nFloors.ToString(System.Globalization.CultureInfo.InvariantCulture));
        attrs.SetUserString(MassingCleanup.FloorIndexKey, floorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        attrs.SetUserString(MassingCleanup.MassingTypeKey, massingType);
        attrs.SetUserString("use_tag", useTag);
        if (farActual > 0)
        {
            attrs.SetUserString(MassingCleanup.FarActualKey,
                farActual.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            attrs.SetUserString(MassingCleanup.FarTargetKey,
                zone.Far.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
        }
        return doc.Objects.AddBrep(brep, attrs) != Guid.Empty ? 1 : 0;
    }

    private List<FootprintPad>? BuildPads(
        Curve envelope, string massingType, ZoneRecord zone, ZoneAnalysis analysis, Random rng)
    {
        return massingType switch
        {
            "perimeter" => null,
            "point" => BuildPointSingle(envelope, zone, analysis),
            "random" => BuildRandom(envelope, zone, analysis, rng),
            "row" => BuildRows(envelope, zone, analysis, rng),
            _ => CurveToPads(new[] { envelope.DuplicateCurve()! }),
        };
    }
}
