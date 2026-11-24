using System.Globalization;

namespace SceneKit;

/// <summary>Build the scene model from an OSG text document (.osgt serializer format, old osgt layouts
/// and legacy .osg).</summary>
public sealed class OsgReader
{
    static readonly Dictionary<string, string> Bindings = new()
    {
        ["BIND_OFF"] = "off", ["BIND_OVERALL"] = "overall", ["BIND_PER_PRIMITIVE_SET"] = "primitive_set",
        ["BIND_PER_PRIMITIVE"] = "primitive", ["BIND_PER_VERTEX"] = "vertex", ["BIND_UNDEFINED"] = "undefined",
        ["OFF"] = "off", ["OVERALL"] = "overall", ["PER_PRIMITIVE_SET"] = "primitive_set", ["PER_PRIMITIVE"] = "primitive",
        ["PER_VERTEX"] = "vertex",
    };
    static readonly Dictionary<string, string> OldArrayTypes = new()
    { ["Vec2fArray"] = "Vec2Array", ["Vec3fArray"] = "Vec3Array", ["Vec4fArray"] = "Vec4Array", ["UByte4Array"] = "Vec4ubArray", ["Vec4ubArray"] = "Vec4ubArray" };
    static readonly HashSet<string> TransformClasses = new()
    { "MatrixTransform", "PositionAttitudeTransform", "AutoTransform", "Transform", "Camera", "DOFTransform", "CameraView" };
    static readonly HashSet<string> NonChildLegacy = new()
    { "StateSet", "Matrix", "UserData", "UserDataContainer", "Descriptions", "ValueList", "RangeList", "FileNameList", "Shape", "InitialBound" };

    readonly Document doc;
    readonly Scene scene;
    readonly bool osgt;
    readonly int version;
    readonly Dictionary<Obj, Node> nodes = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Obj, StateSet> statesets = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Obj, Texture> textures = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Obj, VArray> arrays = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, (string type, double[] data)> arrayIds = new();
    readonly Dictionary<string, VArray> legacyUids = new();
    readonly string baseDir;
    public Action<string>? Progress;

    public OsgReader(Document doc, Scene scene)
    {
        this.doc = doc; this.scene = scene;
        osgt = doc.Dialect == "osgt";
        version = osgt ? doc.Version : 0;
        baseDir = string.IsNullOrEmpty(scene.Path) ? "." : (System.IO.Path.GetDirectoryName(scene.Path) ?? ".");
    }

    public static string Short(string cls) { int i = cls.LastIndexOf("::", StringComparison.Ordinal); return i < 0 ? cls : cls[(i + 2)..]; }

    public static double ToFloat(string? t, double dflt = 0.0) =>
        t != null && double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
        : t != null && PyFloat(t.Trim(), out var w) ? w : dflt;

    static bool PyFloat(string t, out double v)
    {
        var l = t.ToLowerInvariant();
        v = 0;
        if (l is "nan" or "+nan" or "-nan") { v = double.NaN; return true; }
        if (l is "inf" or "+inf" or "infinity" or "+infinity") { v = double.PositiveInfinity; return true; }
        if (l is "-inf" or "-infinity") { v = double.NegativeInfinity; return true; }
        return false;
    }

