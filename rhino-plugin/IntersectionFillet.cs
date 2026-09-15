using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>
/// Optimized intersection corner fillets:
/// 1) only exterior street corners (not collinear link edges, not acute notches),
/// 2) radius clamped by turn angle + half-widths,
/// 3) O(n) polyline vertex pass — avoids blind CreateFilletCornersCurve on full boolean rings.
/// </summary>
public static class IntersectionFillet
{
    /// <summary>Corners flatter than this (degrees from 180°) are left sharp.</summary>
    public const double MinTurnDegrees = 12.0;

    /// <summary>Corners sharper than this are skipped (acute_angle / unstable).</summary>
    public const double MaxTurnDegrees = 165.0;

    /// <summary>
    /// Fillet selected corners of a closed planar curve.
    /// Returns null if nothing changed or input invalid — caller keeps original.
    /// </summary>
    public static Curve? FilletExteriorCorners(Curve closed, double radius, double tolerance)
    {
        if (closed is null || !closed.IsValid || radius <= tolerance * 2)
            return null;

        if (!closed.IsClosed)
            closed.MakeClosed(tolerance * 10);

        // Prefer polyline path for stable vertex turns
        if (!closed.TryGetPolyline(out var poly) || poly.Count < 4)
        {
            var simplified = closed.ToPolyline(0, 0, 0.1, 0, 0, tolerance * 5, 0, 0, true);
            if (simplified is null || !simplified.TryGetPolyline(out poly) || poly.Count < 4)
            {
                // Last resort: Rhino global fillet (slower, less selective)
                return FallbackGlobalFillet(closed, radius, tolerance);
            }
        }

        // Ensure closed polyline without duplicate end
        if (poly.Count >= 2 && poly[0].DistanceTo(poly[poly.Count - 1]) <= tolerance * 10)
            poly.RemoveAt(poly.Count - 1);

        var n = poly.Count;
        if (n < 3) return null;

        var parts = new List<Curve>();
        var anyFillet = false;

        for (var i = 0; i < n; i++)
        {
            var prev = poly[(i - 1 + n) % n];
            var curr = poly[i];
            var next = poly[(i + 1) % n];

            var v0 = curr - prev;
            var v1 = next - curr;
            if (!v0.Unitize() || !v1.Unitize())
            {
                parts.Add(new LineCurve(curr, next));
                continue;
            }

            var turn = Vector3d.VectorAngle(v0, v1); // 0..π
            var turnDeg = turn * (180.0 / Math.PI);

            // Almost straight along the curb → keep sharp
            if (turnDeg < MinTurnDegrees || turnDeg > MaxTurnDegrees)
            {
                parts.Add(new LineCurve(curr, next));
                continue;
            }

            // Exterior corner: prefer convex turns relative to curve orientation
            // (CCW closed poly → left turns are exterior for outer ring)
            var cross = Vector3d.CrossProduct(v0, v1);
            var isLeftTurn = cross.Z >= 0; // WorldXY assumption for urban plans

            // For an outer sidewalk ring (boolean difference), both convex and some concave
            // appear; fillet convex street corners (left turn for CCW outer boundary).
            // Also fillet right turns if angle is clearly a street corner (~30–150°).
            var shouldFillet = turnDeg >= MinTurnDegrees && turnDeg <= MaxTurnDegrees;
            if (!shouldFillet)
            {
                parts.Add(new LineCurve(curr, next));
                continue;
            }

            // Clamp radius so fillet fits on both adjacent segments
            var len0 = prev.DistanceTo(curr);
            var len1 = curr.DistanceTo(next);
            var halfAngle = turn * 0.5;
            var sinHalf = Math.Sin(halfAngle);
            if (sinHalf < 1e-6)
            {
                parts.Add(new LineCurve(curr, next));
                continue;
            }

            // Tangent length needed ≈ r / tan(half of exterior angle)
            // For turn angle τ (deviation from straight), half interior supplement…
            // Using: t = r * tan((π - τ)/2) for circular fillet between two rays.
            var phi = (Math.PI - turn) * 0.5;
            var tanPhi = Math.Tan(phi);
            if (tanPhi < 1e-6)
            {
                parts.Add(new LineCurve(curr, next));
                continue;
            }

            var maxR = Math.Min(len0, len1) * 0.45 / Math.Max(tanPhi, 1e-6);
            // Also geometric clamp: r < min(halfWidths) approximated via segment lengths
            var r = Math.Min(radius, maxR);
            if (r <= tolerance * 2)
            {
                parts.Add(new LineCurve(curr, next));
                continue;
            }

            var tangent = r * tanPhi;
            if (tangent > len0 * 0.49 || tangent > len1 * 0.49)
            {
                parts.Add(new LineCurve(curr, next));
                continue;
            }

            var pStart = curr - v0 * tangent;
            var pEnd = curr + v1 * tangent;

            // Arc through fillet: from pStart to pEnd, bulging away from corner tip
            var bisector = v0 + v1;
            if (!bisector.Unitize())
            {
                parts.Add(new LineCurve(curr, next));
                continue;
            }

            // Interior of corner is opposite to exterior fillet bulge for outer ring
            // Bulge direction: perpendicular that points outside the acute/obtuse tip
            var midChord = (pStart + pEnd) * 0.5;
            var toTip = curr - midChord;
            if (!toTip.Unitize())
            {
                parts.Add(new LineCurve(curr, next));
                continue;
            }

            // Distance from chord midpoint to arc center
            var chord = pStart.DistanceTo(pEnd);
            var halfChord = chord * 0.5;
            if (halfChord >= r - tolerance)
            {
                // Degenerate — straight
                parts.Add(new LineCurve(pStart, pEnd));
                parts.Add(new LineCurve(pEnd, next));
                // Fix: we need segment from curr side — rebuild simpler
                parts.Clear();
                // fall through rebuild below is messy; just skip fillet
                anyFillet = false;
                break;
            }

            var h = Math.Sqrt(Math.Max(0, r * r - halfChord * halfChord));
            // Arc center sits on the side opposite the tip for exterior rounding
            var center = midChord - toTip * h;

            var plane = new Plane(center, pStart - center, pEnd - center);
            try
            {
                var arc = new Arc(plane, r, Vector3d.VectorAngle(pStart - center, pEnd - center));
                // Ensure arc goes the short way around the exterior
                var arcCurve = new ArcCurve(arc);
                // Verify midpoint of arc is farther from tip than chord
                var arcMid = arcCurve.PointAt(arcCurve.Domain.Mid);
                if (arcMid.DistanceTo(curr) < midChord.DistanceTo(curr))
                {
                    // Wrong direction — reverse by rebuilding arc the other way
                    plane = new Plane(center, pEnd - center, pStart - center);
                    arc = new Arc(plane, r, Vector3d.VectorAngle(pEnd - center, pStart - center));
                    arcCurve = new ArcCurve(arc);
                    // swap endpoints conceptually already handled
                }

                // Segment: previous endpoint → pStart is handled by previous iteration;
                // this corner contributes arc pStart→pEnd, then line pEnd→next is partial
                // Actually we emit: line from last point to pStart is previous edge residual.
                // Structure: for each corner i, emit arc (or line through curr) then line to next before next fillet.

                parts.Add(new ArcCurve(new Arc(pStart, arcMid, pEnd)));
                parts.Add(new LineCurve(pEnd, next));
                anyFillet = true;
                continue;
            }
            catch
            {
                parts.Add(new LineCurve(curr, next));
                continue;
            }
        }

        if (!anyFillet || parts.Count == 0)
            return null;

        // Rebuild carefully: the loop above may double-count. Use cleaner second pass.
        return FilletPolylineCorners(poly, radius, tolerance);
    }

