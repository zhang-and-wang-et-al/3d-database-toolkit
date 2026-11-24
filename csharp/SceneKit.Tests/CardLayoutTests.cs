using SceneKit;
using Xunit;

/// <summary>R3-C2: <see cref="Cards.Render(IList{Cards.Item}, IList{Cards.View}, int, Cards.CellRule, ExtraFill?, ExtraFill?, Cards.Padding)"/>
/// over a list of views (frame, offset, slab, extent): vertical views reproduce the azimuth render byte for byte, the
/// top / bottom pictures are oriented and not mirrored, a slab keeps only its layer, class maps stay nearest.</summary>
public class CardLayoutTests
{
    /// <summary>Today's crossed 4 (as replace_cards builds it).</summary>
    static readonly double[] Az = { 0, 180, 45, 225, 90, 270, 135, 315 };

    static Cards.Item Mesh((double[] V, int[] T) m, double r, double g, double b) =>
        new() { V = m.V, T = m.T, Color = new[] { r, g, b, 1.0 } };

    static Cards.Item Quad(double[] p0, double[] p1, double[] p2, double[] p3, double r, double g, double b) =>
        new() { V = p0.Concat(p1).Concat(p2).Concat(p3).ToArray(), T = new[] { 0, 1, 2, 0, 2, 3 }, Color = new[] { r, g, b, 1.0 } };

    static readonly double[] Origin = { 0, 0, 0 };

