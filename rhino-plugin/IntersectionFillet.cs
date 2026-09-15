using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>
/// Intersection fillets: only CONVEX corners (street outer corners).
/// Concave re-entrant corners at link/hub junctions are left sharp —
/// filleting them produced the inverted "bite" shapes.
/// </summary>
public static class IntersectionFillet
{
    public const double CollinearDegrees = 10.0;
    public const double MinCornerDegrees = 20.0;
    public const double MaxCornerDegrees = 150.0;

    public static Curve? FilletExteriorCorners(Curve closed, double radius, double tolerance)
    {
        if (closed is null || !closed.IsValid || radius <= tolerance * 2)
            return null;

        if (!closed.IsClosed)
            closed.MakeClosed(tolerance * 10);

        if (!TryGetOpenPolyline(closed, tolerance, out var poly))
            return null;

        var n = poly.Count;
        if (n < 3) return null;

        // Shoelace: positive => CCW
        double shoelace = 0;
        for (var i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            shoelace += a.X * b.Y - b.X * a.Y;
        }
        var isCcw = shoelace > 0;

        // Precompute convex fillets at each vertex (or null)
        var fillets = new (Point3d Start, Point3d End, ArcCurve Arc)?[n];
        var any = false;

        for (var i = 0; i < n; i++)
        {
            var f = TryConvexFillet(
                poly[(i - 1 + n) % n], poly[i], poly[(i + 1) % n],
                radius, tolerance, isCcw);
            if (f is not null)
            {
                fillets[i] = f;
                any = true;
            }
        }

        if (!any) return null;

        // Walk loop: line to fillet start, arc, …
        var pieces = new List<Curve>();
        for (var i = 0; i < n; i++)
        {
            var prev = (i - 1 + n) % n;
            var from = fillets[prev] is { } pf ? pf.End : poly[prev];
            var to = fillets[i] is { } cf ? cf.Start : poly[i];

            if (from.DistanceTo(to) > tolerance)
                pieces.Add(new LineCurve(from, to));

            if (fillets[i] is { } fillet)
                pieces.Add(fillet.Arc);
        }

        pieces = pieces.Where(c => c.IsValid && c.GetLength() > tolerance).ToList();
        if (pieces.Count == 0) return null;

        var joined = Curve.JoinCurves(pieces, tolerance * 10);
        if (joined is null || joined.Length == 0) return null;

        var loop = joined.OrderByDescending(c => c.GetLength()).First();
        if (!loop.IsClosed)
            loop.MakeClosed(tolerance * 10);

        if (!IsReasonableFillet(closed, loop))
            return null;

        return loop.IsValid ? loop : null;
    }

