using System.Text;

namespace SceneKit;

/// <summary>Write the scene model back as OpenSceneGraph text (.osgt or legacy .osg). Nodes that came from a
/// file are written by patching their original document objects; everything the model does not manage is
/// copied unchanged. New nodes and scenes from other formats are generated in the osgt layout (version 161).</summary>
public abstract class OsgWriterBase
{
    public const int OsgtVersion = 161;
    public const string Generator = "#Generator OpenSceneGraph 3.6.5";

    protected static readonly HashSet<string> GeometryKeysOsgt = new()
    {
        "PrimitiveSetList", "VertexArray", "NormalArray", "ColorArray", "SecondaryColorArray", "FogCoordArray",
        "TexCoordArrayList", "VertexAttribArrayList", "VertexData", "NormalData", "ColorData", "SecondaryColorData",
        "FogCoordData", "TexCoordData", "VertexAttribData", "FastPathHint",
    };
    protected static readonly HashSet<string> GeometryKeysOsg = new()
    {
        "PrimitiveSets", "Primitives", "VertexArray", "NormalBinding", "NormalArray", "ColorBinding", "ColorArray",
        "SecondaryColorBinding", "SecondaryColorArray", "FogCoordBinding", "FogCoordArray", "TexCoordArray",
        "VertexAttribBinding", "VertexAttribArray", "VertexAttribNormalize", "VertexIndices", "NormalIndices",
        "ColorIndices", "SecondaryColorIndices", "FogCoordIndices", "TexCoordIndices", "VertexAttribIndices",
    };
    protected static readonly HashSet<string> NodeHeadKeys = new()
    {
        "Name", "DataVariance", "UserData", "UserDataContainer", "InitialBound", "ComputeBoundingSphereCallback",
        "UpdateCallback", "EventCallback", "CullCallback", "CullingActive", "NodeMask", "Descriptions", "StateSet",
    };
    protected static readonly Dictionary<string, string> BindingOsgt = new()
    {
        ["vertex"] = "BIND_PER_VERTEX", ["overall"] = "BIND_OVERALL", ["primitive_set"] = "BIND_PER_PRIMITIVE_SET",
        ["primitive"] = "BIND_PER_PRIMITIVE", ["off"] = "BIND_OFF", ["undefined"] = "BIND_PER_VERTEX",
    };
    protected static readonly Dictionary<string, string> BindingOsg = new()
    {
        ["vertex"] = "PER_VERTEX", ["overall"] = "OVERALL", ["primitive_set"] = "PER_PRIMITIVE_SET",
        ["primitive"] = "PER_PRIMITIVE", ["off"] = "OFF", ["undefined"] = "PER_VERTEX",
    };
    protected static readonly HashSet<string> DoubleTypes = new() { "Vec2dArray", "Vec3dArray", "Vec4dArray", "DoubleArray" };

    protected readonly Scene scene;
    protected readonly string outPath, outDir, srcDir;
    protected readonly bool relocate;
    protected TextOut w = null!;

    /// <summary>Maps the absolute path of an existing source file to where it will be in the output (Save Copy);
    /// null: references point back at the original files when the output folder differs.</summary>
    public Func<string, string>? Dest;

