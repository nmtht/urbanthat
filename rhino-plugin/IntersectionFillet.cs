using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>
/// Street-corner fillets via pure geometry of two outer curb lines.
/// Does NOT rely on CreateFilletCurves (failed on non-touching offset lines).
/// </summary>
public static class IntersectionFillet
{
    public readonly struct HubLeg
    {
        public Vector3d Outbound { get; init; }
        public double OuterHalf { get; init; }
        public double RoadHalf { get; init; }
        public double Radius { get; init; }
        public double ExtLength { get; init; }
    }

    /// <summary>Open arc curves for each consecutive pair of legs (CCW sorted).</summary>
    public static List<Curve> BuildPairwiseOuterArcs(
        Point3d nodePt,
        IReadOnlyList<HubLeg> legsCcw,
        double tolerance)
    {
        var arcs = new List<Curve>();
        var n = legsCcw.Count;
        if (n < 2) return arcs;

        for (var i = 0; i < n; i++)
        {
            var a = legsCcw[i];
            var b = legsCcw[(i + 1) % n];
            var arc = TryCornerArc(nodePt, a, b, useOuter: true, tolerance);
            if (arc is not null)
                arcs.Add(arc);
        }

        return arcs;
    }

    /// <summary>Closed corner pads (arc + two radials to virtual tip) for planar breps.</summary>
    public static List<Curve> BuildPairwiseCornerPads(
        Point3d nodePt,
        IReadOnlyList<HubLeg> legsCcw,
        bool useOuter,
        double tolerance)
    {
        var pads = new List<Curve>();
        var n = legsCcw.Count;
        if (n < 2) return pads;

        for (var i = 0; i < n; i++)
        {
            var a = legsCcw[i];
            var b = legsCcw[(i + 1) % n];
            var pad = TryCornerPad(nodePt, a, b, useOuter, tolerance);
            if (pad is not null)
                pads.Add(pad);
        }

        return pads;
    }

    private static Curve? TryCornerArc(
        Point3d nodePt, HubLeg a, HubLeg b, bool useOuter, double tolerance)
    {
        if (!TryCornerGeometry(nodePt, a, b, useOuter, tolerance,
                out var pStart, out var pEnd, out var pMid, out var radius))
            return null;

        try
        {
            var arc = new Arc(pStart, pMid, pEnd);
            if (!arc.IsValid || arc.Radius < tolerance * 2)
                return null;
            return new ArcCurve(arc);
        }
        catch
        {
            return null;
        }
    }

