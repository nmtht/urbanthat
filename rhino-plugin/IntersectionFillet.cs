using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>
/// Pairwise outer-curb fillets at road hubs (geometric, no CreateFilletCurves).
/// Legs sorted CCW; each consecutive pair builds one corner arc on the outer curb wedge.
/// </summary>
public static class IntersectionFillet
{
    public sealed class HubLeg
    {
        public Vector3d Outbound;
        public double OuterHalf;
        public double RoadHalf;
        public double Radius;
        public double ExtLength;
    }

    public static List<T> SortByOutboundAngle<T>(IList<T> items, Func<T, Vector3d> getDir)
    {
        var list = items.ToList();
        list.Sort((x, y) =>
        {
            var dx = getDir(x); dx.Z = 0;
            var dy = getDir(y); dy.Z = 0;
            if (!dx.Unitize()) return -1;
            if (!dy.Unitize()) return 1;
            var ax = Math.Atan2(dx.Y, dx.X);
            var ay = Math.Atan2(dy.Y, dy.X);
            return ax.CompareTo(ay);
        });
        return list;
    }

    public static double ClampRadius(double requested, double halfA, double halfB, double turnRadians)
    {
        if (requested <= 0) return 0;
        var turn = Math.Abs(turnRadians);
        if (turn < 1e-6 || turn > Math.PI - 1e-6) return requested;
        // Limit radius so tangent points stay within extension of both legs
        var sinHalf = Math.Sin(turn * 0.5);
        if (sinHalf < 1e-6) return requested;
        var maxByAngle = Math.Min(halfA, halfB) / Math.Max(sinHalf, 0.1);
        return Math.Min(requested, maxByAngle * 2.0);
    }

    public static IEnumerable<Curve> BuildPairwiseOuterArcs(
        Point3d nodePt, IList<HubLeg> legs, double tolerance)
    {
        if (legs.Count < 2) yield break;
        for (var i = 0; i < legs.Count; i++)
        {
            var a = legs[i];
            var b = legs[(i + 1) % legs.Count];
            if (!TryCornerGeometry(nodePt, a, b, useOuter: true, tolerance,
                    out var pStart, out var pEnd, out var pMid, out _))
                continue;
            Arc arc;
            try { arc = new Arc(pStart, pMid, pEnd); }
            catch { continue; }
            if (!arc.IsValid || arc.Length < tolerance * 5) continue;
            yield return new ArcCurve(arc);
        }
    }

    public static IEnumerable<Curve> BuildPairwiseCornerPads(
        Point3d nodePt, IList<HubLeg> legs, bool useOuter, double tolerance)
    {
        if (legs.Count < 2) yield break;
        for (var i = 0; i < legs.Count; i++)
        {
            var a = legs[i];
            var b = legs[(i + 1) % legs.Count];
            if (!TryCornerGeometry(nodePt, a, b, useOuter, tolerance,
                    out var pStart, out var pEnd, out var pMid, out var radius))
                continue;

            // Closed pad: tangent points + arc + back along virtual corner
            Arc arc;
            try { arc = new Arc(pStart, pMid, pEnd); }
            catch { continue; }
            if (!arc.IsValid) continue;

            // Virtual corner for the pad tip
            if (!TryVirtualCorner(nodePt, a, b, useOuter, tolerance, out var corner))
                corner = pMid; // fallback

            var parts = new List<Curve>
            {
                new ArcCurve(arc),
                new LineCurve(pEnd, corner),
                new LineCurve(corner, pStart),
            };
            var joined = Curve.JoinCurves(parts, tolerance * 10);
            if (joined is null || joined.Length == 0) continue;
            var loop = joined[0];
            if (!loop.IsClosed) loop.MakeClosed(tolerance * 10);
            if (loop.IsValid && loop.IsClosed)
                yield return loop;
        }
    }

