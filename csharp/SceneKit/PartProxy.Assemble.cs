namespace SceneKit;

/// <summary>Per-part proxies, step 8: re-assembly. The static parts' proxy goes where the target was; every moving node
/// is kept (same object, name, record, matrix and switch values) with its drawables replaced by its own part's proxy in
/// its own frame; a moving node that is also drawn elsewhere is cloned for this object and never changed.</summary>
public static partial class PartProxy
{
    /// <summary>What <see cref="Assemble"/> put into the graph: the node that stands where the target was (the target
    /// itself when it is a moving node and keeps its place), the geometries of kept parts with the piece each was taken
    /// from, the draw calls of the result, the kept parts and the moving parts kept as they are.</summary>
    internal sealed class Assembled
    {
        public Node Holder = null!;
        public List<(Node Geo, Piece From)> Kept = new();
        public List<string> KeptParts = new();
        public int KeptDraws;
        public List<string> KeptAsIs = new();
        /// <summary>The animated groups kept as they are (<see cref="MovingPart.Animated"/>).</summary>
        public List<string> Animated = new();
        /// <summary>The draw calls of the result as drawn: an output geometry below a switch child that is off is not drawn
        /// and not counted, as the count before (the geometries drawn below the target) does not count it.</summary>
        public int DrawCalls;
    }

    /// <summary>A moving target keeps its place and gets the proxy below it (a drawable cannot hold one).</summary>
    static bool StaysInPlace(Node target) => Optimizer.Moving(target) && target.Kind != Kind.Geometry && target.Children.Count > 0;