    private static Curve? TryCornerPad(
        Point3d nodePt, HubLeg a, HubLeg b, bool useOuter, double tolerance)
    {
        if (!TryCornerGeometry(nodePt, a, b, useOuter, tolerance,
                out var pStart, out var pEnd, out var pMid, out var radius))
            return null;

        try
        {
            var arc = new Arc(pStart, pMid, pEnd);
            if (!arc.IsValid) return null;

            // Closed pad: arc + chord (fills the cut-off tip region)
            var arcCrv = new ArcCurve(arc);
            var chord = new LineCurve(pEnd, pStart);
            var joined = Curve.JoinCurves(new Curve[] { arcCrv, chord }, tolerance * 10);
            if (joined is null || joined.Length == 0) return null;
            var loop = joined[0];
            if (!loop.IsClosed)
                loop.MakeClosed(tolerance * 10);
            return loop.IsValid ? loop : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Core geometry: two offset curb lines → virtual corner P → fillet arc points.
    /// Outer curb of A = right of outbound A; outer of B = left of outbound B (CCW legs).
    /// </summary>
    private static bool TryCornerGeometry(
        Point3d nodePt,
        HubLeg a,
        HubLeg b,
        bool useOuter,
        double tolerance,
        out Point3d pStart,
        out Point3d pEnd,
        out Point3d pMid,
        out double radius)
    {
        pStart = pEnd = pMid = Point3d.Origin;
        radius = 0;

        var dirA = a.Outbound; dirA.Z = 0;
        var dirB = b.Outbound; dirB.Z = 0;
        if (!dirA.Unitize() || !dirB.Unitize()) return false;

        var leftA = Vector3d.CrossProduct(Vector3d.ZAxis, dirA);
        var leftB = Vector3d.CrossProduct(Vector3d.ZAxis, dirB);
        if (!leftA.Unitize() || !leftB.Unitize()) return false;
        var rightA = -leftA;

        var halfA = useOuter ? a.OuterHalf : a.RoadHalf;
        var halfB = useOuter ? b.OuterHalf : b.RoadHalf;
        halfA = Math.Max(halfA, tolerance * 10);
        halfB = Math.Max(halfB, tolerance * 10);

        // Outer curb origins near the node
        var originA = nodePt + rightA * halfA;
        var originB = nodePt + leftB * halfB;

        radius = Math.Min(
            a.Radius > 0 ? a.Radius : b.Radius,
            b.Radius > 0 ? b.Radius : a.Radius);
        if (radius <= tolerance * 2)
            radius = Math.Max(a.Radius, b.Radius);
        if (radius <= tolerance * 2) return false;

        // CCW turn from A to B
        var crossZ = dirA.X * dirB.Y - dirA.Y * dirB.X;
        var ccwTurn = Math.Atan2(crossZ, dirA * dirB);
        if (ccwTurn < 0) ccwTurn += 2 * Math.PI;
        // Skip nearly straight or U-turns
        if (ccwTurn < 15.0 * Math.PI / 180.0 || ccwTurn > 165.0 * Math.PI / 180.0)
            return false;

        radius = ClampRadius(radius, halfA, halfB, ccwTurn);
        if (radius <= tolerance * 2) return false;

        // Virtual corner: intersection of the two infinite curb lines
        if (!LineLineIntersection(originA, dirA, originB, dirB, out var corner))
            return false;

        // Directions from corner along each curb INTO the road (away from exterior tip).
        // originA should lie roughly along +dirA from corner (or -dirA).
        var toOriginA = originA - corner;
        var toOriginB = originB - corner;
        var armA = (toOriginA * dirA >= 0) ? dirA : -dirA;
        var armB = (toOriginB * dirB >= 0) ? dirB : -dirB;
        if (!armA.Unitize() || !armB.Unitize()) return false;

        // Convex corner angle between arms (should match ccwTurn approximately)
        var alpha = Vector3d.VectorAngle(armA, armB);
        if (alpha < 15.0 * Math.PI / 180.0 || alpha > 165.0 * Math.PI / 180.0)
            return false;

        var half = alpha * 0.5;
        var tanHalf = Math.Tan(half);
        if (tanHalf < 1e-8) return false;

        // Distance from vertex to tangent points: R / tan(α/2)
        var d = radius / tanHalf;

        // Clamp so tangent points stay within a reasonable distance of the origins
        var maxD = Math.Max(a.ExtLength, b.ExtLength) * 2 + halfA + halfB;
        if (d > maxD)
        {
            d = maxD;
            radius = d * tanHalf;
        }
        if (radius <= tolerance * 2) return false;

        pStart = corner + armA * d;
        pEnd = corner + armB * d;

        // Arc midpoint: from corner along angle bisector, distance R / sin(α/2)
        var bis = armA + armB;
        if (!bis.Unitize()) return false;
        var sinHalf = Math.Sin(half);
        if (sinHalf < 1e-8) return false;
        var centerDist = radius / sinHalf;
        var center = corner + bis * centerDist;

        // Midpoint of arc is center + (corner-center).Unitized * radius... 
        // Actually mid of minor arc is center projected toward the chord midpoint from center
        // at distance radius, on the side of the bisector (away from corner for exterior?)
        // For a fillet that CUTS the tip, the arc is between pStart and pEnd
        // with center on the bisector INSIDE the angle (center is between corner and the network).
        // center = corner + bis * (R/sin(half)) — bis points into the angle from corner
        // Arc mid (minor arc) = center - bis * radius  (toward the chord / away from deep angle)
        // Wait: distance center to corner = R/sin(half).
        // Distance center to pStart = R.
        // Point on arc closest to corner is center - bis*R (if bis points from corner through center).
        // The MINOR arc mid (the one cutting the tip) is the point on the arc nearest to corner:
        pMid = center - bis * radius;

        // Sanity: pMid should be between chord and corner
        var midChord = (pStart + pEnd) * 0.5;
        if (pMid.DistanceTo(corner) > midChord.DistanceTo(corner) + tolerance)
        {
            // flipped — use the other side
            pMid = center + bis * radius;
        }

        return true;
    }

    private static bool LineLineIntersection(
        Point3d o1, Vector3d d1, Point3d o2, Vector3d d2, out Point3d intersection)
    {
        intersection = Point3d.Origin;
        // 2D: o1 + t d1 = o2 + s d2
        var denom = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(denom) < 1e-12) return false;
        var dx = o2.X - o1.X;
        var dy = o2.Y - o1.Y;
        var t = (dx * d2.Y - dy * d2.X) / denom;
        intersection = o1 + d1 * t;
        intersection.Z = o1.Z;
        return true;
    }

    public static double ClampRadius(
        double requested, double halfWidthA, double halfWidthB, double turnRadians)
    {
        if (requested <= 0) return 0;
        var turn = Math.Abs(turnRadians);
        if (turn < 1e-3 || turn > Math.PI * 1.9) return 0;

        var minHalf = Math.Max(Math.Min(halfWidthA, halfWidthB), 1e-6);
        var byWidth = minHalf * 2.5;
        var byAngle = minHalf / Math.Max(Math.Sin(Math.Min(turn, Math.PI) * 0.5), 0.12);
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

    public static Curve? FilletExteriorCorners(Curve closed, double radius, double tolerance) => null;
}
