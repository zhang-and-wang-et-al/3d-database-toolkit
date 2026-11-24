using System.Globalization;
using System.Text;
using SceneKit;
using Xunit;
using Xunit.Abstractions;

/// <summary>Skips unless SCENEKIT_BENCH=1 (no native library needed).</summary>
public sealed class CardBenchFactAttribute : FactAttribute
{
    public CardBenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCENEKIT_BENCH") != "1") Skip = "benchmark: set SCENEKIT_BENCH=1 to run the card layout comparison";
    }
}

/// <summary>R3-C14: for each card layout the largest on-screen height h* (px) at which the cards stay within the pixel error
/// target, per viewing band. Measured, not asserted (layout rows are reported PASS / FAIL); only the coverage-mips row is asserted.
/// Optional SCENEKIT_BENCH_OUT = a file that also receives the Markdown tables.</summary>
public class CardLayoutComparisonTests : IDisposable
{
    readonly ITestOutputHelper output;
    readonly List<string> dirs = new();
    readonly StringBuilder md = new();

    public CardLayoutComparisonTests(ITestOutputHelper output) { this.output = output; }

    string Fresh()
    {
        string d = Path.Combine(Path.GetTempPath(), $"skcb_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        dirs.Add(d);
        return d;
    }

    public void Dispose() { foreach (var d in dirs) { try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }

    void Say(string s = "") { output.WriteLine(s); md.AppendLine(s); }

    static readonly int[] Ladder = { 16, 23, 32, 45, 64, 91, 128, 181, 256, 362, 512, 724, 1024 };
    const int Probe = 30;   // the box row's check height
    const double Target = 6.0;   // px, vegetation
    static readonly string[] Bands = { "ground", "oblique", "above" };
    static readonly double[][] BandElev = { new[] { 0.0, 5, 10 }, new[] { 30.0 }, new[] { 70.0, 80, 90 } };
    static readonly string[] Layouts = { "billboard", "crossed4", "cross2", "star3", "pixyz", "ground", "aerial" };

    // ---- views ---------------------------------------------------------------------------------------------------

    sealed record Vw(Cards.View V, double Az, int Band);

    static List<Vw> Views()
    {
        var list = new List<Vw>();
        for (int b = 0; b < 3; b++)
            foreach (double e in BandElev[b])
                for (int k = 0; k < 24; k++)
                {
                    double a = 15.0 * k * Math.PI / 180, el = e * Math.PI / 180;
                    double ce = Math.Cos(el), se = Math.Sin(el), ca = Math.Cos(a), sa = Math.Sin(a);
                    var d = new[] { ce * ca, ce * sa, -se };
                    var r = new[] { sa, -ca, 0.0 };
                    var u = new[] { r[1] * d[2] - r[2] * d[1], r[2] * d[0] - r[0] * d[2], r[0] * d[1] - r[1] * d[0] };   // Right x Dir
                    list.Add(new Vw(new Cards.View { Dir = d, Right = r, Up = u }, 15.0 * k, b));
                }
        return list;
    }

    // ---- objects -------------------------------------------------------------------------------------------------

    /// <summary>One bake of a layout: the card items (a billboard's per azimuth) with the numbers of the table.</summary>
    sealed class Bake
    {
        public string Layout = "";
        public Func<double, List<Cards.Item>> ItemsAt = _ => new();
        public int Triangles;
        public long TextureBytes;
    }

    static List<Cards.Item> BuildingItems(Node building) =>
        building.Children.Select(n => new Cards.Item
        {
            V = n.Geom!.Vertices!.Data, T = n.Geom.Triangles(), Color = n.StateSet!.Diffuse!.Concat(new[] { 1.0 }).Take(4).ToArray(),
        }).ToList();

    static void Walk(Node n, Action<Node, Node?> f, Node? parent = null)
    {
        f(n, parent);
        foreach (var c in n.Children) Walk(c, f, n);
    }

    static Bake BakeLayout(string layout, Scene sc, Node target, Dictionary<string, object>? more = null)
    {
        var prm = new Dictionary<string, object> { ["replace_cards.layout"] = layout, ["replace_cards.texture"] = 512 };
        if (more != null) foreach (var kv in more) prm[kv.Key] = kv.Value;
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, prm, targets: new List<Node> { target });
        Assert.Null(results[0].Error);
        var geos = new List<(Node geo, Node? parent)>();
        Walk(sc.Root!, (n, p) => { if (n.Kind == Kind.Geometry && n.StateSet?.MaterialName?.EndsWith(" cards") == true) geos.Add((n, p)); });
        Assert.NotEmpty(geos);
        var bake = new Bake { Layout = layout };
        var parts = new List<(double[] v, int[] t, double[] uv, RawImage img)>();
        double[] center = { 0, 0, 0 };
        bool billboard = false;
        foreach (var (geo, parent) in geos)
        {
            var g = geo.Geom!;
            var atlas = geo.StateSet!.Textures.First(t => t.Unit == 0).Baked!;
            var v = (double[])g.Vertices!.Data.Clone();
            if (parent?.Cls == "osg::Billboard" && parent.Center != null)
            {
                billboard = true; center = parent.Center;
                for (int i = 0; i < v.Length; i += 3) for (int c = 0; c < 3; c++) v[i + c] += center[c];
            }
            parts.Add((v, g.Triangles(), g.TexCoords[0].Data, atlas));
            bake.Triangles += g.Triangles().Length / 3;
            foreach (var t in geo.StateSet.Textures)
            {
                if (t.Baked != null) bake.TextureBytes += (long)t.Baked.Width * t.Baked.Height * 4;
                if (t.Companions != null) foreach (var c in t.Companions.Values) if (c.Baked != null) bake.TextureBytes += (long)c.Baked.Width * c.Baked.Height * 4;
            }
        }
        // a billboard card is turned about Z through its centre to face the view (it is drawn facing -y, i.e. for azimuth 90)
        bake.ItemsAt = az =>
        {
            double rot = billboard ? (az - 90) * Math.PI / 180 : 0, c = Math.Cos(rot), s = Math.Sin(rot);
            return parts.Select(p =>
            {
                var v = (double[])p.v.Clone();
                if (billboard)
                    for (int i = 0; i < v.Length; i += 3)
                    {
                        double x = v[i] - center[0], y = v[i + 1] - center[1];
                        v[i] = center[0] + c * x - s * y; v[i + 1] = center[1] + s * x + c * y;
                    }
                return new Cards.Item { V = v, T = p.t, UV = p.uv, Image = p.img };
            }).ToList();
        };
        return bake;
    }

    // ---- measuring -----------------------------------------------------------------------------------------------

    static (double A0, double A1, double B0, double B1) Extent(IList<Cards.Item> items, Cards.View v, double[] ctr)
    {
        double a0 = double.MaxValue, a1 = double.MinValue, b0 = double.MaxValue, b1 = double.MinValue;
        foreach (var it in items)
            foreach (int i in it.T)
            {
                double x = it.V[3 * i] - ctr[0], y = it.V[3 * i + 1] - ctr[1], z = it.V[3 * i + 2] - ctr[2];
                double p = x * v.Right[0] + y * v.Right[1] + z * v.Right[2], q = x * v.Up[0] + y * v.Up[1] + z * v.Up[2];
                a0 = Math.Min(a0, p); a1 = Math.Max(a1, p); b0 = Math.Min(b0, q); b1 = Math.Max(b1, q);
            }
        return (a0, a1, b0, b1);
    }

    /// <summary>Alpha test 0.5 on the snapshot's coverage byte (2 of the 4 samples of a texel = 127).</summary>
    static bool[] Covered(RawImage img)
    {
        var c = new bool[img.Width * img.Height];
        for (int i = 0; i < c.Length; i++) c[i] = img.Pixels[4 * i + 3] >= 127;
        return c;
    }

    /// <summary>(mismatched coverage pixels / silhouette length of the original, mean |dRGB| over pixels covered in both, or NaN).</summary>
    static (double err, double col) Compare(RawImage orig, RawImage cards)
    {
        // both pictures carry a 1 px margin around the object's frame, so the frame border is not special: a pixel outside the
        // picture counts as uncovered (the object's outline pixels on the frame edge are silhouette pixels like any other)
        int w = orig.Width, h = orig.Height;
        var co = Covered(orig); var cc = Covered(cards);
        int mism = 0, sil = 0; double sum = 0; int both = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (co[i] != cc[i]) mism++;
                if (co[i])
                {
                    bool edge = x == 0 || x == w - 1 || y == 0 || y == h - 1 || !co[i - 1] || !co[i + 1] || !co[i - w] || !co[i + w];
                    if (edge) sil++;
                    if (cc[i])
                    {
                        sum += (Math.Abs(orig.Pixels[4 * i] - cards.Pixels[4 * i]) + Math.Abs(orig.Pixels[4 * i + 1] - cards.Pixels[4 * i + 1]) + Math.Abs(orig.Pixels[4 * i + 2] - cards.Pixels[4 * i + 2])) / 3.0;
                        both++;
                    }
                }
            }
        return (sil == 0 ? double.NaN : mism / (double)sil, both == 0 ? double.NaN : sum / both);
    }

