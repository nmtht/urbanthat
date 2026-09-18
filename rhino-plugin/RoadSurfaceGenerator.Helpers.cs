using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

public sealed partial class RoadSurfaceGenerator
{
    private int AddCrossingMarkings(RhinoDoc doc, RoadNode node, List<EdgeGeom> incident)
    {
        var created = 0;
        var stopOffset = StopLineOffsetMeters * _metersToDoc;
        var crossDepth = CrosswalkDepthMeters * _metersToDoc;
        var stripeW = CrosswalkStripeMeters * _metersToDoc;
        var stripeGap = stripeW;

        foreach (var eg in incident)
        {
            var dir = DirectionFromNode(eg, node.Id);
            if (!dir.Unitize()) continue;
            var nodePt = IsStart(eg, node.Id) ? eg.Curve.PointAtStart : eg.Curve.PointAtEnd;
            var along = Math.Max(eg.ExtStart, eg.ExtEnd);
            if (along < _docTolerance) along = stopOffset;

            var perp = Vector3d.CrossProduct(dir, Vector3d.ZAxis);
            if (!perp.Unitize())
            {
                perp = Vector3d.CrossProduct(dir, Vector3d.XAxis);
                perp.Unitize();
            }

            var halfW = eg.WidthDoc * 0.5;

            // Full crosswalk as planar pedestrian surface (same type as sidewalk strip)
            var crossCenter = nodePt + dir * (along * 0.45 + crossDepth * 0.5);
            var halfD = crossDepth * 0.5;
            var a = crossCenter + dir * (-halfD) + perp * (-halfW);
            var b = crossCenter + dir * (halfD) + perp * (-halfW);
            var c = crossCenter + dir * (halfD) + perp * (halfW);
            var d = crossCenter + dir * (-halfD) + perp * (halfW);
            a.Z = b.Z = c.Z = d.Z = nodePt.Z;
            var poly = new PolylineCurve(new[] { a, b, c, d, a });
            created += AddPlanarBreps(doc, poly, LayerCrossing, nodeId: node.Id);

            // Stop line as thin mesh on top of surface
            var stopCenter = nodePt + dir * (along + stopOffset);
            created += AddMeshStripe(
                doc,
                stopCenter - dir * (stripeW * 0.25),
                dir, perp,
                stripeW * 0.5, halfW * 2,
                LayerCrossing, node.Id);

            // Zebra stripes on the crosswalk surface
            var pitch = stripeW + stripeGap;
            var nStripes = Math.Max(3, (int)(crossDepth / Math.Max(pitch, _docTolerance)));
            for (var i = 0; i < nStripes; i++)
            {
                var sc = crossCenter + dir * (-halfD + pitch * (i + 0.5));
                created += AddMeshStripe(
                    doc, sc, dir, perp,
                    stripeW, halfW * 2,
                    LayerCrossing, node.Id);
            }
        }

        return created;
    }

