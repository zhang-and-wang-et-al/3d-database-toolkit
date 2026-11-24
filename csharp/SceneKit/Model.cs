namespace SceneKit;

/// <summary>Node kinds (same names as the Python version; they appear in reports and JSON).</summary>
public static class Kind
{
    public const string Group = "Group", Transform = "Transform", Lod = "LOD", PagedLod = "PagedLOD",
        Switch = "Switch", Proxy = "Proxy", Geode = "Geode", Geometry = "Geometry", Other = "Other",
        External = "External";
}

public static class Modes
{
    public static readonly HashSet<string> Triangle = new() { "TRIANGLES", "TRIANGLE_STRIP", "TRIANGLE_FAN", "QUADS", "QUAD_STRIP", "POLYGON" };
    public static readonly HashSet<string> Line = new() { "LINES", "LINE_STRIP", "LINE_LOOP" };
}

public static class ArrayTypes
{
    /// <summary>name -> (components, bytes per component, is double).</summary>
    public static readonly Dictionary<string, (int comps, int size)> Info = new()
    {
        ["Vec2Array"] = (2, 4), ["Vec3Array"] = (3, 4), ["Vec4Array"] = (4, 4),
        ["Vec2dArray"] = (2, 8), ["Vec3dArray"] = (3, 8), ["Vec4dArray"] = (4, 8),
        ["FloatArray"] = (1, 4), ["DoubleArray"] = (1, 8),
        ["Vec4ubArray"] = (4, 1), ["Vec3ubArray"] = (3, 1), ["Vec2ubArray"] = (2, 1),
        ["Vec4bArray"] = (4, 1), ["Vec3bArray"] = (3, 1), ["Vec2bArray"] = (2, 1),
        ["Vec4usArray"] = (4, 2), ["Vec3usArray"] = (3, 2), ["Vec2usArray"] = (2, 2),
        ["Vec4sArray"] = (4, 2), ["Vec3sArray"] = (3, 2), ["Vec2sArray"] = (2, 2),
        ["Vec4uiArray"] = (4, 4), ["Vec3uiArray"] = (3, 4), ["Vec2uiArray"] = (2, 4),
        ["Vec4iArray"] = (4, 4), ["Vec3iArray"] = (3, 4), ["Vec2iArray"] = (2, 4),
        ["UByteArray"] = (1, 1), ["ByteArray"] = (1, 1), ["UShortArray"] = (1, 2),
        ["ShortArray"] = (1, 2), ["UIntArray"] = (1, 4), ["IntArray"] = (1, 4),
    };

    public static int Comps(string type) => Info.TryGetValue(type, out var i) ? i.comps : 1;

    public static readonly Dictionary<string, int> IndexTypes = new()
    { ["DrawElementsUByte"] = 1, ["DrawElementsUShort"] = 2, ["DrawElementsUInt"] = 4 };
}

/// <summary>A vertex attribute array: <see cref="Data"/> holds Count x Comps values (row major).
/// Binding is "vertex", "overall", "primitive_set", "primitive", "off" or "undefined".</summary>
public sealed class VArray
{
    public double[] Data;
    public int Comps;
    public string Type;
    public string Binding;
    public bool Normalize;
    public object? Src;

    public VArray(double[] data, int comps, string type, string binding = "vertex", bool normalize = false, object? src = null)
    {
        Data = data; Comps = Math.Max(comps, 1); Type = type; Binding = binding; Normalize = normalize; Src = src;
    }

    public int Count => Data.Length / Comps;

    public long NBytes
    {
        get
        {
            if (ArrayTypes.Info.TryGetValue(Type, out var i)) return (long)Count * i.comps * i.size;
            return (long)Count * Comps * 4;
        }
    }

    public VArray CopyWith(double[] data, int? comps = null) => new(data, comps ?? Comps, Type, Binding, Normalize);

    public double Get(int row, int col) => Data[row * Comps + col];

