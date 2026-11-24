using System.Buffers.Binary;
using System.Text;

namespace SceneKit;

/// <summary>OpenFlight (.flt) record parsing: the record stream as a node tree (groups, objects, LODs, switches,
/// DOFs, instances, external references, faces, meshes) plus the vertex, texture, colour and material palettes.
/// Port of scenekit/flt_records.py.</summary>
public sealed class FltFile
{
    public const int HEADER = 1, GROUP = 2, OBJECT = 4, FACE = 5, PUSH = 10, POP = 11, DOF = 14;
    public const int PUSH_SUB = 19, POP_SUB = 20, PUSH_EXT = 21, POP_EXT = 22, CONT = 23;
    public const int COLOR_PAL = 32, LONG_ID = 33, MATRIX = 49, MULTITEX = 52, UV_LIST = 53, BSP = 55;
    public const int INST_REF = 61, INST_DEF = 62, EXT_REF = 63, TEX_PAL = 64;
    public const int VTX_PAL = 67, VTX_C = 68, VTX_CN = 69, VTX_CNT = 70, VTX_CT = 71, VTX_LIST = 72, LOD = 73;
    public const int MESH = 84, LVP = 85, MESH_PRIM = 86, MORPH_LIST = 89, SWITCH = 96;
    public const int LIGHT_PT = 111, INDEXED_LP = 130, PUSH_ATTR = 122, POP_ATTR = 123;
    public const int MATERIAL_PAL = 113, REPLICATE = 60;

    public static readonly HashSet<int> NodeOps = new()
    {
        GROUP, OBJECT, FACE, DOF, BSP, INST_REF, INST_DEF, EXT_REF, LOD, MESH, SWITCH,
        LIGHT_PT, INDEXED_LP, 90, 91, 95, 98, 100, 101, 112, 114, 115, 127, 129,
    };

    static readonly Dictionary<int, double> Units = new() { [0] = 1.0, [1] = 1000.0, [4] = 0.3048, [5] = 0.0254, [8] = 1852.0 };
    static readonly Dictionary<int, string> UnitNames = new() { [0] = "meters", [1] = "kilometers", [4] = "feet", [5] = "inches", [8] = "nautical miles" };

    /// <summary>One record of the hierarchy.</summary>
    public sealed class Rec
    {
        public readonly int Op;
        public readonly byte[] Data;
        public readonly List<Rec> Children = new();
        public double[]? Matrix;                 // 4x4 row major, column-vector convention
        public readonly List<int[]> VLists = new();
        public byte[]? Lvp;
        public readonly List<byte[]> Prims = new();
        public byte[]? Multi;                    // multitexture record (texture layers 1-7) of a face or mesh
        public readonly List<byte[]?> UvLists = new();   // UV list record (layers 1-7) of each vertex list, null when none
        public readonly List<bool> Morph = new();        // vertex list j is a morph list (its UV list has u0 v0 u100 v100)
        public string? Name;
        public int Index;                        // position in the record stream (continuations merged), for the writer
        public int Replicate;                    // replicate record: drawn 1 + n times, the matrix applied once more each time
        public Rec(int op, byte[] data) { Op = op; Data = data; }
        public override string ToString() => $"<FLT record {Op} '{Name}'>";
    }

    public readonly string Path, Dir;
    public readonly int? Revision;
    public readonly string Unit;
    public readonly double Scale;
    public readonly Rec Root;
    public readonly Dictionary<string, int> Stats = new()
    {
        ["faces"] = 0, ["meshes"] = 0, ["groups"] = 0, ["objects"] = 0, ["lods"] = 0, ["switches"] = 0,
        ["external_refs"] = 0, ["instances"] = 0, ["light_points"] = 0, ["dofs"] = 0,
    };
    public readonly Dictionary<int, string> Textures = new();
    public double[]? Colors;                     // (n x 3) rgb
    public readonly Dictionary<int, (string name, double[] diffuse, double alpha)> Materials = new();
    public readonly Dictionary<int, Rec> InstDefs = new();
    public double[] Positions = Array.Empty<double>();   // (n x 3), scaled to meters
    public double[] Uvs = Array.Empty<double>();         // (n x 2)
    public double[] Normals = Array.Empty<double>();     // (n x 3)
    public bool HasNormals;
    long[] vkeys = Array.Empty<long>();
    int[] vvals = Array.Empty<int>();

    // kept for writing the file back (FltWrite): file stamp, the faces / meshes the scene shows, and
    // which palette entries the reader's state sets and textures came from
    public long FileLength;
    public DateTime FileTime;
    public readonly HashSet<Rec> Represented = new(ReferenceEqualityComparer.Instance);
    public readonly Dictionary<StateSet, (int tex, int mat)> StateKeys = new(ReferenceEqualityComparer.Instance);
    public readonly Dictionary<int, Texture> TextureObjects = new();
    public readonly Dictionary<Texture, (int index, int effect, int mapping, int data)> LayerTextures = new(ReferenceEqualityComparer.Instance);   // units 1-7
    public readonly Dictionary<Rec, Rec> SubfaceOf = new(ReferenceEqualityComparer.Instance);   // subface -> the face it lies on

    public int NumPositions => Positions.Length / 3;

