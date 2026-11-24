namespace SceneKit;

public static partial class Optimizer
{
    /// <summary>Glass and cut-outs: a transparent state set, a texture with (partly) transparent texels, or a texture
    /// with an alpha channel we cannot decode (e.g. DDS, embedded images).</summary>
    public static bool SeeThrough(StateSet? ss)
    {
        if (ss == null) return false;
        if (ss.Transparent) return true;
        foreach (var t in ss.Textures)
            if (SeeThroughTexture(t)) return true;
        return false;
    }

    /// <summary>A texture with (partly) transparent texels, or with an alpha channel we cannot decode.</summary>
    public static bool SeeThroughTexture(Texture t)
    {
        if (t.Channels is not (2 or 4)) return false;
        var img = SoftRender.PreviewTextures.Get(t, out _);
        if (img == null) return t.Exists || t.Embedded;   // has alpha, but we cannot look at it: assume it is used
        if (img.Channels is not (2 or 4)) return false;
        int c = img.Channels;
        for (int i = c - 1; i < img.Pixels.Length; i += c)
            if (img.Pixels[i] < 250) return true;   // cut-outs and tinted glass
        return false;
    }

    static readonly HashSet<string> MovingClasses = new() { "Sequence", "DOFTransform", "MultiSwitch", "AnimationPathMatrixTransform" };

    /// <summary>Parts that change at runtime: switches (damage states, lights), DOFs (hatches, turrets), animated
    /// groups, sequences and nodes with update callbacks.</summary>
    internal static bool Moving(Node n) =>
        n.Kind == Kind.Switch || n.Cls == "FLT DOF" || n.Notes.Contains("animated group") || MovingClasses.Contains(OsgReader.Short(n.Cls))
        || (n.Src is Obj o && (o.Get("UpdateCallback") != null || o.Get("UpdateCallbacks") != null));

    static string RemoveOccluded(OptContext ctx)
    {
        if (ctx.Targets == null) return "nothing changed: select the objects (e.g. vehicles) whose invisible inside should go";
        const string P = "remove_occluded";
        var opt = new Occlusion.Options
        {
            Views = ctx.PInt(P, "views"), Resolution = ctx.PInt(P, "resolution"), Adjacency = ctx.PInt(P, "adjacency"), Progress = ctx.Progress,
            AboveHorizonOnly = ctx.PStr(P, "ground") == "above",
        };
        bool seeThrough = ctx.PBool(P, "see_through");
        var targets = new HashSet<Node>(ctx.Targets, ReferenceEqualityComparer.Instance);
        // everything reachable from the root without entering a target (external files included): geometry there is
        // also used outside the selection, however far up the sharing happens
        var outside = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        if (ctx.Scene.Root != null)
        {
            var stack = new Stack<Node>();
            stack.Push(ctx.Scene.Root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (targets.Contains(n) || !outside.Add(n)) continue;
                foreach (var c in n.AllChildren()) stack.Push(c);
            }
        }
        var parents = ctx.Parents;
        long before = 0, removed = 0, changed = 0, emptied = 0;
        var shared = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var moving = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        foreach (var target in ctx.Targets)
        {
            var drawn = SoftRender.GeometryPaths(target);
            var uses = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
            foreach (var (n, _, _, _) in drawn) uses[n] = uses.GetValueOrDefault(n) + 1;
            var items = new List<Occlusion.Item>();
            var editable = new List<(Node node, int item, int[] tris)>();
            foreach (var (n, w, _, path) in drawn)
            {
                var g = n.Geom!;
                var tris = g.Triangles();
                int nv = g.NumVertices;
                if (tris.Length == 0 || g.Vertices == null || tris.Any(i => i < 0 || i >= nv)) continue;
                var V = new double[nv * 3];
                int cs = g.Vertices.Comps;
                for (int i = 0; i < nv; i++)
                {
                    double x = g.Vertices.Data[i * cs], y = cs > 1 ? g.Vertices.Data[i * cs + 1] : 0, z = cs > 2 ? g.Vertices.Data[i * cs + 2] : 0;
                    V[3 * i] = w[0] * x + w[1] * y + w[2] * z + w[3];
                    V[3 * i + 1] = w[4] * x + w[5] * y + w[6] * z + w[7];
                    V[3 * i + 2] = w[8] * x + w[9] * y + w[10] * z + w[11];
                }
                // a part that moves at runtime hides nothing (it may open) and is left as it is; so is glass, which only
                // gets seen. Transparency counts from every state set on the way down, not just the nearest one.
                bool isMoving = path.Any(Moving);
                bool glass = seeThrough && path.Any(p => SeeThrough(p.StateSet));
                items.Add(new Occlusion.Item(V, tris, !isMoving && !glass));
                if (!g.Optimizable || MeshOps.Instanced(g)) continue;
                if (isMoving) { moving.Add(n); continue; }
                if (outside.Contains(n) || uses[n] > 1) { shared.Add(n); continue; }
                editable.Add((n, items.Count - 1, tris));
            }
            if (editable.Count == 0) continue;
            var vis = Occlusion.Visible(items, opt);
            foreach (var (n, item, tris) in editable)
            {
                var keep = vis[item];
                int m = keep.Length, gone = keep.Count(k => !k);
                before += m;
                if (gone == 0) continue;
                removed += gone;
                changed++;
                var g = n.Geom!;
                var kept = new List<int>((m - gone) * 3);
                for (int t = 0; t < m; t++) if (keep[t]) { kept.Add(tris[3 * t]); kept.Add(tris[3 * t + 1]); kept.Add(tris[3 * t + 2]); }
                var lines = g.Lines();
                var pts = g.Points();
                var used = new SortedSet<int>(kept.Concat(lines).Concat(pts)).ToArray();
                if (used.Length == 0 && parents.TryGetValue(n, out var owners) && owners.All(p => !FixedChildCount(p)))
                {
                    foreach (var p in owners.ToList()) ctx.ReplaceMany(p, new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance) { [n] = new List<Node>() });
                    emptied++;
                    continue;
                }
                var remap = new int[g.NumVertices];
                for (int i = 0; i < used.Length; i++) remap[used[i]] = i;
                SetGeom(n, Rebuild(g, used, kept.Select(i => remap[i]).ToArray(), lines.Select(i => remap[i]).ToArray(), pts.Select(i => remap[i]).ToArray()));
            }
        }
        string msg = $"{PyFormat.Int(removed)} of {PyFormat.Int(before)} triangles were never visible and were removed "
                     + $"({PyFormat.Int(changed)} geometries, {PyFormat.Int(emptied)} removed completely)";
        if (shared.Count > 0) msg += $"; {PyFormat.Int(shared.Count)} shared geometries skipped (also used elsewhere)";
        if (moving.Count > 0) msg += $"; {PyFormat.Int(moving.Count)} moving parts (switches, DOFs, animations) left as they are and not counted as cover";
        if (opt.AboveHorizonOnly) msg += "; views from above the horizon only";
        return msg;
    }
}
