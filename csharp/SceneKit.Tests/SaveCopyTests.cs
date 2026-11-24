using SceneKit;
using Xunit;

/// <summary>Save Copy to Folder: writers take a destination mapping (Dest) so a copy references files inside the copy.</summary>
public class SaveCopyTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"skcopy_{Guid.NewGuid():N}");
    public SaveCopyTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    string Db => Path.Combine(dir, "db");
    string Out => Path.Combine(dir, "out");
    string Mirror(string p) => Path.Combine(Out, Path.GetRelativePath(Db, p));

    static void Png(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ImageCodecs.WritePng(new RawImage(2, 2, 3)));
    }

    /// <summary>db/master.osgt with one geometry using the given textures (one per unit); db/textures/t.png is a 2x2 PNG.</summary>
    string OsgtDb(params string[] texRefs)
    {
        Png(Path.Combine(Db, "textures", "t.png"));
        var geo = new Node(Kind.Geometry, "osg::Geometry", "g") { Geom = TextureTrackingTests.Quad(0) };
        var tex = texRefs.Select((r, i) => Images.FillTexture(new Texture { File = r, Unit = i }, Db)).ToArray();
        var master = Path.Combine(Db, "master.osgt");
        SceneIO.Save(new Scene(master, "osgt") { Root = TextureTrackingTests.Holder(geo, tex) }, master);
        Directory.CreateDirectory(Out);
        return master;
    }

    DateTime started;

    /// <summary>db/master.flt referencing tile_a.flt and tile_b.flt (20x20 grids), plus notes.txt, a backup folder and an
    /// earlier copy (db_optimized); tile_a is simplified.</summary>
    (Scene sc, string root) FltDb(bool simplifyA = true, bool simplifyB = false)
    {
        var r = BuildFltDb(Db, simplifyA, simplifyB);
        started = DateTime.UtcNow;
        return r;
    }

    /// <summary>Shared with CheckpointTests: builds the database in <paramref name="db"/> (see <see cref="FltDb"/>).</summary>
    internal static (Scene sc, string root) BuildFltDb(string db, bool simplifyA = true, bool simplifyB = false)
    {
        Directory.CreateDirectory(db);
        File.WriteAllBytes(Path.Combine(db, "master.flt"), FltWriteTests.Grid(0, "master", false, "tile_a.flt", "tile_b.flt"));
        File.WriteAllBytes(Path.Combine(db, "tile_a.flt"), FltWriteTests.Grid(20, "tile_a"));
        File.WriteAllBytes(Path.Combine(db, "tile_b.flt"), FltWriteTests.Grid(20, "tile_b"));
        File.WriteAllText(Path.Combine(db, "notes.txt"), "notes");
        Directory.CreateDirectory(Path.Combine(db, "_backup_20260101_000000"));
        File.WriteAllBytes(Path.Combine(db, "_backup_20260101_000000", "old.flt"), FltWriteTests.Grid(2, "old"));
        Directory.CreateDirectory(Path.Combine(db, "db_optimized"));
        File.WriteAllBytes(Path.Combine(db, "db_optimized", "x.flt"), FltWriteTests.Grid(2, "x"));
        var sc = SceneIO.Load(Path.Combine(db, "master.flt"));
        foreach (var (tile, on) in new[] { ("tile_a.flt", simplifyA), ("tile_b.flt", simplifyB) })
        {
            if (!on) continue;
            var sub = sc.Externals.First(kv => Path.GetFileName(kv.Key) == tile).Value!;
            Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object> { ["simplify.ratio"] = 0.25, ["simplify.min_tris"] = 10 },
                targets: sub.IterNodes().ToList());
        }
        return (sc, db);
    }

    static long Tris(Scene sc) => Convert.ToInt64(Analyzer.Analyze(sc, deepChecks: false).Summary()["triangles"]);
    static IEnumerable<Texture> Textures(Scene sc) => sc.IterNodes(externals: true).Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures);

    [Fact]
    public void WithoutDestRelocatedReferencesPointAtTheOriginal()
    {
        var sc = SceneIO.Load(OsgtDb("textures/t.png"));
        var outp = Path.Combine(Out, "master.osgt");
        OsgWrite.Write(sc, outp);
        Assert.Contains("../db/textures/t.png", File.ReadAllText(outp));
    }

    [Fact]
    public void DestMapsReferencesIntoTheMirror()
    {
        var sc = SceneIO.Load(OsgtDb("textures/t.png"));
        var outp = Path.Combine(Out, "master.osgt");
        OsgWrite.Write(sc, outp, Mirror);
        var text = File.ReadAllText(outp);
        Assert.Contains("\"textures/t.png\"", text);
        Assert.DoesNotContain("../db/", text);
    }

    [Fact]
    public void AbsoluteReferenceUnderRootBecomesRelativeInTheCopy()
    {
        var sc = SceneIO.Load(OsgtDb(Path.GetFullPath(Path.Combine(Db, "textures", "t.png"))));
        var outp = Path.Combine(Out, "master.osgt");
        OsgWrite.Write(sc, outp, Mirror);
        var text = File.ReadAllText(outp);
        Assert.Contains("\"textures/t.png\"", text);
        Assert.DoesNotContain(dir.Replace('\\', '/'), text);
    }

    [Fact]
    public void DestKeepsFltExternalReferencesResolvableInTheCopy()
    {
        Directory.CreateDirectory(Db);
        Directory.CreateDirectory(Out);
        File.WriteAllBytes(Path.Combine(Db, "master.flt"), FltWriteTests.Grid(0, "master", false, "tile_a.flt"));
        File.WriteAllBytes(Path.Combine(Db, "tile_a.flt"), FltWriteTests.Grid(20, "tile_a"));
        var sc = SceneIO.Load(Path.Combine(Db, "master.flt"));
        OsgWrite.Write(sc, Path.Combine(Out, "master.flt"), Mirror);
        var before = SceneIO.Load(Path.Combine(Out, "master.flt"));
        Assert.Equal("tile_a.flt", before.IterNodes().First(n => n.Files.Count > 0).Files[0]);
        File.Copy(Path.Combine(Db, "tile_a.flt"), Path.Combine(Out, "tile_a.flt"));
        File.Delete(Path.Combine(Db, "tile_a.flt"));   // the reference can now only resolve inside the copy
        var copy = SceneIO.Load(Path.Combine(Out, "master.flt"));
        Assert.Equal("tile_a.flt", copy.IterNodes().First(n => n.Files.Count > 0).Files[0]);
        Assert.DoesNotContain(copy.Warnings, x => x.Contains("not found"));
    }

    [Fact]
    public void FullCopyMirrorsTheDatabaseAndLoadsOnItsOwn()
    {
        var (sc, root) = FltDb();
        string folder = Path.Combine(dir, "out");
        var res = SaveCopy.ToFolder(sc, folder);
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "tile_b.flt")), File.ReadAllBytes(Path.Combine(folder, "tile_b.flt")));
        Assert.NotEqual(File.ReadAllBytes(Path.Combine(root, "tile_a.flt")), File.ReadAllBytes(Path.Combine(folder, "tile_a.flt")));
        Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(folder, "_backup_20260101_000000")));
        Assert.False(Directory.Exists(Path.Combine(folder, "db_optimized")));
        var back = SceneIO.Load(res.Master);
        Assert.DoesNotContain(back.Warnings, w => w.Contains("not found"));
        Assert.Equal(Tris(sc), Tris(back));
        var manifest = File.ReadAllText(res.Manifest);
        Assert.Contains("changed   tile_a.flt", manifest);
        Assert.Contains("copied    tile_b.flt", manifest);
        Assert.Contains("Include unchanged files: yes", manifest);
        Assert.DoesNotContain(Directory.GetFiles(root, "*", SearchOption.AllDirectories), f => File.GetLastWriteTimeUtc(f) > started);   // root untouched
    }

    [Fact]
    public void ChangedOnlyCopyKeepsOriginalReferences()
    {
        var (sc, root) = FltDb();
        var res = SaveCopy.ToFolder(sc, Path.Combine(dir, "out"), new SaveCopyOptions { IncludeUnchanged = false });
        Assert.False(File.Exists(Path.Combine(dir, "out", "tile_b.flt")));
        Assert.True(File.Exists(Path.Combine(dir, "out", "tile_a.flt")));
        var back = SceneIO.Load(res.Master);
        Assert.Contains(back.Warnings, w => w.Contains("tile_b.flt"));          // the reference string is unchanged
        File.Copy(Path.Combine(root, "tile_b.flt"), Path.Combine(dir, "out", "tile_b.flt"));
        Assert.DoesNotContain(SceneIO.Load(res.Master).Warnings, w => w.Contains("not found"));
        Assert.Contains("Copy this folder over a copy of the original database", File.ReadAllText(res.Manifest));
    }

    [Fact]
    public void OutOfRootFilesGoToExternal()
    {
        Png(Path.Combine(dir, "shared", "s.png"));
        var sc = SceneIO.Load(OsgtDb("textures/t.png", "../shared/s.png"));
        var res = SaveCopy.ToFolder(sc, Out);
        Assert.True(File.Exists(Path.Combine(Out, "textures", "t.png")));
        Assert.True(File.Exists(Path.Combine(Out, "_external", "s.png")));
        var back = SceneIO.Load(res.Master);
        Assert.Equal(2, Textures(back).Count());
        Assert.All(Textures(back), t => Assert.True(t.Exists));
        Assert.All(Textures(back), t => Assert.StartsWith(Path.GetFullPath(Out), t.Path!));
        Assert.Contains("external  _external/s.png", File.ReadAllText(res.Manifest));
    }

    [Fact]
    public void ExternalNameCollisionsGetASuffix()
    {
        Png(Path.Combine(dir, "a", "tex.png"));
        Png(Path.Combine(dir, "b", "tex.png"));
        var sc = SceneIO.Load(OsgtDb("../a/tex.png", "../b/tex.png"));
        var res = SaveCopy.ToFolder(sc, Out);
        Assert.True(File.Exists(Path.Combine(Out, "_external", "tex.png")));
        Assert.True(File.Exists(Path.Combine(Out, "_external", "tex_2.png")));
        var back = SceneIO.Load(res.Master);
        Assert.All(Textures(back), t => Assert.True(t.Exists));
        Assert.Equal(new[] { "tex.png", "tex_2.png" }, Textures(back).Select(t => Path.GetFileName(t.Path!)).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void RefusesAFolderInsideTheDatabase()
    {
        var (sc, root) = FltDb();
        Assert.Throws<ArgumentException>(() => SaveCopy.ToFolder(sc, root));
        Assert.Throws<ArgumentException>(() => SaveCopy.ToFolder(sc, Path.Combine(root, "sub")));
        Assert.False(Directory.Exists(Path.Combine(root, "sub")));
    }

    [Fact]
    public void DefaultFolderIsNextToTheDatabase()
    {
        var (sc, _) = FltDb();
        Assert.Equal(Path.Combine(dir, "db_optimized"), SaveCopy.DefaultFolder(sc));
    }

    [Fact]
    public void DefaultFolderForADriveRootDatabaseIsInTemp()
    {
        var sc = new Scene(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "master.flt"), "flt");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "database_optimized"), SaveCopy.DefaultFolder(sc));
    }

    [Fact]
    public void RefusesAnAncestorOfTheDatabase()
    {
        var (sc, root) = FltDb();
        var e = Assert.Throws<ArgumentException>(() => SaveCopy.ToFolder(sc, dir));
        Assert.Contains("outside the opened database folder", e.Message);
        Assert.False(File.Exists(Path.Combine(dir, SaveCopy.ManifestName)));
        Assert.False(File.Exists(Path.Combine(dir, "master.flt")));
    }

    [Fact]
    public void RefusesAFolderHoldingReferencedFiles()
    {
        string s = Path.Combine(dir, "assets", "shared", "s.png");
        Png(s);
        var original = File.ReadAllBytes(s);
        var sc = SceneIO.Load(OsgtDb("textures/t.png", "../assets/shared/s.png"));
        foreach (var target in new[] { Path.Combine(dir, "assets", "shared"), Path.Combine(dir, "assets") })
        {
            var e = Assert.Throws<ArgumentException>(() => SaveCopy.ToFolder(sc, target));
            Assert.Contains("holds files the database references", e.Message);
            Assert.False(File.Exists(Path.Combine(target, SaveCopy.ManifestName)));
        }
        Assert.False(Directory.Exists(Path.Combine(dir, "assets", "_external")));
        Assert.Equal(original, File.ReadAllBytes(s));
        SaveCopy.ToFolder(sc, Out);   // elsewhere it works
    }

    [Fact]
    public void RepointedTextureOutsideTheRootGoesToExternal()
    {
        string nw = Path.Combine(dir, "shared", "new.png");
        Png(nw);
        var sc = SceneIO.Load(OsgtDb("textures/t.png"));
        Assert.True(TextureEdits.ReplaceFile(sc, Textures(sc).Single(), nw).Ok);
        var res = SaveCopy.ToFolder(sc, Out);
        string text = File.ReadAllText(res.Master);
        Assert.Contains("\"_external/new.png\"", text);
        Assert.DoesNotContain("../", text);
        Assert.True(File.Exists(Path.Combine(Out, "_external", "new.png")));
        Assert.All(Textures(SceneIO.Load(res.Master)), t => Assert.StartsWith(Path.GetFullPath(Out), t.Path!));
    }

    [Fact]
    public void RepointedTextureInsideTheRootStaysInTheMirror()
    {
        Png(Path.Combine(Db, "textures", "new.png"));
        var sc = SceneIO.Load(OsgtDb("textures/t.png"));
        Assert.True(TextureEdits.ReplaceFile(sc, Textures(sc).Single(), Path.Combine(Db, "textures", "new.png")).Ok);
        var res = SaveCopy.ToFolder(sc, Out);
        string text = File.ReadAllText(res.Master);
        Assert.Contains("\"textures/new.png\"", text);
        Assert.DoesNotContain("../", text);
        Assert.True(File.Exists(Path.Combine(Out, "textures", "new.png")));
    }

    [Fact]
    public void CheckpointDoesNotLeakItsTextureNamesIntoTheNextSaveOver()
    {
        Png(Path.Combine(dir, "shared", "t.png"));
        string m = OsgtDb("../shared/t.png");
        Assert.Contains("\"../shared/t.png\"", File.ReadAllText(m));
        var sc = SceneIO.Load(m);
        var n = sc.IterNodes().First(x => x.Kind == Kind.Geometry);
        n.NodeMask = 0;
        n.Mark("node_mask");
        var store = new CheckpointStore(Path.Combine(dir, "checkpoints"));
        var ck = store.Write(sc, CheckpointStore.KeyFor(m), store.NewSession(), 1, "hide", "g", "", null);
        Assert.Contains("\"_external/t.png\"", File.ReadAllText(ck.Master));   // right for the checkpoint ...
        var res = SaveOver.Run(sc, SaveOver.Plan(sc));
        Assert.Contains(m, res.Written);
        string text = File.ReadAllText(m);
        Assert.Contains("\"../shared/t.png\"", text);                              // ... but not for the database
        Assert.DoesNotContain("_external", text);
    }

    [Fact]
    public void SaveOverRefusesChangedExternalsInAnotherFormat()
    {
        var (sc, root) = FltDb();
        var originalA = File.ReadAllBytes(Path.Combine(root, "tile_a.flt"));
        string ive = Path.Combine(root, "tile_c.ive");
        var sub = new Scene(ive, "ive") { ConvertedFrom = ive, Root = new Node(Kind.Group, "osg::Group", "c") };   // as read through osgconv
        sub.Root.Mark("name");
        sc.Externals[ive] = sub;
        var plan = SaveOver.Plan(sc);
        Assert.Equal(new[] { ive }, plan.Unsupported);
        Assert.DoesNotContain(ive, plan.Files);
        var e = Assert.Throws<NotSupportedException>(() => SaveOver.Run(sc, plan));
        Assert.Contains("1 changed external file(s) in another format cannot be written in place: tile_c.ive", e.Message);
        Assert.Equal(e.Message, SaveOver.UnsupportedMessage(plan));
        Assert.False(Directory.Exists(plan.BackupFolder));                           // nothing touched
        Assert.Equal(originalA, File.ReadAllBytes(Path.Combine(root, "tile_a.flt")));
    }

    [Fact]
    public void SaveOverBacksUpThenOverwrites()
    {
        var (sc, root) = FltDb();
        var original = File.ReadAllBytes(Path.Combine(root, "tile_a.flt"));
        var plan = SaveOver.Plan(sc);
        Assert.Equal(new[] { Path.Combine(root, "tile_a.flt") }, plan.Files);   // master unchanged: not written
        Assert.StartsWith(Path.Combine(root, "_backup_"), plan.BackupFolder);
        Assert.False(Directory.Exists(plan.BackupFolder));
        var res = SaveOver.Run(sc, plan);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(res.BackupFolder, "tile_a.flt")));
        Assert.NotEqual(original, File.ReadAllBytes(Path.Combine(root, "tile_a.flt")));
        Assert.Equal(new[] { Path.Combine(root, "tile_a.flt") }, res.Written);
        long before = Tris(SceneIO.Load(Path.Combine(res.BackupFolder, "tile_a.flt"))) + Tris(SceneIO.Load(Path.Combine(root, "tile_b.flt")));
        Assert.True(Tris(SceneIO.Load(Path.Combine(root, "master.flt"))) < before);
    }

    [Fact]
    public void SaveOverWithoutChangesIsRefused()
    {
        var (sc, root) = FltDb(false, false);
        Assert.Throws<InvalidOperationException>(() => SaveOver.Run(sc, SaveOver.Plan(sc)));
        Assert.DoesNotContain(Directory.GetDirectories(root, "_backup_2*"), d => !d.EndsWith("_20260101_000000"));
    }

    [Fact]
    public void LockedTargetLeavesBackupAndOtherFilesIntact()
    {
        var (sc, root) = FltDb(true, true);
        var originalB = File.ReadAllBytes(Path.Combine(root, "tile_b.flt"));
        var plan = SaveOver.Plan(sc);
        using (var _ = File.Open(Path.Combine(root, "tile_b.flt"), FileMode.Open, FileAccess.Read, FileShare.Read))   // readable (backup copies it), not replaceable
        {
            var exc = Assert.Throws<IOException>(() => SaveOver.Run(sc, plan));
            Assert.Contains("_backup_", exc.Message);
        }
        var backup = Directory.GetDirectories(root, "_backup_*").Single(d => !d.EndsWith("_20260101_000000"));
        Assert.True(File.Exists(Path.Combine(backup, "tile_a.flt")) && File.Exists(Path.Combine(backup, "tile_b.flt")));
        Assert.Equal(originalB, File.ReadAllBytes(Path.Combine(root, "tile_b.flt")));
    }

    [Fact]
    public void BakedTextureIsReferencedFromTheCopysTexturesFolder()
    {
        // a baked atlas is written into the copy ("tile_a_textures/"): Dest must not send it to "_external/"
        var (sc, _) = FltDb(false, false);
        var tileA = sc.Externals.First(kv => Path.GetFileName(kv.Key) == "tile_a.flt").Value!;
        var geo = tileA.IterNodes().First(n => n.Kind == Kind.Geometry);
        geo.StateSet = new StateSet { Textures = { new Texture { File = "half.rgb", Baked = LodToolsTests.HalfImage(), Exists = true, Channels = 3, Width = 64, Height = 64 } } };
        geo.Geom!.TexCoords[0] = new VArray(Enumerable.Range(0, geo.Geom.NumVertices).SelectMany(i => new[] { geo.Geom.Vertices!.Data[3 * i] / 20, geo.Geom.Vertices.Data[3 * i + 1] / 20 }).ToArray(), 2, "Vec2Array");
        var (results, _, _) = Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object>
            { ["simplify.seams"] = "rebake", ["simplify.ratio"] = 0.1, ["simplify.rebake_texture"] = 64 }, targets: new List<Node> { geo });
        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Contains(Textures(sc), t => t.Baked != null);
        var res = SaveCopy.ToFolder(sc, Out);
        Assert.False(Directory.Exists(Path.Combine(Out, "_external")));
        var atlas = Assert.Single(Directory.GetFiles(Path.Combine(Out, "tile_a_textures")));
        Assert.EndsWith(".rgb", atlas);
        var back = SceneIO.Load(res.Master);
        var tex = Textures(back).ToList();
        Assert.NotEmpty(tex);
        Assert.All(tex, t => Assert.True(t.Exists, t.File));
        Assert.All(tex, t => Assert.StartsWith(Path.GetFullPath(Out), t.Path!));
        Assert.All(tex, t => Assert.StartsWith("tile_a_textures/", t.File.Replace('\\', '/')));
    }

    [Fact]
    public void UnmodifiedOutOfRootExternalIsNotPlannedButCopiedAsExternal()
    {
        Directory.CreateDirectory(Db);
        Directory.CreateDirectory(Path.Combine(dir, "shared"));
        File.WriteAllBytes(Path.Combine(Db, "master.flt"), FltWriteTests.Grid(0, "master", false, "tile_a.flt", "../shared/ext.flt"));
        File.WriteAllBytes(Path.Combine(Db, "tile_a.flt"), FltWriteTests.Grid(20, "tile_a"));
        File.WriteAllBytes(Path.Combine(dir, "shared", "ext.flt"), FltWriteTests.Grid(20, "ext"));
        var sc = SceneIO.Load(Path.Combine(Db, "master.flt"));
        var tileA = sc.Externals.First(kv => Path.GetFileName(kv.Key) == "tile_a.flt").Value!;
        Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object> { ["simplify.ratio"] = 0.25, ["simplify.min_tris"] = 10 },
            targets: tileA.IterNodes().ToList());
        Assert.Equal(new[] { Path.Combine(Db, "tile_a.flt") }, SaveOver.Plan(sc).Files);
        var res = SaveCopy.ToFolder(sc, Out);
        Assert.True(File.Exists(Path.Combine(Out, "_external", "ext.flt")));
        Assert.Contains("external  _external/ext.flt", File.ReadAllText(res.Manifest));
    }
}
