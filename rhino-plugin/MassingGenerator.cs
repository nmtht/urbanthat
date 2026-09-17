using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

public sealed class MassingResult
{
    public Guid SourceZoneId { get; init; }
    public string MassingType { get; init; } = "solid";
    public int Floors { get; init; }
    public double HeightM { get; init; }
    public double FarActual { get; init; }
    public double FarTarget { get; init; }
    public double FootprintAreaSqm { get; init; }
    public int VolumeCount { get; init; }
}

public enum MassingIssueType
{
    EnvelopeGenerationFailed,
    FarNotAchievableWithinHeightLimit,
    ZoneTooSmall,
    SkippedGreenOrPublic,
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
/// Massing by massing_type: solid | perimeter | point | random | row.
/// One Brep per floor slab (floor_index 1..N).
/// </summary>
public sealed class MassingGenerator
{
    public const string LayerMassing = "Buildings::Massing";
    public const double FloorHeightM = 3.3;
    public const double DefaultBlockDepthM = 14.0;
    public const double DefaultPadSizeM = 18.0;
    public const double DefaultMinGapM = 8.0;
    public const double DefaultCoverage = 0.40;

    public static readonly string[] MassingTypes =
    {
        "solid", "perimeter", "point", "random", "row",
    };

    private readonly double _docTolerance;
    private readonly double _metersToDoc;
    private readonly double _docToMeters;

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
                batch.TotalBuiltFloorAreaSqm += result.Floors * result.FootprintAreaSqm;
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

        doc.Views.Redraw();
        return batch;
    }

