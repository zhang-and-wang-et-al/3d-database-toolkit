using SceneKit;
using Xunit;

/// <summary>T1-T3: passes that build new meshes (cards, billboard, proxy, LOD far level, rebake) carry texture units
/// above 0 (detail / light maps) over: a baked atlas per unit with the same layout as unit 0.</summary>
public class MultiTextureTests
{
    static RawImage Solid(byte v)
    {
        var im = new RawImage(8, 8, 3);
        Array.Fill(im.Pixels, v);
        return im;
    }

    /// <summary>Left half (u &lt; 0.5) dark, right half white.</summary>
    static RawImage DarkLeft()
    {
        var im = new RawImage(64, 64, 3);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                for (int k = 0; k < 3; k++) im.Pixels[(y * 64 + x) * 3 + k] = (byte)(x < 32 ? 30 : 255);
        return im;
    }

    /// <summary>The tree with a dark texture on unit 1 of the crown (own UV set 1); the trunk has no unit 1. With
    /// onHolder the texture sits on the tree geode instead of the crown.</summary>
    static (Scene sc, Node tree) DetailTree(bool onHolder = false)
    {
        var (sc, tree) = ReplaceTests.Tree();
        var crown = tree.Children[1];
        var (_, _, uv) = RebakeTests.Sphere(24, 12, 2.0);
        crown.Geom!.TexCoords[1] = new VArray(uv, 2, "Vec2Array");
        var tex = new Texture { File = "detail.png", Unit = 1, Baked = Solid(40), Exists = true, Channels = 3, Width = 8, Height = 8 };
        if (onHolder) tree.StateSet = new StateSet { Textures = { tex } };
        else crown.StateSet!.Textures.Add(tex);
        return (sc, tree);
    }

    /// <summary>Both units present, same size, the UVs of unit 1 a copy of unit 0's, unit 1 opaque everywhere.</summary>
    static (RawImage t0, RawImage t1) CheckUnits(Node geo, StateSet ss)
    {
        var t0 = Assert.Single(ss.Textures, t => t.Unit == 0);
        var t1 = Assert.Single(ss.Textures, t => t.Unit == 1);
        Assert.NotNull(t0.Baked);
        Assert.NotNull(t1.Baked);
        Assert.Equal(t0.Baked!.Width, t1.Baked!.Width);
        Assert.Equal(t0.Baked.Height, t1.Baked.Height);
        Assert.NotEqual(t0.File, t1.File);
        Assert.EndsWith(".png", t1.File);
        Assert.Equal(geo.Geom!.TexCoords[0].Data, geo.Geom.TexCoords[1].Data);
        for (int i = 0; i < t1.Baked.Width * t1.Baked.Height; i++) Assert.Equal(255, t1.Baked.Pixels[4 * i + 3]);
        return (t0.Baked, t1.Baked);
    }