    /// <summary>Puts the proxy of <paramref name="r"/> (meshes filled by <see cref="BakeAtlas"/>, made into geometry nodes
    /// by the caller: <paramref name="geos"/>) into the graph. The static parts and every moving node go into a group
    /// "<paramref name="label"/> proxy" (the caller puts it where the target was); a moving node with a static matrix
    /// above it (the target's own included) sits in a MatrixTransform "&lt;name&gt; place". Below each moving node its
    /// drawables are replaced by its proxy (through <see cref="OptContext.ReplaceMany"/>, so undo restores them); switch
    /// children one by one, so the values stay. A moving target keeps its place: its children are replaced (switch: the
    /// first child that is on), the static proxy taken into its frame. Moving nodes drawn elsewhere too (more than one
    /// parent on the way up to the target) are cloned, and so is everything changed below a clone.</summary>
    internal static Assembled Assemble(OptContext ctx, Node target, ObjectResult r, IReadOnlyDictionary<ProxyMesh, Node> geos,
        Func<Piece, StateSet?> stateOf, string label)
    {
        var a = new Assembled();
        double[]? inPlace = StaysInPlace(target) && Mat.Invert(r.Asm.Frame) is { } inv ? inv : null;
        var content = Content(ctx, target, a, r, r, null, geos, stateOf, inPlace == null ? r.Asm.Frame : null, inPlace == null ? null : (inv: inPlace, frame: r.Asm.Frame), false, true);
        if (inPlace != null)
        {
            var kids = target.Children;
            var vals = target.Kind == Kind.Switch ? target.SwitchValues : null;
            bool On(int i) => vals == null || i >= vals.Count || vals[i];
            int first = Enumerable.Range(0, kids.Count).FirstOrDefault(On, -1);
            var map = new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < kids.Count; i++)
                if (On(i) && !map.ContainsKey(kids[i])) map[kids[i]] = i == first ? (target.Kind == Kind.Geode ? Drawables(content) : content) : new List<Node>();
            ctx.ReplaceMany(target, map);
            a.Holder = target;
        }
        else if (content.Count == 1 && content[0].Kind == Kind.Geode && ctx.ParentsOf(target).Any(p => p.Kind == Kind.Geode))
            a.Holder = content[0];   // a selected drawable: ReplaceEverywhere puts the geometry into its geode
        else
        {
            a.Holder = new Node(Kind.Group, "osg::Group", label + " proxy");
            a.Holder.Children.AddRange(content);
        }
        return a;
    }

    /// <summary>What replaces an owner's drawables: the geode of its proxy mesh, a geode per kept geometry (one draw call each), then each moving node
    /// below it (processed, placed). <paramref name="place"/>: the matrix the moving nodes' static matrices are put under
    /// (the target's frame at the top, else none); <paramref name="local"/>: the frame a moving target's static proxy is
    /// taken into (inverse and frame), else null. <paramref name="drawn"/>: whether the owner is drawn (false below a switch
    /// child that is off); only drawn geometries count as draw calls.</summary>
    static List<Node> Content(OptContext ctx, Node target, Assembled a, ObjectResult top, ObjectResult r, MovingPart? owner,
        IReadOnlyDictionary<ProxyMesh, Node> geos, Func<Piece, StateSet?> stateOf, double[]? place, (double[] inv, double[] frame)? local, bool clone, bool drawn)
    {
        var o = new List<Node>();
        if (top.Meshes.FirstOrDefault(m => ReferenceEquals(m.Owner, owner)) is { } mesh && geos.TryGetValue(mesh, out var geo))
        {
            if (local is { } l) ToLocal(geo.Geom!, l.inv, l.frame);
            o.Add(new Node(Kind.Geode, "osg::Geode", geo.Name) { Children = { geo } });
            if (drawn) a.DrawCalls++;
        }
        foreach (var pr in r.Static.Where(x => !InAtlas(x)))
        {
            var kept = KeptGeometries(r.Asm, pr, stateOf, local?.frame);
            if (kept.Count == 0) continue;
            foreach (var k in kept)   // one draw call per kept geometry (per piece, with its own state set)
                o.Add(new Node(Kind.Geode, "osg::Geode", k.Geo.Name) { Children = { k.Geo } });
            a.Kept.AddRange(kept);
            a.KeptParts.Add(pr.Part.Name);
            if (!drawn) continue;
            a.KeptDraws += kept.Count;
            a.DrawCalls += kept.Count;
        }
        var order = SceneOrder(target);   // scene order, not discovery order
        foreach (var grp in r.Moving.GroupBy(x => x.M.Node, (IEqualityComparer<Node>)ReferenceEqualityComparer.Instance)
                     .OrderBy(x => order.GetValueOrDefault(x.Key, int.MaxValue)))
        {
            var entries = grp.ToList();
            var node = Process(ctx, target, a, top, grp.Key, entries, geos, stateOf, clone || Shared(ctx, grp.Key, target), drawn);
            var m = place == null ? entries[0].M.StaticAbove : Mat.Mul(place, entries[0].M.StaticAbove);
            o.Add(Mat.IsIdentity(m, 0, 1e-12) ? node : new Node(Kind.Transform, "osg::MatrixTransform", node.Name + " place") { Matrix = m, Children = { node } });
        }
        return o;
    }

    /// <summary>The position of every node below <paramref name="root"/> in a depth-first walk (the first visit counts).</summary>
    static Dictionary<Node, int> SceneOrder(Node root)
    {
        var o = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!o.TryAdd(n, o.Count)) continue;
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
        return o;
    }

    /// <summary>One moving node (a switch: all its children's entries): its drawables replaced by its proxy, in its own
    /// frame. A node with nothing drawn, a drawable, an animated group and a node whose parts all failed stay as they are.
    /// Returns the node, or its clone. <paramref name="drawn"/>: whether the node is drawn (see <see cref="Content"/>).</summary>
    static Node Process(OptContext ctx, Node target, Assembled a, ObjectResult top, Node n, List<(MovingPart M, ObjectResult? Inner)> entries,
        IReadOnlyDictionary<ProxyMesh, Node> geos, Func<Piece, StateSet?> stateOf, bool clone, bool drawn)
    {
        var t = clone ? Clone(n) : n;
        var map = new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
        foreach (var (m, inner) in entries)
        {
            bool on = drawn && Drawn(m);
            if (m.Animated)
            {
                // the frames of an animation: neither merged nor split; its geometries stay and draw as they did
                if (!a.Animated.Contains(n.Name)) a.Animated.Add(n.Name);
                if (on) a.DrawCalls += m.Inner.AllPieces().Count(DrawnPiece);
                continue;
            }
            if (inner == null) continue;   // nothing drawn below it
            if (n.Kind == Kind.Geometry || Failed(inner))
            {
                a.KeptAsIs.Add(m.SwitchChild >= 0 ? $"{n.Name} {m.SwitchChild}" : n.Name);
                if (on) a.DrawCalls += inner.Asm.AllPieces().Count(DrawnPiece);
                continue;
            }
            var content = Content(ctx, target, a, top, inner, m, geos, stateOf, null, null, clone, on);
            if (m.SwitchChild >= 0)
            {
                if (m.SwitchChild < n.Children.Count) map[n.Children[m.SwitchChild]] = content;   // a child from an external file stays
            }
            else if (n.Children.Count > 0)
            {
                map[n.Children[0]] = n.Kind == Kind.Geode ? Drawables(content) : content;
                foreach (var c in n.Children.Skip(1)) map.TryAdd(c, new List<Node>());
            }
        }
        if (map.Count > 0)
        {
            if (clone) Optimizer.ReplaceChildren(t, map);   // not in the graph yet: nothing to keep up to date
            else ctx.ReplaceMany(t, map);
        }
        return t;
    }

    /// <summary>Whether a switch child is on (drawn): no switch values, a child past them, or its value true. A child
    /// from an external file (past <see cref="Node.Children"/>) of a switch with values is not drawn, as the scene walk
    /// skips it.</summary>
    static bool SwitchChildOn(Node s, int i) => s.Kind != Kind.Switch || s.SwitchValues == null
        || (i >= 0 && i < s.Children.Count && (i >= s.SwitchValues.Count || s.SwitchValues[i]));

    /// <summary>Whether a moving part is drawn as far as its own node goes (a switch child that is on, or not a switch child).</summary>
    internal static bool Drawn(MovingPart m) => m.SwitchChild < 0 || SwitchChildOn(m.Node, m.SwitchChild);

    /// <summary>Whether a piece is drawn: every switch on its path from the target has the next node on the path on.</summary>
    internal static bool DrawnPiece(Piece p)
    {
        for (int i = 0; i + 1 < p.Path.Length; i++)
        {
            var s = p.Path[i];
            if (s.Kind != Kind.Switch || s.SwitchValues == null) continue;
            int at = -1;
            for (int k = 0; k < s.Children.Count; k++) if (ReferenceEquals(s.Children[k], p.Path[i + 1])) { at = k; break; }
            if (!SwitchChildOn(s, at)) return false;
        }
        return true;
    }

    /// <summary>A moving part none of whose parts got a proxy (all kept as they are; a decimated part is a proxy) and with
    /// no moving node below it.</summary>
    static bool Failed(ObjectResult inner) => inner.Static.Count > 0 && inner.Static.All(p => p.Method == PartMethod.Kept) && inner.Moving.Count == 0;

    /// <summary>Whether the node is drawn somewhere else too: a node on the way up to the target has more than one parent
    /// (or none, i.e. it is reached without the target).</summary>
    static bool Shared(OptContext ctx, Node n, Node target)
    {
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        for (var cur = n; !ReferenceEquals(cur, target); )
        {
            if (!seen.Add(cur)) return true;
            var ps = ctx.ParentsOf(cur);
            if (ps.Count != 1) return true;
            cur = ps[0];
        }
        return false;
    }

    /// <summary>A copy of a node for one object's subtree: every field, its own lists (children, switch values, ranges).</summary>
    static Node Clone(Node n) => new(n.Kind, n.Cls, n.Name)
    {
        Children = new List<Node>(n.Children), Matrix = (double[]?)n.Matrix?.Clone(), StateSet = n.StateSet, Geom = n.Geom,
        Ranges = n.Ranges?.ToList(), RangeMode = n.RangeMode, SwitchValues = n.SwitchValues?.ToList(), Files = new List<string>(n.Files),
        NodeMask = n.NodeMask, Src = n.Src, Dirty = new HashSet<string>(n.Dirty), Notes = new List<string>(n.Notes), External = n.External,
        Center = (double[]?)n.Center?.Clone(), VirtualRoot = n.VirtualRoot, Externals = n.Externals?.ToList(),
        FilesOut = n.FilesOut == null ? null : new Dictionary<string, string>(n.FilesOut), GenObj = n.GenObj,
        Extra = n.Extra == null ? null : new Dictionary<string, object>(n.Extra),
    };

    /// <summary>The drawables of geodes (what may go into a geode).</summary>
    static List<Node> Drawables(List<Node> content) => content.SelectMany(n => n.Kind == Kind.Geode ? n.Children : new List<Node> { n }).ToList();

    /// <summary>Positions through <paramref name="inv"/>, normals through the transpose of <paramref name="frame"/>
    /// (the inverse transpose of <paramref name="inv"/>): drawn below the frame, they are where they were.</summary>
    static void ToLocal(GeomData g, double[] inv, double[] frame)
    {
        var V = (double[])g.Vertices!.Data.Clone();
        for (int i = 0; i < V.Length / 3; i++) (V[3 * i], V[3 * i + 1], V[3 * i + 2]) = Mat.Apply(inv, V[3 * i], V[3 * i + 1], V[3 * i + 2]);
        g.Vertices = new VArray(V, 3, "Vec3Array");
        if (g.Normals is { Comps: 3 } nv)
        {
            var N = (double[])nv.Data.Clone();
            for (int i = 0; i < N.Length / 3; i++)
            {
                double x = N[3 * i], y = N[3 * i + 1], z = N[3 * i + 2];
                for (int c = 0; c < 3; c++) N[3 * i + c] = frame[c] * x + frame[4 + c] * y + frame[8 + c] * z;
            }
            Proxy.Normalize(N);
            g.Normals = new VArray(N, 3, "Vec3Array");
        }
    }

    /// <summary>The 3 x 3 (row major) that takes the piece's original normals to its owner's frame (applied as transpose): the inverse of
    /// the matrix that placed the piece. Null when that matrix cannot be inverted.</summary>
    static double[]? PieceLinear(Piece p)
    {
        if (p.World == null) return new[] { 1.0, 0, 0, 0, 1, 0, 0, 0, 1 };
        if (Mat.Invert(p.World) is not { } inv) return null;
        var o = new double[9];
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) o[3 * r + c] = inv[4 * r + c];   // entry (r, c) of the inverse, so n' = inverse^T n
        return o;
    }

    /// <summary>A kept part drawn as its original, or a decimated part drawn with the original's attributes: one geometry
    /// per piece it has triangles of (kept: those triangles; decimated: the result's triangles whose first corner comes from
    /// that piece, through <see cref="PartResult.From"/>), positions in the owner's frame, per-vertex texture coordinates
    /// and colours of the piece (decimated: of the original vertex each vertex comes from), normals (kept: the original's,
    /// else from the triangles) and the piece's state (<paramref name="stateOf"/>). Named "&lt;part&gt; kept" or
    /// "&lt;part&gt; decimated". <paramref name="frame"/>: a moving target's frame, which the positions are taken out of.</summary>
    internal static List<(Node Geo, Piece From)> KeptGeometries(Assembly asm, PartResult pr, Func<Piece, StateSet?> stateOf, double[]? frame)
    {
        var o = new List<(Node, Piece)>();
        var order = new List<int>();
        var tris = new Dictionary<int, List<int>>();
        foreach (var u in pr.Part.Units)
        {
            if (!tris.TryGetValue(u.Piece, out var l)) { tris[u.Piece] = l = new List<int>(); order.Add(u.Piece); }
            l.AddRange(u.Tris);
        }
        bool dec = pr.Method == PartMethod.Decimated && pr.From != null;
        // per piece, the corners: kept, piece vertices (position and attributes there); decimated, result vertices
        var corners = new Dictionary<int, List<int>>();
        var triOf = new Dictionary<int, List<int>>();   // decimated: the result triangle of each piece triangle
        int[]? srcVert = null;
        if (dec)
        {
            // the piece and piece vertex of every part vertex (FromPieces adds the units' triangles in this order)
            int npv = pr.Part.V.Length / 3, at = 0;
            var srcPiece = new int[npv];
            srcVert = new int[npv];
            foreach (var u in pr.Part.Units)
                foreach (int t in u.Tris)
                    for (int k = 0; k < 3; k++)
                    {
                        int pv = pr.Part.T[at++];
                        (srcPiece[pv], srcVert[pv]) = (u.Piece, asm.Pieces[u.Piece].T[3 * t + k]);
                    }
            for (int t = 0; t < pr.T.Length / 3; t++)
            {
                int pi = srcPiece[pr.From![pr.T[3 * t]]];
                if (!corners.TryGetValue(pi, out var l)) { corners[pi] = l = new List<int>(); triOf[pi] = new List<int>(); }
                l.AddRange(new[] { pr.T[3 * t], pr.T[3 * t + 1], pr.T[3 * t + 2] });
                triOf[pi].Add(t);
            }
        }
        else
            foreach (int pi in order)
                corners[pi] = tris[pi].SelectMany(t => new[] { asm.Pieces[pi].T[3 * t], asm.Pieces[pi].T[3 * t + 1], asm.Pieces[pi].T[3 * t + 2] }).ToList();
        string suffix = dec ? " decimated" : " kept";
        var inv = frame == null ? null : Mat.Invert(frame);
        foreach (int pi in order)
        {
            if (!corners.TryGetValue(pi, out var cs) || cs.Count == 0) continue;
            var p = asm.Pieces[pi];
            var src = dec ? pr.V : p.V;
            int Attr(int key) => dec ? srcVert![pr.From![key]] : key;   // the piece vertex whose attributes a vertex takes
            var id = new Dictionary<int, int>();
            var used = new List<int>();
            var T = new int[cs.Count];
            for (int c = 0; c < cs.Count; c++)
            {
                if (!id.TryGetValue(cs[c], out int j)) { id[cs[c]] = j = used.Count; used.Add(cs[c]); }
                T[c] = j;
            }
            var V = new double[3 * used.Count];
            for (int j = 0; j < used.Count; j++) for (int c = 0; c < 3; c++) V[3 * j + c] = src[3 * used[j] + c];
            var g = new GeomData { Vertices = new VArray(V, 3, "Vec3Array") };
            int nv = p.V.Length / 3;
            // the original's per-vertex normals (taken to the owner's frame with the piece), colours and texture coordinates;
            // normals are rebuilt from the triangles when the original has none or the part was decimated
            if (!dec && p.G.Normals is { Comps: 3, Binding: "vertex" } on && on.Count == nv && PieceLinear(p) is { } lin)
            {
                var N = new double[3 * used.Count];
                for (int j = 0; j < used.Count; j++)
                    for (int c = 0; c < 3; c++)
                        N[3 * j + c] = lin[c] * on.Data[3 * used[j]] + lin[3 + c] * on.Data[3 * used[j] + 1] + lin[6 + c] * on.Data[3 * used[j] + 2];
                Proxy.Normalize(N);
                g.Normals = new VArray(N, 3, "Vec3Array");
            }
            else if (dec && pr.Corner is { } pc && pc.Length == 3 * pr.T.Length)
            {
                // the corner normals from the original (normals = original), averaged per vertex like the triangles' below
                var N = new double[V.Length];
                for (int c = 0; c < T.Length; c++) for (int k = 0; k < 3; k++) N[3 * T[c] + k] += pc[9 * triOf[pi][c / 3] + 3 * (c % 3) + k];
                Proxy.Normalize(N);
                g.Normals = new VArray(N, 3, "Vec3Array");
            }
            else
            {
                var cn = Proxy.CornerNormals(V, T, 0.7);
                var N = new double[V.Length];
                for (int c = 0; c < T.Length; c++) for (int k = 0; k < 3; k++) N[3 * T[c] + k] += cn[3 * c + k];
                Proxy.Normalize(N);
                g.Normals = new VArray(N, 3, "Vec3Array");
            }
            if (p.G.Colors is { Binding: "vertex" } oc && oc.Count == nv)
            {
                var d = new double[oc.Comps * used.Count];
                for (int j = 0; j < used.Count; j++) Array.Copy(oc.Data, Attr(used[j]) * oc.Comps, d, j * oc.Comps, oc.Comps);
                g.Colors = new VArray(d, oc.Comps, oc.Type, "vertex", oc.Normalize);
            }
            foreach (var (unit, uv) in p.G.TexCoords)
            {
                if (uv.Binding != "vertex" || uv.Count != nv) continue;
                var d = new double[uv.Comps * used.Count];
                for (int j = 0; j < used.Count; j++) Array.Copy(uv.Data, Attr(used[j]) * uv.Comps, d, j * uv.Comps, uv.Comps);
                g.TexCoords[unit] = new VArray(d, uv.Comps, uv.Type);
            }
            g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Length, indices: T, indexType: MeshOps.SmallestIndexType(used.Count)));
            if (inv != null) ToLocal(g, inv, frame!);
            var node = new Node(Kind.Geometry, "osg::Geometry", pr.Part.Name + suffix) { Geom = g, StateSet = stateOf(p) };
            node.Mark("geometry");
            o.Add((node, p));
        }
        return o;
    }

    /// <summary><see cref="Detail(ObjectResult, Assembled, double)"/> with the cell of the build (<see cref="ObjectResult.Cell"/>).</summary>
    internal static string Detail(ObjectResult r, Assembled a) => Detail(r, a, r.Cell);

    /// <summary>The detail of an object's result line: the moving nodes kept separate (wheels named when every DOF name
    /// says so), within mode the target and the deviation reached ("within 4 cm (reached 3.2 cm, max over parts)"), the
    /// tiny parts dropped and the distance they were dropped within (keep mode: 2 cells of the object's grid; within: e),
    /// the kept parts ("{k} parts kept original or decimated, +{d} draw calls": k counts parts, d counts their geometries, one
    /// draw call each, a piece with its own state set being one) , the moving parts kept as they are, the LOD alternatives dropped
    /// and the ground rule ("underside cut", "underside not seen from below"; nothing for full).</summary>
    internal static string Detail(ObjectResult r, Assembled a, double cell)
    {
        // an animated group is kept as it is (its own note), not a part kept separate
        var nodes = AllMoving(r).Where(x => !x.M.Animated).Select(x => x.M.Node).Distinct(ReferenceEqualityComparer.Instance).Cast<Node>().ToList();
        int switches = nodes.Count(n => n.Kind == Kind.Switch), dofs = nodes.Count - switches;
        bool wheels = dofs > 0 && nodes.Where(n => n.Kind != Kind.Switch).All(n => n.Name.Contains("wheel", StringComparison.OrdinalIgnoreCase));
        var sep = new List<string>();
        if (dofs > 0) sep.Add($"{PyFormat.Int(dofs)} " + (wheels ? "wheels" : "DOF parts"));
        if (switches > 0) sep.Add($"{PyFormat.Int(switches)} " + (switches == 1 ? "switch" : "switches"));
        string s = sep.Count > 0 ? $" ({string.Join(" and ", sep)} kept separate)" : "";
        bool within = r.Report.TargetM is double e && e > 0;
        if (within && r.Report.ReachedM is double reached) s += "; " + WithinReachedFragment(r.Report.TargetM!.Value, reached);
        int tiny = Owners(r.Asm).Sum(x => x.A.TinyDropped);
        if (tiny > 0) s += $"; {PyFormat.Int(tiny)} tiny parts dropped, within {ErrorTarget.Distance(within ? r.Report.TargetM!.Value : 2 * cell)}";
        if (a.KeptParts.Count > 0)
            s += $"; {PyFormat.Int(a.KeptParts.Count)} parts kept original or decimated, +{PyFormat.Int(a.KeptDraws)} draw calls ({string.Join(", ", a.KeptParts)})";
        int lod = Owners(r.Asm).Sum(x => x.A.LodAlternativesDropped);
        if (lod > 0) s += $"; {PyFormat.Int(lod)} LOD alternatives dropped (the nearest level is used)";
        if (r.Report.Ground == "cut") s += "; underside cut";
        else if (r.Report.Ground == "above") s += "; underside not seen from below";
        return s;
    }
}