    protected OsgWriterBase(Scene scene, string outPath)
    {
        this.scene = scene;
        this.outPath = System.IO.Path.GetFullPath(outPath);
        outDir = System.IO.Path.GetDirectoryName(this.outPath)!;
        srcDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(scene.ConvertedFrom ?? (string.IsNullOrEmpty(scene.Path) ? outPath : scene.Path)))!;
        relocate = !SamePath(outDir, srcDir);
    }

    static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('/', '\\'), System.IO.Path.GetFullPath(b).TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string RelPath(string target, string fromDir)
    {
        try
        {
            var r = System.IO.Path.GetRelativePath(fromDir, target);
            return System.IO.Path.IsPathRooted(r) ? target : r.Replace('\\', '/');
        }
        catch (ArgumentException) { return target; }
    }

    /// <summary>A file reference that still works from the output folder.</summary>
    protected string RelFile(string name, string? resolved = null)
    {
        if (string.IsNullOrEmpty(name) || (!relocate && Dest == null)) return name;
        bool rooted = System.IO.Path.IsPathRooted(name.Replace('\\', '/'));
        if (rooted && resolved == null && Dest == null) return name;
        string target = resolved ?? System.IO.Path.GetFullPath(System.IO.Path.Combine(srcDir, name.Replace('\\', '/')));
        // a rooted name is only mapped when its file exists (a dangling absolute path stays as it is)
        if (Dest != null && (resolved != null || !rooted || System.IO.File.Exists(target))) target = Dest(target);
        else if (rooted && resolved == null) return name;
        return RelPath(target, outDir);
    }

    protected string FileRef(Node n, string name)
    {
        if (n.FilesOut != null && n.FilesOut.TryGetValue(name, out var o)) return o;
        return RelFile(name);
    }

    /// <summary>What <see cref="FixTextureNames"/> changed in the model (document tokens, texture names), undone in reverse
    /// order by <see cref="RestoreTextureNames"/> after the write: the names are only valid from that one output.</summary>
    readonly List<Action> nameUndo = new();

    /// <summary>Put the texture references <see cref="FixTextureNames"/> rewrote for this output back as they were.</summary>
    public void RestoreTextureNames()
    {
        for (int i = nameUndo.Count - 1; i >= 0; i--) nameUndo[i]();
        nameUndo.Clear();
    }

    void SetFile(Texture t, string name)
    {
        string old = t.File;
        nameUndo.Add(() => t.File = old);
        t.File = name;
    }

    /// <summary>Point texture references at files that work from the output folder; write downscaled copies.</summary>
    protected void FixTextureNames()
    {
        ExportBaked();
        var resized = ExportResized();
        if (!relocate && resized.Count == 0) return;
        foreach (var n in scene.IterNodes())
        {
            var ss = n.StateSet;
            if (ss == null) continue;
            foreach (var t in ss.Textures)
            {
                if (t.Embedded || t.File.Length == 0) continue;
                string nw = resized.TryGetValue(t, out var r) ? r : RelFile(t.File, t.Path);
                if (scene.Doc == null) { SetFile(t, nw); continue; }
                var obj = scene.Doc.Dialect == "osgt" ? t.ImageSrc : t.Src as Obj;
                if (t.Src == null) { if (resized.ContainsKey(t)) SetFile(t, nw); continue; }   // generated texture: written from t.File
                string key = scene.Doc.Dialect == "osgt" ? "FileName" : "file";
                var st = obj?.Get(key);
                if (st != null && st.Toks.Length > 1)
                {
                    string oldTok = st.Toks[1];
                    var oldQuoted = (bool[]?)st.Quoted?.Clone();
                    nameUndo.Add(() => { st.Toks[1] = oldTok; st.Quoted = oldQuoted; });
                    st.SetToken(1, nw, true);
                }
            }
        }
    }

    /// <summary>Texture objects read from the file get the model's wrap / filter modes (TX6). Values that already match
    /// stay untouched, so files nobody edited are written as they were.</summary>
    protected void PatchSampling()
    {
        if (scene.Doc == null) return;
        bool osgt = scene.Doc.Dialect == "osgt";
        var seen = new HashSet<Obj>(ReferenceEqualityComparer.Instance);
        foreach (var n in scene.IterNodes())
            if (n.StateSet != null)
                foreach (var t in n.StateSet.Textures)
                    if (t.Src is Obj o && seen.Add(o)) TextureSampling.Patch(o, osgt, t);
    }

    /// <summary>A Billboard made by the optimiser (replace_cards); its position is Node.Center.</summary>
    protected static bool IsBillboard(Node n) => n.Kind == Kind.Geode && n.Cls == "osg::Billboard";

    /// <summary>True for OpenFlight output: textures written next to it get a copy of their source's .attr file.</summary>
    protected virtual bool FltOutput => false;

    /// <summary>What saving the textures had to do differently (format changes, skipped resizes); copied into the scene's
    /// warnings when the file is written.</summary>
    public readonly List<string> TextureWarnings = new();

    protected void Warn(string msg) { if (!TextureWarnings.Contains(msg)) TextureWarnings.Add(msg); }

    /// <summary>Copy the texture warnings into the scene's warnings.</summary>
    public void Publish()
    {
        foreach (var m in TextureWarnings) if (!scene.Warnings.Contains(m)) scene.Warnings.Add(m);
    }

    /// <summary>OpenFlight output: '&lt;image&gt;.attr' next to a written image, copied from the source image's .attr with the new
    /// size and the texture's sampling. Nothing when the source has none.</summary>
    void WriteAttr(string? srcImage, string dstImage, int w, int h, Texture t)
    {
        if (!FltOutput || srcImage == null) return;
        string src = srcImage + ".attr";
        if (!File.Exists(src)) return;
        if (!FltAttr.WriteCopy(src, dstImage + ".attr", w, h, t)) Warn($"{System.IO.Path.GetFileName(src)} could not be copied for {System.IO.Path.GetFileName(dstImage)}");
    }

    /// <summary>Baked textures (cards, proxies, rebake) become image files in '&lt;scene&gt;_textures' next to the output, in
    /// the format of the textures they were baked from (see TextureFormats).</summary>
    protected void ExportBaked()
    {
        var todo = new List<Texture>();
        var seen = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
        foreach (var n in scene.IterNodes())
            if (n.StateSet != null)
                foreach (var t in n.StateSet.Textures)
                    if (t.Baked != null && seen.Add(t)) todo.Add(t);
        if (todo.Count == 0) return;
        TextureFormats.Forget(scene);
        string folder = System.IO.Path.Combine(outDir, System.IO.Path.GetFileNameWithoutExtension(outPath) + "_textures");
        Directory.CreateDirectory(folder);
        var used = new HashSet<string>();
        foreach (var t in todo)
        {
            var f = TextureFormats.Choose(t, scene, FltOutput);
            string stem = System.IO.Path.GetFileNameWithoutExtension(t.File.Length > 0 ? t.File : "proxy");
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
            string Name(string ext)
            {
                string name = stem + ext;
                for (int k = 1; !used.Add(name.ToLowerInvariant()); k++) name = $"{stem}_{k}{ext}";
                return name;
            }
            string dst = System.IO.Path.Combine(folder, Name(f.Ext));
            if (f.Note != null) Warn(f.Note);
            if (!TextureFormats.Write(t.Baked!, f, dst, t))
            {
                Warn($"{stem}: the {f.Ext} encoder failed, written as .png");
                f = f with { Ext = ".png", Bpc = 1 };
                dst = System.IO.Path.Combine(folder, Name(".png"));
                File.WriteAllBytes(dst, ImageCodecs.WritePng(ImageCodecs.WithChannels(t.Baked!, f.Channels)));
            }
            WriteAttr(f.AttrSource?.Path, dst, t.Baked!.Width, t.Baked.Height, t);
            t.Fmt = f.Ext.TrimStart('.');
            t.Channels = f.Channels;
            t.Path = dst;
            t.File = RelPath(dst, outDir);
            t.Exists = true;
            ExportBakedCompanions(t, dst, used);
        }
    }

    /// <summary>The baked companion atlases of a baked texture just written to <paramref name="dst"/>: each next to it,
    /// named by its pattern applied to the written file (so the image generator finds it by the same rule), in the
    /// format of the companion images it was baked from (the pattern's extension wins), with a copy of their .attr.</summary>
    void ExportBakedCompanions(Texture t, string dst, HashSet<string> used)
    {
        if (t.Companions == null) return;
        foreach (var (pat, c) in t.Companions)
        {
            if (c.Baked == null) continue;
            string cdst = System.IO.Path.GetFullPath(Companions.Apply(pat, dst));
            string label = $"{Companions.Label(pat)} companion {System.IO.Path.GetFileName(cdst)}";
            used.Add(System.IO.Path.GetFileName(cdst).ToLowerInvariant());
            var f = TextureFormats.ForExt(TextureFormats.Choose(c, scene, FltOutput), System.IO.Path.GetExtension(cdst).ToLowerInvariant(), label);
            if (f == null) { Warn($"{label} not written: {System.IO.Path.GetExtension(cdst)} cannot be encoded here"); continue; }
            if (f.Note != null) Warn(f.Note);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cdst)!);
            if (!TextureFormats.Write(c.Baked, f, cdst, c)) { Warn($"{label}: the {f.Ext} encoder failed, not written"); continue; }
            WriteAttr(f.AttrSource?.Path, cdst, c.Baked.Width, c.Baked.Height, c);
            c.Fmt = f.Ext.TrimStart('.');
            c.Channels = f.Channels;
            c.Path = cdst;
            c.File = RelPath(cdst, outDir);
        }
    }

    /// <summary>Downscaled copies of textures marked by 'downscale_textures' (needs an image codec; see ImageResizer), in
    /// the same file type. Types that cannot be scaled here (e.g. .dds) keep the original image, with a warning.</summary>
    protected Dictionary<Texture, string> ExportResized()
    {
        var o = new Dictionary<Texture, string>(ReferenceEqualityComparer.Instance);
        var todo = new List<Texture>();
        var seen = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
        foreach (var n in scene.IterNodes())
            if (n.StateSet != null)
                foreach (var t in n.StateSet.Textures)
                    if (t.Path != null && !t.Embedded && seen.Add(t))
                    {
                        if (t.Resize != null) todo.Add(t);
                        else if (t.Repointed) o[t] = RelPath(Dest != null ? Dest(t.Path) : t.Path, outDir);   // file replaced in the inspector
                    }
        if (todo.Count == 0) return o;
        if (ImageResizer.Current == null)
        {
            Warn($"{todo.Count} texture(s) not downscaled: image scaling is not available here (the originals are referenced)");
            return o;
        }
        string bn = System.IO.Path.GetFileNameWithoutExtension(outPath);
        string folder = System.IO.Path.Combine(outDir, bn + "_textures");
        var used = new HashSet<string>();
        foreach (var t in todo)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(t.Path!), ext = System.IO.Path.GetExtension(t.Path!);
            string name = stem + ext;
            int k = 1;
            while (used.Contains(name.ToLowerInvariant())) { name = $"{stem}_{k}{ext}"; k++; }
            used.Add(name.ToLowerInvariant());
            Directory.CreateDirectory(folder);
            string dst = System.IO.Path.Combine(folder, name);
            var (w, h) = t.Resize!.Value;
            bool resized;
            if (t.KeepCoverage && !t.IsClassMap)
            {
                resized = ImageResizer.Current.ResizeKeepingCoverage(t.Path!, dst, w, h);
                if (!resized)
                {
                    Warn($"{System.IO.Path.GetFileName(t.Path!)}: leaf area not kept (cannot read {ext} here), resized plainly");
                    resized = ImageResizer.Current.Resize(t.Path!, dst, w, h);
                }
            }
            else resized = ImageResizer.Current.Resize(t.Path!, dst, w, h, nearest: t.IsClassMap);
            if (!resized)
            {
                Warn($"{System.IO.Path.GetFileName(t.Path!)} not downscaled: {(ext.Length > 0 ? ext : "this file type")} cannot be read or written here (the original is referenced, the format is kept)");
                continue;
            }
            WriteAttr(t.Path, dst, w, h, t);
            o[t] = RelPath(dst, outDir);
            // companion images (infrared ...) at the same size, named by their pattern applied to the written file
            foreach (var (pat, c) in t.Companions ?? new Dictionary<string, Texture>())
            {
                if (c.Path == null || c.Baked != null) continue;
                string cdst = System.IO.Path.GetFullPath(Companions.Apply(pat, dst));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cdst)!);
                if (c.IsClassMap && !t.ResizeClassMaps)
                {
                    // class maps are not resampled unless asked: the file and its .attr are copied as they are
                    CopyClassMap(c.Path, cdst, t.Path!);
                    continue;
                }
                if (!ImageResizer.Current.Resize(c.Path, cdst, w, h, nearest: c.IsClassMap))
                {
                    if (c.IsClassMap)
                    {
                        // a class map the resizer cannot read (16-bit PNG ...) is kept at its size rather than dropped
                        Warn($"{System.IO.Path.GetFileName(c.Path)} (class map of {System.IO.Path.GetFileName(t.Path!)}) not resized: {System.IO.Path.GetExtension(c.Path)} cannot be read here (16-bit PNG?) - copied as it is");
                        CopyClassMap(c.Path, cdst, t.Path!);
                        continue;
                    }
                    Warn($"{System.IO.Path.GetFileName(c.Path)} (companion of {System.IO.Path.GetFileName(t.Path!)}) not resized: {System.IO.Path.GetExtension(c.Path)} cannot be read or written here - the resized texture has no {Companions.Label(pat)} companion");
                    continue;
                }
                WriteAttr(c.Path, cdst, w, h, c);
            }
        }
        return o;
    }

    /// <summary>Byte copy of a class map (and its .attr) next to its resized texture.</summary>
    void CopyClassMap(string src, string dst, string owner)
    {
        try
        {
            File.Copy(src, dst, true);
            if (File.Exists(src + ".attr")) File.Copy(src + ".attr", dst + ".attr", true);
        }
        catch (IOException)
        {
            Warn($"{System.IO.Path.GetFileName(src)} (class map of {System.IO.Path.GetFileName(owner)}) could not be copied");
        }
    }

    public abstract void Write(TextWriter sink);

    /// <summary>A baked .dds written with its own mip chain (<see cref="Texture.Mips"/> not the renderer's): the texture
    /// says useHardwareMipMapGeneration FALSE so the levels are used as written.</summary>
    protected static bool OwnMips(Texture t) => t.Fmt == "dds" && t.Mips != MipRule.Renderer && t.Baked != null;

    protected static string Num(double v) => NumFmt.Num(v);
}

