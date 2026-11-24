using SceneKit;
using Xunit;

/// <summary>Skips a test when the meshoptimizer native library could not be loaded.</summary>
public sealed class MeshoptFactAttribute : FactAttribute
{
    public MeshoptFactAttribute() { if (!MeshoptNative.Available) Skip = "meshoptimizer native library not available"; }
}

/// <summary>The native loader for meshoptimizer: loads on Windows x64, raw calls work, a missing library has a reason.</summary>
public class MeshoptTests
{
    [Fact]
    public void LoadsOnWindowsX64()
    {
        if (OperatingSystem.IsWindows() && Environment.Is64BitProcess) Assert.True(MeshoptNative.Available, MeshoptNative.Unavailable);
    }

    [MeshoptFact]
    public void RawSimplifyReachesTarget()
    {
        var (V, T) = OptimizeTests.Sphere(40, 20);
        var pos = V.Select(x => (float)x).ToArray();
        var idx = T.Select(i => (uint)i).ToArray();
        int vertexCount = V.Length / 3;
        int target = idx.Length / 10 / 3 * 3;
        var dst = new uint[idx.Length];
        int n = MeshoptNative.Simplify(dst, idx, pos, vertexCount, target, 1f, 0, out _);
        Assert.True(n > 0 && n <= target, $"{n} indices for target {target}");
        Assert.All(dst.Take(n), i => Assert.True(i < vertexCount));
    }

    [MeshoptFact]
    public void IndexAtVertexCountIsRejected()
    {
        var pos = new float[9];
        var idx = new uint[] { 0, 1, 3 };   // 3 == vertexCount
        var dst = new uint[3];
        Assert.Throws<ArgumentException>(() => MeshoptNative.Simplify(dst, idx, pos, 3, 3, 1f, 0, out _));
    }

    [MeshoptFact]
    public void ScaleIsTheExtent()
    {
        var pos = new List<float>();
        foreach (var x in new[] { 0f, 2f }) foreach (var y in new[] { 0f, 1f }) foreach (var z in new[] { 0f, 1f }) { pos.Add(x); pos.Add(y); pos.Add(z); }
        Assert.Equal(2f, MeshoptNative.SimplifyScale(pos.ToArray(), 8), 1e-6f);
    }

    [Fact]
    public void ForcedUnavailableHasAReason()
    {
        try
        {
            MeshoptNative.ForceUnavailable = true;
            Assert.False(MeshoptNative.Available);
            Assert.Contains("not available", MeshoptNative.Unavailable);
        }
        finally { MeshoptNative.ForceUnavailable = false; }
    }
}

/// <summary>MeshoptBackend.Run: the Decimate-shaped entry point on top of meshoptimizer.</summary>
public class MeshoptBackendTests
{
    static bool IsBorder(double x, double y) => x == 0 || y == 0 || x == 1 || y == 1;

    static HashSet<(double, double, double)> Rows(double[] V)
    {
        var s = new HashSet<(double, double, double)>();
        for (int i = 0; i < V.Length / 3; i++) s.Add((V[3 * i], V[3 * i + 1], V[3 * i + 2]));
        return s;
    }

    static void AssertBorderPresent(double[] V, Simplify.Result r)
    {
        var outPos = new HashSet<(double, double)>();
        for (int i = 0; i < r.V.Length / 3; i++) outPos.Add((r.V[3 * i], r.V[3 * i + 1]));
        for (int i = 0; i < V.Length / 3; i++)
        {
            double x = V[3 * i], y = V[3 * i + 1];
            if (IsBorder(x, y)) Assert.Contains((x, y), outPos);
        }
    }

    [MeshoptFact]
    public void ReachesTargets()
    {
        var (V, T, _) = OptimizeTests.GridWithSeams(16);
        var res = MeshoptBackend.Run(V, T, new[] { 100, 200 }, null, 0, new MeshoptBackend.Options { Borders = BorderMode.Slide });
        Assert.Equal(2, res.Count);
        Assert.InRange(res[0].T.Length / 3, 150, 200);
        Assert.InRange(res[1].T.Length / 3, 60, 100);
    }

