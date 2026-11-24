using System.Text.Json;
using SceneKit;
using Xunit;

/// <summary>CheckpointStore: changed-files checkpoints on disk, per database key and session.</summary>
public class CheckpointTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"skck_{Guid.NewGuid():N}");
    readonly string baseDir, root, master;
    readonly Scene sc;

    public CheckpointTests()
    {
        Directory.CreateDirectory(dir);
        baseDir = Path.Combine(dir, "checkpoints");
        (sc, root) = SaveCopyTests.BuildFltDb(Path.Combine(dir, "db"));
        master = Path.Combine(root, "master.flt");
    }
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    [Fact]
    public void WriteThenListRoundTrips()
    {
        var store = new CheckpointStore(baseDir); string key = CheckpointStore.KeyFor(master), s = store.NewSession();
        var ck = store.Write(sc, key, s, 1, "simplify, weld", "tile_a", "simplify.ratio=0.25", null);
        Assert.True(File.Exists(Path.Combine(ck.Folder, "tile_a.flt")) && File.Exists(ck.Master) && File.Exists(Path.Combine(ck.Folder, "checkpoint.json")));
        Assert.False(File.Exists(Path.Combine(ck.Folder, "tile_b.flt")));
        var back = store.List(key).Single();
        Assert.Equal((1, "simplify, weld", "tile_a", "simplify.ratio=0.25", master), (back.Index, back.Label, back.Scope, back.Params, back.Source));
        Assert.Equal(new[] { ck.Folder, Path.GetDirectoryName(master)! }, back.Layers);
        Assert.True(back.Tris > 0 && back.Draws >= 0);
        Assert.False(back.IsBackup);
        Assert.Null(back.Parent);
    }

    [Fact]
    public void KeyChangesWhenTheMasterChanges()
    {
        string k1 = CheckpointStore.KeyFor(master); File.AppendAllText(master, "x"); Assert.NotEqual(k1, CheckpointStore.KeyFor(master));
        Assert.Equal(16, k1.Length);
    }

    [Fact]
    public void NewSessionAvoidsCollisionsAcrossKeys()
    {
        var store = new CheckpointStore(baseDir);
        string s1 = store.NewSession();
        Directory.CreateDirectory(Path.Combine(baseDir, "somekey", s1));
        string s2 = store.NewSession();
        Assert.NotEqual(s1, s2);
        Assert.StartsWith(s1, s2);
    }

    [Fact]
    public void RekeyMovesSessionsAndAddsTheBackupAsBase()
    {
        var store = new CheckpointStore(baseDir); string k1 = CheckpointStore.KeyFor(master), k2 = "0123456789abcdef", s = store.NewSession();
        string backup = Path.Combine(root, "_backup_20260102_000000");
        store.Write(sc, k1, s, 1, "a", "tile_a", "", null);
        store.Rekey(k1, k2, backup);
        var ck = store.List(k2).Single();
        Assert.Equal(new[] { ck.Folder, backup, root }, ck.Layers);
        Assert.False(Directory.Exists(Path.Combine(baseDir, k1)));
        Assert.True(File.Exists(Path.Combine(ck.Folder, "tile_a.flt")));
    }

    [Fact]
    public void BackupEntryLoadsFromTheBackupFolder()
    {
        var store = new CheckpointStore(baseDir); string key = CheckpointStore.KeyFor(master), s = store.NewSession();
        string backup = Path.Combine(root, "_backup_20260102_000000");
        Directory.CreateDirectory(backup);
        File.Copy(Path.Combine(root, "tile_a.flt"), Path.Combine(backup, "tile_a.flt"));
        store.Write(sc, key, s, 1, "a", "tile_a", "", null);
        var b = store.AddBackupEntry(key, s, backup, "master.flt", "before this session's save over");
        Assert.True(b.IsBackup);
        Assert.Equal(0, b.Index);
        Assert.Equal(master, b.Master);   // not in the backup folder: the root's master
        Assert.Equal(new[] { backup, root }, b.Layers);
        var all = store.List(key);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.IsBackup && c.Layers.SequenceEqual(new[] { backup, root }));
    }

    [Fact]
    public void PruneRemovesOldAndOversized()
    {
        var store = new CheckpointStore(baseDir); string key = CheckpointStore.KeyFor(master), s = store.NewSession();
        var c1 = store.Write(sc, key, s, 1, "a", "tile_a", "", null);
        store.Write(sc, key, s, 2, "b", "tile_a", "", 1);
        var c3 = store.Write(sc, key, s, 3, "c", "tile_a", "", 2);
        string json = Path.Combine(c1.Folder, "checkpoint.json");
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(json))!;
        var edited = doc.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        edited["Time"] = DateTime.Now.AddDays(-20).ToString("o");
        File.WriteAllText(json, JsonSerializer.Serialize(edited));

        Assert.Equal(1, store.Prune(TimeSpan.FromDays(14), long.MaxValue, null));
        Assert.Equal(2, store.List(key).Count);
        Assert.Equal(0, store.Prune(TimeSpan.MaxValue, 1, keepSession: s));
        Assert.Equal(2, store.List(key).Count);
        Assert.Equal(1, store.Prune(TimeSpan.MaxValue, 1, null));
        Assert.Equal(c3.Folder, store.List(key).Single().Folder);
        Assert.Equal(1, store.Size(key).count);
    }

    static long Tris(Scene sc) => Convert.ToInt64(Analyzer.Analyze(sc, deepChecks: false).Summary()["triangles"]);
    static Scene Sub(Scene sc, string name) => sc.Externals.Single(kv => Path.GetFileName(kv.Key) == name).Value!;

    /// <summary>A checkpoint of <c>sc</c> (tile_a simplified) and that state loaded layered over the database.</summary>
    (CheckpointInfo ck, Scene back) Restored()
    {
        var store = new CheckpointStore(baseDir); string key = CheckpointStore.KeyFor(master), s = store.NewSession();
        var ck = store.Write(sc, key, s, 1, "simplify", "tile_a", "", null);
        return (ck, SceneIO.Load(ck.Master, new LoadOptions { Layers = ck.Layers.ToList() }));
    }

    [Fact]
    public void CheckpointLoadsLayeredOverTheDatabase()
    {
        var (ck, back) = Restored();
        Assert.DoesNotContain(back.Warnings, w => w.Contains("not found"));
        Assert.Equal(Tris(sc), Tris(back));                                   // tile_a from the checkpoint, tile_b from the database
        Assert.Equal(root, DatabaseRoot.Of(back));
        Assert.Equal(ck.Layers, back.Layers);
        Assert.Equal(Path.Combine(root, "tile_a.flt"), DatabaseRoot.BasePath(back, back.Externals.Keys.Single(p => p.Contains("tile_a"))));
        Assert.StartsWith(ck.Folder + Path.DirectorySeparatorChar, back.Externals.Keys.Single(p => p.Contains("tile_a")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(root, "tile_b.flt"), back.Externals.Keys.Single(p => p.Contains("tile_b")), StringComparer.OrdinalIgnoreCase);

        // a checkpoint written after a restore is still keyed to the database's master
        var store = new CheckpointStore(baseDir);
        var ck2 = store.Write(back, CheckpointStore.KeyFor(master), ck.Session, 2, "again", "tile_a", "", 1);
        Assert.Equal(master, ck2.Source);
        Assert.Equal(new[] { ck2.Folder, root }, ck2.Layers);
    }

    [Fact]
    public void SaveOverFromACheckpointOverwritesTheOriginals()
    {
        var (ck, back) = Restored();
        var plan = SaveOver.Plan(back);
        Assert.Contains(Path.Combine(root, "tile_a.flt"), plan.Files);       // from the layer -> counts as changed
        Assert.DoesNotContain(Path.Combine(root, "tile_b.flt"), plan.Files);
        var res = SaveOver.Run(back, plan);
        Assert.Equal(File.ReadAllBytes(Path.Combine(ck.Folder, "tile_a.flt")), File.ReadAllBytes(Path.Combine(root, "tile_a.flt")));
        Assert.True(File.Exists(Path.Combine(res.BackupFolder, "tile_a.flt")));
        Assert.Equal(Tris(sc), Tris(SceneIO.Load(master)));
    }

    [Fact]
    public void SaveOverAfterARestoreWritesReferencesRelativeToTheDatabase()
    {
        var (ck, back) = Restored();
        SaveOver.Run(back, SaveOver.Plan(back));
        string text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(master));   // the raw reference strings
        Assert.Contains("tile_a.flt", text);
        Assert.Contains("tile_b.flt", text);
        Assert.DoesNotContain("checkpoints", text);
        Assert.DoesNotContain("01_simplify", text);
        Assert.DoesNotContain("../", text);
        Assert.DoesNotContain(":\\", text);
        Assert.DoesNotContain(":/", text);
        var reloaded = SceneIO.Load(master);
        Assert.DoesNotContain(reloaded.Warnings, w => w.Contains("not found"));
        Assert.Equal(Tris(sc), Tris(reloaded));
    }

    /// <summary>db2/master.osgt with textures/t.png (8x8) whose texture is downscaled to 4x4; the master's path.</summary>
    string ResizedOsgtDb()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        string db2 = Path.Combine(dir, "db2"), png = Path.Combine(db2, "textures", "t.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        File.WriteAllBytes(png, ImageCodecs.WritePng(new RawImage(8, 8, 3)));
        var geo = new Node(Kind.Geometry, "osg::Geometry", "g") { Geom = TextureTrackingTests.Quad(0) };
        var tex = Images.FillTexture(new Texture { File = "textures/t.png", Unit = 0 }, db2);
        string m = Path.Combine(db2, "master.osgt");
        SceneIO.Save(new Scene(m, "osgt") { Root = TextureTrackingTests.Holder(geo, tex) }, m);
        return m;
    }

    static Texture Tex(Scene s) => s.IterNodes(externals: true).Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Single();

    [Fact]
    public void SaveOverAfterARestoreCopiesTheCheckpointsImagesIntoTheDatabase()
    {
        string m = ResizedOsgtDb(), db2 = Path.GetDirectoryName(m)!;
        var s0 = SceneIO.Load(m);
        Tex(s0).Resize = (4, 4);
        var store = new CheckpointStore(baseDir); string key = CheckpointStore.KeyFor(m);
        var ck = store.Write(s0, key, store.NewSession(), 1, "resize", "all", "", null);
        Assert.True(File.Exists(Path.Combine(ck.Folder, "master_textures", "t.png")));
        var back = SceneIO.Load(ck.Master, new LoadOptions { Layers = ck.Layers.ToList() });
        var plan = SaveOver.Plan(back);
        Assert.Contains((Path.Combine(ck.Folder, "master_textures", "t.png"), Path.Combine(db2, "master_textures", "t.png")), plan.Copies);
        SaveOver.Run(back, plan);
        string text = File.ReadAllText(m);
        Assert.Contains("\"master_textures/t.png\"", text);
        Assert.DoesNotContain("checkpoints", text);
        Assert.True(File.Exists(Path.Combine(db2, "master_textures", "t.png")));
        var t = Tex(SceneIO.Load(m));
        Assert.True(t.Exists);
        Assert.Equal(Path.Combine(db2, "master_textures", "t.png"), Path.GetFullPath(t.Path!), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(4, Images.Probe(t.Path!)!.Width);
    }

    [Fact]
    public void CheckpointAfterARestoreKeepsTheRestoredImages()
    {
        string m = ResizedOsgtDb();
        var s0 = SceneIO.Load(m);
        Tex(s0).Resize = (4, 4);
        var store = new CheckpointStore(baseDir); string key = CheckpointStore.KeyFor(m), s = store.NewSession();
        var ck1 = store.Write(s0, key, s, 1, "resize", "all", "", null);
        var back = SceneIO.Load(ck1.Master, new LoadOptions { Layers = ck1.Layers.ToList() });
        Assert.StartsWith(ck1.Folder + Path.DirectorySeparatorChar, Tex(back).Path!, StringComparison.OrdinalIgnoreCase);
        var ck2 = store.Write(back, key, s, 2, "again", "all", "", 1);
        Assert.True(File.Exists(Path.Combine(ck2.Folder, "master_textures", "t.png")));
        Assert.Contains("copied    master_textures/t.png", File.ReadAllText(Path.Combine(ck2.Folder, SaveCopy.ManifestName)));
        var back2 = SceneIO.Load(ck2.Master, new LoadOptions { Layers = ck2.Layers.ToList() });
        var t = Tex(back2);
        Assert.True(t.Exists);
        Assert.StartsWith(ck2.Folder + Path.DirectorySeparatorChar, t.Path!, StringComparison.OrdinalIgnoreCase);   // not db2/textures
        Assert.Equal(4, Images.Probe(t.Path!)!.Width);
    }

    [Fact]
    public void BackupEntryRestoresTheStateBeforeTheSaveOver()
    {
        long before = Tris(SceneIO.Load(master));
        var res = SaveOver.Run(sc, SaveOver.Plan(sc));   // writes only tile_a: the master is unchanged
        long after = Tris(SceneIO.Load(master));
        Assert.True(after < before);
        Assert.True(File.Exists(Path.Combine(res.BackupFolder, "master.flt")));      // backed up all the same
        var store = new CheckpointStore(baseDir); string key = CheckpointStore.KeyFor(master);
        var b = store.AddBackupEntry(key, store.NewSession(), res.BackupFolder, "master.flt", "before this session's save over", master);
        Assert.Equal(Path.Combine(res.BackupFolder, "master.flt"), b.Master);
        var back = SceneIO.Load(b.Master, new LoadOptions { Layers = b.Layers.ToList() });
        Assert.DoesNotContain(back.Warnings, w => w.Contains("not found"));
        Assert.Equal(before, Tris(back));
    }

    [Fact]
    public void RestoredStateKnowsTheDatabasesMaster()
    {
        var (ck, back) = Restored();
        Assert.Equal(master, back.Origin);
        Assert.Equal(master, DatabaseRoot.Master(back));
        string ive = Path.Combine(root, "master.ive");   // a database read through osgconv: the checkpoint holds master.osgt / .flt
        var asIve = SceneIO.Load(ck.Master, new LoadOptions { Layers = ck.Layers.ToList(), Origin = ive });
        Assert.Equal(ive, asIve.Origin);
        Assert.Equal(ive, DatabaseRoot.Master(asIve));
        Assert.Equal(ive, SaveOver.Plan(asIve).Files[0]);
        var store = new CheckpointStore(baseDir);
        var ck2 = store.Write(asIve, "k", store.NewSession(), 2, "again", "tile_a", "", 1);
        Assert.Equal(ive, ck2.Source);
        Assert.Null(SceneIO.Load(master).Origin);
        Assert.Equal(master, DatabaseRoot.Master(SceneIO.Load(master)));
    }

    [Fact]
    public void ReusedIndexLeavesNoStaleFiles()
    {
        var store = new CheckpointStore(baseDir); string key = CheckpointStore.KeyFor(master), s = store.NewSession();
        var c1 = store.Write(sc, key, s, 1, "a", "tile_a", "", null);
        File.WriteAllBytes(Path.Combine(c1.Folder, "tile_b.flt"), FltWriteTests.Grid(4, "tile_b"));   // left from an earlier state
        var c2 = store.Write(sc, key, s, 1, "a", "tile_a", "", null);
        Assert.Equal(c1.Folder, c2.Folder);
        Assert.False(File.Exists(Path.Combine(c2.Folder, "tile_b.flt")));
        Assert.True(File.Exists(Path.Combine(c2.Folder, "tile_a.flt")));
    }

    [Fact]
    public void SaveCopyFromACheckpointIsComplete()
    {
        var (ck, back) = Restored();
        string outDir = Path.Combine(dir, "out");
        var res = SaveCopy.ToFolder(back, outDir);
        Assert.Equal(File.ReadAllBytes(Path.Combine(ck.Folder, "tile_a.flt")), File.ReadAllBytes(Path.Combine(outDir, "tile_a.flt")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "tile_b.flt")), File.ReadAllBytes(Path.Combine(outDir, "tile_b.flt")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "_external")));
        var copy = SceneIO.Load(res.Master);
        Assert.Empty(copy.Warnings);
        Assert.Equal(Tris(sc), Tris(copy));
    }

    [Fact]
    public void SecondLayerWinsOverTheRootButNotOverTheFirst()
    {
        string ckpt = Path.Combine(dir, "ckpt"), backup = Path.Combine(dir, "backup");
        Directory.CreateDirectory(ckpt);
        Directory.CreateDirectory(backup);
        File.Copy(master, Path.Combine(ckpt, "master.flt"));
        File.WriteAllBytes(Path.Combine(backup, "tile_b.flt"), FltWriteTests.Grid(12, "tile_b"));
        long backupTris = Tris(SceneIO.Load(Path.Combine(backup, "tile_b.flt")));
        long dbTris = Tris(SceneIO.Load(Path.Combine(root, "tile_b.flt")));
        Assert.NotEqual(backupTris, dbTris);

        var three = SceneIO.Load(Path.Combine(ckpt, "master.flt"), new LoadOptions { Layers = new() { ckpt, backup, root } });
        Assert.DoesNotContain(three.Warnings, w => w.Contains("not found"));
        Assert.Equal(backupTris, Tris(Sub(three, "tile_b.flt")));

        var two = SceneIO.Load(Path.Combine(ckpt, "master.flt"), new LoadOptions { Layers = new() { ckpt, root } });
        Assert.DoesNotContain(two.Warnings, w => w.Contains("not found"));
        Assert.Equal(dbTris, Tris(Sub(two, "tile_b.flt")));

        File.WriteAllBytes(Path.Combine(ckpt, "tile_b.flt"), FltWriteTests.Grid(4, "tile_b"));   // the first layer wins over all
        var first = SceneIO.Load(Path.Combine(ckpt, "master.flt"), new LoadOptions { Layers = new() { ckpt, backup, root } });
        Assert.Equal(Tris(SceneIO.Load(Path.Combine(ckpt, "tile_b.flt"))), Tris(Sub(first, "tile_b.flt")));
    }

    [Fact]
    public void LayeredLoadResolvesTexturesThroughTheLayers()
    {
        string db2 = Path.Combine(dir, "db2"), ckpt = Path.Combine(dir, "ckpt2");
        string png = Path.Combine(db2, "textures", "t.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        File.WriteAllBytes(png, ImageCodecs.WritePng(new RawImage(2, 2, 3)));
        var geo = new Node(Kind.Geometry, "osg::Geometry", "g") { Geom = TextureTrackingTests.Quad(0) };
        var tex = Images.FillTexture(new Texture { File = "textures/t.png", Unit = 0 }, db2);
        string m = Path.Combine(db2, "master.osgt");
        SceneIO.Save(new Scene(m, "osgt") { Root = TextureTrackingTests.Holder(geo, tex) }, m);
        Directory.CreateDirectory(ckpt);
        File.Copy(m, Path.Combine(ckpt, "master.osgt"));

        var plain = SceneIO.Load(Path.Combine(ckpt, "master.osgt"));
        Assert.Contains(plain.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures), t => !t.Exists);
        var back = SceneIO.Load(Path.Combine(ckpt, "master.osgt"), new LoadOptions { Layers = new() { ckpt, db2 } });
        var t = back.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Single();
        Assert.True(t.Exists);
        Assert.Equal(Path.GetFullPath(png), Path.GetFullPath(t.Path!), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteFailureThrowsWithoutTouchingTheScene()
    {
        string asFile = Path.Combine(dir, "iamafile");
        File.WriteAllText(asFile, "x");
        var store = new CheckpointStore(asFile);
        var e = Record.Exception(() => store.Write(sc, "k", "s", 1, "a", "tile_a", "", null));
        Assert.True(e is IOException or UnauthorizedAccessException, e?.ToString());
        var res = SaveCopy.ToFolder(sc, Path.Combine(dir, "other"));
        Assert.True(File.Exists(res.Master));
    }
}