/// <summary>Optional image scaling for 'downscale_textures' and encoding of baked textures in formats the core cannot
/// write (set by the app: the core writes SGI, TGA, PNG and uncompressed DDS itself, see ImageCodecs).</summary>
public abstract class ImageResizer
{
    public static ImageResizer? Current;
    /// <summary><paramref name="nearest"/>: nearest-neighbour, channels copied verbatim (class maps), never filtered.</summary>
    public abstract bool Resize(string src, string dst, int width, int height, bool nearest = false);

    /// <summary>True when <see cref="Encode"/> can write this file type (".jpg", ...).</summary>
    public virtual bool CanEncode(string ext) => ImageCodecs.CanWrite(ext);

    /// <summary>Write the image as the file type of <paramref name="path"/>'s extension; false when it cannot.</summary>
    public virtual bool Encode(RawImage img, string path, int sgiBpc = 1) => ImageCodecs.Write(img, path, sgiBpc);

    /// <summary>Resize a cut-out keeping its leaf area (downscale_textures keep_coverage): an image without alpha gets the
    /// plain <see cref="Resize"/>; with alpha it is resized, its alpha scaled so the passing share stays the source's
    /// (<see cref="AlphaMips.KeepCoverage"/>) and written in the same file type. False when it cannot be read or written
    /// here.</summary>
    public virtual bool ResizeKeepingCoverage(string src, string dst, int width, int height)
    {
        try
        {
            var img = ImageCodecs.Read(src);
            if (img == null || width <= 0 || height <= 0) return false;
            if (img.Channels is not (2 or 4)) return Resize(src, dst, width, height);
            var kept = AlphaMips.KeepCoverage(ImageCodecs.Resize(img, width, height), AlphaMips.Coverage(img));
            return ImageCodecs.Write(kept, dst, ImageCodecs.SgiBpc(src));
        }
        catch (Exception exc) when (exc is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }
}

public sealed class OsgtWriter : OsgWriterBase
{
    readonly bool dom;
    readonly bool regenAllGeometry;
    readonly Dictionary<object, Obj> ssObjs = new(ReferenceEqualityComparer.Instance);

    public OsgtWriter(Scene scene, string outPath) : base(scene, outPath)
    {
        dom = scene.Doc != null && scene.Doc.Dialect == "osgt";
        regenAllGeometry = dom && (scene.Doc!.Version) < 147;
    }

    int NewId() => w.NextId++;

    string Header()
    {
        var lines = new List<string> { "#Ascii Scene ", $"#Version {OsgtVersion} ", Generator + " " };
        if (dom) lines.AddRange(scene.Doc!.Header.Where(h => h.StartsWith("#CustomDomain", StringComparison.Ordinal)).Select(h => h + " "));
        return string.Join("\n", lines) + "\n\n";
    }

    public override void Write(TextWriter sink)
    {
        FixTextureNames();
        PatchSampling();
        sink.Write(Header());
        w = new TextOut("osgt", sink);
        var root = scene.Root!;
        if (root.VirtualRoot) foreach (var c in root.Children) WriteNode(c, 0);
        else WriteNode(root, 0);
    }