    /// <summary>Rows reshaped to a given component count (Python reshape semantics: truncate to whole rows).</summary>
    public static VArray FromFlat(double[] flat, string type, string binding = "vertex", bool normalize = false, object? src = null)
    {
        int comps = ArrayTypes.Comps(type);
        int k = flat.Length / comps * comps;
        var data = k == flat.Length ? flat : flat[..k];
        return new VArray(data, comps, type, binding, normalize, src);
    }
}

/// <summary>One primitive set. Kind is "arrays", "lengths" or "elements".</summary>
public sealed class Primitive
{
    public string Mode;
    public string Kind;
    public int First;
    public int Count;
    public int[]? Lengths;
    public int[]? Indices;
    public string? IndexType;
    public int Instances;

    public Primitive(string mode, string kind, int first = 0, int count = 0, int[]? lengths = null, int[]? indices = null,
        string? indexType = null, int instances = 0)
    {
        Mode = mode; Kind = kind; First = first; Count = count; Lengths = lengths; Indices = indices;
        IndexType = indexType; Instances = instances;
    }

    public Primitive Clone() => (Primitive)MemberwiseClone();

    public int[] VertexIndices()
    {
        if (Kind == "elements") return Indices ?? Array.Empty<int>();
        long total = Kind == "lengths" ? (Lengths ?? Array.Empty<int>()).Sum(x => (long)x) : Count;
        var r = new int[Math.Max(total, 0)];
        for (int i = 0; i < r.Length; i++) r[i] = First + i;
        return r;
    }

    public int DrawCalls => Kind == "lengths" ? (Lengths?.Length ?? 0) : 1;

    public long IndexBytes
    {
        get
        {
            if (Kind == "elements") return (long)(Indices?.Length ?? 0) * (ArrayTypes.IndexTypes.TryGetValue(IndexType ?? "", out var b) ? b : 4);
            if (Kind == "lengths") return (long)(Lengths?.Length ?? 0) * 4;
            return 0;
        }
    }
}

public static class Topology
{
    /// <summary>Triangles of one index run as a flat list (3 per triangle).</summary>
    public static void ModeTriangles(string mode, ReadOnlySpan<int> idx, List<int> outTris)
    {
        int n = idx.Length;
        switch (mode)
        {
            case "TRIANGLES":
                for (int i = 0; i < n / 3 * 3; i += 3)
                { outTris.Add(idx[i]); outTris.Add(idx[i + 1]); outTris.Add(idx[i + 2]); }
                break;
            case "TRIANGLE_STRIP":
                for (int i = 0; i < n - 2; i++)
                {
                    bool odd = (i & 1) == 1;
                    int a = idx[i], b = idx[i + 1], c = idx[i + 2];
                    outTris.Add(odd ? b : a); outTris.Add(odd ? a : b); outTris.Add(c);
                }
                break;
            case "TRIANGLE_FAN":
            case "POLYGON":
                for (int i = 1; i < n - 1; i++) { outTris.Add(idx[0]); outTris.Add(idx[i]); outTris.Add(idx[i + 1]); }
                break;
            case "QUADS":
            {
                int q = n / 4;
                for (int k = 0; k < q; k++) { outTris.Add(idx[4 * k]); outTris.Add(idx[4 * k + 1]); outTris.Add(idx[4 * k + 2]); }
                for (int k = 0; k < q; k++) { outTris.Add(idx[4 * k]); outTris.Add(idx[4 * k + 2]); outTris.Add(idx[4 * k + 3]); }
                break;
            }
            case "QUAD_STRIP":
            {
                int q = (n - 2) / 2;
                for (int k = 0; k < q; k++) { int i = 2 * k; outTris.Add(idx[i]); outTris.Add(idx[i + 1]); outTris.Add(idx[i + 3]); }
                for (int k = 0; k < q; k++) { int i = 2 * k; outTris.Add(idx[i]); outTris.Add(idx[i + 3]); outTris.Add(idx[i + 2]); }
                break;
            }
        }
    }