    public static long ToInt(string? t, long dflt = 0)
    {
        if (t == null) return dflt;
        var s = t.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : dflt;
        if (s.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[3..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h2) ? -h2 : dflt;
        double f = ToFloat(s, double.NaN);
        if (double.IsNaN(f) || double.IsInfinity(f)) return dflt;
        return (long)Math.Truncate(f);
    }

    /// <summary>Numbers of a statement: from its block (Num) or its tokens.</summary>
    public static double[] Numbers(Stmt? st)
    {
        if (st == null) return Array.Empty<double>();
        if (st.Block is Num n) return n.AsDoubles();
        if (st.Block is List<Stmt> l)
        {
            var vals = new List<double>();
            foreach (var s in l) foreach (var t in s.Toks) vals.Add(ToFloat(t, double.NaN));
            return vals.ToArray();
        }
        return Array.Empty<double>();
    }

    /// <summary>Integer data (indices, lengths); the document keeps the same int array (no second copy).</summary>
    public static int[] IntNumbers(Stmt? st)
    {
        if (st?.Block is Num n)
        {
            if (n.I != null) return n.I;
            var d = n.D!;
            var ints = new int[d.Length];
            bool exact = true;
            for (int i = 0; i < d.Length; i++)
            {
                double v = d[i];
                ints[i] = double.IsFinite(v) && Math.Abs(v) < 2147483648.0 ? (int)v : (v > 0 ? int.MaxValue : int.MinValue);
                if (ints[i] != v) exact = false;
            }
            if (exact) { n.I = ints; n.D = null; }
            return ints;
        }
        return Numbers(st).Select(v => double.IsFinite(v) ? (int)Math.Clamp(Math.Truncate(v), int.MinValue, int.MaxValue) : 0).ToArray();
    }

    static Obj? FirstObj(Stmt? st)
    {
        if (st == null) return null;
        if (st.Block is Obj o) return o;
        if (st.Block is List<Stmt> l) foreach (var s in l) if (s.Block is Obj ob) return ob;
        return null;
    }

    static List<Obj> ObjList(Stmt? st)
    {
        var o = new List<Obj>();
        if (st?.Block is List<Stmt> l) foreach (var s in l) if (s.Block is Obj ob) o.Add(ob);
        return o;
    }

    void Warn(string msg) { if (!scene.Warnings.Contains(msg)) scene.Warnings.Add(msg); }

    // ------------------------------------------------------------------ nodes
    public Node Read()
    {
        var roots = doc.RootObjects();
        if (roots.Count == 0) throw new InvalidDataException("no scene objects found in the file");
        if (roots.Count == 1) return ReadNode(roots[0]);
        var top = new Node(Kind.Group, "(file)", "") { VirtualRoot = true };
        foreach (var o in roots) top.Children.Add(ReadNode(o));
        return top;
    }

    /// <summary>Iterative (deep hierarchies do not overflow the stack).</summary>
    Node ReadNode(Obj rootObj)
    {
        var root = MakeNode(rootObj, out var rootKids);
        var stack = new Stack<(Node n, List<Obj> kids, int i)>();
        stack.Push((root, rootKids, 0));
        while (stack.Count > 0)
        {
            var (n, kids, i) = stack.Pop();
            if (i >= kids.Count) continue;
            stack.Push((n, kids, i + 1));
            var o = kids[i];
            if (nodes.TryGetValue(o, out var existing)) { n.Children.Add(existing); continue; }
            var c = MakeNode(o, out var ck);
            n.Children.Add(c);
            if (c.Geom != null && n.Cls.EndsWith("Billboard", StringComparison.Ordinal))
            {
                // drawable i sits at PositionList[i] and turns to the viewer: merging or flattening breaks that
                c.Geom.Optimizable = false;
                c.Notes.Add("drawable of an osg::Billboard: kept as it is");
            }
            stack.Push((c, ck, 0));
        }
        return root;
    }

    Node MakeNode(Obj obj, out List<Obj> childObjs)
    {
        if (nodes.TryGetValue(obj, out var known)) { childObjs = new List<Obj>(); return known; }
        if (Progress != null && nodes.Count % 20000 == 19999) Progress($"building the scene ... {nodes.Count + 1:N0} nodes");
        string cls = obj.Cls;
        string s = Short(cls);
        string kind = KindOf(obj, s);
        var n = new Node(kind, cls);
        nodes[obj] = n;
        n.Src = obj;
        n.Name = obj.Value(osgt ? "Name" : "name", "") ?? "";
        var mask = obj.Value(osgt ? "NodeMask" : "nodeMask");
        if (mask != null) n.NodeMask = ToInt(mask, 0xFFFFFFFFL);
        n.StateSet = StateSetOf(obj);
        if (kind == Kind.Geometry)
        {
            try { n.Geom = Geometry(obj); }
            catch (Exception exc)
            {
                n.Geom = new GeomData { Optimizable = false };
                n.Notes.Add($"geometry could not be read: {exc.Message}");
                Warn($"{cls} '{n.Name}': geometry could not be read ({exc.Message})");
            }
            if (cls != "osg::Geometry" && s != "Geometry")
            {
                n.Geom!.Optimizable = false;
                n.Notes.Add($"{cls}: geometry is shown but not modified by the optimizer");
            }
        }
        if (s == "Billboard")
        {
            // drawables are relative to their position; one shared position becomes the node's Center
            var pos = Numbers(obj.Get(osgt ? "PositionList" : "Positions")).Where(x => !double.IsNaN(x)).ToArray();
            if (pos.Length >= 3)
            {
                bool same = true;
                for (int i = 3; i + 2 < pos.Length; i += 3) same &= pos[i] == pos[0] && pos[i + 1] == pos[1] && pos[i + 2] == pos[2];
                if (same) n.Center = pos.Take(3).ToArray();
                else n.Notes.Add("billboard drawables at different positions: the preview draws them unmoved");
            }
        }
        if (s == "MatrixTransform")
        {
            var vals = Numbers(obj.Get("Matrix"));
            if (vals.Length == 16) n.Matrix = Mat.FromOsg(vals);
        }
        else if (s == "PositionAttitudeTransform") n.Matrix = Pat(obj);
        else if (kind == Kind.Transform) n.Notes.Add($"{s}: transform is computed at runtime (treated as identity)");
        if (kind is Kind.Lod or Kind.PagedLod) Lod(obj, n);
        if (kind == Kind.Switch)
        {
            var vl = obj.Get("ValueList");
            if (vl != null)
            {
                var vals = new List<bool>();
                if (vl.Block is Num num) for (int i = 0; i < num.Length; i++) vals.Add(num[i] != 0);
                else if (vl.Block is List<Stmt> l) { foreach (var st in l) foreach (var t in st.Toks) vals.Add(IsTrue(t)); }
                else if (vl.Block is Obj vo) { foreach (var st in vo.Stmts) foreach (var t in st.Toks) vals.Add(IsTrue(t)); }
                n.SwitchValues = vals;
            }
        }
        if (kind == Kind.Proxy) n.Files = Strings(obj.Get(osgt ? "FileNames" : "FileNameList"));
        childObjs = ChildObjs(obj, kind);
        return n;
    }

    static bool IsTrue(string t) { var u = t.ToUpperInvariant(); return u is "TRUE" or "1" or "ON"; }

    string KindOf(Obj obj, string s)
    {
        if (s == "Geometry" || obj.Get("PrimitiveSetList") != null || obj.Get("VertexArray") != null
            || obj.Get("VertexData") != null || obj.Get("PrimitiveSets") != null || obj.Get("Primitives") != null) return Kind.Geometry;
        if (s == "PagedLOD") return Kind.PagedLod;
        if (s == "LOD") return Kind.Lod;
        if (s is "Switch" or "MultiSwitch") return Kind.Switch;
        if (s == "ProxyNode") return Kind.Proxy;
        if (TransformClasses.Contains(s) || s.EndsWith("Transform", StringComparison.Ordinal)) return Kind.Transform;
        if (s is "Geode" or "Billboard") return Kind.Geode;
        if (s == "Group" || obj.Get("Children") != null || obj.Get("num_children") != null) return Kind.Group;
        if (obj.Get("Drawables") != null || obj.Get("num_drawables") != null) return Kind.Geode;
        return Kind.Other;
    }

    List<Obj> ChildObjs(Obj obj, string kind)
    {
        if (osgt)
        {
            var o = ObjList(obj.Get("Children"));
            o.AddRange(ObjList(obj.Get("Drawables")));
            return o;
        }
        var outl = new List<Obj>();
        if (kind == Kind.Geometry) return outl;
        foreach (var st in obj.Stmts)
        {
            if (st.Block is Obj b && !NonChildLegacy.Contains(b.Cls) && !b.Cls.EndsWith("Callback", StringComparison.Ordinal))
            {
                if (kind == Kind.Other && !(Truthy(b.Get("num_children")) || Truthy(b.Get("num_drawables")) || Truthy(b.Get("PrimitiveSets")) || Truthy(b.Get("Primitives"))))
                    continue;
                outl.Add(b);
            }
        }
        return outl;
    }

    static bool Truthy(Stmt? s) => s != null;   // a Stmt object is always truthy in Python

    double[] Pat(Obj obj)
    {
        string K(string a, string b) => osgt ? a : b;
        var pos = NumbersTokens(obj.Get(K("Position", "position")), 3, new double[] { 0, 0, 0 });
        var att = NumbersTokens(obj.Get(K("Attitude", "attitude")), 4, new double[] { 0, 0, 0, 1 });
        var scl = NumbersTokens(obj.Get(K("Scale", "scale")), 3, new double[] { 1, 1, 1 });
        var piv = NumbersTokens(obj.Get(K("PivotPoint", "pivotPoint")), 3, new double[] { 0, 0, 0 });
        return Mat.Pat(pos, att, scl, piv);
    }

    static double[] NumbersTokens(Stmt? st, int n, double[] dflt)
    {
        if (st == null || st.Toks.Length < n + 1) return dflt;
        return st.Toks.Skip(1).Take(n).Select(x => ToFloat(x)).ToArray();
    }

    void Lod(Obj obj, Node n)
    {
        var vals = Numbers(obj.Get("RangeList"));
        n.Ranges = new List<(double, double)>();
        for (int i = 0; i + 1 < vals.Length; i += 2) n.Ranges.Add((vals[i], vals[i + 1]));
        var rm = obj.Value("RangeMode");
        if (!string.IsNullOrEmpty(rm)) n.RangeMode = rm;
        if (osgt)
        {
            var uc = obj.Get("UserCenter");
            if (uc != null && uc.Toks.Length >= 5) n.Center = uc.Toks.Skip(1).Take(4).Select(t => ToFloat(t)).ToArray();
            if (n.Kind == Kind.PagedLod) n.Files = Strings(obj.Get("RangeDataList"));
        }
        else
        {
            var c = obj.Get("Center");
            var r = obj.Value("Radius");
            if (c != null && c.Toks.Length >= 4)
                n.Center = c.Toks.Skip(1).Take(3).Select(t => ToFloat(t)).Append(ToFloat(r, -1)).ToArray();
            if (n.Kind == Kind.PagedLod) n.Files = Strings(obj.Get("FileNameList"));
        }
    }

    static List<string> Strings(Stmt? st)
    {
        var o = new List<string>();
        if (st?.Block is List<Stmt> l) foreach (var s in l) o.AddRange(s.Toks);
        return o;
    }

    // ------------------------------------------------------------------ geometry
    GeomData Geometry(Obj obj)
    {
        var g = new GeomData();
        if (osgt)
        {
            PrimsOsgt(obj.Get("PrimitiveSetList"), g);
            if (obj.Get("VertexData") != null || obj.Get("NormalData") != null) OldArrays(obj, g);
            else
            {
                g.Vertices = ArrayObj(FirstObj(obj.Get("VertexArray")));
                g.Normals = ArrayObj(FirstObj(obj.Get("NormalArray")));
                g.Colors = ArrayObj(FirstObj(obj.Get("ColorArray")));
                foreach (var name in new[] { "SecondaryColorArray", "FogCoordArray" })
                {
                    var a = ArrayObj(FirstObj(obj.Get(name)));
                    if (a != null) g.Extra[name] = a;
                }
                var tl = ListEntries(obj.Get("TexCoordArrayList"));
                for (int u = 0; u < tl.Count; u++) { var a = ArrayObj(tl[u]); if (a != null) g.TexCoords[u] = a; }
                var al = ListEntries(obj.Get("VertexAttribArrayList"));
                for (int i = 0; i < al.Count; i++) { var a = ArrayObj(al[i]); if (a != null) g.Extra[$"VertexAttrib{i}"] = a; }
            }
        }
        else LegacyGeometry(obj, g);
        FixBindings(g);
        return g;
    }

    static List<Obj?> ListEntries(Stmt? st)
    {
        var o = new List<Obj?>();
        if (st?.Block is List<Stmt> l) foreach (var s in l) o.Add(s.Block as Obj);
        return o;
    }

    VArray? ArrayObj(Obj? ao)
    {
        if (ao == null) return null;
        if (arrays.TryGetValue(ao, out var known)) return known;
        string t = Short(ao.Cls);
        var data = Numbers(ao.Get("vector"));
        string binding = Bindings.GetValueOrDefault(ao.Value("Binding", "BIND_UNDEFINED") ?? "BIND_UNDEFINED", "undefined");
        var a = VArray.FromFlat(data, t, binding, ToInt(ao.Value("Normalize", "0")) != 0, ao);
        arrays[ao] = a;
        return a;
    }

    void PrimsOsgt(Stmt? st, GeomData g)
    {
        if (st?.Block is not List<Stmt> l) return;
        foreach (var s in l)
        {
            if (s.Block is Obj o)
            {
                string sh = Short(o.Cls);
                string mode = (o.Value("Mode", "POINTS") ?? "POINTS").Replace("GL_", "");
                int inst = (int)ToInt(o.Value("NumInstances", "0"));
                if (sh == "DrawArrays")
                    g.Prims.Add(new Primitive(mode, "arrays", (int)ToInt(o.Value("First", "0")), (int)ToInt(o.Value("Count", "0")), instances: inst));
                else if (sh == "DrawArrayLengths")
                    g.Prims.Add(new Primitive(mode, "lengths", (int)ToInt(o.Value("First", "0")), lengths: IntNumbers(o.Get("vector")), instances: inst));
                else if (ArrayTypes.IndexTypes.ContainsKey(sh))
                    g.Prims.Add(new Primitive(mode, "elements", indices: IntNumbers(o.Get("vector")), indexType: sh, instances: inst));
                else
                {
                    g.Optimizable = false;
                    g.Notes.Add($"unsupported primitive set {o.Cls}");
                }
            }
            else if (s.Toks.Length > 0) OldPrim(s, g);
        }
    }

    /// <summary>``DrawElementsUShort GL_TRIANGLES [instances] 36 { ... }`` and friends.</summary>
    void OldPrim(Stmt st, GeomData g)
    {
        var t = st.Toks;
        string s = t[0];
        if (t.Length < 2) return;
        if (s == "DrawArraysLength") s = "DrawArrayLengths";  // old osgt spelling
        string mode = t[1].Replace("GL_", "");
        var nums = t.Skip(2).Select(x => (int)ToInt(x)).ToArray();
        if (s == "DrawArrays" && nums.Length >= 2)
            g.Prims.Add(new Primitive(mode, "arrays", nums[^2], nums[^1], instances: nums.Length > 2 ? nums[0] : 0));
        else if (s == "DrawArrayLengths" && nums.Length >= 2)
            g.Prims.Add(new Primitive(mode, "lengths", nums[^2], lengths: IntNumbers(st), instances: nums.Length > 2 ? nums[0] : 0));
        else if (ArrayTypes.IndexTypes.ContainsKey(s))
            g.Prims.Add(new Primitive(mode, "elements", indices: IntNumbers(st), indexType: s, instances: nums.Length > 1 ? nums[0] : 0));
        else
        {
            g.Optimizable = false;
            g.Notes.Add($"unsupported primitive set {s}");
        }
    }

    /// <summary>``VertexData { Array TRUE ArrayID 1 Vec3fArray 8 {...} Indices FALSE Binding ... }``.</summary>
    void OldArrays(Obj obj, GeomData g)
    {
        VArray? One(List<Stmt> block)
        {
            (string type, double[] data)? arr = null, idx = null;
            string binding = "undefined";
            bool norm = false;
            foreach (var st in block)
            {
                string k = st.Key;
                if (k == "Array") arr = OldArray(st);
                else if (k == "Indices") idx = OldArray(st);
                else if (k == "Binding" && st.Toks.Length > 1) binding = Bindings.GetValueOrDefault(st.Toks[1], "undefined");
                else if (k == "Normalize" && st.Toks.Length > 1) norm = ToInt(st.Toks[1]) != 0;
            }
            if (arr == null) return null;
            var a = VArray.FromFlat(arr.Value.data, arr.Value.type, binding, norm);
            if (idx != null)
            {
                a = Expand(a, idx.Value.data.Select(v => (int)v).ToArray());
                g.Notes.Add("deprecated index arrays were expanded");
            }
            return a;
        }

        foreach (var (key, attr) in new[] { ("VertexData", 0), ("NormalData", 1), ("ColorData", 2) })
        {
            var st = obj.Get(key);
            if (st?.Block is List<Stmt> l)
            {
                var a = One(l);
                if (attr == 0) g.Vertices = a; else if (attr == 1) g.Normals = a; else g.Colors = a;
            }
        }
        foreach (var key in new[] { "SecondaryColorData", "FogCoordData" })
        {
            var st = obj.Get(key);
            if (st?.Block is List<Stmt> l) { var a = One(l); if (a != null) g.Extra[key.Replace("Data", "Array")] = a; }
        }
        foreach (var (key, prefix) in new[] { ("TexCoordData", (string?)null), ("VertexAttribData", "VertexAttrib") })
        {
            var st = obj.Get(key);
            if (st?.Block is not List<Stmt> l) continue;
            int unit = 0;
            foreach (var d in l)
            {
                if (d.Key == "Data" && d.Block is List<Stmt> dl)
                {
                    var a = One(dl);
                    if (a != null) { if (prefix != null) g.Extra[$"{prefix}{unit}"] = a; else g.TexCoords[unit] = a; }
                    unit++;
                }
            }
        }
        g.Notes.Add($"old osgt geometry layout (file version {version})");
    }

    static VArray Expand(VArray a, int[] idx)
    {
        int c = a.Comps, cnt = a.Count;
        var o = new double[idx.Length * c];
        for (int i = 0; i < idx.Length; i++)
        {
            int j = Math.Clamp(idx[i], 0, Math.Max(cnt - 1, 0));
            if (cnt == 0) continue;
            Array.Copy(a.Data, j * c, o, i * c, c);
        }
        return a.CopyWith(cnt == 0 ? a.Data : o);
    }

    (string type, double[] data)? OldArray(Stmt st)
    {
        var t = st.Toks;
        if (t.Length < 2 || t[1].ToUpperInvariant() != "TRUE") return null;
        string? aid = t.Length > 3 && t[2] == "ArrayID" ? t[3] : null;
        if (t.Length <= 4 && aid != null) return arrayIds.TryGetValue(aid, out var known) ? known : null;
        string tname = aid != null ? t[4] : t[2];
        tname = OldArrayTypes.GetValueOrDefault(tname, tname);
        var res = (tname, Numbers(st));
        if (aid != null) arrayIds[aid] = res;
        return res;
    }

    void LegacyGeometry(Obj obj, GeomData g)
    {
        var bindings = new Dictionary<string, string>();
        var indices = new Dictionary<string, int[]>();
        foreach (var st in obj.Stmts)
        {
            string k = st.Key;
            var t = st.Toks;
            if ((k == "PrimitiveSets" || k == "Primitives") && st.Block is List<Stmt> pl) { foreach (var p in pl) OldPrim(p, g); }
            else if (k is "NormalBinding" or "ColorBinding" or "SecondaryColorBinding" or "FogCoordBinding" && t.Length > 1)
                bindings[k.Replace("Binding", "")] = Bindings.GetValueOrDefault(t[1], "undefined");
            else if (k is "VertexArray" or "NormalArray" or "ColorArray" or "SecondaryColorArray" or "FogCoordArray")
            {
                var a = LegacyArray(t[1..], st);
                if (a != null)
                {
                    if (k == "VertexArray") g.Vertices = a;
                    else if (k == "NormalArray") g.Normals = a;
                    else if (k == "ColorArray") g.Colors = a;
                    else g.Extra[k] = a;
                    if (k == "VertexArray") a.Binding = "vertex";
                }
            }
            else if (k == "TexCoordArray" && t.Length > 2)
            {
                // 'TexCoordArray 0 [UniqueID id] Vec2Array 24 {...}' or 'TexCoordArray Use id' (unit 0)
                var (unit, rest) = t[1] == "Use" ? (0, t[1..]) : ((int)ToInt(t[1]), t[2..]);
                var a = LegacyArray(rest, st);
                if (a != null) g.TexCoords[unit] = a;
            }
            else if (k == "VertexAttribArray" && t.Length > 2)
            {
                var a = LegacyArray(t[2..], st);
                if (a != null) g.Extra[$"VertexAttrib{ToInt(t[1])}"] = a;
            }
            else if (k.EndsWith("Indices", StringComparison.Ordinal) && st.Block is Num num)
                indices[k] = num.AsDoubles().Select(v => (int)v).ToArray();
        }
        if (g.Normals != null) g.Normals.Binding = bindings.GetValueOrDefault("Normal", "vertex");
        if (g.Colors != null) g.Colors.Binding = bindings.GetValueOrDefault("Color", "vertex");
        foreach (var name in new[] { "SecondaryColor", "FogCoord" })
            if (g.Extra.TryGetValue(name + "Array", out var a)) a.Binding = bindings.GetValueOrDefault(name, "vertex");
        if (indices.Count > 0)
        {
            VArray? Exp(VArray? a, string key) => a != null && indices.TryGetValue(key, out var ix) && a.Count > 0 ? Expand(a, ix) : a;
            g.Vertices = Exp(g.Vertices, "VertexIndices");
            g.Normals = Exp(g.Normals, "NormalIndices");
            g.Colors = Exp(g.Colors, "ColorIndices");
            foreach (var u in g.TexCoords.Keys.ToList()) g.TexCoords[u] = Exp(g.TexCoords[u], "TexCoordIndices")!;
            g.Notes.Add("deprecated index arrays were expanded");
        }
    }

    /// <summary>``Vec3Array 12`` + block, very old ``12`` + block, ``UniqueID id Vec3Array 12`` + block
    /// (shared later with ``Use id``).</summary>
    VArray? LegacyArray(string[] t, Stmt st)
    {
        if (t.Length >= 2 && t[0] == "Use")
            return legacyUids.TryGetValue(t[1], out var a0) ? new VArray(a0.Data, a0.Comps, a0.Type, a0.Binding, a0.Normalize) : null;
        string? uid = null;
        if (t.Length >= 3 && t[0] == "UniqueID") { uid = t[1]; t = t[2..]; }
        if (st.Block is not Num num) return null;
        string tname = t.Length > 0 && t[0].Length > 0 && t[0].All(char.IsDigit) ? "Vec3Array"
            : (t.Length > 0 ? OldArrayTypes.GetValueOrDefault(t[0], t[0]) : "Vec3Array");
        var a = VArray.FromFlat(num.AsDoubles(), tname, "vertex");
        if (num.D != null && a.Data.Length == num.D.Length) a.Data = num.D;   // share with the document
        if (uid != null) legacyUids[uid] = a;
        return a;
    }

    static void FixBindings(GeomData g)
    {
        int nv = g.NumVertices;
        foreach (var (_, a) in g.Arrays())
            if (a.Binding == "undefined") a.Binding = a.Count == nv ? "vertex" : (a.Count == 1 ? "overall" : "vertex");
        foreach (var (_, a) in g.Arrays())
        {
            if (a.Binding is "primitive" or "primitive_set")
            {
                g.Optimizable = false;
                g.Notes.Add($"{a.Binding.Replace('_', ' ')} binding (deprecated) - geometry is not re-indexed");
                break;
            }
            if (a.Binding == "vertex" && a.Count != nv && !ReferenceEquals(a, g.Vertices))
            {
                g.Optimizable = false;
                g.Notes.Add("attribute array length differs from the vertex count");
                break;
            }
        }
    }

    // ------------------------------------------------------------------ state
    StateSet? StateSetOf(Obj obj)
    {
        Obj? so = null;
        if (osgt) so = FirstObj(obj.Get("StateSet"));
        else foreach (var st in obj.Stmts) if (st.Block is Obj b && b.Cls == "StateSet") { so = b; break; }
        if (so == null) return null;
        if (statesets.TryGetValue(so, out var known)) return known;
        var ss = new StateSet { Src = so };
        statesets[so] = ss;
        try
        {
            if (osgt) StateSetOsgt(so, ss); else StateSetLegacy(so, ss);
        }
        catch (Exception exc) { Warn($"state set could not be fully read: {exc.Message}"); }
        return ss;
    }

    void StateSetOsgt(Obj so, StateSet ss)
    {
        var ml = so.Get("ModeList");
        if (ml?.Block is List<Stmt> l) foreach (var st in l) if (st.Toks.Length >= 2) ss.Modes[st.Toks[0]] = st.Toks[1];
        foreach (var ao in ObjList(so.Get("AttributeList"))) Attribute(ao, ss);
        var tal = so.Get("TextureAttributeList");
        if (tal?.Block is List<Stmt> tl)
        {
            int unit = 0;
            foreach (var d in tl)
            {
                if (d.Key == "Data")
                {
                    foreach (var ao in ObjList(d)) Attribute(ao, ss, unit);
                    unit++;
                }
            }
        }
        var hint = so.Value("RenderingHint");
        if (hint != null && ToInt(hint) == 2) ss.Transparent = true;
        if (ss.Modes.GetValueOrDefault("GL_BLEND", "").StartsWith("ON", StringComparison.Ordinal)) ss.Transparent = true;
    }

    void StateSetLegacy(Obj so, StateSet ss)
    {
        foreach (var st in so.Stmts)
        {
            string k = st.Key;
            if (st.Block is Obj b) Attribute(b, ss);
            else if (k == "textureUnit" && st.Block is List<Stmt> l)
            {
                int unit = st.Toks.Length > 1 ? (int)ToInt(st.Toks[1]) : 0;
                foreach (var sub in l) if (sub.Block is Obj sb) Attribute(sb, ss, unit);
            }
            else if (k.StartsWith("GL_", StringComparison.Ordinal) && st.Toks.Length >= 2) ss.Modes[k] = st.Toks[1];
            else if (k == "rendering_hint" && st.Toks.Length > 1 && st.Toks[1] == "TRANSPARENT_BIN") ss.Transparent = true;
        }
        if (ss.Modes.GetValueOrDefault("GL_BLEND", "").StartsWith("ON", StringComparison.Ordinal)) ss.Transparent = true;
    }

    void Attribute(Obj ao, StateSet ss, int? unit = null)
    {
        string s = Short(ao.Cls);
        if (s == "Material")
        {
            ss.MaterialName = ao.Value(osgt ? "Name" : "name", "") ?? "";
            if (osgt)
            {
                var d = ao.Get("Diffuse");
                if (d != null && d.Toks.Length >= 7) ss.Diffuse = d.Toks.Skip(3).Take(4).Select(x => ToFloat(x)).ToArray();
            }
            else
            {
                var d = ao.Get("diffuseColor");
                if (d != null && d.Toks.Length >= 5) ss.Diffuse = d.Toks.Skip(1).Take(4).Select(x => ToFloat(x)).ToArray();
            }
            if (ss.Diffuse != null && ss.Diffuse[3] < 0.999) ss.Transparent = true;
            ss.Summary.Add("Material" + (ss.MaterialName.Length > 0 ? $" '{ss.MaterialName}'" : ""));
        }
        else if (s.StartsWith("Texture", StringComparison.Ordinal) && s is not ("TexEnv" or "TexGen" or "TexMat" or "TexEnvCombine"))
        {
            var tex = TextureOf(ao);
            tex.Unit = unit ?? 0;
            ss.Textures.Add(tex);
            ss.Summary.Add($"{s}[{tex.Unit}] {tex.Label}");
        }
        else ss.Summary.Add(s);
    }

    Texture TextureOf(Obj to)
    {
        if (textures.TryGetValue(to, out var known)) return known;
        var tex = new Texture { Src = to };
        textures[to] = tex;
        TextureSampling.Read(to, osgt, tex);
        if (osgt)
        {
            Obj? img = null;
            foreach (var st in to.Stmts)
            {
                if (st.Block is Obj b && (b.Style == ObjStyle.Image || Short(b.Cls) == "Image")) { img = b; break; }
                // older files (e.g. version 78): "Image TRUE { FileName ... }" as a plain block
                if (st.Key == "Image" && st.Block is List<Stmt> ls && tex.File.Length == 0)
                {
                    var fn = ls.FirstOrDefault(x => x.Key == "FileName");
                    if (fn != null && fn.Toks.Length > 1) tex.File = fn.Toks[1];
                }
            }
            if (img != null)
            {
                tex.ImageSrc = img;
                tex.File = img.Value("FileName", "") ?? "";
                var size = img.Get("Size");
                if (size != null && size.Toks.Length >= 3) { tex.Width = (int)ToInt(size.Toks[1]); tex.Height = (int)ToInt(size.Toks[2]); }
                var data = img.Get("Data");
                if (data != null)
                {
                    tex.Embedded = true;
                    tex.FileBytes = EmbeddedSize(data);
                    long pf = ToInt(img.Value("PixelFormat", "0"));
                    tex.Channels = pf switch { 6407 => 3, 6408 => 4, 6409 => 1, 6410 => 2, 32992 => 3, 32993 => 4, _ => 4 };
                    tex.Fmt = "inline";
                }
            }
        }
        else tex.File = to.Value("file", "") ?? "";
        if (tex.File.Length > 0 && !tex.Embedded) Images.FillTexture(tex, baseDir);
        else if (tex.Embedded) tex.Exists = true;
        return tex;
    }

    static long EmbeddedSize(Stmt st)
    {
        long total = 0;
        if (st.Block is List<Stmt> l)
            foreach (var s in l) for (int i = 0; i < s.Toks.Length; i++) if (s.IsQuoted(i)) total += s.Toks[i].Length * 3 / 4;
        return total;
    }

    /// <summary>Fill <c>scene.Root</c> from <paramref name="doc"/>.</summary>
    public static Scene Read(Document doc, Scene scene, Action<string>? progress = null)
    {
        var r = new OsgReader(doc, scene) { Progress = progress };
        scene.Doc = doc;
        scene.Version = doc.Dialect == "osgt" ? doc.Version : null;
        foreach (var line in doc.Header)
            if (line.StartsWith("#Generator", StringComparison.Ordinal)) scene.Info["generator"] = line["#Generator".Length..].Trim();
        scene.Root = r.Read();
        scene.Info["statesets"] = r.statesets.Count;
        scene.Info["textures"] = r.textures.Count;
        return scene;
    }
}