    void WriteNode(Node n, int depth)
    {
        Obj? src = dom ? n.Src as Obj : null;
        Obj obj;
        if (src != null) obj = src;
        else
        {
            obj = n.GenObj!;
            if (obj == null)
            {
                string cls = ClassForKind(n.Kind);
                if (n.Kind == Kind.Transform && n.Matrix == null) cls = "osg::Group";
                if (IsBillboard(n)) cls = "osg::Billboard";
                obj = new Obj(cls, null, new List<Stmt>(), ObjStyle.Osgt);
                n.GenObj = obj;   // keeps sharing of generated nodes
            }
        }
        if (src != null) w.WriteObj(obj.Cls, obj, depth, d => PatchedBody(n, obj, d));
        else w.WriteObj(obj.Cls, obj, depth, d => GeneratedBody(n, obj, d));
    }

    static string ClassForKind(string kind) => kind switch
    {
        Kind.Group => "osg::Group", Kind.Geode => "osg::Geode", Kind.Transform => "osg::MatrixTransform",
        Kind.Lod => "osg::LOD", Kind.Switch => "osg::Switch", Kind.Proxy => "osg::ProxyNode", Kind.Geometry => "osg::Geometry",
        Kind.PagedLod => "osg::PagedLOD", _ => "osg::Group",
    };

    void PatchedBody(Node n, Obj obj, int depth)
    {
        bool regenGeo = n.Kind == Kind.Geometry && n.Geom != null && (n.Dirty.Contains("geometry") || regenAllGeometry);
        bool childrenDone = false, ssDone = false;
        string childKey = obj.Get("Drawables") != null ? "Drawables" : "Children";
        string cls = OsgReader.Short(obj.Cls);
        var stmts = obj.Stmts;
        bool hasChildrenStmt = stmts.Any(s => s.Key is "Children" or "Drawables");
        bool hasSsStmt = stmts.Any(s => s.Key == "StateSet");
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            string k = s.Key;
            if (!ssDone && !hasSsStmt && n.StateSet != null && !(NodeHeadKeys.Contains(k) && k != "StateSet"))
            {
                StateSetStmt(n.StateSet, depth);
                ssDone = true;
            }
            if (!hasChildrenStmt && !childrenDone && n.Children.Count > 0 && n.Kind != Kind.Geometry && ChildrenInsertHere(cls, stmts, i))
            {
                ChildrenStmt(n, childKey, depth);
                childrenDone = true;
            }
            if (k is "Children" or "Drawables")
            {
                if (!childrenDone) { ChildrenStmt(n, childKey, depth); childrenDone = true; }
                continue;
            }
            if (k == "StateSet")
            {
                if (n.StateSet != null) StateSetStmt(n.StateSet, depth);
                ssDone = true;
                continue;
            }
            if (k == "Name" && n.Dirty.Contains("name"))
            {
                if (n.Name.Length > 0) w.Line(depth, $"Name {TextOut.Quote(n.Name)} ");
                continue;
            }
            if (k == "NodeMask" && n.Dirty.Contains("node_mask")) { w.Line(depth, $"NodeMask 0x{n.NodeMask:x} "); continue; }
            if (k == "Matrix" && n.Matrix != null && n.Dirty.Contains("matrix")) { MatrixStmt(n.Matrix, depth); continue; }
            if (k == "RangeList" && n.Ranges != null && n.Dirty.Contains("ranges")) { RangesStmt(n.Ranges, depth); continue; }
            if (k == "ValueList" && n.SwitchValues != null && n.Dirty.Contains("children")) { ValuesStmt(n.SwitchValues, depth); continue; }
            if (regenGeo && GeometryKeysOsgt.Contains(k)) continue;
            if ((relocate || n.FilesOut != null) && k is "FileNames" or "RangeDataList" && s.Block is List<Stmt> fl)
            {
                w.Line(depth, TextOut.Head(s) + " {");
                foreach (var sub in fl)
                    w.Line(depth + 1, string.Join(" ", sub.Toks.Select(t => t.Length > 0 ? TextOut.Quote(FileRef(n, t)) : "\"\"")) + " ");
                w.Line(depth, "}");
                continue;
            }
            w.WriteStmt(s, depth);
        }
        if (!ssDone && n.StateSet != null) StateSetStmt(n.StateSet, depth);
        if (!childrenDone && n.Children.Count > 0 && n.Kind != Kind.Geometry) ChildrenStmt(n, childKey, depth);
        if (regenGeo) GeometryStmts(n.Geom!, depth);
    }

    /// <summary>Where a missing Children list goes: after the node header keys; PagedLOD keeps Children last,
    /// ProxyNode right after FileNames.</summary>
    static bool ChildrenInsertHere(string cls, List<Stmt> stmts, int i)
    {
        string k = stmts[i].Key;
        if (cls == "PagedLOD") return false;
        if (cls == "ProxyNode") return i > 0 && stmts[i - 1].Key == "FileNames";
        return !NodeHeadKeys.Contains(k);
    }

    void GeneratedBody(Node n, Obj obj, int depth)
    {
        if (n.Name.Length > 0) w.Line(depth, $"Name {TextOut.Quote(n.Name)} ");
        if (n.NodeMask != 0xFFFFFFFFL) w.Line(depth, $"NodeMask 0x{n.NodeMask:x} ");
        if (n.StateSet != null) StateSetStmt(n.StateSet, depth);
        string cls = OsgReader.Short(obj.Cls);
        if (n.Kind == Kind.Geometry)
        {
            if (n.Geom != null) GeometryStmts(n.Geom, depth);
            return;
        }
        var kids = n.Children;
        if (cls is "Geode" or "Billboard")
        {
            if (kids.Count > 0)
            {
                w.Line(depth, $"Drawables {kids.Count} {{");
                foreach (var c in kids) WriteNode(c, depth + 1);
                w.Line(depth, "}");
            }
            if (cls == "Billboard")
            {
                // one position per drawable; the drawables are relative to it and turn around Axis to face the eye
                var pos = n.Center ?? new double[3];
                w.Line(depth, "Mode AXIAL_ROT ");
                w.Line(depth, "Axis 0 0 1 ");
                w.Line(depth, "Normal 0 -1 0 ");
                w.Line(depth, $"PositionList {kids.Count} {{");
                foreach (var _ in kids) w.Line(depth + 1, $"{Num(pos[0])} {Num(pos[1])} {Num(pos[2])} ");
                w.Line(depth, "}");
            }
            return;
        }
        if (cls == "ProxyNode")
        {
            var files = n.Files.Select(f => FileRef(n, f)).ToList();
            w.Line(depth, $"FileNames {files.Count} {{");
            foreach (var f in files) w.Line(depth + 1, TextOut.Quote(f) + " ");
            w.Line(depth, "}");
            if (kids.Count > 0) ChildrenStmt(n, "Children", depth);
            return;
        }
        if (kids.Count > 0) ChildrenStmt(n, "Children", depth);
        if (cls == "MatrixTransform") MatrixStmt(n.Matrix ?? Mat.Identity(), depth);
        else if (cls == "LOD")
        {
            if (n.Center != null && n.Center.Length >= 4)
            {
                w.Line(depth, "CenterMode USER_DEFINED_CENTER ");
                w.Line(depth, "UserCenter " + string.Join(" ", n.Center.Take(4).Select(Num)) + " ");
            }
            w.Line(depth, $"RangeMode {n.RangeMode} ");
            RangesStmt(n.Ranges is { Count: > 0 } ? n.Ranges : Enumerable.Repeat((0.0, 1e30), kids.Count).ToList(), depth);
        }
        else if (cls == "Switch") ValuesStmt(n.SwitchValues ?? Enumerable.Repeat(true, kids.Count).ToList(), depth);
    }

