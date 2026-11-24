using System.Buffers.Binary;
using System.Text;

namespace SceneKit;

/// <summary>Write a scene read from OpenFlight back as .flt by patching the original record stream.
/// Every record the optimiser did not touch is copied byte for byte (attributes, comments, extensions, palettes);
/// geometry that changed becomes new face records with new vertex palette entries; nodes the optimiser removed
/// are left out and new nodes (LOD levels, transforms) are written as group / LOD / object records.
/// The vertex palette is rebuilt: vertices that only removed faces used are dropped.
/// Nodes the scene shares between several parents (make_instances, share_duplicates) are written once as an instance
/// definition (opcode 62) at the top of the hierarchy and referenced (opcode 61) from every place that uses them.</summary>
public sealed class FltWriter : OsgWriterBase
{
    protected override bool FltOutput => true;

    /// <summary>Set while the drawables of a generated osg::Billboard are written: its position. Their faces become
    /// axial billboard faces (template 2) at that position, quads kept as quads (each face turns about its own centre).</summary>
    double[]? billboardAt;

    const int MaxRec = 65532;   // largest record part (multiple of 4, so 32-bit values never straddle a continuation)

    readonly FltFile flt;
    readonly double scale;
    List<(int op, byte[] data, int off)> recs = null!;
    readonly Dictionary<int, Raw> rawOf = new();
    readonly Dictionary<int, FltFile.Rec> recOf = new();
    readonly Dictionary<int, int> vertexAt = new();            // palette key (offset from the palette record) -> record index
    int vpalRec = -1;
    readonly Dictionary<FltFile.Rec, (Node node, double[]? matrix)> instDefs = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<FltFile.Rec> emittedDefs = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<FltFile.Rec, Node> geomOf = new(ReferenceEqualityComparer.Instance);   // face -> the geometry node showing it
    // shared nodes written as new instance definitions: node -> instance number, in definition order (inner ones first)
    readonly Dictionary<Node, int> instNum = new(ReferenceEqualityComparer.Instance);
    readonly List<(Node node, StateSet? inherited)> newDefs = new();
    bool defsWritten;

    // output: the hierarchy goes to 'body'; vertex references are patched once the palette layout is known
    readonly MemoryStream body = new();
    readonly List<(long pos, bool isNew, int value)> fixups = new();
    readonly List<byte[]> newVerts = new();
    readonly SortedSet<int> keptOld = new();
    long paletteAt = -1;                                        // body position where the palette goes
    readonly List<byte[]> newTexRecs = new();
    readonly Dictionary<string, int> texByPath = new(StringComparer.OrdinalIgnoreCase);
    int nextTex;
    Dictionary<Texture, string> resized = new(ReferenceEqualityComparer.Instance);
    public readonly List<string> Warnings = new();

    public FltWriter(Scene scene, string outPath) : base(scene, outPath)
    {
        flt = scene.Flt ?? throw new InvalidOperationException("only scenes read from OpenFlight can be saved as .flt");
        scale = flt.Scale;
    }

    public override void Write(TextWriter sink) => throw new NotSupportedException("OpenFlight is a binary format; use Save");

    sealed class Raw
    {
        public int Index;
        public readonly List<Entry> After = new();   // records and child blocks that follow the node record
    }

    sealed class Block
    {
        public int Push, Pop = -1;
        public readonly List<Entry> Items = new();
    }

    readonly record struct Entry(int Rec, Raw? Node, Block? Blk);

    // ------------------------------------------------------------------------------ public entry

    /// <summary>Write the .flt file (atomically: a temporary file is moved over the target).</summary>
    public string Save()
    {
        var info = new FileInfo(flt.Path);
        if (!info.Exists || info.Length != flt.FileLength || info.LastWriteTimeUtc != flt.FileTime)
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(flt.Path)} changed on disk since it was loaded (or was already saved over); reload it before saving as .flt");
        recs = FltFile.ReadRecords(File.ReadAllBytes(flt.Path));
        BuildRaw();
        IndexRecs(flt.Root);
        foreach (var d in flt.InstDefs.Values) IndexRecs(d);
        CollectInstanceDefs();
        foreach (var n in scene.IterNodes())
            if (n.Kind == Kind.Geometry && n.Src is List<FltFile.Rec> fl)
                foreach (var f in fl) geomOf.TryAdd(f, n);
        var root = scene.Root ?? throw new InvalidOperationException("empty scene");
        PlanInstances(root);
        ExportBaked();
        resized = ExportResized();
        foreach (var (i, name) in flt.Textures) texByPath.TryAdd(TexKey(name, PaletteTexture(i)?.Path), i);
        nextTex = flt.Textures.Count > 0 ? flt.Textures.Keys.Max() + 1 : 0;

        EmitRecNode(root, flt.Root, rawOf[0], root.Matrix, true);

        string tmp = outPath + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                Finish(fs);
            File.Move(tmp, outPath, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
        foreach (var w in Warnings) if (!scene.Warnings.Contains(w)) scene.Warnings.Add(w);
        Publish();
        return outPath;
    }

    // ------------------------------------------------------------------------------ the original record tree

    /// <summary>Same hierarchy walk as FltFile.Build, but keeping every record in place.</summary>
    void BuildRaw()
    {
        var root = new Raw { Index = 0 };
        rawOf[0] = root;
        Raw parent = root, last = root;
        Block? cur = null;
        var stack = new Stack<(Raw, Block?)>();
        int skip = 0;
        long vpalStart = 0;
        void Anc(int r)
        {
            if (!ReferenceEquals(last, parent)) last.After.Add(new Entry(r, null, null));
            else if (cur != null) cur.Items.Add(new Entry(r, null, null));
            else parent.After.Add(new Entry(r, null, null));
        }
        for (int r = 1; r < recs.Count; r++)
        {
            var (op, _, off) = recs[r];
            if (skip > 0)
            {
                if (op is FltFile.PUSH_EXT or FltFile.PUSH_ATTR) skip++;
                else if (op is FltFile.POP_EXT or FltFile.POP_ATTR) skip--;
                Anc(r);
                continue;
            }
            if (op is FltFile.PUSH_EXT or FltFile.PUSH_ATTR) { skip = 1; Anc(r); }
            else if (op is FltFile.PUSH or FltFile.PUSH_SUB)
            {
                var b = new Block { Push = r };
                last.After.Add(new Entry(-1, null, b));
                stack.Push((parent, cur));
                parent = last;
                cur = b;
            }
            else if (op is FltFile.POP or FltFile.POP_SUB)
            {
                if (cur != null) cur.Pop = r; else Anc(r);
                last = parent;
                (parent, cur) = stack.Count > 0 ? stack.Pop() : (root, null);
            }
            else if (FltFile.NodeOps.Contains(op))
            {
                var n = new Raw { Index = r };
                rawOf[r] = n;
                if (cur != null) cur.Items.Add(new Entry(-1, n, null)); else parent.After.Add(new Entry(-1, n, null));
                last = n;
            }
            else
            {
                if (op == FltFile.VTX_PAL) { vpalStart = off; if (vpalRec < 0) vpalRec = r; }
                else if (op >= FltFile.VTX_C && op <= FltFile.VTX_CT) vertexAt.TryAdd((int)(off - vpalStart), r);
                Anc(r);
            }
        }
    }