    public static void ModeLines(string mode, ReadOnlySpan<int> idx, List<int> outLines)
    {
        int n = idx.Length;
        if (mode == "LINES") { for (int i = 0; i < n / 2 * 2; i++) outLines.Add(idx[i]); }
        else if (mode == "LINE_STRIP" && n > 1) { for (int i = 0; i < n - 1; i++) { outLines.Add(idx[i]); outLines.Add(idx[i + 1]); } }
        else if (mode == "LINE_LOOP" && n > 1) { for (int i = 0; i < n; i++) { outLines.Add(idx[i]); outLines.Add(idx[(i + 1) % n]); } }
    }
}

/// <summary>Vertex arrays + primitive sets of one osg::Geometry.</summary>
public sealed class GeomData
{
    public VArray? Vertices, Normals, Colors;
    public SortedDictionary<int, VArray> TexCoords = new();
    public SortedDictionary<string, VArray> Extra = new(StringComparer.Ordinal);
    public List<Primitive> Prims = new();
    public List<string> Notes = new();
    public bool Optimizable = true;

    public int NumVertices => Vertices?.Count ?? 0;

    /// <summary>(name, array) for all arrays in a fixed order.</summary>
    public List<(string name, VArray a)> Arrays()
    {
        var o = new List<(string, VArray)>(4);
        if (Vertices != null) o.Add(("vertices", Vertices));
        if (Normals != null) o.Add(("normals", Normals));
        if (Colors != null) o.Add(("colors", Colors));
        foreach (var kv in TexCoords) o.Add(($"texcoord{kv.Key}", kv.Value));
        foreach (var kv in Extra) o.Add((kv.Key, kv.Value));
        return o;
    }

    IEnumerable<(Primitive p, int[] run)> PrimRuns()
    {
        foreach (var p in Prims)
        {
            if (p.Kind == "lengths")
            {
                int start = p.First;
                foreach (var ln in p.Lengths ?? Array.Empty<int>())
                {
                    var r = new int[Math.Max(ln, 0)];
                    for (int i = 0; i < r.Length; i++) r[i] = start + i;
                    yield return (p, r);
                    start += ln;
                }
            }
            else yield return (p, p.VertexIndices());
        }
    }

    /// <summary>All triangles, flat (3 indices each).</summary>
    public int[] Triangles()
    {
        var l = new List<int>();
        foreach (var (p, run) in PrimRuns())
            if (Modes.Triangle.Contains(p.Mode)) Topology.ModeTriangles(p.Mode, run, l);
        return l.ToArray();
    }

    public int[] Lines()
    {
        var l = new List<int>();
        foreach (var (p, run) in PrimRuns())
            if (Modes.Line.Contains(p.Mode)) Topology.ModeLines(p.Mode, run, l);
        return l.ToArray();
    }

    public int[] Points()
    {
        var l = new List<int>();
        foreach (var (p, run) in PrimRuns())
            if (p.Mode == "POINTS") l.AddRange(run);
        return l.ToArray();
    }

    /// <summary>(triangles, lines, points) without building index arrays.</summary>
    public (long tris, long lines, long points) Counts()
    {
        long tris = 0, lines = 0, points = 0;
        foreach (var p in Prims)
        {
            IEnumerable<long> runs = p.Kind == "lengths"
                ? (p.Lengths ?? Array.Empty<int>()).Select(x => (long)x)
                : new[] { p.Kind == "elements" ? (long)(p.Indices?.Length ?? 0) : p.Count };
            string m = p.Mode;
            long c = 0;
            foreach (var r in runs)
            {
                switch (m)
                {
                    case "TRIANGLES": c += Py.FloorDiv(r, 3); break;
                    case "TRIANGLE_STRIP": case "TRIANGLE_FAN": case "POLYGON": c += Math.Max(r - 2, 0); break;
                    case "QUADS": c += Py.FloorDiv(r, 4) * 2; break;
                    case "QUAD_STRIP": c += Py.FloorDiv(Math.Max(r - 2, 0), 2) * 2; break;
                }
                if (m == "LINES") lines += Py.FloorDiv(r, 2);
                else if (m == "LINE_STRIP") lines += Math.Max(r - 1, 0);
                else if (m == "LINE_LOOP") lines += r;
                else if (m == "POINTS") points += r;
            }
            tris += c;
        }
        return (tris, lines, points);
    }

