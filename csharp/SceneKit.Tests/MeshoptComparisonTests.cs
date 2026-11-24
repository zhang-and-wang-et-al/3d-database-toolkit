using System.Diagnostics;
using System.Globalization;
using System.Text;
using SceneKit;
using Xunit;
using Xunit.Abstractions;

/// <summary>Runs only with SCENEKIT_BENCH=1 (and the meshoptimizer library present).</summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCENEKIT_BENCH") != "1") Skip = "benchmark: set SCENEKIT_BENCH=1 to run the meshopt vs qem comparison";
        else if (!MeshoptNative.Available) Skip = "benchmark: meshoptimizer native library not available";
    }
}

/// <summary>R3-M8: meshopt vs qem on the test meshes, measured, not asserted. One Markdown table per mesh: per target
/// (Keep 10 %, Keep 2 %, Within 0.5 % of size) and variant the triangles reached, Quality.Compare max / mean deviation,
/// open-edge change, texture shift, meshopt's own estimate and the run time.</summary>
public class MeshoptComparisonTests
{
    readonly ITestOutputHelper output;
    public MeshoptComparisonTests(ITestOutputHelper output) => this.output = output;

    sealed record Variant(string Label, string Method, string Seams, bool Prune = false, bool PermissiveFlag = false,
                          (double Uv, double Normals)? Weights = null, bool NoCap = false);

    static readonly Variant[] Variants =
    {
        new("qem keep", "qem", "keep"),
        new("qem permissive", "qem", "permissive"),
        new("meshopt keep", "meshopt", "keep"),
        new("meshopt permissive", "meshopt", "permissive"),
        new("meshopt keep + Permissive flag", "meshopt", "keep", PermissiveFlag: true),
        new("meshopt keep + prune", "meshopt", "keep", Prune: true),
        new("meshopt keep, D9 weights (UV 30, N 1)", "meshopt", "keep", Weights: (30, 1)),
        new("meshopt keep, no cap", "meshopt", "keep", NoCap: true),
        new("fast", "fast", "keep"),
        new("sloppy", "sloppy", "keep"),
    };

    static readonly (string Label, double Ratio, double WithinPct)[] Targets = { ("Keep 10 %", 0.1, 0), ("Keep 2 %", 0.02, 0), ("Within 0.5 %", 0, 0.5) };

    [BenchFact]
    public void MeshoptComparison()
    {
        var ss = new StateSet { Textures = { new Texture { File = "t.png", Width = 1024, Height = 1024, Unit = 0, Exists = true } } };
        {
            var (V, T, UV) = OptimizeTests.GridWithSeams(24);
            Table("GridWithSeams(24), UVs with seams", OptimizeTests.Geom(V, T, UV), ss, null);
        }
        Table("PermissiveTests.Cube(8), hard edges, normals + UVs", PermissiveTests.Cube(8), ss, null);
        {
            var (V, T) = OptimizeTests.Sphere(80, 40);
            Table("Sphere(80, 40), no attributes", OptimizeTests.Geom(V, T), null, null);
        }
        {
            var (sc, tile, _, _) = Meshes.TileWithRoad();
            var placed = SoftRender.GeometryItems(sc.Root!).Where(x => x.node.Geom is { } cg && cg.Counts().tris > 0)
                .Select(x => new Overlays.Placed(x.node, x.world)).ToList();
            var keep = Overlays.KeepFor(tile.Geom!, new[] { Mat.Identity() }, placed, out int found);
            Table($"TileWithRoad tile, UVs, terrain under the road kept ({found} overlay)", tile.Geom!, ss, keep);
        }
        Assert.True(true, "ran");
    }

    void Table(string title, GeomData g, StateSet? ss, Func<double, double, double, bool>? keep)
    {
        long m = g.Counts().tris;
        double diag = Optimizer.Diagonal(g.BBox());
        var sb = new StringBuilder();
        sb.AppendLine($"### {title}: {m} triangles, diagonal {F(diag, 3)}");
        sb.AppendLine();
        sb.AppendLine("| Target | Variant | Tris | MaxDev % | Mean % | Δ open edges | Tex shift mean / p95 (px) | Estimate (% diag) | ms |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (tLabel, ratio, withinPct) in Targets)
            foreach (var v in Variants)
            {
                // fast keeps no vertices (the pass drops the overlay lock for it as well)
                var k = v.Method == "fast" ? null : keep;
                if (withinPct > 0 && v.NoCap) continue;   // Within sets its own limit: no cap means nothing there
                if (withinPct > 0 && v.Method is "fast" or "sloppy")
                {
                    sb.AppendLine($"| {tLabel} | {v.Label} | n/a (no error limit) | | | | | | |");
                    continue;
                }
                var sw = Stopwatch.StartNew();
                Optimizer.SimplifyAttempt Run(double me)
                {
                    var inf = new Optimizer.SimplifyInfo { MeshoptPermissive = v.PermissiveFlag, Weights = v.Weights, MeshoptMaxRelativeError = v.NoCap ? 0 : null };
                    var r = Optimizer.SimplifyGeom(g, new[] { ratio }, v.Method, minTris: 4, seams: v.Seams, info: inf, maxError: me,
                        keep: k, prune: v.Prune)[0];
                    return new(r, null, inf);
                }
                var at = withinPct > 0 ? Optimizer.SimplifyWithin(g, withinPct / 100.0 * diag, 1.0, Run) : Run(0);
                sw.Stop();
                string ms = sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture);
                if (at.Geom == null)
                {
                    string dev = at.Info.WithinDev is double d ? $"best {F(100 * d / diag, 3)}" : "";
                    sb.AppendLine($"| {tLabel} | {v.Label} | stalled: {at.Info.Reason} | {dev} | | | | | {ms} |");
                    continue;
                }
                var q = Quality.Compare(g, at.Geom);
                string tex = "";
                if (ss != null)
                {
                    Quality.CompareTextured(q, g, at.Geom, ss, v.Seams);
                    tex = q.TexShiftMean is double tm ? $"{F(tm, 2)} / {F(q.TexShiftP95 ?? 0, 2)}" : q.TextureNote ?? "";
                }
                string est = at.Info.Estimate is double e ? F(100 * e / diag, 3) : "";
                sb.AppendLine($"| {tLabel} | {v.Label} | {at.Geom.Counts().tris} | {F(q.MaxDevPct, 3)} | {F(q.MeanDevPct, 4)} | " +
                              $"{q.BoundaryAfter - q.BoundaryBefore:+0;-0;0} | {tex} | {est} | {ms} |");
            }
        output.WriteLine(sb.ToString());
    }

    static string F(double x, int d) => x.ToString("F" + d, CultureInfo.InvariantCulture);
}