    [MeshoptFact]
    public void LocksBorderAndKeepsSeamsClosed()
    {
        var (V, T, UV) = OptimizeTests.GridWithSeams(16);
        var r = MeshoptBackend.Run(V, T, new[] { 100 }, UV, 2,
            new MeshoptBackend.Options { Borders = BorderMode.Lock, AttrWeights = new[] { 1.0, 1.0 } })[0];
        Assert.True(r.T.Length / 3 < T.Length / 3);
        AssertBorderPresent(V, r);
        var q = Quality.Compare(OptimizeTests.Geom(V, T, UV), OptimizeTests.Geom(r.V, r.T, r.A));
        Assert.True(q.BoundaryAfter <= q.BoundaryBefore, $"boundary {q.BoundaryBefore} -> {q.BoundaryAfter}");
        var uvRows = new HashSet<(double, double)>();
        for (int i = 0; i < UV.Length / 2; i++) uvRows.Add((UV[2 * i], UV[2 * i + 1]));
        Assert.Equal(2, r.K);
        for (int i = 0; i < r.A!.Length / 2; i++) Assert.Contains((r.A[2 * i], r.A[2 * i + 1]), uvRows);
    }

    [MeshoptFact]
    public void MatchMeansLockForMeshopt()
    {
        var (V, T, UV) = OptimizeTests.GridWithSeams(16);
        var r = MeshoptBackend.Run(V, T, new[] { 100 }, UV, 2, new MeshoptBackend.Options { AttrWeights = new[] { 1.0, 1.0 } })[0];
        Assert.True(r.T.Length / 3 < T.Length / 3);
        AssertBorderPresent(V, r);
    }

    [MeshoptFact]
    public void RespectsMaxErrorAsAnEstimate()
    {
        var (V, T) = OptimizeTests.Sphere(40, 20);
        var errs = new List<double>();
        var r = MeshoptBackend.Run(V, T, new[] { 10 }, null, 0, new MeshoptBackend.Options { MaxError = 0.01 }, errs)[0];
        Assert.True(r.T.Length / 3 > 10);   // stopped early
        Assert.Single(errs);
        Assert.True(errs[0] <= 0.01 + 1e-6, $"estimate {errs[0]}");
        var q = Quality.Compare(OptimizeTests.Geom(V, T), OptimizeTests.Geom(r.V, r.T));
        Assert.True(q.MaxDev < 0.05, $"max dev {q.MaxDev}");
    }

    static bool[] LockLeft(double[] V)
    {
        var lk = new bool[V.Length / 3];
        for (int i = 0; i < lk.Length; i++) lk[i] = V[3 * i] < 0.3;
        return lk;
    }

    [MeshoptFact]
    public void LockedVerticesStay()
    {
        var (V, T, _) = OptimizeTests.GridWithSeams(24);
        var lk = LockLeft(V);
        var r = MeshoptBackend.Run(V, T, new[] { T.Length / 3 / 10 }, null, 0, new MeshoptBackend.Options { Lock = lk })[0];
        Assert.True(r.T.Length / 3 < T.Length / 3);
        var outRows = Rows(r.V);
        for (int i = 0; i < lk.Length; i++)
            if (lk[i]) Assert.Contains((V[3 * i], V[3 * i + 1], V[3 * i + 2]), outRows);
    }