    public long DrawCalls => Prims.Sum(p => (long)p.DrawCalls);
    public long VertexBytes => Arrays().Sum(a => a.a.NBytes);
    public long IndexBytes => Prims.Sum(p => p.IndexBytes);

    /// <summary>(min, max) of the finite vertex positions, or null.</summary>
    public (double[] lo, double[] hi)? BBox()
    {
        var v = Vertices;
        if (v == null || v.Count == 0) return null;
        int c = v.Comps;
        var lo = new double[c]; var hi = new double[c];
        Array.Fill(lo, double.PositiveInfinity); Array.Fill(hi, double.NegativeInfinity);
        bool any = false;
        var d = v.Data;
        for (int r = 0; r < v.Count; r++)
        {
            bool fin = true;
            for (int k = 0; k < c; k++) if (!double.IsFinite(d[r * c + k])) { fin = false; break; }
            if (!fin) continue;
            any = true;
            for (int k = 0; k < c; k++) { double x = d[r * c + k]; if (x < lo[k]) lo[k] = x; if (x > hi[k]) hi[k] = x; }
        }
        return any ? (lo, hi) : null;
    }

    /// <summary>Hash of the content (arrays + primitives) for duplicate detection.</summary>
    public string ContentKey()
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void S(string s) { h.AppendData(System.Text.Encoding.UTF8.GetBytes(s)); h.AppendData(new byte[] { 0 }); }
        foreach (var (name, a) in Arrays())
        {
            S(name); S(a.Type); S(a.Binding); S(a.Comps.ToString());
            h.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.Data.AsSpan()));
        }
        foreach (var p in Prims)
        {
            S($"{p.Mode}|{p.Kind}|{p.First}|{p.Count}|{p.Instances}");
            if (p.Lengths != null) { S("L"); h.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(p.Lengths.AsSpan())); }
            if (p.Indices != null) { S("I"); h.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(p.Indices.AsSpan())); }
        }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    /// <summary>Shallow copy: arrays are shared (passes replace arrays, they never write into them).</summary>
    public GeomData ShallowCopy()
    {
        var c = (GeomData)MemberwiseClone();
        c.TexCoords = new SortedDictionary<int, VArray>(TexCoords);
        c.Extra = new SortedDictionary<string, VArray>(Extra, StringComparer.Ordinal);
        c.Prims = Prims.Select(p => p.Clone()).ToList();
        c.Notes = new List<string>(Notes);
        return c;
    }

    public void SetFrom(GeomData o)
    {
        Vertices = o.Vertices; Normals = o.Normals; Colors = o.Colors;
        TexCoords = new SortedDictionary<int, VArray>(o.TexCoords);
        Extra = new SortedDictionary<string, VArray>(o.Extra, StringComparer.Ordinal);
        Prims = o.Prims.Select(p => p.Clone()).ToList();
        Notes = new List<string>(o.Notes);
        Optimizable = o.Optimizable;
    }
}