    static short I16(byte[] d, int o) => BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(o));
    static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
    static int I32(byte[] d, int o) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(o));
    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
    static double F32(byte[] d, int o) => BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(o));
    static double F64(byte[] d, int o) => BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(o));

    /// <summary>Python str.isspace() for latin-1 characters.</summary>
    static bool PySpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r' or '\x1c' or '\x1d' or '\x1e' or '\x1f' or '\x85' or '\xa0';

    public static string PyStrip(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && PySpace(s[a])) a++;
        while (b > a && PySpace(s[b - 1])) b--;
        return s.Substring(a, b - a);
    }

    /// <summary>Zero terminated latin-1 string, stripped.</summary>
    public static string CStr(byte[] d, int start, int end = int.MaxValue)
    {
        start = Math.Min(start, d.Length);
        end = Math.Min(end, d.Length);
        if (end < start) end = start;
        int z = Array.IndexOf(d, (byte)0, start, end - start);
        if (z < 0) z = end;
        return PyStrip(Encoding.Latin1.GetString(d, start, z - start));
    }

    /// <summary>(opcode, bytes, file offset); continuation records are merged into the record before.</summary>
    public static List<(int op, byte[] data, int off)> ReadRecords(byte[] data)
    {
        var recs = new List<(int op, List<(int s, int l)> parts, int off)>();
        int i = 0, n = data.Length;
        while (i + 4 <= n)
        {
            int op = U16(data, i), ln = U16(data, i + 2);
            if (ln < 4) throw new InvalidDataException($"corrupt OpenFlight record (opcode {op}, length {ln}) at byte {i}");
            ln = Math.Min(ln, n - i);
            if (op == CONT && recs.Count > 0) recs[^1].parts.Add((i + 4, ln - 4));
            else recs.Add((op, new List<(int, int)> { (i, ln) }, i));
            i += ln;
        }
        var outl = new List<(int, byte[], int)>(recs.Count);
        foreach (var (op, parts, off) in recs)
        {
            var b = new byte[parts.Sum(p => p.l)];
            int o = 0;
            foreach (var (s, l) in parts) { Buffer.BlockCopy(data, s, b, o, l); o += l; }
            outl.Add((op, b, off));
        }
        return outl;
    }

    public FltFile(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Dir = System.IO.Path.GetDirectoryName(Path) ?? "";
        var data = File.ReadAllBytes(path);
        FileLength = data.Length;
        FileTime = File.GetLastWriteTimeUtc(path);
        if (data.Length < 4 || U16(data, 0) != HEADER) throw new InvalidDataException("not an OpenFlight file (no header record)");
        var recs = ReadRecords(data);
        var hdr = recs[0].data;
        Revision = hdr.Length >= 16 ? I32(hdr, 12) : null;
        int unitCode = hdr.Length > 62 ? hdr[62] : 0;
        Unit = UnitNames.TryGetValue(unitCode, out var un) ? un : $"code {unitCode}";
        Scale = Units.GetValueOrDefault(unitCode, 1.0);
        Root = new Rec(HEADER, hdr) { Name = hdr.Length >= 12 ? CStr(hdr, 4, 12) : "" };
        Build(recs);
    }

    void Build(List<(int op, byte[] data, int off)> recs)
    {
        var pos = new List<double>(); var uv = new List<double>(); var nrm = new List<double>(); var keys = new List<long>();
        long vpalStart = 0;
        Rec parent = Root, last = Root;
        var stack = new Stack<Rec>();
        int skip = 0;
        for (int r = 1; r < recs.Count; r++)
        {
            var (op, d, off) = recs[r];
            if (skip > 0)
            {
                if (op is PUSH_EXT or PUSH_ATTR) skip++;
                else if (op is POP_EXT or POP_ATTR) skip--;
                continue;
            }
            if (op is PUSH_EXT or PUSH_ATTR) skip = 1;
            else if (op == VTX_PAL) vpalStart = off;
            else if (op >= VTX_C && op <= VTX_CT)
            {
                pos.Add(F64(d, 8)); pos.Add(F64(d, 16)); pos.Add(F64(d, 24));
                if (op == VTX_CNT && d.Length >= 52) { uv.Add(F32(d, 44)); uv.Add(F32(d, 48)); }
                else if (op == VTX_CT && d.Length >= 40) { uv.Add(F32(d, 32)); uv.Add(F32(d, 36)); }
                else { uv.Add(0.0); uv.Add(0.0); }
                if ((op == VTX_CN || op == VTX_CNT) && d.Length >= 44) { nrm.Add(F32(d, 32)); nrm.Add(F32(d, 36)); nrm.Add(F32(d, 40)); }
                else { nrm.Add(0.0); nrm.Add(0.0); nrm.Add(0.0); }
                keys.Add(off - vpalStart);
            }
            else if (op == TEX_PAL && d.Length >= 208) Textures[I32(d, 204)] = CStr(d, 4, 204);
            else if (op == COLOR_PAL && d.Length >= 136)
            {
                int n = Math.Min(1024, (d.Length - 132) / 4);
                Colors = new double[n * 3];
                for (int i = 0; i < n; i++)
                {
                    int o = 132 + 4 * i;   // a b g r
                    Colors[3 * i] = d[o + 3] / 255.0; Colors[3 * i + 1] = d[o + 2] / 255.0; Colors[3 * i + 2] = d[o + 1] / 255.0;
                }
            }
            else if (op is PUSH or PUSH_SUB) { stack.Push(parent); parent = last; }
            else if (op is POP or POP_SUB) { last = parent; parent = stack.Count > 0 ? stack.Pop() : Root; }
            else if (NodeOps.Contains(op))
            {
                var node = new Rec(op, d) { Index = r };
                if (d.Length >= 12) node.Name = CStr(d, 4, 12);
                parent.Children.Add(node);
                last = node;
                Count(node);
            }
            else if (op == MATRIX && d.Length >= 68)
            {
                // OpenFlight uses row vectors: transpose to the column-vector convention
                var m = new double[16];
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) m[j * 4 + i] = F32(d, 4 + 4 * (i * 4 + j));
                for (int i = 0; i < 3; i++) m[i * 4 + 3] *= Scale;   // translations are in database units, like the vertices
                last.Matrix = m;
            }
            else if (op == VTX_LIST) { last.VLists.Add(Ints(d, 4, 1)); last.UvLists.Add(null); last.Morph.Add(false); }
            else if (op == MORPH_LIST) { last.VLists.Add(Ints(d, 4, 2)); last.UvLists.Add(null); last.Morph.Add(true); }
            else if (op == MULTITEX) last.Multi = d;
            else if (op == UV_LIST && last.UvLists.Count > 0) last.UvLists[^1] = d;
            else if (op == LVP) last.Lvp = d;
            else if (op == MESH_PRIM) last.Prims.Add(d);
            else if (op == LONG_ID) last.Name = CStr(d, 4);
            else if (op == REPLICATE && d.Length >= 6) last.Replicate = I16(d, 4);
            else if (op == MATERIAL_PAL && d.Length >= 80)
                Materials[I32(d, 4)] = (CStr(d, 8, 20), new[] { F32(d, 36), F32(d, 40), F32(d, 44) }, F32(d, 76));
        }
        Positions = pos.ToArray();
        if (Scale != 1.0) for (int i = 0; i < Positions.Length; i++) Positions[i] *= Scale;
        Uvs = uv.ToArray();
        Normals = nrm.ToArray();
        HasNormals = Normals.Any(x => x != 0.0);
        var order = Enumerable.Range(0, keys.Count).OrderBy(i => keys[i]).ToArray();
        vkeys = order.Select(i => keys[i]).ToArray();
        vvals = order;
    }

    /// <summary>Drop the vertex arrays once the scene is built (the writer needs only the palettes).</summary>
    public void ReleaseVertices()
    {
        Positions = Array.Empty<double>(); Uvs = Array.Empty<double>(); Normals = Array.Empty<double>();
        vkeys = Array.Empty<long>(); vvals = Array.Empty<int>();
    }

    static int[] Ints(byte[] d, int offset, int stride)
    {
        int n = Math.Max(0, (d.Length - offset) / 4);
        var r = new int[(n + stride - 1) / stride];
        for (int i = 0, k = 0; i < n; i += stride, k++) r[k] = I32(d, offset + 4 * i);
        return r;
    }

    void Count(Rec node)
    {
        string? key = node.Op switch
        {
            FACE => "faces", MESH => "meshes", GROUP => "groups", OBJECT => "objects", LOD => "lods", SWITCH => "switches",
            EXT_REF => "external_refs", INST_REF => "instances", LIGHT_PT => "light_points", INDEXED_LP => "light_points", DOF => "dofs",
            _ => null,
        };
        if (key != null) Stats[key]++;
        if (node.Op == INST_DEF && node.Data.Length >= 8) InstDefs[I16(node.Data, 6)] = node;
    }

    /// <summary>Vertex palette byte offsets -> vertex indices (null when one is not a vertex).</summary>
    public int[]? PaletteIndices(int[] offsets)
    {
        if (vkeys.Length == 0) return null;
        var r = new int[offsets.Length];
        for (int i = 0; i < offsets.Length; i++)
        {
            int p = Array.BinarySearch(vkeys, (long)offsets[i]);
            if (p < 0) return null;
            while (p > 0 && vkeys[p - 1] == offsets[i]) p--;   // searchsorted: the first match
            r[i] = vvals[p];
        }
        return r;
    }

    /// <summary>Colour palette entry: 128 intensities per colour.</summary>
    public double[]? Color(long index)
    {
        if (Colors == null) return null;
        long pal = index / 128, intensity = index % 128;
        if (pal >= Colors.Length / 3) return null;
        double f = Math.Max(intensity, 1) / 127.0;
        return new[] { Colors[3 * pal] * f, Colors[3 * pal + 1] * f, Colors[3 * pal + 2] * f };
    }

    /// <summary>Visibility, texture pattern, material and colour of a face (shift 0) or mesh (shift 4) record.</summary>
    public (bool hidden, int tex, double[]? color) FaceAttributes(byte[] d, int shift)
    {
        bool Has(int off, int size) => d.Length >= off + shift + size;
        int drawType = Has(18, 1) ? (sbyte)d[18 + shift] : 0;
        uint flags = Has(44, 4) ? U32(d, 44 + shift) : 0;
        int tex = Has(28, 2) ? I16(d, 28 + shift) : -1;
        bool hidden = (flags & 0x04000000) != 0 || drawType is 8 or 9 or 10;
        double[]? color = null;
        if ((flags & 0x10000000) != 0)   // packed colour
        {
            if (Has(56, 4))
            {
                uint packed = U32(d, 56 + shift);
                color = new[] { (packed & 0xFF) / 255.0, ((packed >> 8) & 0xFF) / 255.0, ((packed >> 16) & 0xFF) / 255.0 };
            }
        }
        else if ((flags & 0x40000000) == 0)   // has colour
        {
            long? idx = Has(68, 4) ? U32(d, 68 + shift) : null;
            if (idx == null || idx == 0xFFFFFFFF) idx = Has(20, 2) ? U16(d, 20 + shift) : null;
            if (idx != null && idx != 0xFFFF) color = Color(idx.Value);
        }
        int mat = Has(30, 2) ? I16(d, 30 + shift) : -1;   // material palette index: the diffuse modulates the face colour
        if (Materials.Count > 0 && mat >= 0 && Materials.TryGetValue(mat, out var m))
        {
            var diffuse = m.diffuse.Select(x => Math.Clamp(x, 0.0, 1.0)).ToArray();
            color = color == null ? diffuse : new[] { color[0] * diffuse[0], color[1] * diffuse[1], color[2] * diffuse[2] };
        }
        return (hidden, tex, color);
    }

    /// <summary>Local vertex pool: positions (n x 3) and the first texture coordinates (n x 2) when present.</summary>
    public static (double[] pos, double[]? uv) LvpArrays(byte[] d)
    {
        var (pos, uvs) = LvpArrays(d, true);
        return (pos, uvs.GetValueOrDefault(0));
    }

    /// <summary>Local vertex pool: positions (n x 3) and the texture coordinates (n x 2) of every unit present (base UV = unit 0, UV1-7).</summary>
    public static (double[] pos, Dictionary<int, double[]> uvs) LvpArrays(byte[] d, bool allUnits)
    {
        var uvs = new Dictionary<int, double[]>();
        if (d.Length < 12) return (Array.Empty<double>(), uvs);
        uint n = U32(d, 4), mask = U32(d, 8);
        if ((mask & 0x80000000) == 0) return (Array.Empty<double>(), uvs);
        int size = 24;
        var uvOff = new List<(int unit, int off)>();
        if ((mask & 0x40000000) != 0) size += 4;
        else if ((mask & 0x20000000) != 0) size += 4;   // colour index and RGBA exclude each other (as OSG reads it)
        if ((mask & 0x10000000) != 0) size += 12;
        for (int k = 0; k < 8; k++)   // base UV (0x08000000), then UV1-7
            if ((mask & (0x08000000u >> k)) != 0) { if (k == 0 || allUnits) uvOff.Add((k, size)); size += 8; }
        int count = (int)Math.Min(n, (uint)Math.Max(0, (d.Length - 12) / size));
        var pos = new double[count * 3];
        foreach (var (unit, _) in uvOff) uvs[unit] = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            int o = 12 + i * size;
            pos[3 * i] = F64(d, o); pos[3 * i + 1] = F64(d, o + 8); pos[3 * i + 2] = F64(d, o + 16);
            foreach (var (unit, off) in uvOff) { uvs[unit][2 * i] = F32(d, o + off); uvs[unit][2 * i + 1] = F32(d, o + off + 4); }
        }
        return (pos, uvs);
    }

    /// <summary>Multitexture record: the texture layers present (units 1-7, attribute mask bit 31 = layer 1), each with its
    /// texture pattern, effect (0 texture environment, 1 bump map, > 100 user defined), texture mapping and user data.</summary>
    public static List<(int unit, int tex, int effect, int mapping, int data)> Layers(byte[]? d)
    {
        var r = new List<(int, int, int, int, int)>();
        if (d == null || d.Length < 8) return r;
        uint mask = U32(d, 4);
        int o = 8;
        for (int u = 1; u <= 7; u++)
        {
            if ((mask & (0x80000000u >> (u - 1))) == 0) continue;
            if (o + 8 > d.Length) break;
            r.Add((u, I16(d, o), U16(d, o + 2), I16(d, o + 4), U16(d, o + 6)));
            o += 8;
        }
        return r;
    }

    /// <summary>UV list record: texture coordinates of layers 1-7 (n x 2 per unit) for the n vertices of the vertex list before it;
    /// stored per vertex, its layers in unit order. After a morph vertex list each layer holds u0 v0 u100 v100; the 0 %
    /// coordinates are used. Vertices the record is too short for get 0 0.</summary>
    public static Dictionary<int, double[]> UvList(byte[]? d, int n, bool morph = false)
    {
        var r = new Dictionary<int, double[]>();
        if (d == null || d.Length < 8) return r;
        uint mask = U32(d, 4);
        var units = Enumerable.Range(1, 7).Where(u => (mask & (0x80000000u >> (u - 1))) != 0).ToArray();
        if (units.Length == 0) return r;
        foreach (int u in units) r[u] = new double[n * 2];
        int stride = morph ? 16 : 8;
        int count = Math.Min(n, (d.Length - 8) / (stride * units.Length));
        for (int i = 0; i < count; i++)
            for (int k = 0; k < units.Length; k++)
            {
                int o = 8 + stride * (i * units.Length + k);
                r[units[k]][2 * i] = F32(d, o); r[units[k]][2 * i + 1] = F32(d, o + 4);
            }
        return r;
    }

    /// <summary>Mesh primitive record: (type, indices) - 1 strip, 2 fan, 3 quad strip, 4 polygon.</summary>
    public static (int type, int[]? idx) MeshPrimitive(byte[] p)
    {
        if (p.Length < 12) return (0, null);
        int ptype = I16(p, 4), isize = U16(p, 6);
        long count = U32(p, 8);
        if (isize is not (1 or 2 or 4) || count == 0) return (ptype, null);
        count = Math.Min(count, (p.Length - 12) / isize);
        var idx = new int[count];
        for (int i = 0; i < count; i++)
        {
            int o = 12 + i * isize;
            idx[i] = isize == 1 ? p[o] : isize == 2 ? U16(p, o) : (int)U32(p, o);
        }
        return (ptype, idx);
    }

    public static int ReadInt16(byte[] d, int o) => I16(d, o);
    public static int ReadUInt16(byte[] d, int o) => U16(d, o);
    public static uint ReadUInt32(byte[] d, int o) => U32(d, o);
    public static int ReadInt32(byte[] d, int o) => I32(d, o);
    public static double ReadDouble(byte[] d, int o) => F64(d, o);
}

