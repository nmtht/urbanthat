using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Fillet concave corners of a closed hub polygon (the "armpits" of a cross).
/// Convex tips of road arms stay sharp.
/// </summary>
public static class IntersectionFillet
{
    /// <summary>
    /// Returns a new closed curve with concave corners rounded to <paramref name="radius"/>.
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

        // Per-vertex: optional fillet arc (null = keep sharp)
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
            // CCW solid: convex = left turn (cross>0), concave = right turn (cross<0)
            var concave = ccw ? cross < -1e-9 : cross > 1e-9;
            if (!concave)
                continue;

            var cos = Math.Max(-1.0, Math.Min(1.0, vin * vout));
            var turn = Math.Acos(cos); // 0..π exterior turn magnitude
            if (turn < 5.0 * Math.PI / 180.0 || turn > 175.0 * Math.PI / 180.0)
                continue;

            var half = turn * 0.5;
            var tanHalf = Math.Tan(half);
            if (Math.Abs(tanHalf) < 1e-9)
                continue;

            var arm = radius / tanHalf;
            // Keep fillet on each edge (shared with neighbours)
            var maxArm = Math.Min(lenIn, lenOut) * 0.45;
            if (arm > maxArm)
                arm = maxArm;
            if (arm < tolerance * 5)
                continue;

            var pStart = curr - vin * arm;
            var pEnd = curr + vout * arm;

            // Arc middle: from corner into the empty quadrant (outside the solid for a concave vertex)
            // Bisector of -vin and vout, pointing toward the exterior notch
            var bis = -vin + vout;
            bis.Z = 0;
            if (!bis.Unitize())
                continue;

            // For concave corner of a CCW polygon the exterior is to the right of vin;
            // bis as (-vin+vout) points roughly into the notch.
            var pMid = curr + bis * (radius / Math.Sin(half));
            // Distance from corner to arc midpoint along bisector is R / sin(half)
            // Actually standard: center is at distance R/sin(half) from corner along angle bisector
            // Mid of arc = center - bis * R if bis points from corner toward center...
            // Simpler: build Arc through three points — pStart, a point on the correct side, pEnd.

            // Point on arc at 50%: offset from chord toward notch
            var chordMid = (pStart + pEnd) * 0.5;
            var chordDir = pEnd - pStart; chordDir.Z = 0;
            var chordLeft = Vector3d.CrossProduct(Vector3d.ZAxis, chordDir);
            if (!chordLeft.Unitize())
                continue;

            // Choose side of chord that points away from polygon interior (into notch)
            // Interior is toward polygon centroid-ish: use curr → centroid of pts
            var centroid = Point3d.Origin;
            foreach (var p in pts) centroid += p;
            centroid /= n;
            var toCentroid = centroid - chordMid; toCentroid.Z = 0;
            if (chordLeft * toCentroid > 0)
                chordLeft = -chordLeft; // flip so left points away from centroid = into notch

            var sagitta = radius * (1.0 - Math.Cos(half)); // approx for the arc bulge
            // More accurate bulge from geometry: distance center-to-chord
            var distCornerToChord = arm * Math.Sin(half); // not quite
            // Use: mid = chordMid + chordLeft * (R * (1 - cos(π/2 - half))) 
            // For unit circle fillet, sagitta = R * (1 - sin(half))? 
            // Interior angle of polygon at concave vertex is π + turn... keep simple:
            var bulge = radius * (1.0 - Math.Cos(Math.PI / 2.0 - half));
            if (bulge < tolerance)
                bulge = radius * 0.5;
            pMid = chordMid + chordLeft * Math.Max(bulge, tolerance * 10);

            try
            {
                var arc = new Arc(pStart, pMid, pEnd);
                if (!arc.IsValid || arc.Length < tolerance * 5)
                    continue;
                arcs[i] = arc;
                armIn[i] = arm;
                armOut[i] = arm;
            }
            catch
            {
                // skip this corner
            }
        }

        // Assemble: edge segments + arcs
        var parts = new List<Curve>();
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            var a = pts[i];
            var b = pts[j];

            // Start of edge: after outgoing fillet at i (if any)
            var start = arcs[i].HasValue ? pts[i] + (b - a) / (b - a).Length * armOut[i] : a;
            // End of edge: before incoming fillet at j
            var end = arcs[j].HasValue
                ? pts[j] - (b - a) / (b - a).Length * armIn[j]
                : b;

            // Recompute with unit vector safely
            var edge = b - a; edge.Z = 0;
            var edgeLen = edge.Length;
            if (edgeLen < tolerance)
                continue;
            edge /= edgeLen;

            start = arcs[i].HasValue ? a + edge * armOut[i] : a;
            end = arcs[j].HasValue ? b - edge * armIn[j] : b;

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

        if (closed.TryGetPolyline(out Polyline pl) && pl.Count >= 3)
        {
            for (var i = 0; i < pl.Count; i++)
                pts.Add(pl[i]);
        }
        else
        {
            // Polyline approximation of polycurve (union result)
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

        // Drop duplicate closing vertex
        if (pts.Count > 1 && pts[0].DistanceTo(pts[^1]) <= tolerance * 10)
            pts.RemoveAt(pts.Count - 1);

        // Collapse near-duplicates
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