    private static bool TryVirtualCorner(
        Point3d nodePt, HubLeg a, HubLeg b, bool useOuter, double tolerance, out Point3d corner)
    {
        corner = Point3d.Origin;
        return TryCornerGeometry(nodePt, a, b, useOuter, tolerance,
            out _, out _, out _, out _) &&
            LineLineIntersection(
                GetOrigin(nodePt, a, useOuter, true, tolerance),
                Flatten(a.Outbound),
                GetOrigin(nodePt, b, useOuter, false, tolerance),
                Flatten(b.Outbound),
                out corner);
    }

    private static Point3d GetOrigin(Point3d nodePt, HubLeg leg, bool useOuter, bool isA, double tolerance)
    {
        var dir = Flatten(leg.Outbound);
        if (!dir.Unitize()) return nodePt;
        var left = Vector3d.CrossProduct(Vector3d.ZAxis, dir);
        left.Unitize();
        var half = useOuter ? leg.OuterHalf : leg.RoadHalf;
        half = Math.Max(half, tolerance * 10);
        // A uses left, B uses right (see TryCornerGeometry)
        var side = isA ? left : -left;
        return nodePt + side * half;
    }

    private static Vector3d Flatten(Vector3d v)
    {
        v.Z = 0;
        return v;
    }

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
        var rightB = -leftB;

        var halfA = useOuter ? a.OuterHalf : a.RoadHalf;
        var halfB = useOuter ? b.OuterHalf : b.RoadHalf;
        halfA = Math.Max(halfA, tolerance * 10);
        halfB = Math.Max(halfB, tolerance * 10);

        // legsCcw are sorted CCW (SortByOutboundAngle), so for a consecutive pair (a, b)
        // the wedge to fillet lies between a's LEFT curb and b's RIGHT curb —
        // e.g. East road (a) + North road (b) CCW-adjacent -> NE corner needs
        // East's north (left) curb + North's east (right) curb, not the mirrored SW pair.
        var originA = nodePt + leftA * halfA;
        var originB = nodePt + rightB * halfB;

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

        // Distance from curb origin along outbound to tangent point
        // For equal half-widths: arm = radius / tan(turn/2)
        // General: offset from virtual corner along each curb by radius * tan((pi-turn)/2) wait
        // Standard fillet: from virtual corner, go back along each ray by radius / tan(halfAngle)
        var halfTurn = ccwTurn * 0.5;
        var tanHalf = Math.Tan(halfTurn);
        if (Math.Abs(tanHalf) < 1e-9) return false;
        var arm = radius / tanHalf;

        // Tangent points: from virtual corner, walk back along each curb direction (toward node side is -dir for outbound curbs)
        // Curb lines run along dirA / dirB; virtual corner is typically OUT beyond the node for convex outer corners.
        // Walk from corner toward the node along -dir.
        pStart = corner - dirA * arm;
        pEnd = corner - dirB * arm;

        // Arc midpoint on the angle bisector, radius away from corner
        var bisector = dirA + dirB;
        if (!bisector.Unitize())
        {
            // 180 deg degenerate
            bisector = leftA;
            if (!bisector.Unitize()) return false;
        }
        // For outer (convex) corner the arc sits on the side opposite the road centers:
        // from corner, move along the outward normal of the turn (bisector rotated?)
        // Virtual corner is outside; arc bows toward the roads = toward node roughly.
        var toNode = nodePt - corner;
        toNode.Z = 0;
        if (!toNode.Unitize()) toNode = -bisector;
        pMid = corner + toNode * radius;

        // Sanity: arc length roughly radius * turn
        var chord = pStart.DistanceTo(pEnd);
        if (chord < tolerance * 5) return false;
        return true;
    }

    private static bool LineLineIntersection(
        Point3d p1, Vector3d d1, Point3d p2, Vector3d d2, out Point3d result)
    {
        result = Point3d.Origin;
        d1.Z = 0; d2.Z = 0;
        if (!d1.Unitize() || !d2.Unitize()) return false;

        // 2D line-line: p1 + s*d1 = p2 + t*d2
        var denom = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(denom) < 1e-12) return false;

        var dx = p2.X - p1.X;
        var dy = p2.Y - p1.Y;
        var s = (dx * d2.Y - dy * d2.X) / denom;
        result = p1 + d1 * s;
        result.Z = p1.Z;
        return true;
    }
}