/// <summary>OpenFlight -> scene model, for inspection and optimisation (port of scenekit/fltread.py).
/// The record hierarchy is kept; the faces of an object become one geometry per texture + material,
/// like OpenSceneGraph's own OpenFlight loader builds them.</summary>
public sealed class FltRead
{
    static readonly Dictionary<int, (string kind, string cls)> KindOf = new()
    {
        [FltFile.HEADER] = (Kind.Group, "FLT Header"), [FltFile.GROUP] = (Kind.Group, "FLT Group"), [FltFile.OBJECT] = (Kind.Geode, "FLT Object"),
        [FltFile.LOD] = (Kind.Lod, "FLT LOD"), [FltFile.SWITCH] = (Kind.Switch, "FLT Switch"), [FltFile.DOF] = (Kind.Transform, "FLT DOF"),
        [FltFile.EXT_REF] = (Kind.Proxy, "FLT External Reference"), [FltFile.INST_DEF] = (Kind.Group, "FLT Instance Definition"),
        [FltFile.BSP] = (Kind.Group, "FLT BSP"), [FltFile.LIGHT_PT] = (Kind.Other, "FLT Light Point"), [FltFile.INDEXED_LP] = (Kind.Other, "FLT Light Point"),
        [90] = (Kind.Other, "FLT Light Source"), [91] = (Kind.Other, "FLT Sound"), [95] = (Kind.Other, "FLT Text"),
        [98] = (Kind.Other, "FLT Road Segment"), [100] = (Kind.Other, "FLT Road Path"), [101] = (Kind.Other, "FLT Clip"),
        [112] = (Kind.Other, "FLT CAT"), [114] = (Kind.Other, "FLT Extension"), [115] = (Kind.Other, "FLT Curve"),
        [127] = (Kind.Other, "FLT Light Point System"), [129] = (Kind.Other, "FLT Light Point"),
    };

