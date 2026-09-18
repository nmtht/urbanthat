using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

public sealed partial class MassingGenerator
{
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
            var outerBreps = Brep.CreatePlanarBreps(envelope, _docTolerance);
            var innerBreps = Brep.CreatePlanarBreps(inner, _docTolerance);
            if (outerBreps is { Length: > 0 } && innerBreps is { Length: > 0 })
            {
                var rings = Brep.CreateBooleanDifference(outerBreps, innerBreps, _docTolerance);
                if (rings is { Length: > 0 })
                {
                    var pads = new List<FootprintPad>();
                    foreach (var ring in rings)
                    {
                        if (ring is null || !ring.IsValid) continue;
                        foreach (var face in ring.Faces)
                        {
                            var loop = face.OuterLoop?.To3dCurve();
                            if (loop is null || !loop.IsValid) continue;
                            if (!loop.IsClosed) loop.MakeClosed(_docTolerance * 10);
                            var amp = AreaMassProperties.Compute(loop);
                            var areaSqm = amp is null ? 0 : amp.Area * _docToMeters * _docToMeters;
                            if (areaSqm <= 1e-6) continue;
                            pads.Add(new FootprintPad { Curve = loop, AreaSqm = areaSqm });
                        }
                    }
                    if (pads.Count > 0) return pads;
                }
            }
        }
        catch { }

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
            var sizeScale = 0.7 + rng.NextDouble() * 0.5;
            var pad = DefaultPadSizeM * _metersToDoc * sizeScale;
            var half = pad * 0.5;
            var spanX = Math.Max(_docTolerance, bbox.Max.X - bbox.Min.X - pad);
            var spanY = Math.Max(_docTolerance, bbox.Max.Y - bbox.Min.Y - pad);
            var cx = bbox.Min.X + half + rng.NextDouble() * spanX;
            var cy = bbox.Min.Y + half + rng.NextDouble() * spanY;
            var center = new Point3d(cx, cy, bbox.Min.Z);
            if (envelope.Contains(center, Plane.WorldXY, _docTolerance) != PointContainment.Inside)
                continue;

            var rot = (rng.NextDouble() * 2 - 1) * DefaultRotationJitterDeg * (Math.PI / 180.0);
            var sq = MakeRect(cx, cy, bbox.Min.Z, half, half, rot);

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

    private List<FootprintPad> ClipPadsAwayFromRoads(RhinoDoc doc, List<FootprintPad> pads, double z0)
    {
        // Roadway only — do not use sidewalk/parking as cutters (too aggressive)
        var roads = RoadOutlineHelper.CollectPlanarRoadBreps(doc, z0, _docTolerance, includeSidewalkAndParking: false);
        if (roads.Count == 0) return pads;

        var result = new List<FootprintPad>();
        foreach (var pad in pads)
        {
            try
            {
                var planar = Brep.CreatePlanarBreps(pad.Curve, _docTolerance);
                if (planar is null || planar.Length == 0)
                {
                    result.Add(pad);
                    continue;
                }
                foreach (var piece in planar)
                {
                    var remain = RoadOutlineHelper.Subtract(piece, roads, _docTolerance);
                    if (remain.Count == 0)
                    {
                        // clip removed pad entirely — keep original so massing still appears
                        result.Add(pad);
                        continue;
                    }
                    foreach (var r in remain)
                    {
                        if (r is null || !r.IsValid) continue;
                        foreach (var face in r.Faces)
                        {
                            var loop = face.OuterLoop?.To3dCurve();
                            if (loop is null || !loop.IsValid) continue;
                            if (!loop.IsClosed) loop.MakeClosed(_docTolerance * 10);
                            var amp = AreaMassProperties.Compute(loop);
                            var areaSqm = amp is null ? 0 : amp.Area * _docToMeters * _docToMeters;
                            if (areaSqm <= 1e-6) continue;
                            result.Add(new FootprintPad { Curve = loop, AreaSqm = areaSqm });
                        }
                    }
                }
            }
            catch
            {
                result.Add(pad);
            }
        }

        // If somehow empty, fall back to original pads
        if (result.Count == 0)
            return pads;

        return result;
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
            var ext = Extrusion.Create(c, heightDoc, true);
            if (ext is not null)
            {
                var b = ext.ToBrep();
                if (b is not null && b.IsValid) return b;
            }
        }
        catch { }

        try
        {
            var planar = Brep.CreatePlanarBreps(c, _docTolerance);
            if (planar is { Length: > 0 })
            {
                var extruded = planar[0].Faces[0].CreateExtrusion(
                    new LineCurve(new Point3d(0, 0, 0), new Point3d(0, 0, heightDoc)), true);
                if (extruded is not null && extruded.IsValid)
                {
                    extruded.Translate(0, 0, baseZ - extruded.GetBoundingBox(true).Min.Z);
                    return extruded;
                }
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
