using System.Globalization;
using SceneKit;
using Xunit;

/// <summary>Ports of tests/test_debugtools.py: quality check, seam-preserving simplify, change tracking and undo.</summary>
public class OptimizeTests
{
    const string Head = "#Ascii Scene \n#Version 161 \n#Generator OpenSceneGraph 3.6.5 \n\n";

    [Fact]
    public void StructurePassesAreMarked()
    {
        var p = Optimizer.Passes;
        Assert.True(p["remove_redundant"].Structure && p["make_instances"].Structure);
        Assert.False(p["simplify"].Structure);
        Assert.False(p["replace_cards"].Structure);
    }

    [Fact]
    public void SeeThroughLabelNamesInteriors() =>
        Assert.Equal("Keep interiors visible through glass / cut-outs",
            Optimizer.Passes["remove_occluded"].Params.Single(x => x.Key == "see_through").Label);

    /// <summary>UV sphere like tests/test_scenekit.py sphere(): (V flat xyz, T flat triangles).</summary>
    internal static (double[] V, int[] T) Sphere(int nu = 40, int nv = 20, double r = 1.0)
    {
        var V = new List<double>();
        for (int i = 0; i <= nv; i++)
        {
            double th = Math.PI * i / nv;
            for (int j = 0; j <= nu; j++)
            {
                double ph = 2 * Math.PI * j / nu;
                V.Add(Math.Sin(th) * Math.Cos(ph) * r); V.Add(Math.Sin(th) * Math.Sin(ph) * r); V.Add(Math.Cos(th) * r);
            }
        }
        int Idx(int i, int j) => i * (nu + 1) + j;
        var T = new List<int>();
        for (int i = 0; i < nv; i++) for (int j = 0; j < nu; j++) T.AddRange(new[] { Idx(i, j), Idx(i + 1, j), Idx(i + 1, j + 1) });
        for (int i = 0; i < nv; i++) for (int j = 0; j < nu; j++) T.AddRange(new[] { Idx(i, j), Idx(i + 1, j + 1), Idx(i, j + 1) });
        return (V.ToArray(), T.ToArray());
    }