    private static (Point3d Start, Point3d End, ArcCurve Arc)? TryConvexFillet(
        Point3d pPrev, Point3d pCurr, Point3d pNext,
        double radius, double tolerance, bool polyIsCcw)
    {
        var vIn = pCurr - pPrev;
        var vOut = pNext - pCurr;
        var lenIn = vIn.Length;
        var lenOut = vOut.Length;
        if (lenIn <= tolerance || lenOut <= tolerance || !vIn.Unitize() || !vOut.Unitize())
            return null;

        var deflect = Vector3d.VectorAngle(vIn, vOut);
        var deg = deflect * (180.0 / Math.PI);
        if (deg < MinCornerDegrees || deg > MaxCornerDegrees)
            return null;

        // Convex relative to winding: CCW poly → left turn; CW → right turn
        var crossZ = vIn.X * vOut.Y - vIn.Y * vOut.X;
        var isLeft = crossZ > 0;
        if (polyIsCcw && !isLeft) return null;  // concave — skip (the inverted bites)
        if (!polyIsCcw && isLeft) return null;

        var tanHalf = Math.Tan(deflect * 0.5);
        if (tanHalf < 1e-8) return null;

        var r = radius;
        var t = r * tanHalf;
        var maxT = Math.Min(lenIn, lenOut) * 0.45;
        if (t > maxT)
        {
            t = maxT;
            r = t / Math.Max(tanHalf, 1e-8);
        }

        if (r <= tolerance * 2 || t <= tolerance)
            return null;

        var pStart = pCurr - vIn * t;
        var pEnd = pCurr + vOut * t;
        var midChord = (pStart + pEnd) * 0.5;

        // Arc cuts the corner: midpoint lies toward interior of the turn (into angle)
        var intoAngle = midChord - pCurr;
        if (!intoAngle.Unitize()) return null;

        var halfChord = pStart.DistanceTo(pEnd) * 0.5;
        if (halfChord >= r - tolerance * 0.5) return null;

        var sagitta = r - Math.Sqrt(Math.Max(0.0, r * r - halfChord * halfChord));
        var pMid = midChord + intoAngle * sagitta;

        // If we accidentally went the exterior way, flip
        var pMidAlt = midChord - intoAngle * sagitta;
        if (pCurr.DistanceTo(pMid) > pCurr.DistanceTo(pMidAlt))
            pMid = pMidAlt;

        try
        {
            var arc = new Arc(pStart, pMid, pEnd);
            if (!arc.IsValid) return null;
            return (pStart, pEnd, new ArcCurve(arc));
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetOpenPolyline(Curve closed, double tolerance, out Polyline poly)
    {
        poly = null!;
        if (closed.TryGetPolyline(out poly) && poly.Count >= 4)
        {
            // ok
        }
        else
        {
            var pl = closed.ToPolyline(
                0, 0, 0.05, 0, 0, Math.Max(tolerance * 5, 0.01), 0, 0, true);
            if (pl is null || !pl.TryGetPolyline(out poly) || poly.Count < 4)
                return false;
        }

        while (poly.Count > 3 && poly[0].DistanceTo(poly[poly.Count - 1]) <= tolerance * 10)
            poly.RemoveAt(poly.Count - 1);

        // Drop collinear
        var pts = new List<Point3d>();
        var n = poly.Count;
        for (var i = 0; i < n; i++)
        {
            var prev = poly[(i - 1 + n) % n];
            var curr = poly[i];
            var next = poly[(i + 1) % n];
            var v0 = curr - prev;
            var v1 = next - curr;
            if (!v0.Unitize() || !v1.Unitize())
            {
                pts.Add(curr);
                continue;
            }
            var deg = Vector3d.VectorAngle(v0, v1) * (180.0 / Math.PI);
            if (deg >= CollinearDegrees)
                pts.Add(curr);
        }

        if (pts.Count < 3) return false;
        poly = new Polyline(pts);
        return true;
    }

    private static bool IsReasonableFillet(Curve original, Curve filleted)
    {
        try
        {
            var a0 = AreaMassProperties.Compute(original);
            var a1 = AreaMassProperties.Compute(filleted);
            if (a0 is null || a1 is null) return true;
            if (a1.Area <= 0) return false;
            if (a1.Area > a0.Area * 1.1) return false;  // inverted → area grew
            if (a1.Area < a0.Area * 0.4) return false;
            return true;
        }
        catch { return true; }
    }

    public static double ClampRadius(
        double requested, double halfWidthA, double halfWidthB, double turnRadians)
    {
        if (requested <= 0) return 0;
        var turn = Math.Abs(turnRadians);
        if (turn < 1e-3 || turn > Math.PI - 1e-3) return 0;

        var minHalf = Math.Max(Math.Min(halfWidthA, halfWidthB), 1e-6);
        var byWidth = minHalf * 1.25;
        var byAngle = minHalf / Math.Max(Math.Sin(turn * 0.5), 0.2);
        return Math.Min(requested, Math.Min(byWidth, byAngle));
    }

    public static List<T> SortByOutboundAngle<T>(
        IReadOnlyList<T> items, Func<T, Vector3d> outbound)
    {
        return items
            .Select(item =>
            {
                var d = outbound(item);
                d.Z = 0;
                return (item, angle: Math.Atan2(d.Y, d.X));
            })
            .OrderBy(t => t.angle)
            .Select(t => t.item)
            .ToList();
    }
}