    /// <summary>
    /// Flat mesh rectangle centered at <paramref name="center"/>,
    /// extent along <paramref name="alongDir"/> = depth, along <paramref name="perp"/> = width.
    /// </summary>
    private int AddMeshStripe(
        RhinoDoc doc,
        Point3d center,
        Vector3d alongDir,
        Vector3d perp,
        double depth,
        double width,
        string layerPath,
        string nodeId)
    {
        if (depth <= _docTolerance || width <= _docTolerance) return 0;
        if (!alongDir.Unitize() || !perp.Unitize()) return 0;

        var halfD = depth * 0.5;
        var halfW = width * 0.5;
        var lift = _docTolerance * 8;
        var z = center.Z + lift;

        var a = center + alongDir * (-halfD) + perp * (-halfW);
        var b = center + alongDir * (halfD) + perp * (-halfW);
        var c = center + alongDir * (halfD) + perp * (halfW);
        var d = center + alongDir * (-halfD) + perp * (halfW);
        a.Z = b.Z = c.Z = d.Z = z;

        var mesh = new Mesh();
        mesh.Vertices.Add(a);
        mesh.Vertices.Add(b);
        mesh.Vertices.Add(c);
        mesh.Vertices.Add(d);
        mesh.Faces.AddFace(0, 1, 2, 3);
        mesh.Normals.ComputeNormals();

        if (!mesh.IsValid) return 0;

        var layerIndex = EnsureLayerPath(doc, layerPath, System.Drawing.Color.FromArgb(250, 250, 250));
        var attrs = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = System.Drawing.Color.FromArgb(245, 245, 245),
        };
        attrs.SetUserString(RoadSurfaceCleanup.GeneratedByKey, RoadSurfaceCleanup.GeneratedByValue);
        attrs.SetUserString(RoadSurfaceCleanup.SourceNodeKey, nodeId);
        return doc.Objects.AddMesh(mesh, attrs) != Guid.Empty ? 1 : 0;
    }

    private int AddOneWayArrows(RhinoDoc doc, Curve center, string edgeId)
    {
        var len = center.GetLength();
        var spacing = ArrowSpacingMeters * _metersToDoc;
        var arrowLen = ArrowLengthMeters * _metersToDoc;
        var halfW = ArrowHalfWidthMeters * _metersToDoc;
        if (len < arrowLen * 2) return 0;
        var created = 0;
        var plane = Plane.WorldXY;
        if (center.TryGetPlane(out var cp, _docTolerance * 10)) plane = cp;
        for (var d = spacing; d < len - arrowLen; d += spacing)
        {
            if (!center.LengthParameter(d, out var t0) || !center.LengthParameter(d + arrowLen, out var t1)) continue;
            var tip = center.PointAt(t1);
            var tail = center.PointAt(t0);
            var dir = tip - tail;
            if (!dir.Unitize()) continue;
            var perp = Vector3d.CrossProduct(dir, plane.ZAxis);
            if (!perp.Unitize()) continue;
            created += AddCurve(doc, new PolylineCurve(new[] { tail + perp * halfW, tip, tail - perp * halfW }), LayerArrows, edgeId: edgeId);
        }
        return created;
    }

    private Vector3d DirectionFromNode(EdgeGeom eg, string nodeId)
    {
        var curve = eg.Curve;
        if (IsStart(eg, nodeId))
        {
            curve.LengthParameter(Math.Min(1.0 * _metersToDoc, curve.GetLength() * 0.1), out var t);
            return curve.PointAt(t) - curve.PointAtStart;
        }
        var len = curve.GetLength();
        curve.LengthParameter(Math.Max(len - 1.0 * _metersToDoc, len * 0.9), out var t2);
        return curve.PointAt(t2) - curve.PointAtEnd;
    }

    private static double ComputeExtension(double maxWidthAtNode, double edgeLength)
    {
        var ext = (maxWidthAtNode * 0.5) * ExtensionFactor;
        return Math.Max(0, Math.Min(ext, edgeLength * MaxExtensionFraction));
    }

    private static bool IsStart(EdgeGeom eg, string nodeId) =>
        string.Equals(eg.Edge.StartNodeId, nodeId, StringComparison.Ordinal);

    private Curve? ExtractTail(Curve full, string nodeId, EdgeGeom eg, double extLen)
    {
        var len = full.GetLength();
        if (extLen <= 0 || len < extLen) return full.DuplicateCurve();
        if (IsStart(eg, nodeId))
        {
            if (!full.LengthParameter(extLen, out var t)) return null;
            return full.Trim(full.Domain.Min, t);
        }
        if (!full.LengthParameter(len - extLen, out var t2)) return null;
        return full.Trim(t2, full.Domain.Max);
    }

    private Curve? BuildOffsetStrip(Curve center, double halfWidth)
    {
        if (halfWidth <= _docTolerance || center is null || !center.IsValid) return null;
        var plane = Plane.WorldXY;
        if (center.TryGetPlane(out var curvePlane, _docTolerance * 10)) plane = curvePlane;
        var left = center.Offset(plane, halfWidth, _docTolerance, CurveOffsetCornerStyle.Sharp);
        var right = center.Offset(plane, -halfWidth, _docTolerance, CurveOffsetCornerStyle.Sharp);
        if (left is null || left.Length == 0 || right is null || right.Length == 0) return null;
        var l = left[0];
        var r = right[0];
        r.Reverse();
        var parts = new List<Curve>
        {
            l,
            new LineCurve(l.PointAtEnd, r.PointAtStart),
            r,
            new LineCurve(r.PointAtEnd, l.PointAtStart),
        };
        var joined = Curve.JoinCurves(parts, _docTolerance * 10);
        if (joined is null || joined.Length == 0) return null;
        var loop = joined[0];
        if (!loop.IsClosed) loop.MakeClosed(_docTolerance * 10);
        return loop.IsValid ? loop : null;
    }

    private List<Curve> UnionCurves(List<Curve> curves)
    {
        if (curves.Count == 0) return new List<Curve>();
        if (curves.Count == 1) return new List<Curve> { curves[0].DuplicateCurve() };
        try
        {
            var result = Curve.CreateBooleanUnion(curves, _docTolerance);
            return result is null || result.Length == 0 ? new List<Curve>() : result.ToList();
        }
        catch { return new List<Curve>(); }
    }

    private List<Curve> BooleanDifferenceCurves(Curve outer, Curve inner)
    {
        try
        {
            var result = Curve.CreateBooleanDifference(outer, inner, _docTolerance);
            return result is null || result.Length == 0 ? new List<Curve>() : result.ToList();
        }
        catch { return new List<Curve>(); }
    }

    private int AddPlanarBreps(RhinoDoc doc, Curve closed, string layerPath, string? edgeId = null, string? nodeId = null)
    {
        if (closed is null || !closed.IsValid) return 0;
        if (!closed.IsClosed) closed.MakeClosed(_docTolerance * 10);
        var breps = Brep.CreatePlanarBreps(closed, _docTolerance);
        if (breps is null || breps.Length == 0) return 0;
        var layerIndex = EnsureLayerPath(doc, layerPath, System.Drawing.Color.FromArgb(230, 230, 225));
        var count = 0;
        foreach (var brep in breps)
        {
            var attrs = new ObjectAttributes
            {
                LayerIndex = layerIndex,
                ColorSource = ObjectColorSource.ColorFromObject,
                ObjectColor = System.Drawing.Color.FromArgb(235, 235, 230),
            };
            attrs.SetUserString(RoadSurfaceCleanup.GeneratedByKey, RoadSurfaceCleanup.GeneratedByValue);
            if (edgeId is not null) attrs.SetUserString(RoadSurfaceCleanup.SourceEdgeKey, edgeId);
            if (nodeId is not null) attrs.SetUserString(RoadSurfaceCleanup.SourceNodeKey, nodeId);
            if (doc.Objects.AddBrep(brep, attrs) != Guid.Empty) count++;
        }
        return count;
    }

    private int AddCurve(RhinoDoc doc, Curve c, string layerPath, string? edgeId = null, string? nodeId = null)
    {
        if (c is null || !c.IsValid) return 0;
        var layerIndex = EnsureLayerPath(doc, layerPath, null);
        var attrs = new ObjectAttributes { LayerIndex = layerIndex };
        attrs.SetUserString(RoadSurfaceCleanup.GeneratedByKey, RoadSurfaceCleanup.GeneratedByValue);
        if (edgeId is not null) attrs.SetUserString(RoadSurfaceCleanup.SourceEdgeKey, edgeId);
        if (nodeId is not null) attrs.SetUserString(RoadSurfaceCleanup.SourceNodeKey, nodeId);
        return doc.Objects.AddCurve(c, attrs) != Guid.Empty ? 1 : 0;
    }

    private int AddLaneMarkings(RhinoDoc doc, Curve center, double widthDoc, int lanes, bool oneWay, string edgeId)
    {
        if (lanes < 1) return 0;
        var plane = Plane.WorldXY;
        if (center.TryGetPlane(out var cp, _docTolerance * 10)) plane = cp;
        if (oneWay) return AddCurve(doc, center.DuplicateCurve(), LayerMarkings, edgeId: edgeId);
        if (lanes < 2) return 0;
        var count = 0;
        for (var i = 1; i < lanes; i++)
        {
            var offset = -widthDoc * 0.5 + (widthDoc * i / lanes);
            var offs = center.Offset(plane, offset, _docTolerance, CurveOffsetCornerStyle.Smooth);
            if (offs is null) continue;
            foreach (var c in offs) count += AddCurve(doc, c, LayerMarkings, edgeId: edgeId);
        }
        return count;
    }

    private bool HasAcuteAngle(RoadNode node, List<EdgeGeom> incident)
    {
        var dirs = new List<Vector3d>();
        foreach (var eg in incident)
        {
            var v = DirectionFromNode(eg, node.Id);
            if (v.Unitize()) dirs.Add(v);
        }
        for (var i = 0; i < dirs.Count; i++)
            for (var j = i + 1; j < dirs.Count; j++)
            {
                var angle = Vector3d.VectorAngle(dirs[i], dirs[j]) * (180.0 / Math.PI);
                if (angle > 90) angle = 180 - angle;
                if (angle < AcuteAngleDegrees) return true;
            }
        return false;
    }

    private static double GetSidewalkWidthM(RhinoObject obj, string roadClass)
    {
        var s = obj.Attributes.GetUserString("sidewalk_width_m");
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) && w >= 0)
            return w;
        return roadClass.ToLowerInvariant() switch
        {
            "primary" or "secondary" => 2.5,
            "local" => 1.8,
            _ => 0.0,
        };
    }

    private static bool GetGenerateMarkings(RhinoObject obj, string roadClass)
    {
        var s = obj.Attributes.GetUserString("generate_markings");
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return roadClass.ToLowerInvariant() is "primary" or "secondary" or "local";
    }

    private static double ParseAttr(System.Collections.Specialized.NameValueCollection strings, string key, double fallback)
    {
        var s = strings.Get(key);
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static int EnsureLayerPath(RhinoDoc doc, string fullPath, System.Drawing.Color? color)
    {
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer is null || layer.IsDeleted) continue;
            if (layer.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) return i;
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
            if (parentIndex >= 0) newLayer.ParentLayerId = doc.Layers[parentIndex].Id;
            if (p == parts.Length - 1 && color.HasValue) newLayer.Color = color.Value;
            parentIndex = doc.Layers.Add(newLayer);
        }

        return parentIndex >= 0 ? parentIndex : 0;
    }

    private struct EdgeGeom
    {
        public RoadEdge Edge;
        public Curve Curve;
        public double WidthDoc;
        public double SidewalkDoc;
        public int Lanes;
        public bool GenerateMarkings;
        public double ExtStart;
        public double ExtEnd;
        public double CornerRadiusDoc;
        public double MedianWidthDoc;
        public double SidewalkGreenDoc;
        public double ParkingWidthDoc;
        public bool OneWay;
    }

    public sealed class GenerationResult
    {
        public int DeletedCount { get; set; }
        public int CreatedCount { get; set; }
        public int FailedLinks { get; set; }
        public int FailedHubs { get; set; }
    }
}