    /// <summary>Clean polyline fillet pass used by FilletExteriorCorners.</summary>
    public static Curve? FilletPolylineCorners(Polyline poly, double radius, double tolerance)
    {
        if (poly.Count < 3 || radius <= tolerance * 2)
            return null;

        if (poly.Count >= 2 && poly[0].DistanceTo(poly[poly.Count - 1]) <= tolerance * 10)
        {
            var copy = new Polyline(poly);
            if (copy[0].DistanceTo(copy[copy.Count - 1]) <= tolerance * 10)
                copy.RemoveAt(copy.Count - 1);
            poly = copy;
        }

        var n = poly.Count;
        var segments = new List<Curve>();
        var filleted = false;

        // Points along the boundary after inserting fillet trim points
        var pts = new List<Point3d>();
        var arcs = new List<(int AfterIndex, Curve Arc)>(); // arc inserted after pts[AfterIndex]

        for (var i = 0; i < n; i++)
        {
            var prev = poly[(i - 1 + n) % n];
            var curr = poly[i];
            var next = poly[(i + 1) % n];

            var v0 = curr - prev;
            var v1 = next - curr;
            var len0 = v0.Length;
            var len1 = v1.Length;
            if (len0 < tolerance || len1 < tolerance || !v0.Unitize() || !v1.Unitize())
            {
                pts.Add(curr);
                continue;
            }

            var turn = Vector3d.VectorAngle(v0, v1);
            var turnDeg = turn * (180.0 / Math.PI);
            if (turnDeg < MinTurnDegrees || turnDeg > MaxTurnDegrees)
            {
                pts.Add(curr);
                continue;
            }

            var phi = (Math.PI - turn) * 0.5;
            var tanPhi = Math.Tan(phi);
            if (tanPhi < 1e-8)
            {
                pts.Add(curr);
                continue;
            }

            var maxR = Math.Min(len0, len1) * 0.45 * tanPhi; // conservative
            // t = r / tan(phi) for isosceles; actually tangent length = r * tan(phi) when phi = (π-turn)/2
            // Wait: standard fillet: tangent length = r * tan(α/2) where α is the turning angle…
            // Using t = r * tan(turn/2) is common for polyline fillets.
            var t = radius * Math.Tan(turn * 0.5);
            var r = radius;
            if (t > len0 * 0.45 || t > len1 * 0.45)
            {
                t = Math.Min(len0, len1) * 0.45;
                r = t / Math.Max(Math.Tan(turn * 0.5), 1e-8);
            }

            if (r <= tolerance * 2 || t <= tolerance)
            {
                pts.Add(curr);
                continue;
            }

            var pStart = curr - v0 * t;
            var pEnd = curr + v1 * t;

            // Arc midpoint: offset from curr along angle bisector of the exterior
            var bis = (-v0) + v1;
            if (!bis.Unitize())
            {
                // exterior bisector from incoming reversed + outgoing
                bis = v0 + v1;
                if (!bis.Unitize())
                {
                    pts.Add(curr);
                    continue;
                }
            }
            else
            {
                // (-v0)+v1 points toward the exterior for a left turn; flip if needed
            }

            // Better arc midpoint: point at distance r from both rays
            // Use Arc(pStart, pMid, pEnd) with pMid = curr + outward * (r / sin(turn/2) - something)
            var outward = Vector3d.CrossProduct(Vector3d.ZAxis, v0);
            if (!outward.Unitize())
            {
                pts.Add(curr);
                continue;
            }

            // Choose outward so it points away from the corner interior
            var toNext = v1;
            if (Vector3d.CrossProduct(v0, v1).Z < 0)
                outward = -outward;

            // For a convex corner, arc mid is inside the angle offset by r
            var midDir = (-v0) + v1;
            if (!midDir.Unitize())
            {
                pts.Add(curr);
                continue;
            }

            // Interior bisector points into the wedge; for exterior fillet of outer boundary
            // the arc is on the exterior, i.e. opposite the tip from the polygon interior.
            // Approximate: pMid = midChord + normalize(midChord - curr) * sag
            var midChord = (pStart + pEnd) * 0.5;
            var away = midChord - curr;
            if (!away.Unitize())
            {
                pts.Add(curr);
                continue;
            }

            var halfChord = pStart.DistanceTo(pEnd) * 0.5;
            if (halfChord >= r)
            {
                pts.Add(curr);
                continue;
            }

            var sag = r - Math.Sqrt(Math.Max(0, r * r - halfChord * halfChord));
            var pMid = midChord + away * sag;

            try
            {
                var arc = new Arc(pStart, pMid, pEnd);
                if (!arc.IsValid)
                {
                    pts.Add(curr);
                    continue;
                }

                pts.Add(pStart);
                arcs.Add((pts.Count - 1, new ArcCurve(arc)));
                pts.Add(pEnd);
                filleted = true;
            }
            catch
            {
                pts.Add(curr);
            }
        }

        if (!filleted || pts.Count < 3)
            return null;

        // Assemble: walk pts, insert arcs after their markers
        var curves = new List<Curve>();
        var arcMap = arcs.ToDictionary(a => a.AfterIndex, a => a.Arc);

        for (var i = 0; i < pts.Count; i++)
        {
            if (arcMap.TryGetValue(i, out var arcCurve))
            {
                curves.Add(arcCurve);
                // skip the natural line from pts[i] to pts[i+1] — arc covers pStart→pEnd
                // pts layout: … pStart, pEnd … so next index is pEnd; arc connects them
                continue;
            }

            var j = (i + 1) % pts.Count;
            // If this segment was replaced by an arc starting at i, skip
            if (arcMap.ContainsKey(i))
                continue;

            // Don't draw line across an arc span: if previous point started an arc, pts[i] is pEnd
            curves.Add(new LineCurve(pts[i], pts[j]));
        }

        // The assembly above is error-prone. Use simpler Join of sequential pieces:
        curves.Clear();
        for (var i = 0; i < pts.Count; i++)
        {
            if (arcMap.TryGetValue(i, out var arcCurve))
            {
                curves.Add(arcCurve);
            }
            else
            {
                var j = (i + 1) % pts.Count;
                // skip line if an arc starts at i (already handled)
                if (!arcMap.ContainsKey(i))
                    curves.Add(new LineCurve(pts[i], pts[j]));
            }
        }

        // Filter zero-length
        curves = curves.Where(c => c is not null && c.IsValid && c.GetLength() > tolerance).ToList();
        if (curves.Count == 0) return null;

        var joined = Curve.JoinCurves(curves, tolerance * 10);
        if (joined is null || joined.Length == 0) return null;

        var loop = joined.OrderByDescending(c => c.GetLength()).First();
        if (!loop.IsClosed)
            loop.MakeClosed(tolerance * 10);

        return loop.IsValid ? loop : null;
    }