    void ChildrenStmt(Node n, string key, int depth)
    {
        var kids = n.Children;
        if (kids.Count == 0) return;
        w.Line(depth, $"{key} {kids.Count} {{");
        foreach (var c in kids) WriteNode(c, depth + 1);
        w.Line(depth, "}");
    }

    void MatrixStmt(double[] m, int depth)
    {
        w.Line(depth, "Matrix {");
        w.WriteNumbers(depth + 1, Mat.ToOsg(m), 4, true, " ");
        w.Line(depth, "}");
    }

    void RangesStmt(List<(double min, double max)> ranges, int depth)
    {
        w.Line(depth, $"RangeList {ranges.Count} {{");
        foreach (var (a, b) in ranges) w.Line(depth + 1, $"{Num(a)} {Num(b)} ");
        w.Line(depth, "}");
    }

    void ValuesStmt(List<bool> vals, int depth)
    {
        w.Line(depth, $"ValueList {vals.Count} {{");
        w.Line(depth + 1, string.Join(" ", vals.Select(v => v ? "TRUE" : "FALSE")) + " ");
        w.Line(depth, "}");
    }

    // ---------------------------------------------------------------- state
    void StateSetStmt(StateSet ss, int depth)
    {
        w.Line(depth, "StateSet TRUE {");
        if (dom && ss.Src is Obj so) w.WriteObj(so.Cls, so, depth + 1);
        else
        {
            if (!ssObjs.TryGetValue(ss, out var obj)) { obj = new Obj("osg::StateSet", null, new List<Stmt>(), ObjStyle.Osgt); ssObjs[ss] = obj; }
            w.WriteObj("osg::StateSet", obj, depth + 1, d => GeneratedStateSet(ss, d));
        }
        w.Line(depth, "}");
    }

    void GeneratedStateSet(StateSet ss, int depth)
    {
        void L(int d, string t) => w.Line(d, t);
        L(depth, "DataVariance STATIC ");
        bool alphaTest = ss.Modes.TryGetValue("GL_ALPHA_TEST", out var at) && at.StartsWith("ON");
        // the osgt reader expects the StateSet fields in the wrapper's order: ModeList, AttributeList, TextureModeList, ...
        var modes = ss.Modes.Where(kv => kv.Key.StartsWith("GL_") && (kv.Value.StartsWith("ON") || kv.Value.StartsWith("OFF")) && !(ss.Transparent && kv.Key == "GL_BLEND")).ToList();
        if (ss.Transparent) modes.Add(new("GL_BLEND", "ON"));
        if (modes.Count > 0)
        {
            L(depth, $"ModeList {modes.Count} {{");
            foreach (var (k, v) in modes) L(depth + 1, $"{k} {v} ");
            L(depth, "}");
        }
        L(depth, $"AttributeList {(alphaTest ? 2 : 1)} {{");
        L(depth + 1, "osg::Material {");
        L(depth + 2, $"UniqueID {NewId()} ");
        if (ss.MaterialName.Length > 0) L(depth + 2, $"Name {TextOut.Quote(ss.MaterialName)} ");
        L(depth + 2, "ColorMode AMBIENT_AND_DIFFUSE ");
        var d4 = ss.Diffuse ?? new[] { 0.8, 0.8, 0.8, 1.0 };
        L(depth + 2, "Ambient TRUE Front 0.2 0.2 0.2 1 Back 0.2 0.2 0.2 1 ");
        string dd = string.Join(" ", d4.Concat(new[] { 1.0 }).Take(4).Select(Num));
        L(depth + 2, $"Diffuse TRUE Front {dd} Back {dd} ");
        L(depth + 2, "Specular TRUE Front 0 0 0 1 Back 0 0 0 1 ");
        L(depth + 2, "Emission TRUE Front 0 0 0 1 Back 0 0 0 1 ");
        L(depth + 2, "Shininess TRUE Front 0 Back 0 ");
        L(depth + 1, "}");
        L(depth + 1, "Value OFF ");
        if (alphaTest)
        {
            // cut-outs (cards, leaves): texels below half alpha are not drawn, so no sorting is needed
            L(depth + 1, "osg::AlphaFunc {");
            L(depth + 2, $"UniqueID {NewId()} ");
            L(depth + 2, "Function GREATER ");
            L(depth + 2, "ReferenceValue 0.5 ");
            L(depth + 1, "}");
            L(depth + 1, "Value OFF ");
        }
        L(depth, "}");
        var units = new SortedDictionary<int, Texture>();
        foreach (var t in ss.Textures) units.TryAdd(t.Unit, t);
        if (units.Count > 0)
        {
            int nUnits = units.Keys.Max() + 1;
            L(depth, $"TextureModeList {nUnits} {{");
            for (int u = 0; u < nUnits; u++)
            {
                if (units.ContainsKey(u)) { L(depth + 1, "Data 1 {"); L(depth + 2, "GL_TEXTURE_2D ON "); L(depth + 1, "}"); }
                else { L(depth + 1, "Data 0 {"); L(depth + 1, "}"); }
            }
            L(depth, "}");
            L(depth, $"TextureAttributeList {nUnits} {{");
            for (int u = 0; u < nUnits; u++)
            {
                if (!units.TryGetValue(u, out var t)) { L(depth + 1, "Data 0 {"); L(depth + 1, "}"); continue; }
                L(depth + 1, "Data 1 {");
                GeneratedTexture(t, depth + 2);
                L(depth + 2, "Value OFF ");
                L(depth + 1, "}");
            }
            L(depth, "}");
        }
        if (ss.Transparent)
        {
            L(depth, "RenderingHint 2 ");
            L(depth, "RenderBinMode USE_RENDERBIN_DETAILS ");
            L(depth, "BinNumber 10 ");
            L(depth, "BinName \"DepthSortedBin\" ");
        }
    }

    readonly Dictionary<Texture, (Obj tex, Obj img)> genTex = new(ReferenceEqualityComparer.Instance);