public sealed class Texture
{
    public string File = "";
    public string? Path;
    public bool Exists;
    public int Width, Height;
    public int Channels = 4;
    public long FileBytes;
    public bool Embedded;
    public int Unit;
    public object? Src;
    public Obj? ImageSrc;
    public string Fmt = "";
    public double? Bpp;
    public (int w, int h)? Resize;
    /// <summary>Image made by the optimiser (cards, proxy, rebake atlas); written next to the scene when saving, in the
    /// format of the textures it was baked from (see <see cref="BakedFrom"/> and TextureFormats).</summary>
    public RawImage? Baked;
    /// <summary>The texture files a baked image was made from (same unit; baked sources replaced by their own sources).
    /// Saving writes the baked image in their format (the most common extension by texel area) and channel layout.</summary>
    public List<Texture>? BakedFrom;
    /// <summary>The baked image carries coverage in its alpha (the base unit of cards and billboards): saved with alpha.</summary>
    public bool BakedCoverage;
    /// <summary>How the baked image's mipmaps are made (R3-C8): <see cref="MipRule.Renderer"/> writes one level as before;
    /// the other rules write their own chain into a .dds (see <see cref="AlphaMips"/>).</summary>
    public MipRule Mips;
    /// <summary>The atlas cells whose passing share <see cref="MipRule.Coverage"/> keeps one by one (null: the whole image).</summary>
    public IReadOnlyList<Cards.CellRect>? MipCells;
    /// <summary>downscale_textures with keep_coverage: the resized copy keeps the source's leaf area (alpha passing share).</summary>
    public bool KeepCoverage;
    /// <summary>Companion (sidecar) images of this texture found by the scene's companion patterns (e.g. the infrared
    /// image 'tree_ir.rgb' next to 'tree.rgb', see <see cref="SceneKit.Companions"/>): pattern -> the companion file. For a
    /// baked atlas: pattern -> the companion atlas baked with the same layout (written next to it, named by the pattern).
    /// Never changed in place (edits put a new dictionary), so undo snapshots can share it.</summary>
    public Dictionary<string, Texture>? Companions;
    /// <summary>This texture is a companion image (sampled raw: saved in its sources' channel layout).</summary>
    public bool IsCompanion;
    /// <summary>This image is a class map (Trian3D sensor classes 0-15 in the red channel, "*_Classes.png"): sampled
    /// nearest, never blended (see <see cref="SceneKit.Companions.IsClassMap"/>).</summary>
    public bool IsClassMap;
    /// <summary>Saving also resizes this texture's class-map companions (nearest-neighbour); off: they are copied as they are.</summary>
    public bool ResizeClassMaps;
    /// <summary>The image file was replaced in the inspector: savers point the file reference at <see cref="Path"/>.</summary>
    public bool Repointed;
    /// <summary>Sampling (TX6): wrap modes REPEAT, CLAMP, CLAMP_TO_EDGE, CLAMP_TO_BORDER, MIRROR; filters NEAREST, LINEAR,
    /// NEAREST_MIPMAP_NEAREST, LINEAR_MIPMAP_NEAREST, NEAREST_MIPMAP_LINEAR, LINEAR_MIPMAP_LINEAR (magnification:
    /// NEAREST / LINEAR). Null = not in the file (OSG default: REPEAT, LINEAR_MIPMAP_LINEAR, LINEAR, anisotropy 1).
    /// Savers patch loaded texture objects so the file says what these say.</summary>
    public string? WrapS, WrapT, MinFilter, MagFilter;
    public double? MaxAnisotropy;
    /// <summary>The sampling was changed in the inspector (the file has to be written).</summary>
    public bool SamplingEdited;

    public static readonly string[] WrapModes = { "REPEAT", "CLAMP", "CLAMP_TO_EDGE", "CLAMP_TO_BORDER", "MIRROR" };
    public static readonly string[] MinFilters = { "NEAREST", "LINEAR", "NEAREST_MIPMAP_NEAREST", "LINEAR_MIPMAP_NEAREST", "NEAREST_MIPMAP_LINEAR", "LINEAR_MIPMAP_LINEAR" };
    public static readonly string[] MagFilters = { "NEAREST", "LINEAR" };
    /// <summary>Wrap mode of baked atlases (cards, proxies, rebake): REPEAT would bleed the opposite edge into border cells.</summary>
    public const string AtlasWrap = "CLAMP_TO_EDGE";

