namespace SceneKit;

/// <summary>Per-part proxies, step 4: within mode. The error target e (metres, or pixels at a distance), the two-way
/// deviation from the visible original, and measure-and-retry per part (<see cref="Optimizer.TightenWithin{T}"/>) with
/// the per-part fallback: hull within e, then the decimated original within e, then the original unchanged.</summary>
public static partial class PartProxy
{
    /// <summary>Which triangles of the original can be seen: per piece (<see cref="Assembly.AllPieces"/> order), per
    /// triangle of its T.</summary>
    internal sealed class Visibility
    {
        public bool[][] OfPiece = Array.Empty<bool[]>();
    }

    /// <summary>How often <see cref="VisibleOriginal"/> ran (for tests).</summary>
    internal static int VisibleOriginalCalls;

    /// <summary>How many hulls (voxel or convex) and decimations within mode made, one per attempt (for tests).</summary>
    internal static int WithinHulls, WithinDecimations;

    /// <summary>Called with every voxel hull within mode made (the part and the hull, whose <see cref="Proxy.HullResult.Cell"/>
    /// says the cell used), for tests.</summary>
    internal static Action<Part, Proxy.HullResult>? WithinHullMade;

    /// <summary>One <see cref="Occlusion.Visible"/> run on the whole original object in the target's parent frame: every
    /// piece, the moving ones (every piece below a moving part) with Occludes = false (seen, hiding nothing, as in
    /// remove_occluded), <see cref="Limits.MeasureViews"/> views, from above the horizon only unless
    /// <paramref name="ground"/> is full. <see cref="BuildParts"/> makes it once per object and keeps it for every part
    /// and every attempt.</summary>
    internal static Visibility VisibleOriginal(Assembly asm, ProxyGround ground)
    {
        Interlocked.Increment(ref VisibleOriginalCalls);
        var srcOf = SourceIndex(asm);
        var items = new Occlusion.Item[srcOf.Count];
        foreach (var (a, toParent) in Owners(asm))
            foreach (var pc in a.Pieces) items[srcOf[pc]] = new Occlusion.Item(Place(pc.V, toParent), pc.T, Occludes: toParent == null);
        var vis = Occlusion.Visible(items, new Occlusion.Options { Views = Limits.MeasureViews, AboveHorizonOnly = ground != ProxyGround.Full });
        return new Visibility { OfPiece = vis };
    }

    /// <summary>The deviation of a proxy from the visible original: the larger of the distance from samples of the proxy
    /// to the original (all of it) and from samples of the visible original triangles (<paramref name="origVisible"/>,
    /// one per triangle of <paramref name="origT"/>) to the proxy. Invisible triangles contribute no samples, so a removed
    /// interior is no error and a deleted visible part is. Seeded, so deterministic.</summary>
    internal static double TwoWayVisible(double[] proxyV, int[] proxyT, double[] origV, int[] origT, bool[] origVisible,
        int samples = Limits.MeasureSamples, int seed = 1)
    {
        var visT = new List<int>(origT.Length);
        for (int t = 0; t < origT.Length / 3; t++)
            if (origVisible[t]) { visT.Add(origT[3 * t]); visT.Add(origT[3 * t + 1]); visT.Add(origT[3 * t + 2]); }
        if (proxyT.Length == 0) return visT.Count > 0 ? double.PositiveInfinity : 0;
        double toOriginal = origT.Length == 0 ? double.PositiveInfinity : OneWayDistance(proxyV, proxyT, new TriIndex(origV, origT), samples, seed);
        double toProxy = OneWayDistance(origV, visT.ToArray(), new TriIndex(proxyV, proxyT), samples, seed);
        return Math.Max(toOriginal, toProxy);
    }

    /// <summary>The estimate trigger both modes share: the parts' triangles together are more than a single hull's.</summary>
    internal static bool MoreThanSingleHull(long partTris, long singleHullTris) => partTris > singleHullTris;

    /// <summary>What a within build carries for every part: e in the target's parent frame, the visibility of the
    /// original (one run per object), the source of each piece and the best deviation each part reached.</summary>
    internal sealed class WithinRun
    {
        public double E;
        public Visibility Visible = null!;
        public Dictionary<Piece, int> SrcOf = null!;
        public Dictionary<Part, double> Best = new(ReferenceEqualityComparer.Instance);
        /// <summary>The object's build clock and its budget (<see cref="BuildOptions.WithinBudgetSeconds"/>).</summary>
        public System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();
        public double BudgetSeconds = Limits.WithinBudgetSeconds;
        /// <summary>Set once the budget is spent: no further voxel hull attempt runs.</summary>
        public bool BudgetSpent;

