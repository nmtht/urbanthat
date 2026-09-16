using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Fillet concave corners of a closed hub polygon (armpits of a cross).
/// Convex tips of road arms stay sharp.
/// </summary>
public static class IntersectionFillet
{
    /// <summary>
    /// New closed curve with concave corners rounded to <paramref name="radius"/>.
    /// On failure returns a duplicate of the input.
    /// </summary>
    public static Curve FilletConcaveCorners(Curve closed, double radius, double tolerance)
    {
        if (closed is null || !closed.IsValid)
            return closed;

        if (radius <= tolerance * 2)
            return closed.DuplicateCurve();

        if (!TryGetClosedPolyline(closed, tolerance, out var pts))
            return closed.DuplicateCurve();

        var n = pts.Count;
        if (n < 3)
            return closed.DuplicateCurve();

        // Signed area → orientation (CCW if positive)
        var area2 = 0.0;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            area2 += a.X * b.Y - b.X * a.Y;
        }
        var ccw = area2 > 0;

        // Centroid — used to pick the side of the chord that cuts the corner
        var centroid = Point3d.Origin;
        foreach (var p in pts)
            centroid += p;
        centroid /= n;

        var arcs = new Arc?[n];
        var armIn = new double[n];
        var armOut = new double[n];

        for (var i = 0; i < n; i++)
        {
            arcs[i] = null;
            armIn[i] = armOut[i] = 0;

            var prev = pts[(i - 1 + n) % n];
            var curr = pts[i];
            var next = pts[(i + 1) % n];

            var vin = curr - prev; vin.Z = 0;
            var vout = next - curr; vout.Z = 0;
            var lenIn = vin.Length;
            var lenOut = vout.Length;
            if (lenIn < tolerance * 10 || lenOut < tolerance * 10)
                continue;
            if (!vin.Unitize() || !vout.Unitize())
                continue;

            var cross = vin.X * vout.Y - vin.Y * vout.X;
            // CCW solid: concave = right turn (cross < 0)
            var concave = ccw ? cross < -1e-9 : cross > 1e-9;
            if (!concave)
                continue;

            var cos = Math.Max(-1.0, Math.Min(1.0, vin * vout));
            var turn = Math.Acos(cos); // angle between edge directions, 0..π
            if (turn < 5.0 * Math.PI / 180.0 || turn > 175.0 * Math.PI / 180.0)
                continue;

            var half = turn * 0.5;
            var tanHalf = Math.Tan(half);
            if (Math.Abs(tanHalf) < 1e-9)
                continue;

            // Distance vertex → tangent point along each edge
            var arm = radius / tanHalf;
            var maxArm = Math.Min(lenIn, lenOut) * 0.45;
            if (arm > maxArm)
                arm = maxArm;
            if (arm < tolerance * 5)
                continue;

            var pStart = curr - vin * arm;
            var pEnd = curr + vout * arm;

            // Chord pStart–pEnd; arc must sit on the side TOWARD the centroid
            // so the sharp tip is cut off (no external lobes).
            var chordMid = (pStart + pEnd) * 0.5;
            var chord = pEnd - pStart; chord.Z = 0;
            if (chord.Length < tolerance)
                continue;
            var chordLeft = Vector3d.CrossProduct(Vector3d.ZAxis, chord);
            if (!chordLeft.Unitize())
                continue;

            var toCentroid = centroid - chordMid; toCentroid.Z = 0;
            // Point chordLeft toward the centroid (interior)
            if (chordLeft * toCentroid < 0)
                chordLeft = -chordLeft;

            // Sagitta for circular arc spanning angle (π - turn) interior reflex...
            // For the minor arc between tangent points: central angle = π - turn? 
            // Actually central angle equals turn for the complementary sector.
            // sagitta = R * (1 - cos(half)) is wrong for 90°; use:
            //   half-chord = |pEnd-pStart|/2, sagitta = R - sqrt(R² - halfChord²)
            var halfChord = pStart.DistanceTo(pEnd) * 0.5;
            if (halfChord >= radius)
            {
                // radius too small for this arm — skip
                continue;
            }
            var sagitta = radius - Math.Sqrt(Math.Max(0, radius * radius - halfChord * halfChord));
            if (sagitta < tolerance)
                continue;

            var pMid = chordMid + chordLeft * sagitta;

            try
            {
                var arc = new Arc(pStart, pMid, pEnd);
                if (!arc.IsValid || arc.Length < tolerance * 5)
                    continue;
                // Reject near-full circles (inverted / long-way arc)
                if (arc.Angle > Math.PI * 0.95)
                    continue;

                arcs[i] = arc;
                armIn[i] = arm;
                armOut[i] = arm;
            }
            catch
            {
                // skip corner
            }
        }

        // Assemble edges + arcs
        var parts = new List<Curve>();
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            var a = pts[i];
            var b = pts[j];
            var edge = b - a; edge.Z = 0;
            var edgeLen = edge.Length;
            if (edgeLen < tolerance)
                continue;
            edge /= edgeLen;

            var start = arcs[i].HasValue ? a + edge * armOut[i] : a;
            var end = arcs[j].HasValue ? b - edge * armIn[j] : b;

            if (start.DistanceTo(end) > tolerance)
                parts.Add(new LineCurve(start, end));

            if (arcs[j].HasValue)
                parts.Add(new ArcCurve(arcs[j]!.Value));
        }

        if (parts.Count == 0)
            return closed.DuplicateCurve();

        var joined = Curve.JoinCurves(parts, tolerance * 10);
        if (joined is null || joined.Length == 0)
            return closed.DuplicateCurve();

        var result = joined[0];
        if (!result.IsClosed)
            result.MakeClosed(tolerance * 10);

        return result.IsValid ? result : closed.DuplicateCurve();
    }

    private static bool TryGetClosedPolyline(Curve closed, double tolerance, out List<Point3d> pts)
    {
        pts = new List<Point3d>();

        Polyline pl;
        if (closed.TryGetPolyline(out pl) && pl.Count >= 3)
        {
            for (var i = 0; i < pl.Count; i++)
                pts.Add(pl[i]);
        }
        else
        {
            var length = closed.GetLength();
            if (length < tolerance * 10)
                return false;

            var maxSeg = Math.Max(length * 0.02, tolerance * 50);
            var polylineCurve = closed.ToPolyline(0, 0, 0.05, maxSeg, 0, tolerance, 0, 0, true);
            if (polylineCurve is null)
                return false;
            if (!polylineCurve.TryGetPolyline(out pl) || pl.Count < 3)
                return false;

            for (var i = 0; i < pl.Count; i++)
                pts.Add(pl[i]);
        }

        if (pts.Count > 1 && pts[0].DistanceTo(pts[^1]) <= tolerance * 10)
            pts.RemoveAt(pts.Count - 1);

        var cleaned = new List<Point3d>();
        foreach (var p in pts)
        {
            if (cleaned.Count == 0 || cleaned[^1].DistanceTo(p) > tolerance * 10)
                cleaned.Add(p);
        }
        if (cleaned.Count > 1 && cleaned[0].DistanceTo(cleaned[^1]) <= tolerance * 10)
            cleaned.RemoveAt(cleaned.Count - 1);

        pts = cleaned;
        return pts.Count >= 3;
    }
}