    /// <summary>Upper-case and map synonyms (osgt / GL names) to the model's names; null for empty.</summary>
    public static string? NormMode(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim().ToUpperInvariant();
        if (v.StartsWith("GL_", StringComparison.Ordinal)) v = v[3..];
        return v switch
        {
            "MIRRORED_REPEAT" or "MIRRORED_REPEAT_IBM" or "MIRRORED_REPEAT_ARB" => "MIRROR",
            "CLAMP_TO_BORDER_ARB" => "CLAMP_TO_BORDER",
            _ => v,
        };
    }

    /// <summary>"REPEAT" or "CLAMP_TO_EDGE / REPEAT" for the UI ("default" for unset).</summary>
    public string WrapText => WrapS == null && WrapT == null ? "default" : WrapS == WrapT ? WrapS! : $"{WrapS ?? "default"} / {WrapT ?? "default"}";

    /// <summary>"LINEAR_MIPMAP_LINEAR / LINEAR, aniso 8" for the UI ("default" for unset).</summary>
    public string FilterText => (MinFilter == null && MagFilter == null ? "default" : $"{MinFilter ?? "default"} / {MagFilter ?? "default"}")
        + (MaxAnisotropy is double a && a != 1 ? $", aniso {a:0.##}" : "");

    public long GpuBytes
    {
        get
        {
            if (Width == 0 || Height == 0) return 0;
            double bpp = Bpp ?? (Channels >= 3 ? 4 : Math.Max(Channels, 1));
            return (long)Math.Floor((double)Width * Height * bpp * 4 / 3);
        }
    }

    public string Label => File.Length > 0 ? File : (Embedded ? "<embedded>" : "<no file>");

    /// <summary>A copy of every field (for undo snapshots).</summary>
    public Texture ShallowCopy() => (Texture)MemberwiseClone();

    /// <summary>Take every field from <paramref name="o"/> (undo).</summary>
    public void SetFrom(Texture o)
    {
        File = o.File; Path = o.Path; Exists = o.Exists; Width = o.Width; Height = o.Height; Channels = o.Channels;
        FileBytes = o.FileBytes; Embedded = o.Embedded; Unit = o.Unit; Src = o.Src; ImageSrc = o.ImageSrc; Fmt = o.Fmt;
        Bpp = o.Bpp; Resize = o.Resize; Baked = o.Baked; BakedFrom = o.BakedFrom; BakedCoverage = o.BakedCoverage; Repointed = o.Repointed;
        Companions = o.Companions; IsCompanion = o.IsCompanion; IsClassMap = o.IsClassMap; ResizeClassMaps = o.ResizeClassMaps;
        WrapS = o.WrapS; WrapT = o.WrapT; MinFilter = o.MinFilter; MagFilter = o.MagFilter; MaxAnisotropy = o.MaxAnisotropy;
        SamplingEdited = o.SamplingEdited; Mips = o.Mips; MipCells = o.MipCells; KeepCoverage = o.KeepCoverage;
    }
}

public sealed class StateSet
{
    public List<Texture> Textures = new();
    public string MaterialName = "";
    public double[]? Diffuse;
    public Dictionary<string, string> Modes = new();
    public bool Transparent;
    public object? Src;
    public List<string> Summary = new();
}