    private MassingResult? GenerateOne(RhinoDoc doc, ZoneRecord zone, ZoneAnalysis analysis)
    {
        var ztype = zone.ZoneType?.ToLowerInvariant() ?? "";
        if (ztype is "green" or "public")
        {
            analysis.Issues.Add(new ZoneIssue
            {
                Type = ZoneIssueType.MissingAttributes,
                Severity = IssueSeverity.Info,
                Message = $"Massing skipped for {ztype} zone (no buildings).",
                RelatedZoneId = zone.RhinoObjectId,
            });
            return null;
        }

        analysis.MetricsById.TryGetValue(zone.RhinoObjectId, out var metrics);
        var zoneAreaSqm = metrics?.AreaSqm ?? 0;
        var targetFloorArea = metrics?.BuildableAreaSqm ?? (zoneAreaSqm * zone.Far);
        if (zoneAreaSqm <= 1e-6)
        {
            AddIssue(analysis, zone, MassingIssueType.ZoneTooSmall, IssueSeverity.Error,
                "Zone area is zero — cannot generate massing.");
            return null;
        }

        var setbackDoc = Math.Max(0, zone.SetbackM) * _metersToDoc;
        var envelope = BuildEnvelope(zone.Boundary, setbackDoc);
        if (envelope is null)
        {
            AddIssue(analysis, zone, MassingIssueType.EnvelopeGenerationFailed, IssueSeverity.Error,
                "Setback offset failed or self-intersects.");
            return null;
        }

        var massingType = NormalizeMassingType(zone.MassingType);
        var footprints = BuildFootprints(envelope, massingType);
        if (footprints.Count == 0)
        {
            AddIssue(analysis, zone, MassingIssueType.EnvelopeGenerationFailed, IssueSeverity.Error,
                $"No footprints for massing_type={massingType}.");
            return null;
        }

        var footprintAreaSqm = 0.0;
        foreach (var fp in footprints)
        {
            var amp = AreaMassProperties.Compute(fp);
            if (amp is not null)
                footprintAreaSqm += amp.Area * _docToMeters * _docToMeters;
        }

        if (footprintAreaSqm <= 1e-6)
        {
            AddIssue(analysis, zone, MassingIssueType.ZoneTooSmall, IssueSeverity.Error,
                "Footprint area too small.");
            return null;
        }

        var floors = (int)Math.Ceiling(targetFloorArea / footprintAreaSqm);
        if (floors < 1) floors = 1;

        var maxFloorsByHeight = (int)Math.Floor(zone.HeightMax / FloorHeightM);
        if (maxFloorsByHeight < 1)
        {
            AddIssue(analysis, zone, MassingIssueType.ZoneTooSmall, IssueSeverity.Error,
                $"height_max={zone.HeightMax:F1} m < one floor ({FloorHeightM} m).");
            return null;
        }

        if (floors > maxFloorsByHeight)
        {
            floors = maxFloorsByHeight;
            AddIssue(analysis, zone, MassingIssueType.FarNotAchievableWithinHeightLimit, IssueSeverity.Warning,
                $"FAR {zone.Far:F2} not achievable within height_max; clamped to {floors} floors.");
        }

        var floorHeightDoc = FloorHeightM * _metersToDoc;
        var farActual = (floors * footprintAreaSqm) / zoneAreaSqm;
        var layerIndex = EnsureLayer(doc, LayerMassing, System.Drawing.Color.FromArgb(160, 160, 170));
        var volumeCount = 0;

        foreach (var fp in footprints)
        {
            for (var f = 0; f < floors; f++)
            {
                var slab = ExtrudeFloor(fp, floorHeightDoc, f * floorHeightDoc);
                if (slab is null || !slab.IsValid) continue;

                var attrs = new ObjectAttributes { LayerIndex = layerIndex };
                attrs.SetUserString(MassingCleanup.GeneratedByKey, MassingCleanup.GeneratedByValue);
                attrs.SetUserString(MassingCleanup.SourceZoneKey, zone.RhinoObjectId.ToString());
                attrs.SetUserString(MassingCleanup.FloorsKey, floors.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

        return new MassingResult
        {
            SourceZoneId = zone.RhinoObjectId,
            MassingType = massingType,
            Floors = floors,
            HeightM = floors * FloorHeightM,
            FarActual = farActual,
            FarTarget = zone.Far,
            FootprintAreaSqm = footprintAreaSqm,
            VolumeCount = volumeCount,
        };
    }

    private static string NormalizeMassingType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "solid";
        var t = raw.Trim().ToLowerInvariant();
        return MassingTypes.Any(x => x == t) ? t : "solid";
    }

    private List<Curve> BuildFootprints(Curve envelope, string massingType)
    {
        return massingType switch
        {
            "perimeter" => BuildPerimeterFootprints(envelope),
            "point" => BuildPointFootprints(envelope, jitter: false),
            "random" => BuildPointFootprints(envelope, jitter: true),
            "row" => BuildRowFootprints(envelope),
            _ => new List<Curve> { envelope.DuplicateCurve()! },
        };
    }

    private List<Curve> BuildPerimeterFootprints(Curve envelope)
    {
        var depthDoc = DefaultBlockDepthM * _metersToDoc;
        var inner = BuildEnvelope(envelope, depthDoc);
        if (inner is null)
            return new List<Curve> { envelope.DuplicateCurve()! };

        try
        {
            var diff = Curve.CreateBooleanDifference(envelope, inner, _docTolerance);
            if (diff is { Length: > 0 })
                return diff.Where(c => c is not null && c.IsValid).Select(c => c!).ToList();
        }
        catch { }

        return new List<Curve> { envelope.DuplicateCurve()! };
    }

    private List<Curve> BuildPointFootprints(Curve envelope, bool jitter)
    {
        var result = new List<Curve>();
        var bbox = envelope.GetBoundingBox(true);
        var pad = DefaultPadSizeM * _metersToDoc;
        var gap = DefaultMinGapM * _metersToDoc;
        var step = pad + gap;
        if (step < _docTolerance * 50)
            return new List<Curve> { envelope.DuplicateCurve()! };

        var amp = AreaMassProperties.Compute(envelope);
        var envelopeArea = amp?.Area ?? 0;
        var maxPads = envelopeArea > 0
            ? Math.Max(1, (int)(envelopeArea * DefaultCoverage / (pad * pad)))
            : 12;

        var rng = jitter ? new Random(HashCode.Combine(
            (int)(bbox.Center.X * 100), (int)(bbox.Center.Y * 100))) : null;

        var count = 0;
        for (var x = bbox.Min.X + pad * 0.5; x <= bbox.Max.X - pad * 0.5 && count < maxPads; x += step)
        {
            for (var y = bbox.Min.Y + pad * 0.5; y <= bbox.Max.Y - pad * 0.5 && count < maxPads; y += step)
            {
                var cx = x;
                var cy = y;
                if (rng is not null)
                {
                    cx += (rng.NextDouble() - 0.5) * gap * 0.6;
                    cy += (rng.NextDouble() - 0.5) * gap * 0.6;
                }

                var center = new Point3d(cx, cy, bbox.Min.Z);
                if (envelope.Contains(center, Plane.WorldXY, _docTolerance) != PointContainment.Inside)
                    continue;

                var half = pad * 0.5;
                var sq = new PolylineCurve(new[]
                {
                    new Point3d(cx - half, cy - half, bbox.Min.Z),
                    new Point3d(cx + half, cy - half, bbox.Min.Z),
                    new Point3d(cx + half, cy + half, bbox.Min.Z),
                    new Point3d(cx - half, cy + half, bbox.Min.Z),
                    new Point3d(cx - half, cy - half, bbox.Min.Z),
                });
                result.Add(sq);
                count++;
            }
        }

        return result.Count > 0 ? result : new List<Curve> { envelope.DuplicateCurve()! };
    }

    private List<Curve> BuildRowFootprints(Curve envelope)
    {
        var result = new List<Curve>();
        var bbox = envelope.GetBoundingBox(true);
        var depth = DefaultBlockDepthM * _metersToDoc;
        var gap = DefaultMinGapM * _metersToDoc;
        var sizeX = bbox.Max.X - bbox.Min.X;
        var sizeY = bbox.Max.Y - bbox.Min.Y;
        var alongX = sizeX >= sizeY;

        if (alongX)
        {
            for (var y = bbox.Min.Y + depth * 0.5; y <= bbox.Max.Y - depth * 0.5; y += depth + gap)
            {
                var rect = new PolylineCurve(new[]
                {
                    new Point3d(bbox.Min.X + gap * 0.25, y - depth * 0.5, bbox.Min.Z),
                    new Point3d(bbox.Max.X - gap * 0.25, y - depth * 0.5, bbox.Min.Z),
                    new Point3d(bbox.Max.X - gap * 0.25, y + depth * 0.5, bbox.Min.Z),
                    new Point3d(bbox.Min.X + gap * 0.25, y + depth * 0.5, bbox.Min.Z),
                    new Point3d(bbox.Min.X + gap * 0.25, y - depth * 0.5, bbox.Min.Z),
                });
                // Keep only if center is inside envelope
                var c = new Point3d(bbox.Center.X, y, bbox.Min.Z);
                if (envelope.Contains(c, Plane.WorldXY, _docTolerance) == PointContainment.Inside)
                    result.Add(rect);
            }
        }
        else
        {
            for (var x = bbox.Min.X + depth * 0.5; x <= bbox.Max.X - depth * 0.5; x += depth + gap)
            {
                var rect = new PolylineCurve(new[]
                {
                    new Point3d(x - depth * 0.5, bbox.Min.Y + gap * 0.25, bbox.Min.Z),
                    new Point3d(x + depth * 0.5, bbox.Min.Y + gap * 0.25, bbox.Min.Z),
                    new Point3d(x + depth * 0.5, bbox.Max.Y - gap * 0.25, bbox.Min.Z),
                    new Point3d(x - depth * 0.5, bbox.Max.Y - gap * 0.25, bbox.Min.Z),
                    new Point3d(x - depth * 0.5, bbox.Min.Y + gap * 0.25, bbox.Min.Z),
                });
                var c = new Point3d(x, bbox.Center.Y, bbox.Min.Z);
                if (envelope.Contains(c, Plane.WorldXY, _docTolerance) == PointContainment.Inside)
                    result.Add(rect);
            }
        }

        return result.Count > 0 ? result : new List<Curve> { envelope.DuplicateCurve()! };
    }

    private Brep? ExtrudeFloor(Curve footprint, double heightDoc, double baseZ)
    {
        var c = footprint.DuplicateCurve();
        if (c is null) return null;

        // Move curve to slab base elevation
        var bbox = c.GetBoundingBox(true);
        var dz = baseZ - bbox.Min.Z;
        if (Math.Abs(dz) > _docTolerance)
            c.Translate(0, 0, dz);

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
