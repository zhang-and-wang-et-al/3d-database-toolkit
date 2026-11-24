using System.Buffers.Binary;
using SceneKit;
using Xunit;

/// <summary>Saving OpenFlight scenes back as .flt (FltWriter): unchanged records stay byte for byte, changed geometry
/// becomes new faces, external .flt files can be overwritten in place.</summary>
public class FltWriteTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"skfltw_{Guid.NewGuid():N}");

    public FltWriteTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

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

    /// <summary>header, palette, group > object > an n x n grid of quads (two triangle faces each, a little bumpy),
    /// optionally a hidden face and external references.</summary>
    internal static byte[] Grid(int n, string name = "grid", bool hidden = false, params string[] extRefs) => Grid(n, name, hidden, 0, extRefs);

    /// <summary>decals: the first triangle of every quad gets a subface (a decal with texture pattern 0) on top of it.
    /// lightMode: the faces' light mode (record offset 48).</summary>
    static byte[] Grid(int n, string name, bool hidden, int replicate, string[] extRefs, bool decals = false, int lightMode = 0)
    {
        var w = new W();
        var hdr = new byte[320];
        Name("db", 8).CopyTo(hdr, 0);
        BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(8), 1600);
        w.Rec(1, hdr);
        int nv = (n + 1) * (n + 1);
        var vpal = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(vpal, 8 + nv * 40);
        w.Rec(67, vpal);
        long palStart = w.Length - 8;
        var offs = new int[nv];
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                offs[j * (n + 1) + i] = (int)(w.Length - palStart);
                var v = new byte[36];
                BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(4), i);
                BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(12), j);
                BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(20), 0.05 * Math.Sin(i * 0.3) * Math.Cos(j * 0.2));
                BinaryPrimitives.WriteInt32BigEndian(v.AsSpan(32), -1);
                w.Rec(68, v);
            }
        w.Rec(10, Array.Empty<byte>());
        w.Rec(2, Name(name, 44));
        if (replicate > 0)
        {
            float[] m = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, n + 5, 0, 0, 1 };   // row vectors: translation in the last row
            var mb = new byte[64];
            for (int i = 0; i < 16; i++) BinaryPrimitives.WriteSingleBigEndian(mb.AsSpan(4 * i), m[i]);
            w.Rec(49, mb);
            var rb = new byte[4];
            BinaryPrimitives.WriteInt16BigEndian(rb, (short)replicate);
            w.Rec(60, rb);
        }
        w.Rec(10, Array.Empty<byte>());
        foreach (var e in extRefs) w.Rec(63, Name(e, 216));
        if (n > 0)
        {
            w.Rec(4, Name("obj", 28));
            w.Rec(10, Array.Empty<byte>());
            void Face(int a, int b, int c, uint flags, bool decal = false, short tex = -1)
            {
                var f = Name("f", 76);
                BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(24), tex);
                BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(40), flags);
                f[44] = (byte)lightMode;   // record offset 48
                w.Rec(5, f);
                w.Rec(10, Array.Empty<byte>());
                var vl = new byte[12];
                BinaryPrimitives.WriteInt32BigEndian(vl, offs[a]);
                BinaryPrimitives.WriteInt32BigEndian(vl.AsSpan(4), offs[b]);
                BinaryPrimitives.WriteInt32BigEndian(vl.AsSpan(8), offs[c]);
                w.Rec(72, vl);
                w.Rec(11, Array.Empty<byte>());
                if (decal)
                {
                    w.Rec(19, Array.Empty<byte>());   // push subface
                    Face(a, b, c, 0x40000000, false, 0);
                    w.Rec(20, Array.Empty<byte>());   // pop subface
                }
            }
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i, b = a + 1, c = a + n + 2, d = a + n + 1;
                    Face(a, b, c, 0x40000000, decals);
                    Face(a, c, d, 0x40000000);
                }
            if (hidden) Face(0, 1, n + 2, 0x40000000 | 0x04000000);
            w.Rec(11, Array.Empty<byte>());
        }
        w.Rec(11, Array.Empty<byte>());
        w.Rec(11, Array.Empty<byte>());
        return w.Bytes();
    }

    string Put(string name, byte[] data)
    {
        var p = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    static long Tris(Scene sc) => Convert.ToInt64(Analyzer.Analyze(sc, deepChecks: false).Summary()["triangles"]);

    [Fact]
    public void UnchangedSceneIsWrittenByteForByte()
    {
        var src = Put("a.flt", Grid(6, hidden: true));
        var sc = SceneIO.Load(src);
        Assert.Equal(".flt", OsgWrite.NativeExt(sc));
        var outp = Path.Combine(dir, "b.flt");
        SceneIO.Save(sc, outp);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(outp));
    }

    [Fact]
    public void RebakedTextureIsWrittenAsSgiNextToTheFlt()
    {
        // baked textures take their source's format (an SGI source here; a PNG source would give PNG, see TextureFormatTests)
        var src = Put("grid.flt", Grid(16));
        var sc = SceneIO.Load(src);
        var geo = sc.IterNodes().First(n => n.Kind == Kind.Geometry);
        geo.StateSet = new StateSet { Textures = { new Texture { File = "half.rgb", Baked = LodToolsTests.HalfImage(), Exists = true, Channels = 3, Width = 64, Height = 64 } } };
        geo.Geom!.TexCoords[0] = new VArray(Enumerable.Range(0, geo.Geom.NumVertices).SelectMany(i => new[] { geo.Geom.Vertices!.Data[3 * i] / 16, geo.Geom.Vertices.Data[3 * i + 1] / 16 }).ToArray(), 2, "Vec2Array");
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object>
            { ["simplify.seams"] = "rebake", ["simplify.ratio"] = 0.1, ["simplify.rebake_texture"] = 64 }, targets: new List<Node> { geo });
        Assert.All(results, r => Assert.Null(r.Error));
        var outp = Path.Combine(dir, "grid.rebaked.flt");
        SceneIO.Save(sc, outp);
        var files = Directory.GetFiles(Path.Combine(dir, "grid.rebaked_textures"));
        var rgb = Assert.Single(files);
        Assert.EndsWith(".rgb", rgb);
        var back = SceneIO.Load(outp);
        var tex = back.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).ToList();
        Assert.Contains(tex, t => t.Exists && t.File.EndsWith(".rgb"));
    }

    [Fact]
    public void SimplifiedGeometryBecomesNewFaces()
    {
        var src = Put("grid.flt", Grid(30, hidden: true));
        var sc = SceneIO.Load(src);
        long before = Tris(sc);
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object> { ["simplify.ratio"] = 0.2, ["simplify.min_tris"] = 10 });
        Assert.All(results, r => Assert.Null(r.Error));
        var outp = Path.Combine(dir, "grid.optimized.flt");
        SceneIO.Save(sc, outp);
        var back = SceneIO.Load(outp);
        long after = Tris(back);
        Assert.True(after < before / 2, $"{after} of {before}");
        Assert.Equal(Tris(sc), after);
        Assert.Equal(1, back.Info["hidden_faces"]);                 // the hidden face was kept as it was
        Assert.NotNull(back.IterNodes().SingleOrDefault(n => n.Name == "grid"));
        var bb = Analyzer.Analyze(back).Total.BBox!.Value;
        Assert.Equal(30.0, bb.hi[0], 3);
        Assert.Equal(30.0, bb.hi[1], 3);
        Assert.True(new FileInfo(outp).Length < new FileInfo(src).Length);   // unused palette vertices are dropped
    }

    [Fact]
    public void ExternalFltFilesAreOverwrittenInPlace()
    {
        Put("db/tile_a.flt", Grid(20, "tile_a"));
        Put("db/tile_b.flt", Grid(20, "tile_b"));
        var master = Put("db/master.flt", Grid(0, "master", false, "tile_a.flt", "tile_b.flt"));
        var masterBytes = File.ReadAllBytes(master);
        var sc = SceneIO.Load(master);
        long before = Tris(sc);
        Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object> { ["simplify.ratio"] = 0.25, ["simplify.min_tris"] = 10 });
        SceneIO.Save(sc, master, overwriteExternals: true);
        Assert.Equal(2, sc.SavedExternals.Count);
        Assert.All(sc.SavedExternals, p => Assert.Equal(".flt", Path.GetExtension(p)));
        Assert.False(Directory.Exists(Path.Combine(dir, "db", "master_files")));
        Assert.Equal(masterBytes, File.ReadAllBytes(master));         // master itself did not change
        var back = SceneIO.Load(master);
        Assert.Equal(2, back.Externals.Values.Count(s => s != null));
        Assert.True(Tris(back) < before / 2);
        Assert.Throws<InvalidOperationException>(() => SceneIO.Save(sc, Path.Combine(dir, "again.flt")));   // stale after overwriting
    }

    [Fact]
    public void ExternalCopiesKeepTheirFormat()
    {
        Put("db/tile_a.flt", Grid(20, "tile_a"));
        var master = Put("db/master.flt", Grid(0, "master", false, "tile_a.flt"));
        var sc = SceneIO.Load(master);
        Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object> { ["simplify.ratio"] = 0.25, ["simplify.min_tris"] = 10 });
        var outp = Path.Combine(dir, "out", "master.optimized.flt");
        Directory.CreateDirectory(Path.GetDirectoryName(outp)!);
        SceneIO.Save(sc, outp);
        Assert.EndsWith(Path.Combine("master.optimized_files", "tile_a.flt"), sc.SavedExternals.Single());
        var back = SceneIO.Load(outp);
        Assert.Equal(Tris(sc), Tris(back));
    }

    [Fact]
    public void GeneratedLodLevelsBecomeLodRecords()
    {
        var src = Put("lod.flt", Grid(30));
        var sc = SceneIO.Load(src);
        var (results, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object> { ["generate_lod.min_tris"] = 100 });
        Assert.All(results, r => Assert.Null(r.Error));
        var outp = Path.Combine(dir, "lod.optimized.flt");
        SceneIO.Save(sc, outp);
        var back = SceneIO.Load(outp);
        var lods = back.IterNodes().Where(n => n.Kind == Kind.Lod).ToList();
        Assert.True(lods.Count >= 3, $"{lods.Count} LOD records");
        Assert.Equal(Tris(sc), Tris(back));
    }

    [Fact]
    public void GeneratedLodRecordsGetATransitionRange()
    {
        // image generators that blend LOD levels use the transition range (offset 64): 10 % of the switch-in distance
        var src = Put("lodt.flt", Grid(30));
        var sc = SceneIO.Load(src);
        Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object> { ["generate_lod.min_tris"] = 100 });
        var outp = Path.Combine(dir, "lodt.optimized.flt");
        SceneIO.Save(sc, outp);
        var lods = FltFile.ReadRecords(File.ReadAllBytes(outp)).Where(r => r.op == 73).ToList();
        Assert.NotEmpty(lods);
        foreach (var r in lods)
        {
            double switchIn = BinaryPrimitives.ReadDoubleBigEndian(r.data.AsSpan(16));
            double transition = BinaryPrimitives.ReadDoubleBigEndian(r.data.AsSpan(64));
            if (switchIn >= 1e29) Assert.Equal(0.0, transition);
            else Assert.InRange(transition, switchIn * 0.0999, switchIn * 0.1001);
        }
    }

    [Fact]
    public void BillboardCardBecomesOneAxialBillboardQuadFace()
    {
        var src = Put("bb.flt", Grid(4));
        var sc = SceneIO.Load(src);
        var (tsc, tree) = ReplaceTests.Tree();
        tsc.Root!.Children.Clear();
        sc.Root!.Children.Add(tree);
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, new Dictionary<string, object> { ["replace_cards.mode"] = "billboard", ["replace_cards.texture"] = 64 },
            targets: new List<Node> { tree });
        Assert.All(results, r => Assert.Null(r.Error));
        var outp = Path.Combine(dir, "bb.out.flt");
        SceneIO.Save(sc, outp);
        var recs = FltFile.ReadRecords(File.ReadAllBytes(outp));
        var faces = recs.Select((r, i) => (r, i)).Where(x => x.r.op == 5 && x.r.data[25] == 2).ToList();
        var face = Assert.Single(faces);
        Assert.Equal(0, face.r.data[18]);                                    // back face culled
        var vl = recs.Skip(face.i).First(r => r.op == 72);
        Assert.Equal(4 + 4 * 4, vl.data.Length);                             // one quad
        var back = SceneIO.Load(outp);
        var bb = Assert.Single(back.IterNodes().Where(n => n.Kind == Kind.Geometry && n.Name.Contains("billboard")).ToList());
        var V = bb.Geom!.Vertices!.Data;
        for (int i = 0; i < V.Length / 3; i++) { Assert.InRange(V[3 * i], 7.9, 12.1); Assert.InRange(V[3 * i + 1], 19.9, 20.1); }
    }

    [Theory]
    [InlineData("safe")]
    [InlineData("aggressive")]
    [InlineData("lod")]
    public void PresetsRoundTripInPlace(string preset)
    {
        Put("db/tile_a.flt", Grid(25, "tile_a", hidden: true));
        Put("db/sub/tile_b.flt", Grid(25, "tile_b"));
        var master = Put("db/master.flt", Grid(10, "master", false, "tile_a.flt", "sub/tile_b.flt"));
        var sc = SceneIO.Load(master);
        var (results, _, after) = Optimizer.Run(sc, Optimizer.Presets[preset], new Dictionary<string, object> { ["simplify.min_tris"] = 100, ["generate_lod.min_tris"] = 100 });
        Assert.All(results, r => Assert.Null(r.Error));
        SceneIO.Save(sc, master, overwriteExternals: true);
        Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(dir, "db")), d => d.EndsWith("_files"));
        var back = SceneIO.Load(master);
        Assert.Equal(2, back.Externals.Values.Count(s => s != null));
        var b = Analyzer.Analyze(back, deepChecks: false).Summary();
        Assert.Equal(Convert.ToInt64(after["triangles"]), Convert.ToInt64(b["triangles"]));
        if (preset != "lod")   // OpenFlight keeps each LOD level as its own LOD record: read back they are siblings, not one osg::LOD
            Assert.Equal(Convert.ToInt64(after["visible_triangles"]), Convert.ToInt64(b["visible_triangles"]));
    }

    [Fact]
    public void NestedExternalTreeIsOverwrittenInPlace()
    {
        // master -> regions -> tiles, three levels in sub folders; one tile is used by both regions
        var tiles = new[] { "db/r1/t/tile_1.flt", "db/r1/t/tile_2.flt", "db/r2/tile_3.flt", "db/shared/tile_s.flt" };
        foreach (var t in tiles) Put(t, Grid(20, Path.GetFileNameWithoutExtension(t)));
        Put("db/r1/region_1.flt", Grid(12, "region_1", false, "t/tile_1.flt", "t/tile_2.flt", "../shared/tile_s.flt"));
        Put("db/r2/region_2.flt", Grid(12, "region_2", false, "tile_3.flt", "../shared/tile_s.flt"));
        var master = Put("db/master.flt", Grid(0, "master", false, "r1/region_1.flt", "r2/region_2.flt"));
        var files = Directory.GetFiles(Path.Combine(dir, "db"), "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();
        var sc = SceneIO.Load(master);
        Assert.Equal(6, sc.Externals.Values.Count(s => s != null));
        long before = Tris(sc);
        var (_, _, after) = Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object> { ["simplify.ratio"] = 0.25, ["simplify.min_tris"] = 100 });
        SceneIO.Save(sc, master, overwriteExternals: true);
        // the same files in the same folders, nothing added
        Assert.Equal(files, Directory.GetFiles(Path.Combine(dir, "db"), "*", SearchOption.AllDirectories).OrderBy(x => x).ToList());
        foreach (var t in tiles.Concat(new[] { "db/r1/region_1.flt", "db/r2/region_2.flt" }))
            Assert.Contains(Path.GetFullPath(Path.Combine(dir, t)), sc.SavedExternals);
        var back = SceneIO.Load(master);
        Assert.Equal(6, back.Externals.Values.Count(s => s != null));
        Assert.Equal(Convert.ToInt64(after["triangles"]), Tris(back));
        Assert.True(Tris(back) < before / 2);
        foreach (var t in tiles)   // every tile on its own is smaller now
            Assert.True(Tris(SceneIO.Load(Path.Combine(dir, t), new LoadOptions { FollowProxy = false })) < 800);
    }

    [Fact]
    public void GeometrySharedFromAnotherFileIsRewritten()
    {
        // share_duplicates can give a node the geometry (and face records) of another, larger file
        var big = SceneIO.Load(Put("big.flt", Grid(12, hidden: true)));
        var small = SceneIO.Load(Put("small.flt", Grid(2)));
        var bigGeo = big.IterNodes().First(n => n.Kind == Kind.Geometry);
        var smallGeo = small.IterNodes().First(n => n.Kind == Kind.Geometry);
        smallGeo.Src = bigGeo.Src;
        smallGeo.Mark("geometry");
        var outp = Path.Combine(dir, "small2.flt");
        SceneIO.Save(small, outp);
        Assert.Equal(8, Tris(SceneIO.Load(outp)));
    }

    [Fact]
    public void ReplicatedGroupsAreDrawnAndWrittenBack()
    {
        var src = Put("rep.flt", Grid(3, "fence", false, 4, Array.Empty<string>()));
        var sc = SceneIO.Load(src);
        Assert.Equal(5 * 18, Tris(sc));                                  // the group and 4 copies
        Assert.Equal(4 * 8.0 + 3, Analyzer.Analyze(sc).Total.BBox!.Value.hi[0], 6);
        var outp = Path.Combine(dir, "rep2.flt");
        SceneIO.Save(sc, outp);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(outp));   // the replicate record is written once, unchanged
        var sc2 = SceneIO.Load(src);
        Optimizer.Run(sc2, new[] { "simplify" }, new Dictionary<string, object> { ["simplify.ratio"] = 0.5, ["simplify.min_tris"] = 1 });
        SceneIO.Save(sc2, outp);
        Assert.Equal(Tris(sc2), Tris(SceneIO.Load(outp)));
    }

    [Fact]
    public void DeeplyNestedExternalFilesAreLoaded()
    {
        // 8 levels: level_0 -> level_1 -> ... -> level_7 (the old limit stopped after 4)
        for (int i = 7; i >= 0; i--)
            Put($"deep/level_{i}.flt", i == 7 ? Grid(2, $"level_{i}") : Grid(2, $"level_{i}", false, $"level_{i + 1}.flt"));
        var sc = SceneIO.Load(Path.Combine(dir, "deep/level_0.flt"));
        Assert.Equal(7, sc.Externals.Values.Count(s => s != null));
        Assert.Equal(8 * 8, Tris(sc));
        Assert.DoesNotContain(sc.Warnings, w => w.Contains("not loaded"));
    }

    [Fact]
    public void MissingExternalFilesOfSubFilesAreReported()
    {
        Put("miss/sub.flt", Grid(1, "sub", false, "gone.flt"));
        var sc = SceneIO.Load(Put("miss/master.flt", Grid(1, "master", false, "sub.flt")));
        Assert.Contains(sc.Warnings, w => w.Contains("gone.flt") && w.Contains("sub.flt"));
    }

    [Fact]
    public void SelectionIsShownInOrangeWhereItSitsInTheScene()
    {
        // a replicated fence: the object selected below the second copy must be drawn there, inside the grey scene
        var sc = SceneIO.Load(Put("sel.flt", Grid(3, "fence", false, 2, Array.Empty<string>())));
        var rep = Analyzer.Analyze(sc);
        var obj = sc.IterNodes().First(n => n.Cls == "FLT Object");
        var copy1 = sc.IterNodes().First(n => n.Name == "fence #1");
        var fence = copy1.Children[0];
        var path = new List<Node> { sc.Root!, sc.IterNodes().First(n => n.Cls == "FLT Replicate"), copy1, fence };   // what the tree row gives
        var w = SoftRender.PathMatrix(path);
        Assert.Equal(8.0, w[3], 9);                                  // the second copy is 3 + 5 to the right
        Assert.Contains(SoftRender.ParentMatrix(obj, rep.Parents)[3], new[] { 0.0, 8.0, 16.0 });   // without a row: one of the copies
        var sel = SoftRender.GeometryItems(obj, start: w).Select(x => SoftRender.MeshFromGeom(x.node.Geom, x.world, x.ss)!.WithStyle(2)).ToList();
        var ctx = SoftRender.Collect(sc.Root!).Select(m => m.WithStyle(1)).ToList();
        Assert.Equal(54, ctx.Sum(m => m.NumTris));
        var cam = SoftRender.Camera.Fit(ctx);
        var (img, _) = SoftRender.Render(sel.Concat(ctx).ToList(), cam, 200, 100, backfaces: false);
        int orange = 0, grey = 0;
        for (int p = 0; p < img.Length; p += 4)
        {
            int b = img[p], g = img[p + 1], r = img[p + 2];
            if (r > g + 40 && g > b + 40) orange++;
            else if (Math.Abs(r - b) < 25 && r > 60) grey++;
        }
        Assert.True(orange > 50, $"{orange} orange pixels");
        Assert.True(grey > orange, $"{grey} grey / {orange} orange");
    }

    [Fact]
    public void SubfacesAreReadAndWrittenBackAsSubfaces()
    {
        var src = Put("decal.flt", Grid(4, "wall", false, 0, Array.Empty<string>(), decals: true));
        var sc = SceneIO.Load(src);
        Assert.Equal(32 + 16, Tris(sc));                                    // 16 decals on the wall
        var outp = Path.Combine(dir, "decal2.flt");
        SceneIO.Save(sc, outp);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(outp));      // unchanged: byte for byte, subfaces in place
    }

    [Theory]
    [InlineData("wall")]
    [InlineData("decals")]
    [InlineData("both")]
    public void SubfacesSurviveRewritingTheWallOrTheDecals(string change)
    {
        var src = Put($"decal_{change}.flt", Grid(4, "wall", false, 0, Array.Empty<string>(), decals: true));
        var sc = SceneIO.Load(src);
        var geos = sc.IterNodes().Where(n => n.Kind == Kind.Geometry).ToList();
        Assert.Equal(2, geos.Count);                                        // wall (untextured) and decals (pattern 0)
        foreach (var g in geos)
        {
            bool decal = g.StateSet?.Textures.Count > 0 || g.Name != "untextured";
            if (change == "both" || (change == "decals") == decal) { g.Geom!.Prims.Reverse(); g.Mark("geometry"); }
        }
        var outp = Path.Combine(dir, $"decal_{change}2.flt");
        SceneIO.Save(sc, outp);
        var back = SceneIO.Load(outp);
        Assert.Equal(48, Tris(back));                                       // nothing lost, nothing doubled
        var ops = FltFile.ReadRecords(File.ReadAllBytes(outp)).Select(r => r.op).ToList();
        if (change == "wall") Assert.Equal(1, ops.Count(o => o == 19));     // the kept decals ride on the first new wall face
        if (change == "decals") Assert.Equal(16, ops.Count(o => o == 19));  // the kept wall faces keep their (now empty) subface lists
    }

    [Fact]
    public void DecalsAreLeftAloneBySimplifyAndStayOnTheWall()
    {
        var src = Put("house.flt", Grid(20, "house", false, 0, Array.Empty<string>(), decals: true));
        var sc = SceneIO.Load(src);
        Assert.Contains(sc.IterNodes(), n => n.Name.EndsWith(" decals") && n.Geom is { Optimizable: false });
        Optimizer.Run(sc, Optimizer.Presets["aggressive"], new Dictionary<string, object> { ["simplify.min_tris"] = 100 });
        var outp = Path.Combine(dir, "house2.flt");
        SceneIO.Save(sc, outp);
        var back = SceneIO.Load(outp);
        Assert.Equal(Tris(sc), Tris(back));
        Assert.True(Tris(back) < 800 + 400);                                // the wall was simplified
        Assert.Equal(400, back.IterNodes().Where(n => n.Name.EndsWith(" decals")).Sum(n => n.Geom!.Counts().tris));   // all decals, still subfaces
        Assert.DoesNotContain(sc.Warnings, w => w.Contains("decal"));
    }

    [Fact]
    public void FltSceneCanStillBeSavedAsOsgt()
    {
        var sc = SceneIO.Load(Put("c.flt", Grid(4)));
        var outp = Path.Combine(dir, "c.osgt");
        SceneIO.Save(sc, outp);
        Assert.Equal(32, Tris(SceneIO.Load(outp)));
    }

    static int Records(byte[] d, int opcode)
    {
        int n = 0;
        for (int i = 0; i + 4 <= d.Length;)
        {
            int op = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i)), len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
            if (len < 4) break;
            if (op == opcode) n++;
            i += len;
        }
        return n;
    }

    /// <summary>The data (header included) of every face record (opcode 5).</summary>
    static IEnumerable<byte[]> FaceRecords(byte[] d)
    {
        for (int i = 0; i + 4 <= d.Length;)
        {
            int op = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i)), len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
            if (len < 4) break;
            if (op == 5) yield return d.AsSpan(i, len).ToArray();
            i += len;
        }
    }

    /// <summary>Position and every texture unit's coordinates of each vertex, with the unit's texture file.</summary>
    static HashSet<string> Corners(Node g)
    {
        var o = new HashSet<string>();
        var geo = g.Geom!;
        for (int i = 0; i < geo.NumVertices; i++)
        {
            var s = $"{geo.Vertices!.Data[3 * i]:0.###} {geo.Vertices.Data[3 * i + 1]:0.###}";
            foreach (var (u, a) in geo.TexCoords) s += $" | {u}: {a.Data[2 * i]:0.###} {a.Data[2 * i + 1]:0.###}";
            o.Add(s);
        }
        return o;
    }

    [Fact]
    public void MultitextureFacesAreWrittenByteForByteWhenUnchanged()
    {
        var src = Put("multi.flt", FltTests.MultiTexBytes());
        var outp = Path.Combine(dir, "multi.out.flt");
        SceneIO.Save(SceneIO.Load(src), outp);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(outp));
    }

    [Fact]
    public void RewrittenFacesKeepTheirTextureLayers()
    {
        var src = Put("multi.flt", FltTests.MultiTexBytes());
        var sc = SceneIO.Load(src);
        var geoms = sc.IterNodes().Where(n => n.Kind == Kind.Geometry).ToList();
        var before = geoms.Select(g => (string.Join(" ", g.StateSet!.Textures.OrderBy(t => t.Unit).Select(t => $"{t.Unit}={t.File}")), Corners(g))).ToList();
        foreach (var g in geoms) g.Mark("geometry");
        var outp = Path.Combine(dir, "multi.out.flt");
        SceneIO.Save(sc, outp);
        var bytes = File.ReadAllBytes(outp);
        Assert.Equal(4, Records(bytes, 52));                               // one multitexture record per face
        Assert.Equal(3, Records(bytes, 53));                               // UV lists on the faces of the geometry with unit 1 coordinates
        var back = SceneIO.Load(outp).IterNodes().Where(n => n.Kind == Kind.Geometry).ToList();
        Assert.Equal(2, back.Count);
        foreach (var (tex, corners) in before)
        {
            var b = Assert.Single(back, g => string.Join(" ", g.StateSet!.Textures.OrderBy(t => t.Unit).Select(t => $"{t.Unit}={t.File}")) == tex);
            Assert.Equal(corners, Corners(b));
        }
        Assert.Contains(back.SelectMany(g => g.StateSet!.Summary), s => s.Contains("bump.rgb") && s.Contains("bump map"));   // the effect is kept
    }

    [Fact]
    public void GeneratedStateSetWritesUnitZeroAsTheFaceTexture()
    {
        var src = Put("multi.flt", FltTests.MultiTexBytes());
        var sc = SceneIO.Load(src);
        var g = sc.IterNodes().First(n => n.Kind == Kind.Geometry && n.Geom!.TexCoords.ContainsKey(1));
        g.StateSet = new StateSet { Textures = { new Texture { File = "detail2.rgb", Unit = 1 }, new Texture { File = "base2.rgb" } } };
        g.Mark("geometry");
        var outp = Path.Combine(dir, "multi.out.flt");
        SceneIO.Save(sc, outp);
        var back = SceneIO.Load(outp).IterNodes().Where(n => n.Kind == Kind.Geometry).ToList();
        var b = Assert.Single(back, n => n.StateSet!.Textures.Any(t => t.File == "base2.rgb"));
        Assert.Equal("base2.rgb", Assert.Single(b.StateSet!.Textures, t => t.Unit == 0).File);
        Assert.Equal("detail2.rgb", Assert.Single(b.StateSet!.Textures, t => t.Unit == 1).File);
        Assert.True(b.Geom!.TexCoords.ContainsKey(1));
    }

    [Theory]
    [InlineData("replace_cards", "whole")]
    [InlineData("replace_proxy", "whole")]
    [InlineData("replace_proxy", "per_part")]
    public void CardsAndProxyOfAMultitextureObjectKeepUnitOneInTheFlt(string pass, string parts = "whole")
    {
        // the user's case: an OpenFlight object with a second texture layer is replaced by new geometry and saved as .flt
        var src = Put("grid.flt", Grid(16));
        var sc = SceneIO.Load(src);
        var geo = sc.IterNodes().First(n => n.Kind == Kind.Geometry);
        var g0 = geo.Geom!;
        var uv = Enumerable.Range(0, g0.NumVertices).SelectMany(i => new[] { g0.Vertices!.Data[3 * i] / 16, g0.Vertices.Data[3 * i + 1] / 16 }).ToArray();
        g0.TexCoords[0] = new VArray(uv, 2, "Vec2Array");
        g0.TexCoords[1] = new VArray(uv.Select(x => 1 - x).ToArray(), 2, "Vec2Array");
        geo.StateSet = new StateSet { Textures = {
            new Texture { File = "base.rgb", Baked = LodToolsTests.HalfImage(), Exists = true, Channels = 3, Width = 64, Height = 64 },
            new Texture { File = "detail.rgb", Unit = 1, Baked = LodToolsTests.HalfImage(), Exists = true, Channels = 3, Width = 64, Height = 64 } } };
        var obj = sc.IterNodes().First(n => n.Kind == Kind.Geode || n.Children.Contains(geo));
        var (results, _, _) = Optimizer.Run(sc, new[] { pass }, new Dictionary<string, object> { [pass + ".texture"] = 64, ["replace_proxy.parts"] = parts },
            targets: new List<Node> { obj });
        Assert.All(results, r => Assert.Null(r.Error));
        if (parts == "per_part") Assert.Contains("per-part proxy", results[0].Message);   // the per-part path ran, not the single hull
        var outp = Path.Combine(dir, "grid.out.flt");
        SceneIO.Save(sc, outp);
        Assert.True(Records(File.ReadAllBytes(outp), 52) > 0);
        Assert.Contains(Directory.GetFiles(Path.Combine(dir, "grid.out_textures")), f => f.Contains("_u1") && f.EndsWith(".rgb"));
        var back = SceneIO.Load(outp).IterNodes().Where(n => n.Kind == Kind.Geometry && n.StateSet != null).ToList();
        var g = Assert.Single(back, n => n.StateSet!.Textures.Any(t => t.Unit == 1));
        Assert.True(g.Geom!.TexCoords.ContainsKey(1));
        Assert.All(g.StateSet!.Textures, t => Assert.True(t.Exists, t.File));
    }

    /// <summary>Light modes of the face records written when a grid with faces of this light mode is replaced by cards
    /// (the whole object is replaced: every face record in the file is a new one).</summary>
    List<int> CardLightModes(int lightMode)
    {
        var sc = SceneIO.Load(Put("lit.flt", Grid(16, "grid", false, 0, Array.Empty<string>(), lightMode: lightMode)));
        var geo = sc.IterNodes().First(n => n.Kind == Kind.Geometry);
        var g0 = geo.Geom!;
        g0.TexCoords[0] = new VArray(Enumerable.Range(0, g0.NumVertices).SelectMany(i => new[] { g0.Vertices!.Data[3 * i] / 16, g0.Vertices.Data[3 * i + 1] / 16 }).ToArray(), 2, "Vec2Array");
        geo.StateSet = new StateSet { Textures = { new Texture { File = "base.rgb", Baked = LodToolsTests.HalfImage(), Exists = true, Channels = 3, Width = 64, Height = 64 } } };
        var obj = sc.IterNodes().First(n => n.Kind == Kind.Geode || n.Children.Contains(geo));
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, new Dictionary<string, object> { ["replace_cards.texture"] = 64 },
            targets: new List<Node> { obj });
        Assert.All(results, r => Assert.Null(r.Error));
        var outp = Path.Combine(dir, "lit.out.flt");
        SceneIO.Save(sc, outp);
        var faces = FaceRecords(File.ReadAllBytes(outp)).ToList();
        Assert.NotEmpty(faces);
        return faces.Select(f => (int)f[48]).ToList();
    }

    [Fact]
    public void CardsOfLitFacesAreLit() => Assert.All(CardLightModes(2), m => Assert.Equal(2, m));

    [Fact]
    public void CardsOfUnlitFacesStayUnlit() => Assert.All(CardLightModes(0), m => Assert.Equal(0, m));

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public void NewGeometryWithoutASourceFaceIsLitWhenItHasNormals(bool normals, int expected)
    {
        // a geometry with no source face and no carried template: its faces are lit when it has normals
        var sc = SceneIO.Load(Put("new.flt", Grid(2, "grid", false, 0, Array.Empty<string>(), lightMode: 2)));
        var geo = sc.IterNodes().First(n => n.Kind == Kind.Geometry);
        var g = new GeomData { Vertices = new VArray(new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0 }, 3, "Vec3Array") };
        if (normals) g.Normals = new VArray(new double[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, 3, "Vec3Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: 3, indices: new[] { 0, 1, 2 }, indexType: "DrawElementsUShort"));
        geo.Geom = g;
        geo.Src = null;
        geo.Extra?.Remove(FltFaceCodes.Key);
        geo.Mark("geometry");
        var outp = Path.Combine(dir, "new.out.flt");
        SceneIO.Save(sc, outp);
        var f = Assert.Single(FaceRecords(File.ReadAllBytes(outp)));
        Assert.Equal(expected, f[48]);
    }
}
