namespace SceneKit;

/// <summary>Per-part proxies, step 3 for small, thin and floating tiny parts: a convex hull (3a) or the decimated original
/// (3b), whichever is cheaper within the tolerance, else the original part as it is.</summary>
public static partial class PartProxy
{
    /// <summary>The largest distance from area samples of <paramref name="fromV"/> / <paramref name="fromT"/> (and every
    /// vertex they use; <see cref="Quality.Sample"/>, seeded, so the result is deterministic) to the triangles of
    /// <paramref name="to"/>. Infinity when <paramref name="to"/> has no triangles, 0 when the mesh has none.</summary>
    internal static double OneWayDistance(double[] fromV, int[] fromT, TriIndex to, int samples, int seed = 1)
    {
        if (fromT.Length == 0) return 0;
        if (to.Count == 0) return double.PositiveInfinity;
        var (P, _) = Quality.Sample(fromV, fromT, samples, new Random(seed));
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        Grow(fromV, fromT, lo, hi);
        double reach = Math.Max(Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (hi[c] - lo[c]) * (hi[c] - lo[c]))), 1e-9);
        int n = P.Length / 3;
        var d = new double[n];
        Parallel.For(0, n, i =>
        {
            // search within the mesh's own size first, then farther out until something is found
            double r = reach;
            TriIndex.Hit? h = null;
            for (int k = 0; k < 64 && h == null; k++, r *= 4) h = to.Closest(P[3 * i], P[3 * i + 1], P[3 * i + 2], r);
            d[i] = h == null ? double.PositiveInfinity : Math.Sqrt(h.Value.D2);
        });
        return d.Max();
    }

    /// <summary>The proxy of a small, thin or floating tiny part, with tol = e (keep mode, e = 0: 2 cells). (3a) The convex
    /// hull of its vertices (<see cref="ConvexHull.Build"/>; a flat part, smallest PCA extent c below 1e-6 x its diagonal,
    /// gets <see cref="ConvexHull.ThinBox"/> c thick, or tol / 4 when c is 0), accepted when the largest distance from it
    /// to the original is at most tol. (3b) <see cref="Decimate.Run"/> on the part alone (MaxError tol, parts not merged,
    /// borders sliding, a target of 8 triangles), accepted when it has fewer triangles than the original and lies within
    /// tol of it both ways (measured; tightened and repeated as <see cref="Optimizer.SimplifyWithin"/> does, at most
    /// <see cref="Limits.MaxDecimateAttempts"/> runs). The cheaper accepted candidate wins, counting draw calls before triangles: the hull goes into the atlas, a
    /// decimated part adds a draw call, so an accepted hull wins (and wins a tie) and 3b is not tried; a null hull goes
    /// straight to 3b. With neither, the part is kept as it is. A tiny part (Classify leaves only the floating ones) gets
    /// the hull without a check and no 3b. Built around the part's centre, so far from the origin keeps its precision.
    /// <see cref="PartResult.DevM"/> is the measured distance (convex: hull to original; decimated: the larger of both ways);
    /// NaN for a tiny part's hull, which is not checked, so it does not feed the report's ReachedM.</summary>
    internal static PartResult BuildSmall(Part part, double e, double cell)
    {
        double tol = e > 0 ? e : 2 * cell;
        var kept = new PartResult { Part = part, Method = PartMethod.Kept, V = part.V, T = part.T, Cell = cell };
        if (part.T.Length == 0) return kept;
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        Grow(part.V, part.T, lo, hi);
        var ctr = Enumerable.Range(0, 3).Select(c => (lo[c] + hi[c]) / 2).ToArray();
        var V = Shift(part.V, ctr, -1);
        var T = part.T;

        // ---------------------------------------------------------------- (3a) convex hull
        double c0 = Extents(V, T).c;
        var hull = c0 < 1e-6 * part.Diag ? ConvexHull.ThinBox(V, c0 > 0 ? c0 : tol / 4) : ConvexHull.Build(V);
        var original = new TriIndex(V, T);
        if (hull is { } h)
        {
            bool tiny = part.Class == PartClass.Tiny;
            double dev = tiny ? double.NaN : OneWayDistance(h.V, h.T, original, Limits.MeasureSamples);
            if (tiny || dev <= tol)
                // cheaper: draw calls first, then triangles. A decimated part keeps its own texture and adds a draw call
                // per piece, the hull goes into the atlas and adds none, so an accepted hull always wins (and every tie)
                return new PartResult { Part = part, Method = PartMethod.Convex, V = Shift(h.V, ctr, 1), T = h.T, DevM = dev, Cell = cell };
        }
        if (part.Class == PartClass.Tiny) return kept;

        // ---------------------------------------------------------------- (3b) decimated original
        // MaxError is a one-sided collapse bound, so the result is measured and, as Optimizer.SimplifyWithin does, the run
        // repeated with maxError x Limits.DecimateTighten x tol / deviation while it is too far, at most Limits.MaxDecimateAttempts
        double maxError = tol;
        for (int attempt = 0; attempt < Limits.MaxDecimateAttempts; attempt++)
        {
            var red = Decimate.Run(V, T, new[] { 8 }, null, 0, new Decimate.Options { MaxError = maxError, MergeParts = false, Borders = BorderMode.Slide })[0];
            if (red.T.Length == 0 || red.T.Length >= T.Length) break;   // nothing collapsed: the original it is
            double dev = Math.Max(OneWayDistance(red.V, red.T, original, Limits.MeasureSamples),
                OneWayDistance(V, T, new TriIndex(red.V, red.T), Limits.MeasureSamples));
            if (!double.IsFinite(dev)) break;
            if (dev <= tol)
                return new PartResult { Part = part, Method = PartMethod.Decimated, V = Shift(red.V, ctr, 1), T = red.T, DevM = dev, Cell = cell, From = red.Kept };
            maxError *= Limits.DecimateTighten * tol / dev;
        }
        return kept;
    }

    /// <summary>Positions plus <paramref name="sign"/> x <paramref name="by"/>.</summary>
    static double[] Shift(double[] V, double[] by, int sign)
    {
        var o = new double[V.Length];
        for (int i = 0; i < V.Length; i++) o[i] = V[i] + sign * by[i % 3];
        return o;
    }
}