        /// <summary>Whether a voxel hull attempt may still run; the first time the budget is found spent, the note goes to
        /// <paramref name="notes"/>.</summary>
        public bool MayHull(List<string> notes)
        {
            if (BudgetSpent) return false;
            double sec = Watch.Elapsed.TotalSeconds;
            if (sec < BudgetSeconds) return true;
            BudgetSpent = true;
            notes.Add($"time budget reached after {PyFormat.Int((long)Math.Round(sec))} s");
            return false;
        }

        /// <summary>Per triangle of the part (<see cref="Part.T"/>, built unit by unit), whether the original is seen there.</summary>
        public bool[] Mask(Assembly a, Part part)
        {
            var m = new bool[part.T.Length / 3];
            int k = 0;
            foreach (var u in part.Units)
            {
                var of = Visible.OfPiece[SrcOf[a.Pieces[u.Piece]]];
                foreach (int t in u.Tris) m[k++] = of[t];
            }
            return m;
        }
    }

    /// <summary>A part's proxy in within mode, each attempt measured with <see cref="TwoWayVisible"/> in the target's
    /// parent frame (<paramref name="toParent"/>: the owner's frame, null for the target) and repeated with a tighter
    /// error by <see cref="Optimizer.TightenWithin{T}"/>; every attempt makes ONE hull or ONE decimation. First the hull:
    /// a bulk part's voxel hull (<see cref="Proxy.Hull"/> with <see cref="Proxy.Options.MaxError"/> and cells of
    /// <see cref="Limits.WithinCellShare"/> of the attempt's error, so each tighter attempt refines, <paramref name="resolution"/>
    /// (the voxels) the upper limit; when an attempt's <see cref="Proxy.HullResult.Cell"/> is already coarser than e the
    /// cell is the limit, tightening would only re-voxelise at the same cell, so no further hull is made; nor once the
    /// object's time budget is spent, <see cref="WithinRun.MayHull"/>), a small, thin or floating tiny part's convex hull (<see cref="ConvexOnce"/>, made once: it does not
    /// depend on the error). Then, the hull not within e, the decimated original (<see cref="DecimateOnce"/>, the body of
    /// <see cref="BuildSmall"/>'s 3b; not for a tiny part, as in keep mode). With neither, the original as it is (DevM 0).
    /// With a ground plane (<paramref name="g"/> in the owner's frame) every attempt is cut before it is measured; the
    /// best deviation goes to <see cref="WithinRun.Best"/>.</summary>
    static PartResult WithinPart(Assembly a, Part part, Func<IList<Proxy.Source>> sources, WithinRun w, double cell, int resolution,
        GroundPlane? g, double t, double[]? toParent, List<string> notes, Action<string>? progress)
    {
        // e is metres of the target's parent frame; the builders work in the owner's frame
        double ms = toParent == null ? 1 : Mat.MaxScale(toParent);
        double s = ms > 0 && double.IsFinite(ms) ? ms : 1;
        var mask = w.Mask(a, part);
        var origV = Place(part.V, toParent);
        double Measure(PartResult pr) => TwoWayVisible(Place(pr.V, toParent), pr.T, origV, part.T, mask);
        bool wouldEmpty = false;
        PartResult? Cut(PartResult? pr)
        {
            wouldEmpty = false;
            if (pr != null && g is { } gs) wouldEmpty = !CutSmall(pr, gs, t);
            return pr;
        }
        double best = double.PositiveInfinity;
        (PartResult? Result, double BestDev, int Attempts) got;
        // ---------------------------------------------------------------- the hull within e
        bool noMoreHulls = false;
        if (part.Class == PartClass.Bulk)
        {
            var src = sources();
            got = Optimizer.TightenWithin(w.E, me =>
            {
                if (noMoreHulls) return null;   // the cell is the limit: the same cell again would not get closer
                if (!w.MayHull(notes)) return null;   // the object's time budget is spent: the decimated original or the original
                Interlocked.Increment(ref WithinHulls);
                var h = Proxy.Hull(src, new Proxy.Options { MaxError = me / s, CellShare = Limits.WithinCellShare, Resolution = resolution, Progress = progress, Cut = g, CutTolerance = t });
                if (h != null) WithinHullMade?.Invoke(part, h);
                wouldEmpty = h is { CutWouldEmpty: true };
                if (h != null && h.Cell * s > w.E * (1 + Limits.CellSlack)) noMoreHulls = true;
                return h == null ? null : new PartResult { Part = part, Method = PartMethod.Voxel, V = h.V, T = h.T, Cell = h.Cell };
            }, Measure);
        }
        else
            got = Optimizer.TightenWithin(w.E, me =>
            {
                if (noMoreHulls) return null;   // a convex hull does not depend on the error: made once
                noMoreHulls = true;
                Interlocked.Increment(ref WithinHulls);
                return Cut(ConvexOnce(part, me / s, cell));
            }, Measure);
        best = Math.Min(best, got.BestDev);
        // ---------------------------------------------------------------- else the decimated original within e
        if (got.Result == null && part.Class != PartClass.Tiny)
        {
            got = Optimizer.TightenWithin(w.E, me =>
            {
                Interlocked.Increment(ref WithinDecimations);
                return Cut(DecimateOnce(part, me / s, cell));
            }, Measure);
            best = Math.Min(best, got.BestDev);
        }
        w.Best[part] = best;
        if (got.Result is not { } done)
            return new PartResult { Part = part, Method = PartMethod.Kept, V = part.V, T = part.T, Cell = cell, DevM = 0 };
        if (wouldEmpty) notes.Add(UncutNote(part));
        done.DevM = got.BestDev;
        return done;
    }