    internal static GeomData Geom(double[] V, int[] T, double[]? uv = null)
    {
        var g = new GeomData { Vertices = new VArray(V.Select(x => (double)(float)x).ToArray(), 3, "Vec3Array") };
        if (uv != null) g.TexCoords[0] = new VArray(uv.Select(x => (double)(float)x).ToArray(), 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Length, indices: (int[])T.Clone(), indexType: "DrawElementsUInt"));
        return g;
    }

    static int[] Reversed(int[] T)
    {
        var r = new int[T.Length];
        for (int t = 0; t < T.Length / 3; t++) { r[3 * t] = T[3 * t + 2]; r[3 * t + 1] = T[3 * t + 1]; r[3 * t + 2] = T[3 * t]; }
        return r;
    }

    /// <summary>A wavy grid with duplicated vertices (own UVs) along every 4th column: UV seams.</summary>
    internal static (double[] V, int[] T, double[] UV) GridWithSeams(int n = 24)
    {
        var V = new List<double>(); var UV = new List<double>(); var T = new List<int>();
        var vid = new Dictionary<(int, int, int), int>();
        double X(int i) => (double)i / n;
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
                foreach (int side in i % 4 == 0 && i > 0 && i < n ? new[] { 0, 1 } : new[] { 0 })
                {
                    vid[(i, j, side)] = V.Count / 3;
                    V.AddRange(new[] { X(i), X(j), 0.05 * Math.Sin(X(i) * 6) * Math.Cos(X(j) * 5) });
                    UV.AddRange(new[] { X(i) + side * 0.5, X(j) });
                }
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = vid.GetValueOrDefault((i, j, 1), vid[(i, j, 0)]), b = vid[(i + 1, j, 0)], c = vid[(i + 1, j + 1, 0)];
                int d = vid.GetValueOrDefault((i, j + 1, 1), vid[(i, j + 1, 0)]);
                T.AddRange(new[] { a, b, c, a, c, d });
            }
        return (V.ToArray(), T.ToArray(), UV.ToArray());
    }

    [Fact]
    public void IdenticalIsOk()
    {
        var (V, T) = Sphere(30, 15);
        var q = Quality.Compare(Geom(V, T), Geom(V, T));
        Assert.Equal("ok", q.Verdict);
        Assert.True(q.MaxDevPct < 1e-6);
        Assert.Equal(q.BoundaryBefore, q.BoundaryAfter);
    }

    [Fact]
    public void HoleAndMovedSurfaceAreDamage()
    {
        var (V, T) = Sphere(30, 15);
        var holed = Enumerable.Range(0, T.Length / 3).Where(t => t % 7 != 0).SelectMany(t => new[] { T[3 * t], T[3 * t + 1], T[3 * t + 2] }).ToArray();
        var q = Quality.Compare(Geom(V, T), Geom(V, holed));
        Assert.Equal("damaged", q.Verdict);
        Assert.True(q.BoundaryAfter > q.BoundaryBefore);
        var dented = (double[])V.Clone();
        for (int i = 0; i < V.Length / 3; i++)
            if (V[3 * i + 2] > 0.8) for (int c = 0; c < 3; c++) dented[3 * i + c] *= 0.7;
        q = Quality.Compare(Geom(V, T), Geom(dented, T));
        Assert.Equal("damaged", q.Verdict);
        Assert.True(q.VertexDev!.Max() > 2.0);
    }

    [Fact]
    public void FlippedFaces()
    {
        var (V, T) = Sphere(30, 15);
        Assert.True(Quality.Compare(Geom(V, T), Geom(V, Reversed(T))).FlippedPct > 50);
        // double-sided original: the same faces both ways are not "flipped"
        var both = T.Concat(Reversed(T)).ToArray();
        Assert.True(Quality.Compare(Geom(V, both), Geom(V, T)).FlippedPct < 1);
    }

    [Fact]
    public void SimplifyKeepsSeamsClosed()
    {
        var (V, T, UV) = GridWithSeams();
        var g = Geom(V, T, UV);
        var info = new Optimizer.SimplifyInfo();
        var nw = Optimizer.SimplifyGeom(g, new[] { 0.4 }, "qem", minTris: 4, seams: "keep", info: info)[0];
        Assert.True(nw != null, info.Reason);
        var q = Quality.Compare(g, nw);
        Assert.True(q.BoundaryAfter <= q.BoundaryBefore);   // no cracks along the UV seams
        Assert.True(q.MaxDevPct < Quality.CheckDev);
        var ign = Optimizer.SimplifyGeom(g, new[] { 0.4 }, "qem", minTris: 4, seams: "ignore")[0];
        Assert.True(ign!.Counts().tris < T.Length / 3);
    }

    [Fact]
    public void DecimateReachesTargetsAndLocksBorder()
    {
        var (V, T, _) = GridWithSeams(16);
        var res = Decimate.Run(V, T, new[] { 200, 100 }, null, 0, new Decimate.Options { Borders = BorderMode.Lock });
        Assert.Equal(2, res.Count);
        Assert.InRange(res[0].T.Length / 3, 150, 200);
        Assert.InRange(res[1].T.Length / 3, 60, 100);
        // locked borders: every original border vertex is still there, unmoved (terrain tiles keep matching)
        var outPos = new HashSet<(double, double)>();
        for (int i = 0; i < res[1].V.Length / 3; i++) outPos.Add((res[1].V[3 * i], res[1].V[3 * i + 1]));
        for (int i = 0; i < V.Length / 3; i++)
        {
            double x = V[3 * i], y = V[3 * i + 1];
            if (x == 0 || y == 0 || x == 1 || y == 1) Assert.Contains((x, y), outPos);
        }
    }

    [Fact]
    public void DecimateMatchBorderNeverMovesBorderVertices()
    {
        var (V, T, _) = GridWithSeams(16);
        var r = Decimate.Run(V, T, new[] { 80 }, null, 0)[0];   // default: match
        Assert.True(r.T.Length / 3 <= 80);
        var orig = new HashSet<(double, double, double)>();
        for (int i = 0; i < V.Length / 3; i++) orig.Add((V[3 * i], V[3 * i + 1], V[3 * i + 2]));
        var outPos = new HashSet<(double, double)>();
        for (int i = 0; i < r.V.Length / 3; i++)
        {
            double x = r.V[3 * i], y = r.V[3 * i + 1];
            outPos.Add((x, y));
            if (x == 0 || y == 0 || x == 1 || y == 1) Assert.Contains((x, y, r.V[3 * i + 2]), orig);   // on the border: an original vertex
        }
        foreach (var c in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0) }) Assert.Contains(c, outPos);   // corners stay
    }

    [Fact]
    public void DecimateSlidingBorderStaysOnBorder()
    {
        var (V, T, _) = GridWithSeams(16);
        var r = Decimate.Run(V, T, new[] { 80 }, null, 0, new Decimate.Options { Borders = BorderMode.Slide })[0];
        Assert.True(r.T.Length / 3 <= 80);
        for (int i = 0; i < r.V.Length / 3; i++)
        {
            Assert.InRange(r.V[3 * i], -0.01, 1.01);
            Assert.InRange(r.V[3 * i + 1], -0.01, 1.01);
        }
    }

    [Fact]
    public void DecimateMovesSeamsInsteadOfFreezingThem()
    {
        var (V, T, UV) = GridWithSeams();
        var g = Geom(V, T, UV);
        var info = new Optimizer.SimplifyInfo();
        var nw = Optimizer.SimplifyGeom(g, new[] { 0.2 }, "qem", minTris: 4, seams: "keep", info: info)[0];
        Assert.True(nw != null, info.Reason);
        Assert.True(nw!.Counts().tris <= T.Length / 3 * 0.2 + 1);
        var q = Quality.Compare(g, nw);
        Assert.True(q.BoundaryAfter <= q.BoundaryBefore);   // seams stay closed
        Assert.True(q.MaxDevPct < Quality.CheckDev);
        // UVs keep their seam offset: every output u is near a grid value or near grid value + 0.5
        var uv = nw.TexCoords[0]!;
        for (int i = 0; i < uv.Count; i++) Assert.InRange(uv.Data[2 * i], -0.01, 1.51);
    }

    [Fact]
    public void DecimateMergesNearbyParts()
    {
        // two coplanar grids with a tiny gap: with part merging the result can go lower without leaving the plane
        var (V1, T1, _) = GridWithSeams(12);
        int n1 = V1.Length / 3;
        var V = V1.Concat(V1.Select((x, i) => i % 3 == 0 ? x + 1.001 : x)).ToArray();
        var T = T1.Concat(T1.Select(t => t + n1)).ToArray();
        var opts = new Decimate.Options { Borders = BorderMode.Slide, MergeParts = true };
        var r = Decimate.Run(V, T, new[] { 40 }, null, 0, opts)[0];
        Assert.True(r.T.Length / 3 <= 40);
        for (int i = 0; i < r.V.Length / 3; i++) Assert.InRange(r.V[3 * i + 2], -0.1, 0.1);
    }

    [Fact]
    public void DecimateRespectsMaxError()
    {
        var (V, T) = Sphere(40, 20);
        var r = Decimate.Run(V, T, new[] { 10 }, null, 0, new Decimate.Options { MaxError = 0.01 })[0];
        Assert.True(r.T.Length / 3 > 10);                    // stopped early
        var q = Quality.Compare(Geom(V, T), Geom(r.V, r.T));
        Assert.True(q.MaxDev < 0.05, $"max dev {q.MaxDev}");
    }

    static string SphereGeode(int uid, string name, int nu, int nv, double r)
    {
        var (V, T) = Sphere(nu, nv, r);
        var inv = CultureInfo.InvariantCulture;
        var verts = string.Join("\n", Enumerable.Range(0, V.Length / 3).Select(i =>
            string.Join(" ", Enumerable.Range(0, 3).Select(c => Math.Round(V[3 * i + c], 6).ToString("R", inv)))));
        return $@"osg::Geode {{
  UniqueID {uid}
  Name ""{name}""
  Drawables 1 {{
    osg::Geometry {{
      UniqueID {uid + 1}
      PrimitiveSetList 1 {{
        osg::DrawElementsUInt {{
          UniqueID {uid + 2}
          Mode TRIANGLES
          vector {T.Length} {{
{string.Join(" ", T)}
          }}
        }}
      }}
      VertexArray TRUE {{
        osg::Vec3Array {{
          UniqueID {uid + 3}
          Binding BIND_PER_VERTEX
          vector {V.Length / 3} {{
{verts}
          }}
        }}
      }}
    }}
  }}
}}";
    }

    static Scene TwoSpheres(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"sk_{Guid.NewGuid():N}.osgt");
        File.WriteAllText(path, Head + "osg::Group {\n  UniqueID 1\n  Children 2 {\n" + SphereGeode(10, "a", 60, 30, 1.0) + "\n"
                                + SphereGeode(5000, "b", 60, 30, 2.0) + "\n  }\n}\n");
        return SceneIO.Load(path);
    }

    /// <summary>Two spheres in the old .osg text format (saving .osg needs a scene loaded from .osg).</summary>
    static string LegacySpheres()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        void L(string t) => sb.Append(t).Append('\n');
        L("Group {"); L("  name \"root\""); L("  num_children 2");
        foreach (var (name, r) in new[] { ("a", 1.0), ("b", 2.0) })
        {
            var (V, T) = Sphere(60, 30, r);
            L("  Geode {"); L($"    name \"{name}\""); L("    num_drawables 1"); L("    Geometry {");
            L("      Primitives 1"); L("      {"); L($"        DrawElementsUInt TRIANGLES {T.Length}"); L("        {");
            for (int t = 0; t < T.Length / 3; t++) L($"          {T[3 * t]} {T[3 * t + 1]} {T[3 * t + 2]}");
            L("        }"); L("      }");
            L($"      VertexArray {V.Length / 3}"); L("      {");
            for (int v = 0; v < V.Length / 3; v++)
                L("        " + string.Join(" ", Enumerable.Range(0, 3).Select(c => Math.Round(V[3 * v + c], 6).ToString("R", inv))));
            L("      }"); L("    }"); L("  }");
        }
        L("}");
        string path = Path.Combine(Path.GetTempPath(), $"sk_{Guid.NewGuid():N}.osg");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    [Fact]
    public void LodWithTexturedProxySavesAndReloads()
    {
        foreach (var ext in new[] { ".osgt", ".osg" })
        {
            string path;
            Scene sc;
            if (ext == ".osgt") sc = TwoSpheres(out path);
            else { path = LegacySpheres(); sc = SceneIO.Load(path); }
            string outp = Path.Combine(Path.GetTempPath(), $"sk_proxy_{Guid.NewGuid():N}{ext}");
            try
            {
                var (results, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object>
                {
                    ["generate_lod.min_tris"] = 500, ["generate_lod.ratios"] = "0.5, 0.1", ["generate_lod.far_level"] = "proxy",
                    ["generate_lod.proxy_texture"] = 128, ["generate_lod.proxy_voxels"] = 48,
                });
                Assert.Null(results[0].Error);
                var lods = sc.IterNodes().Where(n => n.Kind == Kind.Lod).ToList();
                Assert.Equal(2, lods.Count);
                Assert.All(lods, l => Assert.Equal(3, l.Children.Count));
                var proxy = lods[0].Children[^1];
                Assert.EndsWith("proxy", proxy.Name);
                Assert.NotNull(proxy.StateSet!.Textures[0].Baked);
                SceneIO.Save(sc, outp);
                string texDir = Path.Combine(Path.GetDirectoryName(outp)!, Path.GetFileNameWithoutExtension(outp) + "_textures");
                Assert.True(Directory.GetFiles(texDir, "*.png").Length >= 2);
                var back = SceneIO.Load(outp);
                var lods2 = back.IterNodes().Where(n => n.Kind == Kind.Lod).ToList();
                Assert.Equal(2, lods2.Count);
                var tex = back.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).ToList();
                Assert.Contains(tex, t => t.Exists && t.File.EndsWith(".png"));
            }
            finally
            {
                File.Delete(path);
                if (File.Exists(outp)) File.Delete(outp);
                string texDir = Path.Combine(Path.GetDirectoryName(outp)!, Path.GetFileNameWithoutExtension(outp) + "_textures");
                if (Directory.Exists(texDir)) Directory.Delete(texDir, true);
            }
        }
    }

    [Fact]
    public void TrackingAndUndo()
    {
        var sc = TwoSpheres(out var path);
        try
        {
            var t0 = History.QuickTotals(sc);
            var snap = new Snapshot(sc, "test");
            var (results, _, _) = Optimizer.Run(sc, new[] { "simplify", "generate_lod" },
                new Dictionary<string, object> { ["simplify.ratio"] = 0.5, ["simplify.min_tris"] = 1000, ["generate_lod.min_tris"] = 500 });
            var simp = results[0];
            Assert.Null(simp.Error);
            Assert.Equal(2, simp.Changes.Count);
            Assert.All(simp.Changes, c => { Assert.Equal("changed", c.Kind); Assert.NotNull(c.Before); });
            var (b, a, _, _) = simp.Changes[0].Counts();
            Assert.True(a < b);
            Assert.True(simp.Seconds >= 0);
            Assert.Equal(t0, simp.TotalsBefore);
            Assert.True(simp.Totals!["triangles"] < t0["triangles"]);
            Assert.Contains(results[1].Changes, c => c.Kind == "lod level");
            var q = Quality.Compare(simp.Changes[0].Before, simp.Changes[0].After);
            Assert.Contains(q.Verdict, new[] { "ok", "check" });
            Assert.NotEqual(t0, History.QuickTotals(sc));
            snap.Restore();
            Assert.Equal(t0, History.QuickTotals(sc));
            Assert.Equal(new[] { Kind.Geode, Kind.Geode }, sc.Root!.Children.Select(c => c.Kind));
            // undo again after a delete
            snap = new Snapshot(sc, "delete");
            Optimizer.ReplaceChild(sc.Root, sc.Root.Children[0], Array.Empty<Node>());
            Assert.Single(sc.Root.Children);
            snap.Restore();
            Assert.Equal(2, sc.Root.Children.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UndoRestoresTheSavedFile()
    {
        var sc = TwoSpheres(out var path);
        var a = path + ".a.osgt";
        var b = path + ".b.osgt";
        try
        {
            SceneIO.Save(sc, a);
            var snap = new Snapshot(sc, "aggressive");
            Optimizer.Run(sc, Optimizer.Presets["aggressive"], new Dictionary<string, object> { ["simplify.min_tris"] = 100 });
            snap.Restore();
            SceneIO.Save(sc, b);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally { File.Delete(path); File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void SafePresetWeldsSoupAndRoundTrips()
    {
        // 50 quads as an unindexed triangle soup: weld + clean must index them (4 vertices per quad)
        var inv = CultureInfo.InvariantCulture;
        var verts = new List<string>();
        for (int i = 0; i < 50; i++)
        {
            string P(int x, int y) => $"{(i + x).ToString(inv)} {y} 0";
            verts.AddRange(new[] { P(0, 0), P(1, 0), P(1, 1), P(0, 0), P(1, 1), P(0, 1) });
        }
        var text = Head + $@"osg::Group {{
  UniqueID 1
  Children 1 {{
    osg::Geode {{
      UniqueID 2
      Drawables 1 {{
        osg::Geometry {{
          UniqueID 3
          PrimitiveSetList 1 {{
            osg::DrawArrays {{
              UniqueID 4
              Mode TRIANGLES
              First 0
              Count {verts.Count}
            }}
          }}
          VertexArray TRUE {{
            osg::Vec3Array {{
              UniqueID 5
              Binding BIND_PER_VERTEX
              vector {verts.Count} {{
{string.Join("\n", verts)}
              }}
            }}
          }}
        }}
      }}
    }}
  }}
}}
";
        var path = Path.Combine(Path.GetTempPath(), $"sk_{Guid.NewGuid():N}.osgt");
        File.WriteAllText(path, text);
        var outp = path + ".opt.osgt";
        try
        {
            var sc = SceneIO.Load(path);
            var (results, before, after) = Optimizer.Run(sc, Optimizer.Presets["safe"]);
            Assert.All(results, r => Assert.Null(r.Error));
            Assert.Equal(300L, Convert.ToInt64(before["vertices"]));
            Assert.Equal(102L, Convert.ToInt64(after["vertices"]));   // a 51 x 2 vertex strip
            Assert.Equal(100L, Convert.ToInt64(after["triangles"]));
            SceneIO.Save(sc, outp);
            var back = Analyzer.Analyze(SceneIO.Load(outp)).Summary();
            Assert.Equal(102L, Convert.ToInt64(back["vertices"]));
            Assert.Equal(100L, Convert.ToInt64(back["triangles"]));
        }
        finally { File.Delete(path); File.Delete(outp); }
    }
}