    readonly FltFile flt;
    readonly Dictionary<int, Texture> textures = new();
    readonly Dictionary<(int tex, int unit, int effect), Texture> layerTextures = new();
    readonly Dictionary<(int? tex, int? mat, string layers), StateSet> statesets = new();
    readonly Dictionary<int, Node> instNodes = new();
    public int HiddenFaces;

    FltRead(FltFile f) { flt = f; }

    public static Scene Read(string path)
    {
        var f = new FltFile(path);
        var scene = new Scene(System.IO.Path.GetFullPath(path), "flt");
        var b = new FltRead(f);
        scene.Root = b.NodeOf(f.Root, 0);
        if (string.IsNullOrEmpty(scene.Root.Name)) scene.Root.Name = System.IO.Path.GetFileName(path);
        scene.Info["openflight_revision"] = f.Revision!;
        scene.Info["units"] = f.Unit;
        scene.Info["records"] = new Dictionary<string, int>(f.Stats);
        scene.Info["texture_palette"] = f.Textures.Count;
        scene.Info["vertex_palette"] = f.NumPositions;
        scene.Info["hidden_faces"] = b.HiddenFaces;
        if (f.Scale != 1.0)
        {
            scene.Info["unit_scale"] = f.Scale;
            scene.Info["unit_name"] = f.Unit;   // what the file stores (positions are converted to metres on load)
        }
        scene.Editable = false;
        foreach (var (k, ss) in b.statesets) f.StateKeys[ss] = (k.tex ?? -1, k.mat ?? -1);
        foreach (var (i, t) in b.textures) f.TextureObjects[i] = t;
        f.ReleaseVertices();
        scene.Flt = f;            // lets FltWrite patch the original records when saving as .flt
        return scene;
    }