    /// <summary>The convex hull of a small, thin or floating tiny part, as <see cref="BuildSmall"/>'s 3a makes it (a flat
    /// part gets <see cref="ConvexHull.ThinBox"/>, <paramref name="tol"/> / 4 thick when it has no thickness at all),
    /// unchecked (DevM NaN): within mode measures it. Null when no hull could be made.</summary>
    static PartResult? ConvexOnce(Part part, double tol, double cell)
    {
        if (part.T.Length == 0) return null;
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        Grow(part.V, part.T, lo, hi);
        var ctr = Enumerable.Range(0, 3).Select(c => (lo[c] + hi[c]) / 2).ToArray();
        var V = Shift(part.V, ctr, -1);
        double c0 = Extents(V, part.T).c;
        var hull = c0 < 1e-6 * part.Diag ? ConvexHull.ThinBox(V, c0 > 0 ? c0 : tol / 4) : ConvexHull.Build(V);
        return hull is { } h ? new PartResult { Part = part, Method = PartMethod.Convex, V = Shift(h.V, ctr, 1), T = h.T, DevM = double.NaN, Cell = cell } : null;
    }

    /// <summary>One run of <see cref="Decimate.Run"/> on a part alone (MaxError <paramref name="maxError"/>, a target of 8
    /// triangles, parts not merged, borders sliding; around the part's centre, as <see cref="BuildSmall"/> does). Null
    /// when nothing collapsed.</summary>
    static PartResult? DecimateOnce(Part part, double maxError, double cell)
    {
        if (part.T.Length == 0) return null;
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        Grow(part.V, part.T, lo, hi);
        var ctr = Enumerable.Range(0, 3).Select(c => (lo[c] + hi[c]) / 2).ToArray();
        var red = Decimate.Run(Shift(part.V, ctr, -1), part.T, new[] { 8 }, null, 0,
            new Decimate.Options { MaxError = maxError, MergeParts = false, Borders = BorderMode.Slide })[0];
        if (red.T.Length == 0 || red.T.Length >= part.T.Length) return null;
        return new PartResult { Part = part, Method = PartMethod.Decimated, V = Shift(red.V, ctr, 1), T = red.T, Cell = cell, From = red.Kept };
    }

    /// <summary>Within: every proxy measured again on its final geometry (after the cut and the hidden-triangle removal);
    /// kept parts are the original (0).</summary>
    static void Remeasure(ObjectResult r, WithinRun w)
    {
        foreach (var o in AllResults(r))
        {
            var toParent = ReferenceEquals(o, r) ? null : o.Asm.Frame;
            foreach (var pr in o.Static)
            {
                if (pr.Method == PartMethod.Kept) { pr.DevM = 0; continue; }
                pr.DevM = TwoWayVisible(Place(pr.V, toParent), pr.T, Place(pr.Part.V, toParent), pr.Part.T, w.Mask(o.Asm, pr.Part));
            }
        }
    }

    /// <summary>"hull cells 3.52 cm, coarser than the 1 cm asked".</summary>
    static string CoarseCellNote(double cell, double e) => $"hull cells {ErrorTarget.Distance(cell)}, coarser than the {ErrorTarget.Distance(e)} asked";

    /// <summary>"Within 4 cm: could not stay within it (best 6.1 cm); left unchanged" (without a best when none was measured).</summary>
    static string WithinFailedNote(double e, double best) => double.IsFinite(best)
        ? $"Within {ErrorTarget.Distance(e)}: could not stay within it (best {ErrorTarget.Distance(best)}); left unchanged"
        : $"Within {ErrorTarget.Distance(e)}: could not stay within it; left unchanged";

    /// <summary>"within 4 cm (reached 3.2 cm, max over parts)"; when the final geometry lies farther than e (the cut and the
    /// hidden-triangle removal come after the per-part check), "within 4 cm asked, reached 4.3 cm after the cut (max over parts)".</summary>
    internal static string WithinReachedFragment(double e, double reached) => reached <= e
        ? $"within {ErrorTarget.Distance(e)} (reached {ErrorTarget.Distance(reached)}, max over parts)"
        : $"within {ErrorTarget.Distance(e)} asked, reached {ErrorTarget.Distance(reached)} after the cut (max over parts)";
}
