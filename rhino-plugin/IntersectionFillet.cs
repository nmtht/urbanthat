using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>
/// Intersection corner fillets with correct orientation.
/// 1) Drop collinear vertices so only real corners remain.
/// 2) Rhino CreateFilletCornersCurve (correct winding).
/// 3) Reject results that invert/explode area.
/// </summary>
public static class IntersectionFillet
{
    public const double CollinearDegrees = 10.0;

    /// <summary>
    /// Fillet corners of a closed planar curve. Returns null if unchanged.
    /// </summary>
    public static Curve? FilletExteriorCorners(Curve closed, double radius, double tolerance)
    {
        if (closed is null || !closed.IsValid || radius <= tolerance * 2)
            return null;

        if (!closed.IsClosed)
            closed.MakeClosed(tolerance * 10);

        var simplified = SimplifyClosed(closed, tolerance) ?? closed;

        try
        {
            var native = Curve.CreateFilletCornersCurve(
                simplified, radius, tolerance, Math.PI / 180.0);

            if (native is not null && native.IsValid)
            {
                if (!native.IsClosed)
                    native.MakeClosed(tolerance * 10);

                if (IsReasonableFillet(simplified, native))
                    return native;
            }
        }
        catch
        {
            // keep original
        }

        return null;
    }

    /// <summary>Remove near-collinear vertices so fillet only hits real corners.</summary>
    public static Curve? SimplifyClosed(Curve closed, double tolerance)
    {
        if (!closed.TryGetPolyline(out var poly) || poly.Count < 4)
        {
            var pl = closed.ToPolyline(
                0, 0, 0.05, 0, 0, Math.Max(tolerance * 5, 0.01), 0, 0, true);
            if (pl is null || !pl.TryGetPolyline(out poly) || poly.Count < 4)
                return null;
        }

        while (poly.Count > 3 && poly[0].DistanceTo(poly[poly.Count - 1]) <= tolerance * 10)
            poly.RemoveAt(poly.Count - 1);

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

        if (pts.Count < 3)
            return null;

        pts.Add(pts[0]);
        var simple = new PolylineCurve(pts);
        if (!simple.IsClosed)
            simple.MakeClosed(tolerance * 10);
        return simple.IsValid ? simple : null;
    }

    /// <summary>
    /// Fillet must slightly reduce area (cuts corners), not grow or collapse.
    /// Growth usually means inverted arcs.
    /// </summary>
    private static bool IsReasonableFillet(Curve original, Curve filleted)
    {
        try
        {
            var a0 = AreaMassProperties.Compute(original);
            var a1 = AreaMassProperties.Compute(filleted);
            if (a0 is null || a1 is null) return true;
            if (a1.Area <= 0) return false;
            // Inverted fillets often increase enclosed area of sidewalk rings oddly
            // or collapse; allow modest shrink only
            if (a1.Area > a0.Area * 1.15) return false;
            if (a1.Area < a0.Area * 0.35) return false;
            return true;
        }
        catch
        {
            return true;
        }
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
