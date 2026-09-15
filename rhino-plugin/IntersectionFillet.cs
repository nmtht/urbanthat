using Rhino.Geometry;

namespace UrbanBridge.Rhino;

/// <summary>
/// Selective intersection corner fillets — only meaningful exterior turns,
/// radius clamped by segment length and turn angle. O(n) polyline pass.
/// </summary>
public static class IntersectionFillet
{
    /// <summary>Skip nearly collinear vertices (degrees of deflection).</summary>
    public const double MinDeflectionDegrees = 15.0;

    /// <summary>Skip unstable near-180° spikes.</summary>
    public const double MaxDeflectionDegrees = 160.0;

    /// <summary>
    /// Fillet exterior corners of a closed planar curve.
    /// Returns null if no change — caller keeps the original.
    /// </summary>
    public static Curve? FilletExteriorCorners(Curve closed, double radius, double tolerance)
    {
        if (closed is null || !closed.IsValid || radius <= tolerance * 2)
            return null;

        if (!closed.IsClosed)
            closed.MakeClosed(tolerance * 10);

        Polyline poly;
        if (!closed.TryGetPolyline(out poly) || poly.Count < 4)
        {
            var pline = closed.ToPolyline(0, 0, 0.05, 0, 0, Math.Max(tolerance * 5, 0.01), 0, 0, true);
            if (pline is null || !pline.TryGetPolyline(out poly) || poly.Count < 4)
                return FallbackGlobal(closed, radius, tolerance);
        }

        // Drop duplicate closing vertex
        while (poly.Count > 3 && poly[0].DistanceTo(poly[poly.Count - 1]) <= tolerance * 10)
            poly.RemoveAt(poly.Count - 1);

        var n = poly.Count;
        if (n < 3) return null;

        // Build sequence of curves around the loop
        var pieces = new List<Curve>(n * 2);
        var any = false;

        for (var i = 0; i < n; i++)
        {
            var pPrev = poly[(i - 1 + n) % n];
            var pCurr = poly[i];
            var pNext = poly[(i + 1) % n];

            var vIn = pCurr - pPrev;
            var vOut = pNext - pCurr;
            var lenIn = vIn.Length;
            var lenOut = vOut.Length;

            if (lenIn <= tolerance || lenOut <= tolerance || !vIn.Unitize() || !vOut.Unitize())
                continue;

            var deflect = Vector3d.VectorAngle(vIn, vOut); // 0 = straight, π = hairpin
            var deg = deflect * (180.0 / Math.PI);

            if (deg < MinDeflectionDegrees || deg > MaxDeflectionDegrees)
                continue; // handled as plain segment below

            // Tangent length for circular fillet: t = r * tan(deflect/2)
            var half = deflect * 0.5;
            var tanHalf = Math.Tan(half);
            if (tanHalf < 1e-8) continue;

            var r = radius;
            var t = r * tanHalf;
            var maxT = Math.Min(lenIn, lenOut) * 0.45;
            if (t > maxT)
            {
                t = maxT;
                r = t / tanHalf;
            }

            if (r <= tolerance * 2 || t <= tolerance)
                continue;

            var pStart = pCurr - vIn * t;
            var pEnd = pCurr + vOut * t;

            // Arc midpoint on the exterior side of the corner tip
            var midChord = (pStart + pEnd) * 0.5;
            var awayFromTip = midChord - pCurr;
            if (!awayFromTip.Unitize())
                continue;

            var halfChord = pStart.DistanceTo(pEnd) * 0.5;
            if (halfChord >= r - tolerance * 0.5)
                continue;

            var sagitta = r - Math.Sqrt(Math.Max(0.0, r * r - halfChord * halfChord));
            var pMid = midChord + awayFromTip * sagitta;

            Arc arc;
            try
            {
                arc = new Arc(pStart, pMid, pEnd);
                if (!arc.IsValid) continue;
            }
            catch
            {
                continue;
            }

            // Store fillet markers on this vertex index
            // We'll reconstruct in a second structured pass
            any = true;
        }

        if (!any)
            return null;

        // Second pass: emit geometry with fillets applied
        pieces.Clear();
        for (var i = 0; i < n; i++)
        {
            var pPrev = poly[(i - 1 + n) % n];
            var pCurr = poly[i];
            var pNext = poly[(i + 1) % n];

            var vIn = pCurr - pPrev;
            var vOut = pNext - pCurr;
            var lenIn = vIn.Length;
            var lenOut = vOut.Length;

            Point3d segStart; // end of previous fillet or previous vertex
            Point3d segEnd;

            // Compute fillet at current vertex (if any)
            Point3d? filletStart = null;
            Point3d? filletEnd = null;
            ArcCurve? filletArc = null;

            if (lenIn > tolerance && lenOut > tolerance && vIn.Unitize() && vOut.Unitize())
            {
                var deflect = Vector3d.VectorAngle(vIn, vOut);
                var deg = deflect * (180.0 / Math.PI);
                if (deg >= MinDeflectionDegrees && deg <= MaxDeflectionDegrees)
                {
                    var tanHalf = Math.Tan(deflect * 0.5);
                    if (tanHalf >= 1e-8)
                    {
                        var r = radius;
                        var t = r * tanHalf;
                        var maxT = Math.Min(lenIn, lenOut) * 0.45;
                        if (t > maxT)
                        {
                            t = maxT;
                            r = t / tanHalf;
                        }

                        if (r > tolerance * 2 && t > tolerance)
                        {
                            var pStart = pCurr - vIn * t;
                            var pEnd = pCurr + vOut * t;
                            var midChord = (pStart + pEnd) * 0.5;
                            var away = midChord - pCurr;
                            if (away.Unitize())
                            {
                                var halfChord = pStart.DistanceTo(pEnd) * 0.5;
                                if (halfChord < r - tolerance * 0.5)
                                {
                                    var sag = r - Math.Sqrt(Math.Max(0.0, r * r - halfChord * halfChord));
                                    var pMid = midChord + away * sag;
                                    try
                                    {
                                        var arc = new Arc(pStart, pMid, pEnd);
                                        if (arc.IsValid)
                                        {
                                            filletStart = pStart;
                                            filletEnd = pEnd;
                                            filletArc = new ArcCurve(arc);
                                        }
                                    }
                                    catch { /* no fillet */ }
                                }
                            }
                        }
                    }
                }
            }

            // Previous vertex fillet end (or previous poly point) → current fillet start (or curr)
            var prevFillet = ComputeFillet(poly, (i - 1 + n) % n, radius, tolerance);
            var from = prevFillet.End ?? pPrev;
            var to = filletStart ?? pCurr;

            if (from.DistanceTo(to) > tolerance)
                pieces.Add(new LineCurve(from, to));

            if (filletArc is not null)
                pieces.Add(filletArc);
        }

        pieces = pieces.Where(c => c is not null && c.IsValid && c.GetLength() > tolerance).ToList();
        if (pieces.Count == 0) return null;

        var joined = Curve.JoinCurves(pieces, tolerance * 10);
        if (joined is null || joined.Length == 0) return null;

        var loop = joined.OrderByDescending(c => c.GetLength()).First();
        if (!loop.IsClosed)
            loop.MakeClosed(tolerance * 10);

        return loop.IsValid ? loop : null;
    }