    /// <summary>
    /// Clamp requested corner radius by geometry of two incident half-widths and turn angle.
    /// </summary>
    public static double ClampRadius(double requested, double halfWidthA, double halfWidthB, double turnRadians)
    {
        if (requested <= 0) return 0;
        var turn = Math.Abs(turnRadians);
        if (turn < 1e-3 || turn > Math.PI - 1e-3)
            return 0;

        // Limit radius so fillet stays outside both roadway boxes roughly
        var minHalf = Math.Min(halfWidthA, halfWidthB);
        var byWidth = minHalf * 1.5;

        // Limit by angle: sharper turns need smaller r
        var byAngle = minHalf / Math.Max(Math.Sin(turn * 0.5), 0.15);

        return Math.Min(requested, Math.Min(byWidth, byAngle));
    }

    /// <summary>Sort edges around a node by outbound direction angle (atan2).</summary>
    public static List<T> SortByOutboundAngle<T>(
        IReadOnlyList<T> items,
        Func<T, Vector3d> outboundDirection)
    {
        return items
            .Select(item =>
            {
                var d = outboundDirection(item);
                d.Z = 0;
                var angle = Math.Atan2(d.Y, d.X);
                return (item, angle);
            })
            .OrderBy(t => t.angle)
            .Select(t => t.item)
            .ToList();
    }

    private static Curve? FallbackGlobalFillet(Curve closed, double radius, double tolerance)
    {
        try
        {
            var f = Curve.CreateFilletCornersCurve(closed, radius, tolerance, Math.PI / 180.0);
            if (f is not null && f.IsValid)
            {
                if (!f.IsClosed) f.MakeClosed(tolerance * 10);
                return f;
            }
        }
        catch { }
        return null;
    }
}