public sealed class Node
{
    public string Kind;
    public string Cls;
    public string Name;
    public List<Node> Children = new();
    public double[]? Matrix;            // 4x4 row major, column-vector convention (p' = M p)
    public StateSet? StateSet;
    public GeomData? Geom;
    public List<(double min, double max)>? Ranges;
    public string RangeMode = "DISTANCE_FROM_EYE_POINT";
    public List<bool>? SwitchValues;
    public List<string> Files = new();
    public long NodeMask = 0xFFFFFFFFL;
    public object? Src;
    public HashSet<string> Dirty = new();
    public List<string> Notes = new();
    public string? External;
    public double[]? Center;
    // what the Python version keeps in node.extra
    public bool VirtualRoot;
    public List<Node>? Externals;
    public Dictionary<string, string>? FilesOut;
    public Obj? GenObj;
    public Dictionary<string, object>? Extra;

    public Node(string kind, string cls = "", string name = "")
    {
        Kind = kind; Cls = string.IsNullOrEmpty(cls) ? kind : cls; Name = name ?? "";
    }

    public void Mark(string what)
    {
        Dirty.Add(what);
        Edited();
    }

    static long edits;

    /// <summary>Grows with every edit (each <see cref="Mark"/>, also of a key that is already dirty, and each texture edit);
    /// never goes down, so "did this run edit anything" is a before / after comparison (see History.EditSignature).</summary>
    public static long Edits => Interlocked.Read(ref edits);

    /// <summary>Counts an edit that is not a <see cref="Mark"/> (texture edits).</summary>
    public static void Edited() => Interlocked.Increment(ref edits);

    public IReadOnlyList<Node> AllChildren()
    {
        if (Externals == null || Externals.Count == 0) return Children;
        var l = new List<Node>(Children.Count + Externals.Count);
        l.AddRange(Children); l.AddRange(Externals);
        return l;
    }

    public IReadOnlyList<Node> ExternalNodes => (IReadOnlyList<Node>?)Externals ?? Array.Empty<Node>();

    public override string ToString() => $"<Node {Cls} '{Name}' ({Children.Count} children)>";
}

public sealed class Scene
{
    public string Path;
    public string Fmt;
    public Node? Root;
    public Document? Doc;
    public FltFile? Flt;          // set for scenes read from OpenFlight: saving as .flt patches the original records
    public int? Version;
    public List<string> Warnings = new();
    public Dictionary<string, object> Info = new();
    public string? ConvertedFrom;
    public Dictionary<string, Scene?> Externals = new();
    public bool Editable = true;
    public List<string> SavedExternals = new();
    /// <summary>Layered database: first = the layer the master was read from, last = the real database root; null = not layered.</summary>
    public List<string>? Layers;
    /// <summary>Layered load: the database's master file this state stands for (the master was read from a checkpoint
    /// folder, possibly in another format); null otherwise. See DatabaseRoot.Master.</summary>
    public string? Origin;
    /// <summary>Naming rules of companion (sidecar) images, e.g. "{stem}_ir{ext}" (see <see cref="SceneKit.Companions"/>).
    /// Change them with Companions.SetPatterns, which detects the companions again.</summary>
    public List<string> CompanionPatterns = new(SceneKit.Companions.DefaultPatterns);

    public Scene(string path, string fmt) { Path = path; Fmt = fmt; }

    /// <summary>Depth-first, pre-order; shared nodes once when <paramref name="unique"/>.</summary>
    public IEnumerable<Node> IterNodes(bool unique = true, bool externals = false)
    {
        if (Root == null) yield break;
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Node>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (unique && !seen.Add(n)) continue;
            yield return n;
            var kids = externals ? n.AllChildren() : n.Children;
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
    }
}

