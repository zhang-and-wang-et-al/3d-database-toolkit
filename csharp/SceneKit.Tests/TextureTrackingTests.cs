using System.Buffers.Binary;
using SceneKit;
using Xunit;

/// <summary>T6 / T7 / TX1-TX5 / U11: texture units tracked through passes, texture views and edits, undo / redo.</summary>
public class TextureTrackingTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"sktex_{Guid.NewGuid():N}");

    public TextureTrackingTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    internal static GeomData Quad(params int[] uvSets)
    {
        var g = new GeomData { Vertices = new VArray(new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }, 3, "Vec3Array") };
        foreach (var u in uvSets) g.TexCoords[u] = new VArray(new double[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", indices: new[] { 0, 1, 2, 0, 2, 3 }, indexType: "DrawElementsUShort"));
        return g;
    }

    static Texture Tex(string file, int unit, bool exists = true) =>
        new() { File = file, Unit = unit, Exists = exists, Width = 64, Height = 64, Fmt = "png" };

    /// <summary>root (unit 0: base.png) > geode (unit 1: detail.png, missing) > geo (texcoord set 0 only);
    /// root > plain (no state) > bare (no texture).</summary>
    static (Scene sc, Node root, Node geode, Node geo, Node bare) Make()
    {
        var geo = new Node(Kind.Geometry, "osg::Geometry", "geo") { Geom = Quad(0) };
        var geode = new Node(Kind.Geode, "osg::Geode", "geode") { StateSet = new StateSet { Textures = { Tex("detail.png", 1, exists: false) } } };
        geode.Children.Add(geo);
        var bare = new Node(Kind.Geometry, "osg::Geometry", "bare") { Geom = Quad() };
        var plain = new Node(Kind.Geode, "osg::Geode", "plain") { Children = { bare } };
        var root = new Node(Kind.Group, "osg::Group", "root") { StateSet = new StateSet { Textures = { Tex("base.png", 0) } }, Children = { geode, plain } };
        return (new Scene("x.osgt", "osgt") { Root = root }, root, geode, geo, bare);
    }

    // ------------------------------------------------------------------ TX1 / TX2 / TX3 / TX5

    [Fact]
    public void UnitsBadgeShowsEffectiveUnitsAndWarnings()
    {
        var (sc, root, geode, geo, bare) = Make();
        var rep = Analyzer.Analyze(sc);
        var idx = new InspectorModel.FindingIndex(rep);
        Assert.Equal("0 1 ⚠", InspectorModel.RowFor(rep, idx, geo).Units);       // unit 1: missing image and no texcoords
        Assert.Equal("0 1 ⚠", InspectorModel.RowFor(rep, idx, root).Units);      // groups: union of the units drawn below
        Assert.Equal("0", InspectorModel.RowFor(rep, idx, bare).Units.Replace(" ⚠", ""));   // inherits unit 0 from the root
        Assert.EndsWith("⚠", InspectorModel.RowFor(rep, idx, bare).Units);        // ... but has no texcoords for it
        geo.Geom!.TexCoords[1] = geo.Geom.TexCoords[0];
        geode.StateSet!.Textures[0].Exists = true;
        bare.Geom!.TexCoords[0] = geo.Geom.TexCoords[0];
        rep = Analyzer.Analyze(sc);
        Assert.Equal("0 1", rep.TexIndex.Badge(geo));
        Assert.Equal("0 1", rep.TexIndex.Badge(root));
    }

    [Fact]
    public void TextureUnitsTellOwnFromInheritedAndTexcoordPairing()
    {
        var (sc, root, geode, geo, _) = Make();
        var rep = Analyzer.Analyze(sc);
        var units = InspectorModel.TextureUnits(rep, geo);
        Assert.Equal(new[] { 0, 1 }, units.Select(u => u.Unit));
        Assert.Same(root, units[0].InheritedFrom);
        Assert.True(units[0].HasTexCoords);
        Assert.Equal("ok", units[0].State);
        Assert.Same(geode, units[1].InheritedFrom);
        Assert.False(units[1].HasTexCoords);
        Assert.Equal("MISSING", units[1].State);
        var own = InspectorModel.TextureUnits(rep, geode);
        Assert.Null(own.Single(u => u.Unit == 1).InheritedFrom);
        var text = InspectorModel.Details(rep, new InspectorModel.FindingIndex(rep), geo);
        Assert.Contains("TEXTURES", text);
        Assert.Contains("unit 0: base.png", text);
        Assert.Contains("from root", text);
        Assert.Contains("uv MISSING", text);
    }

    [Fact]
    public void FindingForUnitWithoutTexcoords()
    {
        var (sc, _, geode, geo, bare) = Make();
        var rep = Analyzer.Analyze(sc);
        var fs = rep.Findings.Where(f => f.Code == "missing_texcoords").ToList();
        Assert.Equal(2, fs.Count);
        var detail = fs.Single(f => f.Textures[0].File == "detail.png");
        Assert.Equal("medium", detail.Severity);
        Assert.Equal(new[] { geo }, detail.Nodes);
        Assert.Equal(new[] { bare }, fs.Single(f => f.Textures[0].File == "base.png").Nodes);
        // generated texture coordinates need no array
        geode.StateSet!.Summary.Add("TexGen");
        rep = Analyzer.Analyze(sc);
        Assert.DoesNotContain(rep.Findings, f => f.Code == "missing_texcoords" && f.Textures[0].File == "detail.png");
    }

    [Fact]
    public void TextureTableListsStateUsersAndProblems()
    {
        var (sc, root, _, _, _) = Make();
        root.StateSet!.Textures[0].Resize = (32, 32);
        var rep = Analyzer.Analyze(sc);
        var rows = InspectorModel.TextureTable(rep);
        Assert.Equal(2, rows.Count);
        var b = rows.Single(r => r.File == "base.png");
        Assert.Equal("0", b.Unit);
        Assert.Equal("resize -> 32x32", b.State);
        Assert.Equal(new[] { root }, b.UserNodes);
        Assert.Equal(2, b.Drawables);
        Assert.Equal(1, b.NoTexCoords);
        var d = rows.Single(r => r.File == "detail.png");
        Assert.Equal("MISSING", d.State);
        Assert.Contains("missing image", d.Problems);
        Assert.Contains("1 geometry without texcoord set 1", d.Problems);
        Assert.Single(InspectorModel.TextureTable(rep, "DETAIL"));
        Assert.Single(InspectorModel.TextureTable(rep, "resize"));
        root.Children[1].Children[0].Geom!.TexCoords[0] = root.Children[0].Children[0].Geom!.TexCoords[0];
        rep = Analyzer.Analyze(sc);
        Assert.Equal("detail.png", Assert.Single(InspectorModel.TextureTable(rep, problemsOnly: true)).File);
    }

    // ------------------------------------------------------------------ T6 / T7

    internal static Node Holder(Node geo, params Texture[] tex)
    {
        var h = new Node(Kind.Geode, "osg::Geode", geo.Name + "_geode") { StateSet = new StateSet() };
        h.StateSet.Textures.AddRange(tex);
        h.Children.Add(geo);
        return h;
    }

    [Fact]
    public void ReplacedSubtreeThatLosesAUnitIsFlagged()
    {
        var geo = new Node(Kind.Geometry, "osg::Geometry", "tree") { Geom = Quad(0, 1) };
        var old = Holder(geo, Tex("bark.png", 0), Tex("detail.png", 1));
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { old } };
        var sc = new Scene("x.osgt", "osgt") { Root = root };
        var t0 = History.QuickTotals(sc);
        Assert.Equal(2, t0["texture_units"]);
        Assert.Equal(2, t0["textures"]);
        var before = History.GeometryState(sc);
        // a "pass" replaces the subtree with a card that only has unit 0 (made from a normalised copy of the geometry)
        var card = new Node(Kind.Geometry, "osg::Geometry", "card") { Geom = Quad(0) };
        root.Children[0] = Holder(card, Tex("card.png", 0));
        var after = History.GeometryState(sc);
        var ch = Assert.Single(History.Diff("replace_cards", before, after,
            new Dictionary<Node, (GeomData?, string)>(ReferenceEqualityComparer.Instance) { [card] = (geo.Geom.ShallowCopy(), "cards") }));
        Assert.Equal("0 1", ch.UnitsBefore!.ToString());
        Assert.Equal("0", ch.UnitsAfter!.ToString());
        Assert.Equal("texture unit 1 dropped", ch.UnitDelta);
        Assert.Equal("check", InspectorModel.ChangeVerdict(ch));
        Assert.StartsWith("texture unit 1 dropped; ", InspectorModel.ChangeCheckText(ch));
        // T7: each side keeps its own state for the preview
        Assert.Equal(new[] { "bark.png", "detail.png" }, ch.StateBefore!.Textures.Select(t => t.File));
        Assert.Equal("card.png", Assert.Single(ch.StateAfter!.Textures).File);
        var t1 = History.QuickTotals(sc);
        Assert.Equal(1, t1["texture_units"]);
        var log = InspectorModel.LogResults(new List<PassResult> { new("replace_cards", "done") { Changes = new() { ch }, TotalsBefore = t0, Totals = t1 } },
            Analyzer.Analyze(sc, findings: false).Summary(), Analyzer.Analyze(sc, findings: false).Summary());
        Assert.Contains(log, l => l.Contains("texture units 2->1"));
    }

    [Fact]
    public void ChangedGeometryThatLosesTexcoordsIsFlagged()
    {
        var geo = new Node(Kind.Geometry, "osg::Geometry", "g") { Geom = Quad(0, 1) };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { Holder(geo, Tex("a.png", 0), Tex("b.png", 1)) } } };
        var before = History.GeometryState(sc);
        var g = geo.Geom!.ShallowCopy();
        g.TexCoords.Remove(1);
        geo.Geom = g;
        var ch = Assert.Single(History.Diff("simplify", before, History.GeometryState(sc)));
        Assert.Equal("texture unit 1 lost its texcoords", ch.UnitDelta);
        // the same geometry with a dropped unit only: still reported
        before = History.GeometryState(sc);
        geo.Geom = Quad(0, 1);
        before = History.GeometryState(sc);
        sc.Root!.Children[0].StateSet!.Textures.RemoveAt(1);
        ch = Assert.Single(History.Diff("x", before, History.GeometryState(sc)));
        Assert.Equal("texture unit 1 dropped", ch.UnitDelta);
        Assert.Equal("texture state changed", ch.Note);
        // nothing lost: no delta, no change
        before = History.GeometryState(sc);
        Assert.Empty(History.Diff("x", before, History.GeometryState(sc)));
    }

    [Fact]
    public void LodLevelComparesWithItsSourceGeometry()
    {
        var (sc, _, geo) = RebakeTests.TexturedSphere();
        var (results, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object>
            { ["generate_lod.min_tris"] = 100, ["generate_lod.ratios"] = "0.5" });
        Assert.Null(results[0].Error);
        var lvl = results[0].Changes.First(c => c.Kind == "lod level");
        Assert.Equal("0", lvl.UnitsBefore!.ToString());
        Assert.Equal("0", lvl.UnitsAfter!.ToString());
        Assert.Null(lvl.UnitDelta);
        Assert.NotNull(lvl.StateBefore);
    }

    // ------------------------------------------------------------------ U11

    static (Scene sc, Node root, Node a, Node b) TwoQuads()
    {
        var a = new Node(Kind.Geometry, "osg::Geometry", "a") { Geom = Quad(0) };
        var b = new Node(Kind.Geometry, "osg::Geometry", "b") { Geom = Quad(0) };
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { a, b } };
        return (new Scene("x.osgt", "osgt") { Root = root }, root, a, b);
    }

    [Fact]
    public void UndoRedoAndHistoryEntries()
    {
        var (sc, root, a, b) = TwoQuads();
        var h = new UndoHistory();
        Assert.False(h.CanUndo);
        h.Push(new Snapshot(sc, "delete a"));
        root.Children.Remove(a);
        h.Push(new Snapshot(sc, "delete b"));
        root.Children.Remove(b);
        var e = h.Entries();
        Assert.Equal(new[] { "delete a", "delete b" }, e.Select(x => x.Label));
        Assert.All(e, x => Assert.Equal(-2, x.TrisDelta));
        Assert.All(e, x => Assert.False(x.Undone));
        Assert.Equal("delete b", h.Undo()!.Label);
        Assert.Equal(new[] { b }, root.Children);
        Assert.True(h.CanRedo);
        Assert.Equal("delete b", h.RedoLabel);
        e = h.Entries();
        Assert.True(e[1].Undone);
        Assert.Equal(-2, e[1].TrisDelta);
        Assert.Equal("delete a", h.Undo()!.Label);
        Assert.Equal(new[] { a, b }, root.Children);
        Assert.Equal("delete a", h.Redo()!.Label);
        Assert.Equal(new[] { b }, root.Children);
        Assert.Equal("delete b", h.Redo()!.Label);
        Assert.Empty(root.Children);
        Assert.False(h.CanRedo);
        h.Undo();
        Assert.True(h.CanRedo);
        h.Push(new Snapshot(sc, "hide b"));                                    // a new step clears the redo steps
        Assert.False(h.CanRedo);
        Assert.Null(h.CapNote());
    }

    [Fact]
    public void CapDropsOldestStepsAndSaysSo()
    {
        var (sc, _, a, _) = TwoQuads();
        var h = new UndoHistory();
        for (int i = 0; i < InspectorModel.UndoLevels + 3; i++)
        {
            h.Push(new Snapshot(sc, $"step {i}"));
            a.Name = $"a{i}";
        }
        Assert.Equal(InspectorModel.UndoLevels, h.UndoCount);
        Assert.Equal(3, h.Dropped);
        Assert.Equal($"3 oldest steps dropped (undo keeps the last {InspectorModel.UndoLevels})", h.CapNote());
        Assert.Equal("step 3", h.Entries()[0].Label);
        h.Clear();
        Assert.Null(h.CapNote());
    }

    [Fact]
    public void RedoOfAPassGivesTheSameResult()
    {
        var (sc, _, geo) = RebakeTests.TexturedSphere();
        var h = new UndoHistory();
        var snap = new Snapshot(sc, "simplify");
        Optimizer.DomJournal = snap.DomSwaps;
        try { Optimizer.Run(sc, new[] { "simplify" }, new() { ["simplify.ratio"] = 0.3 }, targets: new List<Node> { geo }); }
        finally { Optimizer.DomJournal = null; }
        h.Push(snap);
        var done = geo.Geom!;
        long tris = done.Counts().tris;
        h.Undo();
        Assert.NotEqual(tris, geo.Geom!.Counts().tris);
        h.Redo();
        Assert.Same(done, geo.Geom);
        Assert.Equal(tris, geo.Geom!.Counts().tris);
        Assert.True(h.Entries()[0].TrisDelta < 0);
    }

    // ------------------------------------------------------------------ TX4

    string Png(string name, int w, int h)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, ImageCodecs.WritePng(new RawImage(w, h, 3)));
        return p;
    }

    [Fact]
    public void ResizeIsUndoableAndChecked()
    {
        var p = Png("a.png", 64, 32);
        var t = Images.FillTexture(new Texture { File = "a.png" }, dir);
        var geo = new Node(Kind.Geometry, "osg::Geometry", "g") { Geom = Quad(0) };
        var sc = new Scene(Path.Combine(dir, "x.osgt"), "osgt") { Root = Holder(geo, t) };
        var h = new UndoHistory();
        Assert.True(TextureEdits.SetResize(sc, t, 32, 16, h).Ok);
        Assert.Equal((32, 16), t.Resize);
        Assert.Equal(32, t.Width);
        Assert.Equal("resize -> 32x16", TextureTracking.StateText(t));
        Assert.False(TextureEdits.SetResize(sc, t, 128, 64, h).Ok);           // larger than the image
        Assert.True(TextureEdits.SetResize(sc, t, 64, 32, h).Ok);             // its own size: no resize
        Assert.Null(t.Resize);
        h.Undo();
        Assert.Equal((32, 16), t.Resize);
        h.Undo();
        Assert.Null(t.Resize);
        Assert.Equal(64, t.Width);
        Assert.False(TextureEdits.SetResize(sc, new Texture { Embedded = true, Width = 8, Height = 8 }, 4, 4).Ok);
        var baked = new Texture { File = "b.png", Baked = new RawImage(64, 64, 3), Exists = true, Width = 64, Height = 64 };
        Assert.True(TextureEdits.SetResize(sc, baked, 16, 16).Ok);
        Assert.Equal(16, baked.Baked!.Width);
    }

    [Fact]
    public void ReplacedFileIsSavedAndUndoable()
    {
        Png("a.png", 16, 16);
        var bPath = Png("b.png", 32, 32);
        // write a textured scene, load it back: a scene with a document and texture objects from the file
        var t = Images.FillTexture(new Texture { File = "a.png" }, dir);
        var geo = new Node(Kind.Geometry, "osg::Geometry", "g") { Geom = Quad(0) };
        var src = Path.Combine(dir, "scene.osgt");
        SceneIO.Save(new Scene(src, "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { Holder(geo, t) } } }, src);
        var sc = SceneIO.Load(src);
        var tex = sc.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Single();
        Assert.IsType<Obj>(tex.Src);
        var h = new UndoHistory();
        var r = TextureEdits.ReplaceFile(sc, tex, bPath, h);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("b.png", tex.File);
        Assert.Equal(32, tex.Width);
        Assert.True(SceneIO.SceneModified(sc));
        var same = Path.Combine(dir, "same.osgt");
        SceneIO.Save(sc, same);
        Assert.Equal("b.png", SceneIO.Load(same).IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Single().File);
        var sub = Path.Combine(dir, "sub", "moved.osgt");
        Directory.CreateDirectory(Path.GetDirectoryName(sub)!);
        SceneIO.Save(sc, sub);
        var moved = SceneIO.Load(sub).IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Single();
        Assert.Equal("../b.png", moved.File);
        Assert.True(moved.Exists);
        h.Undo();
        Assert.Equal("a.png", tex.File);
        Assert.Equal(16, tex.Width);
        Assert.False(tex.Repointed);
        Assert.False(TextureEdits.ReplaceFile(sc, tex, Path.Combine(dir, "nope.png")).Ok);
    }

    [Fact]
    public void ReplacedFileIsWrittenToTheOpenFlightPalette()
    {
        Png("a.png", 16, 16);
        var bPath = Png("b.png", 32, 32);
        var src = Path.Combine(dir, "t.flt");
        File.WriteAllBytes(src, TexturedFlt("a.png"));
        var sc = SceneIO.Load(src);
        var tex = sc.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Distinct().Single();
        Assert.Equal("a.png", tex.File);
        Assert.True(TextureEdits.ReplaceFile(sc, tex, bPath).Ok);
        var outp = Path.Combine(dir, "t2.flt");
        SceneIO.Save(sc, outp);
        var back = SceneIO.Load(outp).IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Distinct().Single();
        Assert.Equal("b.png", back.File);
        Assert.Equal(32, back.Width);
    }

    /// <summary>header, texture palette (index 0), vertex palette, group > object > one textured triangle.</summary>
    static byte[] TexturedFlt(string texFile)
    {
        var ms = new MemoryStream();
        void Rec(int op, byte[] body)
        {
            var h = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)op);
            BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), (ushort)(4 + body.Length));
            ms.Write(h);
            ms.Write(body);
        }
        static byte[] Name(string s, int size) { var b = new byte[size]; System.Text.Encoding.ASCII.GetBytes(s).CopyTo(b, 0); return b; }
        var hdr = new byte[320];
        Name("db", 8).CopyTo(hdr, 0);
        BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(8), 1600);
        Rec(1, hdr);
        var pal = Name(texFile, 212);
        BinaryPrimitives.WriteInt32BigEndian(pal.AsSpan(200), 0);
        Rec(64, pal);
        var vpal = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(vpal, 8 + 3 * 40);
        Rec(67, vpal);
        long palStart = ms.Length - 8;
        var offs = new int[3];
        double[][] P = { new double[] { 0, 0, 0 }, new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 } };
        for (int i = 0; i < 3; i++)
        {
            offs[i] = (int)(ms.Length - palStart);
            var v = new byte[36];
            for (int c = 0; c < 3; c++) BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(4 + 8 * c), P[i][c]);
            BinaryPrimitives.WriteInt32BigEndian(v.AsSpan(32), -1);
            Rec(68, v);
        }
        Rec(10, Array.Empty<byte>());
        Rec(2, Name("group", 44));
        Rec(10, Array.Empty<byte>());
        Rec(4, Name("obj", 28));
        Rec(10, Array.Empty<byte>());
        var f = Name("f", 76);
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(24), 0);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(40), 0x40000000);
        Rec(5, f);
        Rec(10, Array.Empty<byte>());
        var vl = new byte[12];
        for (int i = 0; i < 3; i++) BinaryPrimitives.WriteInt32BigEndian(vl.AsSpan(4 * i), offs[i]);
        Rec(72, vl);
        Rec(11, Array.Empty<byte>());
        Rec(11, Array.Empty<byte>());
        Rec(11, Array.Empty<byte>());
        Rec(11, Array.Empty<byte>());
        return ms.ToArray();
    }

    [Fact]
    public void RemoveUnitOnlyFromGeneratedStateSets()
    {
        Png("a.png", 16, 16);
        var geo = new Node(Kind.Geometry, "osg::Geometry", "g") { Geom = Quad(0, 1) };
        var holder = Holder(geo, Tex("a.png", 0), Tex("b.png", 1));
        var sc = new Scene(Path.Combine(dir, "x.osgt"), "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { holder } } };
        var h = new UndoHistory();
        Assert.False(TextureEdits.RemoveUnit(sc, holder.StateSet!, 3, h).Ok);
        Assert.True(TextureEdits.RemoveUnit(sc, holder.StateSet!, 1, h).Ok);
        Assert.Equal(new[] { 0 }, holder.StateSet!.Textures.Select(t => t.Unit));
        h.Undo();
        Assert.Equal(new[] { 0, 1 }, holder.StateSet!.Textures.Select(t => t.Unit));
        // a state set read from a file is copied from the file when saving: not supported
        var src = Path.Combine(dir, "s.osgt");
        SceneIO.Save(sc, src);
        var loaded = SceneIO.Load(src);
        var ss = loaded.IterNodes().First(n => n.StateSet != null).StateSet!;
        var r = TextureEdits.RemoveUnit(loaded, ss, 1);
        Assert.False(r.Ok);
        Assert.Contains("not supported yet", r.Message);
        Assert.Equal(2, ss.Textures.Count);
    }

    [Fact]
    public void RemoveUnitUndoRestoresSummaryAndRedoRemovesAgain()
    {
        var geo = new Node(Kind.Geometry, "osg::Geometry", "g") { Geom = Quad(0, 1) };
        var holder = Holder(geo, Tex("a.png", 0), Tex("b.png", 1));
        holder.StateSet!.Summary.AddRange(new[] { "Texture2D[0] a.png", "Texture2D[1] b.png" });
        var sc = new Scene(Path.Combine(dir, "x.osgt"), "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { holder } } };
        var h = new UndoHistory();
        Assert.True(TextureEdits.RemoveUnit(sc, holder.StateSet!, 1, h).Ok);
        Assert.Equal(new[] { "Texture2D[0] a.png" }, holder.StateSet!.Summary);
        h.Undo();
        Assert.Equal(new[] { 0, 1 }, holder.StateSet!.Textures.Select(t => t.Unit));
        Assert.Equal(new[] { "Texture2D[0] a.png", "Texture2D[1] b.png" }, holder.StateSet!.Summary);
        h.Redo();
        Assert.Equal(new[] { 0 }, holder.StateSet!.Textures.Select(t => t.Unit));
        Assert.Equal(new[] { "Texture2D[0] a.png" }, holder.StateSet!.Summary);
    }
}