    void IndexRecs(FltFile.Rec rec)
    {
        var stack = new Stack<FltFile.Rec>();
        stack.Push(rec);
        while (stack.Count > 0)
        {
            var r = stack.Pop();
            recOf[r.Index] = r;
            foreach (var c in r.Children) stack.Push(c);
        }
    }

    /// <summary>Instance definitions are not children in the scene: find their nodes below the instance references.</summary>
    void CollectInstanceDefs()
    {
        foreach (var n in scene.IterNodes())
            foreach (var c in n.Children)
            {
                if (c.Src is FltFile.Rec r && r.Op == FltFile.INST_DEF) instDefs.TryAdd(r, (c, c.Matrix));
                else if (Unwrap(c) is { } u && u.inner.Src is FltFile.Rec ri && ri.Op == FltFile.INST_DEF) instDefs.TryAdd(ri, (u.inner, u.matrix));
            }
    }

    /// <summary>The node inside an "FLT Matrix" wrapper (FltRead puts matrices of non-group records on a parent transform)
    /// or a replicate wrapper (copies under transforms, all sharing the node).</summary>
    static Node? Wrapped(Node n) => Unwrap(n)?.inner;

    static (Node inner, double[]? matrix)? Unwrap(Node n)
    {
        if (n.Src != null) return null;
        if (n.Cls == "FLT Matrix" && n.Children.Count == 1 && n.Children[0].Src is FltFile.Rec) return (n.Children[0], n.Matrix);
        if (n.Cls == "FLT Replicate" && n.Extra?.GetValueOrDefault("replicate_matrix") is double[] m && n.Children.Count > 0 &&
            n.Children.All(c => c.Children.Count == 1 && ReferenceEquals(c.Children[0], n.Children[0].Children[0])) &&
            n.Children[0].Children[0].Src is FltFile.Rec)
            return (n.Children[0].Children[0], m);   // the record keeps its matrix and replicate count
        return null;
    }

    // ------------------------------------------------------------------------------ record output