    [MeshoptFact]
    public void FarFromOriginKeepsExactCoordinates()
    {
        var (V0, T, _) = OptimizeTests.GridWithSeams(16);
        var V = V0.Select((x, i) => i % 3 == 0 ? x + 600000 : i % 3 == 1 ? x + 5300000 : x).ToArray();
        int target = 100;
        var r = MeshoptBackend.Run(V, T, new[] { target }, null, 0, new MeshoptBackend.Options { Borders = BorderMode.Slide })[0];
        Assert.True(r.T.Length / 3 <= 1.1 * target, $"{r.T.Length / 3} triangles");
        var inRows = new HashSet<(long, long, long)>();
        for (int i = 0; i < V.Length / 3; i++)
            inRows.Add((BitConverter.DoubleToInt64Bits(V[3 * i]), BitConverter.DoubleToInt64Bits(V[3 * i + 1]), BitConverter.DoubleToInt64Bits(V[3 * i + 2])));
        for (int i = 0; i < r.V.Length / 3; i++)
            Assert.Contains((BitConverter.DoubleToInt64Bits(r.V[3 * i]), BitConverter.DoubleToInt64Bits(r.V[3 * i + 1]), BitConverter.DoubleToInt64Bits(r.V[3 * i + 2])), inRows);
    }

    [MeshoptFact]
    public void PruneDropsATinySeparatePart()
    {
        var (V1, T1) = OptimizeTests.Sphere(40, 20);
        var (V2, T2) = OptimizeTests.Sphere(8, 4, 0.01);
        int n1 = V1.Length / 3;
        var V = V1.Concat(V2.Select((x, i) => i % 3 == 0 ? x + 5 : x)).ToArray();
        var T = T1.Concat(T2.Select(t => t + n1)).ToArray();
        bool NearTiny(Simplify.Result r)
        {
            for (int i = 0; i < r.V.Length / 3; i++)
                if (Math.Abs(r.V[3 * i] - 5) < 0.1 && Math.Abs(r.V[3 * i + 1]) < 0.1 && Math.Abs(r.V[3 * i + 2]) < 0.1) return true;
            return false;
        }
        var pruned = MeshoptBackend.Run(V, T, new[] { 4 }, null, 0, new MeshoptBackend.Options { MaxError = 0.05, Prune = true })[0];
        var kept = MeshoptBackend.Run(V, T, new[] { 4 }, null, 0, new MeshoptBackend.Options { MaxError = 0.05 })[0];
        Assert.False(NearTiny(pruned));
        Assert.True(NearTiny(kept));
    }

    [MeshoptFact]
    public void PruneKeepsLockedParts()
    {
        var (V1, T1) = OptimizeTests.Sphere(40, 20);
        var (V2, T2) = OptimizeTests.Sphere(8, 4, 0.01);
        int n1 = V1.Length / 3;
        var V = V1.Concat(V2.Select((x, i) => i % 3 == 0 ? x + 5 : x)).ToArray();
        var T = T1.Concat(T2.Select(t => t + n1)).ToArray();
        var lk = Enumerable.Range(0, V.Length / 3).Select(i => i >= n1).ToArray();
        var r = MeshoptBackend.Run(V, T, new[] { 4 }, null, 0,
                                   new MeshoptBackend.Options { MaxError = 0.05, Prune = true, Lock = lk })[0];
        bool nearTiny = false;
        for (int i = 0; i < r.V.Length / 3; i++)
            if (Math.Abs(r.V[3 * i] - 5) < 0.1 && Math.Abs(r.V[3 * i + 1]) < 0.1 && Math.Abs(r.V[3 * i + 2]) < 0.1) nearTiny = true;
        Assert.True(nearTiny);
    }

    [MeshoptFact]
    public void SloppyKeepsLockedVertices()
    {
        var (V, T, _) = OptimizeTests.GridWithSeams(24);
        var lk = LockLeft(V);
        int target = T.Length / 3 / 10;
        // without locks Sloppy reaches the target
        var free = MeshoptBackend.Run(V, T, new[] { target }, null, 0, new MeshoptBackend.Options { Sloppy = true })[0];
        Assert.True(free.T.Length / 3 <= target * 1.1, $"{free.T.Length / 3} triangles for target {target}");
        // with locks, triangles whose three corners are all locked cannot go (336 here, more than the target itself),
        // so the bound is target x 1.1 on top of them
        int allLocked = Enumerable.Range(0, T.Length / 3).Count(t => lk[T[3 * t]] && lk[T[3 * t + 1]] && lk[T[3 * t + 2]]);
        var r = MeshoptBackend.Run(V, T, new[] { target }, null, 0, new MeshoptBackend.Options { Sloppy = true, Lock = lk })[0];
        Assert.True(r.T.Length / 3 <= allLocked + target * 1.1, $"{r.T.Length / 3} triangles for target {target} + {allLocked} all-locked");
        var outRows = Rows(r.V);
        for (int i = 0; i < lk.Length; i++)
            if (lk[i]) Assert.Contains((V[3 * i], V[3 * i + 1], V[3 * i + 2]), outRows);
    }