    Texture? TextureOf(int? idx)
    {
        if (idx == null || idx < 0 || !flt.Textures.TryGetValue(idx.Value, out var file)) return null;
        if (!textures.TryGetValue(idx.Value, out var t))
        {
            t = new Texture { File = file };
            Images.FillTexture(t, flt.Dir);
            FltAttr.Read(t);
            textures[idx.Value] = t;
        }
        return t;
    }

    /// <summary>Texture of a multitexture layer (unit 1-7): its own object per unit and effect, remembered for the writer.</summary>
    Texture? LayerTextureOf((int unit, int tex, int effect, int mapping, int data) l)
    {
        if (l.tex < 0 || !flt.Textures.TryGetValue(l.tex, out var file)) return null;
        if (!layerTextures.TryGetValue((l.tex, l.unit, l.effect), out var t))
        {
            t = new Texture { File = file, Unit = l.unit };
            Images.FillTexture(t, flt.Dir);
            FltAttr.Read(t);
            layerTextures[(l.tex, l.unit, l.effect)] = t;
            flt.LayerTextures[t] = (l.tex, l.effect, l.mapping, l.data);
        }
        return t;
    }

    /// <summary>Texture layers of a face or mesh (those with a texture pattern) and their key for grouping faces into state sets.</summary>
    static (List<(int unit, int tex, int effect, int mapping, int data)> layers, string key) LayersOf(FltFile.Rec r)
    {
        var l = FltFile.Layers(r.Multi).Where(x => x.tex >= 0).ToList();
        return (l, string.Join(",", l.Select(x => $"{x.unit}:{x.tex}:{x.effect}")));
    }

    StateSet StateSetOf(int? tex, int? mat, List<(int unit, int tex, int effect, int mapping, int data)>? layers = null, string layerKey = "")
    {
        if (statesets.TryGetValue((tex, mat, layerKey), out var ss)) return ss;
        ss = new StateSet();
        var t = TextureOf(tex);
        if (t != null) { ss.Textures.Add(t); ss.Summary.Add($"Texture2D[0] {t.Label}"); }
        foreach (var l in layers ?? new())
        {
            var lt = LayerTextureOf(l);
            if (lt == null) continue;
            ss.Textures.Add(lt);
            ss.Summary.Add($"Texture2D[{l.unit}] {lt.Label}" + (l.effect == 1 ? " (bump map)" : l.effect != 0 ? $" (effect {l.effect})" : ""));
        }
        (string name, double[] diffuse, double alpha)? m = mat is int mi && mi >= 0 && flt.Materials.TryGetValue(mi, out var mm) ? mm : null;
        if (m != null)
        {
            ss.MaterialName = m.Value.name;
            ss.Diffuse = new[] { m.Value.diffuse[0], m.Value.diffuse[1], m.Value.diffuse[2], m.Value.alpha };
            ss.Transparent = m.Value.alpha < 0.999;
            ss.Summary.Add($"Material '{m.Value.name}'");
        }
        if (ss.Textures.Count == 0 && m == null) ss.Summary.Add("vertex colours");
        statesets[(tex, mat, layerKey)] = ss;
        return ss;
    }

    Node NodeOf(FltFile.Rec rec, int depth)
    {
        var n = BuildNode(rec, depth);
        return rec.Replicate > 0 && n.Matrix != null && n.Kind != Kind.Geometry ? Replicated(n, rec.Replicate) : WithTransform(n);
    }

    /// <summary>Replicate record (fences, rows of trees ...): like OpenSceneGraph's loader, 1 + count copies of the node
    /// under transforms identity, M, M², ...; the copies share the node.</summary>
    static Node Replicated(Node n, int count)
    {
        var m = n.Matrix!;
        n.Matrix = null;
        var wrap = new Node(Kind.Group, "FLT Replicate", n.Name) { Extra = new() { ["replicate_matrix"] = m } };
        wrap.Notes.Add($"replicated {count} times");
        var acc = Mat.Identity();
        for (int i = 0; i <= count; i++)
        {
            var t = new Node(Kind.Transform, "FLT Replicate Copy", $"{n.Name} #{i}") { Matrix = acc };
            t.Children.Add(n);
            wrap.Children.Add(t);
            acc = Mat.Mul(acc, m);
        }
        return wrap;
    }

