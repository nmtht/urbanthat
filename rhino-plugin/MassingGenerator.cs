using Rhino;
using Rhino.DocObjects;
using global::Rhino.Geometry;

namespace UrbanBridge.Rhino;

public sealed class MassingResult
{
    public Guid SourceZoneId { get; init; }
    public Curve? BuildableEnvelope { get; init; }
    public int Floors { get; init; }
    public double HeightM { get; init; }
    public double FarActual { get; init; }
    public double FarTarget { get; init; }
    public double FootprintAreaSqm { get; init; }
    public Brep? Volume { get; init; }
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

/// <summary>One block per zone from FAR / height_max / setback (Stage 2.3).</summary>
public sealed class MassingGenerator
{
    public const string LayerMassing = "Buildings::Massing";
    public const double FloorHeightM = 3.3;

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
                if (result.Volume is not null)
                {
                    batch.CreatedCount++;
                    batch.TotalBuiltFloorAreaSqm += result.Floors * result.FootprintAreaSqm;
                }
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
        var type = zone.ZoneType?.ToLowerInvariant() ?? "";
        if (type is "green" or "public")
        {
            analysis.Issues.Add(new ZoneIssue
            {
                Type = ZoneIssueType.MissingAttributes,
                Severity = IssueSeverity.Info,
                Message = $"Massing skipped for {type} zone (no buildings).",
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
                "Setback offset failed or self-intersects — zone too small/complex for setback.");
            return null;
        }

        var amp = AreaMassProperties.Compute(envelope);
        if (amp is null || amp.Area <= _docTolerance * _docTolerance)
        {
            AddIssue(analysis, zone, MassingIssueType.EnvelopeGenerationFailed, IssueSeverity.Error,
                "Buildable envelope has zero area.");
            return null;
        }

        var footprintAreaSqm = amp.Area * _docToMeters * _docToMeters;
        if (footprintAreaSqm <= 1e-6)
        {
            AddIssue(analysis, zone, MassingIssueType.ZoneTooSmall, IssueSeverity.Error,
                "Footprint area too small after setback.");
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
                $"FAR {zone.Far:F2} not achievable within height_max={zone.HeightMax:F1} m; clamped to {floors} floors.");
        }

        var heightM = floors * FloorHeightM;
        var heightDoc = heightM * _metersToDoc;
        var farActual = (floors * footprintAreaSqm) / zoneAreaSqm;

        Brep? volume = null;
        try
        {
            var extrude = Extrusion.Create(envelope, heightDoc, cap: true);
            if (extrude is not null)
                volume = extrude.ToBrep();
        }
        catch
        {
            volume = null;
        }

        if (volume is null || !volume.IsValid)
        {
            var bbox = envelope.GetBoundingBox(true);
            var box = new Box(
                Plane.WorldXY,
                new Interval(bbox.Min.X, bbox.Max.X),
                new Interval(bbox.Min.Y, bbox.Max.Y),
                new Interval(bbox.Min.Z, bbox.Min.Z + heightDoc));
            volume = box.ToBrep();
        }

        if (volume is null || !volume.IsValid)
        {
            AddIssue(analysis, zone, MassingIssueType.EnvelopeGenerationFailed, IssueSeverity.Error,
                "Failed to create extrusion volume.");
            return null;
        }

        var layerIndex = EnsureLayer(doc, LayerMassing, System.Drawing.Color.FromArgb(160, 160, 170));
        var attrs = new ObjectAttributes { LayerIndex = layerIndex };
        attrs.SetUserString(MassingCleanup.GeneratedByKey, MassingCleanup.GeneratedByValue);
        attrs.SetUserString(MassingCleanup.SourceZoneKey, zone.RhinoObjectId.ToString());
        attrs.SetUserString(MassingCleanup.FloorsKey, floors.ToString(System.Globalization.CultureInfo.InvariantCulture));
        attrs.SetUserString(MassingCleanup.FarActualKey,
            farActual.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
        attrs.SetUserString(MassingCleanup.FarTargetKey,
            zone.Far.ToString("G", System.Globalization.CultureInfo.InvariantCulture));

        doc.Objects.AddBrep(volume, attrs);

        return new MassingResult
        {
            SourceZoneId = zone.RhinoObjectId,
            BuildableEnvelope = envelope,
            Floors = floors,
            HeightM = heightM,
            FarActual = farActual,
            FarTarget = zone.Far,
            FootprintAreaSqm = footprintAreaSqm,
            Volume = volume,
        };
    }

    private Curve? BuildEnvelope(Curve boundary, double setbackDoc)
    {
        if (boundary is null || !boundary.IsValid) return null;
        var curve = boundary.DuplicateCurve();
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
        catch
        {
            return null;
        }

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
                var events = global::Rhino.Geometry.Intersect.Intersection.CurveSelf(o, _docTolerance);
                if (events is { Count: > 0 }) continue;
            }
            catch { /* keep candidate */ }

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