    [MeshoptFact]
    public void SloppyKeepsBordersUnderLock()
    {
        var (V, T, _) = OptimizeTests.GridWithSeams(24);
        int target = T.Length / 3 / 10;
        int BorderCount(double[] W) => Rows(W).Count(r => IsBorder(r.Item1, r.Item2));
        var locked = MeshoptBackend.Run(V, T, new[] { target }, null, 0,
            new MeshoptBackend.Options { Sloppy = true, Borders = BorderMode.Lock })[0];
        var outRows = Rows(locked.V);
        for (int i = 0; i < V.Length / 3; i++)
            if (IsBorder(V[3 * i], V[3 * i + 1])) Assert.Contains((V[3 * i], V[3 * i + 1], V[3 * i + 2]), outRows);
        var slid = MeshoptBackend.Run(V, T, new[] { target }, null, 0,
            new MeshoptBackend.Options { Sloppy = true, Borders = BorderMode.Slide })[0];
        Assert.True(BorderCount(slid.V) < BorderCount(V), $"{BorderCount(slid.V)} vs {BorderCount(V)}");
    }

    [MeshoptFact]
    public void ManyAttributeChannelsAreCapped()
    {
        var (V, T, _) = OptimizeTests.GridWithSeams(12);
        int n = V.Length / 3, k = 40;
        var rnd = new Random(7);
        var A = Enumerable.Range(0, n * k).Select(_ => rnd.NextDouble()).ToArray();
        var r = MeshoptBackend.Run(V, T, new[] { T.Length / 3 / 4 }, A, k)[0];
        Assert.Equal(k, r.K);
        Assert.Equal(r.V.Length / 3 * k, r.A!.Length);
        for (int i = 0; i < r.Kept.Length; i++)
            Assert.Equal(A.AsSpan(r.Kept[i] * k, k).ToArray(), r.A.AsSpan(i * k, k).ToArray());
    }
}

/// <summary>R3-M4: simplify and generate_lod dispatch to meshoptimizer (method meshopt / sloppy), prune, overlays, and a
/// clear skip when the library is missing.</summary>
public class MeshoptDispatchTests
{
    static (Scene sc, Node geo) GridScene()
    {
        var (V, T, UV) = OptimizeTests.GridWithSeams();
        var (sc, root) = Meshes.SceneOf(("grid", OptimizeTests.Geom(V, T, UV), null));
        return (sc, root.Children[0].Children[0]);
    }