    Node BuildNode(FltFile.Rec rec, int depth)
    {
        int op = rec.Op;
        var d = rec.Data;
        if (op == FltFile.INST_REF && d.Length >= 8)
        {
            int num = FltFile.ReadInt16(d, 6);
            var inst = new Node(Kind.Group, "FLT Instance Reference", $"instance {num}") { Src = rec };
            if (flt.InstDefs.TryGetValue(num, out var target))
            {
                if (!instNodes.TryGetValue(num, out var shared))
                    instNodes[num] = shared = NodeOf(target, depth + 1);
                inst.Children.Add(shared);
            }
            else inst.Notes.Add($"instance definition {num} not found");
            if (rec.Matrix != null) inst.Matrix = (double[])rec.Matrix.Clone();
            return inst;
        }
        var (kind, cls) = KindOf.TryGetValue(op, out var kc) ? kc : (Kind.Other, $"FLT record {op}");
        var n = new Node(kind, cls, rec.Name ?? "") { Src = rec };
        if (rec.Matrix != null) n.Matrix = (double[])rec.Matrix.Clone();
        if (op == FltFile.LOD && d.Length >= 32)
        {
            double switchIn = FltFile.ReadDouble(d, 16), switchOut = FltFile.ReadDouble(d, 24);
            n.Ranges = new List<(double, double)> { (switchOut, switchIn) };
            n.Notes.Add($"switch in {PyFormat.G(switchIn)} / out {PyFormat.G(switchOut)}");
            if (d.Length >= 64)   // centre at record offset 40, in database units like the vertices
                n.Center = new[] { FltFile.ReadDouble(d, 40) * flt.Scale, FltFile.ReadDouble(d, 48) * flt.Scale, FltFile.ReadDouble(d, 56) * flt.Scale };
        }
        else if (op == FltFile.SWITCH)
        {
            var vals = SwitchMask(rec);
            if (vals != null) n.SwitchValues = vals;
        }
        else if (op == FltFile.EXT_REF)
        {
            string r = d.Length >= 8 ? FltFile.PyStrip(FltFile.CStr(d, 4, 204).Split('<', 2)[0]) : "";
            n.Files = r.Length > 0 ? new List<string> { r } : new List<string>();
            if (n.Name.Length == 0) n.Name = System.IO.Path.GetFileName(r);
        }
        else if (op == FltFile.OBJECT && d.Length >= 16 && (FltFile.ReadUInt32(d, 12) & 0x04000000) != 0)
            n.Notes.Add("shadow object");
        else if (op == FltFile.GROUP && d.Length >= 20 && (FltFile.ReadUInt32(d, 16) & (0x40000000 | 0x20000000 | 0x02000000)) != 0)
            n.Notes.Add("animated group");
        var faces = new List<FltFile.Rec>();
        var meshes = new List<FltFile.Rec>();
        foreach (var c in rec.Children)
        {
            if (c.Op == FltFile.FACE) { faces.Add(c); AddSubfaces(c, faces); continue; }
            if (c.Op == FltFile.MESH) { meshes.Add(c); continue; }
            if (c.Op == FltFile.INST_DEF) continue;   // drawn through instance references
            if (depth > 400) { n.Notes.Add("hierarchy too deep, truncated"); break; }
            n.Children.Add(NodeOf(c, depth + 1));
        }
        if (faces.Count > 0) n.Children.InsertRange(0, FaceGeometries(faces));
        foreach (var m in meshes)
        {
            var g = MeshGeometry(m);
            if (g != null) n.Children.Add(g);
        }
        return n;
    }

    /// <summary>Subfaces (decals: windows, signs, markings on a wall or road) are children of the face they lie on;
    /// OpenSceneGraph draws them (with a polygon offset), so they are geometry here too.</summary>
    void AddSubfaces(FltFile.Rec face, List<FltFile.Rec> faces)
    {
        foreach (var s in face.Children)
        {
            if (s.Op != FltFile.FACE) continue;
            flt.SubfaceOf[s] = face;
            faces.Add(s);
            AddSubfaces(s, faces);
        }
    }

    static List<bool>? SwitchMask(FltFile.Rec rec)
    {
        var d = rec.Data;
        if (d.Length < 28) return null;
        int cur = FltFile.ReadInt32(d, 16), words = FltFile.ReadInt32(d, 20), nmasks = FltFile.ReadInt32(d, 24);
        if (!(0 <= cur && cur < nmasks) || words <= 0) return null;
        long start = 28 + (long)cur * words * 4;
        if (start + (long)words * 4 > d.Length) return null;
        var masks = new uint[words];
        for (int i = 0; i < words; i++) masks[i] = FltFile.ReadUInt32(d, (int)start + 4 * i);
        int cnt = rec.Children.Count;
        var r = new List<bool>(cnt);
        for (int i = 0; i < cnt; i++) r.Add(i / 32 < masks.Length ? ((masks[i / 32] >> (i % 32)) & 1) != 0 : true);
        return r;
    }

    /// <summary>numpy round(x, decimals): multiply, round half to even, divide.</summary>
    static double NpRound(double x, double scale) => Math.Round(x * scale, MidpointRounding.ToEven) / scale;

    sealed record ColorKey(double R, double G, double B);