public static class Mat
{
    public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    /// <summary>OSG writes matrices row-major for row vectors (translation in the last row).</summary>
    public static double[] FromOsg(ReadOnlySpan<double> v)
    {
        var m = new double[16];
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) m[r * 4 + c] = v[c * 4 + r];
        return m;
    }

    public static double[] ToOsg(double[] m)
    {
        var v = new double[16];
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) v[c * 4 + r] = m[r * 4 + c];
        return v;
    }

    public static double[] Mul(double[] a, double[] b)
    {
        var o = new double[16];
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a[r * 4 + k] * b[k * 4 + c];
                o[r * 4 + c] = s;
            }
        return o;
    }

    /// <summary>Largest factor by which the matrix stretches a length: the longest column of the upper 3 x 3 (the image
    /// of a unit axis; mirroring does not matter).</summary>
    public static double MaxScale(double[] m)
    {
        double s = 0;
        for (int c = 0; c < 3; c++) s = Math.Max(s, Math.Sqrt(m[c] * m[c] + m[4 + c] * m[4 + c] + m[8 + c] * m[8 + c]));
        return s;
    }

    /// <summary>Inverse of a 4x4 matrix (Gauss-Jordan with partial pivoting), or null when it is singular.</summary>
    public static double[]? Invert(double[] m)
    {
        var a = (double[])m.Clone();
        var inv = Identity();
        double scale = 0;
        foreach (var v in m) scale = Math.Max(scale, Math.Abs(v));
        if (scale == 0 || !double.IsFinite(scale)) return null;
        for (int c = 0; c < 4; c++)
        {
            int p = c;
            for (int r = c + 1; r < 4; r++) if (Math.Abs(a[r * 4 + c]) > Math.Abs(a[p * 4 + c])) p = r;
            if (Math.Abs(a[p * 4 + c]) < 1e-12 * scale) return null;
            if (p != c)
                for (int k = 0; k < 4; k++)
                {
                    (a[p * 4 + k], a[c * 4 + k]) = (a[c * 4 + k], a[p * 4 + k]);
                    (inv[p * 4 + k], inv[c * 4 + k]) = (inv[c * 4 + k], inv[p * 4 + k]);
                }
            double d = a[c * 4 + c];
            for (int k = 0; k < 4; k++) { a[c * 4 + k] /= d; inv[c * 4 + k] /= d; }
            for (int r = 0; r < 4; r++)
            {
                if (r == c) continue;
                double f = a[r * 4 + c];
                if (f == 0) continue;
                for (int k = 0; k < 4; k++) { a[r * 4 + k] -= f * a[c * 4 + k]; inv[r * 4 + k] -= f * inv[c * 4 + k]; }
            }
        }
        return inv;
    }

    public static (double x, double y, double z) Apply(double[] m, double x, double y, double z) =>
        (m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11]);

    public static bool IsIdentity(double[] m, double rtol = 1e-5, double atol = 1e-8)
    {
        var id = Identity();
        for (int i = 0; i < 16; i++) if (Math.Abs(m[i] - id[i]) > atol + rtol * Math.Abs(id[i])) return false;
        return true;
    }

    public static double[,] QuatToMatrix(double x, double y, double z, double w)
    {
        double n = x * x + y * y + z * z + w * w;
        if (n < 1e-20) return new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        double s = 2.0 / n;
        return new double[,] {
            { 1 - s * (y * y + z * z), s * (x * y - z * w), s * (x * z + y * w) },
            { s * (x * y + z * w), 1 - s * (x * x + z * z), s * (y * z - x * w) },
            { s * (x * z - y * w), s * (y * z + x * w), 1 - s * (x * x + y * y) } };
    }

    /// <summary>PositionAttitudeTransform: M = T(position) * R(attitude) * S(scale) * T(-pivot).</summary>
    public static double[] Pat(double[] pos, double[] att, double[] scale, double[] pivot)
    {
        var R = QuatToMatrix(att[0], att[1], att[2], att[3]);
        var m = Identity();
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) m[r * 4 + c] = R[r, c] * scale[c];
        for (int r = 0; r < 3; r++)
        {
            double t = pos[r];
            for (int c = 0; c < 3; c++) t -= m[r * 4 + c] * pivot[c];
            m[r * 4 + 3] = t;
        }
        return m;
    }
}

public static class Py
{
    public static long FloorDiv(long a, long b) { long q = a / b; if ((a % b != 0) && ((a < 0) != (b < 0))) q--; return q; }
}