    void GeneratedTexture(Texture t, int depth)
    {
        if (!genTex.TryGetValue(t, out var objs))
        {
            objs = (new Obj("osg::Texture2D", null, new List<Stmt>(), ObjStyle.Osgt), new Obj("osg::Image", null, new List<Stmt>(), ObjStyle.Image));
            genTex[t] = objs;
        }
        w.WriteObj("osg::Texture2D", objs.tex, depth, d =>
        {
            w.Line(d, $"WRAP_S {t.WrapS ?? "REPEAT"} ");
            w.Line(d, $"WRAP_T {t.WrapT ?? "REPEAT"} ");
            w.Line(d, "WRAP_R REPEAT ");
            w.Line(d, $"MIN_FILTER {t.MinFilter ?? "LINEAR_MIPMAP_LINEAR"} ");
            w.Line(d, $"MAG_FILTER {t.MagFilter ?? "LINEAR"} ");
            if (t.MaxAnisotropy is double an) w.Line(d, $"MaxAnisotropy {Num(an)} ");
            // a .dds with its own mip chain (R3-C8): the GPU must not regenerate (box-filter) the levels; TRUE is the default
            if (OwnMips(t)) w.Line(d, "UseHardwareMipMapGeneration FALSE ");
            w.WriteObj("Image TRUE", objs.img, d, d2 =>
            {
                w.Line(d2, $"FileName {TextOut.Quote(scene.Doc == null ? RelFile(t.File, t.Path) : t.File)} ");
                w.Line(d2, "WriteHint 0 2 ");
                w.Line(d2, "DataVariance STATIC ");
            });
        });
    }

    // ---------------------------------------------------------------- geometry
    void GeometryStmts(GeomData g, int depth)
    {
        if (g.Prims.Count > 0)
        {
            w.Line(depth, $"PrimitiveSetList {g.Prims.Count} {{");
            foreach (var p in g.Prims) PrimitiveObj(p, depth + 1);
            w.Line(depth, "}");
        }
        if (g.Vertices != null) ArrayStmt("VertexArray", g.Vertices, depth);
        if (g.Normals != null) ArrayStmt("NormalArray", g.Normals, depth);
        if (g.Colors != null) ArrayStmt("ColorArray", g.Colors, depth);
        foreach (var name in new[] { "SecondaryColorArray", "FogCoordArray" })
            if (g.Extra.TryGetValue(name, out var a)) ArrayStmt(name, a, depth);
        if (g.TexCoords.Count > 0)
        {
            int nUnits = g.TexCoords.Keys.Max() + 1;
            w.Line(depth, $"TexCoordArrayList {nUnits} {{");
            for (int u = 0; u < nUnits; u++)
            {
                if (g.TexCoords.TryGetValue(u, out var a)) ArrayObj(a, depth + 1);
                else w.Line(depth + 1, "NULL");
            }
            w.Line(depth, "}");
        }
        var attribs = new SortedDictionary<int, VArray>();
        foreach (var kv in g.Extra)
            if (kv.Key.StartsWith("VertexAttrib", StringComparison.Ordinal) && int.TryParse(kv.Key["VertexAttrib".Length..], out var idx))
                attribs[idx] = kv.Value;
        if (attribs.Count > 0)
        {
            int nAttr = attribs.Keys.Max() + 1;
            w.Line(depth, $"VertexAttribArrayList {nAttr} {{");
            for (int i = 0; i < nAttr; i++)
            {
                if (attribs.TryGetValue(i, out var a)) ArrayObj(a, depth + 1);
                else w.Line(depth + 1, "NULL");
            }
            w.Line(depth, "}");
        }
    }

    void PrimitiveObj(Primitive p, int depth)
    {
        string cls;
        if (p.Kind == "arrays") cls = "DrawArrays";
        else if (p.Kind == "lengths") cls = "DrawArrayLengths";
        else
        {
            cls = p.IndexType ?? "DrawElementsUInt";
            int mx = p.Indices is { Length: > 0 } ? p.Indices.Max() : 0;
            if (cls == "DrawElementsUByte" && p.Indices!.Length > 0 && mx > 255) cls = "DrawElementsUShort";
            if (cls == "DrawElementsUShort" && p.Indices!.Length > 0 && mx > 65535) cls = "DrawElementsUInt";
        }
        w.Line(depth, $"osg::{cls} {{");
        w.Line(depth + 1, $"UniqueID {NewId()} ");
        if (p.Instances != 0) w.Line(depth + 1, $"NumInstances {p.Instances} ");
        w.Line(depth + 1, $"Mode {p.Mode} ");
        if (p.Kind == "arrays")
        {
            if (p.First != 0) w.Line(depth + 1, $"First {p.First} ");
            w.Line(depth + 1, $"Count {p.Count} ");
        }
        else if (p.Kind == "lengths")
        {
            if (p.First != 0) w.Line(depth + 1, $"First {p.First} ");
            IntVector(p.Lengths ?? Array.Empty<int>(), 4, depth + 1);
        }
        else IntVector(p.Indices ?? Array.Empty<int>(), 4, depth + 1);
        w.Line(depth, "}");
    }

    void ArrayStmt(string key, VArray a, int depth)
    {
        w.Line(depth, $"{key} TRUE {{");
        ArrayObj(a, depth + 1);
        w.Line(depth, "}");
    }

    void ArrayObj(VArray a, int depth)
    {
        w.Line(depth, $"osg::{a.Type} {{");
        w.Line(depth + 1, $"UniqueID {NewId()} ");
        w.Line(depth + 1, $"Binding {BindingOsgt.GetValueOrDefault(a.Binding, "BIND_PER_VERTEX")} ");
        if (a.Normalize) w.Line(depth + 1, "Normalize TRUE ");
        int comps = ArrayTypes.Comps(a.Type);
        w.Line(depth + 1, $"vector {a.Count} {{");
        w.WriteNumbers(depth + 2, a.Data, comps > 1 ? comps : 4, DoubleTypes.Contains(a.Type), " ");
        w.Line(depth + 1, "}");
        w.Line(depth, "}");
    }

    void IntVector(int[] data, int perLine, int depth)
    {
        w.Line(depth, $"vector {data.Length} {{");
        w.WriteNumbers(depth + 1, data, perLine, " ");
        w.Line(depth, "}");
    }
}

public sealed class OsgLegacyWriter : OsgWriterBase
{
    readonly Dictionary<Node, Obj> generated = new(ReferenceEqualityComparer.Instance);

    public OsgLegacyWriter(Scene scene, string outPath) : base(scene, outPath) { }

    public override void Write(TextWriter sink)
    {
        FixTextureNames();
        PatchSampling();
        // dry run: count how often every object is written, so UniqueID / Use only appear for shared objects
        w = new TextOut("osg", TextWriter.Null);
        Emit();
        var calls = w.Calls;
        w = new TextOut("osg", sink) { UseCount = calls };
        Emit();
    }

    void Emit()
    {
        var root = scene.Root!;
        if (root.VirtualRoot) foreach (var c in root.Children) WriteNode(c, 0);
        else WriteNode(root, 0);
    }

    Obj ObjOf(Node n)
    {
        if (n.Src is Obj o) return o;
        if (!generated.TryGetValue(n, out var g))
        {
            string cls = n.Kind switch
            {
                Kind.Group => "Group", Kind.Geode => "Geode", Kind.Transform => "MatrixTransform", Kind.Lod => "LOD",
                Kind.Switch => "Switch", Kind.Geometry => "Geometry", Kind.Proxy => "ProxyNode", _ => "Group",
            };
            if (n.Kind == Kind.Transform && n.Matrix == null) cls = "Group";
            if (IsBillboard(n)) cls = "Billboard";
            g = new Obj(cls, null, new List<Stmt>(), ObjStyle.Osg);
            generated[n] = g;
        }
        return g;
    }

