using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

public sealed class MassingResult
{
    public Guid SourceZoneId { get; init; }
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

/// <summary>
/// Massing 2.4: solid | perimeter | point | random | row.
/// One Brep per floor; z0 from zone boundary; limits + height/rotation jitter.
/// After volumes: CourtyardGenerator places green spaces per massing_type.
/// </summary>
public sealed partial class MassingGenerator
{
    public const string LayerMassing = "Buildings::Massing";
    public const double FloorHeightM = 3.3;

    public const double DefaultBlockDepthM = 14.0;
    public const double DefaultPadSizeM = 18.0;
    public const double DefaultMinGapM = 8.0;
    public const double DefaultCoverageMax = 0.40;
    public const int DefaultMaxBuildings = 10;
    public const double DefaultMaxBarLengthM = 48.0;
    public const double DefaultHeightJitter = 0.35;
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

        analysis.MetricsById.TryGetValue(zone.RhinoObjectId, out var metrics);
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

        var massingType = NormalizeMassingType(zone.MassingType);
        var seed = StableSeed(zone.RhinoObjectId);
        var rng = new Random(seed);

        var pads = BuildPads(envelope, massingType, zone, analysis, rng);
        if (pads is null || pads.Count == 0)
            return null;

        pads = ClipPadsAwayFromRoads(doc, pads, z0);
        if (pads.Count == 0)
        {
            AddIssue(analysis, zone, MassingIssueType.FootprintRejected, IssueSeverity.Warning,
                "All footprints overlap roads after clip.");
            return null;
        }

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

        var jitter = massingType is "random" or "row" or "perimeter" ? DefaultHeightJitter : 0.0;

        var floorCounts = new int[pads.Count];
        var totalGfa = 0.0;
        for (var i = 0; i < pads.Count; i++)
        {
            var u = jitter > 0 ? (rng.NextDouble() * 2 - 1) * jitter : 0.0;
            var f = (int)Math.Round(baseFloors * (1.0 + u));
            f = Math.Clamp(f, 1, maxFloorsByHeight);
            floorCounts[i] = f;
            totalGfa += pads[i].AreaSqm * f;
        }

        if (targetGfa > 0 && totalGfa > targetGfa * 1.15)
        {
            while (totalGfa > targetGfa * 1.05)
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
            for (var f = 0; f < nFloors; f++)
            {
                var baseZ = z0 + f * floorHeightDoc;
                var slab = ExtrudeFloor(fp, floorHeightDoc, baseZ);
                if (slab is null || !slab.IsValid) continue;

                var attrs = new ObjectAttributes { LayerIndex = layerIndex };
                attrs.SetUserString(MassingCleanup.GeneratedByKey, MassingCleanup.GeneratedByValue);
                attrs.SetUserString(MassingCleanup.SourceZoneKey, zone.RhinoObjectId.ToString());
                attrs.SetUserString(MassingCleanup.FloorsKey, nFloors.ToString(System.Globalization.CultureInfo.InvariantCulture));
                attrs.SetUserString(MassingCleanup.FloorIndexKey, (f + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                attrs.SetUserString(MassingCleanup.MassingTypeKey, massingType);
                attrs.SetUserString(MassingCleanup.FarActualKey,
                    farActual.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
                attrs.SetUserString(MassingCleanup.FarTargetKey,
                    zone.Far.ToString("G", System.Globalization.CultureInfo.InvariantCulture));

                if (doc.Objects.AddBrep(slab, attrs) != Guid.Empty)
                    volumeCount++;
            }
        }

        // Green spaces for this massing type
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
            $"{pads.Count} building(s), floors {floorsMin}–{floorsMax}, slabs {volumeCount}, courtyard {courtyardCount}");

        return new MassingResult
        {
            SourceZoneId = zone.RhinoObjectId,
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

    private List<FootprintPad>? BuildPads(
        Curve envelope, string massingType, ZoneRecord zone, ZoneAnalysis analysis, Random rng)
    {
        return massingType switch
        {
            "perimeter" => BuildPerimeter(envelope, zone, analysis),
            "point" => BuildPointSingle(envelope, zone, analysis),
            "random" => BuildRandom(envelope, zone, analysis, rng),
            "row" => BuildRows(envelope, zone, analysis, rng),
            _ => CurveToPads(new[] { envelope.DuplicateCurve()! }),
        };
    }
}