    /// <summary>Negative control: with the library forced away the same run must say so and change nothing, which proves
    /// the dispatch reached the meshopt branch (an unknown method would fall through to qem and succeed).</summary>
    static void AssertDispatchReachesMeshopt(Func<(Scene sc, Node target, Dictionary<string, object> prms)> setup)
    {
        try
        {
            MeshoptNative.ForceUnavailable = true;
            var (sc, target, prms) = setup();
            var before = target.Geom!;
            var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, prms, targets: new List<Node> { target });
            Assert.Null(res[0].Error);
            Assert.Contains("meshoptimizer is not available", res[0].Message);
            Assert.Same(before, target.Geom);
        }
        finally { MeshoptNative.ForceUnavailable = false; }
    }

    [MeshoptFact]
    public void SloppyDispatchKeepsLockedPositions()
    {
        var (V, T, _) = OptimizeTests.GridWithSeams(24);
        var g = OptimizeTests.Geom(V, T);
        var lockedRows = new HashSet<(double, double, double)>();
        var vd = g.Vertices!.Data;
        for (int i = 0; i < vd.Length / 3; i++)
            if (vd[3 * i] < 0.3) lockedRows.Add((vd[3 * i], vd[3 * i + 1], vd[3 * i + 2]));
        Assert.NotEmpty(lockedRows);
        Func<double, double, double, bool> keep = (x, y, z) => x < 0.3;
        var info = new Optimizer.SimplifyInfo();
        var sloppy = Optimizer.SimplifyGeom(g, new[] { 0.1 }, "sloppy", minTris: 4, info: info, keep: keep)[0];
        Assert.True(sloppy != null, info.Reason);
        Assert.NotNull(info.Estimate);
        var outV = sloppy!.Vertices!.Data;
        var outRows = new HashSet<(double, double, double)>();
        for (int i = 0; i < outV.Length / 3; i++) outRows.Add((outV[3 * i], outV[3 * i + 1], outV[3 * i + 2]));
        foreach (var p in lockedRows) Assert.Contains(p, outRows);
        var meshopt = Optimizer.SimplifyGeom(g, new[] { 0.1 }, "meshopt", minTris: 4, keep: keep)[0];
        Assert.NotEqual(meshopt?.Counts().tris, sloppy.Counts().tris);
    }

    [Fact]
    public void GenerateLodNamesAMissingMeshopt()
    {
        try
        {
            MeshoptNative.ForceUnavailable = true;
            var (V, T) = OptimizeTests.Sphere(60, 30, 2.0);
            var (sc, root) = Meshes.SceneOf(("ball", OptimizeTests.Geom(V, T), null));
            var (res, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object>
            {
                ["generate_lod.min_tris"] = 100, ["generate_lod.method"] = "meshopt",
            });
            Assert.Null(res[0].Error);
            Assert.Contains("meshoptimizer is not available", res[0].Message);
            Assert.Empty(sc.IterNodes().Where(n => n.Kind == Kind.Lod).ToList());
        }
        finally { MeshoptNative.ForceUnavailable = false; }
    }

    const string MatchNote = "meshoptimizer: tile edges kept (match needs the standard simplifier)";

    /// <summary>The pass offers the method (an unknown choice would silently run the standard simplifier).</summary>
    static void Offered(string pass, string key, string value) =>
        Assert.Contains(value, Optimizer.Passes[pass].Params.Single(p => p.Key == key).Choices!);

    [MeshoptFact]
    public void SimplifyGeomKeepsSeamsClosedWithMeshopt()
    {
        var (V, T, UV) = OptimizeTests.GridWithSeams();
        var g = OptimizeTests.Geom(V, T, UV);
        var info = new Optimizer.SimplifyInfo();
        var nw = Optimizer.SimplifyGeom(g, new[] { 0.4 }, "meshopt", minTris: 4, seams: "keep", info: info)[0];
        Assert.True(nw != null, info.Reason);
        Assert.NotNull(info.Estimate);   // meshoptimizer's own error estimate: the meshopt branch ran
        var q = Quality.Compare(g, nw);
        Assert.True(q.BoundaryAfter <= q.BoundaryBefore, $"boundary {q.BoundaryBefore} -> {q.BoundaryAfter}");
        Assert.True(q.MaxDevPct < Quality.CheckDev, $"max dev {q.MaxDevPct} %");
    }

    [MeshoptFact]
    public void PermissiveReducesFlatShadedMoreThanKeep()
    {
        var g = PermissiveTests.FacetedGrid();
        long m = g.Counts().tris;
        var info = new Optimizer.SimplifyInfo();
        var keep = Optimizer.SimplifyGeom(g, new[] { 0.1 }, "meshopt", minTris: 4, seams: "keep")[0];
        var perm = Optimizer.SimplifyGeom(g, new[] { 0.1 }, "meshopt", minTris: 4, seams: "permissive", info: info)[0];
        Assert.NotNull(perm);
        Assert.NotNull(info.Estimate);
        long keepTris = keep?.Counts().tris ?? m;
        Assert.True(perm!.Counts().tris < 0.6 * keepTris, $"permissive {perm.Counts().tris}, keep {keepTris} of {m}");
    }

    [MeshoptFact]
    public void PassRunsMeshoptWithin()
    {
        Offered("simplify", "method", "meshopt");
        var (sc, geo) = GridScene();
        var orig = geo.Geom!;
        long t0 = orig.Counts().tris;
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object>
        {
            ["simplify.target"] = "within", ["simplify.max_error"] = 0.01, ["simplify.method"] = "meshopt",
        }, targets: new List<Node> { geo });
        Assert.Null(res[0].Error);
        Assert.True(geo.Geom!.Counts().tris < t0, res[0].Message);
        Assert.Contains(MatchNote, res[0].Message);
        double dev = Quality.Compare(orig, geo.Geom!).MaxDev;
        Assert.True(dev <= 0.01, $"max dev {dev}");
        AssertDispatchReachesMeshopt(() =>
        {
            var (sc2, geo2) = GridScene();
            return (sc2, geo2, new Dictionary<string, object> { ["simplify.target"] = "within", ["simplify.max_error"] = 0.01, ["simplify.method"] = "meshopt" });
        });
    }

    [MeshoptFact]
    public void MeshoptKeepsTerrainUnderTheRoad()
    {
        var (sc, tile, _, _) = Meshes.TileWithRoad();
        var under = OverlayTests.UnderRoad(tile);
        long t0 = tile.Geom!.Counts().tris;
        var prms = OverlayTests.Within(0.5, true);
        prms["simplify.method"] = "meshopt";
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, prms, targets: new List<Node> { tile });
        Assert.Null(res[0].Error);
        Assert.True(tile.Geom!.Counts().tris < t0, res[0].Message);
        Assert.Contains("overlay contact kept on 1 geometry", res[0].Message);
        Assert.Contains(MatchNote, res[0].Message);
        foreach (var p in under) Assert.True(OverlayTests.Present(tile, p), $"vertex {p} under the road is gone");
        AssertDispatchReachesMeshopt(() =>
        {
            var (sc2, tile2, _, _) = Meshes.TileWithRoad();
            var p2 = OverlayTests.Within(0.5, true);
            p2["simplify.method"] = "meshopt";
            return (sc2, tile2, p2);
        });
    }

    [MeshoptFact]
    public void SloppyProtectsOverlaysToo()
    {
        Offered("simplify", "method", "sloppy");
        var (sc, tile, _, _) = Meshes.TileWithRoad();
        var under = OverlayTests.UnderRoad(tile);
        long t0 = tile.Geom!.Counts().tris;
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object>
        {
            ["simplify.target"] = "keep", ["simplify.ratio"] = 0.5, ["simplify.method"] = "sloppy", ["simplify.protect_overlays"] = true,
        }, targets: new List<Node> { tile });
        Assert.Null(res[0].Error);
        Assert.True(tile.Geom!.Counts().tris < t0, res[0].Message);
        Assert.Contains("overlay contact kept", res[0].Message);
        Assert.DoesNotContain(Overlays.FastNote, res[0].Message);
        Assert.DoesNotContain("Fast, rough cannot keep", res[0].Message);
        Assert.Contains(MatchNote, res[0].Message);
        foreach (var p in under) Assert.True(OverlayTests.Present(tile, p), $"vertex {p} under the road is gone");
        AssertDispatchReachesMeshopt(() =>
        {
            var (sc2, tile2, _, _) = Meshes.TileWithRoad();
            return (sc2, tile2, new Dictionary<string, object>
            {
                ["simplify.target"] = "keep", ["simplify.ratio"] = 0.5, ["simplify.method"] = "sloppy", ["simplify.protect_overlays"] = true,
            });
        });
    }

    [MeshoptFact]
    public void TryAndPassAgreeForMeshopt()
    {
        var (sc, tile, _, _) = Meshes.TileWithRoad();
        var orig = tile.Geom!;
        var rep = Analyzer.Analyze(sc);
        var p = new SimplifyParams { Within = true, Error = new ErrorTarget(0.5, true), Engine = SimplifyEngine.Meshopt };
        Assert.Equal("meshopt", p.Method);
        var tr = InspectorModel.TrySimplify(tile, p, InspectorModel.TryContextFor(rep, tile, SoftRender.ParentMatrix(tile, rep.Parents)));
        Assert.NotNull(tr);
        Assert.True(tr!.TrisAfter < tr.TrisBefore);
        Assert.Contains(MatchNote, tr.Quality.Notes);
        Assert.Contains("overlay contact kept", tr.Quality.Notes);
        Assert.Same(orig, tile.Geom);   // the try changed nothing in the scene
        // Apply as previewed: the try's geometries and its parameters, through the pass
        var (targets, prms) = InspectorModel.ApplyArgs(tr, p.ToPassParams());
        Assert.Equal(new[] { tile }, targets);
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, prms, targets: targets);
        Assert.Null(res[0].Error);
        Assert.Contains(MatchNote, res[0].Message);
        Assert.Equal(tr.TrisAfter, tile.Geom!.Counts().tris);
        // negative control: the standard engine gives a different count, so the agreement is the meshopt branch's
        var (sc2, tile2, _, _) = Meshes.TileWithRoad();
        var rep2 = Analyzer.Analyze(sc2);
        var std = InspectorModel.TrySimplify(tile2, p with { Engine = SimplifyEngine.Standard },
            InspectorModel.TryContextFor(rep2, tile2, SoftRender.ParentMatrix(tile2, rep2.Parents)));
        Assert.NotEqual(tr.TrisAfter, std!.TrisAfter);
        Assert.DoesNotContain(MatchNote, std.Quality.Notes);
    }

    [Fact]
    public void UnavailableNativeSkipsWithReason()
    {
        try
        {
            MeshoptNative.ForceUnavailable = true;
            Offered("simplify", "method", "meshopt");
            var (sc, geo) = GridScene();
            var before = geo.Geom!;
            var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object> { ["simplify.method"] = "meshopt" },
                targets: new List<Node> { geo });
            Assert.Null(res[0].Error);
            Assert.Same(before, geo.Geom);
            Assert.Contains("meshoptimizer is not available", res[0].Message);
        }
        finally { MeshoptNative.ForceUnavailable = false; }
    }

    [MeshoptFact]
    public void GenerateLodAcceptsMeshopt()
    {
        Offered("generate_lod", "method", "meshopt");
        var (V, T) = OptimizeTests.Sphere(60, 30, 2.0);
        var (sc, root) = Meshes.SceneOf(("ball", OptimizeTests.Geom(V, T), null));
        long t0 = root.Children[0].Children[0].Geom!.Counts().tris;
        var (res, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object>
        {
            ["generate_lod.min_tris"] = 100, ["generate_lod.method"] = "meshopt",
        });
        Assert.Null(res[0].Error);
        var lod = Assert.Single(sc.IterNodes().Where(n => n.Kind == Kind.Lod).ToList());
        Assert.True(lod.Children.Count >= 2, res[0].Message);
        var levels = lod.Children.Skip(1).Select(c => (c.Kind == Kind.Geode ? c.Children[0].Geom! : c.Geom!).Counts().tris).ToList();
        Assert.All(levels, t => Assert.True(t < t0, $"{t} of {t0}"));
        // negative control: without the library the same run makes no LOD and says why
        try
        {
            MeshoptNative.ForceUnavailable = true;
            var (sc2, _) = Meshes.SceneOf(("ball", OptimizeTests.Geom(V, T), null));
            var (res2, _, _) = Optimizer.Run(sc2, new[] { "generate_lod" }, new Dictionary<string, object>
            {
                ["generate_lod.min_tris"] = 100, ["generate_lod.method"] = "meshopt",
            });
            Assert.Contains("meshoptimizer is not available", res2[0].Message);
            Assert.Empty(sc2.IterNodes().Where(n => n.Kind == Kind.Lod).ToList());
        }
        finally { MeshoptNative.ForceUnavailable = false; }
    }
}