    void WriteNode(Node n, int depth)
    {
        var obj = ObjOf(n);
        if (n.Src is Obj) w.WriteObj(obj.Cls, obj, depth, d => PatchedBody(n, obj, d));
        else w.WriteObj(obj.Cls, obj, depth, d => GeneratedBody(n, obj, d));
    }

    void PatchedBody(Node n, Obj obj, int depth)
    {
        bool regenGeo = n.Kind == Kind.Geometry && n.Geom != null && n.Dirty.Contains("geometry");
        var childObjs = new HashSet<Obj>(n.Children.Select(ObjOf), ReferenceEqualityComparer.Instance);
        var oldChildren = new HashSet<Obj>(ReferenceEqualityComparer.Instance);
        foreach (var s in obj.Stmts)
            if (s.Block is Obj b && b.Cls != "StateSet" && !b.Cls.EndsWith("Callback", StringComparison.Ordinal)
                && b.Cls is not ("UserData" or "Descriptions" or "ValueList" or "Matrix"))
                oldChildren.Add(b);
        string countKey = n.Kind == Kind.Geode ? "num_drawables" : "num_children";
        bool childrenDone = false, ssDone = false;
        foreach (var s in obj.Stmts)
        {
            string k = s.Key;
            var b = s.Block;
            if (b is Obj bo && bo.Cls == "StateSet")
            {
                if (n.StateSet != null && !ssDone) StateSetW(n.StateSet, depth);
                ssDone = true;
                continue;
            }
            if (k is "num_children" or "num_drawables")
            {
                if (!childrenDone) { Children(n, countKey, depth); childrenDone = true; }
                continue;
            }
            if (b is Obj bc && (oldChildren.Contains(bc) || childObjs.Contains(bc)))
            {
                if (!childrenDone) { Children(n, countKey, depth); childrenDone = true; }
                continue;
            }
            if (k == "name" && n.Dirty.Contains("name"))
            {
                if (n.Name.Length > 0) w.Line(depth, $"name {TextOut.Quote(n.Name)}");
                continue;
            }
            if (k == "nodeMask" && n.Dirty.Contains("node_mask")) { w.Line(depth, $"nodeMask 0x{n.NodeMask:x}"); continue; }
            if (k == "Matrix" && n.Matrix != null && n.Dirty.Contains("matrix")) { MatrixW(n.Matrix, depth); continue; }
            if (k == "RangeList" && n.Ranges != null && n.Dirty.Contains("ranges")) { RangesW(n.Ranges, depth); continue; }
            if (regenGeo && GeometryKeysOsg.Contains(k)) continue;
            if (k == "FileNameList" && (relocate || n.FilesOut != null) && b is List<Stmt> fl)
            {
                w.Line(depth, TextOut.Head(s) + " {");
                foreach (var sub in fl)
                    w.Line(depth + 1, string.Join(" ", sub.Toks.Select(t => t.Length > 0 ? TextOut.Quote(FileRef(n, t)) : "\"\"")));
                w.Line(depth, "}");
                continue;
            }
            w.WriteStmt(s, depth);
        }
        if (!ssDone && n.StateSet != null) StateSetW(n.StateSet, depth);
        if (!childrenDone && n.Children.Count > 0 && n.Kind != Kind.Geometry) Children(n, countKey, depth);
        if (regenGeo) GeometryW(n.Geom!, depth);
    }

    void GeneratedBody(Node n, Obj obj, int depth)
    {
        if (n.Name.Length > 0) w.Line(depth, $"name {TextOut.Quote(n.Name)}");
        if (n.NodeMask != 0xFFFFFFFFL) w.Line(depth, $"nodeMask 0x{n.NodeMask:x}");
        if (n.StateSet != null) StateSetW(n.StateSet, depth);
        if (n.Kind == Kind.Geometry)
        {
            if (n.Geom != null) GeometryW(n.Geom, depth);
            return;
        }
        if (obj.Cls == "MatrixTransform")
        {
            w.Line(depth, "referenceFrame RELATIVE");
            MatrixW(n.Matrix ?? Mat.Identity(), depth);
        }
        else if (obj.Cls == "LOD")
        {
            w.Line(depth, "CenterMode USE_BOUNDING_SPHERE_CENTER");
            w.Line(depth, $"RangeMode {n.RangeMode}");
            RangesW(n.Ranges is { Count: > 0 } ? n.Ranges : Enumerable.Repeat((0.0, 1e30), n.Children.Count).ToList(), depth);
        }
        else if (obj.Cls == "Switch")
        {
            var vals = n.SwitchValues ?? Enumerable.Repeat(true, n.Children.Count).ToList();
            w.Line(depth, "NewChildDefaultValue 1");
            w.Line(depth, "ValueList {");
            foreach (var v in vals) w.Line(depth + 1, v ? "1" : "0");
            w.Line(depth, "}");
        }
        else if (obj.Cls == "Billboard")
        {
            var pos = n.Center ?? new double[3];
            w.Line(depth, "Mode AXIAL_ROT");
            w.Line(depth, "Axis 0 0 1");
            w.Line(depth, "Normal 0 -1 0");
            w.Line(depth, "Positions {");
            foreach (var _ in n.Children) w.Line(depth + 1, $"{Num(pos[0])} {Num(pos[1])} {Num(pos[2])}");
            w.Line(depth, "}");
        }
        Children(n, obj.Cls is "Geode" or "Billboard" ? "num_drawables" : "num_children", depth);
    }

    void Children(Node n, string key, int depth)
    {
        w.Line(depth, $"{key} {n.Children.Count}");
        foreach (var c in n.Children) WriteNode(c, depth);
    }

