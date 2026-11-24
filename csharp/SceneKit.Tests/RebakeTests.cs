using SceneKit;
using Xunit;

/// <summary>D2 "rebake": simplify on positions only without the safety brake, new UV atlas, colour baked from the original.</summary>
public class RebakeTests
{
    /// <summary>UV sphere (u around, v from pole to pole; the u = 0 / 1 column is duplicated: a UV seam).</summary>
    internal static (double[] V, int[] T, double[] UV) Sphere(int nu = 40, int nv = 20, double r = 1.0)
    {
        var V = new List<double>(); var UV = new List<double>(); var T = new List<int>();
        for (int i = 0; i <= nv; i++)
            for (int j = 0; j <= nu; j++)
            {
                double th = Math.PI * i / nv, ph = 2 * Math.PI * j / nu;
                V.AddRange(new[] { Math.Sin(th) * Math.Cos(ph) * r, Math.Sin(th) * Math.Sin(ph) * r, Math.Cos(th) * r });
                UV.AddRange(new[] { (double)j / nu, 1 - (double)i / nv });
            }
        int Idx(int i, int j) => i * (nu + 1) + j;
        for (int i = 0; i < nv; i++)
            for (int j = 0; j < nu; j++)
            {
                T.AddRange(new[] { Idx(i, j), Idx(i + 1, j), Idx(i + 1, j + 1) });
                T.AddRange(new[] { Idx(i, j), Idx(i + 1, j + 1), Idx(i, j + 1) });
            }
        return (V.ToArray(), T.ToArray(), UV.ToArray());
    }

