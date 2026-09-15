using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>
/// Street-corner fillets: arc between outer curb lines of two consecutive roads.
/// Rhino API: CreateFilletCurves(curve0, point0, curve1, point1, radius, ...)
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

            var dirA = a.Outbound;
            var dirB = b.Outbound;
            dirA.Z = 0;
            dirB.Z = 0;
            if (!dirA.Unitize() || !dirB.Unitize()) continue;

            var leftA = Vector3d.CrossProduct(Vector3d.ZAxis, dirA);
            var leftB = Vector3d.CrossProduct(Vector3d.ZAxis, dirB);
            if (!leftA.Unitize() || !leftB.Unitize()) continue;
            var rightA = -leftA;

            var outerA = Math.Max(a.OuterHalf, tolerance * 10);
            var outerB = Math.Max(b.OuterHalf, tolerance * 10);

            var originA = nodePt + rightA * outerA;
            var originB = nodePt + leftB * outerB;

            var lenA = Math.Max(a.ExtLength, outerA + a.Radius + tolerance * 10);
            var lenB = Math.Max(b.ExtLength, outerB + b.Radius + tolerance * 10);

            var lineA = new LineCurve(originA, originA + dirA * lenA);
            var lineB = new LineCurve(originB, originB + dirB * lenB);

            var radius = Math.Min(
                a.Radius > 0 ? a.Radius : b.Radius,
                b.Radius > 0 ? b.Radius : a.Radius);
            if (radius <= tolerance * 2)
                radius = Math.Max(a.Radius, b.Radius);
            if (radius <= tolerance * 2) continue;

            var cross = Vector3d.CrossProduct(dirA, dirB).Z;
            var ccwTurn = Math.Atan2(cross, dirA * dirB);
            if (ccwTurn < 0) ccwTurn += 2 * Math.PI;
            radius = ClampRadius(radius, outerA, outerB, ccwTurn);
            if (radius <= tolerance * 2) continue;

            try
            {
                // Pick points near the node end of each curb (API wants Point3d, not parameter)
                var ptA = lineA.PointAt(lineA.Domain.ParameterAt(0.15));
                var ptB = lineB.PointAt(lineB.Domain.ParameterAt(0.15));

                var fillet = Curve.CreateFilletCurves(
                    lineA, ptA,
                    lineB, ptB,
                    radius,
                    join: false,
                    trim: false,
                    arcExtension: true,
                    tolerance,
                    tolerance);

                if (fillet is null || fillet.Length == 0)
                {
                    ptA = lineA.PointAt(lineA.Domain.ParameterAt(0.35));
                    ptB = lineB.PointAt(lineB.Domain.ParameterAt(0.35));
                    fillet = Curve.CreateFilletCurves(
                        lineA, ptA, lineB, ptB, radius,
                        false, false, true, tolerance, tolerance);
                }

                if (fillet is null) continue;

                foreach (var c in fillet)
                {
                    if (c is null || !c.IsValid) continue;
                    if (c.TryGetArc(out _) || c is ArcCurve)
                        arcs.Add(c);
                    else if (c.GetLength() > tolerance * 10 &&
                             c.GetLength() < (outerA + outerB + radius) * 3)
                        arcs.Add(c);
                }
            }
            catch
            {
                // skip this corner
            }
        }

        return arcs;
    }

    public static Curve? BuildHubOuterLoop(
        Point3d nodePt,
        IReadOnlyList<HubLeg> legsCcw,
        double tolerance)
    {
        var n = legsCcw.Count;
        if (n < 2) return null;

        var pieces = new List<Curve>();

        for (var i = 0; i < n; i++)
        {
            var a = legsCcw[i];
            var b = legsCcw[(i + 1) % n];

            var dirA = a.Outbound; dirA.Z = 0;
            var dirB = b.Outbound; dirB.Z = 0;
            if (!dirA.Unitize() || !dirB.Unitize()) continue;

            var leftA = Vector3d.CrossProduct(Vector3d.ZAxis, dirA);
            var leftB = Vector3d.CrossProduct(Vector3d.ZAxis, dirB);
            if (!leftA.Unitize() || !leftB.Unitize()) continue;
            var rightA = -leftA;

            var outerA = Math.Max(a.OuterHalf, tolerance * 10);
            var outerB = Math.Max(b.OuterHalf, tolerance * 10);
            var originA = nodePt + rightA * outerA;
            var originB = nodePt + leftB * outerB;
            var lenA = Math.Max(a.ExtLength, outerA + a.Radius + 1);
            var lenB = Math.Max(b.ExtLength, outerB + b.Radius + 1);

            var lineA = new LineCurve(originA, originA + dirA * lenA);
            var lineB = new LineCurve(originB, originB + dirB * lenB);

            var radius = Math.Min(
                a.Radius > 0 ? a.Radius : Math.Max(b.Radius, outerA),
                b.Radius > 0 ? b.Radius : Math.Max(a.Radius, outerB));
            var cross = Vector3d.CrossProduct(dirA, dirB).Z;
            var ccwTurn = Math.Atan2(cross, dirA * dirB);
            if (ccwTurn < 0) ccwTurn += 2 * Math.PI;
            radius = ClampRadius(Math.Max(radius, tolerance * 10), outerA, outerB, ccwTurn);

            try
            {
                var ptA = lineA.PointAt(lineA.Domain.ParameterAt(0.2));
                var ptB = lineB.PointAt(lineB.Domain.ParameterAt(0.2));
                var fillet = Curve.CreateFilletCurves(
                    lineA, ptA, lineB, ptB, radius,
                    join: true, trim: true, arcExtension: true,
                    tolerance, tolerance);

                if (fillet is not null && fillet.Length > 0)
                {
                    foreach (var c in fillet)
                    {
                        if (c is not null && c.IsValid)
                            pieces.Add(c);
                    }
                    continue;
                }
            }
            catch { /* fallback below */ }

            pieces.Add(new LineCurve(originA, originA + dirA * lenA * 0.5));
            pieces.Add(new LineCurve(originA + dirA * lenA * 0.5, originB + dirB * lenB * 0.5));
            pieces.Add(new LineCurve(originB + dirB * lenB * 0.5, originB));
        }

        if (pieces.Count == 0) return null;

        var joined = Curve.JoinCurves(pieces, tolerance * 20);
        if (joined is null || joined.Length == 0) return null;

        var loop = joined.OrderByDescending(c => c.GetLength()).First();
        if (!loop.IsClosed)
            loop.MakeClosed(tolerance * 20);

        return loop.IsValid ? loop : null;
    }

    public static double ClampRadius(
        double requested, double halfWidthA, double halfWidthB, double turnRadians)
    {
        if (requested <= 0) return 0;
        var turn = Math.Abs(turnRadians);
        if (turn < 1e-3) return 0;
        if (turn > Math.PI * 1.9) return 0;

        var minHalf = Math.Max(Math.Min(halfWidthA, halfWidthB), 1e-6);
        var byWidth = minHalf * 2.0;
        var byAngle = minHalf / Math.Max(Math.Sin(Math.Min(turn, Math.PI) * 0.5), 0.15);
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