    void StateSetW(StateSet ss, int depth)
    {
        if (ss.Src is Obj o) { w.WriteObj(o.Cls, o, depth); return; }
        // made by the optimiser (proxy LODs): material + texture unit 0
        void L(int d, string t) => w.Line(d, t);
        L(depth, "StateSet {");
        L(depth + 1, "DataVariance STATIC");
        L(depth + 1, "rendering_hint DEFAULT_BIN");
        L(depth + 1, "renderBinMode INHERIT");
        L(depth + 1, "GL_LIGHTING ON");
        foreach (var (k, v) in ss.Modes)
            if (k.StartsWith("GL_") && k != "GL_LIGHTING" && (v.StartsWith("ON") || v.StartsWith("OFF"))) L(depth + 1, $"{k} {v}");
        if (ss.Transparent && !ss.Modes.ContainsKey("GL_BLEND")) L(depth + 1, "GL_BLEND ON");
        if (ss.Modes.TryGetValue("GL_ALPHA_TEST", out var at) && at.StartsWith("ON"))
        {
            L(depth + 1, "AlphaFunc {");
            L(depth + 2, "comparisonFunc GREATER");
            L(depth + 2, "referenceValue 0.5");
            L(depth + 1, "}");
        }
        L(depth + 1, "Material {");
        L(depth + 2, "ColorMode AMBIENT_AND_DIFFUSE");
        L(depth + 2, "ambientColor 0.2 0.2 0.2 1");
        var d4 = (ss.Diffuse ?? new[] { 0.8, 0.8, 0.8, 1.0 }).Concat(new[] { 1.0 }).Take(4).Select(Num);
        L(depth + 2, $"diffuseColor {string.Join(" ", d4)}");
        L(depth + 2, "specularColor 0 0 0 1");
        L(depth + 2, "emissionColor 0 0 0 1");
        L(depth + 2, "shininess 0");
        L(depth + 1, "}");
        foreach (var t in ss.Textures.GroupBy(x => x.Unit).Select(g => g.First()).OrderBy(x => x.Unit))
        {
            L(depth + 1, $"textureUnit {t.Unit} {{");
            L(depth + 2, "GL_TEXTURE_2D ON");
            L(depth + 2, "Texture2D {");
            L(depth + 3, "DataVariance STATIC");
            L(depth + 3, $"file {TextOut.Quote(t.File)}");
            L(depth + 3, $"wrap_s {t.WrapS ?? "REPEAT"}");
            L(depth + 3, $"wrap_t {t.WrapT ?? "REPEAT"}");
            L(depth + 3, "wrap_r REPEAT");
            L(depth + 3, $"min_filter {t.MinFilter ?? "LINEAR_MIPMAP_LINEAR"}");
            L(depth + 3, $"mag_filter {t.MagFilter ?? "LINEAR"}");
            L(depth + 3, $"maxAnisotropy {Num(t.MaxAnisotropy ?? 1)}");
            // a .dds with its own mip chain (R3-C8): the GPU must not regenerate (box-filter) the levels
            L(depth + 3, $"useHardwareMipMapGeneration {(OwnMips(t) ? "FALSE" : "TRUE")}");
            L(depth + 3, "unRefImageDataAfterApply FALSE");
            L(depth + 3, "internalFormatMode USE_IMAGE_DATA_FORMAT");
            L(depth + 3, "resizeNonPowerOfTwo TRUE");
            L(depth + 2, "}");
            L(depth + 1, "}");
        }
        L(depth, "}");
    }

    void MatrixW(double[] m, int depth)
    {
        w.Line(depth, "Matrix {");
        w.WriteNumbers(depth + 1, Mat.ToOsg(m), 4, true, "");
        w.Line(depth, "}");
    }

    void RangesW(List<(double min, double max)> ranges, int depth)
    {
        w.Line(depth, $"RangeList {ranges.Count} {{");
        foreach (var (a, b) in ranges) w.Line(depth + 1, $"{Num(a)} {Num(b)}");
        w.Line(depth, "}");
    }

    void GeometryW(GeomData g, int depth)
    {
        w.Line(depth, $"PrimitiveSets {g.Prims.Count}");
        w.Line(depth, "{");
        foreach (var p in g.Prims)
        {
            if (p.Kind == "arrays") w.Line(depth + 1, $"DrawArrays {p.Mode} {p.First} {p.Count}");
            else if (p.Kind == "lengths")
            {
                var l = p.Lengths ?? Array.Empty<int>();
                w.Line(depth + 1, $"DrawArrayLengths {p.Mode} {p.First} {l.Length}");
                Block(l, 10, depth + 1);
            }
            else
            {
                var idx = p.Indices ?? Array.Empty<int>();
                string cls = p.IndexType ?? "DrawElementsUInt";
                int mx = idx.Length > 0 ? idx.Max() : 0;
                if (cls == "DrawElementsUByte" && mx > 255) cls = "DrawElementsUShort";
                if (cls == "DrawElementsUShort" && mx > 65535) cls = "DrawElementsUInt";
                w.Line(depth + 1, $"{cls} {p.Mode} {idx.Length}");
                Block(idx, 10, depth + 1);
            }
        }
        w.Line(depth, "}");
        if (g.Vertices != null) ArrayW("VertexArray", g.Vertices, depth);
        if (g.Normals != null)
        {
            w.Line(depth, $"NormalBinding {BindingOsg.GetValueOrDefault(g.Normals.Binding, "PER_VERTEX")}");
            ArrayW("NormalArray", g.Normals, depth);
        }
        if (g.Colors != null)
        {
            w.Line(depth, $"ColorBinding {BindingOsg.GetValueOrDefault(g.Colors.Binding, "PER_VERTEX")}");
            ArrayW("ColorArray", g.Colors, depth);
        }
        foreach (var name in new[] { "SecondaryColorArray", "FogCoordArray" })
        {
            if (!g.Extra.TryGetValue(name, out var a)) continue;
            w.Line(depth, $"{name.Replace("Array", "Binding")} {BindingOsg.GetValueOrDefault(a.Binding, "PER_VERTEX")}");
            ArrayW(name, a, depth);
        }
        foreach (var kv in g.TexCoords) ArrayW($"TexCoordArray {kv.Key}", kv.Value, depth);
    }

    void ArrayW(string key, VArray a, int depth)
    {
        int comps = ArrayTypes.Comps(a.Type);
        w.Line(depth, $"{key} {a.Type} {a.Count}");
        w.Line(depth, "{");
        w.WriteNumbers(depth + 1, a.Data, comps > 1 ? comps : 10, DoubleTypes.Contains(a.Type), "");
        w.Line(depth, "}");
    }

    void Block(int[] data, int perLine, int depth)
    {
        w.Line(depth, "{");
        w.WriteNumbers(depth + 1, data, perLine, "");
        w.Line(depth, "}");
    }
}

public static class OsgWrite
{
    /// <summary>Write the scene to <paramref name="path"/> (.osgt, .osg, or .flt for scenes read from OpenFlight).</summary>
    public static string Write(Scene scene, string path, Func<string, string>? dest = null)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".flt") return new FltWriter(scene, path) { Dest = dest }.Save();
        OsgWriterBase writer;
        if (ext == ".osg")
        {
            if (scene.Doc == null || scene.Doc.Dialect != "osg")
                throw new InvalidOperationException("legacy .osg output is only written for scenes read from .osg; save as .osgt (or let osgconv convert it)");
            writer = new OsgLegacyWriter(scene, path);
        }
        else
        {
            if (scene.Doc != null && scene.Doc.Dialect == "osg")
                throw new InvalidOperationException("this scene was read from a legacy .osg file; save it as .osg (or let osgconv convert it)");
            writer = new OsgtWriter(scene, path);
        }
        writer.Dest = dest;
        try { return WriteText(writer, path); }
        finally
        {
            writer.RestoreTextureNames();
            writer.Publish();
        }
    }

    static string WriteText(OsgWriterBase writer, string path)
    {
        string tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false), 1 << 20))
            {
                sw.NewLine = "\n";
                writer.Write(sw);
            }
            File.Move(tmp, path, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
        return path;
    }

    /// <summary>The format a scene is saved in by default: its own (.flt scenes are patched in place, see FltWriter).</summary>
    public static string NativeExt(Scene scene) => scene.Flt != null ? ".flt" : scene.Doc != null && scene.Doc.Dialect == "osg" ? ".osg" : ".osgt";
}