    void Put(byte[] data, List<(int at, bool isNew, int value)>? fix = null)
    {
        // data holds the whole record (header included); long records are split into continuation records
        long start = body.Position;
        int len = data.Length;
        var first = new byte[Math.Min(len, MaxRec)];
        Buffer.BlockCopy(data, 0, first, 0, first.Length);
        BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(2), (ushort)first.Length);
        body.Write(first);
        var hdr = new byte[4];
        for (int o = first.Length; o < len; o += MaxRec - 4)
        {
            int n = Math.Min(MaxRec - 4, len - o);
            BinaryPrimitives.WriteUInt16BigEndian(hdr, FltFile.CONT);
            BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(2), (ushort)(n + 4));
            body.Write(hdr);
            body.Write(data, o, n);
        }
        if (fix == null) return;
        foreach (var (at, isNew, value) in fix)
        {
            long pos = at < first.Length ? start + at : start + first.Length + (at - first.Length) / (MaxRec - 4) * MaxRec + 4 + (at - first.Length) % (MaxRec - 4);
            fixups.Add((pos, isNew, value));
            if (!isNew) keptOld.Add(value);
        }
    }

    static byte[] NewRec(int op, int length)
    {
        var d = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(d, (ushort)op);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2), (ushort)Math.Min(length, 0xFFFF));
        return d;
    }

    void PutPush() => Put(NewRec(FltFile.PUSH, 4));
    void PutPop() => Put(NewRec(FltFile.POP, 4));

    /// <summary>An original record; vertex lists keep pointing at their (moved) palette vertices.</summary>
    void PutOriginal(int r)
    {
        var (op, data, _) = recs[r];
        if (op is FltFile.VTX_LIST or FltFile.MORPH_LIST)
        {
            var fix = new List<(int, bool, int)>();
            for (int o = 4; o + 4 <= data.Length; o += 4)
            {
                int key = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(o));
                if (vertexAt.ContainsKey(key)) fix.Add((o, false, key));
            }
            Put(data, fix);
        }
        else Put(data);
    }

    void PutRawEntries(List<Entry> entries)
    {
        foreach (var e in entries)
        {
            if (e.Node != null) PutRaw(e.Node);
            else if (e.Blk != null) PutRawBlock(e.Blk);
            else PutOriginal(e.Rec);
        }
    }

    void PutRawBlock(Block b)
    {
        PutOriginal(b.Push);
        PutRawEntries(b.Items);
        if (b.Pop >= 0) PutOriginal(b.Pop); else PutPop();
    }

    void PutRaw(Raw n)
    {
        PutOriginal(n.Index);
        PutRawEntries(n.After);
    }

    /// <summary>A face that stays as it was. Its subfaces go with it, except those whose geometry was rewritten
    /// (they are written with the new faces).</summary>
    void PutKeptFace(Raw n)
    {
        PutOriginal(n.Index);
        foreach (var e in n.After)
        {
            if (e.Blk == null) { if (e.Node != null) PutRaw(e.Node); else PutOriginal(e.Rec); continue; }
            PutOriginal(e.Blk.Push);
            foreach (var it in e.Blk.Items)
            {
                if (it.Node == null) { PutOriginal(it.Rec); continue; }
                recOf.TryGetValue(it.Node.Index, out var c);
                if (c == null || c.Op != FltFile.FACE || !flt.Represented.Contains(c)) PutRaw(it.Node);
                else if (FaceKept(c)) PutKeptFace(it.Node);
            }
            if (e.Blk.Pop >= 0) PutOriginal(e.Blk.Pop); else PutPop();
        }
    }

    /// <summary>A face of this file whose geometry is still in the scene unchanged (so its original records are written).</summary>
    bool FaceKept(FltFile.Rec f) => flt.Represented.Contains(f) && geomOf.TryGetValue(f, out var g) && !GeometryChanged(g);

    void PutName(byte[] d, string name)
    {
        var b = Encoding.Latin1.GetBytes(name);
        Buffer.BlockCopy(b, 0, d, 4, Math.Min(b.Length, 7));
        Put(d);
        if (b.Length > 7)
        {
            var id = NewRec(FltFile.LONG_ID, (4 + b.Length + 1 + 3) / 4 * 4);
            Buffer.BlockCopy(b, 0, id, 4, b.Length);
            Put(id);
        }
    }

    void PutMatrix(double[] m)
    {
        var d = NewRec(FltFile.MATRIX, 68);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double v = m[j * 4 + i];
                if (i == 3 && j < 3) v /= scale;   // translations are in database units
                BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(4 + 4 * (i * 4 + j)), (float)v);
            }
        Put(d);
    }

    static bool SameMatrix(double[]? a, double[]? b)
    {
        if (a == null || b == null) return a == b;
        for (int i = 0; i < 16; i++) if (Math.Abs(a[i] - b[i]) > 1e-9 * Math.Max(1.0, Math.Abs(a[i]))) return false;
        return true;
    }

    // ------------------------------------------------------------------------------ scene nodes

    enum ItemKind { Raw, Stray, Scene, Block }

    readonly record struct Item(ItemKind Kind, Raw? RawNode = null, int Rec = -1, Node? Node = null, double[]? Matrix = null,
        bool HasMatrix = false, int Records = 1, int RawPos = -1, int SceneIndex = -1);

    void EmitScene(Node n, double[]? matrix, bool hasMatrix, StateSet? inherited, bool definition = false)
    {
        if (!definition && instNum.TryGetValue(n, out int num)) { PutInstanceRef(num, hasMatrix ? matrix : null, n.Name); return; }
        if (Unwrap(n) is { } u) { EmitScene(u.inner, u.matrix, true, inherited); return; }
        if (n.Kind == Kind.Geometry) { EmitGeometry(n, inherited); return; }
        if (n.Src is FltFile.Rec rec && recOf.TryGetValue(rec.Index, out var own) && ReferenceEquals(own, rec) && rawOf.TryGetValue(rec.Index, out var raw))
        {
            if (rec.Op == FltFile.INST_DEF && !emittedDefs.Add(rec)) return;
            EmitRecNode(n, rec, raw, hasMatrix ? matrix : n.Matrix, false);
            return;
        }
        EmitNew(n, hasMatrix ? matrix : n.Matrix, inherited);
    }

    /// <summary>A node that came from a record: the original record and its attributes, children from the scene.</summary>
    void EmitRecNode(Node n, FltFile.Rec rec, Raw raw, double[]? matrix, bool isRoot)
    {
        bool keepMatrix = SameMatrix(matrix, rec.Matrix);
        var kids = rec.Op is FltFile.INST_REF or FltFile.EXT_REF ? new List<Node>() : n.Children;

        // which scene child stands for which original child record
        var kidOf = new Dictionary<FltFile.Rec, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < kids.Count; i++)
        {
            var k = Wrapped(kids[i]) ?? kids[i];
            if (k.Src is FltFile.Rec kr) kidOf.TryAdd(kr, i);
            else if (k.Src is List<FltFile.Rec> faces && !GeometryChanged(k)) foreach (var f in faces) kidOf.TryAdd(f, i);
        }
        var placed = new bool[kids.Count];
        var plan = new List<(Entry entry, List<Item>? items)>();
        int rawPos = 0;
        List<Item>? main = null;
        foreach (var e in raw.After)
        {
            if (e.Blk == null) { plan.Add((e, null)); continue; }
            var items = new List<Item>();
            foreach (var it in e.Blk.Items)
            {
                if (it.Node == null) { items.Add(new Item(ItemKind.Stray, Rec: it.Rec, Records: 0)); continue; }
                int pos = rawPos++;
                recOf.TryGetValue(it.Node.Index, out var cr);
                if (cr != null && cr.Op == FltFile.INST_DEF)
                {
                    if (instDefs.TryGetValue(cr, out var def)) items.Add(new Item(ItemKind.Scene, Node: def.node, Matrix: def.matrix, HasMatrix: true, RawPos: pos));
                    else items.Add(new Item(ItemKind.Raw, it.Node, RawPos: pos));
                }
                else if (cr != null && kidOf.TryGetValue(cr, out int ki))
                {
                    if (!placed[ki]) { placed[ki] = true; items.Add(SceneItem(kids[ki], ki, pos)); }
                }
                else if (cr == null || (cr.Op is FltFile.FACE or FltFile.MESH && !flt.Represented.Contains(cr)))
                    items.Add(new Item(ItemKind.Raw, it.Node, RawPos: pos));   // hidden faces etc.: not in the scene, kept as they are
            }
            if (main == null || (main.All(x => x.Kind == ItemKind.Stray) && items.Any(x => x.Kind != ItemKind.Stray))) main = items;
            plan.Add((e, items));
        }
        var extra = new List<Item>();
        for (int i = 0; i < kids.Count; i++)
        {
            if (placed[i]) continue;
            var k = Wrapped(kids[i]) ?? kids[i];
            if (k.Src is FltFile.Rec kr && kr.Op == FltFile.INST_DEF) continue;   // written where the definition was
            extra.Add(SceneItem(kids[i], i, -1));
        }
        if (extra.Count > 0)
        {
            if (main == null) { main = new List<Item>(); plan.Add((new Entry(-1, null, new Block { Push = -1 }), main)); }
            main.AddRange(extra);
        }

        // the node record itself
        var data = (byte[])recs[raw.Index].data.Clone();
        if (rec.Op == FltFile.LOD && n.Dirty.Contains("ranges") && n.Ranges is { Count: > 0 } rg && data.Length >= 32)
        {
            BinaryPrimitives.WriteDoubleBigEndian(data.AsSpan(16), rg[0].max);
            BinaryPrimitives.WriteDoubleBigEndian(data.AsSpan(24), rg[0].min);
        }
        else if (rec.Op == FltFile.EXT_REF && n.Files.Count > 0 && data.Length >= 204)
        {
            string old = FltFile.CStr(data, 4, 204);
            string nw = FileRef(n, n.Files[0]);
            if (nw != n.Files[0])
            {
                int lt = old.IndexOf('<');
                string full = nw + (lt >= 0 ? old[lt..] : "");
                var b = Encoding.Latin1.GetBytes(full);
                if (b.Length > 199) Warnings.Add($"external reference path too long for OpenFlight (kept '{old}')");
                else { Array.Clear(data, 4, 200); Buffer.BlockCopy(b, 0, data, 4, b.Length); }
            }
        }
        else if (rec.Op == FltFile.SWITCH) data = SwitchRecord(n, rec, data, plan);
        Put(data);
        if (!keepMatrix && matrix != null) PutMatrix(matrix);

        foreach (var (e, items) in plan)
        {
            if (items == null)
            {
                int r = e.Rec;
                var op = r >= 0 ? recs[r].op : -1;
                if (e.Node != null) PutRaw(e.Node);   // a node record before any push (only directly below the header)
                else if (op == FltFile.MATRIX && !keepMatrix) { }
                else if (isRoot && op >= FltFile.VTX_C && op <= FltFile.VTX_CT) { }
                else if (isRoot && r == vpalRec) paletteAt = body.Position;
                else if (isRoot && op == FltFile.TEX_PAL) PutTexturePalette(r);
                else PutOriginal(r);
                continue;
            }
            if (isRoot && paletteAt < 0) paletteAt = body.Position;   // no palette in the file: put it before the hierarchy
            if (e.Blk!.Push >= 0) PutOriginal(e.Blk.Push); else PutPush();
            if (isRoot && !defsWritten) { defsWritten = true; PutInstanceDefs(); }   // definitions before every reference
            foreach (var it in items)
            {
                switch (it.Kind)
                {
                    case ItemKind.Stray: PutOriginal(it.Rec); break;
                    case ItemKind.Raw: PutRaw(it.RawNode!); break;
                    default: EmitScene(it.Node!, it.Matrix, it.HasMatrix, n.StateSet); break;
                }
            }
            if (e.Blk.Pop >= 0) PutOriginal(e.Blk.Pop); else PutPop();
        }
        if (isRoot && paletteAt < 0) paletteAt = body.Position;
    }

    // ------------------------------------------------------------------------------ shared nodes -> instances

    /// <summary>Find the nodes the scene reaches through more than one parent that can be written as an OpenFlight
    /// instance: not geometry (faces need their object record), no parent that is an object (geode), the same inherited
    /// state at every use, nothing below that references other files or instances of the source file, not the node of a
    /// replicate record, not an instance definition of the source file (those keep their records and numbers). The walk
    /// follows what the writer emits. Everything else stays written once per parent.</summary>
    void PlanInstances(Node root)
    {
        var uses = new Dictionary<Node, HashSet<(Node parent, int index)>>(ReferenceEqualityComparer.Instance);
        var inherits = new Dictionary<Node, HashSet<StateSet?>>(ReferenceEqualityComparer.Instance);
        var bad = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var seen = new Dictionary<Node, HashSet<StateSet?>>(ReferenceEqualityComparer.Instance);
        var order = new List<Node>();   // post order: a node after everything below it
        var orderSet = new HashSet<Node>(ReferenceEqualityComparer.Instance);

        void Walk(Node n, Node? parent, int index, StateSet? inh, bool inBillboard, int depth)
        {
            if (parent != null)
            {
                if (!uses.TryGetValue(n, out var u)) { uses[n] = u = new(UseComparer.Instance); inherits[n] = new(ReferenceEqualityComparer.Instance); }
                u.Add((parent, index));
                inherits[n].Add(inh);
                if (parent.Kind == Kind.Geode || inBillboard) bad.Add(n);
            }
            if (!seen.TryGetValue(n, out var s)) seen[n] = s = new(ReferenceEqualityComparer.Instance);
            if (!s.Add(inh) || depth > 400) return;
            if (n.Kind != Kind.Geometry)
            {
                if (Unwrap(n) is { } w)
                {
                    if (n.Cls == "FLT Replicate") bad.Add(w.inner);   // a reference would lose the replicate record
                    Walk(w.inner, n, 0, inh, inBillboard, depth + 1);
                }
                else if (n.Src is FltFile.Rec rec && recOf.TryGetValue(rec.Index, out var own) && ReferenceEquals(own, rec) && rawOf.ContainsKey(rec.Index))
                {
                    if (rec.Op is not (FltFile.INST_REF or FltFile.EXT_REF))
                        for (int i = 0; i < n.Children.Count; i++) Walk(n.Children[i], n, i, n.StateSet, inBillboard, depth + 1);
                }
                else
                {
                    var ss = n.StateSet ?? inh;
                    bool bb = inBillboard || IsBillboard(n);
                    for (int i = 0; i < n.Children.Count; i++) Walk(n.Children[i], n, i, ss, bb, depth + 1);
                }
            }
            if (orderSet.Add(n)) order.Add(n);
        }
        Walk(root, null, 0, null, false, 0);
        foreach (var (node, _) in instDefs.Values) Walk(node, null, 0, null, false, 0);

        var subtreeOk = new Dictionary<Node, bool>(ReferenceEqualityComparer.Instance);
        bool Expressible(Node n)
        {
            if (subtreeOk.TryGetValue(n, out var ok)) return ok;
            subtreeOk[n] = true;   // a cycle (should not happen) ends here
            ok = !(n.Kind is Kind.Proxy or Kind.PagedLod || n.Files.Count > 0 ||
                   n.Src is FltFile.Rec { Op: FltFile.INST_REF or FltFile.INST_DEF or FltFile.EXT_REF }) &&
                 n.Children.All(Expressible);
            return subtreeOk[n] = ok;
        }

        // new numbers follow every instance number the source file uses (definitions and references)
        int next = 0;
        foreach (var (op, data, _) in recs)
            if (op is FltFile.INST_DEF or FltFile.INST_REF && data.Length >= 8) next = Math.Max(next, FltFile.ReadUInt16(data, 6) + 1);
        foreach (var n in order)
        {
            if (ReferenceEquals(n, root) || n.Kind == Kind.Geometry || bad.Contains(n)) continue;
            if (!uses.TryGetValue(n, out var u) || u.Count < 2 || inherits[n].Count != 1) continue;
            if (n.Src is FltFile.Rec { Op: FltFile.INST_DEF } || !Expressible(n)) continue;
            if (next > short.MaxValue) { Warnings.Add("too many shared nodes for OpenFlight instance numbers: the rest are written once per place"); break; }
            instNum[n] = next++;
            newDefs.Add((n, inherits[n].First()));
        }
    }

    sealed class UseComparer : IEqualityComparer<(Node parent, int index)>
    {
        public static readonly UseComparer Instance = new();
        public bool Equals((Node parent, int index) a, (Node parent, int index) b) => ReferenceEquals(a.parent, b.parent) && a.index == b.index;
        public int GetHashCode((Node parent, int index) x) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(x.parent), x.index);
    }

    /// <summary>The new instance definitions, right after the push that opens the hierarchy below the header: each a
    /// definition record, push, the shared node, pop. Inner ones come first, so every definition precedes the references
    /// to it (OpenSceneGraph resolves a reference while reading it).</summary>
    void PutInstanceDefs()
    {
        foreach (var (n, inh) in newDefs)
        {
            var d = NewRec(FltFile.INST_DEF, 8);
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(6), (ushort)instNum[n]);
            Put(d);
            PutPush();
            EmitScene(n, null, false, inh, definition: true);
            PutPop();
        }
    }

    /// <summary>A use of a shared node. A placement of its own (an "FLT Matrix" wrapper around a record node) goes on a
    /// group around the reference: OpenSceneGraph's reader does not apply a matrix that follows an instance reference.</summary>
    void PutInstanceRef(int num, double[]? matrix, string name)
    {
        bool wrap = matrix != null && !SameMatrix(matrix, Mat.Identity());
        if (wrap)
        {
            PutName(NewRec(FltFile.GROUP, 44), name);
            PutMatrix(matrix!);
            PutPush();
        }
        var d = NewRec(FltFile.INST_REF, 8);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(6), (ushort)num);
        Put(d);
        if (wrap) PutPop();
    }


    Item SceneItem(Node k, int index, int rawPos)
    {
        var inner = Wrapped(k) ?? k;
        int records = inner.Kind == Kind.Geometry ? GeometryRecords(inner) : 1;
        return new Item(ItemKind.Scene, Node: k, Records: records, RawPos: rawPos, SceneIndex: index);
    }

    /// <summary>Switch masks follow the child records: rebuild them (one mask, the current one) when the children changed.</summary>
    byte[] SwitchRecord(Node n, FltFile.Rec rec, byte[] data, List<(Entry entry, List<Item>? items)> plan)
    {
        var items = plan.Where(p => p.items != null).SelectMany(p => p.items!).Where(i => i.Kind != ItemKind.Stray).ToList();
        int rawCount = rec.Children.Count;
        bool same = items.Count == rawCount && items.Select((it, i) => it.RawPos == i && it.Records == 1 && !GeometryChanged(it.Node)).All(x => x);
        if (same || data.Length < 28) return data;
        int cur = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)), words = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20));
        int nmasks = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(24));
        bool OldBit(int i)
        {
            if (i < 0 || cur < 0 || cur >= nmasks || i / 32 >= words) return true;
            int o = 28 + (cur * words + i / 32) * 4;
            return o + 4 > data.Length || ((BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o)) >> (i % 32)) & 1) != 0;
        }
        var bits = new List<bool>();
        foreach (var it in items)
        {
            bool on = it.SceneIndex >= 0 ? (n.SwitchValues != null && it.SceneIndex < n.SwitchValues.Count ? n.SwitchValues[it.SceneIndex] : true) : OldBit(it.RawPos);
            for (int k = 0; k < it.Records; k++) bits.Add(on);
        }
        int nw = Math.Max(1, (bits.Count + 31) / 32);
        var d = new byte[28 + 4 * nw];
        Buffer.BlockCopy(data, 0, d, 0, 28);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16), 0);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(20), nw);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(24), 1);
        for (int i = 0; i < bits.Count; i++)
            if (bits[i])
            {
                int o = 28 + 4 * (i / 32);
                BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(o), BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o)) | (1u << (i % 32)));
            }
        if (nmasks > 1) Warnings.Add($"switch '{rec.Name}': children changed, only the current mask was kept");
        return d;
    }

    /// <summary>Nodes the optimiser created: groups, transforms (group + matrix), LODs (one LOD record per level), objects.</summary>
    void EmitNew(Node n, double[]? matrix, StateSet? inherited)
    {
        var ss = n.StateSet ?? inherited;
        if (n.Kind == Kind.Lod || n.Kind == Kind.PagedLod)
        {
            PutName(NewRec(FltFile.GROUP, 44), n.Name);
            if (matrix != null) PutMatrix(matrix);
            PutPush();
            for (int i = 0; i < n.Children.Count; i++)
            {
                var (lo, hi) = n.Ranges != null && i < n.Ranges.Count ? n.Ranges[i] : (0.0, 1e30);
                var d = NewRec(FltFile.LOD, 80);
                BinaryPrimitives.WriteDoubleBigEndian(d.AsSpan(16), hi);   // switch in (far)
                BinaryPrimitives.WriteDoubleBigEndian(d.AsSpan(24), lo);   // switch out (near)
                // transition range: image generators that blend levels fade over 10 % of the switch distance
                if (hi < 1e29) BinaryPrimitives.WriteDoubleBigEndian(d.AsSpan(64), hi * 0.1);
                var c = n.Center ?? Center(n.Children[i]);
                if (c != null) for (int k = 0; k < 3; k++) BinaryPrimitives.WriteDoubleBigEndian(d.AsSpan(40 + 8 * k), c[k] / scale);
                PutName(d, $"{(n.Name.Length > 0 ? n.Name : "lod")}{i}");
                PutPush();
                EmitScene(n.Children[i], null, false, ss);
                PutPop();
            }
            PutPop();
            return;
        }
        PutName(n.Kind == Kind.Geode ? NewRec(FltFile.OBJECT, 28) : NewRec(FltFile.GROUP, 44), n.Name);
        if (matrix != null) PutMatrix(matrix);
        if (n.Children.Count == 0) return;
        PutPush();
        var outer = billboardAt;
        if (IsBillboard(n)) billboardAt = n.Center ?? new double[3];
        foreach (var c in n.Children) EmitScene(c, null, false, ss);
        billboardAt = outer;
        PutPop();
    }

    static double[]? Center(Node n)
    {
        double[]? lo = null, hi = null;
        void Walk(Node x, double[]? m, int depth)
        {
            if (depth > 200) return;
            var mm = x.Matrix == null ? m : m == null ? x.Matrix : Mul(m, x.Matrix);
            if (x.Geom?.BBox() is { } bb)
                for (int c = 0; c < 8; c++)
                {
                    double[] p = { (c & 1) == 0 ? bb.lo[0] : bb.hi[0], (c & 2) == 0 ? bb.lo[1] : bb.hi[1], bb.lo.Length > 2 ? ((c & 4) == 0 ? bb.lo[2] : bb.hi[2]) : 0 };
                    if (mm != null) p = new[] { 0, 1, 2 }.Select(r => mm[r * 4] * p[0] + mm[r * 4 + 1] * p[1] + mm[r * 4 + 2] * p[2] + mm[r * 4 + 3]).ToArray();
                    lo ??= (double[])p.Clone(); hi ??= (double[])p.Clone();
                    for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], p[k]); hi[k] = Math.Max(hi[k], p[k]); }
                }
            foreach (var c in x.Children) Walk(c, mm, depth + 1);
        }
        Walk(n, null, 0);
        return lo == null ? null : new[] { (lo[0] + hi![0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 };
    }

    static double[] Mul(double[] a, double[] b)
    {
        var r = new double[16];
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) for (int k = 0; k < 4; k++) r[i * 4 + j] += a[i * 4 + k] * b[k * 4 + j];
        return r;
    }

    // ------------------------------------------------------------------------------ geometry

    /// <summary>Geometry is rewritten unless it still is the faces / mesh of this file it was read from
    /// (sharing duplicates can hand a node the geometry of another file).</summary>
    bool GeometryChanged(Node? n) =>
        n != null && n.Kind == Kind.Geometry && (n.Dirty.Contains("geometry") || n.Geom == null ||
            !(n.Src is List<FltFile.Rec> l && l.Count > 0 && l.All(flt.Represented.Contains) ||
              n.Src is FltFile.Rec { Op: FltFile.MESH } m && flt.Represented.Contains(m)));

    int GeometryRecords(Node g)
    {
        if (!GeometryChanged(g)) return g.Src is List<FltFile.Rec> l ? l.Count : 1;
        var (t, l2, _) = g.Geom?.Counts() ?? (0, 0, 0);
        return (int)Math.Min(int.MaxValue, t + l2);
    }

    void EmitGeometry(Node g, StateSet? inherited)
    {
        if (!GeometryChanged(g))
        {
            if (g.Src is List<FltFile.Rec> faces)
            {
                foreach (var f in faces)
                {
                    // a subface is written inside the face it lies on: the original one, or the new faces replacing it
                    if (flt.SubfaceOf.TryGetValue(f, out var owner) && (FaceKept(owner) || geomOf.ContainsKey(owner))) continue;
                    if (rawOf.TryGetValue(f.Index, out var r)) PutKeptFace(r);
                }
            }
            else if (g.Src is FltFile.Rec mr0 && rawOf.TryGetValue(mr0.Index, out var rm)) PutRaw(rm);
            return;
        }
        var geo = g.Geom;
        if (geo?.Vertices == null || geo.Vertices.Count == 0) return;
        var ss = g.StateSet ?? inherited;
        int tex = TextureIndex(ss), mat = MaterialIndex(ss);
        var layers = LayersOf(ss);
        var template = FaceTemplate(g);
        var (_, _, tplColor) = flt.FaceAttributes(template, 0);
        BinaryPrimitives.WriteInt16BigEndian(template.AsSpan(28), (short)tex);
        BinaryPrimitives.WriteInt16BigEndian(template.AsSpan(30), (short)mat);
        var bbAt = billboardAt;
        if (bbAt != null) template[25] = 2;   // axial rotate billboard
        // generated state sets say whether back faces are culled (cards: front and back are separate faces)
        if (ss is { Src: null } && ss.Modes.TryGetValue("GL_CULL_FACE", out var cull)) template[18] = (byte)(cull.StartsWith("ON") ? 0 : 1);

        int nv = geo.Vertices.Count, vc = geo.Vertices.Comps;
        var P = geo.Vertices.Data;
        double[]? m = g.Matrix;   // only meshes carry their own matrix
        var N = geo.Normals is { } na && na.Comps == 3 && (na.Count == nv || na.Count == 1) ? na : null;
        var UV = geo.TexCoords.TryGetValue(0, out var ta) && ta.Count == nv && ta.Comps >= 2 ? ta : null;
        var C = geo.Colors is { } ca && ca.Comps >= 3 && (ca.Count == nv || ca.Count == 1) ? ca : null;
        // texture layers 1-7: a multitexture record after every face, a UV list after its vertex list for the units with coordinates
        byte[]? multi = null;
        if (layers.Count > 0)
        {
            multi = NewRec(FltFile.MULTITEX, 8 + 8 * layers.Count);
            uint lmask = 0;
            for (int k = 0; k < layers.Count; k++)
            {
                var l = layers[k];
                lmask |= 0x80000000u >> (l.unit - 1);
                BinaryPrimitives.WriteInt16BigEndian(multi.AsSpan(8 + 8 * k), (short)l.tex);
                BinaryPrimitives.WriteUInt16BigEndian(multi.AsSpan(10 + 8 * k), (ushort)l.effect);
                BinaryPrimitives.WriteInt16BigEndian(multi.AsSpan(12 + 8 * k), (short)l.mapping);
                BinaryPrimitives.WriteUInt16BigEndian(multi.AsSpan(14 + 8 * k), (ushort)l.data);
            }
            BinaryPrimitives.WriteUInt32BigEndian(multi.AsSpan(4), lmask);
        }
        var layerUv = layers.Select(l => (l.unit, uv: geo.TexCoords.TryGetValue(l.unit, out var la) && la.Count == nv && la.Comps >= 2 ? la : null))
            .Where(x => x.uv != null).Select(x => (x.unit, uv: x.uv!)).ToList();
        uint uvMask = 0;
        foreach (var (unit, _) in layerUv) uvMask |= 0x80000000u >> (unit - 1);
        double[]? diffuse = mat >= 0 && flt.Materials.TryGetValue(mat, out var mm) ? mm.diffuse : null;
        var vid = new int[nv];
        Array.Fill(vid, -1);
        int Vertex(int i)
        {
            if (vid[i] >= 0) return vid[i];
            int op = N != null ? (UV != null ? FltFile.VTX_CNT : FltFile.VTX_CN) : (UV != null ? FltFile.VTX_CT : FltFile.VTX_C);
            int len = op switch { FltFile.VTX_CNT => 64, FltFile.VTX_CN => 56, FltFile.VTX_CT => 48, _ => 40 };
            var d = NewRec(op, len);
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(6), 0x2000);   // no vertex colour: the face colour applies
            double x = P[i * vc], y = vc > 1 ? P[i * vc + 1] : 0, z = vc > 2 ? P[i * vc + 2] : 0;
            if (m != null) (x, y, z) = (m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11]);
            if (bbAt != null) { x += bbAt[0]; y += bbAt[1]; z += bbAt[2]; }
            BinaryPrimitives.WriteDoubleBigEndian(d.AsSpan(8), x / scale);
            BinaryPrimitives.WriteDoubleBigEndian(d.AsSpan(16), y / scale);
            BinaryPrimitives.WriteDoubleBigEndian(d.AsSpan(24), z / scale);
            if (N != null)
            {
                int r = N.Count == 1 ? 0 : i;
                for (int k = 0; k < 3; k++) BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(32 + 4 * k), (float)N.Data[r * 3 + k]);
            }
            if (UV != null)
            {
                int o = op == FltFile.VTX_CNT ? 44 : 32;
                BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(o), (float)UV.Data[i * UV.Comps]);
                BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(o + 4), (float)UV.Data[i * UV.Comps + 1]);
            }
            vid[i] = newVerts.Count;
            newVerts.Add(d);
            return vid[i];
        }
        byte[] Face(int first, bool line)
        {
            var d = (byte[])template.Clone();
            if (line) d[18] = 3;   // open wireframe
            if (C != null)
            {
                int r = C.Count == 1 ? 0 : first;
                var rgb = new double[3];
                for (int k = 0; k < 3; k++)
                {
                    double v = C.Data[r * C.Comps + k];
                    if (diffuse != null && diffuse[k] > 1e-6) v /= Math.Clamp(diffuse[k], 0.0, 1.0);   // FltRead multiplied the material in
                    rgb[k] = Math.Clamp(v, 0.0, 1.0);
                }
                bool same = tplColor != null && Enumerable.Range(0, 3).All(k => Math.Abs(tplColor[k] / (diffuse != null && diffuse[k] > 1e-6 ? Math.Clamp(diffuse[k], 0.0, 1.0) : 1.0) - rgb[k]) < 2e-3);
                if (!same)
                {
                    uint flags = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(44));
                    flags = (flags | 0x10000000) & ~0x40000000u;   // packed colour
                    BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(44), flags);
                    uint packed = 0xFF000000u | ((uint)Math.Round(rgb[2] * 255) << 16) | ((uint)Math.Round(rgb[1] * 255) << 8) | (uint)Math.Round(rgb[0] * 255);
                    BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(56), packed);
                }
            }
            return d;
        }
        void PutFace(byte[] face, ReadOnlySpan<int> corners)
        {
            Put(face);
            if (multi != null) Put(multi);
            PutPush();
            var vl = NewRec(FltFile.VTX_LIST, 4 + 4 * corners.Length);
            var fix = new List<(int, bool, int)>(corners.Length);
            for (int k = 0; k < corners.Length; k++) fix.Add((4 + 4 * k, true, Vertex(corners[k])));
            Put(vl, fix);
            if (layerUv.Count > 0)
            {
                var ul = NewRec(FltFile.UV_LIST, 8 + 8 * layerUv.Count * corners.Length);
                BinaryPrimitives.WriteUInt32BigEndian(ul.AsSpan(4), uvMask);
                int o = 8;
                foreach (int c in corners)   // per vertex, its layers in unit order
                    foreach (var (_, uv) in layerUv)
                    {
                        BinaryPrimitives.WriteSingleBigEndian(ul.AsSpan(o), (float)uv.Data[c * uv.Comps]);
                        BinaryPrimitives.WriteSingleBigEndian(ul.AsSpan(o + 4), (float)uv.Data[c * uv.Comps + 1]);
                        o += 8;
                    }
                Put(ul);
            }
            PutPop();
        }
        // decals kept as they were whose face is rewritten here: subfaces of the first new face (OpenSceneGraph draws
        // every subface with the same polygon offset, so which face carries them does not matter)
        var hosted = g.Src is List<FltFile.Rec> owners
            ? owners.Where(flt.Represented.Contains).SelectMany(o => o.Children).Where(c => c.Op == FltFile.FACE && FaceKept(c)).ToList()
            : new List<FltFile.Rec>();
        if (g.Src is List<FltFile.Rec> rewritten && rewritten.Any(f => flt.SubfaceOf.ContainsKey(f) && flt.Represented.Contains(f)))
            Warnings.Add($"'{g.Name}': rewritten decal (subface) triangles are written as ordinary faces and may flicker; leave decals out of simplify");
        var tris = geo.Triangles();
        bool hostedDone = hosted.Count == 0;
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            if (tris[t] >= nv || tris[t + 1] >= nv || tris[t + 2] >= nv) continue;
            // billboard cards: the triangle pair (a b c)(a c d) is one quad face, so the card turns as a whole
            if (bbAt != null && t + 5 < tris.Length && tris[t + 3] == tris[t] && tris[t + 4] == tris[t + 2] && tris[t + 5] < nv)
            {
                PutFace(Face(tris[t], false), new[] { tris[t], tris[t + 1], tris[t + 2], tris[t + 5] });
                t += 3;
                continue;
            }
            PutFace(Face(tris[t], false), tris.AsSpan(t, 3));
            if (hostedDone) continue;
            hostedDone = true;
            Put(NewRec(FltFile.PUSH_SUB, 4));
            foreach (var h in hosted) if (rawOf.TryGetValue(h.Index, out var rh)) PutKeptFace(rh);
            Put(NewRec(FltFile.POP_SUB, 4));
        }
        if (!hostedDone) foreach (var h in hosted) if (rawOf.TryGetValue(h.Index, out var rh)) PutKeptFace(rh);   // no face left to carry them
        var lines = geo.Lines();
        for (int t = 0; t + 1 < lines.Length; t += 2)
        {
            if (lines[t] >= nv || lines[t + 1] >= nv) continue;
            PutFace(Face(lines[t], true), lines.AsSpan(t, 2));
        }
        if (geo.Points().Length > 0) Warnings.Add($"'{g.Name}': points are not written to OpenFlight");
    }

    /// <summary>The record new faces copy their attributes from: an original face (or mesh) of this geometry (when its
    /// faces have different IR / material codes, one with the codes covering the largest area), else defaults. New
    /// geometry made from OpenFlight sources (cards, billboards, proxies, LOD levels, merged geometry) carries the source
    /// face with the dominant codes (FltFaceCodes.Carry): the defaults then get its IR colour code, IR material code,
    /// surface material code and feature ID, and are lit when it is (without a carried face: when there are normals).</summary>
    byte[] FaceTemplate(Node g)
    {
        byte[]? src = null;
        // the record bytes come from the Rec itself: after sharing duplicates it may belong to another file
        if (g.Src is List<FltFile.Rec> l && l.Count > 0)
        {
            src = l[0].Data;
            var first = FltFaceCodes.Of(l[0]);
            if (l.Any(f => FltFaceCodes.Of(f) != first) && FltFaceCodes.Dominant(new[] { (g, 1.0) }, scene) is { } dom) src = dom.face;
        }
        else if (g.Src is FltFile.Rec { Op: FltFile.MESH } mr && mr.Data.Length >= 84) src = FltFaceCodes.FaceLayout(mr);   // a mesh record is a face record with 4 more bytes at offset 12
        if (src != null && src.Length >= 80)
        {
            var d = (byte[])src.Clone();
            BinaryPrimitives.WriteUInt16BigEndian(d, FltFile.FACE);
            return d;
        }
        var f = NewRec(FltFile.FACE, 80);
        Encoding.Latin1.GetBytes("f").CopyTo(f, 4);
        f[18] = 1;                                                      // solid, no back face culling (like OSG)
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(26), -1);         // detail texture
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(44), 0x40000000); // no colour
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(64), -1);         // texture mapping
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(68), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(72), 0xFFFFFFFF);
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(78), -1);         // shader
        if (FltFaceCodes.Carried(g) is { } tpl && FltFaceCodes.Read(tpl) is { } codes) FltFaceCodes.Write(f, codes);   // IR / SMC / feature of the sources
        // lit like the sources (unlit sources stay unlit: the designer's choice), else lit when there are normals;
        // mode 2, not 3: new faces are written without vertex colours
        bool lit = FltFaceCodes.Carried(g) is { } t && FltFaceCodes.LightMode(t) is { } lm ? lm >= 2 : g.Geom?.Normals != null;
        f[FltFaceCodes.LightModeAt] = (byte)(lit ? 2 : 0);
        return f;
    }

    // ------------------------------------------------------------------------------ palettes

    static string TexKey(string name, string? resolved) => resolved != null ? System.IO.Path.GetFullPath(resolved) : name.Replace('\\', '/');

    /// <summary>Texture of a palette entry as the reader loaded it (on unit 0, else on a multitexture layer).</summary>
    Texture? PaletteTexture(int idx) =>
        flt.TextureObjects.GetValueOrDefault(idx) ?? flt.LayerTextures.Where(kv => kv.Value.index == idx).Select(kv => kv.Key).FirstOrDefault();

    /// <summary>Palette index of the face texture: the state set's unit 0.</summary>
    int TextureIndex(StateSet? ss)
    {
        if (ss == null) return -1;
        var t0 = ss.Textures.FirstOrDefault(x => x.Unit == 0);
        if (flt.StateKeys.TryGetValue(ss, out var k) && (k.tex < 0 || t0 != null && flt.TextureObjects.TryGetValue(k.tex, out var kt) && ReferenceEquals(kt, t0)))
            return k.tex;
        return t0 == null ? -1 : PaletteIndex(t0);
    }

    /// <summary>Texture layers 1-7 of a state set as (unit, palette index, effect, mapping, data); the reader's effect and
    /// mapping are kept, new layers are plain texture environment layers without a mapping.</summary>
    List<(int unit, int tex, int effect, int mapping, int data)> LayersOf(StateSet? ss)
    {
        var r = new List<(int, int, int, int, int)>();
        if (ss == null) return r;
        foreach (var t in ss.Textures.Where(x => x.Unit is >= 1 and <= 7).GroupBy(x => x.Unit).Select(x => x.First()).OrderBy(x => x.Unit))
        {
            int idx = PaletteIndex(t);
            if (idx < 0) continue;
            var (_, effect, mapping, data) = flt.LayerTextures.TryGetValue(t, out var li) ? li : (idx, 0, -1, 0);
            r.Add((t.Unit, idx, effect, mapping, data));
        }
        return r;
    }

    /// <summary>Palette index of a texture: the entry it was read from, else one with the same file, else a new entry.</summary>
    int PaletteIndex(Texture t)
    {
        foreach (var (i, o) in flt.TextureObjects) if (ReferenceEquals(o, t)) return i;
        if (flt.LayerTextures.TryGetValue(t, out var li)) return li.index;
        if (t.File.Length == 0) return -1;
        string key = TexKey(t.File, t.Path);
        if (texByPath.TryGetValue(key, out int idx)) return idx;
        string name = resized.TryGetValue(t, out var rz) ? rz : t.Path != null ? RelPath(Dest != null ? Dest(t.Path) : t.Path, outDir) : t.File;
        var d = NewRec(FltFile.TEX_PAL, 216);
        var b = Encoding.Latin1.GetBytes(name);
        Buffer.BlockCopy(b, 0, d, 4, Math.Min(b.Length, 199));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(204), nextTex);
        newTexRecs.Add(d);
        texByPath[key] = nextTex;
        return nextTex++;
    }

    int MaterialIndex(StateSet? ss)
    {
        if (ss == null) return -1;
        if (flt.StateKeys.TryGetValue(ss, out var k)) return k.mat;
        if (ss.Diffuse == null) return -1;
        foreach (var (i, m) in flt.Materials)
            if (m.name == ss.MaterialName && Enumerable.Range(0, 3).All(c => Math.Abs(m.diffuse[c] - ss.Diffuse[c]) < 1e-4)) return i;
        return -1;
    }

    /// <summary>Texture palette entry: points at the downscaled copy, or stays valid from the output folder.</summary>
    void PutTexturePalette(int r)
    {
        var d = (byte[])recs[r].data.Clone();
        if (d.Length >= 208)
        {
            int idx = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(204));
            string name = FltFile.CStr(d, 4, 204);
            var t = PaletteTexture(idx);
            string nw = t != null && resized.TryGetValue(t, out var rz) ? rz
                : t != null ? resized.Where(kv => kv.Key.Path != null && kv.Key.Path == t.Path).Select(kv => kv.Value).FirstOrDefault() ?? RelFile(name, t.Path)
                : RelFile(name);
            if (nw != name)
            {
                var b = Encoding.Latin1.GetBytes(nw);
                if (b.Length > 199) Warnings.Add($"texture path too long for OpenFlight (kept '{name}')");
                else { Array.Clear(d, 4, 200); Buffer.BlockCopy(b, 0, d, 4, b.Length); }
            }
        }
        Put(d);
    }

    /// <summary>Header and palettes, the new vertex palette, then the hierarchy with its vertex references patched.</summary>
    void Finish(Stream o)
    {
        var buf = body.GetBuffer();
        long n = body.Length;
        if (paletteAt < 0) paletteAt = n;
        // keep every palette vertex except those only removed faces used (others may be referenced from places we do not parse)
        var usedBefore = new HashSet<int>();
        foreach (var (op, data, _) in recs)
            if (op is FltFile.VTX_LIST or FltFile.MORPH_LIST)
                for (int i = 4; i + 4 <= data.Length; i += 4) usedBefore.Add(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i)));
        foreach (int key in vertexAt.Keys) if (!usedBefore.Contains(key)) keptOld.Add(key);
        var newOld = new Dictionary<int, int>();
        int at = 8;
        var kept = new List<int>();
        foreach (int key in keptOld)
        {
            int r = vertexAt[key];
            newOld[key] = at;
            kept.Add(r);
            at += recs[r].data.Length;
        }
        var newOff = new int[newVerts.Count];
        for (int i = 0; i < newVerts.Count; i++) { newOff[i] = at; at += newVerts[i].Length; }
        foreach (var (pos, isNew, value) in fixups)
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan((int)pos), isNew ? newOff[value] : newOld[value]);

        o.Write(buf, 0, (int)paletteAt);
        foreach (var t in newTexRecs) o.Write(t);
        if (kept.Count + newVerts.Count > 0 || vpalRec >= 0)
        {
            var pal = vpalRec >= 0 ? (byte[])recs[vpalRec].data.Clone() : NewRec(FltFile.VTX_PAL, 8);
            if (pal.Length < 8) Array.Resize(ref pal, 8);
            BinaryPrimitives.WriteUInt16BigEndian(pal.AsSpan(2), 8);
            BinaryPrimitives.WriteInt32BigEndian(pal.AsSpan(4), at);
            o.Write(pal, 0, 8);
            foreach (int r in kept) o.Write(recs[r].data);
            foreach (var v in newVerts) o.Write(v);
        }
        o.Write(buf, (int)paletteAt, (int)(n - paletteAt));
    }
}
