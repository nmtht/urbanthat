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
    public int VolumeCount { get; init; }
    public int BuildingCount { get; init; }
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
    public List<MassingResult> Buildings { get; } = new();
    public List<MassingIssue> Issues { get; } = new();
    public double TotalBuiltFloorAreaSqm { get; set; }
}

/// <summary>
/// Massing 2.4: solid | perimeter | point | random | row.
/// One Brep per floor; z0 from zone boundary; limits + height/rotation jitter.
/// </summary>
public sealed class MassingGenerator
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
        EnsureLayer(doc, LayerMassing, System.Drawing.Color.FromArgb(160, 160, 170));

        foreach (var zone in analysis.Zones)
        {
            try
            {
                var result = GenerateOne(doc, zone, analysis);
                if (result is null) continue;
                batch.Buildings.Add(result);
                batch.CreatedCount += result.VolumeCount;
                batch.TotalBuiltFloorAreaSqm += result.FarActual * (analysis.MetricsById.TryGetValue(zone.RhinoObjectId, out var m) ? m.AreaSqm : result.FootprintAreaSqm);
                // GFA = floors-weighted footprint sum already in FarActual * zone area; prefer explicit:
                batch.TotalBuiltFloorAreaSqm = batch.Buildings.Sum(b => b.FootprintAreaSqm * ((b.FloorsMin + b.FloorsMax) / 2.0)); // refined below
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

        // Recompute GFA from actual results stored during generate
        batch.TotalBuiltFloorAreaSqm = 0;
        foreach (var b in batch.Buildings)
            batch.TotalBuiltFloorAreaSqm += b.FootprintAreaSqm * b.Floors; // Floors = average-ish; see GenerateOne

        doc.Views.Redraw();
        return batch;
    }

    private MassingResult? GenerateOne(RhinoDoc doc, ZoneRecord zone, ZoneAnalysis analysis)
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

        // Lift envelope to zone plane
        AlignCurveToZ(envelope, z0);

        var massingType = NormalizeMassingType(zone.MassingType);
        var seed = StableSeed(zone.RhinoObjectId);
        var rng = new Random(seed);

        var pads = BuildPads(envelope, massingType, zone, analysis, rng);
        if (pads is null || pads.Count == 0)
            return null;

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

        // Per-pad floor counts with height jitter (point/solid: no jitter)
        var jitter = massingType is "random" or "row" or "perimeter" ? DefaultHeightJitter : 0.0;
        if (massingType == "point") jitter = 0.0;

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

        // Trim GFA if overshoot target significantly (>15%)
        if (targetGfa > 0 && totalGfa > targetGfa * 1.15)
        {
            // Reduce tallest pads first
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

        RhinoApp.WriteLine(
            $"[UrbanBridge] Massing {massingType}: zone {zone.RhinoObjectId.ToString()[..8]}… " +
            $"{pads.Count} building(s), floors {floorsMin}–{floorsMax}, slabs {volumeCount}");

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
            VolumeCount = volumeCount,
            BuildingCount = pads.Count,
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

    private List<FootprintPad>? BuildPerimeter(Curve envelope, ZoneRecord zone, ZoneAnalysis analysis)
    {
        var depthDoc = DefaultBlockDepthM * _metersToDoc;
        var inner = BuildEnvelope(envelope, depthDoc);
        if (inner is null)
        {
            AddIssue(analysis, zone, MassingIssueType.PerimeterBooleanFailed, IssueSeverity.Warning,
                "Perimeter inner offset failed — no massing for this zone.");
            return null;
        }

        try
        {
            var diff = Curve.CreateBooleanDifference(envelope, inner, _docTolerance);
            if (diff is { Length: > 0 })
            {
                var pads = CurveToPads(diff);
                if (pads.Count > 0) return pads;
            }
        }
        catch { }

        AddIssue(analysis, zone, MassingIssueType.PerimeterBooleanFailed, IssueSeverity.Warning,
            "Perimeter boolean difference failed — no silent solid fallback.");
        return null;
    }

    private List<FootprintPad>? BuildPointSingle(Curve envelope, ZoneRecord zone, ZoneAnalysis analysis)
    {
        var bbox = envelope.GetBoundingBox(true);
        var pad = DefaultPadSizeM * _metersToDoc;
        // Fit pad inside envelope: clamp to 40% of shorter side
        var shortSide = Math.Min(bbox.Max.X - bbox.Min.X, bbox.Max.Y - bbox.Min.Y);
        if (pad > shortSide * 0.5)
            pad = shortSide * 0.4;

        if (pad < _docTolerance * 50)
        {
            AddIssue(analysis, zone, MassingIssueType.PointMassingNotFeasible, IssueSeverity.Warning,
                "Point massing: envelope too small for a tower pad.");
            return null;
        }

        var center = bbox.Center;
        center.Z = bbox.Min.Z;
        if (envelope.Contains(center, Plane.WorldXY, _docTolerance) != PointContainment.Inside)
        {
            // fallback: try bbox center projections
            AddIssue(analysis, zone, MassingIssueType.PointMassingNotFeasible, IssueSeverity.Warning,
                "Point massing: centroid not inside setback envelope.");
            return null;
        }

        var half = pad * 0.5;
        var sq = MakeRect(center.X, center.Y, center.Z, half, half, 0);
        var pads = CurveToPads(new[] { sq });
        if (pads.Count == 0)
        {
            AddIssue(analysis, zone, MassingIssueType.PointMassingNotFeasible, IssueSeverity.Warning,
                "Point massing: footprint area is zero.");
            return null;
        }
        return pads;
    }

    private List<FootprintPad>? BuildRandom(
        Curve envelope, ZoneRecord zone, ZoneAnalysis analysis, Random rng)
    {
        var bbox = envelope.GetBoundingBox(true);
        var gap = DefaultMinGapM * _metersToDoc;
        var amp = AreaMassProperties.Compute(envelope);
        var envArea = amp?.Area ?? 0;
        var envAreaSqm = envArea * _docToMeters * _docToMeters;

        var maxByCoverage = envAreaSqm > 0
            ? Math.Max(1, (int)(envAreaSqm * DefaultCoverageMax / (DefaultPadSizeM * DefaultPadSizeM)))
            : DefaultMaxBuildings;
        var maxPads = Math.Min(DefaultMaxBuildings, maxByCoverage);

        var candidates = new List<Curve>();
        var attempts = maxPads * 20;
        for (var a = 0; a < attempts && candidates.Count < maxPads; a++)
        {
            var sizeScale = 0.7 + rng.NextDouble() * 0.5; // 0.7..1.2
            var pad = DefaultPadSizeM * _metersToDoc * sizeScale;
            var half = pad * 0.5;
            var cx = bbox.Min.X + half + rng.NextDouble() * Math.Max(_docTolerance, bbox.Max.X - bbox.Min.X - pad);
            var cy = bbox.Min.Y + half + rng.NextDouble() * Math.Max(_docTolerance, bbox.Max.Y - bbox.Min.Y - pad);
            var center = new Point3d(cx, cy, bbox.Min.Z);
            if (envelope.Contains(center, Plane.WorldXY, _docTolerance) != PointContainment.Inside)
                continue;

            var rot = (rng.NextDouble() * 2 - 1) * DefaultRotationJitterDeg * (Math.PI / 180.0);
            var sq = MakeRect(cx, cy, bbox.Min.Z, half, half, rot);

            // min gap vs existing
            var ok = true;
            foreach (var existing in candidates)
            {
                var ec = existing.GetBoundingBox(true).Center;
                if (center.DistanceTo(ec) < gap + half)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;

            candidates.Add(sq);
        }

        if (candidates.Count == 0)
        {
            AddIssue(analysis, zone, MassingIssueType.FootprintRejected, IssueSeverity.Warning,
                "Random massing: no valid pads placed.");
            return null;
        }

        if (candidates.Count >= maxPads)
        {
            AddIssue(analysis, zone, MassingIssueType.MassingClamped, IssueSeverity.Info,
                $"Random massing clamped to {maxPads} buildings (max_buildings/coverage).");
        }

        return CurveToPads(candidates);
    }

    private List<FootprintPad>? BuildRows(
        Curve envelope, ZoneRecord zone, ZoneAnalysis analysis, Random rng)
    {
        var result = new List<Curve>();
        var bbox = envelope.GetBoundingBox(true);
        var depth = DefaultBlockDepthM * _metersToDoc;
        var gap = DefaultMinGapM * _metersToDoc;
        var maxLen = DefaultMaxBarLengthM * _metersToDoc;
        var sizeX = bbox.Max.X - bbox.Min.X;
        var sizeY = bbox.Max.Y - bbox.Min.Y;
        var alongX = sizeX >= sizeY;

        if (alongX)
        {
            for (var y = bbox.Min.Y + depth * 0.5; y <= bbox.Max.Y - depth * 0.5 && result.Count < DefaultMaxBuildings; y += depth + gap)
            {
                var rowStart = bbox.Min.X + gap * 0.25;
                var rowEnd = bbox.Max.X - gap * 0.25;
                for (var x0 = rowStart; x0 < rowEnd - depth * 0.5 && result.Count < DefaultMaxBuildings; x0 += maxLen + gap)
                {
                    var x1 = Math.Min(x0 + maxLen, rowEnd);
                    if (x1 - x0 < depth) break;
                    var cx = (x0 + x1) * 0.5;
                    var c = new Point3d(cx, y, bbox.Min.Z);
                    if (envelope.Contains(c, Plane.WorldXY, _docTolerance) != PointContainment.Inside)
                        continue;
                    var halfW = (x1 - x0) * 0.5;
                    var halfD = depth * 0.5;
                    var rot = (rng.NextDouble() * 2 - 1) * (DefaultRotationJitterDeg * 0.25) * (Math.PI / 180.0);
                    result.Add(MakeRect(cx, y, bbox.Min.Z, halfW, halfD, rot));
                }
            }
        }
        else
        {
            for (var x = bbox.Min.X + depth * 0.5; x <= bbox.Max.X - depth * 0.5 && result.Count < DefaultMaxBuildings; x += depth + gap)
            {
                var rowStart = bbox.Min.Y + gap * 0.25;
                var rowEnd = bbox.Max.Y - gap * 0.25;
                for (var y0 = rowStart; y0 < rowEnd - depth * 0.5 && result.Count < DefaultMaxBuildings; y0 += maxLen + gap)
                {
                    var y1 = Math.Min(y0 + maxLen, rowEnd);
                    if (y1 - y0 < depth) break;
                    var cy = (y0 + y1) * 0.5;
                    var c = new Point3d(x, cy, bbox.Min.Z);
                    if (envelope.Contains(c, Plane.WorldXY, _docTolerance) != PointContainment.Inside)
                        continue;
                    var halfD = depth * 0.5;
                    var halfW = (y1 - y0) * 0.5;
                    var rot = (rng.NextDouble() * 2 - 1) * (DefaultRotationJitterDeg * 0.25) * (Math.PI / 180.0);
                    result.Add(MakeRect(x, cy, bbox.Min.Z, halfD, halfW, rot));
                }
            }
        }

        if (result.Count == 0)
        {
            AddIssue(analysis, zone, MassingIssueType.FootprintRejected, IssueSeverity.Warning,
                "Row massing: no valid segments.");
            return null;
        }

        if (result.Count >= DefaultMaxBuildings)
        {
            AddIssue(analysis, zone, MassingIssueType.MassingClamped, IssueSeverity.Info,
                $"Row massing clamped to {DefaultMaxBuildings} segments.");
        }

        return CurveToPads(result);
    }

    private static Curve MakeRect(double cx, double cy, double z, double halfX, double halfY, double rotRad)
    {
        var corners = new[]
        {
            new Point3d(-halfX, -halfY, 0),
            new Point3d(halfX, -halfY, 0),
            new Point3d(halfX, halfY, 0),
            new Point3d(-halfX, halfY, 0),
        };
        var cos = Math.Cos(rotRad);
        var sin = Math.Sin(rotRad);
        var pts = new Point3d[5];
        for (var i = 0; i < 4; i++)
        {
            var x = corners[i].X * cos - corners[i].Y * sin;
            var y = corners[i].X * sin + corners[i].Y * cos;
            pts[i] = new Point3d(cx + x, cy + y, z);
        }
        pts[4] = pts[0];
        return new PolylineCurve(pts);
    }

    private List<FootprintPad> CurveToPads(IEnumerable<Curve?> curves)
    {
        var list = new List<FootprintPad>();
        foreach (var c in curves)
        {
            if (c is null || !c.IsValid) continue;
            var amp = AreaMassProperties.Compute(c);
            var areaSqm = amp is null ? 0 : amp.Area * _docToMeters * _docToMeters;
            if (areaSqm <= 1e-6) continue;
            list.Add(new FootprintPad { Curve = c, AreaSqm = areaSqm });
        }
        return list;
    }

    private static void AlignCurveToZ(Curve c, double z0)
    {
        var bb = c.GetBoundingBox(true);
        var dz = z0 - bb.Min.Z;
        if (Math.Abs(dz) > 1e-9)
            c.Translate(0, 0, dz);
    }

    private static int StableSeed(Guid id)
    {
        var bytes = id.ToByteArray();
        var h = 17;
        foreach (var b in bytes)
            h = h * 31 + b;
        return h;
    }

    private static string NormalizeMassingType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "solid";
        var t = raw.Trim().ToLowerInvariant();
        return MassingTypes.Any(x => x == t) ? t : "solid";
    }

    private Brep? ExtrudeFloor(Curve footprint, double heightDoc, double baseZ)
    {
        var c = footprint.DuplicateCurve();
        if (c is null) return null;
        AlignCurveToZ(c, baseZ);

        try
        {
            var ext = Extrusion.Create(c, heightDoc, cap: true);
            if (ext is not null)
            {
                var b = ext.ToBrep();
                if (b is not null && b.IsValid) return b;
            }
        }
        catch { }

        try
        {
            var bb = c.GetBoundingBox(true);
            var box = new Box(
                Plane.WorldXY,
                new Interval(bb.Min.X, bb.Max.X),
                new Interval(bb.Min.Y, bb.Max.Y),
                new Interval(baseZ, baseZ + heightDoc));
            return box.ToBrep();
        }
        catch { return null; }
    }

    private Curve? BuildEnvelope(Curve boundary, double setbackDoc)
    {
        if (boundary is null || !boundary.IsValid) return null;
        var curve = boundary.DuplicateCurve();
        if (curve is null) return null;
        if (!curve.IsClosed)
            curve.MakeClosed(_docTolerance * 10);

        if (setbackDoc <= _docTolerance)
            return curve;

        var plane = Plane.WorldXY;
        if (curve.TryGetPlane(out var cp, _docTolerance * 10))
            plane = cp;

        if (curve.ClosedCurveOrientation(plane) == CurveOrientation.Clockwise)
            curve.Reverse();

        Curve[]? offsets = null;
        try
        {
            offsets = curve.Offset(plane, -setbackDoc, _docTolerance, CurveOffsetCornerStyle.Sharp);
        }
        catch { return null; }

        if (offsets is null || offsets.Length == 0)
            return null;

        Curve? best = null;
        double bestLen = 0;
        foreach (var o in offsets)
        {
            if (o is null || !o.IsValid) continue;
            if (!o.IsClosed)
                o.MakeClosed(_docTolerance * 10);
            if (!o.IsClosed) continue;

            try
            {
                var events = Rhino.Geometry.Intersect.Intersection.CurveSelf(o, _docTolerance);
                if (events is { Count: > 0 }) continue;
            }
            catch { }

            var len = o.GetLength();
            if (len > bestLen)
            {
                bestLen = len;
                best = o;
            }
        }

        return best;
    }

    private static void AddIssue(
        ZoneAnalysis analysis, ZoneRecord zone,
        MassingIssueType type, IssueSeverity severity, string message)
    {
        analysis.Issues.Add(new ZoneIssue
        {
            Type = type switch
            {
                MassingIssueType.EnvelopeGenerationFailed => ZoneIssueType.DegenerateZone,
                MassingIssueType.ZoneTooSmall => ZoneIssueType.DegenerateZone,
                MassingIssueType.PerimeterBooleanFailed => ZoneIssueType.DegenerateZone,
                MassingIssueType.PointMassingNotFeasible => ZoneIssueType.MissingAttributes,
                _ => ZoneIssueType.MissingAttributes,
            },
            Severity = severity,
            Message = $"[massing] {message}",
            RelatedZoneId = zone.RhinoObjectId,
        });
    }

    private static int EnsureLayer(RhinoDoc doc, string fullPath, System.Drawing.Color color)
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