    sealed class Row
    {
        public string Layout = "";
        public int[] HStar = new int[3];        // 0 = none on the ladder
        public double[] ErrAt = new double[3], Col = new double[3];
        public double[] Err30 = new double[3];
        public int Tris; public long Bytes;
    }

    /// <summary>Per layout and band: the band mean error and colour difference at every ladder height (+ the probe height).</summary>
    List<Row> Measure(IList<Cards.Item> orig, List<Bake> bakes)
    {
        var views = Views();
        var ctr = Cards.Bounds(orig) is var (lo, hi) ? new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 } : new double[3];
        var sizes = Ladder.Concat(new[] { Probe }).ToArray();   // index Ladder.Length = the probe
        int L = bakes.Count, S = sizes.Length;
        var err = new double[views.Count, L, S]; var col = new double[views.Count, L, S];
        var perAz = bakes.Select(b => Enumerable.Range(0, 24).Select(k => b.ItemsAt(15.0 * k)).ToArray()).ToArray();
        Parallel.For(0, views.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, vi =>
        {
            var vw = views[vi];
            var ex = Extent(orig, vw.V, ctr);
            double aspect = (ex.A1 - ex.A0) / (ex.B1 - ex.B0);
            int k = (int)Math.Round(vw.Az / 15) % 24;
            for (int s = 0; s < S; s++)
            {
                int h = sizes[s], w = Math.Max(1, (int)Math.Round(h * aspect));
                // the on-screen height stays h; the frame is widened by one pixel's world size on every side (w+2 x h+2 picture)
                double pa = (ex.A1 - ex.A0) / w, pb = (ex.B1 - ex.B0) / h;
                var exp = (ex.A0 - pa, ex.A1 + pa, ex.B0 - pb, ex.B1 + pb);
                var o = Cards.Snapshot(orig, vw.V, ctr, exp, w + 2, h + 2);
                for (int l = 0; l < L; l++)
                {
                    var c = Cards.Snapshot(perAz[l][k], vw.V, ctr, exp, w + 2, h + 2);
                    (err[vi, l, s], col[vi, l, s]) = Compare(o, c);
                }
            }
        });
        var rows = new List<Row>();
        for (int l = 0; l < L; l++)
        {
            var r = new Row { Layout = bakes[l].Layout, Tris = bakes[l].Triangles, Bytes = bakes[l].TextureBytes };
            for (int b = 0; b < 3; b++)
            {
                var idx = Enumerable.Range(0, views.Count).Where(i => views[i].Band == b).ToList();
                double E(int s) { var v = idx.Select(i => err[i, l, s]).Where(x => !double.IsNaN(x)).ToList(); return v.Count == 0 ? double.NaN : v.Average(); }
                double C(int s) { var v = idx.Select(i => col[i, l, s]).Where(x => !double.IsNaN(x)).ToList(); return v.Count == 0 ? double.NaN : v.Average(); }
                int pick = -1;
                for (int s = Ladder.Length - 1; s >= 0; s--) if (E(s) <= Target) { pick = s; break; }
                r.HStar[b] = pick < 0 ? 0 : Ladder[pick];
                int at = pick < 0 ? 0 : pick;
                r.ErrAt[b] = E(at); r.Col[b] = C(at); r.Err30[b] = E(Ladder.Length);
            }
            rows.Add(r);
        }
        return rows;
    }

    // ---- verdicts ------------------------------------------------------------------------------------------------

    static double H(Row r, int band) => r.HStar[band] == 0 ? 11 : r.HStar[band];   // none on the ladder: one step under 16

    static readonly int LadderTop = Ladder[^1];

    /// <summary>One condition "layout value >= factor x crossed4 value": ok / no / ceiling (crossed4 at the ladder top, so the
    /// reference is only a lower bound: a layout below factor x bound is "no", one at or above it cannot be judged).</summary>
    static (string res, string txt) Cond(string name, Row r, Row c4, int band, double factor)
    {
        double rv = H(r, band), cv = H(c4, band);
        bool ceil = c4.HStar[band] >= LadderTop;
        bool meets = rv >= factor * cv;
        // a ceiling reference is only a lower bound: a layout already below factor x bound fails whatever the true value is,
        // while one at or above it cannot be judged
        string res = !meets ? "no" : ceil ? "ceiling" : "ok";
        string note = ceil ? (meets ? "crossed4 at ladder top" : "no, below the bound set by crossed4 at ladder top") : res;
        return (res, $"{name} {rv:0} vs {factor:0.##} x {cv:0}: {note}");
    }

    static string Join(params (string res, string txt)[] cs)
    {
        string why = string.Join("; ", cs.Select(c => c.txt));
        if (cs.Any(c => c.res == "no")) return "FAIL (" + why + ")";
        if (cs.Any(c => c.res == "ceiling")) return "inconclusive (ceiling) (" + why + ")";
        return "PASS (" + why + ")";
    }

    static string Verdict(Row r, Row c4, string obj)
    {
        if (r.Layout is "billboard" or "crossed4") return "regression: see CardGoldenTests";
        if (obj == "building") return "n/a (vegetation rules)";
        switch (r.Layout)
        {
            case "aerial": return Join(Cond("above", r, c4, 2, 2), Cond("ground", r, c4, 0, 0.7));
            case "pixyz" or "ground": return Join(Cond("oblique", r, c4, 1, 1.25), Cond("ground", r, c4, 0, 0.9));
            case "cross2" or "star3": return Join(Cond("ground", r, c4, 0, 0.8));
        }
        return "";
    }

    string F(double x, string f = "0.00") => double.IsNaN(x) ? "n/a" : x.ToString(f, CultureInfo.InvariantCulture);

    void Table(string obj, List<Row> rows)
    {
        var c4 = rows.First(r => r.Layout == "crossed4");
        for (int b = 0; b < 3; b++)
        {
            Say($"### {obj}, {Bands[b]} band (target {Target:0} px){(obj == "building" ? ": h* is informational here (vegetation target, no verdicts)" : "")}");
            Say();
            Say("| layout | h* | error at h* | colour diff | triangles | texture bytes | row verdict |");
            Say("|---|---|---|---|---|---|---|");
            foreach (var r in rows)
            {
                string verdict = obj == "building" && r.Layout == "box" && b == 0
                    ? (r.Err30[0] <= 1.0 ? "PASS" : "FAIL") + $" (error at {Probe} px: {F(r.Err30[0])} px, limit 1)"
                    : obj == "building" && r.Layout == "box" ? "n/a (judged in the ground band)"
                    : b == 0 ? Verdict(r, c4, obj) : "(see ground band row)";
                if (b == 0 && r.Layout is "billboard" or "crossed4") verdict = "regression: see CardGoldenTests";
                Say($"| {r.Layout} | {(r.HStar[b] == 0 ? "< 16" : r.HStar[b].ToString())} | {F(r.ErrAt[b])} px{(r.HStar[b] == 0 ? " (at 16)" : "")} | {F(r.Col[b], "0.0")} | {r.Tris} | {r.Bytes:N0} | {verdict} |");
            }
            Say();
        }
    }

    // ---- the bench -----------------------------------------------------------------------------------------------

    [CardBenchFact]
    public void CardLayoutComparison()
    {
        try { Bench(); }
        finally { Flush(); }
    }

    void Flush()
    {
        string? outFile = Environment.GetEnvironmentVariable("SCENEKIT_BENCH_OUT");
        if (!string.IsNullOrEmpty(outFile)) File.WriteAllText(outFile, md.ToString());
    }

    void Bench()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Say("# Card layout bench");
        Say();
        Say($"Views: 24 azimuths x (ground 0/5/10 deg, oblique 30 deg, above 70/80/90 deg); ladder {string.Join(", ", Ladder)}; alpha test 0.5; error target {Target:0} px; all layouts at texture 512.");
        Say();

        // synthetic tree
        {
            var bakes = new List<Bake>();
            foreach (var name in Layouts)
            {
                var (s2, t2) = CardFixtures.TexturedTree(Fresh(), 600, 1);
                bakes.Add(BakeLayout(name, s2, t2));
            }
            var rows = Measure(CardFixtures.Items(600, 1), bakes);
            Table("synthetic tree (600 leaves)", rows);
        }
        // building
        {
            var bakes = new List<Bake>();
            foreach (var name in Layouts.Concat(new[] { "box" }))
            {
                var (s2, b2) = CardFixtures.Building();
                bakes.Add(BakeLayout(name, s2, b2));
            }
            var (_, b0) = CardFixtures.Building();
            var rows = Measure(BuildingItems(b0), bakes);
            Table("building", rows);
        }
        // coverage mips on the sparse tree
        {
            Say("### coverage mips, sparse tree (150 leaves), share of leaf area kept at 1/2, 1/4, 1/8 (limit 0.90 to 1.10)");
            Say();
            Say("| layout | kept 1/2 | kept 1/4 | kept 1/8 | box 1/2 | box 1/4 | box 1/8 | row verdict |");
            Say("|---|---|---|---|---|---|---|---|");
            var failures = new List<string>();
            foreach (var name in new[] { "crossed4", "ground" })
            {
                var (sc, tree) = CardFixtures.TexturedTree(Fresh(), 150, 1);
                Texture? tex = null;
                var prm = new Dictionary<string, object> { ["replace_cards.layout"] = name, ["replace_cards.texture"] = 512, ["replace_cards.alpha_mips"] = "coverage" };
                var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, prm, targets: new List<Node> { tree });
                Assert.Null(results[0].Error);
                Walk(sc.Root!, (n, p) => { if (n.Kind == Kind.Geometry && n.StateSet?.MaterialName?.EndsWith(" cards") == true) tex ??= n.StateSet.Textures.First(t => t.Unit == 0); });
                Assert.NotNull(tex?.Baked);
                var (box, kept) = AlphaMips.Measure(tex!.Baked!, tex.MipCells);
                bool ok = kept.All(k => k >= 0.9 && k <= 1.1);
                if (!ok) failures.Add($"{name}: kept {string.Join(", ", kept.Select(k => F(k, "0.000")))}");
                Say($"| {name} | {F(kept[0], "0.000")} | {F(kept[1], "0.000")} | {F(kept[2], "0.000")} | {F(box[0], "0.000")} | {F(box[1], "0.000")} | {F(box[2], "0.000")} | {(ok ? "PASS" : "FAIL")} |");
            }
            Say();
            Say($"Run time {sw.Elapsed.TotalSeconds:0} s.");
            Flush();
            Assert.True(failures.Count == 0, "coverage mips outside +-10 %: " + string.Join("; ", failures));
        }
    }
}