    /// <summary>Faces of one parent -> one Geometry node per texture + material.</summary>
    List<Node> FaceGeometries(List<FltFile.Rec> faces)
    {
        var buckets = new Dictionary<(int tex, int mat, bool sub, int bb, bool glass, string layers), List<(int[] idx, double[]? color, int drawType, double[]? matrix, Dictionary<int, double[]>? uvs)>>();
        var bucketRecs = new Dictionary<(int tex, int mat, bool sub, int bb, bool glass, string layers), List<FltFile.Rec>>();
        var bucketOrder = new List<(int tex, int mat, bool sub, int bb, bool glass, string layers)>();
        var bucketLayers = new Dictionary<string, List<(int unit, int tex, int effect, int mapping, int data)>>();
        foreach (var f in faces)
        {
            var (hidden, tex, color) = flt.FaceAttributes(f.Data, 0);
            if (hidden) { HiddenFaces++; continue; }
            if (f.VLists.Count == 0) continue;
            var offs = f.VLists.Count == 1 ? f.VLists[0] : f.VLists.SelectMany(x => x).ToArray();
            if (offs.Length < 3) continue;
            var idx = flt.PaletteIndices(offs);
            if (idx == null) continue;
            int drawType = f.Data.Length > 18 ? (sbyte)f.Data[18] : 0;
            int mat = f.Data.Length >= 32 ? FltFile.ReadInt16(f.Data, 30) : -1;
            int bb = f.Data.Length > 25 ? f.Data[25] : 0;   // template: 0/1 fixed, 2 axial, 4 point billboard
            if (bb != 2 && bb != 4) bb = 0;
            bool glass = f.Data.Length >= 42 && FltFile.ReadUInt16(f.Data, 40) > 0;   // face transparency at 40 (0 = opaque)
            var (layers, lk) = LayersOf(f);   // multitexture: faces with other layer textures get their own state set
            bucketLayers.TryAdd(lk, layers);
            var key = (tex, mat, flt.SubfaceOf.ContainsKey(f), bb, glass, lk);   // decals, billboards and glass get their own geometry
            if (!buckets.TryGetValue(key, out var items)) { buckets[key] = items = new(); bucketRecs[key] = new(); bucketOrder.Add(key); }
            bucketRecs[key].Add(f);
            flt.Represented.Add(f);
            items.Add((idx, color, drawType, f.Matrix, layers.Count > 0 ? LayerUvs(f, offs.Length) : null));   // some writers (e.g. osgconv) put a matrix record on single faces
        }
        var outl = new List<Node>();
        foreach (var bk in bucketOrder)
        {
            var (tex, mat, sub, bb, glass, lk) = bk;
            var items = buckets[bk];
            var units = bucketLayers[lk].Select(l => l.unit).ToArray();
            var hasUnit = new bool[units.Length];
            var luvIds = new Dictionary<string, int>();
            var luvs = new List<double[]>();   // per distinct corner: u v of every layer unit
            var g = new GeomData();
            var colIds = new Dictionary<ColorKey, int>();
            var colOrder = new List<ColorKey?>();
            int noColorId = -1;
            var matIds = new Dictionary<string, int>();
            var mats = new List<double[]>();
            var keys = new List<(int pal, int cid, int mid, int lid)>();
            var tris = new List<int>();
            var lines = new List<int>();
            foreach (var (idx, color, drawType, fmat, fuvs) in items)
            {
                int cid;
                if (color == null)
                {
                    if (noColorId < 0) { noColorId = colOrder.Count; colOrder.Add(null); }
                    cid = noColorId;
                }
                else
                {
                    var ck = new ColorKey(NpRound(color[0], 1e4), NpRound(color[1], 1e4), NpRound(color[2], 1e4));
                    if (!colIds.TryGetValue(ck, out cid)) { cid = colOrder.Count; colIds[ck] = cid; colOrder.Add(ck); }
                }
                int mid = -1;
                if (fmat != null)
                {
                    var mk = string.Join(",", fmat.Select(x => (NpRound(x, 1e9) + 0.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                    if (!matIds.TryGetValue(mk, out mid)) { mid = mats.Count; matIds[mk] = mid; mats.Add(fmat); }
                }
                int start = keys.Count;
                for (int u = 0; u < units.Length; u++) if (fuvs != null && fuvs.ContainsKey(units[u])) hasUnit[u] = true;
                for (int c = 0; c < idx.Length; c++)
                {
                    int lid = -1;
                    if (units.Length > 0)
                    {
                        var lv = new double[2 * units.Length];
                        for (int u = 0; u < units.Length; u++)
                            if (fuvs != null && fuvs.TryGetValue(units[u], out var a)) { lv[2 * u] = a[2 * c]; lv[2 * u + 1] = a[2 * c + 1]; }
                        var lkey = string.Join(",", lv.Select(BitConverter.DoubleToInt64Bits));
                        if (!luvIds.TryGetValue(lkey, out lid)) { lid = luvs.Count; luvIds[lkey] = lid; luvs.Add(lv); }
                    }
                    keys.Add((idx[c], cid, mid, lid));
                }
                var local = Enumerable.Range(start, idx.Length).ToArray();
                if (drawType is 2 or 3) Topology.ModeLines(drawType == 2 ? "LINE_LOOP" : "LINE_STRIP", local, lines);   // wireframe
                else Topology.ModeTriangles("POLYGON", local, tris);
            }
            // weld identical (palette vertex, colour, matrix, layer uvs) rows: numpy unique rows (sorted) + inverse
            var order = Enumerable.Range(0, keys.Count).ToArray();
            Array.Sort(order, (x, y) =>
            {
                int c = keys[x].pal.CompareTo(keys[y].pal); if (c != 0) return c;
                c = keys[x].cid.CompareTo(keys[y].cid); if (c != 0) return c;
                c = keys[x].mid.CompareTo(keys[y].mid); if (c != 0) return c;
                c = keys[x].lid.CompareTo(keys[y].lid); if (c != 0) return c;
                return x.CompareTo(y);
            });
            var inv = new int[keys.Count];
            var uniq = new List<(int pal, int cid, int mid, int lid)>();
            for (int i = 0; i < order.Length; i++)
            {
                var k = keys[order[i]];
                if (uniq.Count == 0 || uniq[^1] != k) uniq.Add(k);
                inv[order[i]] = uniq.Count - 1;
            }
            int nv = uniq.Count;
            var pos = new double[nv * 3];
            double[]? nrm = flt.HasNormals ? new double[nv * 3] : null;
            for (int i = 0; i < nv; i++)
            {
                int p = uniq[i].pal;
                for (int c = 0; c < 3; c++) pos[3 * i + c] = flt.Positions[3 * p + c];
                if (nrm != null) for (int c = 0; c < 3; c++) nrm[3 * i + c] = flt.Normals[3 * p + c];
            }
            for (int mid = 0; mid < mats.Count; mid++)
            {
                var fm = mats[mid];
                var inv3 = Inverse3(fm);
                for (int i = 0; i < nv; i++)
                {
                    if (uniq[i].mid != mid) continue;
                    double x = pos[3 * i], y = pos[3 * i + 1], z = pos[3 * i + 2];
                    for (int r = 0; r < 3; r++) pos[3 * i + r] = x * fm[r * 4] + y * fm[r * 4 + 1] + z * fm[r * 4 + 2] + fm[r * 4 + 3];
                    if (nrm != null && inv3 != null)
                    {
                        double a = nrm[3 * i], b = nrm[3 * i + 1], c = nrm[3 * i + 2];
                        double nx = a * inv3[0] + b * inv3[3] + c * inv3[6];
                        double ny = a * inv3[1] + b * inv3[4] + c * inv3[7];
                        double nz = a * inv3[2] + b * inv3[5] + c * inv3[8];
                        double ln = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (ln > 0) { nx /= ln; ny /= ln; nz /= ln; }
                        nrm[3 * i] = nx; nrm[3 * i + 1] = ny; nrm[3 * i + 2] = nz;
                    }
                }
            }
            g.Vertices = new VArray(pos, 3, "Vec3Array");
            if (nrm != null && nrm.Any(x => x != 0.0)) g.Normals = new VArray(nrm, 3, "Vec3Array");
            if (tex >= 0)
            {
                var uv = new double[nv * 2];
                for (int i = 0; i < nv; i++) { uv[2 * i] = flt.Uvs[2 * uniq[i].pal]; uv[2 * i + 1] = flt.Uvs[2 * uniq[i].pal + 1]; }
                g.TexCoords[0] = new VArray(uv, 2, "Vec2Array");
            }
            for (int u = 0; u < units.Length; u++)
            {
                if (!hasUnit[u]) continue;   // corners of faces without a UV list for the unit get 0 0
                var uv = new double[nv * 2];
                for (int i = 0; i < nv; i++)
                    if (uniq[i].lid >= 0) { uv[2 * i] = luvs[uniq[i].lid][2 * u]; uv[2 * i + 1] = luvs[uniq[i].lid][2 * u + 1]; }
                g.TexCoords[units[u]] = new VArray(uv, 2, "Vec2Array");
            }
            if (colOrder.Any(c => c != null))
            {
                var cv = new double[colOrder.Count * 4];
                for (int i = 0; i < colOrder.Count; i++)
                {
                    var c = colOrder[i];
                    cv[4 * i] = c?.R ?? 0.8; cv[4 * i + 1] = c?.G ?? 0.8; cv[4 * i + 2] = c?.B ?? 0.8; cv[4 * i + 3] = 1.0;
                }
                if (colOrder.Count == 1) g.Colors = new VArray(cv, 4, "Vec4Array", "overall");
                else
                {
                    var pv = new double[nv * 4];
                    for (int i = 0; i < nv; i++) Array.Copy(cv, 4 * uniq[i].cid, pv, 4 * i, 4);
                    g.Colors = new VArray(pv, 4, "Vec4Array");
                }
            }
            string it = IndexType(nv);
            if (tris.Count > 0) g.Prims.Add(new Primitive("TRIANGLES", "elements", indices: tris.Select(i => inv[i]).ToArray(), indexType: it));
            if (lines.Count > 0) g.Prims.Add(new Primitive("LINES", "elements", indices: lines.Select(i => inv[i]).ToArray(), indexType: it));
            string name = tex >= 0 ? System.IO.Path.GetFileName(flt.Textures.GetValueOrDefault(tex, "")) : "untextured";
            if (sub)
            {
                name += " decals";
                g.Optimizable = false;   // subfaces lie exactly on their face: changing them makes them flicker
                g.Notes.Add("decals (OpenFlight subfaces): kept as they are");
            }
            if (bb != 0)
            {
                name += bb == 2 ? " billboard (axial)" : " billboard (point)";
                g.Optimizable = false;   // every face turns to the viewer on its own: merging or rebuilding them breaks that
                g.Notes.Add("billboard faces (OpenFlight template): kept as they are");
            }
            var ss = StateSetOf(tex >= 0 ? tex : null, mat, bucketLayers[lk], lk);
            if (glass && ss?.Transparent != true)
                ss = ss == null ? new StateSet { Transparent = true }
                                : new StateSet { Textures = ss.Textures, MaterialName = ss.MaterialName, Diffuse = ss.Diffuse, Modes = new(ss.Modes), Transparent = true, Src = ss.Src, Summary = new(ss.Summary) };
            var n = new Node(Kind.Geometry, "FLT Faces", name) { Geom = g, StateSet = ss, Src = bucketRecs[bk] };
            (n.Extra ??= new())["faces"] = items.Count;
            outl.Add(n);
        }
        return outl;
    }

    /// <summary>Layer texture coordinates of a face's corners (its vertex lists in order), per unit with a UV list.</summary>
    static Dictionary<int, double[]>? LayerUvs(FltFile.Rec f, int corners)
    {
        Dictionary<int, double[]>? r = null;
        int at = 0;
        for (int j = 0; j < f.VLists.Count; j++)
        {
            int n = f.VLists[j].Length;
            foreach (var (unit, a) in FltFile.UvList(j < f.UvLists.Count ? f.UvLists[j] : null, n, j < f.Morph.Count && f.Morph[j]))
            {
                r ??= new();
                if (!r.TryGetValue(unit, out var all)) r[unit] = all = new double[2 * corners];
                Array.Copy(a, 0, all, 2 * at, Math.Min(a.Length, all.Length - 2 * at));
            }
            at += n;
        }
        return r;
    }

    static double[]? Inverse3(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        double det = a * A + b * B + c * C;
        if (det == 0) return null;
        return new[]
        {
            A / det, -(b * i - c * h) / det, (b * f - c * e) / det,
            B / det, (a * i - c * g) / det, -(a * f - c * d) / det,
            C / det, -(a * h - b * g) / det, (a * e - b * d) / det,
        };
    }

    Node? MeshGeometry(FltFile.Rec rec)
    {
        if (rec.Lvp == null) return null;
        var (hidden, tex, color) = flt.FaceAttributes(rec.Data, 4);
        if (hidden) { HiddenFaces++; return null; }
        var (pos, uvs) = FltFile.LvpArrays(rec.Lvp, true);
        if (pos.Length == 0) return null;
        int nv = pos.Length / 3;
        if (flt.Scale != 1.0) for (int i = 0; i < pos.Length; i++) pos[i] *= flt.Scale;
        var g = new GeomData { Vertices = new VArray(pos, 3, "Vec3Array") };
        if (uvs.TryGetValue(0, out var uv) && tex >= 0) g.TexCoords[0] = new VArray(uv, 2, "Vec2Array");
        var (layers, lk) = LayersOf(rec);
        foreach (var l in layers) if (uvs.TryGetValue(l.unit, out var luv)) g.TexCoords[l.unit] = new VArray(luv, 2, "Vec2Array");
        foreach (var p in rec.Prims)
        {
            var (ptype, idx) = FltFile.MeshPrimitive(p);
            if (idx == null) continue;
            string? mode = ptype switch { 1 => "TRIANGLE_STRIP", 2 => "TRIANGLE_FAN", 3 => "QUAD_STRIP", 4 => "POLYGON", _ => null };
            if (mode != null) g.Prims.Add(new Primitive(mode, "elements", indices: idx, indexType: IndexType(nv)));
        }
        if (color != null) g.Colors = new VArray(new[] { color[0], color[1], color[2], 1.0 }, 4, "Vec4Array", "overall");
        flt.Represented.Add(rec);
        return new Node(Kind.Geometry, "FLT Mesh", rec.Name ?? "") { Geom = g, StateSet = StateSetOf(tex >= 0 ? tex : null, null, layers, lk), Src = rec };
    }

    /// <summary>FLT matrix records can sit on any node: groups become transforms, other nodes get a transform parent.</summary>
    static Node WithTransform(Node n)
    {
        if (n.Matrix == null || n.Kind == Kind.Transform || n.Kind == Kind.Geometry) return n;
        if (n.Kind == Kind.Group) { n.Kind = Kind.Transform; return n; }
        var wrap = new Node(Kind.Transform, "FLT Matrix", n.Name) { Matrix = n.Matrix };
        n.Matrix = null;
        wrap.Children.Add(n);
        return wrap;
    }

    static string IndexType(int nverts) => nverts < 65536 ? "DrawElementsUShort" : "DrawElementsUInt";
}