    /// <summary>Unit 1 colour at the centroids of the triangles whose centroid z is in [z0, z1].</summary>
    static List<double> Unit1Grey(Node geo, RawImage img, double z0, double z1)
    {
        var g = geo.Geom!;
        var V = g.Vertices!.Data; var UV = g.TexCoords[1].Data; var T = g.Triangles();
        var o = new List<double>();
        Span<double> c = stackalloc double[4];
        for (int t = 0; t < T.Length / 3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], d = T[3 * t + 2];
            double z = (V[3 * a + 2] + V[3 * b + 2] + V[3 * d + 2]) / 3;
            if (z < z0 || z > z1) continue;
            Bake.Bilinear(img, (UV[2 * a] + UV[2 * b] + UV[2 * d]) / 3, (UV[2 * a + 1] + UV[2 * b + 1] + UV[2 * d + 1]) / 3, c);
            o.Add(c[1]);
        }
        return o;
    }

    [Fact]
    public void BillboardKeepsTheSecondUnit()
    {
        var (sc, tree) = DetailTree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, new() { ["replace_cards.mode"] = "billboard", ["replace_cards.texture"] = 128 },
            targets: new List<Node> { tree });
        Assert.Contains("unit", results[0].Message);
        var bb = Assert.Single(sc.Root!.Children);
        var geo = Assert.Single(bb.Children);
        var (_, t1) = CheckUnits(geo, geo.StateSet!);
        Span<double> c = stackalloc double[4];
        Bake.Bilinear(t1, 0.5, 0.2, c);                               // trunk: no unit 1 -> neutral white
        Assert.True(c[0] > 0.9 && c[1] > 0.9 && c[2] > 0.9, $"trunk {c[0]:F2} {c[1]:F2} {c[2]:F2}");
        Bake.Bilinear(t1, 0.5, 0.7, c);                               // crown: the detail texture, raw (no green material)
        Assert.True(c[0] < 0.25 && c[1] < 0.25 && c[2] < 0.25, $"crown {c[0]:F2} {c[1]:F2} {c[2]:F2}");
    }

    [Fact]
    public void CrossedCardsKeepTheSecondUnit()
    {
        var (sc, tree) = DetailTree();
        Optimizer.Run(sc, new[] { "replace_cards" }, new() { ["replace_cards.mode"] = "crossed", ["replace_cards.planes"] = 2, ["replace_cards.texture"] = 128 },
            targets: new List<Node> { tree });
        var geo = Assert.Single(SoftRender.GeometryItems(Assert.Single(sc.Root!.Children))).node;
        var (t0, t1) = CheckUnits(geo, geo.StateSet!);
        int dark = 0, white = 0;
        for (int i = 0; i < t0.Width * t0.Height; i++)
        {
            if (t0.Pixels[4 * i + 3] < 200) continue;
            if (t1.Pixels[4 * i] < 60) dark++;
            else if (t1.Pixels[4 * i] > 230) white++;
        }
        Assert.True(dark > 100 && white > 20, $"dark {dark} white {white}");
    }

    [Fact]
    public void ProxyKeepsTheSecondUnit()
    {
        var (sc, tree) = DetailTree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" },
            new() { ["replace_proxy.ratio"] = 0.5, ["replace_proxy.texture"] = 128, ["replace_proxy.voxels"] = 48 }, targets: new List<Node> { tree });
        Assert.Contains("unit", results[0].Message);
        var geo = Assert.Single(SoftRender.GeometryItems(Assert.Single(sc.Root!.Children))).node;
        var (_, t1) = CheckUnits(geo, geo.StateSet!);
        var crown = Unit1Grey(geo, t1, 4.5, 10);
        var trunk = Unit1Grey(geo, t1, -1, 1.5);
        Assert.True(crown.Count > 5 && trunk.Count > 2, $"{crown.Count} {trunk.Count}");
        Assert.True(crown.Count(x => x < 0.3) >= crown.Count * 0.9, "crown dark: " + string.Join(" ", crown.Select(x => x.ToString("F2"))));
        Assert.True(trunk.Count(x => x > 0.9) >= trunk.Count * 0.9, "trunk white: " + string.Join(" ", trunk.Select(x => x.ToString("F2"))));
    }

    [Fact]
    public void LodFarProxyUsesTheUnitOnTheHolder()
    {
        var (sc, _) = DetailTree(onHolder: true);
        var (results, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object>
        {
            ["generate_lod.min_tris"] = 100, ["generate_lod.ratios"] = "0.5, 0.2", ["generate_lod.far_level"] = "proxy",
            ["generate_lod.proxy_texture"] = 128, ["generate_lod.proxy_voxels"] = 48, ["generate_lod.switching"] = "fixed",
        });
        Assert.Null(results[0].Error);
        var lod = Assert.Single(sc.IterNodes().Where(n => n.Kind == Kind.Lod).ToList());
        var far = lod.Children[^1];
        Assert.EndsWith("proxy", far.Name);
        var (_, t1) = CheckUnits(far, far.StateSet!);
        var crown = Unit1Grey(far, t1, 4.5, 10);
        Assert.True(crown.Count > 3 && crown.Count(x => x < 0.3) >= crown.Count * 0.9, string.Join(" ", crown.Select(x => x.ToString("F2"))));
    }

    [Fact]
    public void LodFarCardsKeepTheSecondUnit()
    {
        var (sc, _) = DetailTree(onHolder: true);
        Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object>
            { ["generate_lod.min_tris"] = 100, ["generate_lod.far_level"] = "cards", ["generate_lod.proxy_texture"] = 128 });
        var lod = Assert.Single(sc.IterNodes().Where(n => n.Kind == Kind.Lod).ToList());
        var geo = Assert.Single(SoftRender.GeometryItems(lod.Children[^1])).node;
        CheckUnits(geo, geo.StateSet!);
    }

    /// <summary>The rebake sphere with a second unit: UV set 1 is unit 0's mapping transposed (u1 = v0), with a texture
    /// that is dark for u &lt; 0.5, so on unit 1 the lower half (z &lt; 0) is dark and the upper half white.</summary>
    internal static (Scene sc, Node holder, Node geo) TwoUnitSphere()
    {
        var (sc, holder, geo) = RebakeTests.TexturedSphere();
        var uv0 = geo.Geom!.TexCoords[0].Data;
        var uv1 = new double[uv0.Length];
        for (int i = 0; i < uv0.Length / 2; i++) { uv1[2 * i] = uv0[2 * i + 1]; uv1[2 * i + 1] = uv0[2 * i]; }
        geo.Geom.TexCoords[1] = new VArray(uv1, 2, "Vec2Array");
        holder.StateSet!.Textures.Add(new Texture { File = "light.png", Unit = 1, Baked = DarkLeft(), Exists = true, Channels = 3, Width = 64, Height = 64 });
        return (sc, holder, geo);
    }

    static Dictionary<string, object> Rebake(double ratio) => new()
    {
        ["simplify.seams"] = "rebake", ["simplify.ratio"] = ratio, ["simplify.rebake_texture"] = 128,
    };

    [Fact]
    public void RebakeBakesTheSecondUnit()
    {
        var (sc, _, geo) = TwoUnitSphere();
        long m = geo.Geom!.Counts().tris;
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, Rebake(0.1), targets: new List<Node> { geo });
        Assert.True(geo.Geom!.Counts().tris < m / 2, results[0].Message);
        var (_, t1) = CheckUnits(geo, geo.StateSet!);
        var low = Unit1Grey(geo, t1, -2, -0.3);
        var high = Unit1Grey(geo, t1, 0.3, 2);
        Assert.True(low.Count > 10 && high.Count > 10);
        Assert.True(low.Count(x => x < 0.3) >= low.Count * 0.9, "low dark: " + string.Join(" ", low.Select(x => x.ToString("F2"))));
        Assert.True(high.Count(x => x > 0.9) >= high.Count * 0.9, "high white: " + string.Join(" ", high.Select(x => x.ToString("F2"))));
    }

    [Fact]
    public void RebakeKeyTellsSecondUnitTexturesApart()
    {
        // one geometry under two geodes that differ only in the unit 1 texture: rebake would give both the same
        var (sc, holder, geo) = TwoUnitSphere();
        var other = new Node(Kind.Geode, "osg::Geode", "variant")
        {
            StateSet = new StateSet
            {
                MaterialName = "paint", Diffuse = new[] { 0.8, 0.8, 0.8, 1 },
                Textures = { holder.StateSet!.Textures[0], new Texture { File = "light2.png", Unit = 1, Baked = Solid(200), Exists = true } },
            },
            Children = { geo },
        };
        sc.Root!.Children.Add(other);
        var g0 = geo.Geom;
        Optimizer.Run(sc, new[] { "simplify" }, Rebake(0.1), targets: new List<Node> { geo });
        Assert.Same(g0, geo.Geom);
    }

    [Fact]
    public void RebakedSecondUnitSavesAndLoadsBack()
    {
        var (sc, _, geo) = TwoUnitSphere();
        Optimizer.Run(sc, new[] { "simplify" }, Rebake(0.1), targets: new List<Node> { geo });
        string outp = Path.Combine(Path.GetTempPath(), $"sk_mt_{Guid.NewGuid():N}.osgt");
        string texDir = Path.Combine(Path.GetDirectoryName(outp)!, Path.GetFileNameWithoutExtension(outp) + "_textures");
        try
        {
            SceneIO.Save(sc, outp);
            var back = SceneIO.Load(outp);
            var ball = back.IterNodes().Single(n => n.Kind == Kind.Geometry && n.Name == "ball");
            Assert.True(ball.Geom!.TexCoords.ContainsKey(0) && ball.Geom.TexCoords.ContainsKey(1));
            var tex = ball.StateSet!.Textures;
            Assert.Contains(tex, t => t.Unit == 0 && t.Exists);
            Assert.Contains(tex, t => t.Unit == 1 && t.Exists);
            Assert.Equal(2, tex.Select(t => t.File).Distinct().Count());
        }
        finally
        {
            if (File.Exists(outp)) File.Delete(outp);
            if (Directory.Exists(texDir)) Directory.Delete(texDir, true);
        }
    }
}