    /// <summary>Covered texels (alpha above 0) whose colour is close to (r, g, b).</summary>
    static List<(int x, int y)> Texels(RawImage img, int r, int g, int b)
    {
        var o = new List<(int, int)>();
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int i = 4 * (y * img.Width + x);
                if (img.Pixels[i + 3] == 0) continue;
                if (Math.Abs(img.Pixels[i] - r) < 40 && Math.Abs(img.Pixels[i + 1] - g) < 40 && Math.Abs(img.Pixels[i + 2] - b) < 40) o.Add((x, y));
            }
        return o;
    }

    [Fact]
    public void VerticalViewsMatchTheAzimuthRender()
    {
        var a = Cards.Render(CardFixtures.Items(), Az, 128);
        var b = Cards.Render(CardFixtures.Items(), Az.Select(Cards.Vertical).ToList(), 128);
        Assert.NotNull(a); Assert.NotNull(b);
        Assert.Equal(a!.Atlas.Pixels, b!.Atlas.Pixels);
        Assert.Equal(a.Extra.Keys.OrderBy(k => k.ToString()), b.Extra.Keys.OrderBy(k => k.ToString()));
        Assert.Equal(2, a.Extra.Count);   // unit 1 and the class map, both present
        foreach (var (k, img) in a.Extra) Assert.Equal(img.Pixels, b.Extra[k].Pixels);
        Assert.Equal(a.Center, b.Center);
        Assert.Equal(a.Cards.Count, b.Cards.Count);
        for (int i = 0; i < a.Cards.Count; i++)
        {
            Cards.Card p = a.Cards[i], q = b.Cards[i];
            Assert.Equal(p.Dir, q.Dir);
            Assert.Equal(new[] { p.U0, p.V0, p.U1, p.V1, p.S0, p.S1, p.Z0, p.Z1 }, new[] { q.U0, q.V0, q.U1, q.V1, q.S0, q.S1, q.Z0, q.Z1 });
            Assert.Equal(i, p.Cell); Assert.Equal(i, q.Cell);
        }
        // ruling P3: the azimuth path fills the cells of its equal grid too
        Assert.Equal(a.Cells, b.Cells);
        Assert.Equal(new Cards.CellRect(0, 0, 42, 42), a.Cells[0]);
        Assert.Equal(new Cards.CellRect(42, 0, 42, 42), a.Cells[1]);
    }

    [Fact]
    public void MarkerOnPlusXIsOnTheRightOfTheLookingAlongPlusYPicture()
    {
        // looking along +y, the viewer sees the box's -y face; the marker lies on that face's +x half
        var items = new List<Cards.Item>
        {
            Mesh(OcclusionTests.Box(-1, -1, 0, 1, 1, 2), 1, 1, 1),
            Quad(new[] { 0.4, -1.01, 0.8 }, new[] { 0.9, -1.01, 0.8 }, new[] { 0.9, -1.01, 1.2 }, new[] { 0.4, -1.01, 1.2 }, 1, 0, 0),
        };
        var img = Cards.Snapshot(items, Cards.Vertical(90), Origin, (-1.5, 1.5, -0.5, 2.5), 64, 64);
        var red = Texels(img, 255, 0, 0);
        Assert.NotEmpty(red);
        Assert.All(red, p => Assert.True(p.x >= 32, $"red at x {p.x}"));
        Assert.NotEmpty(Texels(img, 255, 255, 255));
    }

    /// <summary>A white box with a red marker on its +x half and a blue marker on its +y half, both on the face at <paramref name="z"/>.</summary>
    static List<Cards.Item> Markers(double z) => new()
    {
        Mesh(OcclusionTests.Box(-1, -1, 0, 1, 1, 2), 1, 1, 1),
        Quad(new[] { 0.4, -0.2, z }, new[] { 0.9, -0.2, z }, new[] { 0.9, 0.2, z }, new[] { 0.4, 0.2, z }, 1, 0, 0),
        Quad(new[] { -0.2, 0.4, z }, new[] { 0.2, 0.4, z }, new[] { 0.2, 0.9, z }, new[] { -0.2, 0.9, z }, 0, 0, 1),
    };

    [Fact]
    public void TopPictureHasPlusXOnTheRight()
    {
        var img = Cards.Snapshot(Markers(2.01), Cards.Top(), Origin, (-1.5, 1.5, -1.5, 1.5), 64, 64);
        var red = Texels(img, 255, 0, 0); var blue = Texels(img, 0, 0, 255);
        Assert.NotEmpty(red); Assert.NotEmpty(blue);
        Assert.All(red, p => Assert.True(p.x >= 32, $"red at x {p.x}"));
        Assert.All(blue, p => Assert.True(p.y < 32, $"blue at y {p.y}"));
    }

    [Fact]
    public void BottomPictureIsNotMirrored()
    {
        var img = Cards.Snapshot(Markers(-0.01), Cards.Bottom(), Origin, (-1.5, 1.5, -1.5, 1.5), 64, 64);
        var red = Texels(img, 255, 0, 0); var blue = Texels(img, 0, 0, 255);
        Assert.NotEmpty(red); Assert.NotEmpty(blue);
        Assert.All(red, p => Assert.True(p.x >= 32, $"red at x {p.x}"));
        Assert.All(blue, p => Assert.True(p.y >= 32, $"blue at y {p.y}"));
    }

    [Fact]
    public void SlabKeepsOnlyItsLayer()
    {
        // trunk 0..4 high, one green leaf quad at 0.75 of the height (z 3); Top looks along -z, so the depth is -z
        var items = new List<Cards.Item>
        {
            Mesh(OcclusionTests.Box(-0.1, -0.1, 0, 0.1, 0.1, 4), 0.45, 0.3, 0.2),
            Quad(new[] { -1.0, -1.0, 3.0 }, new[] { 1.0, -1.0, 3.0 }, new[] { 1.0, 1.0, 3.0 }, new[] { -1.0, 1.0, 3.0 }, 0, 1, 0),
        };
        var around = Cards.Top(); around.Slab = (-3.2, -2.8);
        var aroundImg = Cards.Snapshot(items, around, Origin, (-1.5, 1.5, -1.5, 1.5), 64, 64);
        var leaf = Texels(aroundImg, 0, 255, 0);
        Assert.True(leaf.Count > 64 * 64 / 4, $"leaf texels {leaf.Count}");
        Assert.Empty(Texels(aroundImg, 115, 77, 51));   // the trunk's top (z 4) lies above the slab
        var low = Cards.Top(); low.Slab = (-2.0, 0.0);
        Assert.Empty(Texels(Cards.Snapshot(items, low, Origin, (-1.5, 1.5, -1.5, 1.5), 64, 64), 0, 255, 0));
        // no slab: the trunk's top hides the middle of the leaf
        var all = Cards.Snapshot(items, Cards.Top(), Origin, (-1.5, 1.5, -1.5, 1.5), 64, 64);
        Assert.NotEmpty(Texels(all, 115, 77, 51));
        Assert.True(Texels(all, 0, 255, 0).Count < leaf.Count);
    }

    [Fact]
    public void ClassViewStaysNearestInsideTheSlab()
    {
        var v = Cards.Top(); v.Slab = (-5.0, -4.0);   // the crown's middle layer (z 4..5)
        var res = Cards.Render(CardFixtures.Items(), new[] { v }, 128);
        Assert.NotNull(res);
        var cls = res!.Extra[new LayerKey(0, CardFixtures.ClassPattern)];
        var reds = new HashSet<byte>();
        for (int i = 0; i < cls.Width * cls.Height; i++) reds.Add(cls.Pixels[4 * i]);
        Assert.Subset(new HashSet<byte> { 0, 1, 2 }, reds);
        Assert.Contains((byte)1, reds);
        Assert.Single(res.Cards);
        Assert.Equal(0, res.Cards[0].Cell);
        Assert.Equal(new Cards.CellRect(0, 0, 128, 128), res.Cells[0]);
    }

    [Theory]
    [InlineData("billboard", "billboard", 2, 1)] [InlineData("crossed4", "crossed", 16, 8)] [InlineData("cross2", "crossed", 8, 4)]
    [InlineData("star3", "crossed", 12, 6)] [InlineData("pixyz", "layered", 12, 6)] [InlineData("ground", "layered", 16, 8)]
    [InlineData("aerial", "layered", 18, 9)] [InlineData("box", "box", 10, 5)]
    public void NamedLayoutsMatchTheTable(string name, string mode, int tris, int pics)
    {
        var l = CardLayout.Named(name);
        Assert.Equal(mode, l.Mode); Assert.Equal(tris, l.Triangles); Assert.Equal(pics, l.Pictures);
        Assert.Equal(name, l.MatchesNamed()); Assert.Equal(name, l.Label);
    }

    [Fact]
    public void UnknownNameThrows() => Assert.Throws<ArgumentException>(() => CardLayout.Named("nope"));

    [Fact]
    public void BoxBottomAddsAFace()
    {
        var l = CardLayout.Resolve("box", new Dictionary<string, object> { ["box_bottom"] = true });
        Assert.Equal(12, l.Triangles); Assert.Equal(6, l.Pictures);
    }

    [Fact]
    public void CustomWithUntouchedRowsIsTodaysCrossed4()
    {
        var l = CardLayout.Resolve("custom", new Dictionary<string, object>());
        Assert.Equal(CardLayout.Named("crossed4").Rows().OrderBy(k => k.Key), l.Rows().OrderBy(k => k.Key));
        Assert.True(l.Legacy); Assert.Equal("crossed4", l.Label);
    }

    [Fact]
    public void BillboardAndCrossed4AreLegacy()
    {
        foreach (var n in new[] { "billboard", "crossed4", "cross2", "star3" }) Assert.True(CardLayout.Named(n).Legacy, n);
        foreach (var n in new[] { "pixyz", "ground", "aerial", "box" }) Assert.False(CardLayout.Named(n).Legacy, n);
        Assert.True(CardLayout.Default.Legacy);
        Assert.False((CardLayout.Default with { Back = "mirror" }).Legacy);
        Assert.False((CardLayout.Default with { Padding = "full" }).Legacy);
        Assert.False((CardLayout.Default with { Cells = "by_area" }).Legacy);
        Assert.False((CardLayout.Default with { Horizontal = 1 }).Legacy);
        Assert.True((CardLayout.Default with { AlphaMips = "coverage" }).Legacy);
    }

    [Fact]
    public void ChangingARowShowsCustomFrom()
    {
        var l = CardLayout.Resolve("ground", new Dictionary<string, object> { ["horizontal"] = 3 });
        Assert.Equal("custom (from ground)", l.Label); Assert.Equal(20, l.Triangles); Assert.Equal(10, l.Pictures);
    }

    [Fact]
    public void AutoHeightsFollowTheSpec()
    {
        Assert.Equal(new[] { 0.5 }, CardLayout.AutoHeights(1));
        Assert.Equal(new[] { 0.4, 0.7 }, CardLayout.AutoHeights(2));
        Assert.Equal(new[] { 0.3, 0.55, 0.8 }, CardLayout.AutoHeights(3));
    }

    static CardLayout WithHeights(string text, int horizontal, List<string> notes) =>
        CardLayout.Resolve("custom", new Dictionary<string, object> { ["mode"] = "layered", ["horizontal"] = horizontal, ["heights"] = text }, notes);

    [Fact]
    public void HeightsParsingIsForgiving()
    {
        var n = new List<string>();
        Assert.Equal(new[] { 0.05, 0.95 }, WithHeights("0.97, 0.01", 2, n).Heights); Assert.Empty(n);
        var a = WithHeights("0.4; x", 2, n);
        Assert.True(a.HeightsAuto); Assert.Equal(new[] { 0.4, 0.7 }, a.Heights); Assert.Contains(n, x => x.Contains("heights"));
        n.Clear();
        Assert.True(WithHeights("0.2, 0.5, 0.8", 2, n).HeightsAuto); Assert.Contains(n, x => x.Contains("heights"));
        n.Clear();
        Assert.Equal(new[] { 0.95 }, WithHeights("1.7", 1, n).Heights);
        Assert.Equal(new[] { 0.35, 0.6 }, WithHeights("0.6,0.35", 2, n).Heights);
        Assert.True(WithHeights("NaN", 1, n).HeightsAuto);
        Assert.True(WithHeights("", 1, n).HeightsAuto);
    }

    [Fact]
    public void RangesAreClamped()
    {
        CardLayout R(string k, int v) => CardLayout.Resolve("custom", new Dictionary<string, object> { [k] = v });
        Assert.Equal(1, R("planes", 0).Planes); Assert.Equal(8, R("planes", 12).Planes);
        Assert.Equal(3, R("horizontal", 5).Horizontal); Assert.Equal(0, R("horizontal", -1).Horizontal);
    }

    [Fact]
    public void CountLines()
    {
        Assert.Equal("3 vertical + 3 horizontal · 18 triangles · 9 pictures of 341 px", CardLayout.Named("aerial").CountLine(1024));
        Assert.Equal("4 vertical · 16 triangles · 8 pictures of 170 px", CardLayout.Named("crossed4").CountLine(512));
        Assert.Equal("1 billboard · 2 triangles · 1 picture of 512 px", CardLayout.Named("billboard").CountLine(512));
        Assert.Equal("box, 5 faces · 10 triangles · 5 pictures of 170 px", CardLayout.Named("box").CountLine(512));
    }

    [Fact]
    public void KeyIsTheCanonicalExpansion() =>
        Assert.Equal("mode=layered planes=2 horizontal=2 heights=0.4,0.7 slab=slice faces=both back=own box_bottom=false cells=equal padding=8 alpha_mips=renderer",
            CardLayout.Named("ground").Key);

    [Fact]
    public void ReplaceCardsKeysAreRegistered()
    {
        var ps = Optimizer.Passes["replace_cards"].Params;
        Assert.Equal(new[] { "mode", "planes", "texture", "share", "extra_fill", "class_fill" }, ps.Take(6).Select(p => p.Key).ToArray());
        Assert.Equal("Cards", ps[0].Label); Assert.Equal("Crossed: planes", ps[1].Label); Assert.Equal("Texture size (pixels)", ps[2].Label);
        Assert.Equal("crossed", ps[0].Default); Assert.Equal(4, ps[1].Default); Assert.Equal(512, ps[2].Default);
        Assert.Equal(true, ps[3].Default); Assert.Equal("white", ps[4].Default); Assert.Equal(0, ps[5].Default);
        Assert.Equal(new[] { "crossed", "billboard", "layered", "box" }, ps[0].Choices);
        object D(string k) => ps.Single(p => p.Key == k).Default;
        Assert.Equal("custom", D("layout")); Assert.Equal(0, D("horizontal")); Assert.Equal("auto", D("heights")); Assert.Equal("full", D("slab"));
        Assert.Equal("both", D("faces")); Assert.Equal("own", D("back")); Assert.Equal(false, D("box_bottom")); Assert.Equal("equal", D("cells"));
        Assert.Equal("8", D("padding")); Assert.Equal("renderer", D("alpha_mips"));
        Assert.Equal(16, ps.Length);
    }

    [Fact]
    public void ContextHasOnlyWhatWasSet()
    {
        var ctx = new OptContext(new Scene("x.osgt", "osgt"), new Dictionary<string, object> { ["replace_cards.horizontal"] = 2, ["back"] = "mirror" });
        Assert.True(ctx.Has("replace_cards", "horizontal")); Assert.True(ctx.Has("replace_cards", "back")); Assert.False(ctx.Has("replace_cards", "slab"));
        var l = CardLayout.FromContext(ctx);
        Assert.Equal(2, l.Horizontal); Assert.Equal("mirror", l.Back);
    }

    [Fact]
    public void AerialKeepsAutoHeights()
    {
        Assert.True(CardLayout.Named("aerial").HeightsAuto);
        Assert.Equal("auto", CardLayout.Named("aerial").Rows()["heights"]);
    }

    [Fact]
    public void KeyOmitsPlanesWhereTheyAreIgnored()
    {
        Assert.DoesNotContain("planes=", CardLayout.Named("box").Key);
        Assert.DoesNotContain("planes=", CardLayout.Named("billboard").Key);
        Assert.Contains("planes=2 ", CardLayout.Named("cross2").Key);
        Assert.Equal(CardLayout.Named("box").Key, (CardLayout.Named("box") with { Planes = 7 }).Key);
    }

    // ---- R3-C4: BuildCards from the layout ------------------------------------------------------------------------

    public static IEnumerable<object[]> AllNames => CardLayout.Names.Select(n => new object[] { n });

    /// <summary>One card quad: four corners (x, y, z), their UVs, and the winding normal of its first triangle.</summary>
    sealed record CardQuad(double[][] P, double[][] Uv, double[] Normal)
    {
        public double UMin => Uv.Min(q => q[0]);
        public double UMax => Uv.Max(q => q[0]);
        public double VMin => Uv.Min(q => q[1]);
        public double VMax => Uv.Max(q => q[1]);
        public bool Flat => P.All(p => Math.Abs(p[2] - P[0][2]) < 1e-9);
    }

    static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    static double DotV(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    static double[] CrossV(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

    /// <summary>The card geometry (named "... cards") in the scene, after a replace_cards run.</summary>
    static Node CardGeometry(Scene sc) => Assert.Single(sc.IterNodes().Where(n => n.Kind == Kind.Geometry && n.Name.EndsWith(" cards")).ToList());

    static List<CardQuad> Quads(Node geo)
    {
        var g = geo.Geom!;
        var V = g.Vertices!.Data; var UV = g.TexCoords[0].Data; var T = g.Triangles();
        Assert.Equal(0, V.Length / 3 % 4);
        Assert.Equal(T.Length, V.Length / 3 / 4 * 6);
        var o = new List<CardQuad>();
        for (int q = 0; q < V.Length / 12; q++)
        {
            var P = Enumerable.Range(4 * q, 4).Select(i => new[] { V[3 * i], V[3 * i + 1], V[3 * i + 2] }).ToArray();
            var U = Enumerable.Range(4 * q, 4).Select(i => new[] { UV[2 * i], UV[2 * i + 1] }).ToArray();
            int a = T[6 * q], b = T[6 * q + 1], c = T[6 * q + 2];
            Assert.All(new[] { a, b, c }, i => Assert.InRange(i, 4 * q, 4 * q + 3));
            var pa = new[] { V[3 * a], V[3 * a + 1], V[3 * a + 2] };
            var n = CrossV(Sub(new[] { V[3 * b], V[3 * b + 1], V[3 * b + 2] }, pa), Sub(new[] { V[3 * c], V[3 * c + 1], V[3 * c + 2] }, pa));
            o.Add(new CardQuad(P, U, n));
        }
        return o;
    }

    static RawImage Atlas(Node geo) => geo.StateSet!.Textures.Single(t => t.Unit == 0).Baked!;

    /// <summary>The UV at a point on the quad's plane (UVs are affine on a card).</summary>
    static (double u, double v) UvAt(CardQuad q, double[] x)
    {
        double[] e1 = Sub(q.P[1], q.P[0]), e2 = Sub(q.P[3], q.P[0]), w = Sub(x, q.P[0]);
        double d00 = DotV(e1, e1), d01 = DotV(e1, e2), d11 = DotV(e2, e2), d20 = DotV(w, e1), d21 = DotV(w, e2);
        double den = d00 * d11 - d01 * d01, s = (d11 * d20 - d01 * d21) / den, t = (d00 * d21 - d01 * d20) / den;
        return (q.Uv[0][0] + s * (q.Uv[1][0] - q.Uv[0][0]) + t * (q.Uv[3][0] - q.Uv[0][0]),
                q.Uv[0][1] + s * (q.Uv[1][1] - q.Uv[0][1]) + t * (q.Uv[3][1] - q.Uv[0][1]));
    }

    static byte[] TexelAt(RawImage img, double u, double v)
    {
        int x = Math.Clamp((int)Math.Floor(u * img.Width), 0, img.Width - 1), y = Math.Clamp((int)Math.Floor((1 - v) * img.Height), 0, img.Height - 1);
        int i = 4 * (y * img.Width + x);
        return img.Pixels.Skip(i).Take(4).ToArray();
    }

    /// <summary>Covered texels of the quad's cell (its UV rect widened by the half-texel inset) close to (r, g, b).</summary>
    static int CellTexels(RawImage img, CardQuad q, int r, int g, int b)
    {
        int x0 = (int)Math.Round(q.UMin * img.Width - 0.5), x1 = (int)Math.Round(q.UMax * img.Width + 0.5);
        int y0 = (int)Math.Round((1 - q.VMax) * img.Height - 0.5), y1 = (int)Math.Round((1 - q.VMin) * img.Height + 0.5);
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = 4 * (y * img.Width + x);
                if (img.Pixels[i + 3] == 0) continue;
                if (Math.Abs(img.Pixels[i] - r) < 40 && Math.Abs(img.Pixels[i + 1] - g) < 40 && Math.Abs(img.Pixels[i + 2] - b) < 40) n++;
            }
        return n;
    }

    static PassResult RunCards(Scene sc, Node target, Dictionary<string, object> prms)
    {
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, prms, targets: new List<Node> { target });
        return results[0];
    }

    /// <summary>The MadeFrom note the pass left on a new node.</summary>
    static string Note(PassResult r, Node n) => r.Changes.First(c => ReferenceEquals(c.Node, n)).Note;

    static Node ColourGeo(string name, (double[] V, int[] T) m, double r, double g, double b)
    {
        var gd = new GeomData { Vertices = new VArray(m.V, 3, "Vec3Array") };
        gd.Prims.Add(new Primitive("TRIANGLES", "elements", count: m.T.Length, indices: m.T, indexType: "DrawElementsUInt"));
        return new Node(Kind.Geometry, "osg::Geometry", name) { Geom = gd, StateSet = new StateSet { MaterialName = name, Diffuse = new[] { r, g, b, 1.0 } } };
    }

    static (double[] V, int[] T) Flat(double x0, double y0, double x1, double y1, double z) =>
        (new[] { x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z }, new[] { 0, 1, 2, 0, 2, 3 });

    static (Scene sc, Node obj) SceneOf(string name, params Node[] geos)
    {
        var obj = new Node(Kind.Geode, "osg::Geode", name);
        obj.Children.AddRange(geos);
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { obj } };
        return (new Scene("x.osgt", "osgt") { Root = root }, obj);
    }

    [Fact]
    public void LayeredQuadsSitAtTheirHeights()
    {
        var (sc, tree) = ReplaceTests.Tree();
        var r = RunCards(sc, tree, new() { ["replace_cards.layout"] = "ground", ["replace_cards.texture"] = 64 });
        Assert.Null(r.Error);
        var geo = CardGeometry(sc);
        Assert.Equal(16, geo.Geom!.Triangles().Length / 3);
        var quads = Quads(geo);
        Assert.Equal(8, quads.Count);
        var flat = quads.Where(q => q.Flat).ToList();
        Assert.Equal(4, flat.Count);
        Assert.Equal(2, flat.Count(q => Math.Abs(q.P[0][2] - 2.4) < 1e-9));
        Assert.Equal(2, flat.Count(q => Math.Abs(q.P[0][2] - 4.2) < 1e-9));
        foreach (var q in quads.Where(q => !q.Flat))
        {
            Assert.Equal(0, q.P.Min(p => p[2]), 9);
            Assert.Equal(6, q.P.Max(p => p[2]), 9);
        }
        Assert.Equal(8, quads.Select(q => (q.UMin, q.UMax, q.VMin, q.VMax)).Distinct().Count());
        Assert.Contains("replaced with ground cards", r.Message);
        Assert.StartsWith("ground: 2 + 2 cards, 16 triangles, 8 pictures in 64 px; layout=", Note(r, geo));
    }

    [Theory]
    [MemberData(nameof(AllNames))]
    public void EveryQuadUvLiesInsideItsCell(string name)
    {
        var (sc, tree) = ReplaceTests.Tree();
        var r = RunCards(sc, tree, new() { ["replace_cards.layout"] = name, ["replace_cards.texture"] = 64 });
        Assert.Null(r.Error);
        var geo = CardGeometry(sc);
        int S = Atlas(geo).Width;
        Assert.Equal(64, S);
        int n = CardLayout.Named(name).Pictures, cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (n + cols - 1) / cols, cw = S / cols, ch = S / rows;
        var used = new List<int>();
        foreach (var q in Quads(geo))
        {
            double x0 = q.UMin * S - 0.5, x1 = q.UMax * S + 0.5, y0 = (1 - q.VMax) * S - 0.5, y1 = (1 - q.VMin) * S + 0.5;
            int k = Enumerable.Range(0, n).FirstOrDefault(k => Math.Abs(x0 - k % cols * cw) < 1e-6 && Math.Abs(y0 - k / cols * ch) < 1e-6
                && Math.Abs(x1 - x0 - cw) < 1e-6 && Math.Abs(y1 - y0 - ch) < 1e-6, -1);
            Assert.True(k >= 0, $"{name}: quad UV rect ({x0}, {y0})-({x1}, {y1}) is not a cell of the {cols} x {rows} grid");
            used.Add(k);
        }
        Assert.Equal(used.Count, used.Distinct().Count());   // every quad its own cell: the rects are disjoint
        Assert.Equal(n, used.Count);
    }

    static (double[] lo, double[] hi) Bounds(Node n)
    {
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var g in n.Children)
        {
            var V = g.Geom!.Vertices!.Data;
            foreach (int i in g.Geom.Triangles())
                for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], V[3 * i + c]); hi[c] = Math.Max(hi[c], V[3 * i + c]); }
        }
        return (lo, hi);
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 6)]
    public void BoxFacesLieOnTheBoxAndFaceOutward(bool bottom, int faces)
    {
        var (sc, building) = CardFixtures.Building();
        var (lo, hi) = Bounds(building);
        var ctr = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 };
        var prm = new Dictionary<string, object> { ["replace_cards.layout"] = "box", ["replace_cards.texture"] = 64 };
        if (bottom) prm["replace_cards.box_bottom"] = true;
        var r = RunCards(sc, building, prm);
        Assert.Null(r.Error);
        var geo = CardGeometry(sc);
        var quads = Quads(geo);
        Assert.Equal(faces, quads.Count);
        Assert.Equal(2 * faces, geo.Geom!.Triangles().Length / 3);
        var seen = new HashSet<(int axis, bool high)>();
        foreach (var q in quads)
        {
            var on = new List<(int, bool)>();
            for (int c = 0; c < 3; c++)
            {
                if (q.P.All(p => Math.Abs(p[c] - lo[c]) < 1e-9)) on.Add((c, false));
                if (q.P.All(p => Math.Abs(p[c] - hi[c]) < 1e-9)) on.Add((c, true));
            }
            var face = Assert.Single(on);
            Assert.True(seen.Add(face), $"two quads on face {face}");
            var mid = new[] { q.P.Average(p => p[0]), q.P.Average(p => p[1]), q.P.Average(p => p[2]) };
            Assert.True(DotV(q.Normal, Sub(mid, ctr)) > 0, $"quad on face {face} faces inward");
        }
        Assert.Equal(bottom, seen.Contains((2, false)));
        Assert.Contains((2, true), seen);
        if (!bottom)
        {
            Assert.Contains("replaced with box cards", r.Message);
            Assert.StartsWith("box: 5 faces, 10 triangles, 5 pictures in 64 px; layout=", Note(r, geo));
        }
    }

    [Fact]
    public void BottomQuadIsNotMirrored()
    {
        var (sc, obj) = SceneOf("marker",
            ColourGeo("box", OcclusionTests.Box(-1, -1, 0, 1, 1, 2), 1, 1, 1),
            ColourGeo("red", Flat(0.4, -0.2, 0.9, 0.2, -0.01), 1, 0, 0),
            ColourGeo("blue", Flat(-0.2, 0.4, 0.2, 0.9, -0.01), 0, 0, 1));
        var r = RunCards(sc, obj, new() { ["replace_cards.layout"] = "pixyz", ["replace_cards.texture"] = 128 });
        Assert.Null(r.Error);
        var geo = CardGeometry(sc);
        var atlas = Atlas(geo);
        var bottom = Assert.Single(Quads(geo).Where(q => q.Flat && q.Normal[2] < 0).ToList());
        double z = bottom.P[0][2];
        Assert.Equal(-0.01 + 0.5 * 2.01, z, 9);
        bool Is(double x, double y, int cr, int cg, int cb)
        {
            var (u, v) = UvAt(bottom, new[] { x, y, z });
            var t = TexelAt(atlas, u, v);
            return t[3] > 0 && Math.Abs(t[0] - cr) < 40 && Math.Abs(t[1] - cg) < 40 && Math.Abs(t[2] - cb) < 40;
        }
        Assert.True(Is(0.65, 0, 255, 0, 0), "+x is not red");
        Assert.True(Is(0, 0.65, 0, 0, 255), "+y is not blue");
        Assert.True(Is(-0.65, 0, 255, 255, 255), "-x is not white");
        Assert.True(Is(0, -0.65, 255, 255, 255), "-y is not white");
    }

    [Fact]
    public void SliceShowsALeafOnlyInItsLayer()
    {
        // trunk 0..4, a green leaf at 0.75 of the height (z 3); ground: planes at 1.6 and 2.8, the slices split at 2.2
        var (sc, obj) = SceneOf("plant",
            ColourGeo("trunk", OcclusionTests.Box(-0.1, -0.1, 0, 0.1, 0.1, 4), 0.45, 0.3, 0.2),
            ColourGeo("leaf", Flat(-1, -1, 1, 1, 3), 0, 1, 0));
        var r = RunCards(sc, obj, new() { ["replace_cards.layout"] = "ground", ["replace_cards.texture"] = 128 });
        Assert.Null(r.Error);
        var geo = CardGeometry(sc);
        var atlas = Atlas(geo);
        var quads = Quads(geo);
        var upperTop = Assert.Single(quads.Where(q => q.Flat && Math.Abs(q.P[0][2] - 2.8) < 1e-9 && q.Normal[2] > 0).ToList());
        Assert.True(CellTexels(atlas, upperTop, 0, 255, 0) > 100, "no leaf in the upper plane's top picture");
        var lower = quads.Where(q => q.Flat && Math.Abs(q.P[0][2] - 1.6) < 1e-9).ToList();
        Assert.Equal(2, lower.Count);
        foreach (var q in lower) Assert.Equal(0, CellTexels(atlas, q, 0, 255, 0));
        Assert.True(CellTexels(atlas, lower.Single(q => q.Normal[2] > 0), 115, 77, 51) > 0, "the lower slice shows the trunk");
        foreach (var q in quads.Where(q => !q.Flat)) Assert.Equal(0, CellTexels(atlas, q, 0, 255, 0));   // the leaf is seen edge-on
        // faces both: the upper plane's bottom picture sees the leaf from below (its slab reaches up to the top)
        var upperBottom = Assert.Single(quads.Where(q => q.Flat && Math.Abs(q.P[0][2] - 2.8) < 1e-9 && q.Normal[2] < 0).ToList());
        Assert.True(CellTexels(atlas, upperBottom, 0, 255, 0) > 100, "no leaf in the upper plane's bottom picture");
    }

    [Fact]
    public void FlatObjectIsSkippedWithoutNaN()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"skcl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string png = Path.Combine(dir, "leaf.png");
            var img = CardFixtures.LeafImage();
            File.WriteAllBytes(png, ImageCodecs.WritePng(img));
            var quad = ColourGeo("decal", Flat(0, 0, 2, 2, 1), 1, 1, 1);
            quad.Geom!.TexCoords[0] = new VArray(new[] { 0.0, 0, 1, 0, 1, 1, 0, 1 }, 2, "Vec2Array");
            quad.StateSet!.Textures.Add(new Texture { File = "leaf.png", Path = png, Exists = true, Unit = 0, Width = img.Width, Height = img.Height, Channels = 4, Fmt = "png" });
            var (sc, obj) = SceneOf("flat", quad);
            string before = CardGolden.Hash(quad.Geom);
            var r = RunCards(sc, obj, new() { ["replace_cards.layout"] = "aerial", ["replace_cards.texture"] = 64 });
            Assert.Null(r.Error);
            Assert.Contains("skipped", r.Message);
            Assert.Contains("flat skipped: it is flat", r.Message);
            Assert.DoesNotContain("nothing drawn", r.Message);   // the flat reason, not the generic skip text
            Assert.DoesNotContain("NaN", r.Message);
            Assert.Same(obj, Assert.Single(sc.Root!.Children));
            Assert.Same(quad, Assert.Single(obj.Children));
            Assert.Equal(before, CardGolden.Hash(quad.Geom));
            Assert.All(sc.IterNodes().Where(n => n.Geom != null).SelectMany(n => n.Geom!.Vertices!.Data), x => Assert.True(double.IsFinite(x)));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void BoxOnAFlatFootprintLeavesOutZeroAreaFaces()
    {
        // a textured quad standing upright in the x-z plane (no depth along y): only the two faces looking along y have area
        string dir = Path.Combine(Path.GetTempPath(), $"skcl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string png = Path.Combine(dir, "leaf.png");
            var img = CardFixtures.LeafImage();
            File.WriteAllBytes(png, ImageCodecs.WritePng(img));
            var quad = ColourGeo("sign", (new[] { 0.0, 0, 0, 2, 0, 0, 2, 0, 2, 0, 0, 2 }, new[] { 0, 1, 2, 0, 2, 3 }), 1, 1, 1);
            quad.Geom!.TexCoords[0] = new VArray(new[] { 0.0, 0, 1, 0, 1, 1, 0, 1 }, 2, "Vec2Array");
            quad.StateSet!.Textures.Add(new Texture { File = "leaf.png", Path = png, Exists = true, Unit = 0, Width = img.Width, Height = img.Height, Channels = 4, Fmt = "png" });
            var (sc, obj) = SceneOf("sign", quad);
            var r = RunCards(sc, obj, new() { ["replace_cards.layout"] = "box", ["replace_cards.texture"] = 64 });
            Assert.Null(r.Error);
            Assert.DoesNotContain("NaN", r.Message);
            var geo = CardGeometry(sc);
            var g = geo.Geom!;
            Assert.All(g.Vertices!.Data, x => Assert.True(double.IsFinite(x)));
            Assert.All(g.TexCoords[0].Data, x => Assert.True(double.IsFinite(x)));
            var quads = Quads(geo);
            Assert.Equal(2, quads.Count);
            foreach (var q in quads)
            {
                double area = Math.Sqrt(DotV(q.Normal, q.Normal));
                Assert.True(area > 1, $"quad area {area}");
                Assert.True(Math.Abs(q.Normal[1]) > 0.99 * area, "a face of a sign standing in the x-z plane looks along y");
                Assert.All(q.P, p => Assert.Equal(0, p[1], 9));
            }
            Assert.StartsWith("box: 2 faces, 4 triangles, 2 pictures in 64 px; layout=", Note(r, geo));
            Assert.Contains("3 box faces left out", r.Message);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void BoxOnAPoleIsSkippedWithAReason()
    {
        // all on the z axis: height, but no footprint, so no box face has area and nothing is rendered
        var (sc, obj) = SceneOf("pole", ColourGeo("pole", (new[] { 0.0, 0, 0, 0, 0, 1, 0, 0, 2 }, new[] { 0, 1, 2 }), 1, 1, 1));
        var r = RunCards(sc, obj, new() { ["replace_cards.layout"] = "box", ["replace_cards.texture"] = 64 });
        Assert.Null(r.Error);
        Assert.Contains("pole skipped: it has no footprint", r.Message);
        Assert.DoesNotContain("nothing drawn", r.Message);
        Assert.Same(obj, Assert.Single(sc.Root!.Children));
    }

    [Fact]
    public void ZeroAreaExtentIsRejected()
    {
        var items = new List<Cards.Item> { Mesh(OcclusionTests.Box(-1, -1, 0, 1, 1, 2), 1, 1, 1) };
        var flatA = Cards.Top(); flatA.Extent = (0.5, 0.5, -1, 1);
        var flatB = Cards.Vertical(0); flatB.Extent = (-1, 1, 2, 2);
        Assert.Throws<ArgumentException>(() => Cards.Render(items, new List<Cards.View> { flatA }, 64));
        Assert.Throws<ArgumentException>(() => Cards.Render(items, new List<Cards.View> { flatB }, 64));
        Assert.Throws<ArgumentException>(() => Cards.Snapshot(items, Cards.Top(), new[] { 0.0, 0, 0 }, (1, 1, -1, 1), 8, 8));
    }

    [Fact]
    public void TopOnlyFacesWarnAndNameTheLowestPlane()
    {
        var (sc, tree) = ReplaceTests.Tree();
        var r = RunCards(sc, tree, new() { ["replace_cards.layout"] = "aerial", ["replace_cards.texture"] = 64 });
        Assert.Null(r.Error);
        Assert.Contains("the crown's underside is not baked: from below, horizontal planes are invisible", r.Message);
        Assert.Contains("lowest horizontal plane 1.8 m above the base", r.Message);
        Assert.Contains("replaced with aerial cards", r.Message);
        var geo = CardGeometry(sc);
        Assert.Equal(18, geo.Geom!.Triangles().Length / 3);
        Assert.StartsWith("aerial: 3 + 3 cards, 18 triangles, 9 pictures in 64 px; layout=", Note(r, geo));
    }

    [Fact]
    public void GroundEndToEndWithClassMapsSavesToFlt()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"skcl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // an OpenFlight host with the textured tree grafted in (as CardGoldenTests.FltCards): its images sit next to the .flt
            string host = Path.Combine(dir, "host.flt");
            File.WriteAllBytes(host, FltWriteTests.Grid(4));
            var sc = SceneIO.Load(host);
            var tree = CardFixtures.TexturedTreeNode(dir, classMaps: true, unit1: true);
            sc.Root!.Children.Add(tree);
            Companions.Detect(sc);
            var hashes = tree.Children.Select(c => CardGolden.Hash(c.Geom!)).ToList();
            var snap = new Snapshot(sc, "before");
            var r = RunCards(sc, tree, new() { ["replace_cards.layout"] = "ground", ["replace_cards.texture"] = 128 });
            Assert.Null(r.Error);
            Assert.Contains("replaced with ground cards", r.Message);
            var geo = CardGeometry(sc);
            Assert.Equal(16, geo.Geom!.Triangles().Length / 3);
            Assert.Equal("ON", geo.StateSet!.Modes["GL_ALPHA_TEST"]);

            string outp = Path.Combine(dir, "x.flt");
            SceneIO.Save(sc, outp);
            string texDir = Path.Combine(dir, "x_textures");
            var files = Directory.GetFiles(texDir).Select(f => Path.GetFileName(f)).ToList();
            string? Find(Func<string, bool> f) => files.FirstOrDefault(x => x.StartsWith("tree_cards") && !x.EndsWith(".attr") && f(x));
            var baseFile = Find(x => !x.Contains("_u1") && !x.Contains("Classes"));
            var unit1 = Find(x => x.Contains("_u1") && !x.Contains("Classes"));
            var classes = Find(x => x.Contains("Classes"));
            Assert.True(baseFile != null && unit1 != null && classes != null, string.Join(", ", files));
            var a = ImageCodecs.Read(Path.Combine(texDir, baseFile!))!;
            var u1 = ImageCodecs.Read(Path.Combine(texDir, unit1!))!;
            var cls = ImageCodecs.Read(Path.Combine(texDir, classes!))!;
            Assert.Equal(128, a.Width);
            // OpenFlight keeps no alpha-test mode: the cut-out travels in the atlas alpha (transparent and opaque texels)
            Assert.Equal(4, a.Channels);
            Assert.Contains(Enumerable.Range(0, a.Width * a.Height), i => a.Pixels[4 * i + 3] == 0);
            Assert.Contains(Enumerable.Range(0, a.Width * a.Height), i => a.Pixels[4 * i + 3] == 255);
            Assert.Equal((a.Width, a.Height), (u1.Width, u1.Height));
            var reds = new HashSet<byte>();
            for (int i = 0; i < cls.Width * cls.Height; i++) reds.Add(cls.Pixels[cls.Channels * i]);
            Assert.Subset(new HashSet<byte> { 0, 1, 2 }, reds);
            Assert.Contains((byte)1, reds);

            var back = SceneIO.Load(outp);
            var cards = back.IterNodes().Where(n => n.Geom != null && n.Name.Contains("tree")).ToList();
            Assert.Equal(16, cards.Sum(n => (long)n.Geom!.Triangles().Length / 3));
            Assert.All(cards, n => Assert.Contains(n.StateSet!.Textures, t => t.Unit == 0 && t.File.Contains("tree_cards")));

            snap.Restore();
            Assert.Contains(tree, sc.Root!.Children);
            Assert.Equal(hashes, tree.Children.Select(c => CardGolden.Hash(c.Geom!)).ToList());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---- R3-C5: back = mirror ----

    static int DistinctCells(List<CardQuad> quads) =>
        quads.Select(q => (Math.Round(q.UMin, 9), Math.Round(q.UMax, 9), Math.Round(q.VMin, 9), Math.Round(q.VMax, 9))).Distinct().Count();

    [Fact]
    public void MirrorBackReusesTheFrontCellFlipped()
    {
        var (sc, tree) = ReplaceTests.Tree();
        var r = RunCards(sc, tree, new()
        {
            ["replace_cards.mode"] = "crossed", ["replace_cards.planes"] = 4, ["replace_cards.back"] = "mirror", ["replace_cards.texture"] = 64,
        });
        Assert.Null(r.Error);
        var geo = CardGeometry(sc);
        Assert.Equal(16, geo.Geom!.Triangles().Length / 3);
        var quads = Quads(geo);
        Assert.Equal(8, quads.Count);
        Assert.Equal(4, DistinctCells(quads));
        // quads come as front, back per plane: same corners, opposite normal, the cell's UVs with U0 and U1 swapped
        for (int i = 0; i < quads.Count; i += 2)
        {
            CardQuad f = quads[i], b = quads[i + 1];
            for (int k = 0; k < 4; k++) for (int c = 0; c < 3; c++) Assert.Equal(f.P[k][c], b.P[k][c], 9);
            Assert.True(DotV(f.Normal, b.Normal) < 0, "the back quad does not face the other way");
            for (int k = 0; k < 4; k++)
            {
                Assert.Equal(f.UMin + f.UMax - f.Uv[k][0], b.Uv[k][0], 9);
                Assert.Equal(f.Uv[k][1], b.Uv[k][1], 9);
            }
            Assert.Equal(f.VMin, b.VMin, 9);
            Assert.Equal(f.VMax, b.VMax, 9);
        }
    }

    [Theory]
    [InlineData("cross2", 8, 2)]
    [InlineData("ground", 16, 6)]
    public void MirrorHalvesTheVerticalCells(string name, int tris, int cells)
    {
        var (sc, tree) = ReplaceTests.Tree();
        var r = RunCards(sc, tree, new() { ["replace_cards.layout"] = name, ["replace_cards.back"] = "mirror", ["replace_cards.texture"] = 64 });
        Assert.Null(r.Error);
        var geo = CardGeometry(sc);
        Assert.Equal(tris, geo.Geom!.Triangles().Length / 3);
        Assert.Equal(cells, DistinctCells(Quads(geo)));
        Assert.Contains($"{cells} pictures", Note(r, geo));
    }

    [Fact]
    public void MirroredClassMapStaysIds()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"skcl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string host = Path.Combine(dir, "host.flt");
            File.WriteAllBytes(host, FltWriteTests.Grid(4));
            var sc = SceneIO.Load(host);
            var tree = CardFixtures.TexturedTreeNode(dir, classMaps: true, unit1: true);
            sc.Root!.Children.Add(tree);
            Companions.Detect(sc);
            var r = RunCards(sc, tree, new() { ["replace_cards.layout"] = "ground", ["replace_cards.back"] = "mirror", ["replace_cards.texture"] = 128 });
            Assert.Null(r.Error);
            SceneIO.Save(sc, Path.Combine(dir, "x.flt"));
            string texDir = Path.Combine(dir, "x_textures");
            var cf = Directory.GetFiles(texDir).Single(f => Path.GetFileName(f).StartsWith("tree_cards") && Path.GetFileName(f).Contains("Classes") && !f.EndsWith(".attr"));
            var cls = ImageCodecs.Read(cf)!;
            var reds = new HashSet<byte>();
            for (int i = 0; i < cls.Width * cls.Height; i++) reds.Add(cls.Pixels[cls.Channels * i]);
            Assert.Subset(new HashSet<byte> { 0, 1, 2 }, reds);
            Assert.Contains((byte)1, reds);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---- R3-C6: by_area packing, power-of-two atlas, padding full ----

    /// <summary>The cell (texels) a quad's UV rectangle covers: half a texel inside the cell on every side.</summary>
    static Cards.CellRect CellOf(CardQuad q, int S) =>
        new((int)Math.Round(q.UMin * S - 0.5), (int)Math.Round((1 - q.VMax) * S - 0.5), (int)Math.Round((q.UMax - q.UMin) * S + 1), (int)Math.Round((q.VMax - q.VMin) * S + 1));

    static bool Overlap(Cards.CellRect a, Cards.CellRect b) => a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    [Fact]
    public void ByAreaGivesThinHorizontalCellsFewerTexels()
    {
        // a 1 x 1 x 10 post: its top / ground planes are 1 x 1, its vertical planes 1.4 x 10
        var (sc, obj) = SceneOf("post", ColourGeo("post", OcclusionTests.Box(0, 0, 0, 1, 1, 10), 0.2, 0.8, 0.2));
        var r = RunCards(sc, obj, new() { ["replace_cards.layout"] = "ground", ["replace_cards.cells"] = "by_area", ["replace_cards.texture"] = 256 });
        Assert.Null(r.Error);
        var geo = CardGeometry(sc);
        int S = Atlas(geo).Width;
        var quads = Quads(geo);
        var flat = quads.Where(q => q.Flat).Select(q => CellOf(q, S)).ToList();
        var tall = quads.Where(q => !q.Flat).Select(q => CellOf(q, S)).ToList();
        Assert.NotEmpty(flat); Assert.NotEmpty(tall);
        Assert.All(flat, f => Assert.All(tall, t => Assert.True(f.W * f.H < t.W * t.H, $"{f} vs {t}")));
    }

    [Fact]
    public void CellsNeverOverlapAndFit()
    {
        var rnd = new Random(12345);
        for (int run = 0; run < 50; run++)
        {
            int n = rnd.Next(1, 13), size = new[] { 64, 128, 256, 512 }[rnd.Next(4)];
            var planes = Enumerable.Range(0, n).Select(_ => (W: 0.2 + rnd.NextDouble() * 10, H: 0.2 + rnd.NextDouble() * 10)).ToList();
            var cells = Cards.Pack(planes, size, out double scale);
            Assert.Equal(n, cells.Count);
            Assert.True(scale > 0);
            for (int i = 0; i < n; i++)
            {
                var c = cells[i];
                Assert.True(c.W >= 1 && c.H >= 1 && c.X >= 0 && c.Y >= 0 && c.X + c.W <= size && c.Y + c.H <= size, $"run {run}: {c}");
                for (int j = i + 1; j < n; j++) Assert.False(Overlap(c, cells[j]), $"run {run}: {c} overlaps {cells[j]}");
            }
            Assert.True(cells.Sum(c => (long)c.W * c.H) <= (long)size * size);
        }
    }

    [Fact]
    public void NewLayoutsUseAPowerOfTwoAtlas()
    {
        var (sc, tree) = ReplaceTests.Tree();
        Assert.Null(RunCards(sc, tree, new() { ["replace_cards.layout"] = "ground", ["replace_cards.texture"] = 300 }).Error);
        var a = Atlas(CardGeometry(sc));
        Assert.Equal((512, 512), (a.Width, a.Height));

        var (sc2, tree2) = ReplaceTests.Tree();
        Assert.Null(RunCards(sc2, tree2, new() { ["replace_cards.layout"] = "crossed4", ["replace_cards.texture"] = 300 }).Error);
        var b = Atlas(CardGeometry(sc2));
        Assert.Equal((300, 300), (b.Width, b.Height));
    }

    [Fact]
    public void TinyAtlasStillPacksEveryPicture()
    {
        var (sc, tree) = ReplaceTests.Tree();
        var r = RunCards(sc, tree, new() { ["replace_cards.layout"] = "aerial", ["replace_cards.cells"] = "by_area", ["replace_cards.texture"] = 16 });
        Assert.Null(r.Error);
        Assert.Contains("smaller than 8 px", r.Message);
        var geo = CardGeometry(sc);
        int S = Atlas(geo).Width;
        Assert.Equal(16, S);
        var cells = Quads(geo).Select(q => CellOf(q, S)).ToList();
        Assert.Equal(9, cells.Count);
        Assert.All(cells, c => Assert.True(c.W >= 1 && c.H >= 1 && c.X >= 0 && c.Y >= 0 && c.X + c.W <= S && c.Y + c.H <= S, c.ToString()));
        for (int i = 0; i < cells.Count; i++)
            for (int j = i + 1; j < cells.Count; j++) Assert.False(Overlap(cells[i], cells[j]), $"{cells[i]} overlaps {cells[j]}");
    }

    [Fact]
    public void FullPaddingFloodsTheCell()
    {
        var items = CardFixtures.Items();
        var (lo, hi) = Cards.Bounds(items);
        var views = CardLayout.Named("ground").Views(lo, hi).ToList();
        var eight = Cards.Render(items, views, 128)!;
        var full = Cards.Render(items, views, 128, padding: Cards.Padding.Full)!;
        Assert.Equal(eight.Cells, full.Cells);
        var A = full.Atlas;
        int flooded = 0;
        foreach (var c in full.Cells)
        {
            var covered = new List<(int x, int y)>();
            for (int y = c.Y; y < c.Y + c.H; y++)
                for (int x = c.X; x < c.X + c.W; x++)
                    if (A.Pixels[4 * (y * A.Width + x) + 3] > 0) covered.Add((x, y));
            if (covered.Count == 0) continue;
            for (int y = c.Y; y < c.Y + c.H; y++)
                for (int x = c.X; x < c.X + c.W; x++)
                {
                    int i = 4 * (y * A.Width + x);
                    // alpha is the coverage of the eight-texel render: flooding never touches it
                    Assert.Equal(eight.Atlas.Pixels[i + 3], A.Pixels[i + 3]);
                    if (A.Pixels[i + 3] > 0) { Assert.Equal(eight.Atlas.Pixels[i], A.Pixels[i]); continue; }
                    int best = covered.Min(p => Math.Abs(p.x - x) + Math.Abs(p.y - y));
                    bool match = covered.Where(p => Math.Abs(p.x - x) + Math.Abs(p.y - y) == best).Any(p =>
                    {
                        int j = 4 * (p.y * A.Width + p.x);
                        return A.Pixels[i] == A.Pixels[j] && A.Pixels[i + 1] == A.Pixels[j + 1] && A.Pixels[i + 2] == A.Pixels[j + 2];
                    });
                    Assert.True(match, $"texel ({x}, {y}) is not the colour of a nearest covered texel");
                    flooded++;
                }
        }
        Assert.True(flooded > 0);
        // class maps: IDs only, copied
        var classKey = items.SelectMany(it => it.Extra).First(kv => kv.Value.ClassMap).Key;
        var cls = full.Extra[classKey];
        var reds = new HashSet<byte>();
        for (int i = 0; i < cls.Width * cls.Height; i++) reds.Add(cls.Pixels[4 * i]);
        Assert.Subset(new HashSet<byte> { 0, 1, 2 }, reds);
        Assert.Contains((byte)1, reds);
    }

    // ------------------------------------------------------------------ R3-C7: billboard + crown planes as one group

    static readonly Dictionary<string, object> BillboardCrown = new() { ["replace_cards.mode"] = "billboard", ["replace_cards.horizontal"] = 1, ["replace_cards.texture"] = 64 };

    /// <summary>The group a billboard with crown planes becomes: [osg::Billboard, osg::Geode].</summary>
    static (Node group, Node bb, Node crown) CrownGroup(Node holder)
    {
        Assert.Equal("osg::Group", holder.Cls);
        Assert.Equal(2, holder.Children.Count);
        Node bb = holder.Children[0], crown = holder.Children[1];
        Assert.Equal("osg::Billboard", bb.Cls);
        Assert.Equal("osg::Geode", crown.Cls);
        return (holder, bb, crown);
    }

    static string Tmp()
    {
        string d = Path.Combine(Path.GetTempPath(), $"skcl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void BillboardWithCrownIsOneGroup()
    {
        var (sc, tree) = ReplaceTests.Tree();
        var r = RunCards(sc, tree, BillboardCrown);
        Assert.Null(r.Error);
        var (_, bb, crown) = CrownGroup(Assert.Single(sc.Root!.Children));
        Assert.NotNull(bb.Center);
        var bbGeo = Assert.Single(bb.Children);
        var crownGeo = Assert.Single(crown.Children);
        Assert.Equal(2, bbGeo.Geom!.Triangles().Length / 3);
        Assert.Equal(4, crownGeo.Geom!.Triangles().Length / 3);
        var layout = CardLayout.Resolve("custom", new Dictionary<string, object> { ["mode"] = "billboard", ["horizontal"] = 1 });
        Assert.Equal(6, layout.Triangles);
        Assert.False(bbGeo.Geom.Optimizable);
        Assert.Contains("drawable of an osg::Billboard: kept as it is", bbGeo.Geom.Notes);
        // one bake: both state sets hold the same Texture objects
        Assert.Equal(bbGeo.StateSet!.Textures.Count, crownGeo.StateSet!.Textures.Count);
        for (int i = 0; i < bbGeo.StateSet.Textures.Count; i++) Assert.Same(crownGeo.StateSet.Textures[i], bbGeo.StateSet.Textures[i]);
        // the camera-facing card stands in the x-z plane around the billboard's centre, facing -y; the crown is flat
        var q = Assert.Single(Quads(bbGeo));
        Assert.All(q.P, p => Assert.Equal(0, p[1], 9));
        Assert.True(q.Normal[1] < 0);
        Assert.All(Quads(crownGeo), c => Assert.True(c.Flat));
        // P6: the layout key is in the MadeFrom notes of the new holder
        Assert.Contains("layout=" + layout.Key, Note(r, crownGeo));
        Assert.Contains("layout=" + layout.Key, Note(r, bbGeo));
    }

    [Theory]
    [InlineData("padding", "full")]
    [InlineData("cells", "by_area")]
    [InlineData("back", "mirror")]
    public void BillboardTurnsWhateverTheAtlasRowsSay(string row, string value)
    {
        var (sc, tree) = ReplaceTests.Tree();
        var r = RunCards(sc, tree, new() { ["replace_cards.mode"] = "billboard", ["replace_cards.texture"] = 64, ["replace_cards." + row] = value });
        Assert.Null(r.Error);
        var bb = Assert.Single(sc.Root!.Children);
        Assert.Equal("osg::Billboard", bb.Cls);
        Assert.NotNull(bb.Center);
        var geo = Assert.Single(bb.Children);
        Assert.False(geo.Geom!.Optimizable);
        var q = Assert.Single(Quads(geo));
        Assert.All(q.P, p => Assert.Equal(0, p[1], 9));
        Assert.True(q.Normal[1] < 0);
    }

    [Fact]
    public void BillboardCopiesUseTheBillboardTargetRuleUnderAnAtlasRow()
    {
        string dir = Tmp();
        try
        {
            var (sc, a, b) = CardFixtures.TwoCopies(dir);
            var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" },
                new Dictionary<string, object> { ["replace_cards.mode"] = "billboard", ["replace_cards.texture"] = 64, ["replace_cards.padding"] = "full", ["replace_cards.share"] = true },
                targets: new List<Node> { a, b });
            Assert.Null(results[0].Error);
            Assert.Contains("2 objects -> 1 atlases", results[0].Message);
            Assert.Equal(2, sc.IterNodes().Count(n => n.Cls == "osg::Billboard"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void TiltedCopyCarriesTheCrownWithoutRebake()
    {
        string dir = Tmp();
        try
        {
            var (sc, a, b) = CardFixtures.TwoCopies(dir, 20);
            var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, new(BillboardCrown) { ["replace_cards.share"] = true },
                targets: new List<Node> { a, b });
            var r = results[0];
            Assert.Null(r.Error);
            Assert.Contains("2 objects -> 1 atlases", r.Message);
            var group = sc.Root!.Children[0];
            CrownGroup(group);
            var mt = sc.IterNodes().Single(n => n.Cls == "osg::MatrixTransform" && n.Name != "copy");
            Assert.Same(group, Assert.Single(mt.Children));
            Assert.Equal(1, sc.IterNodes().Count(n => n.Cls == "osg::Billboard"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void BillboardWithCrownSavesToFlt()
    {
        string dir = Tmp();
        try
        {
            string host = Path.Combine(dir, "host.flt");
            File.WriteAllBytes(host, FltWriteTests.Grid(4));
            var sc = SceneIO.Load(host);
            var (_, tree) = ReplaceTests.Tree();
            sc.Root!.Children.Add(tree);
            var r = RunCards(sc, tree, BillboardCrown);
            Assert.Null(r.Error);
            string outp = Path.Combine(dir, "x.flt");
            SceneIO.Save(sc, outp);
            var back = SceneIO.Load(outp);
            var parents = new Dictionary<Node, Node>(ReferenceEqualityComparer.Instance);
            foreach (var n in back.IterNodes()) foreach (var c in n.Children) parents[c] = n;
            var geos = back.IterNodes().Where(n => n.Kind == Kind.Geometry && n.Name.Contains("_cards")).ToList();
            var bb = Assert.Single(geos, g => g.Name.EndsWith(" billboard (axial)"));
            Assert.Equal(1, Convert.ToInt32(bb.Extra!["faces"]));
            // the two fixed crown quads (top and bottom): OpenFlight writes fixed card faces as triangles, so 4 faces / 4 triangles
            var fixedFaces = geos.Where(g => !g.Name.Contains("billboard")).ToList();
            Assert.Equal(4, fixedFaces.Sum(g => g.Geom!.Triangles().Length / 3));
            Assert.Equal(4, fixedFaces.Sum(g => Convert.ToInt32(g.Extra!["faces"])));
            List<Node> Up(Node n) { var o = new List<Node>(); while (parents.TryGetValue(n, out var p)) { o.Add(p); n = p; } return o; }
            var common = geos.Select(Up).Aggregate((x, y) => x.Where(n => y.Contains(n, ReferenceEqualityComparer.Instance)).ToList());
            // all card faces sit below one group that holds nothing else (not the host's root)
            var low = common.First(n => n.Kind == Kind.Group);
            Assert.DoesNotContain(back.IterNodes().Where(n => n.Kind == Kind.Geometry && !n.Name.Contains("_cards")), g => Up(g).Contains(low, ReferenceEqualityComparer.Instance));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void AnotherLayoutNeverReusesAnAtlas()
    {
        string dir = Tmp();
        try
        {
            var (sc, a, b) = CardFixtures.TwoCopies(dir);
            var r1 = RunCards(sc, a, new() { ["replace_cards.layout"] = "ground", ["replace_cards.texture"] = 64 });
            Assert.Null(r1.Error);
            var aHolder = sc.Root!.Children[0];
            var aGeo = Assert.Single(aHolder.Children);
            var aTex = aGeo.StateSet!.Textures.Single(t => t.Unit == 0);
            string aHash = CardGolden.Hash(aTex.Baked!), aGeoHash = CardGolden.Hash(aGeo.Geom!);
            Assert.Contains("layout=" + CardLayout.Named("ground").Key, Note(r1, aGeo));
            var r2 = RunCards(sc, b, new() { ["replace_cards.layout"] = "cross2", ["replace_cards.share"] = true, ["replace_cards.texture"] = 64 });
            Assert.Null(r2.Error);
            Assert.Contains("1 objects -> 1 atlases", r2.Message);
            var bGeo = sc.IterNodes().Single(n => n.Kind == Kind.Geometry && n.Name.EndsWith(" cards") && !ReferenceEquals(n, aGeo));
            var bTex = bGeo.StateSet!.Textures.Single(t => t.Unit == 0);
            Assert.NotSame(aTex, bTex);
            Assert.NotEqual(aHash, CardGolden.Hash(bTex.Baked!));
            // cross2 is a legacy layout: today's note, without a layout key (P6)
            Assert.True(CardLayout.Named("cross2").Legacy);
            Assert.Equal("2 cross cards", Note(r2, bGeo));
            // a unchanged
            Assert.Same(aHolder, sc.Root.Children[0]);
            Assert.Same(aGeo, Assert.Single(aHolder.Children));
            Assert.Same(aTex, aGeo.StateSet.Textures.Single(t => t.Unit == 0));
            Assert.Equal(aHash, CardGolden.Hash(aTex.Baked!));
            Assert.Equal(aGeoHash, CardGolden.Hash(aGeo.Geom!));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---- R3-C11: window helpers ----

    [Theory]
    [InlineData("crossed", "pass.crossed4")]
    [InlineData("billboard", "pass.billboard")]
    public void SimCardsResetsTheNewKeys(string mode, string golden)
    {
        var prms = new Dictionary<string, object>
        {
            ["replace_cards.layout"] = "ground", ["replace_cards.horizontal"] = 2, ["replace_cards.heights"] = "0.3, 0.6",
            ["replace_cards.slab"] = "slice", ["replace_cards.faces"] = "top", ["replace_cards.back"] = "mirror",
            ["replace_cards.box_bottom"] = true, ["replace_cards.cells"] = "by_area",
            ["replace_cards.padding"] = "full", ["replace_cards.alpha_mips"] = "coverage",
            ["replace_cards.texture"] = 128,
        };
        // the editor's planes after picking a named layout (cross2: 2 planes) must not leak into Cross Cards All
        prms["replace_cards.planes"] = CardLayout.Named("cross2").Rows()["planes"];
        prms["replace_cards.layout"] = "cross2";
        Assert.Equal(2, Convert.ToInt32(prms["replace_cards.planes"]));
        InspectorModel.SimCardsParams(prms, mode);
        Assert.Equal(4, Convert.ToInt32(prms["replace_cards.planes"]));

        Assert.Equal("custom", prms["replace_cards.layout"]);
        Assert.Equal(0, Convert.ToInt32(prms["replace_cards.horizontal"]));
        Assert.Equal("auto", prms["replace_cards.heights"]);
        Assert.Equal("full", prms["replace_cards.slab"]);
        Assert.Equal("both", prms["replace_cards.faces"]);
        Assert.Equal("own", prms["replace_cards.back"]);
        Assert.Equal(false, prms["replace_cards.box_bottom"]);
        Assert.Equal("equal", prms["replace_cards.cells"]);
        Assert.Equal("8", Convert.ToString(prms["replace_cards.padding"]));
        Assert.Equal("renderer", prms["replace_cards.alpha_mips"]);
        Assert.Equal(mode, prms["replace_cards.mode"]);
        Assert.Equal(true, prms["replace_cards.share"]);

        string dir = Path.Combine(Path.GetTempPath(), $"skc11_{Guid.NewGuid():N}"), outDir = Path.Combine(Path.GetTempPath(), $"skc11_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir); Directory.CreateDirectory(outDir);
        try
        {
            var (sc, a, b) = CardFixtures.TwoCopies(dir);
            var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, prms, targets: new List<Node> { a, b });
            Assert.Null(results[0].Error);
            CardGolden.AssertMatches(golden, CardGolden.Capture(sc, outDir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            try { Directory.Delete(outDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void SimCardsKeepsTheEditorsPlanesForACustomLayout()
    {
        var prms = new Dictionary<string, object> { ["replace_cards.layout"] = "custom", ["replace_cards.planes"] = 3 };
        InspectorModel.SimCardsParams(prms, "crossed");
        Assert.Equal(3, Convert.ToInt32(prms["replace_cards.planes"]));
        var none = new Dictionary<string, object> { ["replace_cards.planes"] = 3 };
        InspectorModel.SimCardsParams(none, "crossed");
        Assert.Equal(3, Convert.ToInt32(none["replace_cards.planes"]));
    }

    [Fact]
    public void LayoutNamesAreTrimmedAndCaseInsensitive()
    {
        Assert.Equal("ground", CardLayout.Named("  Ground ").Label);
        var l = CardLayout.Resolve(" GROUND ", new Dictionary<string, object>());
        Assert.Equal("ground", l.Label);
        Assert.Equal(CardLayout.Named("ground").Triangles, l.Triangles);
        Assert.Equal("crossed4", CardLayout.Resolve(" Custom ", new Dictionary<string, object>()).Label);
    }

    [Fact]
    public void CardsAllTextNamesLayoutAndTriangles()
    {
        Assert.Equal("ground cards, 16 triangles each", InspectorModel.CardsAllWith(CardLayout.Named("ground")));
        var l = CardLayout.Resolve("ground", new Dictionary<string, object> { ["horizontal"] = 3 });
        Assert.Equal("custom (from ground) cards, 20 triangles each", InspectorModel.CardsAllWith(l));
    }

    [Fact]
    public void FindingNamesTheActiveTreeProfile()
    {
        var tree = Profiles.BuiltIn().First(p => p.Name == "Tree, ground level");
        var building = Profiles.BuiltIn().First(p => p.Name == "Building far");
        var f = new Finding("similar_objects", "medium", "t", "m", fix: "replace_cards");
        Assert.Equal("Tree, ground level suggests ground cards", InspectorModel.CardsFindingNote(f, tree));
        Assert.Null(InspectorModel.CardsFindingNote(f, building));
        Assert.Null(InspectorModel.CardsFindingNote(f, null));
        Assert.Null(InspectorModel.CardsFindingNote(new Finding("similar_objects", "medium", "t", "m", fix: "replace_proxy"), tree));
    }
}
