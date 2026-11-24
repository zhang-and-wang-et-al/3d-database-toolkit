using System.Buffers.Binary;
using SceneKit;
using Xunit;

/// <summary>OpenFlight reader and FLT databases split into external files (ports of the Python FLT tests).</summary>
public class FltTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"skflt_{Guid.NewGuid():N}");

    public FltTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    sealed class FltWriter
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

    static byte[] Matrix(double tx, double ty, double tz)
    {
        // row-vector convention: translation in the last row
        float[] m = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, (float)tx, (float)ty, (float)tz, 1 };
        var b = new byte[64];
        for (int i = 0; i < 16; i++) BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4 * i), m[i]);
        return b;
    }

    /// <summary>tests/test_scenekit.py flt_bytes(): header, vertex palette, group > LOD > object > 2 faces
    /// (a quad split in two). groupMatrix puts a matrix record on the group, faceMatrix one on the first face.</summary>
    static byte[] FltBytes(string group = "group1", double? groupX = null, double? faceX = null, string? extRefA = null, string? extRefB = null,
        byte secondTemplate = 0, ushort secondTransparency = 0)
    {
        var w = new FltWriter();
        var hdr = new byte[320];
        Name("db", 8).CopyTo(hdr, 0);
        BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(8), 1600);   // revision at record offset 12
        w.Rec(1, hdr);
        var vpal = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(vpal, 8 + 4 * 40);
        w.Rec(67, vpal);
        long palStart = w.Length - 8;
        var offsets = new List<int>();
        foreach (var (x, y, z) in new[] { (0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 1.0, 0.0), (0.0, 1.0, 0.0) })
        {
            offsets.Add((int)(w.Length - palStart));
            var v = new byte[36];
            BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(4), x);
            BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(12), y);
            BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(20), z);
            BinaryPrimitives.WriteInt32BigEndian(v.AsSpan(32), -1);
            w.Rec(68, v);
        }
        w.Rec(10, Array.Empty<byte>());
        w.Rec(2, Name(group, 44));
        if (groupX != null) w.Rec(49, Matrix(groupX.Value, 0, 0));
        w.Rec(10, Array.Empty<byte>());
        foreach (var ext in new[] { extRefA, extRefB })
            if (ext != null) w.Rec(63, Name(ext, 216));
        var lod = Name("lod", 76);
        BinaryPrimitives.WriteDoubleBigEndian(lod.AsSpan(12), 500.0);   // switch in / out at record offsets 16 / 24
        BinaryPrimitives.WriteDoubleBigEndian(lod.AsSpan(20), 0.0);
        w.Rec(73, lod);
        w.Rec(10, Array.Empty<byte>());
        w.Rec(4, Name("obj1", 28));
        w.Rec(10, Array.Empty<byte>());
        bool first = true;
        foreach (var tri in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } })
        {
            var face = Name("f", 76);
            BinaryPrimitives.WriteInt16BigEndian(face.AsSpan(24), -1);              // texture pattern (record offset 28)
            BinaryPrimitives.WriteUInt32BigEndian(face.AsSpan(40), 0x40000000);     // flags (record offset 44): no colour
            if (!first) face[21] = secondTemplate;                                  // template / billboard (record offset 25)
            if (!first) BinaryPrimitives.WriteUInt16BigEndian(face.AsSpan(36), secondTransparency);   // transparency (record offset 40)
            w.Rec(5, face);
            if (first && faceX != null) w.Rec(49, Matrix(faceX.Value, 0, 0));
            first = false;
            w.Rec(10, Array.Empty<byte>());
            var vl = new byte[12];
            for (int i = 0; i < 3; i++) BinaryPrimitives.WriteInt32BigEndian(vl.AsSpan(4 * i), offsets[tri[i]]);
            w.Rec(72, vl);
            w.Rec(11, Array.Empty<byte>());
        }
        for (int i = 0; i < 4; i++) w.Rec(11, Array.Empty<byte>());
        return w.Bytes();
    }

    /// <summary>Template (billboard) byte of every face record in an OpenFlight file.</summary>
    static List<byte> FaceTemplates(byte[] d)
    {
        var o = new List<byte>();
        for (int i = 0; i + 4 <= d.Length;)
        {
            int op = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i)), len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
            if (len < 4) break;
            if (op == 5 && len > 25) o.Add(d[i + 25]);
            i += len;
        }
        return o;
    }

    [Fact]
    public void BillboardFacesStayBillboards()
    {
        // one object, two faces with the same (no) texture: a normal face and an axial billboard face (template 2)
        var p = WriteFile("bb.flt", FltBytes(secondTemplate: 2));
        var sc = SceneIO.Load(p);
        var geoms = sc.IterNodes(externals: true).Where(n => n.Kind == Kind.Geometry).ToList();
        Assert.Equal(2, geoms.Count);                                       // not merged into one geometry
        var bb = Assert.Single(geoms, g => !g.Geom!.Optimizable);
        Assert.Contains(bb.Geom!.Notes, s => s.Contains("billboard"));
        // passes that would merge or rebuild the faces leave the billboard alone; saving keeps one billboard face
        Optimizer.Run(sc, new[] { "weld", "merge_geometries", "triangulate", "clean" });
        var outp = Path.Combine(dir, "bb.out.flt");
        SceneIO.Save(sc, outp);
        var tpl = FaceTemplates(File.ReadAllBytes(outp));
        Assert.Equal(1, tpl.Count(t => t == 2));
        Assert.Contains((byte)0, tpl);
    }

    [Fact]
    public void BillboardDrawablesInOsgtAreNotOptimised()
    {
        const string hand = @"#Ascii Scene
#Version 161
#Generator OpenSceneGraph 3.6.5

osg::Billboard {
  UniqueID 1
  Name ""tree""
  Drawables 1 {
    osg::Geometry {
      UniqueID 2
      PrimitiveSetList 1 {
        osg::DrawElementsUShort {
          UniqueID 3
          Mode TRIANGLES
          vector 6 {
            0 1 2 0 2 3
          }
        }
      }
      VertexArray TRUE {
        osg::Vec3Array {
          UniqueID 4
          Binding BIND_PER_VERTEX
          vector 4 {
            0 0 0
            1 0 0
            1 0 1
            0 0 1
          }
        }
      }
    }
  }
  Mode AXIAL_ROT
  Axis 0 0 1
  Normal 0 -1 0
  PositionList 1 {
    5 0 0
  }
}
";
        var sc = SceneIO.Load(WriteFile("hand.osgt", System.Text.Encoding.ASCII.GetBytes(hand)));
        var g = Assert.Single(sc.IterNodes(externals: true), n => n.Kind == Kind.Geometry);
        Assert.False(g.Geom!.Optimizable);
    }

    [Fact]
    public void FaceTransparencyMakesItsOwnTransparentGeometry()
    {
        var sc = SceneIO.Load(WriteFile("glass.flt", FltBytes(secondTransparency: 40000)));
        var geoms = sc.IterNodes(externals: true).Where(n => n.Kind == Kind.Geometry).ToList();
        Assert.Equal(2, geoms.Count);
        Assert.Single(geoms, g => g.StateSet?.Transparent == true);
        Assert.Single(geoms, g => g.StateSet?.Transparent != true);
    }

    string WriteFile(string name, byte[] data)
    {
        var p = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    [Fact]
    public void ReadsHierarchyAndLod()
    {
        var sc = SceneIO.Load(WriteFile("db.flt", FltBytes()));
        Assert.Equal("flt", sc.Fmt);
        Assert.Equal(2, Analyzer.Analyze(sc).Total.Tris);
        var lods = sc.IterNodes().Where(n => n.Kind == Kind.Lod).ToList();
        Assert.Single(lods);
        Assert.Equal(new List<(double, double)> { (0.0, 500.0) }, lods[0].Ranges);
        Assert.Equal(1600, sc.Info["openflight_revision"]);
        Assert.Equal("meters", sc.Info["units"]);
    }

    [Fact]
    public void SavedAsOsgt()
    {
        var sc = SceneIO.Load(WriteFile("db.flt", FltBytes()));
        var outp = Path.Combine(dir, "db.osgt");
        SceneIO.Save(sc, outp);
        Assert.Equal(2, Analyzer.Analyze(SceneIO.Load(outp)).Total.Tris);
    }

    [Fact]
    public void FaceMatrixMovesTheFace()
    {
        var sc = SceneIO.Load(WriteFile("mat.flt", FltBytes(faceX: 10)));
        Assert.Equal(11.0, Analyzer.Analyze(sc).Total.BBox!.Value.hi[0], 9);
    }

    [Fact]
    public void GroupMatrixSurvivesSaving()
    {
        var sc = SceneIO.Load(WriteFile("grp.flt", FltBytes("tree1", groupX: 320)));
        var tree = sc.IterNodes().Single(n => n.Name == "tree1");
        Assert.Equal(Kind.Transform, tree.Kind);
        var outp = Path.Combine(dir, "grp.osgt");
        SceneIO.Save(sc, outp);
        var back = SceneIO.Load(outp).IterNodes().Single(n => n.Name == "tree1");
        Assert.Equal(320.0, back.Matrix![3], 6);
    }

    string Database()
    {
        WriteFile("db/tile_a.flt", FltBytes("tile_a", groupX: 10));
        WriteFile("db/tile_b.flt", FltBytes("tile_b", groupX: 20));
        return WriteFile("db/master.flt", FltBytes("master", extRefA: "tile_a.flt", extRefB: "tile_b.flt"));
    }

    [Fact]
    public void ExternalsAreOptimisedAndSavedAsCopies()
    {
        var sc = SceneIO.Load(Database());
        Assert.Equal(2, sc.Externals.Values.Count(s => s != null));
        var (results, before, after) = Optimizer.Run(sc, Optimizer.Presets["safe"]);
        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal(6L, Convert.ToInt64(before["triangles"]));
        var outp = Path.Combine(dir, "out", "master.optimized.osgt");
        Directory.CreateDirectory(Path.GetDirectoryName(outp)!);
        SceneIO.Save(sc, outp);
        Assert.Equal(2, sc.SavedExternals.Count);
        Assert.Contains("master.optimized_files/tile_a.flt", File.ReadAllText(outp));   // external copies keep their format
        var back = Analyzer.Analyze(SceneIO.Load(outp), deepChecks: false).Summary();
        Assert.Equal(Convert.ToInt64(after["triangles"]), Convert.ToInt64(back["triangles"]));
        Assert.Equal(Convert.ToInt64(after["draw_calls"]), Convert.ToInt64(back["draw_calls"]));
    }

    [Fact]
    public void KeepExternalsLeavesExternalFilesAlone()
    {
        var sc = SceneIO.Load(Database());
        Optimizer.Run(sc, Optimizer.Presets["safe"], new Dictionary<string, object> { ["include_externals"] = false });
        var outp = Path.Combine(dir, "keep", "master.optimized.osgt");
        Directory.CreateDirectory(Path.GetDirectoryName(outp)!);
        SceneIO.Save(sc, outp);
        Assert.Empty(sc.SavedExternals);
        Assert.Contains("tile_a.flt", File.ReadAllText(outp));   // still references the original external file
    }

    [Fact]
    public void FltNodesCountAsPlainButSpecialOnesDoNot()
    {
        var sc = SceneIO.Load(Database());
        var plain = sc.IterNodes(externals: true).Where(Optimizer.IsPlain).Select(n => n.Cls).ToHashSet();
        Assert.Contains("FLT Object", plain);
        Assert.False(Optimizer.IsPlain(sc.IterNodes().First(n => n.Cls == "FLT LOD")));
        Assert.False(Optimizer.HasMeaningfulName(new Node(Kind.Group, "FLT Group", "g12")));
        Assert.True(Optimizer.HasMeaningfulName(new Node(Kind.Group, "FLT Group", "harbour_crane")));
    }

    static byte[] TexPal(string file, int index)
    {
        var t = new byte[212];
        System.Text.Encoding.ASCII.GetBytes(file).CopyTo(t, 0);
        BinaryPrimitives.WriteInt32BigEndian(t.AsSpan(200), index);   // record offset 204
        return t;
    }

    /// <summary>Multitexture record: (unit, texture pattern, effect) layers.</summary>
    static byte[] Multi(params (int unit, short tex, ushort effect)[] layers)
    {
        var b = new byte[4 + 8 * layers.Length];
        uint mask = 0;
        int o = 4;
        foreach (var (unit, tex, effect) in layers.OrderBy(l => l.unit))
        {
            mask |= 0x80000000u >> (unit - 1);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(o), tex);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o + 2), effect);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(o + 4), -1);   // texture mapping
            o += 8;
        }
        BinaryPrimitives.WriteUInt32BigEndian(b, mask);
        return b;
    }

    /// <summary>header, texture palette (base.rgb 0, detail.rgb 1, bump.rgb 2; none of the files exist), 4 vertices with uv0,
    /// object > faces with base.rgb and a layer on unit 1: (0 1 2) and (0 2 3) with detail.rgb and UV lists (uv1 = 10 + v, 20 + v;
    /// vertex 0 of the second face 99 99), (1 2 3) with detail.rgb and no UV list, (0 1 2) with bump.rgb as a bump map.</summary>
    internal static byte[] MultiTexBytes()
    {
        var w = new FltWriter();
        var hdr = new byte[320];
        Name("db", 8).CopyTo(hdr, 0);
        BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(8), 1600);
        w.Rec(1, hdr);
        w.Rec(64, TexPal("base.rgb", 0));
        w.Rec(64, TexPal("detail.rgb", 1));
        w.Rec(64, TexPal("bump.rgb", 2));
        var vpal = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(vpal, 8 + 4 * 48);
        w.Rec(67, vpal);
        long palStart = w.Length - 8;
        var offsets = new List<int>();
        foreach (var (x, y) in new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0) })
        {
            offsets.Add((int)(w.Length - palStart));
            var v = new byte[44];
            BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(4), x);
            BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(12), y);
            BinaryPrimitives.WriteSingleBigEndian(v.AsSpan(28), (float)x);   // uv0 at record offset 32
            BinaryPrimitives.WriteSingleBigEndian(v.AsSpan(32), (float)y);
            BinaryPrimitives.WriteInt32BigEndian(v.AsSpan(40), -1);
            w.Rec(71, v);
        }
        w.Rec(10, Array.Empty<byte>());
        w.Rec(2, Name("group", 44));
        w.Rec(10, Array.Empty<byte>());
        w.Rec(4, Name("obj", 28));
        w.Rec(10, Array.Empty<byte>());
        void Face(int[] corners, short layerTex, ushort effect, (float u, float v)[]? uv1)
        {
            var face = Name("f", 76);
            BinaryPrimitives.WriteInt16BigEndian(face.AsSpan(24), 0);               // base.rgb
            BinaryPrimitives.WriteUInt32BigEndian(face.AsSpan(40), 0x40000000);     // no colour
            w.Rec(5, face);
            w.Rec(52, Multi((1, layerTex, effect)));
            w.Rec(10, Array.Empty<byte>());
            var vl = new byte[4 * corners.Length];
            for (int i = 0; i < corners.Length; i++) BinaryPrimitives.WriteInt32BigEndian(vl.AsSpan(4 * i), offsets[corners[i]]);
            w.Rec(72, vl);
            if (uv1 != null)
            {
                var ul = new byte[4 + 8 * uv1.Length];
                BinaryPrimitives.WriteUInt32BigEndian(ul, 0x80000000u);   // layer 1
                for (int i = 0; i < uv1.Length; i++)
                {
                    BinaryPrimitives.WriteSingleBigEndian(ul.AsSpan(4 + 8 * i), uv1[i].u);
                    BinaryPrimitives.WriteSingleBigEndian(ul.AsSpan(8 + 8 * i), uv1[i].v);
                }
                w.Rec(53, ul);
            }
            w.Rec(11, Array.Empty<byte>());
        }
        Face(new[] { 0, 1, 2 }, 1, 0, new[] { (10f, 20f), (11f, 21f), (12f, 22f) });
        Face(new[] { 0, 2, 3 }, 1, 0, new[] { (99f, 99f), (12f, 22f), (13f, 23f) });
        Face(new[] { 1, 2, 3 }, 1, 0, null);
        Face(new[] { 0, 1, 2 }, 2, 1, null);
        for (int i = 0; i < 3; i++) w.Rec(11, Array.Empty<byte>());
        return w.Bytes();
    }

    [Fact]
    public void MultitextureLayersBecomeTextureUnitsWithTheirUvs()
    {
        var sc = SceneIO.Load(WriteFile("multi.flt", MultiTexBytes()));
        var geoms = sc.IterNodes().Where(n => n.Kind == Kind.Geometry).ToList();
        Assert.Equal(2, geoms.Count);                                       // different layer textures: separate state sets
        var detail = Assert.Single(geoms, g => g.StateSet!.Textures.Any(t => t.File == "detail.rgb"));
        var ss = detail.StateSet!;
        Assert.Equal("base.rgb", Assert.Single(ss.Textures, t => t.Unit == 0).File);
        var t1 = Assert.Single(ss.Textures, t => t.Unit == 1);
        Assert.False(t1.Exists);                                            // missing file, still listed
        Assert.Contains(ss.Summary, s => s.StartsWith("Texture2D[1]"));
        var g = detail.Geom!;
        Assert.Equal(8, g.NumVertices);                                     // uv1 differs per face corner: not welded
        var uv0 = g.TexCoords[0];
        var uv1 = g.TexCoords[1];
        Assert.Equal(8, uv1.Count);
        var seen = new HashSet<(double, double, double, double)>();
        for (int i = 0; i < 8; i++) seen.Add((g.Vertices!.Data[3 * i], g.Vertices.Data[3 * i + 1], uv1.Data[2 * i], uv1.Data[2 * i + 1]));
        foreach (var e in new[] { (0.0, 0.0, 10.0, 20.0), (1.0, 0.0, 11.0, 21.0), (1.0, 1.0, 12.0, 22.0), (0.0, 0.0, 99.0, 99.0),
                                  (0.0, 1.0, 13.0, 23.0), (1.0, 0.0, 0.0, 0.0), (1.0, 1.0, 0.0, 0.0), (0.0, 1.0, 0.0, 0.0) })
            Assert.Contains(e, seen);
        for (int i = 0; i < 8; i++) Assert.Equal(g.Vertices!.Data[3 * i], uv0.Data[2 * i]);

        var bump = Assert.Single(geoms, x => !ReferenceEquals(x, detail));
        var tb = Assert.Single(bump.StateSet!.Textures, t => t.Unit == 1);
        Assert.Equal("bump.rgb", tb.File);
        Assert.Contains(bump.StateSet!.Summary, s => s.Contains("bump map"));
        Assert.False(bump.Geom!.TexCoords.ContainsKey(1));                  // no UV list on any of its faces
    }

    [Fact]
    public void MeshVertexPoolLayersBecomeTextureUnits()
    {
        var w = new FltWriter();
        var hdr = new byte[320];
        Name("db", 8).CopyTo(hdr, 0);
        BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(8), 1600);
        w.Rec(1, hdr);
        w.Rec(64, TexPal("base.rgb", 0));
        w.Rec(64, TexPal("detail.rgb", 1));
        w.Rec(10, Array.Empty<byte>());
        w.Rec(4, Name("obj", 28));
        w.Rec(10, Array.Empty<byte>());
        var mesh = Name("m", 80);
        BinaryPrimitives.WriteInt16BigEndian(mesh.AsSpan(28), 0);               // texture at record offset 32
        BinaryPrimitives.WriteUInt32BigEndian(mesh.AsSpan(44), 0x40000000);     // flags at record offset 48
        w.Rec(84, mesh);
        w.Rec(52, Multi((1, 1, 0)));
        w.Rec(10, Array.Empty<byte>());
        var lvp = new byte[8 + 4 * 40];
        BinaryPrimitives.WriteUInt32BigEndian(lvp, 4);
        BinaryPrimitives.WriteUInt32BigEndian(lvp.AsSpan(4), 0x80000000u | 0x08000000u | 0x04000000u);   // position, uv0, uv1
        var pts = new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0) };
        for (int i = 0; i < 4; i++)
        {
            int o = 8 + 40 * i;
            BinaryPrimitives.WriteDoubleBigEndian(lvp.AsSpan(o), pts[i].Item1);
            BinaryPrimitives.WriteDoubleBigEndian(lvp.AsSpan(o + 8), pts[i].Item2);
            BinaryPrimitives.WriteSingleBigEndian(lvp.AsSpan(o + 24), (float)pts[i].Item1);
            BinaryPrimitives.WriteSingleBigEndian(lvp.AsSpan(o + 28), (float)pts[i].Item2);
            BinaryPrimitives.WriteSingleBigEndian(lvp.AsSpan(o + 32), 5f + i);
            BinaryPrimitives.WriteSingleBigEndian(lvp.AsSpan(o + 36), 7f + i);
        }
        w.Rec(85, lvp);
        var prim = new byte[8 + 4 * 4];
        BinaryPrimitives.WriteInt16BigEndian(prim, 1);                          // triangle strip
        BinaryPrimitives.WriteUInt16BigEndian(prim.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt32BigEndian(prim.AsSpan(4), 4);
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32BigEndian(prim.AsSpan(8 + 4 * i), (uint)i);
        w.Rec(86, prim);
        w.Rec(11, Array.Empty<byte>());
        w.Rec(11, Array.Empty<byte>());
        w.Rec(11, Array.Empty<byte>());
        var sc = SceneIO.Load(WriteFile("mesh.flt", w.Bytes()));
        var g = Assert.Single(sc.IterNodes(), n => n.Kind == Kind.Geometry);
        Assert.Equal("detail.rgb", Assert.Single(g.StateSet!.Textures, t => t.Unit == 1).File);
        Assert.Equal(new double[] { 5, 7, 6, 8, 7, 9, 8, 10 }, g.Geom!.TexCoords[1].Data);
        Assert.Equal(new double[] { 0, 0, 1, 0, 0, 1, 1, 1 }, g.Geom.TexCoords[0].Data);
    }

    [Fact]
    public void EmptyFileIsRejected()
    {
        var ex = Assert.ThrowsAny<Exception>(() => SceneIO.Load(WriteFile("empty.flt", Array.Empty<byte>())));
        Assert.Contains("not an OpenFlight file", ex.Message);
    }

    [Fact]
    public void UvListAfterAMorphListUsesTheZeroPercentCoordinates()
    {
        // layer 1 only; per vertex u0 v0 u100 v100
        var d = new byte[8 + 2 * 16];
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), 0x80000000u);
        float[] vals = { 1, 2, 9, 9, 3, 4, 9, 9 };
        for (int i = 0; i < vals.Length; i++) BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(8 + 4 * i), vals[i]);
        Assert.Equal(new double[] { 1, 2, 3, 4 }, FltFile.UvList(d, 2, morph: true)[1]);
        Assert.Equal(new double[] { 1, 2, 9, 9 }, FltFile.UvList(d, 2)[1]);
    }
}
