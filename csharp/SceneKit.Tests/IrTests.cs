using System.Buffers.Binary;
using SceneKit;
using Xunit;

/// <summary>Infrared (IR) support: the fill of baked layers where a part has none (extra_fill), OpenFlight face codes
/// (IR colour / IR material / SMC / feature ID) on generated faces, and companion images found by naming rules
/// ("tree_ir.rgb" next to "tree.rgb") that are baked, resized and saved with their texture.</summary>
public class IrTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"skir_{Guid.NewGuid():N}");

    public IrTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    static RawImage Solid(byte v, int size = 8, int ch = 3)
    {
        var im = new RawImage(size, size, ch);
        Array.Fill(im.Pixels, v);
        if (ch == 4) for (int i = 3; i < im.Pixels.Length; i += 4) im.Pixels[i] = 255;
        return im;
    }

    string Put(string name, byte[] data)
    {
        var p = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    const string Ir = "{stem}_ir{ext}";

    // ------------------------------------------------------------------------------ A: fill

    /// <summary>The replace tree (brown trunk, green crown) with textures on unit 0 (solid grey 100 / 200) and UVs; the
    /// crown's texture has an in-memory IR companion (solid 40), the trunk's none. With unit1 the crown also has a
    /// texture on unit 1 (solid 40).</summary>
    static (Scene sc, Node tree) IrTree(bool unit1 = false)
    {
        var (sc, tree) = ReplaceTests.Tree();
        var trunk = tree.Children[0];
        var crown = tree.Children[1];
        var (_, _, uv) = RebakeTests.Sphere(24, 12, 2.0);
        crown.Geom!.TexCoords[0] = new VArray(uv, 2, "Vec2Array");
        trunk.Geom!.TexCoords[0] = new VArray(new double[2 * trunk.Geom.NumVertices], 2, "Vec2Array");
        trunk.StateSet!.Textures.Add(new Texture { File = "bark.png", Baked = Solid(100), Exists = true, Channels = 3, Width = 8, Height = 8 });
        var ct = new Texture { File = "leaves.png", Baked = Solid(200), Exists = true, Channels = 3, Width = 8, Height = 8 };
        ct.Companions = new() { [Ir] = new Texture { File = "leaves_ir.png", Baked = Solid(40), Exists = true, Channels = 3, Width = 8, Height = 8, IsCompanion = true } };
        crown.StateSet!.Textures.Add(ct);
        if (unit1)
        {
            crown.Geom.TexCoords[1] = new VArray(uv, 2, "Vec2Array");
            crown.StateSet.Textures.Add(new Texture { File = "detail.png", Unit = 1, Baked = Solid(40), Exists = true, Channels = 3, Width = 8, Height = 8 });
        }
        return (sc, tree);
    }

    /// <summary>Billboard of the tree: (unit 0 texture, the card geometry, pass message).</summary>
    static (Texture t0, Node geo, string msg) Billboard(Scene sc, Node tree, string fill)
    {
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" },
            new() { ["replace_cards.mode"] = "billboard", ["replace_cards.texture"] = 128, ["replace_cards.extra_fill"] = fill }, targets: new List<Node> { tree });
        Assert.Null(results[0].Error);
        var geo = Assert.Single(Assert.Single(sc.Root!.Children).Children);
        return (Assert.Single(geo.StateSet!.Textures, t => t.Unit == 0), geo, results[0].Message);
    }

    static double Grey(RawImage img, double u, double v)
    {
        Span<double> c = stackalloc double[4];
        Bake.Bilinear(img, u, v, c);
        return c[0];
    }

    [Theory]
    [InlineData("white", 1.0)]
    [InlineData("black", 0.0)]
    [InlineData("128", 128 / 255.0)]
    [InlineData("luminance", 0.132)]   // the trunk's baked colour: 100/255 x (0.5, 0.3, 0.1)
    public void ExtraUnitFill(string fill, double trunk)
    {
        var (sc, tree) = IrTree(unit1: true);
        var (_, geo, msg) = Billboard(sc, tree, fill);
        var t1 = Assert.Single(geo.StateSet!.Textures, t => t.Unit == 1).Baked!;
        Assert.InRange(Grey(t1, 0.5, 0.2), trunk - 0.04, trunk + 0.04);           // trunk: no unit 1 -> the fill
        Assert.InRange(Grey(t1, 0.5, 0.7), 40 / 255.0 - 0.04, 40 / 255.0 + 0.04);  // crown: the detail texture, raw
        Assert.Contains("1 of 2 part(s) without it", msg);
    }

    [Theory]
    [InlineData("white", 1.0)]
    [InlineData("black", 0.0)]
    [InlineData("200", 200 / 255.0)]
    [InlineData("luminance", 0.132)]
    public void CompanionFill(string fill, double trunk)
    {
        var (sc, tree) = IrTree();
        var (t0, _, msg) = Billboard(sc, tree, fill);
        var ir = Assert.Single(t0.Companions!).Value;
        Assert.Equal(Ir, Assert.Single(t0.Companions!).Key);
        Assert.Equal((t0.Baked!.Width, t0.Baked.Height), (ir.Baked!.Width, ir.Baked.Height));   // same layout
        Assert.Equal(t0.File.Replace(".png", "_ir.png"), ir.File);                                 // the pattern applied to the atlas
        Assert.InRange(Grey(ir.Baked, 0.5, 0.2), trunk - 0.04, trunk + 0.04);                    // trunk: no companion -> the fill
        Assert.InRange(Grey(ir.Baked, 0.5, 0.7), 40 / 255.0 - 0.04, 40 / 255.0 + 0.04);         // crown: raw (no green material)
        Assert.Contains("IR companion of unit 0: 1 of 2 part(s) without it", msg);
    }

    [Fact]
    public void ProxyCompanionUsesTheFill()
    {
        var (sc, tree) = IrTree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, new()
        {
            ["replace_proxy.ratio"] = 0.5, ["replace_proxy.texture"] = 128, ["replace_proxy.voxels"] = 48, ["replace_proxy.extra_fill"] = "black",
        }, targets: new List<Node> { tree });
        Assert.Null(results[0].Error);
        var geo = Assert.Single(SoftRender.GeometryItems(Assert.Single(sc.Root!.Children))).node;
        var t0 = Assert.Single(geo.StateSet!.Textures);
        var ir = t0.Companions![Ir].Baked!;
        Assert.Equal((t0.Baked!.Width, t0.Baked.Height), (ir.Width, ir.Height));
        var crown = AtCentroids(geo, ir, 4.5, 10);
        var trunk = AtCentroids(geo, ir, -1, 1.5);
        Assert.True(crown.Count > 5 && trunk.Count > 2, $"{crown.Count} {trunk.Count}");
        Assert.True(crown.Count(x => Math.Abs(x - 40 / 255.0) < 0.05) >= crown.Count * 0.9, "crown raw 40: " + string.Join(" ", crown.Select(x => x.ToString("F2"))));
        Assert.True(trunk.Count(x => x < 0.05) >= trunk.Count * 0.9, "trunk black: " + string.Join(" ", trunk.Select(x => x.ToString("F2"))));
    }

    /// <summary>Grey of an image (on unit 0's UVs) at the centroids of the triangles whose centroid z is in [z0, z1].</summary>
    static List<double> AtCentroids(Node geo, RawImage img, double z0, double z1)
    {
        var g = geo.Geom!;
        var V = g.Vertices!.Data; var UV = g.TexCoords[0].Data; var T = g.Triangles();
        var o = new List<double>();
        for (int t = 0; t < T.Length / 3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], d = T[3 * t + 2];
            double z = (V[3 * a + 2] + V[3 * b + 2] + V[3 * d + 2]) / 3;
            if (z < z0 || z > z1) continue;
            o.Add(Grey(img, (UV[2 * a] + UV[2 * b] + UV[2 * d]) / 3, (UV[2 * a + 1] + UV[2 * b + 1] + UV[2 * d + 1]) / 3));
        }
        return o;
    }

    [Fact]
    public void FillParsing()
    {
        Assert.Equal(ExtraFill.White, ExtraFill.Parse("WHITE"));
        Assert.Equal(ExtraFill.Black, ExtraFill.Parse("black"));
        Assert.Null(ExtraFill.Parse("luminance").Grey);
        Assert.Equal(1.0, ExtraFill.Parse("255").Grey);
        Assert.Throws<ArgumentException>(() => ExtraFill.Parse("hot"));
        Assert.Throws<ArgumentException>(() => ExtraFill.Parse("300"));
        var (sc, tree) = IrTree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, new() { ["replace_cards.extra_fill"] = "hot" }, targets: new List<Node> { tree });
        Assert.Contains("extra_fill", results[0].Error);
    }

    // ------------------------------------------------------------------------------ B: OpenFlight face codes

    sealed class W
    {
        readonly MemoryStream ms = new();
        public long Length => ms.Length;
        public void Rec(int op, byte[] body)
        {
            var h = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)op);
            BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), (ushort)(4 + body.Length));
            ms.Write(h);
            ms.Write(body);
        }
        public byte[] Bytes() => ms.ToArray();
    }

    static byte[] Name(string s, int size)
    {
        var b = new byte[size];
        System.Text.Encoding.ASCII.GetBytes(s).CopyTo(b, 0);
        return b;
    }

    static readonly FaceCodes TrunkCodes = new(11, 21, 31, 41), CrownCodes = new(12, 22, 32, 42);

    /// <summary>An OpenFlight tree: group 'tree' > object 'trunk' (box 0.6 x 0.6 x 3, faces with TrunkCodes, texture
    /// bark.rgb) and object 'crown' (sphere r 2 at z 4, triangles with CrownCodes, texture crown.rgb); vertices with UVs.</summary>
    static byte[] TreeFlt()
    {
        var w = new W();
        var hdr = new byte[320];
        Name("db", 8).CopyTo(hdr, 0);
        BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(8), 1600);
        w.Rec(1, hdr);
        foreach (var (file, idx) in new[] { ("crown.rgb", 0), ("bark.rgb", 1) })
        {
            var tp = Name(file, 212);
            BinaryPrimitives.WriteInt32BigEndian(tp.AsSpan(200), idx);
            w.Rec(64, tp);
        }
        var verts = new List<(double x, double y, double z, double u, double v)>();
        foreach (var (x, y, z) in new[] { (-0.3, -0.3, 0.0), (0.3, -0.3, 0.0), (0.3, 0.3, 0.0), (-0.3, 0.3, 0.0), (-0.3, -0.3, 3.0), (0.3, -0.3, 3.0), (0.3, 0.3, 3.0), (-0.3, 0.3, 3.0) })
            verts.Add((x, y, z, (x + y + 0.6) / 1.2, z / 3));
        int nu = 16, nv = 8, sphere0 = verts.Count;
        for (int i = 0; i <= nv; i++)
            for (int j = 0; j <= nu; j++)
            {
                double th = Math.PI * i / nv, ph = 2 * Math.PI * j / nu;
                verts.Add((2 * Math.Sin(th) * Math.Cos(ph), 2 * Math.Sin(th) * Math.Sin(ph), 4 + 2 * Math.Cos(th), (double)j / nu, 1 - (double)i / nv));
            }
        var vpal = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(vpal, 8 + verts.Count * 44);
        w.Rec(67, vpal);
        long palStart = w.Length - 8;
        var offs = new int[verts.Count];
        for (int k = 0; k < verts.Count; k++)
        {
            offs[k] = (int)(w.Length - palStart);
            var v = new byte[40];
            BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(4), verts[k].x);
            BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(12), verts[k].y);
            BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(20), verts[k].z);
            BinaryPrimitives.WriteSingleBigEndian(v.AsSpan(28), (float)verts[k].u);
            BinaryPrimitives.WriteSingleBigEndian(v.AsSpan(32), (float)verts[k].v);
            BinaryPrimitives.WriteInt32BigEndian(v.AsSpan(36), -1);
            w.Rec(71, v);
        }
        void Face(int[] corners, FaceCodes c, short tex)
        {
            var f = Name("f", 76);   // body offset = record offset - 4
            BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(8), c.IrColor);
            BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(24), tex);
            BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(26), -1);
            BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(28), (short)c.Smc);
            BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(30), (short)c.Feature);
            BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(32), c.IrMaterial);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(40), 0x40000000);   // no colour
            w.Rec(5, f);
            w.Rec(10, Array.Empty<byte>());
            var vl = new byte[4 * corners.Length];
            for (int k = 0; k < corners.Length; k++) BinaryPrimitives.WriteInt32BigEndian(vl.AsSpan(4 * k), offs[corners[k]]);
            w.Rec(72, vl);
            w.Rec(11, Array.Empty<byte>());
        }
        w.Rec(10, Array.Empty<byte>());
        w.Rec(2, Name("tree", 44));
        w.Rec(10, Array.Empty<byte>());
        w.Rec(4, Name("trunk", 28));
        w.Rec(10, Array.Empty<byte>());
        foreach (var q in new[] { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 } })
            Face(q, TrunkCodes, 1);
        w.Rec(11, Array.Empty<byte>());
        w.Rec(4, Name("crown", 28));
        w.Rec(10, Array.Empty<byte>());
        int Idx(int i, int j) => sphere0 + i * (nu + 1) + j;
        for (int i = 0; i < nv; i++)
            for (int j = 0; j < nu; j++)
            {
                if (i < nv - 1) Face(new[] { Idx(i, j), Idx(i + 1, j), Idx(i + 1, j + 1) }, CrownCodes, 0);
                if (i > 0) Face(new[] { Idx(i, j), Idx(i + 1, j + 1), Idx(i, j + 1) }, CrownCodes, 0);
            }
        w.Rec(11, Array.Empty<byte>());
        w.Rec(11, Array.Empty<byte>());
        w.Rec(11, Array.Empty<byte>());
        return w.Bytes();
    }

    /// <summary>attr file: texel counts, repeat, mipmap trilinear, a marker byte at 100.</summary>
    static byte[] Attr(int w, int h)
    {
        var d = new byte[216];
        BinaryPrimitives.WriteInt32BigEndian(d, w);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(4), h);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(28), 6);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(32), 1);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(40), 2);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(44), 2);
        d[100] = 0x5A;
        return d;
    }

    /// <summary>The OpenFlight tree on disk with its textures: crown.rgb (grey 200) with crown_ir.rgb (grey 40, with an
    /// .attr), bark.rgb (grey 100) without a companion.</summary>
    Scene LoadTree(int size = 32)
    {
        Put("crown.rgb", ImageCodecs.WriteSgi(Solid(200, size)));
        Put("crown_ir.rgb", ImageCodecs.WriteSgi(Solid(40, size)));
        Put("crown_ir.rgb.attr", Attr(size, size));
        Put("bark.rgb", ImageCodecs.WriteSgi(Solid(100, size)));
        return SceneIO.Load(Put("tree.flt", TreeFlt()));
    }

    static Node Named(Scene sc, string name) => sc.IterNodes().First(n => n.Name == name);

    static IEnumerable<Texture> TexturesOf(Scene sc) => sc.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Distinct();

    [Fact]
    public void FltReaderShowsTheFaceCodes()
    {
        var sc = LoadTree();
        var crown = sc.IterNodes().First(n => n.Kind == Kind.Geometry && n.StateSet!.Textures.Any(t => t.File == "crown.rgb"));
        var rep = Analyzer.Analyze(sc);
        var text = InspectorModel.Details(rep, new InspectorModel.FindingIndex(rep), crown);
        Assert.Contains("OPENFLIGHT FACE CODES", text);
        Assert.Contains("IR colour 12, IR material 22, SMC 32, feature ID 42", text);
        Assert.Contains("IR companion ({stem}_ir{ext})", text);
    }

    [Theory]
    [InlineData("replace_cards", "whole")]
    [InlineData("replace_proxy", "whole")]
    [InlineData("replace_proxy", "per_part")]
    public void GeneratedFltFacesKeepTheDominantSourceCodes(string pass, string parts = "whole")
    {
        var sc = LoadTree();
        var (results, _, _) = Optimizer.Run(sc, new[] { pass }, new()
        {
            [pass + ".texture"] = 64, ["replace_proxy.ratio"] = 0.3, ["replace_proxy.voxels"] = 48, ["replace_proxy.share"] = false,
            ["replace_proxy.parts"] = parts,
        }, targets: new List<Node> { Named(sc, "tree") });
        Assert.Null(results[0].Error);
        if (parts == "per_part") Assert.Contains("per-part proxy", results[0].Message);   // the per-part path ran, not the single hull
        Assert.Contains(FltFaceCodes.DifferNote, results[0].Message);   // trunk and crown differ: the crown covers more area
        var geo = sc.IterNodes().Single(n => n.Kind == Kind.Geometry);
        Assert.Equal(CrownCodes, FltFaceCodes.Read(FltFaceCodes.Carried(geo)!));
        var rep = Analyzer.Analyze(sc);
        Assert.Contains(CrownCodes.ToString(), InspectorModel.Details(rep, new InspectorModel.FindingIndex(rep), geo));

        var outp = Path.Combine(dir, "tree.out.flt");
        SceneIO.Save(sc, outp);
        var faces = FltFile.ReadRecords(File.ReadAllBytes(outp)).Where(r => r.op == FltFile.FACE).Select(r => r.data).ToList();
        Assert.NotEmpty(faces);
        Assert.All(faces, f => Assert.Equal(CrownCodes, FltFaceCodes.Read(f)));
        var back = SceneIO.Load(outp);
        var g = back.IterNodes().Single(n => n.Kind == Kind.Geometry);
        Assert.Equal($"{CrownCodes}   ({FltFaceCodes.Describe(g)[0].Split('(')[1]}", FltFaceCodes.Describe(g)[0]);
        Assert.Single(FltFaceCodes.Describe(g));
    }

    [Fact]
    public void AgreeingSourcesGiveNoNote()
    {
        var sc = LoadTree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, new() { ["replace_cards.texture"] = 64 }, targets: new List<Node> { Named(sc, "crown") });
        Assert.DoesNotContain(FltFaceCodes.DifferNote, results[0].Message);
        var geo = sc.IterNodes().First(n => n.Name.EndsWith("cards") && n.Kind == Kind.Geometry);
        Assert.Equal(CrownCodes, FltFaceCodes.Read(FltFaceCodes.Carried(geo)!));
    }

    [Fact]
    public void LodLevelsAndMergedGeometryKeepTheCodes()
    {
        var sc = LoadTree();
        Optimizer.Run(sc, new[] { "generate_lod" }, new()
        {
            ["generate_lod.min_tris"] = 100, ["generate_lod.ratios"] = "0.5", ["generate_lod.switching"] = "fixed",
        });
        var lod = sc.IterNodes().Single(n => n.Kind == Kind.Lod);
        var level = SoftRender.GeometryItems(lod.Children[1]).Select(x => x.node).Single(n => n.Src == null);
        Assert.Equal(CrownCodes, FltFaceCodes.Read(FltFaceCodes.Carried(level)!));
        var outp = Path.Combine(dir, "lod.out.flt");
        SceneIO.Save(sc, outp);
        var codes = FltFile.ReadRecords(File.ReadAllBytes(outp)).Where(r => r.op == FltFile.FACE).Select(r => FltFaceCodes.Read(r.data)).Distinct().ToList();
        Assert.Equal(new FaceCodes?[] { TrunkCodes, CrownCodes }.OrderBy(c => c!.Value.IrColor), codes.OrderBy(c => c!.Value.IrColor));   // nothing defaulted to 0
    }

    [Fact]
    public void MergeKeepsFacesWithOtherCodesApart()
    {
        // two objects with the same state (no texture): merging would give the trunk the crown's codes
        var b = TreeFlt();
        var recs = FltFile.ReadRecords(b);
        foreach (var (op, data, off) in recs)
            if (op == FltFile.FACE) BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(off + 28), -1);   // untextured
        var sc = SceneIO.Load(Put("plain.flt", b));
        Optimizer.Run(sc, new[] { "merge_geometries" });
        var geoms = sc.IterNodes().Where(n => n.Kind == Kind.Geometry).ToList();
        Assert.Equal(2, geoms.Count);
    }

    // ------------------------------------------------------------------------------ C: companions

    [Fact]
    public void DetectionWithTheDefaultPatterns()
    {
        Put("a.rgb", ImageCodecs.WriteSgi(Solid(1)));
        Put("a_ir.rgb", ImageCodecs.WriteSgi(Solid(2, 4)));
        Put("b.rgb", ImageCodecs.WriteSgi(Solid(1)));
        Put("b.ir.rgb", ImageCodecs.WriteSgi(Solid(2)));
        Put("c.rgb", ImageCodecs.WriteSgi(Solid(1)));
        Put("g.rgb", ImageCodecs.WriteSgi(Solid(1)));
        Put("g_IR.rgb", ImageCodecs.WriteSgi(Solid(2)));
        var a = Companions.Find(Path.Combine(dir, "a.rgb"), Companions.DefaultPatterns)!;
        var ac = Assert.Single(a);
        Assert.Equal(Ir, ac.Key);
        Assert.Equal((4, 4, 3, true), (ac.Value.Width, ac.Value.Height, ac.Value.Channels, ac.Value.IsCompanion));
        Assert.Equal(Path.Combine(dir, "a_ir.rgb"), ac.Value.Path);
        Assert.Equal("{stem}.ir{ext}", Assert.Single(Companions.Find(Path.Combine(dir, "b.rgb"), new[] { "{stem}.ir{ext}" })!).Key);
        Assert.Null(Companions.Find(Path.Combine(dir, "c.rgb"), Companions.DefaultPatterns));
        // one file, found by "_ir" (Windows ignores case) and "_IR": it belongs to the pattern spelling it as on disk
        Assert.Equal("{stem}_IR{ext}", Assert.Single(Companions.Find(Path.Combine(dir, "g.rgb"), Companions.DefaultPatterns)!).Key);
    }

    [Fact]
    public void DefaultPatternsFindClassesPng()
    {
        Put("crown.rgb", ImageCodecs.WriteSgi(Solid(1)));
        Put("crown_Classes.png", ImageCodecs.WritePng(Solid(3)));
        Assert.Equal(new[] { "{stem}_Classes.png", "{stem}_ir{ext}", "{stem}_IR{ext}" }, Companions.DefaultPatterns);
        var hit = Assert.Single(Companions.Find(Path.Combine(dir, "crown.rgb"), Companions.DefaultPatterns)!);
        Assert.Equal("{stem}_Classes.png", hit.Key);
        Assert.Equal("Classes", Companions.Label(hit.Key));
        Assert.True(hit.Value.IsClassMap);
        Assert.True(Companions.IsClassMap("{stem}_Classes.png"));
        Assert.True(Companions.IsClassMap("ground_classes.png"));
        Assert.False(Companions.IsClassMap("{stem}_ir{ext}"));
    }

    [Fact]
    public void UnitOneClassesTextureIsTyped()
    {
        var ground = new Texture { File = "ground_classes.png", Unit = 1 };
        var plain = new Texture { File = "ground.png", Unit = 0 };
        var root = new Node(Kind.Group, "osg::Group", "root") { StateSet = new StateSet { Textures = { plain, ground } } };
        var sc = new Scene("x.osgt", "osgt") { Root = root };
        Companions.Detect(sc);
        Assert.True(ground.IsClassMap);
        Assert.False(plain.IsClassMap);
    }

    [Fact]
    public void PartialClassMapFindingSaysClassMaps()
    {
        var sc = LoadTree();
        Put("crown_Classes.png", ImageCodecs.WritePng(Solid(3, 32)));
        Companions.SetPatterns(sc, new[] { "{stem}_Classes.png" });
        var f = Assert.Single(Analyzer.Analyze(sc).Findings, x => x.Code == "companion_partial");
        Assert.Contains("class maps on only some parts", f.Title);
        Assert.Contains("class_fill", f.Message);
    }

    [Fact]
    public void DetectionWithCustomPatterns()
    {
        Put("d.rgb", ImageCodecs.WriteSgi(Solid(1)));
        Put("ir/d.rgb", ImageCodecs.WriteSgi(Solid(2)));
        Put("e.rgb", ImageCodecs.WriteSgi(Solid(1)));
        Put("e_ir.int", ImageCodecs.WriteSgi(Solid(2, 8, 1)));
        Put("h.rgb", ImageCodecs.WriteSgi(Solid(1)));
        Put("H_THERMAL.RGB", ImageCodecs.WriteSgi(Solid(2)));
        var pats = Companions.Parse("ir/{stem}{ext}; {stem}_ir.int, {stem}_thermal{ext}");
        Assert.Equal(3, pats.Count);
        Assert.Equal(Path.Combine(dir, "ir", "d.rgb"), Path.GetFullPath(Companions.Find(Path.Combine(dir, "d.rgb"), pats)!["ir/{stem}{ext}"].Path!));
        var e = Companions.Find(Path.Combine(dir, "e.rgb"), pats)!["{stem}_ir.int"];
        Assert.Equal(1, e.Channels);
        var h = Companions.Find(Path.Combine(dir, "h.rgb"), pats);   // case-insensitive
        Assert.NotNull(h);
        Assert.EndsWith("H_THERMAL.RGB", h!["{stem}_thermal{ext}"].Path!);
        Assert.Equal("IR", Companions.Label("ir/{stem}{ext}"));
        Assert.Equal("IR", Companions.Label("{stem}_ir.int"));
        Assert.Equal("THERMAL", Companions.Label("{stem}_thermal{ext}"));
        Assert.NotNull(Companions.Check("{ext}_ir"));
        Assert.NotNull(Companions.Check("{stem}_{unit}{ext}"));
        Assert.Equal(Path.Combine("x", "y", "ir", "t.rgb"), Companions.Apply("ir/{stem}{ext}", Path.Combine("x", "y", "t.rgb")));
        Assert.Equal("x/y/t_ir.rgb".Replace('/', Path.DirectorySeparatorChar), Companions.Apply("{dir}/{stem}_ir{ext}", Path.Combine("x", "y", "t.rgb")).Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void SceneDetectionAndPatternChanges()
    {
        var sc = LoadTree();
        var crown = TexturesOf(sc).Single(t => t.File == "crown.rgb");
        var bark = TexturesOf(sc).Single(t => t.File == "bark.rgb");
        Assert.Equal(Ir, Assert.Single(crown.Companions!).Key);
        Assert.Null(bark.Companions);
        Put("thermal/bark.rgb", ImageCodecs.WriteSgi(Solid(9)));
        var (found, problems) = Companions.SetPatterns(sc, Companions.Parse("thermal/{stem}{ext}; nonsense"));
        Assert.Equal(1, found);
        Assert.Single(problems);
        Assert.Null(crown.Companions);
        Assert.Equal("thermal/{stem}{ext}", Assert.Single(bark.Companions!).Key);
        // through the optimiser parameter (CLI --set companion_patterns=..., the inspector's field)
        Optimizer.Run(sc, Array.Empty<string>(), new() { ["companion_patterns"] = Ir });
        Assert.NotNull(crown.Companions);
        Assert.Null(bark.Companions);
    }

    [Fact]
    public void FindingWhenOnlySomePartsHaveACompanion()
    {
        var sc = LoadTree();
        var rep = Analyzer.Analyze(sc);
        var f = Assert.Single(rep.Findings, x => x.Code == "companion_partial");
        Assert.Equal("tree", Assert.Single(f.Nodes).Name);
        Assert.Contains("crown.rgb", f.Message);
        Assert.Contains("bark.rgb", f.Message);
        Assert.Contains("extra_fill", f.Message);
        Assert.Contains(InspectorModel.TextureTable(rep), r => r.File == "crown.rgb" && r.Companions == "IR");
        Put("bark_ir.rgb", ImageCodecs.WriteSgi(Solid(70, 32)));
        Companions.Detect(sc);
        Assert.DoesNotContain(Analyzer.Analyze(sc).Findings, x => x.Code == "companion_partial");
    }

    string TexDir(string outp) => Path.Combine(dir, Path.GetFileNameWithoutExtension(outp) + "_textures");

    [Theory]
    [InlineData("replace_cards", "tree_cards", "whole")]
    [InlineData("replace_proxy", "tree_proxy", "whole")]
    [InlineData("replace_proxy", "tree_proxy", "per_part")]
    public void BakingWritesTheCompanionAtlasNextToTheAtlas(string pass, string stem, string parts = "whole")
    {
        var sc = LoadTree();
        var (results, _, _) = Optimizer.Run(sc, new[] { pass }, new()
        {
            [pass + ".texture"] = 64, [pass + ".extra_fill"] = "black", ["replace_proxy.ratio"] = 0.3, ["replace_proxy.voxels"] = 48,
            ["replace_proxy.parts"] = parts,
        }, targets: new List<Node> { Named(sc, "tree") });
        Assert.Null(results[0].Error);
        if (parts == "per_part") Assert.Contains("per-part proxy", results[0].Message);   // the per-part path ran, not the single hull
        var outp = Path.Combine(dir, "tree.out.flt");
        SceneIO.Save(sc, outp);
        string atlas = Path.Combine(TexDir(outp), stem + ".rgb"), ir = Path.Combine(TexDir(outp), stem + "_ir.rgb");
        Assert.True(File.Exists(atlas), string.Join(", ", Directory.GetFiles(TexDir(outp))));
        Assert.True(File.Exists(ir), string.Join(", ", Directory.GetFiles(TexDir(outp))));
        var a = ImageCodecs.Read(atlas)!;
        var c = ImageCodecs.Read(ir)!;
        Assert.Equal((a.Width, a.Height), (c.Width, c.Height));
        Assert.Equal(3, c.Channels);                                  // the companion source's layout (grey 40 as RGB, no alpha)
        // same layout: where the atlas shows the crown (grey 200) the companion is raw 40, where it shows the trunk (100) the fill
        int crown = 0, crownOk = 0, trunk = 0, trunkOk = 0;
        for (int i = 0; i < a.Width * a.Height; i++)
        {
            if (a.Channels == 4 && a.Pixels[4 * i + 3] < 250) continue;
            int g = a.Pixels[a.Channels * i], v = c.Pixels[3 * i];
            if (Math.Abs(g - 200) < 6) { crown++; if (Math.Abs(v - 40) < 6) crownOk++; }
            else if (Math.Abs(g - 100) < 6) { trunk++; if (v < 6) trunkOk++; }
        }
        Assert.True(crown > 50 && crownOk >= crown * 0.95, $"crown {crownOk}/{crown}");
        Assert.True(trunk > 3 && trunkOk >= trunk * 0.9, $"trunk {trunkOk}/{trunk}");
        // the companion's .attr follows (new texel counts), and the reloaded scene finds the companion by the same rule
        var d = File.ReadAllBytes(ir + ".attr");
        Assert.Equal(c.Width, BinaryPrimitives.ReadInt32BigEndian(d));
        Assert.Equal(0x5A, d[100]);
        Assert.False(File.Exists(atlas + ".attr"));                   // crown.rgb has no .attr: none is invented
        var back = SceneIO.Load(outp);
        var t = TexturesOf(back).Single(x => x.File.Contains(stem));
        Assert.Equal(ir, Path.GetFullPath(t.Companions![Ir].Path!));
    }

    [Fact]
    public void RebakeWritesTheCompanionAtlas()
    {
        var sc = LoadTree();
        var crown = sc.IterNodes().First(n => n.Kind == Kind.Geometry && n.StateSet!.Textures.Any(t => t.File == "crown.rgb"));
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, new()
        {
            ["simplify.seams"] = "rebake", ["simplify.ratio"] = 0.5, ["simplify.rebake_texture"] = 64,
        }, targets: new List<Node> { crown });
        Assert.Null(results[0].Error);
        var t0 = Assert.Single(crown.StateSet!.Textures);
        Assert.NotNull(t0.Baked);
        var ir = t0.Companions![Ir];
        Assert.Equal((64, 64), (ir.Baked!.Width, ir.Baked.Height));
        var vals = AtCentroids(crown, ir.Baked, 2.5, 6);
        Assert.True(vals.Count > 10 && vals.All(v => Math.Abs(v - 40 / 255.0) < 0.03), string.Join(" ", vals.Select(x => x.ToString("F2"))));
        var outp = Path.Combine(dir, "rb.out.flt");
        SceneIO.Save(sc, outp);
        // the geometry is named after its texture: atlas 'crown.rgb_rebake.rgb', companion by the same rule
        var ir2 = Path.Combine(TexDir(outp), "crown.rgb_rebake_ir.rgb");
        Assert.True(File.Exists(ir2), string.Join(", ", Directory.GetFiles(TexDir(outp))));
        Assert.Equal(64, Images.Probe(ir2)!.Width);
        Assert.True(File.Exists(ir2 + ".attr"));
    }

    [Fact]
    public void ResizeKeepsTheCompanionInSync()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        var sc = LoadTree();
        var crown = TexturesOf(sc).Single(t => t.File == "crown.rgb");
        var r = TextureEdits.SetResize(sc, crown, 16, 16);
        Assert.True(r.Ok);
        Assert.Contains("IR companion", r.Message);
        var outp = Path.Combine(dir, "small.flt");
        SceneIO.Save(sc, outp);
        var ir = Path.Combine(TexDir(outp), "crown_ir.rgb");
        var info = Images.Probe(ir)!;
        Assert.Equal((16, 16), (info.Width, info.Height));
        Assert.Equal((16, 16), (Images.Probe(Path.Combine(TexDir(outp), "crown.rgb"))!.Width, Images.Probe(Path.Combine(TexDir(outp), "crown.rgb"))!.Height));
        var d = File.ReadAllBytes(ir + ".attr");
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(d));
        Assert.Equal(0x5A, d[100]);
        Assert.False(File.Exists(Path.Combine(TexDir(outp), "bark_ir.rgb")));

        // the downscale pass too
        var sc2 = LoadTree();
        var (results, _, _) = Optimizer.Run(sc2, new[] { "downscale_textures" }, new() { ["downscale_textures.max_size"] = 8 });
        Assert.Contains("1 companion image(s) resized with them", results[0].Message);
        var out2 = Path.Combine(dir, "tiny.osgt");
        SceneIO.Save(sc2, out2);
        Assert.Equal(8, Images.Probe(Path.Combine(TexDir(out2), "crown_ir.rgb"))!.Width);
        Assert.False(File.Exists(Path.Combine(TexDir(out2), "crown_ir.rgb.attr")));   // .attr copies only next to .flt
    }

    /// <summary>crown.rgb (32 px) with a crown_Classes.png of ids 7 and 9 in a column pattern; returns the png path and bytes.</summary>
    (Scene sc, string png, byte[] bytes) LoadTreeWithClasses()
    {
        var cls = new RawImage(32, 32, 3);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                cls.Pixels[(y * 32 + x) * 3] = (byte)(x % 3 == 0 ? 9 : 7);
        var bytes = ImageCodecs.WritePng(cls);
        var sc = LoadTree();
        var png = Put("crown_Classes.png", bytes);
        Companions.Detect(sc);
        return (sc, png, bytes);
    }

    [Fact]
    public void DownscaleLeavesClassMapsAloneByDefault()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        var (sc, _, bytes) = LoadTreeWithClasses();
        var (results, _, _) = Optimizer.Run(sc, new[] { "downscale_textures" }, new() { ["downscale_textures.max_size"] = 16 });
        Assert.Null(results[0].Error);
        var outp = Path.Combine(dir, "cls.osgt");
        SceneIO.Save(sc, outp);
        Assert.Equal(16, Images.Probe(Path.Combine(TexDir(outp), "crown.rgb"))!.Width);
        var copy = Path.Combine(TexDir(outp), "crown_Classes.png");
        Assert.Equal(32, Images.Probe(copy)!.Width);
        Assert.Equal(bytes, File.ReadAllBytes(copy));
    }

    [Fact]
    public void ResizeClassMapsTooUsesNearest()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        var (sc, _, _) = LoadTreeWithClasses();
        var (results, _, _) = Optimizer.Run(sc, new[] { "downscale_textures" }, new() { ["downscale_textures.max_size"] = 16, ["downscale_textures.resize_class_maps"] = true });
        Assert.Null(results[0].Error);
        var outp = Path.Combine(dir, "cls2.osgt");
        SceneIO.Save(sc, outp);
        var img = ImageCodecs.Read(Path.Combine(TexDir(outp), "crown_Classes.png"))!;
        Assert.Equal((16, 16), (img.Width, img.Height));
        for (int p = 0; p < 16 * 16; p++) Assert.Contains(img.Pixels[p * img.Channels], new byte[] { 7, 9 });
    }

    /// <summary>TwoUnitSphere with a file-backed unit 1 class texture x_classes.png (32 x 32, ids 7 and 9 in columns);
    /// returns the png path and bytes.</summary>
    (Scene sc, Texture u1, string png, byte[] bytes) UnitOneClassFile()
    {
        var cls = new RawImage(32, 32, 3);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                cls.Pixels[(y * 32 + x) * 3] = (byte)(x % 3 == 0 ? 9 : 7);
        var bytes = ImageCodecs.WritePng(cls);
        var png = Put("x_classes.png", bytes);
        var (sc, holder, _) = MultiTextureTests.TwoUnitSphere();
        var u1 = holder.StateSet!.Textures.Single(t => t.Unit == 1);
        (u1.File, u1.Path, u1.Baked, u1.Width, u1.Height) = ("x_classes.png", png, null, 32, 32);
        Companions.Detect(sc);
        Assert.True(u1.IsClassMap);
        return (sc, u1, png, bytes);
    }

    [Fact]
    public void DownscaleLeavesUnitOneClassTexturesAlone()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        var (sc, u1, png, bytes) = UnitOneClassFile();
        var (results, _, _) = Optimizer.Run(sc, new[] { "downscale_textures" }, new() { ["downscale_textures.max_size"] = 16 });
        Assert.Null(results[0].Error);
        Assert.Null(u1.Resize);
        Assert.Equal(32, u1.Width);
        var outp = Path.Combine(dir, "u1.osgt");
        SceneIO.Save(sc, outp);
        Assert.False(File.Exists(Path.Combine(TexDir(outp), "x_classes.png")));   // referenced as it is, at full size
        Assert.Contains("x_classes.png", File.ReadAllText(outp));
        Assert.Equal(bytes, File.ReadAllBytes(png));
    }

    [Fact]
    public void ResizeClassMapsTooResizesUnitOneClassTexturesNearest()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        var (sc, _, _, _) = UnitOneClassFile();
        var (results, _, _) = Optimizer.Run(sc, new[] { "downscale_textures" }, new() { ["downscale_textures.max_size"] = 16, ["downscale_textures.resize_class_maps"] = true });
        Assert.Null(results[0].Error);
        var outp = Path.Combine(dir, "u1n.osgt");
        SceneIO.Save(sc, outp);
        var img = ImageCodecs.Read(Path.Combine(TexDir(outp), "x_classes.png"))!;
        Assert.Equal((16, 16), (img.Width, img.Height));
        for (int p = 0; p < 16 * 16; p++) Assert.Contains(img.Pixels[p * img.Channels], new byte[] { 7, 9 });
    }

    [Fact]
    public void TexturesTabResizeKeepsClassTexturesUnlessAsked()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        var (sc, u1, _, _) = UnitOneClassFile();
        var r = TextureEdits.SetResize(sc, u1, 16, 16);
        Assert.False(r.Ok);
        Assert.Contains("class map keeps its size", r.Message);
        Assert.Null(u1.Resize);
        Assert.True(TextureEdits.SetResize(sc, u1, 32, 32).Ok);   // its own size (Original size) is fine
        Assert.True(TextureEdits.SetResize(sc, u1, 16, 16, null, resizeClassMaps: true).Ok);
        var outp = Path.Combine(dir, "u1t.osgt");
        SceneIO.Save(sc, outp);
        var img = ImageCodecs.Read(Path.Combine(TexDir(outp), "x_classes.png"))!;
        Assert.Equal(16, img.Width);
        for (int p = 0; p < 16 * 16; p++) Assert.Contains(img.Pixels[p * img.Channels], new byte[] { 7, 9 });

        // a baked class atlas: refused without the box, nearest with it
        var baked = new Texture { File = "a_classes.png", Baked = ClassImage(), Exists = true, Channels = 3, Width = 8, Height = 8, IsClassMap = true, Unit = 1 };
        Assert.False(TextureEdits.SetResize(sc, baked, 4, 4).Ok);
        Assert.Equal(8, baked.Baked!.Width);
        Assert.True(TextureEdits.SetResize(sc, baked, 4, 4, null, resizeClassMaps: true).Ok);
        Assert.Equal(4, baked.Baked!.Width);
        Assert.True(ClassPairs(baked.Baked).IsSubsetOf(new[] { (3, 200), (12, 50) }));
    }

    [Fact]
    public void UnreadableClassMapIsCopiedWhenResizeClassMapsIsOn()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        var sc = LoadTree();
        var bytes = Png16();
        Put("crown_Classes.png", bytes);
        Companions.Detect(sc);
        var (results, _, _) = Optimizer.Run(sc, new[] { "downscale_textures" }, new() { ["downscale_textures.max_size"] = 16, ["downscale_textures.resize_class_maps"] = true });
        Assert.Null(results[0].Error);
        var outp = Path.Combine(dir, "c16.osgt");
        SceneIO.Save(sc, outp);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(TexDir(outp), "crown_Classes.png")));
        Assert.Contains(sc.Warnings, w => w.Contains("crown_Classes.png") && w.Contains("copied as it is"));
    }

    [Fact]
    public void ResizingABakedAtlasResizesItsCompanion()
    {
        var (sc, tree) = IrTree();
        var (t0, _, _) = Billboard(sc, tree, "white");
        var before = t0.Companions;
        Assert.True(TextureEdits.SetResize(sc, t0, 64, 64).Ok);
        Assert.Equal(64, t0.Companions![Ir].Baked!.Width);
        Assert.Equal(128, before![Ir].Baked!.Width);   // a new dictionary: the undo snapshot keeps the old one
    }

    [Fact]
    public void ReplaceFileWarnsWhenTheNewFileLacksACompanion()
    {
        var sc = LoadTree();
        var crown = TexturesOf(sc).Single(t => t.File == "crown.rgb");
        var other = Put("other.rgb", ImageCodecs.WriteSgi(Solid(1)));
        var r = TextureEdits.ReplaceFile(sc, crown, other);
        Assert.True(r.Ok);
        Assert.Contains("warning: the new file has no IR companion", r.Message);
        Assert.Contains("other_ir.rgb", r.Message);
        Assert.Null(crown.Companions);
        Put("other2_ir.rgb", ImageCodecs.WriteSgi(Solid(1)));
        var r2 = TextureEdits.ReplaceFile(sc, crown, Put("other2.rgb", ImageCodecs.WriteSgi(Solid(1))));
        Assert.DoesNotContain("warning", r2.Message);
        Assert.NotNull(crown.Companions);
    }

    // ------------------------------------------------------------------------------ D: class maps baked by nearest lookup

    internal const string Cls = "{stem}_Classes.png";

    /// <summary>8 x 8 class map in 2 x 2 blocks: class 3 (G 200) and class 12 (G 50) like a checkerboard, so any
    /// filtering would give values in between.</summary>
    static RawImage ClassImage()
    {
        var im = new RawImage(8, 8, 3);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                bool a = (x / 2 + y / 2) % 2 == 0;
                im.Pixels[(y * 8 + x) * 3] = (byte)(a ? 3 : 12);
                im.Pixels[(y * 8 + x) * 3 + 1] = (byte)(a ? 200 : 50);
            }
        return im;
    }

    /// <summary>A class-map companion (attach under <see cref="Cls"/>): <paramref name="image"/>, or the checkerboard of
    /// <see cref="ClassImage"/>.</summary>
    internal static Texture ClassCompanion(string file, RawImage? image = null)
    {
        image ??= ClassImage();
        return new() { File = file, Baked = image, Exists = true, Channels = image.Channels, Width = image.Width, Height = image.Height, IsCompanion = true, IsClassMap = true };
    }

    /// <summary>IrTree with the crown's companion a class map (leaves_Classes.png) instead of the IR image.</summary>
    static (Scene sc, Node tree) ClassTree()
    {
        var (sc, tree) = IrTree();
        tree.Children[1].StateSet!.Textures[0].Companions = new() { [Cls] = ClassCompanion("leaves_Classes.png") };
        return (sc, tree);
    }

    /// <summary>The distinct (R, G) pairs of an image (texels with alpha 0 left out).</summary>
    internal static HashSet<(int r, int g)> ClassPairs(RawImage img)
    {
        var o = new HashSet<(int, int)>();
        for (int i = 0; i < img.Width * img.Height; i++)
        {
            if (img.Channels == 4 && img.Pixels[4 * i + 3] == 0) continue;
            o.Add((img.Pixels[img.Channels * i], img.Pixels[img.Channels * i + 1]));
        }
        return o;
    }

    internal static void OnlyIds(RawImage img, params (int r, int g)[] allowed)
    {
        var got = ClassPairs(img);
        Assert.True(got.IsSubsetOf(allowed), "unexpected (R, G): " + string.Join(" ", got.Except(allowed)));
        Assert.Contains((3, 200), got);
        Assert.Contains((12, 50), got);
    }

    [Fact]
    public void CardsBakeClassMapWithoutBlending()
    {
        var (sc, tree) = ClassTree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" },
            new() { ["replace_cards.mode"] = "billboard", ["replace_cards.texture"] = 64 }, targets: new List<Node> { tree });
        Assert.Null(results[0].Error);
        var geo = Assert.Single(Assert.Single(sc.Root!.Children).Children);
        var t0 = Assert.Single(geo.StateSet!.Textures, t => t.Unit == 0);
        var cls = t0.Companions![Cls];
        Assert.True(cls.IsClassMap);
        OnlyIds(cls.Baked!, (3, 200), (12, 50), (0, 0));   // the trunk has none: class_fill 0
        Assert.Contains("class_fill 0", results[0].Message);
    }

    [Fact]
    public void ProxyBakeUsesClassFillForPartsWithout()
    {
        var (sc, tree) = ClassTree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, new()
        {
            ["replace_proxy.ratio"] = 0.5, ["replace_proxy.texture"] = 128, ["replace_proxy.voxels"] = 48, ["replace_proxy.class_fill"] = 5,
        }, targets: new List<Node> { tree });
        Assert.Null(results[0].Error);
        var geo = Assert.Single(SoftRender.GeometryItems(Assert.Single(sc.Root!.Children))).node;
        var cls = Assert.Single(geo.StateSet!.Textures).Companions![Cls].Baked!;
        OnlyIds(cls, (3, 200), (12, 50), (5, 0));
        var trunk = NearestAtCentroids(geo, cls, -1, 1.5);
        Assert.True(trunk.Count > 2 && trunk.Count(p => p == (5, 0)) >= trunk.Count * 0.9, "trunk: " + string.Join(" ", trunk));
        Assert.Contains("class_fill 5", results[0].Message);
    }

    /// <summary>(R, G) of an image by nearest lookup (on unit 0's UVs) at the centroids of the triangles whose centroid z is
    /// in [z0, z1].</summary>
    static List<(int r, int g)> NearestAtCentroids(Node geo, RawImage img, double z0, double z1)
    {
        var g = geo.Geom!;
        var V = g.Vertices!.Data; var UV = g.TexCoords[0].Data; var T = g.Triangles();
        var o = new List<(int, int)>();
        var c = new double[4];
        for (int t = 0; t < T.Length / 3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], d = T[3 * t + 2];
            double z = (V[3 * a + 2] + V[3 * b + 2] + V[3 * d + 2]) / 3;
            if (z < z0 || z > z1) continue;
            Bake.Nearest(img, (UV[2 * a] + UV[2 * b] + UV[2 * d]) / 3, (UV[2 * a + 1] + UV[2 * b + 1] + UV[2 * d + 1]) / 3, c);
            o.Add(((int)Math.Round(c[0] * 255), (int)Math.Round(c[1] * 255)));
        }
        return o;
    }

    [Fact]
    public void RebakeKeepsClassIds()
    {
        var (sc, holder, geo) = RebakeTests.TexturedSphere();
        holder.StateSet!.Textures[0].Companions = new() { [Cls] = ClassCompanion("half_Classes.png") };
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, new()
        {
            ["simplify.seams"] = "rebake", ["simplify.ratio"] = 0.3, ["simplify.rebake_texture"] = 64,
        }, targets: new List<Node> { geo });
        Assert.Null(results[0].Error);
        var cls = Assert.Single(geo.StateSet!.Textures).Companions![Cls];
        Assert.True(cls.IsClassMap);
        OnlyIds(cls.Baked!, (3, 200), (12, 50));
    }

    [Fact]
    public void UnitOneClassTextureIsBakedNearest()
    {
        var (sc, holder, geo) = MultiTextureTests.TwoUnitSphere();
        var u1 = holder.StateSet!.Textures.Single(t => t.Unit == 1);
        (u1.File, u1.Baked, u1.Width, u1.Height, u1.IsClassMap) = ("x_classes.png", ClassImage(), 8, 8, true);
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, new()
        {
            ["simplify.seams"] = "rebake", ["simplify.ratio"] = 0.3, ["simplify.rebake_texture"] = 64,
        }, targets: new List<Node> { geo });
        Assert.Null(results[0].Error);
        var t1 = Assert.Single(geo.StateSet!.Textures, t => t.Unit == 1);
        Assert.True(t1.IsClassMap);
        OnlyIds(t1.Baked!, (3, 200), (12, 50));
    }

    /// <summary>An 8 x 8 RGB PNG with 16 bits per sample (which the reader does not decode).</summary>
    static byte[] Png16()
    {
        static void Chunk(Stream s, string type, byte[] body)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, body.Length);
            s.Write(b);
            var tb = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(tb);
            s.Write(body);
            uint crc = 0xFFFFFFFF;
            foreach (byte x in tb.Concat(body))
            {
                crc ^= x;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            BinaryPrimitives.WriteUInt32BigEndian(b, ~crc);
            s.Write(b);
        }
        var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, 8);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 8);
        ihdr[8] = 16; ihdr[9] = 2;   // depth 16, RGB
        Chunk(ms, "IHDR", ihdr);
        var raw = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < 8; y++)
            {
                z.WriteByte(0);
                for (int x = 0; x < 8; x++) z.Write(new byte[] { 0, (byte)(x % 2 == 0 ? 3 : 12), 0, 200, 0, 0 });
            }
        Chunk(ms, "IDAT", raw.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    [Fact]
    public void SixteenBitClassMapIsSkippedWithANote()
    {
        var bytes = Png16();
        var png = Put("leaves_Classes.png", bytes);
        var (sc, tree) = IrTree();
        tree.Children[1].StateSet!.Textures[0].Companions = new()
        {
            [Cls] = new Texture { File = "leaves_Classes.png", Path = png, Exists = true, Channels = 3, Width = 8, Height = 8, IsCompanion = true, IsClassMap = true },
        };
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" },
            new() { ["replace_cards.mode"] = "billboard", ["replace_cards.texture"] = 64 }, targets: new List<Node> { tree });
        Assert.Null(results[0].Error);
        Assert.Contains("class map leaves_Classes.png could not be read (16-bit PNG?): not baked, file left untouched", results[0].Message);
        var geo = Assert.Single(Assert.Single(sc.Root!.Children).Children);
        Assert.Null(Assert.Single(geo.StateSet!.Textures, t => t.Unit == 0).Companions?.GetValueOrDefault(Cls));
        SceneIO.Save(sc, Path.Combine(dir, "cards.osgt"));
        Assert.Equal(bytes, File.ReadAllBytes(png));
    }

    [Fact]
    public void ClassFillParsing()
    {
        Assert.Throws<ArgumentException>(() => ExtraFill.ClassFill(256));
        Span<double> o = stackalloc double[4];
        ExtraFill.ClassFill(7).Apply(o, 0.5, 0.5, 0.5);
        Assert.Equal(new[] { 7 / 255.0, 0, 0, 1 }, o.ToArray());
        var (sc, tree) = ClassTree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, new() { ["replace_cards.class_fill"] = -1 }, targets: new List<Node> { tree });
        Assert.Contains("class_fill: a class ID 0-255", results[0].Error);
    }
}