    internal static (Scene sc, Node holder, Node geo) TexturedSphere()
    {
        var (V, T, UV) = Sphere();
        var g = new GeomData { Vertices = new VArray(V, 3, "Vec3Array") };
        g.TexCoords[0] = new VArray(UV, 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Length, indices: T, indexType: "DrawElementsUInt"));
        var geo = new Node(Kind.Geometry, "osg::Geometry", "ball") { Geom = g };
        var holder = new Node(Kind.Geode, "osg::Geode", "ballgeode")
        {
            StateSet = new StateSet { MaterialName = "paint", Diffuse = new[] { 0.8, 0.8, 0.8, 1 }, Textures = { new Texture { File = "half.png", Baked = LodToolsTests.HalfImage(), Exists = true, Channels = 3, Width = 64, Height = 64 } } },
        };
        holder.Children.Add(geo);
        var root = new Node(Kind.Group, "osg::Group", "root");
        root.Children.Add(holder);
        return (new Scene("x.osgt", "osgt") { Root = root }, holder, geo);
    }

    static Dictionary<string, object> Prms(double ratio) => new()
    {
        ["simplify.seams"] = "rebake", ["simplify.ratio"] = ratio, ["simplify.rebake_texture"] = 128,
    };

    [Fact]
    public void ReachesTheTargetWhereTheBrakeStopsKeep()
    {
        var (sc, _, geo) = TexturedSphere();
        long m = geo.Geom!.Counts().tris;
        var (sc2, _, geo2) = TexturedSphere();
        Optimizer.Run(sc2, new[] { "simplify" }, new() { ["simplify.ratio"] = 0.02 }, targets: new List<Node> { geo2 });
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, Prms(0.02), targets: new List<Node> { geo });
        Assert.Null(results[0].Error);
        long after = geo.Geom!.Counts().tris;
        Assert.True(after <= m * 0.02 + 2, $"{after} of {m}: {results[0].Message}");
        Assert.True(geo2.Geom!.Counts().tris > after * 2, $"keep: {geo2.Geom!.Counts().tris}");
    }

    [Fact]
    public void BakedTextureShowsTheOriginalColours()
    {
        var (sc, holder, geo) = TexturedSphere();
        Optimizer.Run(sc, new[] { "simplify" }, Prms(0.1), targets: new List<Node> { geo });
        var ss = geo.StateSet;
        Assert.NotNull(ss);
        Assert.NotSame(holder.StateSet, ss);
        var tex = Assert.Single(ss!.Textures);
        Assert.NotNull(tex.Baked);
        Assert.Equal(128, tex.Baked!.Width);
        Assert.Equal("CLAMP_TO_EDGE", tex.WrapS);                   // a new UV atlas
        Assert.Equal("paint", ss.MaterialName);                     // the material stays (colour is not baked in twice)
        var g = geo.Geom!;
        var V = g.Vertices!.Data; var UV = g.TexCoords[0].Data; var T = g.Triangles();
        Assert.All(UV, u => Assert.InRange(u, 0, 1));
        Assert.NotNull(g.Normals);
        // HalfImage: u < 0.5 red, else blue; on the sphere u < 0.5 is y > 0
        int bad = 0, total = 0;
        Span<double> c = stackalloc double[4];
        for (int t = 0; t < T.Length / 3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], d = T[3 * t + 2];
            double y = (V[3 * a + 1] + V[3 * b + 1] + V[3 * d + 1]) / 3;
            if (Math.Abs(y) < 0.3) continue;
            Bake.Bilinear(tex.Baked, (UV[2 * a] + UV[2 * b] + UV[2 * d]) / 3, (UV[2 * a + 1] + UV[2 * b + 1] + UV[2 * d + 1]) / 3, c);
            total++;
            if (y > 0 != c[0] > c[2]) bad++;
        }
        Assert.True(total > 20);
        Assert.True(bad <= total / 10, $"{bad} of {total} wrong");
    }

    [Fact]
    public void UndoBringsBackGeometryAndStateSet()
    {
        var (sc, _, geo) = TexturedSphere();
        var g0 = geo.Geom;
        var snap = new Snapshot(sc, "before");
        Optimizer.Run(sc, new[] { "simplify" }, Prms(0.1), targets: new List<Node> { geo });
        Assert.NotNull(geo.StateSet);
        snap.Restore();
        Assert.Same(g0, geo.Geom);
        Assert.Null(geo.StateSet);
    }

    [Fact]
    public void UntexturedGeometryIsSimplifiedWithoutATexture()
    {
        var (sc, holder, geo) = TexturedSphere();
        holder.StateSet = null;
        geo.Geom!.TexCoords.Clear();
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, Prms(0.02), targets: new List<Node> { geo });
        Assert.True(geo.Geom!.Counts().tris <= 34, results[0].Message);
        Assert.True(geo.StateSet == null || geo.StateSet.Textures.Count == 0);
    }

    // ---------------------------------------------------------------- final review fixes

    [Fact]
    public void TextureThatCannotBeReadIsNotThrownAway()
    {
        // review #2: a texture we cannot decode (missing file, .dds): keep the geometry as it is
        var (sc, holder, geo) = TexturedSphere();
        holder.StateSet!.Textures[0] = new Texture { File = "bark.dds", Exists = false, Channels = 3 };
        var g0 = geo.Geom;
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, Prms(0.1), targets: new List<Node> { geo });
        Assert.Same(g0, geo.Geom);
        Assert.Null(geo.StateSet);
        Assert.Contains("texture", results[0].Message);
    }

    [Fact]
    public void MaterialColourIsNotAppliedTwice()
    {
        // review #3: OpenFlight vertex colours already carry the material diffuse (0.5); the image generator applies the
        // material again, so the texture must hold the face colour (white) and the vertices the diffuse
        var (sc, holder, geo) = TexturedSphere();
        holder.StateSet = new StateSet { MaterialName = "grey", Diffuse = new[] { 0.5, 0.5, 0.5, 1 } };
        var g = geo.Geom!;
        g.TexCoords.Clear();
        g.Colors = new VArray(Enumerable.Repeat(0.5, g.NumVertices * 4).ToArray(), 4, "Vec4Array");
        Optimizer.Run(sc, new[] { "simplify" }, Prms(0.1), targets: new List<Node> { geo });
        var img = geo.StateSet!.Textures[0].Baked!;
        Span<double> c = stackalloc double[4];
        Bake.Bilinear(img, 0.5, 0.5, c);
        Assert.InRange(c[0], 0.95, 1.0);
        Assert.All(geo.Geom!.Colors!.Data, v => Assert.InRange(v, 0.49, 1.0));
        Assert.InRange(geo.Geom.Colors.Data[0], 0.49, 0.51);
    }

    [Fact]
    public void LinesAreNotLost()
    {
        // review #4: geometry with lines is left as it is
        var (sc, _, geo) = TexturedSphere();
        geo.Geom!.Prims.Add(new Primitive("LINES", "elements", count: 2, indices: new[] { 0, 1 }, indexType: "DrawElementsUInt"));
        var g0 = geo.Geom;
        Optimizer.Run(sc, new[] { "simplify" }, Prms(0.1), targets: new List<Node> { geo });
        Assert.Same(g0, geo.Geom);
    }

    [Fact]
    public void SecondTextureUnitIsBakedToo()
    {
        // a second unit (here without its own UV set: unit 0's is used) gets its own baked texture
        var (sc, holder, geo) = TexturedSphere();
        holder.StateSet!.Textures.Add(new Texture { File = "light.png", Unit = 1, Baked = LodToolsTests.HalfImage(), Exists = true });
        var g0 = geo.Geom;
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, Prms(0.1), targets: new List<Node> { geo });
        Assert.NotSame(g0, geo.Geom);
        Assert.Equal(new[] { 0, 1 }, geo.StateSet!.Textures.Select(t => t.Unit).OrderBy(u => u));
        Assert.All(geo.StateSet.Textures, t => Assert.NotNull(t.Baked));
        Assert.True(geo.Geom!.TexCoords.ContainsKey(1));
        Assert.Contains("unit", results[0].Message);
    }

    [Fact]
    public void TextureAboveAndMaterialOnTheGeometryAreBothUsed()
    {
        // review #5a: OSG merges state down the path: texture on the geode, material on the geometry
        var (sc, holder, geo) = TexturedSphere();
        geo.StateSet = new StateSet { MaterialName = "own", Diffuse = new[] { 1.0, 1.0, 1.0, 1 } };
        Optimizer.Run(sc, new[] { "simplify" }, Prms(0.1), targets: new List<Node> { geo });
        var tex = Assert.Single(geo.StateSet!.Textures);
        Assert.NotNull(tex.Baked);
        Assert.Equal("own", geo.StateSet.MaterialName);
        Span<double> c = stackalloc double[4];
        var img = tex.Baked!;
        int red = 0, blue = 0;
        for (int y = 0; y < img.Height; y += 4)
            for (int x = 0; x < img.Width; x += 4)
            {
                Bake.Bilinear(img, (x + 0.5) / img.Width, (y + 0.5) / img.Height, c);
                if (c[0] > 0.6 && c[2] < 0.4) red++;
                if (c[2] > 0.6 && c[0] < 0.4) blue++;
            }
        Assert.True(red > 20 && blue > 20, $"red {red} blue {blue}: the geode's texture was baked");
    }

    [Fact]
    public void GeometrySharedUnderDifferentTexturesIsLeftAlone()
    {
        // review #5b: one geometry under two geodes with different textures (texture variants)
        var (sc, holder, geo) = TexturedSphere();
        var other = new Node(Kind.Geode, "osg::Geode", "variant")
        {
            StateSet = new StateSet { Textures = { new Texture { File = "other.png", Baked = new RawImage(4, 4, 3), Exists = true } } },
            Children = { geo },
        };
        sc.Root!.Children.Add(other);
        var g0 = geo.Geom;
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, Prms(0.1), targets: new List<Node> { geo });
        Assert.Same(g0, geo.Geom);
        Assert.Null(geo.StateSet);
    }

    [Fact]
    public void PassOffersRebake()
    {
        var spec = Optimizer.Passes["simplify"].Params.First(p => p.Key == "seams");
        Assert.Contains("rebake", spec.Choices!);
    }
}