    private static (Point3d? Start, Point3d? End, ArcCurve? Arc) ComputeFillet(
        Polyline poly, int i, double radius, double tolerance)
    {
        var n = poly.Count;
        var pPrev = poly[(i - 1 + n) % n];
        var pCurr = poly[i];
        var pNext = poly[(i + 1) % n];

        var vIn = pCurr - pPrev;
        var vOut = pNext - pCurr;
        var lenIn = vIn.Length;
        var lenOut = vOut.Length;
        if (lenIn <= tolerance || lenOut <= tolerance || !vIn.Unitize() || !vOut.Unitize())
            return (null, null, null);

        var deflect = Vector3d.VectorAngle(vIn, vOut);
        var deg = deflect * (180.0 / Math.PI);
        if (deg < MinDeflectionDegrees || deg > MaxDeflectionDegrees)
            return (null, null, null);

        var tanHalf = Math.Tan(deflect * 0.5);
        if (tanHalf < 1e-8) return (null, null, null);

        var r = radius;
        var t = r * tanHalf;
        var maxT = Math.Min(lenIn, lenOut) * 0.45;
        if (t > maxT)
        {
            t = maxT;
            r = t / tanHalf;
        }

        if (r <= tolerance * 2 || t <= tolerance)
            return (null, null, null);

        var pStart = pCurr - vIn * t;
        var pEnd = pCurr + vOut * t;
        var midChord = (pStart + pEnd) * 0.5;
        var away = midChord - pCurr;
        if (!away.Unitize()) return (null, null, null);

        var halfChord = pStart.DistanceTo(pEnd) * 0.5;
        if (halfChord >= r - tolerance * 0.5) return (null, null, null);

        var sag = r - Math.Sqrt(Math.Max(0.0, r * r - halfChord * halfChord));
        var pMid = midChord + away * sag;

        try
        {
            var arc = new Arc(pStart, pMid, pEnd);
            if (!arc.IsValid) return (null, null, null);
            return (pStart, pEnd, new ArcCurve(arc));
        }
        catch
        {
            return (null, null, null);
        }
    }

    /// <summary>Clamp radius by half-widths and turn angle between two incident edges.</summary>
    public static double ClampRadius(double requested, double halfWidthA, double halfWidthB, double turnRadians)
    {
        if (requested <= 0) return 0;
        var turn = Math.Abs(turnRadians);
        if (turn < 1e-3 || turn > Math.PI - 1e-3) return 0;

        var minHalf = Math.Max(Math.Min(halfWidthA, halfWidthB), 1e-6);
        var byWidth = minHalf * 1.25;
        var byAngle = minHalf / Math.Max(Math.Sin(turn * 0.5), 0.2);
        return Math.Min(requested, Math.Min(byWidth, byAngle));
    }

    public static List<T> SortByOutboundAngle<T>(IReadOnlyList<T> items, Func<T, Vector3d> outbound)
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

    private static Curve? FallbackGlobal(Curve closed, double radius, double tolerance)
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
