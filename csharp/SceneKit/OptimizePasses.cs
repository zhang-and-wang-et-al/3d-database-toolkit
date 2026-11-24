using System.Globalization;

namespace SceneKit;

public static partial class Optimizer
{
    /// <summary>When set, document edits are recorded as (statement, old block) for undo.</summary>
    [ThreadStatic] public static List<(Stmt stmt, object? block)>? DomJournal;

    static Optimizer() => RegisterAll();

    static PassParam PB(string k, string l, bool d) => new(k, l, d, typeof(bool));
    static PassParam PI(string k, string l, int d) => new(k, l, d, typeof(int));
    static PassParam PD(string k, string l, double d) => new(k, l, d, typeof(double));
    static PassParam PS(string k, string l, string d) => new(k, l, d, typeof(string));
    static PassParam PC(string k, string l, string d, params string[] choices) => new(k, l, d, typeof(string), choices);

    static void RegisterAll()
    {
        Reg("remove_hidden", "Remove hidden subtrees", "cleanup", false,
            "Deletes nodes with node mask 0. They are loaded but never drawn - unless the application switches them on at runtime, so only use this if you know it does not.",
            RemoveHidden);
        Reg("remove_occluded", "Remove geometry nobody can see", "cleanup", false,
            "For the selected objects (e.g. vehicles): renders them from many directions around them and deletes the triangles that are never visible from outside (interiors, engines, the inside of double walls). Glass and cut-out textures count as see-through. Geometry also used outside the selection is skipped; moving parts (switches, DOFs, animations) are left as they are and hide nothing. Select single objects, not the ground they stand on: views come from far away, so with the ground selected an object's underside counts as hidden even where a camera close to the ground could see it. Underside 'cut' and 'above' are wrong for objects seen from below (aircraft, bridges).",
            RemoveOccluded, PI("views", "Directions around the object", 162), PI("resolution", "Pixels per view (0 = automatic)", 0),
            PI("adjacency", "Keep neighbours of visible triangles (rings)", 1), PB("see_through", "Keep interiors visible through glass / cut-outs", true),
            PC("ground", "Views: all around / from above the horizon only", "full", "full", "above"));
        Reg("remove_empty", "Remove empty nodes", "cleanup", true,
            "Deletes geometries without primitives and groups / geodes without children (LOD levels are kept: an empty LOD level intentionally draws nothing).",
            RemoveEmpty);
        Reg("remove_redundant", "Remove redundant groups / identity transforms", "cleanup", false,
            "Collapses plain groups with a single child and transforms that do nothing.", RemoveRedundant,
            PB("keep_named", "Keep named nodes (applications may look them up by name)", true));
        Reg("flatten_transforms", "Flatten static transforms", "draw calls", false,
            "Applies static MatrixTransform / PositionAttitudeTransform matrices to the vertices below them and removes the transform. Skipped where geometry is shared (instancing) or coordinates would get large.",
            FlattenTransforms, PB("keep_named", "Keep named transforms", true), PD("max_coord", "Do not create coordinates larger than", 1e5));
        Reg("share_duplicates", "Share duplicates (instancing)", "memory", true,
            "Identical textures, state sets and geometries are stored once and referenced from every place that used a copy.",
            ShareDuplicates, PB("geometry", "Share identical geometry", true), PB("statesets", "Share identical state sets / textures", true));
        Reg("make_instances", "Make instances of identical / rigid copies", "memory", false,
            "Copies of an object that differ only by placement (the same vertex data, or the same shape turned and moved with its vertices pre-transformed) keep one geometry; every other copy becomes a MatrixTransform that places the shared one. Per texture / material variant: copies that look different stay separate. Applies to the selection, or to every group of similar objects. Names below the replaced copies are lost (the transform keeps the copy's name). Saved as OpenSceneGraph files the copies share the geometry; saved as OpenFlight (.flt) the shared object is written once as an instance definition and every copy as an instance reference under a group with its matrix.",
            MakeInstances, PD("tolerance", "Rigid copies: largest deviation (share of the object's radius)", 0.002));
        Reg("weld", "Merge duplicate vertices (index geometry)", "memory", true,
            "Vertices with identical position, normal, colour and texture coordinates are stored once; unindexed geometry (DrawArrays) becomes one indexed triangle list.",
            Weld, PD("tolerance", "Position tolerance (scene units, 0 = exact)", 0.0));
        Reg("clean", "Remove degenerate triangles & unused vertices", "memory", true,
            "Drops zero-area triangles, triangles with a repeated corner, duplicated triangles and vertices no primitive uses.", Clean);
        Reg("triangulate", "Single indexed triangle lists", "draw calls", true,
            "Strips, fans, quads, polygons and DrawArrayLengths become one indexed triangle list per geometry (one draw call) with the smallest index type.",
            Triangulate);
        Reg("compact_arrays", "Compact constant arrays", "memory", true,
            "Per-vertex colour / normal arrays that hold a single value become one overall value.", Compact);
        Reg("float_arrays", "Double -> float arrays", "memory", false,
            "Vec3dArray etc. become 32-bit float arrays (half the memory). Run 'Re-center' first for large coordinates.", FloatArrays);
        Reg("vertex_cache", "Optimise vertex cache order", "speed", true,
            "Reorders triangles (Tipsify) and vertices (first use) so the GPU re-uses transformed vertices.", VertexCache,
            PI("min_tris", "Only geometries with at least", 1000));
        Reg("merge_geometries", "Merge small geometries with the same state", "draw calls", true,
            "Sibling drawables (in one geode, or single-drawable geodes in one group) that share the same state set and vertex layout are merged into one geometry. Names of merged drawables are lost.",
            Merge, PI("max_tris_each", "Only merge geometries smaller than (triangles)", 10000), PI("max_vertices", "Max vertices per merged geometry", 1_000_000));
        Reg("recenter", "Re-center far-away geometry", "precision", false,
            "Geometry with large vertex coordinates is moved to its local origin; the offset goes into a MatrixTransform (double precision). Fixes jitter and z-fighting far from the origin.",
            Recenter, PD("min_coord", "Coordinates above", 1e5));
        Reg("simplify", "Simplify meshes (reduce triangles)", "detail", false,
            "Reduces triangles with attribute-aware quadric edge collapse (UVs, normals and colours count in the error; seams slide along themselves; 'permissive' lets hard edges and flat shading collapse too and recomputes the normals afterwards, UV seams stay; 'rebake' simplifies the shape only, without the 2 % safety limit, and bakes the textures and vertex colours onto a new texture per geometry; tile borders can stay locked; nearby parts can merge) or fast top-down clustering (R-Simp), or meshoptimizer ('meshopt'; 'sloppy' for Fast, rough). Applies to the selected nodes, or to every geometry above the size limit.",
            SimplifyPass, PC("target", "Target: keep a share of the triangles, or stay within a distance", "keep", "keep", "within"),
            PD("ratio", "Keep this share of triangles", 0.5), PI("min_tris", "Only geometries with at least (triangles)", 10000),
            PC("method", "Method", "qem", "qem", "fast", "meshopt", "sloppy"), PC("seams", "UV / normal seams", "keep", "keep", "permissive", "rebake", "ignore"),
            PC("borders", "Open borders (terrain tile edges)", "match", "match", "lock", "slide"), PC("parts", "Nearby separate parts", "merge", "merge", "keep"),
            PD("max_error", "Max deviation in metres (scene units after load), converted per geometry into its local units; 0 = no limit. With max_error_pct: the upper limit of each part's share", 0.0),
            PD("max_error_pct", "Within: largest deviation in % of each part's own size, at most that % of the selected node's size (0 = use max_error)", 0.0), PD("crease", "Permissive / rebake: hard edge above (degrees)", 40.0),
            PI("rebake_texture", "Rebake: texture size (pixels)", 512),
            PB("protect_overlays", "Keep roads and decals on the ground (protect terrain under overlays; on by default, false = old behaviour)", true),
            PB("prune", "meshoptimizer: drop tiny separate parts within the error limit", false), PS("extra_fill", "Fill where a part has no extra unit / companion (white, black, luminance, 0-255)", "white"), PI("class_fill", "Class ID where a part has no class map", 0));
        Reg("generate_lod", "Generate LOD levels", "detail", false,
            "Heavy geometry (or the selection) gets an osg::LOD with simplified copies that are drawn when the object is far away. The full detail is kept for close-ups. Switching 'error' measures each level's largest deviation and switches it in where that deviation is at most the allowed pixels on screen (d = error x screen height / (2 tan(fov/2) x pixels)), so switches do not pop; levels that would never be seen are dropped. 'fixed' uses the distances below (x bounding radius). OpenFlight LOD records get a transition range of 10 % for image generators that blend.",
            GenerateLod, PI("min_tris", "Only geometries with at least (triangles)", 20000), PS("ratios", "Detail of the levels", "0.5, 0.25, 0.12, 0.06"),
            PC("switching", "Switch distances", "error", "error", "fixed"), PI("screen_height", "Error: screen height (pixels)", 1080),
            PD("fov", "Error: vertical field of view (degrees)", 45.0), PD("pixel_error", "Error: allowed change per switch (pixels)", 1.0),
            PS("distances", "Fixed: switch distances (x bounding radius)", "8, 25, 80, 200"), PC("method", "Method", "qem", "qem", "fast", "meshopt"),
            PC("far_level", "Farthest level", "simplify", "simplify", "proxy", "cards"), PI("proxy_texture", "Proxy texture size (pixels)", 512),
            PI("proxy_voxels", "Proxy voxel resolution (longest side)", 128),
            PC("proxy_parts", "Proxy parts: one hull for everything / each part its own hull (wheels, mirrors, moving parts stay separate)", "whole", "whole", "per_part"),
            PD("proxy_from", "Proxy: appears from this distance (metres; 0 = off, uses the ratios)", 0.0),
            PC("proxy_ground", "Proxy underside: keep / not seen from below / cut off (objects on the ground only)", "full", "full", "above", "cut"),
            PC("proxy_normals", "Proxy shading: from the hull / from the original (keeps curved bodies round)", "hull", "hull", "original"), PS("extra_fill", "Fill where a part has no extra unit / companion (white, black, luminance, 0-255)", "white"), PI("class_fill", "Class ID where a part has no class map", 0),
            PC("far_layout", "Farthest level cards: layout", "crossed4", CardLayout.Names));
        Reg("replace_cards", "Replace with billboard cards (trees, bushes)", "detail", false,
            "Each selected object is rendered from several sides into one alpha texture and replaced by vertical cards at the same place and size: 'crossed' = fixed planes through the centre (front and back each have their own picture; works in every renderer), 'billboard' = one osg::Billboard card that turns around the up axis towards the viewer (OpenFlight: axial billboard face). Select the foliage alone to keep a real trunk. 'layered' (vertical planes plus horizontal ones for crowns and ground cover) and 'box' (five or six faces) and the named layouts are new choices next to these; the defaults build the same cards as before. Z is up.",
            ReplaceCards, PC("mode", "Cards", "crossed", "crossed", "billboard", "layered", "box"), PI("planes", "Crossed: planes", 4), PI("texture", "Texture size (pixels)", 512),
            PB("share", "Bake identical / rigid copies once (per texture variant)", true), PS("extra_fill", "Fill where a part has no extra unit / companion (white, black, luminance, 0-255)", "white"), PI("class_fill", "Class ID where a part has no class map", 0),
            PC("layout", "Layout (named shortcut)", "custom", "custom", "billboard", "crossed4", "cross2", "star3", "pixyz", "ground", "aerial", "box"),
            PI("horizontal", "Horizontal planes (layered; billboard: crown planes)", 0),
            PS("heights", "Horizontal plane heights (share of the height, or auto)", "auto"),
            PC("slab", "Horizontal planes show", "full", "full", "slice"), PC("faces", "Horizontal planes: pictures", "both", "both", "top"),
            PC("back", "Back of vertical planes", "own", "own", "mirror"), PB("box_bottom", "Box: bake the bottom face", false),
            PC("cells", "Atlas cells", "equal", "equal", "by_area"), PC("padding", "Colour bleed into transparent texels", "8", "8", "full"),
            PC("alpha_mips", "Mipmaps of the alpha (coverage: only written into .dds)", "renderer", "renderer", "coverage"));
        Reg("replace_proxy", "Replace with a textured proxy", "detail", false,
            "Each selected object (a building, a vehicle seen from far) is replaced by one closed, simplified hull with one baked texture at the same place and size: one draw call. Hidden insides disappear. Underside 'cut' and 'above' are wrong for objects seen from below (aircraft, bridges).",
            ReplaceProxy,
            PC("parts", "Parts: one hull for everything / each part its own hull (wheels, mirrors, moving parts stay separate)", "whole", "whole", "per_part"),
            PC("target", "Target: keep a share of the triangles, or stay within a distance", "keep", "keep", "within"),
            PD("ratio", "Keep this share of triangles", 0.05), PD("max_error", "Within: largest deviation (metres)", 0.04),
            PD("max_error_px", "Within: largest deviation in pixels at the distance below (0 = use metres)", 0.0), PD("at_distance", "... seen from (metres)", 100.0),
            PC("ground", "Underside: keep / not seen from below / cut off (objects on the ground only)", "full", "full", "above", "cut"),
            PC("normals", "Shading: from the hull / from the original (keeps curved bodies round)", "hull", "hull", "original"),
            PD("crease", "Original shading: hard edge above (degrees)", 45.0),
            PI("texture", "Texture size (pixels)", 512), PI("voxels", "Voxel resolution (longest side); with Within: the upper limit", 128),
            PB("share", "Build identical / rigid copies once (per texture variant)", true), PS("extra_fill", "Fill where a part has no extra unit / companion (white, black, luminance, 0-255)", "white"), PI("class_fill", "Class ID where a part has no class map", 0));
        Reg("downscale_textures", "Downscale large textures", "textures", false,
            "Textures larger than the limit are halved until they fit. The resized images are written next to the saved scene (folder '<scene>_textures'); originals are not touched. Companion images (e.g. the infrared 'tree_ir.rgb' of 'tree.rgb') are resized with their texture to the same size and written under the same naming rule. Class maps ('*_Classes.png') are copied unchanged unless 'Resize class maps too' is on; then they are resized nearest-neighbour, never filtered.",
            Downscale, PI("max_size", "Largest side (pixels)", 2048), PB("resize_class_maps", "Resize class maps too", false),
            PB("keep_coverage", "Keep the leaf area of cut-out textures (alpha) when shrinking", false));
        // rearrange the graph but keep the picture and every triangle
        foreach (var name in new[] { "remove_redundant", "make_instances", "flatten_transforms", "recenter" })
            Passes[name] = Passes[name] with { Structure = true };
    }

    // ------------------------------------------------------------------------------ cleanup

    static string RemoveHidden(OptContext ctx)
    {
        long removed = 0;
        foreach (var n in ctx.ScopeNodes())
        {
            if (FixedChildCount(n) && n.Kind != Kind.Switch) continue;
            var hidden = n.Children.Where(c => c.NodeMask == 0).ToList();
            if (hidden.Count > 0) { removed += hidden.Count; ctx.RemoveMany(n, hidden); }
        }
        return Summ(removed, "hidden node(s) removed");
    }

    static string RemoveEmpty(OptContext ctx)
    {
        long removed = 0;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var n in ctx.ScopeNodes())
            {
                if (n.Kind is Kind.Lod or Kind.PagedLod or Kind.Proxy) continue;
                var empties = new List<Node>();
                foreach (var c in n.Children)
                {
                    bool empty = false;
                    if (c.Kind == Kind.Geometry && c.Geom != null && c.Geom.Optimizable)
                    {
                        var (t, l, p) = c.Geom.Counts();
                        empty = c.Geom.Prims.Count == 0 || c.Geom.NumVertices == 0 || t + l + p == 0;
                    }
                    else if (c.Kind is Kind.Group or Kind.Geode && c.Children.Count == 0 && c.ExternalNodes.Count == 0 && IsPlain(c))
                        empty = true;
                    if (empty) empties.Add(c);
                }
                if (empties.Count > 0) { removed += empties.Count; ctx.RemoveMany(n, empties); changed = true; }
            }
        }
        return Summ(removed, "empty node(s) removed");
    }

    static string RemoveRedundant(OptContext ctx)
    {
        bool keepNamed = ctx.PBool("remove_redundant", "keep_named");
        long removed = 0;
        var root = ctx.Scene.Root;
        foreach (var n in ctx.ScopeNodes())
        {
            if (ReferenceEquals(n, root)) continue;
            if (keepNamed && HasMeaningfulName(n)) continue;
            if (!IsPlain(n) || n.StateSet != null || n.ExternalNodes.Count > 0) continue;
            string cls = OsgClass(n);
            bool identity = n.Kind == Kind.Transform && (n.Matrix == null || Mat.IsIdentity(n.Matrix, 1e-5, 1e-12));
            bool singleGroup = cls == "Group" && n.Children.Count == 1;
            if (!(identity || singleGroup)) continue;
            foreach (var p in ctx.ParentsOf(n).ToList())
            {
                if (FixedChildCount(p) && n.Children.Count != 1) continue;
                if (p.Kind == Kind.Geode) continue;
                if (ctx.Replace(p, n, n.Children.ToList())) removed++;
            }
        }
        return Summ(removed, "redundant node(s) collapsed");
    }

    static string FlattenTransforms(OptContext ctx)
    {
        bool keepNamed = ctx.PBool("flatten_transforms", "keep_named");
        double maxCoord = ctx.PDouble("flatten_transforms", "max_coord");
        int done = 0;
        var depth = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        var order = new List<Node>();
        foreach (var r in ctx.Targets ?? new List<Node> { ctx.Scene.Root! })
        {
            var stack = new List<(Node, int)> { (r, 0) };
            while (stack.Count > 0)
            {
                var (x, dd) = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                if (depth.ContainsKey(x)) continue;
                depth[x] = dd;
                order.Add(x);
                stack.AddRange(ctx.Kids(x).Where(c => c.Kind != Kind.External).Select(c => (c, dd + 1)));
            }
        }
        var xforms = order.Where(n => n.Kind == Kind.Transform && n.Matrix != null && IsPlain(n)
                                      && OsgClass(n) is "MatrixTransform" or "PositionAttitudeTransform"
                                      && !(keepNamed && HasMeaningfulName(n)) && n.StateSet == null)
                          .Select((n, i) => (n, i)).OrderBy(x => -depth[x.n]).ThenBy(x => x.i).Select(x => x.n).ToList();
        foreach (var t in xforms)
        {
            var parents = ctx.ParentsOf(t);
            if (parents.Count != 1 || parents[0].Kind == Kind.Geode) continue;
            var sub = new List<Node>();
            bool ok = true;
            var stack = new List<Node>(t.Children);
            var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
            while (stack.Count > 0 && ok)
            {
                var x = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                if (!seen.Add(x)) continue;
                if (ctx.ParentsOf(x).Count != 1) { ok = false; break; }
                if (x.Kind == Kind.Geometry)
                {
                    if (x.Geom == null || !x.Geom.Optimizable || MeshOps.Instanced(x.Geom)) ok = false;
                    sub.Add(x);
                }
                else if (x.Kind == Kind.Geode && x.Center != null) ok = false;   // a billboard: drawables are relative to its position
                else if (x.Kind is Kind.Group or Kind.Geode or Kind.Switch || (x.Kind == Kind.Lod && x.Center == null))
                {
                    stack.AddRange(x.Children);
                    if (x.Matrix != null || x.ExternalNodes.Count > 0) ok = false;
                }
                else ok = false;
            }
            if (!ok) continue;
            var M = t.Matrix!;
            double[,] lin = { { M[0], M[1], M[2] }, { M[4], M[5], M[6] }, { M[8], M[9], M[10] } };
            var inv = Invert3(lin);
            if (inv == null) continue;
            var newPos = new List<double[]>();
            foreach (var g in sub)
            {
                var v = g.Geom!.Vertices!;
                var pv = new double[v.Count * 3];
                double maxNew = 0, maxOld = 0;
                for (int i = 0; i < v.Count; i++)
                {
                    double x = v.Get(i, 0), y = v.Comps > 1 ? v.Get(i, 1) : 0, z = v.Comps > 2 ? v.Get(i, 2) : 0;
                    var (px, py, pz) = Mat.Apply(M, x, y, z);
                    pv[3 * i] = px; pv[3 * i + 1] = py; pv[3 * i + 2] = pz;
                    maxNew = Math.Max(maxNew, Math.Max(Math.Abs(px), Math.Max(Math.Abs(py), Math.Abs(pz))));
                    for (int c = 0; c < v.Comps; c++) maxOld = Math.Max(maxOld, Math.Abs(v.Get(i, c)));
                }
                if (pv.Length > 0 && maxNew > maxCoord && maxCoord > maxOld + 1e-9) { ok = false; break; }
                newPos.Add(pv);
            }
            if (!ok) continue;
            bool mirror = Det3(lin) < 0;
            for (int gi = 0; gi < sub.Count; gi++)
            {
                var g = sub[gi];
                var geom = g.Geom!;
                if (mirror)
                {
                    geom = Normalized(geom).g;
                    foreach (var p in geom.Prims)
                        if (p.Mode == "TRIANGLES" && p.Indices != null)
                        {
                            var idx = (int[])p.Indices.Clone();
                            for (int i = 0; i + 2 < idx.Length; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
                            p.Indices = idx;
                        }
                }
                else geom = geom.ShallowCopy();
                geom.Vertices = geom.Vertices!.CopyWith(newPos[gi], 3);
                if (geom.Normals != null && geom.Normals.Binding != "off")
                {
                    var nrm = geom.Normals;
                    var nd = new double[nrm.Count * 3];
                    for (int i = 0; i < nrm.Count; i++)
                    {
                        double x = nrm.Get(i, 0), y = nrm.Comps > 1 ? nrm.Get(i, 1) : 0, z = nrm.Comps > 2 ? nrm.Get(i, 2) : 0;
                        // n' = n @ inv(lin) (row vector times the inverse = inverse transpose for column vectors)
                        double nx = x * inv[0, 0] + y * inv[1, 0] + z * inv[2, 0];
                        double ny = x * inv[0, 1] + y * inv[1, 1] + z * inv[2, 1];
                        double nz = x * inv[0, 2] + y * inv[1, 2] + z * inv[2, 2];
                        double ln = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (ln > 0) { nx /= ln; ny /= ln; nz /= ln; }
                        nd[3 * i] = nx; nd[3 * i + 1] = ny; nd[3 * i + 2] = nz;
                    }
                    geom.Normals = nrm.CopyWith(nd, 3);
                }
                SetGeom(g, geom);
            }
            ctx.Replace(parents[0], t, t.Children.ToList());
            done++;
        }
        return Summ(done, "transform(s) flattened");
    }

    static double Det3(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    static double[,]? Invert3(double[,] m)
    {
        double det = Det3(m);
        if (det == 0 || !double.IsFinite(det)) return null;
        var r = new double[3, 3];
        r[0, 0] = (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) / det;
        r[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / det;
        r[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / det;
        r[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) / det;
        r[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / det;
        r[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / det;
        r[2, 0] = (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) / det;
        r[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / det;
        r[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / det;
        return r;
    }

    // ------------------------------------------------------------------------------ sharing

    static string ShareDuplicates(OptContext ctx)
    {
        var msgs = new List<string>();
        var nodes = ctx.ScopeNodes();
        if (ctx.PBool("share_duplicates", "statesets"))
        {
            var texCanon = new Dictionary<string, Texture>();
            int replacedTex = 0;
            foreach (var n in nodes)
            {
                var ss = n.StateSet;
                if (ss == null) continue;
                for (int i = 0; i < ss.Textures.Count; i++)
                {
                    var t = ss.Textures[i];
                    if (t.Embedded || t.Path == null) continue;
                    string sig = t.Path + "\u0000" + (ObjSignature(t.Src as Obj) ?? "\u0001none");
                    if (!texCanon.TryGetValue(sig, out var c)) { texCanon[sig] = t; continue; }
                    if (!ReferenceEquals(c, t) && SwapTexture(ss, t, c)) { ss.Textures[i] = c; replacedTex++; }
                }
            }
            if (replacedTex > 0) msgs.Add($"{replacedTex} texture copies shared");
            var canon = new Dictionary<string, StateSet>();
            int replaced = 0;
            foreach (var n in nodes)
            {
                var ss = n.StateSet;
                if (ss == null || (ss.Src == null && ctx.Scene.Doc != null)) continue;
                string sig = Analyzer.StateSetSignature(ss);
                if (!canon.TryGetValue(sig, out var c)) { canon[sig] = ss; continue; }
                if (!ReferenceEquals(c, ss)) { n.StateSet = c; n.Mark("stateset"); replaced++; }
            }
            if (replaced > 0) msgs.Add($"{replaced} state set copies shared");
        }
        if (ctx.PBool("share_duplicates", "geometry"))
        {
            var canon = new Dictionary<string, Node>();
            var todo = new Dictionary<Node, Dictionary<Node, List<Node>>>(ReferenceEqualityComparer.Instance);
            var ssIds = new Dictionary<StateSet, int>(ReferenceEqualityComparer.Instance);
            string SsKey(StateSet? ss) => ss == null ? "-" : (ssIds.TryGetValue(ss, out var i) ? i : ssIds[ss] = ssIds.Count).ToString(CultureInfo.InvariantCulture);
            foreach (var n in nodes)
            {
                if (n.Kind != Kind.Geometry || n.Geom == null) continue;
                string key = n.Geom.ContentKey() + "|" + SsKey(n.StateSet) + "|" + (HasMeaningfulName(n) ? n.Name : "") + "|" + n.Cls;
                if (!canon.TryGetValue(key, out var c)) { canon[key] = n; continue; }
                if (ReferenceEquals(c, n)) continue;
                foreach (var p in ctx.ParentsOf(n))
                {
                    if (!todo.TryGetValue(p, out var map)) todo[p] = map = new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
                    map[n] = new List<Node> { c };
                }
            }
            int replaced = ApplyReplacements(ctx, todo);
            var gcanon = new Dictionary<string, Node>();
            var gtodo = new Dictionary<Node, Dictionary<Node, List<Node>>>(ReferenceEqualityComparer.Instance);
            var nodeIds = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
            string Nid(Node x) => (nodeIds.TryGetValue(x, out var i) ? i : nodeIds[x] = nodeIds.Count).ToString(CultureInfo.InvariantCulture);
            foreach (var n in ctx.ScopeNodes())
            {
                if (n.Kind != Kind.Geode || !IsPlain(n)) continue;
                string key = string.Join(",", n.Children.Select(Nid)) + "|" + SsKey(n.StateSet) + "|" + (HasMeaningfulName(n) ? n.Name : "");
                if (!gcanon.TryGetValue(key, out var c)) { gcanon[key] = n; continue; }
                if (ReferenceEquals(c, n)) continue;
                foreach (var p in ctx.ParentsOf(n))
                {
                    if (!gtodo.TryGetValue(p, out var map)) gtodo[p] = map = new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
                    map[n] = new List<Node> { c };
                }
            }
            int greplaced = ApplyReplacements(ctx, gtodo);
            if (replaced > 0) msgs.Add($"{replaced} geometry copies replaced by shared instances");
            if (greplaced > 0) msgs.Add($"{greplaced} geode copies shared");
        }
        return msgs.Count > 0 ? string.Join("; ", msgs) : "nothing to share";
    }

    static string? ObjSignature(Obj? obj)
    {
        if (obj == null) return null;
        var sw = new StringWriter();
        new TextOut("osgt", sw).WriteStmts(obj.Stmts.Where(s => s.Key is not ("Image" or "file" or "FileName")).ToList(), 0);
        return string.Join("\n", sw.ToString().Split('\n').SkipLast(1).Where(l => !l.Contains("UniqueID")));
    }

    /// <summary>Point the state set's document at the shared texture object.</summary>
    static bool SwapTexture(StateSet ss, Texture old, Texture nw)
    {
        if (ss.Src is not Obj so || old.Src is not Obj oo || nw.Src is not Obj no) return ss.Src == null;
        bool found = false;
        var stack = new Stack<List<Stmt>>();
        stack.Push(so.Stmts);
        while (stack.Count > 0)
        {
            foreach (var s in stack.Pop())
            {
                if (ReferenceEquals(s.Block, oo))
                {
                    DomJournal?.Add((s, s.Block));
                    s.Block = no;
                    found = true;
                }
                else if (s.Block is List<Stmt> l) stack.Push(l);
            }
        }
        return found;
    }

    // ------------------------------------------------------------------------------ vertex / index passes

    static HashSet<Node> AlreadyClean(List<Node> nodes, Func<GeomData, MeshOps.Stats, bool> test)
    {
        var o = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        foreach (var n in nodes)
        {
            var g = n.Geom!;
            if (g.Prims.Count == 0 || g.NumVertices == 0 || g.NumVertices > 4096) continue;
            var st = MeshOps.MeshStats(g);
            if (st != null && test(g, st)) o.Add(n);
        }
        return o;
    }

    static bool SimplePrims(GeomData g) =>
        g.Prims.All(p => p.Kind == "elements" && p.Mode is "TRIANGLES" or "LINES" or "POINTS") && g.Prims.Select(p => p.Mode).Distinct().Count() == g.Prims.Count;

    static string Weld(OptContext ctx)
    {
        double tol = ctx.PDouble("weld", "tolerance");
        long saved = 0, changed = 0;
        var nodes = ctx.Geometries();
        var skip = tol != 0 ? new HashSet<Node>(ReferenceEqualityComparer.Instance)
            : AlreadyClean(nodes, (g, st) => st.DupVertices == 0 && st.Unused == 0 && SimplePrims(g));
        foreach (var n in nodes)
        {
            var g = n.Geom!;
            if (g.Prims.Count == 0 || g.NumVertices == 0 || skip.Contains(n)) continue;
            var (nw, info) = Normalized(g, weld: true, tol: tol);
            if (info.VertsAfter < info.VertsBefore || g.DrawCalls > nw.Prims.Count || g.Prims.Any(p => p.Kind != "elements"))
            {
                SetGeom(n, nw);
                saved += info.VertsBefore - info.VertsAfter;
                changed++;
            }
        }
        return Summ(changed, "geometries re-indexed", $"{PyFormat.Int(saved)} vertices removed");
    }

    static string Clean(OptContext ctx)
    {
        long tris = 0, verts = 0, changed = 0;
        var nodes = ctx.Geometries();
        var skip = AlreadyClean(nodes, (g, st) => !(st.Degenerate != 0 || st.DuplicateTris != 0 || st.Unused != 0 || st.DegenerateLines != 0));
        foreach (var n in nodes)
        {
            var g = n.Geom!;
            if (g.Prims.Count == 0 || skip.Contains(n)) continue;
            var (nw, info) = Normalized(g, dropDegenerate: true, dedupeTris: true);
            long dt = info.TrisBefore - info.TrisAfter, dv = info.VertsBefore - info.VertsAfter;
            if (dt != 0 || dv != 0)
            {
                SetGeom(n, nw);
                tris += dt; verts += dv; changed++;
            }
        }
        return Summ(changed, "geometries cleaned", $"{PyFormat.Int(tris)} triangles, {PyFormat.Int(verts)} vertices removed");
    }

    static string Triangulate(OptContext ctx)
    {
        long changed = 0, calls = 0;
        foreach (var n in ctx.Geometries())
        {
            var g = n.Geom!;
            bool simple = SimplePrims(g) && g.Prims.All(p => p.IndexType == MeshOps.SmallestIndexType(g.NumVertices));
            if (simple || g.Prims.Count == 0) continue;
            long before = g.DrawCalls;
            var (nw, _) = Normalized(g, dropUnused: false);
            SetGeom(n, nw);
            calls += before - nw.DrawCalls;
            changed++;
        }
        return Summ(changed, "geometries converted", $"{PyFormat.Int(calls)} draw calls saved");
    }

    static string Compact(OptContext ctx)
    {
        long changed = 0;
        foreach (var n in ctx.Geometries())
        {
            var g = n.Geom!;
            var a = g.Colors;
            bool did = false;
            if (a != null && a.Binding == "vertex" && a.Count > 1)
            {
                bool same = true;
                for (int r = 1; r < a.Count && same; r++)
                    for (int j = 0; j < a.Comps; j++) if (a.Data[r * a.Comps + j] != a.Data[j]) { same = false; break; }
                if (same)
                {
                    g.Colors = new VArray(a.Data[..a.Comps], a.Comps, a.Type, "overall", a.Normalize);
                    did = true;
                }
            }
            if (did) { n.Mark("geometry"); changed++; }
        }
        return Summ(changed, "geometries compacted");
    }

    static string FloatArrays(OptContext ctx)
    {
        long changed = 0;
        var map = new Dictionary<string, string> { ["Vec2dArray"] = "Vec2Array", ["Vec3dArray"] = "Vec3Array", ["Vec4dArray"] = "Vec4Array", ["DoubleArray"] = "FloatArray" };
        foreach (var n in ctx.Geometries())
        {
            bool did = false;
            foreach (var (_, a) in n.Geom!.Arrays())
            {
                if (!map.TryGetValue(a.Type, out var t)) continue;
                a.Type = t;
                a.Data = a.Data.Select(v => (double)(float)v).ToArray();
                did = true;
            }
            if (did) { n.Mark("geometry"); changed++; }
        }
        return Summ(changed, "geometries converted to float");
    }

    static string VertexCache(OptContext ctx)
    {
        int minTris = ctx.PInt("vertex_cache", "min_tris");
        long changed = 0;
        double beforeSum = 0, afterSum = 0;
        foreach (var n in ctx.Geometries(minTris))
        {
            var g = n.Geom!;
            var triPrims = g.Prims.Where(p => p.Mode == "TRIANGLES" && p.Kind == "elements").ToList();
            if (triPrims.Count != 1 || g.Prims.Count > 3) g = Normalized(g, dropUnused: false).g;
            var tris = g.Triangles();
            int m = tris.Length / 3;
            if (m < minTris) continue;
            double a0 = MeshOps.Acmr(tris);
            var order = Simplify.Tipsify(tris, g.NumVertices);
            var tris2 = new int[tris.Length];
            for (int i = 0; i < order.Length; i++) { tris2[3 * i] = tris[3 * order[i]]; tris2[3 * i + 1] = tris[3 * order[i] + 1]; tris2[3 * i + 2] = tris[3 * order[i] + 2]; }
            double a1 = MeshOps.Acmr(tris2);
            if (a1 >= a0 - 0.02) continue;
            // vertex fetch order: first use
            int nv = g.NumVertices;
            var seenV = new bool[nv];
            var keepL = new List<int>(nv);
            foreach (var v in tris2) if (!seenV[v]) { seenV[v] = true; keepL.Add(v); }
            for (int v = 0; v < nv; v++) if (!seenV[v]) keepL.Add(v);
            var keep = keepL.ToArray();
            var remap = new int[nv];
            for (int i = 0; i < keep.Length; i++) remap[keep[i]] = i;
            var lines = g.Lines();
            var pts = g.Points();
            var nw = Rebuild(g, keep, tris2.Select(i => remap[i]).ToArray(), lines.Select(i => remap[i]).ToArray(), pts.Select(i => remap[i]).ToArray());
            SetGeom(n, nw);
            changed++;
            beforeSum += a0; afterSum += a1;
        }
        if (changed == 0) return "no geometry needed reordering";
        return Summ(changed, "geometries reordered", $"ACMR {PyFormat.Fixed(beforeSum / changed, 2)} -> {PyFormat.Fixed(afterSum / changed, 2)}");
    }

    // ------------------------------------------------------------------------------ draw calls

    static string Merge(OptContext ctx)
    {
        int maxTris = ctx.PInt("merge_geometries", "max_tris_each");
        int maxV = ctx.PInt("merge_geometries", "max_vertices");
        long mergedTotal = 0, created = 0;
        foreach (var parent in ctx.ScopeNodes())
        {
            if (parent.Kind is Kind.Lod or Kind.PagedLod or Kind.Switch or Kind.Proxy or Kind.Geometry) continue;
            var cands = new List<(Node g, Node? holder)>();
            foreach (var c in parent.Children)
            {
                if ((c.Kind == Kind.Geometry && parent.Kind == Kind.Geode) || (c.Kind == Kind.Geometry && parent.Kind == Kind.Group)) cands.Add((c, null));
                else if (c.Kind == Kind.Geode && c.Children.Count == 1 && c.Matrix == null && IsPlain(c) && ctx.ParentsOf(c).Count == 1 && parent.Kind != Kind.Geode)
                    cands.Add((c.Children[0], c));
            }
            var groups = new Dictionary<(object?, object?, string), List<(Node, Node?)>>();
            var order = new List<(object?, object?, string)>();
            foreach (var (gnode, holder) in cands)
            {
                var g = gnode.Geom;
                if (g == null || !g.Optimizable || MeshOps.Instanced(g) || g.Vertices == null) continue;
                if (ctx.ParentsOf(gnode).Count != 1) continue;
                if (g.Counts().tris > maxTris) continue;
                string layout = string.Join(";", g.Arrays().OrderBy(x => x.name, StringComparer.Ordinal)
                    .Select(x => $"{x.name},{x.a.Type},{(x.a.Binding is "vertex" or "overall")}"));
                var hss = holder?.StateSet;
                // OpenFlight: faces with other IR / material codes stay apart (one merged geometry gets one set of codes)
                var codes = FltFaceCodes.Faces(gnode, null).Select(f => f.codes).Distinct().Select(c => c.ToString()).OrderBy(x => x, StringComparer.Ordinal);
                var key = ((object?)gnode.StateSet, (object?)hss, layout + "|" + string.Join(";", codes));
                if (!groups.TryGetValue(key, out var l)) { groups[key] = l = new List<(Node, Node?)>(); order.Add(key); }
                l.Add((gnode, holder));
            }
            foreach (var key in order)
            {
                var items = groups[key];
                if (items.Count < 2) continue;
                var batch = new List<(Node, Node?)>();
                long nverts = 0;
                foreach (var (gnode, holder) in items)
                {
                    int nv = gnode.Geom!.NumVertices;
                    if (batch.Count > 0 && nverts + nv > maxV)
                    {
                        created += MergeBatch(ctx, parent, batch);
                        mergedTotal += batch.Count;
                        batch = new List<(Node, Node?)>();
                        nverts = 0;
                    }
                    batch.Add((gnode, holder));
                    nverts += nv;
                }
                if (batch.Count >= 2)
                {
                    created += MergeBatch(ctx, parent, batch);
                    mergedTotal += batch.Count;
                }
            }
        }
        return Summ(mergedTotal, "geometries merged", $"into {created}");
    }

    static int MergeBatch(OptContext ctx, Node parent, List<(Node g, Node? holder)> batch)
    {
        var geoms = batch.Select(b => Normalized(b.g.Geom!, dropUnused: false).g).ToList();
        var names = geoms[0].Arrays().Select(x => x.name).ToList();
        var o = new GeomData();
        var tris = new List<int>(); var lines = new List<int>(); var pts = new List<int>();
        bool hasT = false, hasL = false, hasP = false;
        int offset = 0;
        var cols = names.ToDictionary(n => n, _ => new List<double>());
        var compsOf = new Dictionary<string, int>();
        foreach (var g in geoms)
        {
            int nv = g.NumVertices;
            var arrs = g.Arrays().ToDictionary(x => x.name, x => x.a);
            foreach (var name in names)
            {
                var a = arrs[name];
                compsOf.TryAdd(name, a.Comps);
                if (a.Binding == "overall" || a.Count != nv)
                    for (int i = 0; i < nv; i++) cols[name].AddRange(a.Data.AsSpan(0, a.Comps).ToArray());
                else cols[name].AddRange(a.Data);
            }
            foreach (var p in g.Prims)
            {
                var idx = (p.Indices ?? Array.Empty<int>()).Select(x => x + offset);
                if (p.Mode == "TRIANGLES") { tris.AddRange(idx); hasT = true; }
                else if (p.Mode == "LINES") { lines.AddRange(idx); hasL = true; }
                else { pts.AddRange(idx); hasP = true; }
            }
            offset += nv;
        }
        var first = geoms[0].Arrays().ToDictionary(x => x.name, x => x.a);
        foreach (var name in names)
        {
            var a = first[name];
            SetArray(o, name, new VArray(cols[name].ToArray(), compsOf[name], a.Type, "vertex", a.Normalize));
        }
        string it = MeshOps.SmallestIndexType(offset);
        if (hasT) o.Prims.Add(new Primitive("TRIANGLES", "elements", indices: tris.ToArray(), indexType: it));
        if (hasL) o.Prims.Add(new Primitive("LINES", "elements", indices: lines.ToArray(), indexType: it));
        if (hasP) o.Prims.Add(new Primitive("POINTS", "elements", indices: pts.ToArray(), indexType: it));
        var (firstNode, firstHolder) = batch[0];
        var node = new Node(Kind.Geometry, "osg::Geometry", $"merged ({batch.Count})") { Geom = o, StateSet = firstNode.StateSet };
        node.Mark("geometry");
        FltFaceCodes.Carry(node, batch.Select(b => (b.g, 1.0)), ctx.Scene, null);
        if (firstHolder == null)
        {
            var mapping = new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
            foreach (var (gn, _) in batch.Skip(1)) mapping[gn] = new List<Node>();
            mapping[firstNode] = new List<Node> { node };
            ctx.ReplaceMany(parent, mapping);
        }
        else
        {
            ctx.Replace(firstHolder, firstNode, new[] { node });
            ctx.RemoveMany(parent, batch.Skip(1).Select(b => b.holder!));
        }
        return 1;
    }

    // ------------------------------------------------------------------------------ precision

    static string Recenter(OptContext ctx)
    {
        double lim = ctx.PDouble("recenter", "min_coord");
        int done = 0;
        var handled = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        foreach (var gnode in ctx.Geometries())
        {
            var bb = gnode.Geom!.BBox();
            if (bb == null || Math.Max(bb.Value.lo.Max(Math.Abs), bb.Value.hi.Max(Math.Abs)) < lim) continue;
            var parents = ctx.ParentsOf(gnode);
            if (parents.Count != 1) continue;
            var holder = parents[0].Kind == Kind.Geode ? parents[0] : gnode;
            if (handled.Contains(holder)) continue;
            var hp = ctx.ParentsOf(holder);
            if (hp.Count != 1 || (!ReferenceEquals(holder, gnode) && ctx.ParentsOf(holder).Count != 1)) continue;
            var drawables = holder.Kind == Kind.Geode ? holder.Children.ToList() : new List<Node> { gnode };
            if (drawables.Any(d => d.Kind != Kind.Geometry || d.Geom == null || !d.Geom.Optimizable || ctx.ParentsOf(d).Count != 1)) continue;
            var boxes = drawables.Select(d => d.Geom!.BBox()).Where(b => b != null).Select(b => b!.Value).ToList();
            int dim = boxes[0].lo.Length;
            var center = new double[dim];
            for (int c = 0; c < dim; c++)
                center[c] = Math.Round((boxes.Min(b => b.lo[c]) + boxes.Max(b => b.hi[c])) / 2.0, MidpointRounding.ToEven);
            foreach (var d in drawables)
            {
                var v = d.Geom!.Vertices!;
                var nd = new double[v.Data.Length];
                for (int i = 0; i < v.Count; i++) for (int c = 0; c < v.Comps; c++) nd[i * v.Comps + c] = v.Data[i * v.Comps + c] - (c < dim ? center[c] : 0);
                var ng = d.Geom.ShallowCopy();
                ng.Vertices = v.CopyWith(nd);
                d.Geom = ng;
                d.Mark("geometry");
            }
            var xf = new Node(Kind.Transform, "osg::MatrixTransform", "recentered") { Matrix = Mat.Identity() };
            for (int c = 0; c < Math.Min(3, dim); c++) xf.Matrix[c * 4 + 3] = center[c];
            xf.Children = new List<Node> { holder };
            ctx.Replace(hp[0], holder, new[] { xf });
            handled.Add(holder);
            done++;
        }
        return Summ(done, "geometry group(s) re-centered");
    }

    // ------------------------------------------------------------------------------ detail reduction

    static double Rq(double v) { double r = Math.Round(v, MidpointRounding.ToEven); return r == 0 ? 0.0 : r; }

    public sealed class SimplifyInfo
    {
        public string? Reason; public List<string> Notes = new();
        /// <summary>Within left unchanged: the lowest deviation reached (metres, world) and the measured attempts.</summary>
        internal double? WithinDev; internal int WithinAttempts;
        /// <summary>meshoptimizer: its own error estimate of the (first) result, local units.</summary>
        internal double? Estimate;
        /// <summary>Benchmark hooks (MeshoptComparisonTests), input only: meshopt's Permissive flag with seams "keep", and
        /// other attribute weights (texture coordinates, normals) instead of Decimate's. Not set anywhere else.</summary>
        internal bool MeshoptPermissive; internal (double Uv, double Normals)? Weights;
        /// <summary>Benchmark hook, input only: meshopt's MaxRelativeError for Keep (0 = no cap, purely count-driven).</summary>
        internal double? MeshoptMaxRelativeError;
    }

    /// <summary>The simplify note for meshoptimizer with borders Match (R3-D2); Try puts the same line on its verdict.</summary>
    public const string MeshoptMatchNote = "meshoptimizer: tile edges kept (match needs the standard simplifier)";

    /// <summary>New geometries with about ratio x triangles (one per ratio, null where nothing could be done).
    /// <paramref name="keep"/> (local coordinates of g): vertices where it is true do not move (Decimate.Options.Lock;
    /// terrain under overlays).</summary>
    public static List<GeomData?> SimplifyGeom(GeomData g, IList<double> ratios, string method = "qem", Action<string>? progress = null,
        int minTris = 64, string seams = "keep", SimplifyInfo? info = null, string borders = "match", string parts = "merge", double maxError = 0,
        double crease = 40.0, Func<double, double, double, bool>? keep = null, bool prune = false)
    {
        info ??= new SimplifyInfo();
        var (bas, _) = Normalized(g, weld: true);
        // permissive: normals are left out (vertices that only differ in their normal become one), so hard edges and
        // flat shading do not block collapses; the result gets new normals with a crease angle
        VArray? normals = null;
        if (seams == "permissive" && bas.Normals is { Binding: "vertex" } nb && nb.Count == bas.NumVertices)
        {
            normals = nb;
            var noN = bas.ShallowCopy();
            noN.Normals = null;
            (bas, _) = Normalized(noN, weld: true);
        }
        var tris = bas.Triangles();
        if (tris.Length / 3 < minTris)
        {
            info.Reason = $"too small (< {minTris} triangles)";
            return ratios.Select(_ => (GeomData?)null).ToList();
        }
        if (seams == "ignore")
        {
            // one vertex per position (attributes of the first vertex at that position)
            var p0 = Pos3(bas.Vertices!);
            int n0 = bas.NumVertices;
            double scale = 0;
            foreach (var v in p0) scale = Math.Max(scale, Math.Abs(v));
            if (scale == 0) scale = 1.0;
            double q = scale * 1e-9;
            var rows = new double[n0 * 3];
            for (int i = 0; i < p0.Length; i++) rows[i] = Rq(p0[i] / q);
            var (first, inv) = Simplify.UniqueRowsNumeric(rows, n0, 3);
            var tl = new List<int>(tris.Length);
            for (int t = 0; t < tris.Length / 3; t++)
            {
                int a = inv[tris[3 * t]], b = inv[tris[3 * t + 1]], c = inv[tris[3 * t + 2]];
                if (a != b && b != c && a != c) { tl.Add(a); tl.Add(b); tl.Add(c); }
            }
            tris = tl.ToArray();
            var sub = new GeomData();
            foreach (var (name, a) in bas.Arrays())
                SetArray(sub, name, a.Binding == "vertex" && a.Count == bas.NumVertices ? Gather(a, first) : a.CopyWith(a.Data));
            sub.Prims = new List<Primitive> { new("TRIANGLES", "elements", indices: tris) };
            bas = sub;
        }
        int m = tris.Length / 3;
        var targets = ratios.Select(r => Math.Max((int)(m * r), 4)).ToList();
        var pos = Pos3(bas.Vertices!);
        int nvb = bas.NumVertices;
        var attrArrays = bas.Arrays().Where(x => x.name != "vertices" && x.a.Binding == "vertex" && x.a.Count == nvb).ToList();
        int k = attrArrays.Sum(x => x.a.Comps);
        double[]? attrs = null;
        if (attrArrays.Count > 0)
        {
            attrs = new double[nvb * k];
            int off = 0;
            foreach (var (_, a) in attrArrays)
            {
                for (int i = 0; i < nvb; i++) for (int j = 0; j < a.Comps; j++) attrs[i * k + off + j] = a.Data[i * a.Comps + j];
                off += a.Comps;
            }
        }
        List<Simplify.Result> results;
        if (method is "fast" or "cluster")   // "cluster": name used by older settings
            results = targets.Select(tg => RSimp.Run(pos, tris, tg, attrs, k)).ToList();
        else if (method is "meshopt" or "sloppy")
        {
            // meshoptimizer: Lock and Match both keep the borders (it has no Match thinning); sloppy = grid clustering
            // that still keeps the locked vertices
            if (!MeshoptNative.Available) { info.Reason = MeshoptNative.Unavailable; return ratios.Select(_ => (GeomData?)null).ToList(); }
            var errs = new List<double>();
            results = MeshoptBackend.Run(pos, tris, targets, attrs, k, new MeshoptBackend.Options
            {
                Borders = borders == "slide" ? BorderMode.Slide : BorderMode.Lock, MaxError = maxError > 0 ? maxError : null,
                AttrWeights = AttrWeights(attrArrays, info.Weights), Lock = KeepMask(pos, keep), Prune = prune, Sloppy = method == "sloppy",
                Permissive = info.MeshoptPermissive, MaxRelativeError = info.MeshoptMaxRelativeError ?? 0.02,
            }, errs);
            if (errs.Count > 0) info.Estimate = errs[0];
        }
        else
        {
            results = Decimate.Run(pos, tris, targets, attrs, k, new Decimate.Options
            {
                Borders = borders == "lock" ? BorderMode.Lock : borders == "slide" ? BorderMode.Slide : BorderMode.Match, MergeParts = parts == "merge", MaxError = maxError > 0 ? maxError : null,
                AttrWeights = AttrWeights(attrArrays), Progress = progress, Lock = KeepMask(pos, keep),
            });
        }
        var outl = new List<GeomData?>();
        foreach (var r in results)
        {
            if (r.T.Length / 3 > m * 0.97)
            {
                info.Reason = maxError > 0 ? "every collapse would exceed the max deviation" : "no edge could be collapsed without visible damage";
                outl.Add(null);
            }
            else
            {
                var o = Assemble(bas, r);
                if (normals != null) o = WithCreaseNormals(o, normals, crease, FacesAgreeWithNormals(g));
                outl.Add(o);
            }
        }
        return outl;
    }

    /// <summary>Attribute weights per channel: a difference of 1 costs like weight x bounding diagonal (texture
    /// coordinates 1, normals 0.25, everything else 0.5).</summary>
    static double[] AttrWeights(List<(string name, VArray a)> arrays, (double Uv, double Normals)? other = null)
    {
        double uv = other?.Uv ?? 1.0, nrm = other?.Normals ?? 0.25;
        var weights = new List<double>();
        foreach (var (name, a) in arrays)
            for (int j = 0; j < a.Comps; j++)
                weights.Add(name.StartsWith("texcoord", StringComparison.OrdinalIgnoreCase) ? uv : name == "normals" ? nrm : 0.5);
        return weights.ToArray();
    }

    /// <summary>Per vertex (positions x, y, z): true where <paramref name="keep"/> is; null without a predicate.</summary>
    static bool[]? KeepMask(double[] pos, Func<double, double, double, bool>? keep)
    {
        if (keep == null) return null;
        var m = new bool[pos.Length / 3];
        for (int i = 0; i < m.Length; i++) m[i] = keep(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]);
        return m;
    }

    /// <summary>True when the input's normals mostly point to the side the triangles' winding says (the usual case).</summary>
    static bool FacesAgreeWithNormals(GeomData g)
    {
        if (g.Normals is not { Binding: "vertex" } nrm || nrm.Count != g.NumVertices || g.Vertices == null) return true;
        var P = Pos3(g.Vertices);
        var T = g.Triangles();
        double s = 0;
        int c = nrm.Comps;
        for (int t = 0; t < T.Length / 3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], d = T[3 * t + 2];
            double ux = P[3 * b] - P[3 * a], uy = P[3 * b + 1] - P[3 * a + 1], uz = P[3 * b + 2] - P[3 * a + 2];
            double vx = P[3 * d] - P[3 * a], vy = P[3 * d + 1] - P[3 * a + 1], vz = P[3 * d + 2] - P[3 * a + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            foreach (int v in new[] { a, b, d })
                s += nx * nrm.Data[v * c] + (c > 1 ? ny * nrm.Data[v * c + 1] : 0) + (c > 2 ? nz * nrm.Data[v * c + 2] : 0);
        }
        return s >= 0;
    }

    /// <summary>Per-vertex normals from the faces: smooth across edges flatter than the crease angle, hard (split
    /// vertices) across sharper ones. Faces meet by position, so UV seams do not make edges hard.</summary>
    public static GeomData WithCreaseNormals(GeomData o, VArray like, double creaseDeg, bool outward = true)
    {
        var tris = o.Triangles();
        int nv = o.NumVertices;
        var P = Pos3(o.Vertices!);
        var posId = new int[nv];
        var ids = new Dictionary<(double, double, double), int>();
        var PP = new List<double>();
        for (int i = 0; i < nv; i++)
        {
            var key = (P[3 * i], P[3 * i + 1], P[3 * i + 2]);
            if (!ids.TryGetValue(key, out int id)) { ids[key] = id = ids.Count; PP.Add(P[3 * i]); PP.Add(P[3 * i + 1]); PP.Add(P[3 * i + 2]); }
            posId[i] = id;
        }
        var TP = tris.Select(i => posId[i]).ToArray();
        var corner = Proxy.CornerNormals(PP.ToArray(), TP, Math.Cos(creaseDeg * Math.PI / 180));
        if (!outward) for (int i = 0; i < corner.Length; i++) corner[i] = -corner[i];
        // one output vertex per (input vertex, normal)
        var keep = new List<int>();
        var nd = new List<double>();
        var first = new int[nv];
        Array.Fill(first, -1);
        var split = new Dictionary<(int, long, long, long), int>();
        var nt = new int[tris.Length];
        // zero-area slivers have no normal of their own (they average every neighbour): their corners take a normal the
        // vertex already got from a real face, so they come last
        var flat = new bool[tris.Length / 3];
        for (int t = 0; t < flat.Length; t++)
        {
            int a = TP[3 * t], b = TP[3 * t + 1], d = TP[3 * t + 2];
            double ux = P[3 * tris[3 * t + 1]] - P[3 * tris[3 * t]], uy = P[3 * tris[3 * t + 1] + 1] - P[3 * tris[3 * t] + 1], uz = P[3 * tris[3 * t + 1] + 2] - P[3 * tris[3 * t] + 2];
            double vx = P[3 * tris[3 * t + 2]] - P[3 * tris[3 * t]], vy = P[3 * tris[3 * t + 2] + 1] - P[3 * tris[3 * t] + 1], vz = P[3 * tris[3 * t + 2] + 2] - P[3 * tris[3 * t] + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            flat[t] = a == b || b == d || a == d || nx * nx + ny * ny + nz * nz == 0;
        }
        foreach (int c in Enumerable.Range(0, tris.Length).OrderBy(c => flat[c / 3] ? 1 : 0))
        {
            int v = tris[c];
            if (flat[c / 3] && first[v] >= 0) { nt[c] = first[v]; continue; }
            double x = corner[3 * c], y = corner[3 * c + 1], z = corner[3 * c + 2];
            var key = (v, (long)Math.Round(x * 1e4), (long)Math.Round(y * 1e4), (long)Math.Round(z * 1e4));
            if (!split.TryGetValue(key, out int w))
            {
                split[key] = w = keep.Count;
                keep.Add(v);
                nd.Add(x); nd.Add(y); nd.Add(z);
                if (first[v] < 0) first[v] = w;
            }
            nt[c] = w;
        }
        int[] Other(int[] idx)
        {
            var r = new int[idx.Length];
            for (int i = 0; i < idx.Length; i++)
            {
                int v = idx[i];
                if (first[v] < 0) { first[v] = keep.Count; keep.Add(v); nd.Add(0); nd.Add(0); nd.Add(1); }
                r[i] = first[v];
            }
            return r;
        }
        var lines = Other(o.Lines());
        var pts = Other(o.Points());
        var res = Rebuild(o, keep.ToArray(), nt, lines, pts);
        res.Normals = new VArray(nd.ToArray(), 3, like.Comps == 3 ? like.Type : "Vec3Array", "vertex", like.Normalize);
        return res;
    }

    static double[] Pos3(VArray a)
    {
        if (a.Comps == 3) return a.Data;
        var o = new double[a.Count * 3];
        for (int i = 0; i < a.Count; i++) for (int c = 0; c < Math.Min(3, a.Comps); c++) o[3 * i + c] = a.Data[i * a.Comps + c];
        return o;
    }

    static GeomData Assemble(GeomData bas, Simplify.Result r)
    {
        var o = new GeomData();
        o.Vertices = bas.Vertices!.CopyWith(r.V, 3);
        int col = 0;
        int nv = bas.NumVertices;
        foreach (var (name, a) in bas.Arrays())
        {
            if (name == "vertices") continue;
            VArray na;
            if (a.Binding == "vertex" && a.Count == nv && r.A != null)
            {
                int w = a.Comps;
                int cnt = r.Kept.Length;
                var d = new double[cnt * w];
                for (int i = 0; i < cnt; i++) for (int j = 0; j < w; j++) d[i * w + j] = r.A[i * r.K + col + j];
                col += w;
                if (name == "normals")
                    for (int i = 0; i < cnt; i++)
                    {
                        double s = 0;
                        for (int j = 0; j < w; j++) s += d[i * w + j] * d[i * w + j];
                        double ln = Math.Sqrt(s);
                        if (ln > 0) for (int j = 0; j < w; j++) d[i * w + j] /= ln;
                    }
                na = a.CopyWith(d);
            }
            else na = a.CopyWith((double[])a.Data.Clone());
            SetArray(o, name, na);
        }
        string it = MeshOps.SmallestIndexType(r.V.Length / 3);
        if (r.T.Length > 0) o.Prims.Add(new Primitive("TRIANGLES", "elements", indices: r.T, indexType: it));
        // lines / points refer to original vertices: keep those that survived
        var remap = new int[nv];
        Array.Fill(remap, -1);
        for (int i = 0; i < r.Kept.Length; i++) remap[r.Kept[i]] = i;
        foreach (var (mode, idx, per) in new[] { ("LINES", bas.Lines(), 2), ("POINTS", bas.Points(), 1) })
        {
            if (idx.Length == 0) continue;
            var keep = new List<int>();
            for (int i = 0; i + per - 1 < idx.Length; i += per)
            {
                bool all = true;
                for (int j = 0; j < per; j++) if (remap[idx[i + j]] < 0) all = false;
                if (all) for (int j = 0; j < per; j++) keep.Add(remap[idx[i + j]]);
            }
            if (keep.Count > 0) o.Prims.Add(new Primitive(mode, "elements", indices: keep.ToArray(), indexType: it));
        }
        return o;
    }

    static string SimplifyPass(OptContext ctx)
    {
        double ratio = ctx.PDouble("simplify", "ratio");
        int minTris = ctx.Targets != null ? 0 : ctx.PInt("simplify", "min_tris");
        string method = ctx.PStr("simplify", "method");
        string seams = ctx.PStr("simplify", "seams");
        string borders = ctx.PStr("simplify", "borders");
        string parts = ctx.PStr("simplify", "parts");
        bool prune = ctx.PBool("simplify", "prune");
        double maxErrorM = ctx.PDouble("simplify", "max_error");
        double maxErrorPct = ctx.PDouble("simplify", "max_error_pct");
        double crease = ctx.PDouble("simplify", "crease");
        int rebakeTex = Math.Clamp(ctx.PInt("simplify", "rebake_texture"), 16, 8192);
        var fill = seams == "rebake" ? ExtraFill.Parse(ctx.PStr("simplify", "extra_fill")) : ExtraFill.White;
        var classFill = ExtraFill.ClassFill(seams == "rebake" ? ctx.PInt("simplify", "class_fill") : 0);
        var imageCache = new Dictionary<string, RawImage?>();
        long before = 0, after = 0, done = 0, baked = 0;
        var notes = new List<string>();
        var skipped = new Dictionary<string, int>();
        var skippedOrder = new List<string>();
        var worst = new Dictionary<string, (double dev, int n)>();
        // Within: no share of the triangles, the error limit stops the collapse (the 4-triangle floor stays). Fast, rough
        // has no error limit and keeps the ratio instead (sloppy, its meshoptimizer twin, as well).
        bool within = ctx.PStr("simplify", "target") == "within";
        if (within && method is "fast" or "cluster" or "sloppy")
        {
            within = false;
            maxErrorM = maxErrorPct = 0;
            notes.Add($"Fast, rough has no error limit: kept {PyFormat.G(ratio)} instead");
        }
        else if (within)
        {
            if (maxErrorM <= 0 && maxErrorPct <= 0) throw new ArgumentException("simplify: Within needs max_error or max_error_pct above 0");
            ratio = 0;
        }
        // meshoptimizer cannot thin tile edges the way Match does: it keeps them (as Lock); rebake always uses the
        // standard simplifier
        if (method is "meshopt" or "sloppy" && borders == "match" && seams != "rebake") notes.Add(MeshoptMatchNote);
        var geoms = ctx.Geometries(minTris);
        // terrain under overlays (roads, decals) keeps its vertices; candidates = every geometry of the scene, placed in
        // the world once per run (their faces are only looked at where the boxes overlap)
        // (fast, rough clusters by grid cell and cannot keep vertices: the overlays are still looked for, so the
        // message can say they were not protected; meshoptimizer's sloppy keeps locked vertices, so it protects them)
        bool lookForOverlays = ctx.PBool("simplify", "protect_overlays");
        bool protect = lookForOverlays && method is not ("fast" or "cluster");
        List<Overlays.Placed>? placed = null;
        long protectedGeoms = 0;
        // every drawn geometry with its full world matrix (also the edges from an external reference into its file):
        // the one source of places for the error frames and the overlay candidates, as in Try
        List<(Node node, double[] world, StateSet? ss)>? drawn = null;
        List<(Node node, double[] world, StateSet? ss)> Drawn() => drawn ??= SoftRender.GeometryItems(ctx.Scene.Root!);
        // the limit is given in world metres (or % of the world size); each geometry gets it in its own local units
        var frames = within || maxErrorM > 0 ? ErrorFrames(ctx, geoms, Drawn()) : null;
        for (int i = 0; i < geoms.Count; i++)
        {
            var n = geoms[i];
            long t0 = n.Geom!.Counts().tris;
            ctx.Progress($"simplifying {i + 1}/{geoms.Count} ({PyFormat.Int(t0)} triangles) ...");
            var info = new SimplifyInfo();
            double maxError = 0, toLocal = 1;
            if (frames != null)
            {
                (toLocal, double diag) = frames[n];
                // "% of size": of each part's own size, at most that share of the node's size (or of max_error when
                // both are given: the limit a preview measured); otherwise max_error in metres
                double metres = within && maxErrorPct > 0
                    ? PartLimit(maxErrorPct, maxErrorM > 0 ? maxErrorM : maxErrorPct / 100.0 * diag, n.Geom!, toLocal > 0 ? 1.0 / toLocal : 1.0)
                    : maxErrorM;
                maxError = metres * toLocal;
            }
            GeomData? nw;
            StateSet? nss = null;
            bool kept = false;
            if (seams == "rebake" && ctx.ParentsOf(n).Select(p => BakeKey(MergedState(StatesAbove(ctx, p).Append(n.StateSet)))).Distinct().Count() > 1)
            {
                nw = null;
                info.Reason = "shared by places with different textures or materials (rebake would give all of them one)";
            }
            else
            {
                var g0 = n.Geom;
                Func<double, double, double, bool>? keep = null;
                if (lookForOverlays)
                {
                    placed ??= Drawn().Where(x => x.node.Geom is { } cg && cg.Counts().tris > 0)
                        .Select(x => new Overlays.Placed(x.node, x.world)).ToList();
                    // the tile's places in the same world as the candidates (full matrices, also the edges from an
                    // external reference into its file, which ctx.Parents leaves out); InstanceMatrices only for a
                    // geometry that is not drawn (a hidden LOD level, a switched-off child)
                    var places = placed.Where(x => ReferenceEquals(x.Node, n)).Select(x => x.World).Take(16).ToList();
                    if (places.Count == 0) places = InstanceMatrices(n, ctx.Parents, 16);
                    keep = Overlays.KeepFor(g0, places, placed, out int found, out int copies);
                    if (copies > 0) notes.Add(Overlays.CopyNote);
                    if (!protect)
                    {
                        if (found > 0) notes.Add(Overlays.FastNote);
                        keep = null;
                    }
                    kept = keep != null;
                }
                SimplifyAttempt Run(double me)
                {
                    var inf = new SimplifyInfo();
                    if (seams == "rebake")
                    {
                        string label = n.Name.Length > 0 ? n.Name : ctx.ParentsOf(n).Select(p => p.Name).FirstOrDefault(x => x.Length > 0) ?? "";
                        var rb = Rebake(g0, InheritedStateSet(ctx, n), label, ratio, rebakeTex, crease, borders, parts, me, inf, imageCache,
                            ctx.Progress, ctx.Targets != null ? 4 : 64, ctx.Scene, fill, classFill, keep);
                        return new(rb?.g, rb?.ss, inf);
                    }
                    return new(SimplifyGeom(g0, new[] { ratio }, method, ctx.Progress, ctx.Targets != null ? 4 : 64, seams, inf, borders, parts, me, crease, keep, prune)[0], null, inf);
                }
                // Within that cannot be met leaves the geometry unchanged; the reason names the limit and the deviation
                var at = within ? SimplifyWithin(g0, maxError, toLocal > 0 ? 1.0 / toLocal : 1.0, Run) : Run(maxError);
                (nw, nss, info) = (at.Geom, at.State, at.Info);
                if (seams == "rebake" && nw != null) notes.AddRange(info.Notes);
            }
            if (nw == null)
            {
                string reason = info.Reason ?? "not simplifiable";
                if (!skipped.ContainsKey(reason)) skippedOrder.Add(reason);
                skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
                NoteWorst(worst, reason, info);
                continue;
            }
            SetGeom(n, nw);
            // counted only for geometries that changed, so the message never claims "kept" on a skipped one
            if (kept) protectedGeoms++;
            if (nss != null) { n.StateSet = nss; n.Mark("stateset"); baked++; }
            before += t0;
            after += nw.Counts().tris;
            done++;
        }
        if (done == 0 && skipped.Count == 0 && minTris != 0) return BelowLimit(ctx, minTris, "simplify");
        string msg = Summ(done, "geometries simplified", $"{PyFormat.Int(before)} -> {PyFormat.Int(after)} triangles");
        if (baked > 0) msg += $"; {PyFormat.Int(baked)} rebaked onto new {rebakeTex} px textures";
        foreach (var note in notes.Distinct()) msg += "; " + note;
        if (protectedGeoms > 0) msg += $"; overlay contact kept on {PyFormat.Int(protectedGeoms)} geometr{(protectedGeoms == 1 ? "y" : "ies")}";
        foreach (var reason in skippedOrder) msg += $"; {skipped[reason]} skipped: {reason}{WorstNote(worst, reason)}";
        return msg;
    }

    /// <summary>The limit in metres of a relative error target for one geometry (R2-D5): <paramref name="pct"/> % of the
    /// part's own world diagonal (its local box diagonal x <paramref name="scale"/>, its largest instance scale), but no
    /// more than <paramref name="capMetres"/> (the same share of the selected node's size). A small prop inside a large
    /// tile so keeps to its own size. The pass and Try both use it.</summary>
    internal static double PartLimit(double pct, double capMetres, GeomData g, double scale)
    {
        double own = Diagonal(g.BBox()) * (scale > 0 && double.IsFinite(scale) ? scale : 1.0);
        return own > 0 && double.IsFinite(own) ? Math.Min(capMetres, pct / 100.0 * own) : capMetres;
    }

    /// <summary>Within left unchanged: keeps the worst deviation per (constant) reason, so the reasons still group.</summary>
    internal static void NoteWorst(Dictionary<string, (double dev, int n)> worst, string reason, SimplifyInfo info)
    {
        if (info.WithinDev is not double d) return;
        if (!worst.TryGetValue(reason, out var w) || d > w.dev) worst[reason] = (d, info.WithinAttempts);
    }

    /// <summary>" (worst 24.7 cm after 3 attempts)" for a reason with a noted deviation, else "".</summary>
    internal static string WorstNote(Dictionary<string, (double dev, int n)> worst, string reason) =>
        worst.TryGetValue(reason, out var w) ? $" (worst {ErrorTarget.Distance(w.dev)} after {w.n} attempt{(w.n == 1 ? "" : "s")})" : "";

    /// <summary>One simplify run of a geometry: the result (null = left as it is, see Info.Reason), the new state set of a
    /// rebake, and the run's info.</summary>
    internal readonly record struct SimplifyAttempt(GeomData? Geom, StateSet? State, SimplifyInfo Info);

    /// <summary>Within: simplify with a deviation limit (local units) and keep to it, for the pass and for Try alike.
    /// Decimate's MaxError is a one-sided collapse bound, not a Hausdorff bound (it only checks the points absorbed by the
    /// two collapsing vertices), so the result can deviate several times more. Each result is therefore measured with
    /// Quality.Compare (seed 1: deterministic, so Try and Apply agree); while its MaxDev exceeds the limit, the run is
    /// repeated with maxError x 0.9 x limit / deviation, at most 4 attempts in all. Returns the first attempt within the
    /// limit. Within means within: when no attempt stays within it, the geometry is left unchanged (Geom null) and
    /// Info.Reason says so (worldPerLocal turns local lengths into metres for that note). A first result of null
    /// (nothing collapsed) is returned as it is.</summary>
    internal static SimplifyAttempt SimplifyWithin(GeomData before, double limit, double worldPerLocal, Func<double, SimplifyAttempt> run)
    {
        SimplifyAttempt last = default;
        var (within, bestDev, a) = TightenWithin(limit, maxError =>
        {
            last = run(maxError);
            return last.Geom == null ? null : new System.Runtime.CompilerServices.StrongBox<SimplifyAttempt>(last);
        }, at => Quality.Compare(before, at.Value.Geom!).MaxDev);
        if (within != null) return within.Value;
        if (a == 0) return last;   // a first result of null (nothing collapsed) is returned as it is
        // constant text per limit, so the pass groups the geometries; the deviation travels separately (WithinDev)
        var info = new SimplifyInfo
        {
            Reason = $"Within {ErrorTarget.Distance(limit * worldPerLocal)}: could not stay within it; left unchanged",
            WithinDev = double.IsFinite(bestDev) ? bestDev * worldPerLocal : null,
            WithinAttempts = a,
        };
        return new(null, null, info);
    }

    /// <summary>The measure-and-retry loop of Within (<see cref="SimplifyWithin"/> and the per-part proxy): runs
    /// <paramref name="run"/> with maxError = <paramref name="limit"/>, measures the result and, while it lies farther than the
    /// limit, runs again with maxError x <see cref="PartProxy.Limits.DecimateTighten"/> x limit / deviation, at most
    /// <see cref="PartProxy.Limits.MaxDecimateAttempts"/> attempts. Stops when a run returns null (nothing could be made)
    /// or a deviation is not finite (not measurable: not within). Returns the first result within the limit (else null),
    /// the smallest deviation measured (its deviation on success; infinity when none was finite) and the number of results
    /// measured.</summary>
    internal static (T? Result, double BestDev, int Attempts) TightenWithin<T>(double limit, Func<double, T?> run, Func<T, double> measure) where T : class
    {
        double maxError = limit;
        double bestDev = double.PositiveInfinity;
        int a = 0;
        while (a < PartProxy.Limits.MaxDecimateAttempts)
        {
            var at = run(maxError);
            if (at == null) break;   // tightened until nothing could be made: none of the attempts stayed within
            a++;
            double dev = measure(at);
            if (!double.IsFinite(dev)) break;   // not measurable: not within
            if (dev <= limit) return (at, dev, a);
            bestDev = Math.Min(bestDev, dev);
            maxError *= PartProxy.Limits.DecimateTighten * limit / dev;
        }
        return (null, bestDev, a);
    }

    /// <summary>Per geometry: the factor from world lengths to its local lengths (1 / largest scale over ALL places the
    /// geometry is drawn at, so the limit holds at the largest instance) and the world bounding diagonal "% of size"
    /// refers to: of the target node holding the geometry (with targets, see <see cref="WorldBox"/>), else of the
    /// geometry itself. The places come from <paramref name="drawn"/> (every drawn geometry with its full world matrix,
    /// external edges included, as Try and the overlay candidates see them); <see cref="InstanceMatrices"/> only for a
    /// geometry that is not drawn (a hidden LOD level, a switched-off child).</summary>
    internal static Dictionary<Node, (double ToLocal, double Diag)> ErrorFrames(OptContext ctx, IReadOnlyList<Node> geoms,
        IReadOnlyList<(Node node, double[] world, StateSet? ss)> drawn)
    {
        var worlds = new Dictionary<Node, List<double[]>>(ReferenceEqualityComparer.Instance);
        foreach (var (n, w, _) in drawn)
        {
            if (!worlds.TryGetValue(n, out var l)) worlds[n] = l = new List<double[]>(1);
            l.Add(w);
        }
        var parents = ctx.Parents;
        // with targets: the geometries below each target (the first target that holds one wins)
        var holder = new Dictionary<Node, Node>(ReferenceEqualityComparer.Instance);
        var targetDiag = new Dictionary<Node, double>(ReferenceEqualityComparer.Instance);
        foreach (var t in ctx.Targets ?? new List<Node>())
        {
            var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<Node>();
            stack.Push(t);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!seen.Add(n)) continue;
                if (n.Kind == Kind.External && !ctx.IncludeExternals) continue;
                if (n.Kind == Kind.Geometry) { if (n.Geom != null) holder.TryAdd(n, t); continue; }
                foreach (var c in n.Kind == Kind.External ? n.Children : ctx.Kids(n)) stack.Push(c);
            }
            // the box of the paths through the target (Try computes the same box from the same start)
            targetDiag[t] = Diagonal(WorldBox(t, SoftRender.ParentMatrix(t, parents)));
        }
        var o = new Dictionary<Node, (double, double)>(ReferenceEqualityComparer.Instance);
        foreach (var g in geoms)
        {
            double s, own;
            if (worlds.TryGetValue(g, out var ws))
            {
                s = ws.Max(Mat.MaxScale);
                own = Diagonal(GeomBox(g.Geom!, ws[0]));   // the first place, as ParentMatrix takes the first parent
            }
            else
            {
                s = InstanceMatrices(g, parents).Max(Mat.MaxScale);
                own = Diagonal(WorldBox(g, SoftRender.ParentMatrix(g, parents)));
            }
            double diag = holder.TryGetValue(g, out var t) ? targetDiag[t] : own;
            o[g] = (s > 0 && double.IsFinite(s) ? 1.0 / s : 1.0, diag);
        }
        return o;
    }

    /// <summary>The matrices from the scene root down to (not including) <paramref name="node"/>, one per place it is
    /// drawn at (every parent path; stops after <paramref name="cap"/> places). Never empty.</summary>
    internal static List<double[]> InstanceMatrices(Node node, IReadOnlyDictionary<Node, List<Node>> parents, int cap = 10_000)
    {
        var outl = new List<double[]>();
        var onPath = new HashSet<Node>(ReferenceEqualityComparer.Instance) { node };
        void Up(Node cur, double[] below)
        {
            if (outl.Count >= cap) return;
            bool any = false;
            if (parents.TryGetValue(cur, out var ps))
                foreach (var p in ps)
                {
                    if (!onPath.Add(p)) continue;
                    any = true;
                    Up(p, p.Matrix != null ? Mat.Mul(p.Matrix, below) : below);
                    onPath.Remove(p);
                }
            if (!any) outl.Add(below);
        }
        Up(node, Mat.Identity());
        return outl;
    }

    /// <summary>World bounding box of ALL geometry below <paramref name="node"/> (each place with its own matrix;
    /// <paramref name="start"/> = the matrix from the root to the node's parent), or null without vertices. "% of size"
    /// refers to its diagonal, in Try and in the pass alike.</summary>
    internal static (double[] lo, double[] hi)? WorldBox(Node node, double[]? start) =>
        Box(SoftRender.GeometryItems(node, int.MaxValue, start).Select(x => (x.node.Geom!, x.world)));

    /// <summary>Box of one geometry's vertices under the full matrix <paramref name="world"/> (null without vertices).</summary>
    internal static (double[] lo, double[] hi)? GeomBox(GeomData g, double[] world) => Box(new[] { (g, world) });

    static (double[] lo, double[] hi)? Box(IEnumerable<(GeomData g, double[] w)> items)
    {
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        bool any = false;
        foreach (var (g, w) in items)
        {
            var va = g.Vertices;
            if (va == null || va.Count == 0) continue;
            int c = va.Comps;
            for (int i = 0; i < va.Count; i++)
            {
                var (x, y, z) = Mat.Apply(w, va.Data[i * c], c > 1 ? va.Data[i * c + 1] : 0, c > 2 ? va.Data[i * c + 2] : 0);
                lo[0] = Math.Min(lo[0], x); lo[1] = Math.Min(lo[1], y); lo[2] = Math.Min(lo[2], z);
                hi[0] = Math.Max(hi[0], x); hi[1] = Math.Max(hi[1], y); hi[2] = Math.Max(hi[2], z);
                any = true;
            }
        }
        return any ? (lo, hi) : null;
    }

    /// <summary>Diagonal of a box (0 for none).</summary>
    internal static double Diagonal((double[] lo, double[] hi)? box) =>
        box is { } b ? Math.Sqrt(Enumerable.Range(0, Math.Min(3, Math.Min(b.lo.Length, b.hi.Length))).Sum(k => (b.hi[k] - b.lo[k]) * (b.hi[k] - b.lo[k]))) : 0;

    static string BelowLimit(OptContext ctx, long minTris, string pass)
    {
        var sizes = ctx.Geometries().Select(n => n.Geom!.Counts().tris).ToList();
        if (sizes.Count == 0) return "no geometry that can be changed";
        return $"nothing changed: no geometry has {PyFormat.Int(minTris)}+ triangles (largest: {PyFormat.Int(sizes.Max())}). "
               + $"Lower '{pass}' -> 'Only geometries with at least' or select nodes and run it on the selection";
    }

    static List<double> ParseList(string s) =>
        s.Replace(';', ',').Split(',').Where(x => x.Trim().Length > 0).Select(x => double.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToList();

    static string GenerateLod(OptContext ctx)
    {
        int minTris = ctx.Targets != null ? 0 : ctx.PInt("generate_lod", "min_tris");
        var ratios = ParseList(ctx.PStr("generate_lod", "ratios"));
        var dists = ParseList(ctx.PStr("generate_lod", "distances"));
        bool byError = ctx.PStr("generate_lod", "switching") != "fixed";
        // distance at which a deviation of 1 scene unit covers the allowed number of pixels
        double pxFactor = ctx.PInt("generate_lod", "screen_height")
                          / (2 * Math.Tan(Math.Clamp(ctx.PDouble("generate_lod", "fov"), 1, 179) * Math.PI / 360)
                             * Math.Max(ctx.PDouble("generate_lod", "pixel_error"), 1e-3));
        int dropped = 0;
        string method = ctx.PStr("generate_lod", "method");
        string farLevel = ctx.PStr("generate_lod", "far_level");
        bool proxyFar = farLevel is "proxy" or "cards";
        // read only for card far levels; an unknown name falls back to today's crossed 4
        string farLayout = farLevel == "cards" && CardLayout.Names.Contains(ctx.PStr("generate_lod", "far_layout")) ? ctx.PStr("generate_lod", "far_layout") : "crossed4";
        int proxyTex = ctx.PInt("generate_lod", "proxy_texture");
        int proxyVox = ctx.PInt("generate_lod", "proxy_voxels");
        var imageCache = new Dictionary<string, RawImage?>();
        var unitNotes = new List<string>();
        var codeNotes = new List<string>();
        var fill = proxyFar ? ExtraFill.Parse(ctx.PStr("generate_lod", "extra_fill")) : ExtraFill.White;
        var classFill = ExtraFill.ClassFill(proxyFar ? ctx.PInt("generate_lod", "class_fill") : 0);
        // the proxy far level: one hull (whole, today's) or each part its own (per_part: the far levels of all holders of an
        // object together, one atlas); proxy_from fixes e at the allowed pixels seen from there (fov clamped as above)
        var pp = ProxyParams.FromLodParams(ctx.Params);
        bool perPart = farLevel == "proxy" && pp.Parts == ProxyParts.PerPart;
        double eWorld = farLevel == "proxy" && pp.Within ? pp.MaxError : 0;   // FromLodParams computed it
        while (dists.Count < ratios.Count) dists.Add((dists.Count > 0 ? dists[^1] : 10.0) * 3);
        int done = 0;
        var handled = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var geoms = ctx.Geometries(minTris);
        bool meshoptMissing = false;
        // the node a geometry's LOD goes around (its geode, or the geometry itself); null when it gets none
        Node? HolderOf(Node gnode)
        {
            var parents = ctx.ParentsOf(gnode);
            if (parents.Count == 0 || (parents[0].Kind == Kind.Geode && parents.Count != 1)) return null;
            var holder = parents[0].Kind == Kind.Geode ? parents[0] : gnode;
            var hparents = ctx.ParentsOf(holder);
            if ((hparents.Count == 0 && !ReferenceEquals(holder, ctx.Scene.Root)) || hparents.Any(p => p.Kind is Kind.Lod or Kind.PagedLod)) return null;
            return holder;
        }
        // only holders that will get a LOD (they have a bounding box) take part in the atlas
        var partFar = perPart && ratios.Count > 0
            ? PartFarLevels(ctx, geoms.Select(HolderOf).OfType<Node>().Distinct(ReferenceEqualityComparer.Instance).Cast<Node>()
                    .Where(h => LodDrawables(h).Any(d => d.Geom?.BBox() != null)).ToList(), pp, eWorld,
                proxyTex, imageCache, unitNotes, fill, classFill)
            : null;
        for (int i = 0; i < geoms.Count; i++)
        {
            var gnode = geoms[i];
            var holder = HolderOf(gnode);
            if (holder == null || handled.Contains(holder)) continue;
            var hparents = ctx.ParentsOf(holder).ToList();
            bool isRoot = ReferenceEquals(holder, ctx.Scene.Root);
            var drawables = LodDrawables(holder);
            (double[] lo, double[] hi)? bb = null;
            foreach (var d in drawables)
            {
                var b = d.Geom?.BBox();
                if (b == null) continue;
                bb = bb == null ? b : (bb.Value.lo.Zip(b.Value.lo, Math.Min).ToArray(), bb.Value.hi.Zip(b.Value.hi, Math.Max).ToArray());
            }
            if (bb == null) continue;
            double radius = Math.Sqrt(bb.Value.hi.Zip(bb.Value.lo, (h, l) => (h - l) * (h - l)).Sum()) / 2;
            if (radius == 0) radius = 1.0;
            var levels = new List<Node> { holder };
            var perDrawable = new List<List<GeomData?>>();
            foreach (var d in drawables)
            {
                bool ok = d.Kind == Kind.Geometry && d.Geom != null && d.Geom.Optimizable;
                ctx.Progress($"LOD levels for geometry {i + 1}/{geoms.Count} ...");
                var lodInfo = new SimplifyInfo();
                perDrawable.Add(ok ? SimplifyGeom(d.Geom!, ratios, method, ctx.Progress, info: lodInfo) : ratios.Select(_ => (GeomData?)null).ToList());
                if (lodInfo.Reason == MeshoptNative.Unavailable) meshoptMissing = true;
            }
            var useful = Enumerable.Range(0, ratios.Count).Where(li => perDrawable.Any(v => v[li] != null)).ToList();
            Node? proxyNode = null;
            if (proxyFar && ratios.Count > 0)
            {
                ctx.Progress($"proxy for geometry {i + 1}/{geoms.Count} ...");
                proxyNode = farLevel == "cards"
                    ? BuildCards(ctx, holder, CardLayout.Named(farLayout), proxyTex, imageCache, unitNotes, fill, classFill)?.holder
                    : partFar != null ? partFar.GetValueOrDefault(holder)
                    : BuildProxy(ctx, holder, drawables, ratios[^1], proxyTex, proxyVox, imageCache, unitNotes, fill, classFill, pp,
                        eWorld > 0 ? eWorld / ObjectWorldScale(ctx, LodObject(ctx, holder)) : 0);
                if (proxyNode != null)
                {
                    // per part: every geometry of the far level already carries the object's result line
                    if (farLevel == "proxy" && partFar == null)
                        ctx.MadeFrom(proxyNode, drawables[0].Geom, $"textured proxy ({(eWorld > 0 ? "within " + ErrorTarget.Distance(eWorld) : PyFormat.Percent(ratios[^1], 0))})");
                    useful.Remove(ratios.Count - 1);
                }
            }
            if (useful.Count == 0 && proxyNode == null) continue;
            foreach (int li in useful)
            {
                var simpChildren = new List<Node>();
                for (int di = 0; di < drawables.Count; di++)
                {
                    var d = drawables[di];
                    var nw = perDrawable[di][li];
                    if (nw == null) { simpChildren.Add(d); continue; }
                    var nn = new Node(Kind.Geometry, "osg::Geometry", (d.Name.Length > 0 ? d.Name + " " : "") + $"LOD{li + 1}") { Geom = nw, StateSet = d.StateSet };
                    nn.Mark("geometry");
                    FltFaceCodes.Carry(nn, new[] { (d, 1.0) }, ctx.Scene, codeNotes);   // OpenFlight: the level's faces keep the IR / material codes
                    ctx.MadeFrom(nn, d.Geom, $"LOD level {li + 1} ({PyFormat.Percent(ratios[li], 0)})");
                    simpChildren.Add(nn);
                }
                if (holder.Kind == Kind.Geode)
                {
                    var gl = new Node(Kind.Geode, "osg::Geode", (holder.Name.Length > 0 ? holder.Name + " " : "") + $"LOD{li + 1}") { StateSet = holder.StateSet, Children = simpChildren };
                    levels.Add(gl);
                }
                else levels.Add(simpChildren[0]);
            }
            if (proxyNode != null) { levels.Add(proxyNode); useful.Add(ratios.Count - 1); }
            var edges = new List<double> { 0.0 };
            if (byError)
            {
                // switch-in distance of each level from its measured deviation; a level that is not less accurate than
                // the next one would never be seen (the next one switches in no later), so it is dropped
                ctx.Progress($"measuring LOD levels for geometry {i + 1}/{geoms.Count} ...");
                var orig = Merged(drawables);
                var near = new List<(Node level, double d)>();
                for (int li = 1; li < levels.Count; li++)
                {
                    double e = Quality.Compare(orig, Merged(levels[li].Kind == Kind.Geode ? levels[li].Children : new List<Node> { levels[li] })).MaxDev;
                    double d = Math.Max(e * pxFactor, radius * 0.5);
                    // proxy_from is a floor: the far level never appears nearer than it (metres, in the holder's units as e is)
                    if (eWorld > 0 && proxyNode != null && ReferenceEquals(levels[li], proxyNode))
                        d = Math.Max(d, ctx.PDouble("generate_lod", "proxy_from") / ObjectWorldScale(ctx, LodObject(ctx, holder)));
                    while (near.Count > 0 && near[^1].d >= d) { near.RemoveAt(near.Count - 1); dropped++; }
                    near.Add((levels[li], d));
                }
                levels = new List<Node> { holder };
                levels.AddRange(near.Select(x => x.level));
                edges.AddRange(near.Select(x => x.d));
            }
            else edges.AddRange(useful.Select(li => radius * dists[li]));
            edges.Add(1e30);
            var lod = new Node(Kind.Lod, "osg::LOD", holder.Name.Length > 0 ? holder.Name + " LOD" : "LOD") { Children = levels };
            lod.Ranges = Enumerable.Range(0, levels.Count).Select(kk => (edges[kk], edges[kk + 1])).ToList();
            lod.Mark("ranges");
            foreach (var p in hparents) ctx.Replace(p, holder, new[] { lod });
            if (isRoot) { ctx.Scene.Root = lod; ctx.Invalidate(); }
            handled.Add(holder);
            done++;
        }
        string missingNote = $"no levels made with meshopt: {MeshoptNative.Unavailable}";
        if (done == 0 && meshoptMissing) return "nothing changed: " + missingNote;
        if (done == 0 && minTris != 0) return BelowLimit(ctx, minTris, "generate_lod");
        string extra = byError ? $"up to {ratios.Count} extra level(s) each, switching where a level changes the picture by at most {PyFormat.Fixed(ctx.PDouble("generate_lod", "pixel_error"), 1)} px"
                                 : $"{ratios.Count} extra level(s) each";
        if (dropped > 0) extra += $"; {dropped} level(s) dropped (never seen: the next level is as accurate)";
        if (farLevel == "proxy" && !perPart && pp.Ground != ProxyGround.Full) extra += "; proxy_ground applies with proxy_parts = per_part";
        if (eWorld > 0 && !byError) extra += "; proxy_from applies with switching = error";
        foreach (var note in unitNotes.Distinct()) extra += "; far level: " + note;
        foreach (var note in codeNotes.Distinct()) extra += "; levels: " + note;
        if (meshoptMissing) extra += "; " + missingNote;
        return Summ(done, "LOD node(s) created", extra);
    }

    /// <summary>All triangles of some drawables in one geometry (for measuring a LOD level against the original).</summary>
    static GeomData Merged(IEnumerable<Node> drawables)
    {
        var V = new List<double>();
        var T = new List<int>();
        foreach (var d in drawables)
        {
            if (d.Geom?.Vertices == null) continue;
            int b = V.Count / 3;
            V.AddRange(Pos3(d.Geom.Vertices));
            T.AddRange(d.Geom.Triangles().Select(x => x + b));
        }
        var g = new GeomData { Vertices = new VArray(V.ToArray(), 3, "Vec3Array") };
        g.Prims.Add(new Primitive("TRIANGLES", "elements", indices: T.ToArray()));
        return g;
    }

    /// <summary>Texture unit 0 as 2 x n (null when there is none per vertex).</summary>
    static double[]? Uv0(GeomData g) => Uv(g, 0);

    /// <summary>Texture coordinates of a unit as 2 x n (null when there are none per vertex).</summary>
    static double[]? Uv(GeomData g, int unit)
    {
        int n = g.NumVertices;
        if (!g.TexCoords.TryGetValue(unit, out var uv) || uv.Binding != "vertex" || uv.Count != n) return null;
        var o = new double[2 * n];
        for (int v = 0; v < n; v++) { o[2 * v] = uv.Data[v * uv.Comps]; o[2 * v + 1] = uv.Comps > 1 ? uv.Data[v * uv.Comps + 1] : 0; }
        return o;
    }

    /// <summary>Vertex colours as RGBA 0..1 per vertex (null when there are none).</summary>
    static double[]? VertexColors(GeomData g)
    {
        if (g.Colors == null || g.Colors.Count == 0) return null;
        var c = g.Colors;
        int n = g.NumVertices;
        var o = new double[4 * n];
        for (int v = 0; v < n; v++)
        {
            int ci = c.Binding == "vertex" && c.Count == n ? v : 0;
            for (int k = 0; k < 4; k++) o[4 * v + k] = k < c.Comps ? c.Data[ci * c.Comps + k] / (c.Normalize ? 255.0 : 1.0) : 1.0;
        }
        return o;
    }

    /// <summary>The layers of a state for baking onto a new mesh, with UVs and image: the texture units above 0, and the
    /// companion images (infrared, see <see cref="Companions"/>) of every unit's texture. A unit without texture
    /// coordinates of its own uses unit 0's; one without any, or whose image cannot be read, is left out (the fill
    /// there). Class maps (<see cref="Texture.IsClassMap"/>) are marked for nearest lookup; one that cannot be read is
    /// not baked (its file is left untouched). What happened goes into notes.</summary>
    static Dictionary<LayerKey, (double[] UV, RawImage Image, bool ClassMap)> ExtraUnits(GeomData g, StateSet? ss, Dictionary<string, RawImage?> cache, List<string>? notes)
    {
        var o = new Dictionary<LayerKey, (double[] UV, RawImage Image, bool ClassMap)>();
        if (ss == null) return o;
        foreach (var t in ss.Textures)
        {
            if (t.Unit == 0) continue;
            notes?.Add(t.IsClassMap ? $"texture unit {t.Unit} class map baked by nearest lookup (class IDs, never blended)"
                : $"texture unit {t.Unit} baked raw (no material colour; a normal map cannot be reprojected)");
            var uv = Uv(g, t.Unit);
            if (uv == null && (uv = Uv0(g)) != null) notes?.Add($"unit {t.Unit} without texture coordinates of its own uses unit 0's");
            var img = LoadImage(t, cache);
            if (img == null && t.IsClassMap) { notes?.Add(UnreadableClassMap(t)); continue; }
            if (uv == null || img == null) { notes?.Add($"unit {t.Unit} texture {t.File} not usable (unreadable or no texture coordinates): the fill is used"); continue; }
            o[t.Unit] = (uv, img, t.IsClassMap);
        }
        foreach (var t in ss.Textures)
        {
            if (t.Companions == null) continue;
            var uv = Uv(g, t.Unit) ?? Uv0(g);
            if (uv == null) continue;
            foreach (var (pat, c) in t.Companions)
            {
                var img = LoadImage(c, cache);
                if (img == null && c.IsClassMap) { notes?.Add(UnreadableClassMap(c)); continue; }
                if (img == null) { notes?.Add($"{Companions.Label(pat)} companion {c.File} could not be read: the fill is used"); continue; }
                notes?.Add(c.IsClassMap ? $"{Companions.Label(pat)} companions ({pat}) baked by nearest lookup (class IDs, never blended) into a companion atlas with the same layout"
                    : $"{Companions.Label(pat)} companions ({pat}) baked raw into a companion atlas with the same layout");
                o[new LayerKey(t.Unit, pat)] = (uv, img, c.IsClassMap);
            }
        }
        return o;
    }

    static string UnreadableClassMap(Texture t) => $"class map {t.File} could not be read (16-bit PNG?): not baked, file left untouched";

    static RawImage? LoadImage(Texture? tex, Dictionary<string, RawImage?> cache)
    {
        if (tex == null) return null;
        if (tex.Baked != null) return tex.Baked;
        if (tex.Path == null || !tex.Exists) return null;
        if (!cache.TryGetValue(tex.Path, out var img))
        {
            try { img = ImageCodecs.Read(tex.Path); } catch (Exception) { img = null; }
            cache[tex.Path] = img;
        }
        return img;
    }

    /// <summary>The state that applies to a node, merged down the path from the root like OSG does (along the first
    /// parent): nearer state sets override textures per unit, the material and modes. A new object, for reading.</summary>
    static StateSet? InheritedStateSet(OptContext ctx, Node n) => MergedState(StatesAbove(ctx, n));

    /// <summary>State sets from the root down to the node (included), along the first parent.</summary>
    static List<StateSet?> StatesAbove(OptContext ctx, Node n)
    {
        var l = new List<StateSet?>();
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        for (var cur = n; cur != null && seen.Add(cur); cur = ctx.ParentsOf(cur).FirstOrDefault()) l.Add(cur.StateSet);
        l.Reverse();
        return l;
    }

    /// <summary>State sets (root first) merged into one; null when there are none.</summary>
    internal static StateSet? MergedState(IEnumerable<StateSet?> rootFirst)
    {
        StateSet? m = null;
        foreach (var st in rootFirst)
        {
            if (st == null) continue;
            m ??= new StateSet();
            if (st.Diffuse != null) { m.Diffuse = st.Diffuse; m.MaterialName = st.MaterialName; }
            else if (st.MaterialName.Length > 0) m.MaterialName = st.MaterialName;
            foreach (var t in st.Textures) { m.Textures.RemoveAll(x => x.Unit == t.Unit); m.Textures.Add(t); }
            m.Transparent |= st.Transparent;
            foreach (var (k, v) in st.Modes) m.Modes[k] = v;
        }
        return m;
    }

    /// <summary>What a rebake reads from a state: the texture on every unit and the material colour.</summary>
    static string BakeKey(StateSet? st)
    {
        static string Id(Texture t) => t.Baked != null ? "baked:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(t.Baked) : (t.Path ?? t.File);
        string tex = string.Join(";", st?.Textures.OrderBy(x => x.Unit).Select(x => x.Unit + ":" + Id(x)) ?? Enumerable.Empty<string>());
        return tex + "|" + string.Join(",", st?.Diffuse ?? Array.Empty<double>());
    }

    /// <summary>Rebake: the shape is simplified on positions only, without the relative safety limit, so the target
    /// count decides; the result gets its own UV atlas and a texture baked from the original (texture unit 0 x vertex
    /// colours; the material stays as it is and still applies), plus one raw texture per unit above 0 with the same UVs.
    /// Without a texture or vertex colours there is nothing to bake: the shape is simplified the same way and keeps no
    /// UVs. Null (with info.Reason) when nothing could be done.</summary>
    public static (GeomData g, StateSet? ss)? Rebake(GeomData g0, StateSet? ss, string name, double ratio, int texSize, double crease,
        string borders, string parts, double maxError, SimplifyInfo info, Dictionary<string, RawImage?> cache, Action<string>? progress = null,
        int minTris = 4, Scene? scene = null, ExtraFill? fill = null, ExtraFill? classFill = null, Func<double, double, double, bool>? keep = null)
    {
        var (g, _) = Normalized(g0);
        var T = g.Triangles();
        if (T.Length / 3 < minTris || g.Vertices == null) { info.Reason = $"too small (< {minTris} triangles)"; return null; }
        // what a rebake cannot carry over: then the geometry stays as it is
        if (g.Lines().Length > 0 || g.Points().Length > 0) { info.Reason = "has lines or points (rebake keeps triangles only)"; return null; }
        var P = Pos3(g.Vertices);
        var uv = Uv0(g);
        var tex0 = ss?.Textures.FirstOrDefault(t => t.Unit == 0);
        var img = uv == null ? null : LoadImage(tex0, cache);
        if (tex0 != null && img == null)
        {
            info.Reason = uv == null ? "textured without texture coordinates" : $"texture {tex0.File} could not be read (missing, or a format like .dds)";
            return null;
        }
        if (ss?.Textures.FirstOrDefault(t => t.Unit != 0 && ((Uv(g, t.Unit) ?? uv) == null || LoadImage(t, cache) == null)) is { } bad)
        {
            info.Reason = (Uv(g, bad.Unit) ?? uv) == null ? $"texture unit {bad.Unit} without texture coordinates"
                : $"texture {bad.File} (unit {bad.Unit}) could not be read (missing, or a format like .dds)";
            return null;
        }
        var extra = ExtraUnits(g, ss, cache, info.Notes);
        var vcol = VertexColors(g);
        // vertex colours that already carry the material colour (OpenFlight: face colour x diffuse; OSG: colour material)
        // go into the texture divided by it, and the vertices get the material colour: drawn, it is the same product
        var diffuse = ss?.Diffuse;
        if (vcol != null && diffuse != null)
            for (int i = 0; i < vcol.Length / 4; i++)
                for (int k = 0; k < 3; k++) vcol[4 * i + k] = Math.Min(1.0, vcol[4 * i + k] / Math.Max(diffuse[k], 1e-6));
        bool bakeBase = img != null || vcol != null, bake = bakeBase || extra.Count > 0;

        var posOnly = new GeomData { Vertices = g.Vertices };
        posOnly.Prims.Add(new Primitive("TRIANGLES", "elements", indices: T));
        var (w, _) = Normalized(posOnly, weld: true);
        var Tw = w.Triangles();
        int m = Tw.Length / 3;
        int target = Math.Max((int)(m * ratio), 4);
        progress?.Invoke($"rebake: simplifying {m:N0} -> {target:N0} triangles ...");
        var wp = Pos3(w.Vertices!);
        var r = Decimate.Run(wp, Tw, new[] { target }, null, 0, new Decimate.Options
        {
            Borders = borders == "lock" ? BorderMode.Lock : borders == "slide" ? BorderMode.Slide : BorderMode.Match, MergeParts = parts == "merge",
            MaxError = maxError > 0 ? maxError : null, MaxRelativeError = 0, Progress = progress, Lock = KeepMask(wp, keep),
        })[0];
        if (r.T.Length == 0 || r.T.Length / 3 > m * 0.97)
        {
            info.Reason = maxError > 0 ? "every collapse would exceed the max deviation" : "no edge could be collapsed";
            return null;
        }
        var like = g.Normals ?? new VArray(Array.Empty<double>(), 3, "Vec3Array");
        bool outward = FacesAgreeWithNormals(g0);
        if (!bake)
        {
            var plain = new GeomData { Vertices = g.Vertices.CopyWith(r.V, 3) };
            plain.Prims.Add(new Primitive("TRIANGLES", "elements", indices: r.T, indexType: MeshOps.SmallestIndexType(r.V.Length / 3)));
            return (WithCreaseNormals(plain, like, crease, outward), null);
        }
        progress?.Invoke("rebake: unwrapping ...");
        var uw = Unwrap.Run(r.V, r.T, texSize);
        var o = new GeomData { Vertices = g.Vertices.CopyWith(uw.V, 3) };
        o.TexCoords[0] = new VArray(uw.UV, 2, "Vec2Array");
        if (vcol != null)
        {
            // the colours are in the texture now; the vertices keep the material colour (white without one)
            double scale = g.Colors!.Normalize ? 255.0 : 1.0;
            var matCol = new double[uw.V.Length / 3 * 4];
            for (int i = 0; i < matCol.Length / 4; i++)
                for (int k = 0; k < 4; k++) matCol[4 * i + k] = (k < 3 && diffuse != null ? diffuse[k] : 1.0) * scale;
            o.Colors = new VArray(matCol, 4, g.Colors.Normalize ? "Vec4ubArray" : "Vec4Array", "vertex", g.Colors.Normalize);
        }
        o.Prims.Add(new Primitive("TRIANGLES", "elements", indices: uw.T, indexType: MeshOps.SmallestIndexType(uw.V.Length / 3)));
        o = WithCreaseNormals(o, like, crease, outward);

        progress?.Invoke("rebake: baking the texture ...");
        int n = g.NumVertices;
        double diag = 0;
        for (int c = 0; c < 3; c++)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int i = 0; i < n; i++) { lo = Math.Min(lo, P[3 * i + c]); hi = Math.Max(hi, P[3 * i + c]); }
            diag += (hi - lo) * (hi - lo);
        }
        diag = Math.Sqrt(diag);
        var col = vcol ?? Enumerable.Repeat(1.0, 4 * n).ToArray();
        var (baked, bakedExtra) = Bake.RunAll(new Bake.Input
        {
            V = uw.V, T = uw.T, UV = uw.UV, Size = texSize, Original = new TriIndex(P, T), OV = P, OT = T, TriSource = new int[T.Length / 3],
            OUV = uv ?? new double[2 * n], OColor = col, HasUV = new[] { img != null }, Images = new[] { img }, MaxDist = Math.Max(diag * 0.1, 1e-9),
            Layers = extra.ToDictionary(kv => kv.Key, kv => new Bake.Layer
            {
                OUV = kv.Value.UV, Images = new RawImage?[] { kv.Value.Image }, Nearest = kv.Value.ClassMap, Fill = kv.Value.ClassMap ? classFill ?? ExtraFill.ClassFill(0) : null,
            }),
            Fill = fill ?? ExtraFill.White,
        });
        var nss = new StateSet
        {
            MaterialName = ss?.MaterialName ?? "", Diffuse = ss?.Diffuse, Transparent = ss?.Transparent ?? false,
            Modes = new Dictionary<string, string>(ss?.Modes ?? new Dictionary<string, string>()),
        };
        string file = (name.Length > 0 ? name : "object") + "_rebake";
        var states = new List<StateSet?> { ss };
        if (bakeBase) nss.Textures.Add(BakedTexture(file, 0, baked, states, scene, info.Notes));
        AddExtraUnits(o, nss, bakedExtra, file, states, scene, info.Notes, bakeBase ? baked : null, ClassKeys(new[] { extra }));
        return (o, nss);
    }

    /// <summary>One textured proxy geometry for all drawables of a holder (see Proxy); null when it cannot be built. Shading
    /// from <paramref name="pp"/> (hull: today's). <paramref name="maxError"/> &gt; 0 (proxy_from; in the holder's units):
    /// the hull is built within it, measured as the switching measures a level and tightened like within mode; null when no
    /// attempt stays within it.</summary>
    static Node? BuildProxy(OptContext ctx, Node holder, List<Node> drawables, double ratio, int texSize, int voxels,
        Dictionary<string, RawImage?> imageCache, List<string>? notes, ExtraFill? fill = null, ExtraFill? classFill = null,
        ProxyParams? pp = null, double maxError = 0)
    {
        pp ??= new ProxyParams();
        fill ??= ExtraFill.White;
        classFill ??= ExtraFill.ClassFill(0);
        var used = new List<Node>();
        var progress = ctx.Progress;
        var above = StatesAbove(ctx, holder);
        var sources = new List<Proxy.Source>();
        var states = new List<StateSet?>();
        long tris = 0;
        foreach (var d in drawables)
        {
            if (d.Kind != Kind.Geometry || d.Geom == null || d.Geom.Vertices == null) continue;
            var (g, _) = Normalized(d.Geom);
            var T = g.Triangles();
            if (T.Length == 0) continue;
            int n = g.NumVertices;
            var src = new Proxy.Source
            {
                V = Pos3(g.Vertices!), T = T, UV = Uv0(g),
                N = g.Normals is { Comps: 3, Binding: "vertex" } on && on.Count == g.Vertices!.Count ? on.Data : null,   // read by normals = original only
            };
            // the state that applies to the drawable, merged down the path like OSG does
            var ss = MergedState(ReferenceEquals(d, holder) ? above : above.Append(d.StateSet));
            states.Add(ss);
            src.Image = LoadImage(ss?.Textures.FirstOrDefault(t => t.Unit == 0), imageCache);
            src.Extra = ExtraUnits(g, ss, imageCache, notes);
            if (ss?.Diffuse != null) src.Color = ss.Diffuse.Concat(new[] { 1.0 }).Take(4).ToArray();
            src.VertexColors = VertexColors(g);
            sources.Add(src);
            used.Add(d);
            tris += T.Length / 3;
        }
        if (sources.Count == 0) return null;
        LayerNotes(sources.Select(s => s.Extra), fill, classFill, notes);
        int target = (int)Math.Max(32, tris * ratio);
        Proxy.Options Options(double? me) => new()
        {
            TargetTris = target, Resolution = voxels, TextureSize = Math.Clamp(texSize, 16, 8192), Progress = progress, Fill = fill, ClassFill = classFill,
            Normals = pp.Normals, Crease = pp.Crease, MaxError = me,
        };
        Proxy.Result? r;
        if (maxError > 0)
        {
            var orig = Merged(used);
            r = TightenWithin(maxError, me => Proxy.Build(sources, Options(me)), x =>
            {
                var g = new GeomData { Vertices = new VArray(x.V, 3, "Vec3Array") };
                g.Prims.Add(new Primitive("TRIANGLES", "elements", indices: x.T));
                return Quality.Compare(orig, g).MaxDev;
            }).Result;
            if (r == null) notes?.Add("proxy_from: the proxy could not stay within the error; the simplified level is used");
        }
        else r = Proxy.Build(sources, Options(null));
        if (r == null) return null;
        // the error limit can stop the hull far above the target (thin sheets like terrain need both sides):
        // then the plain simplified level is the better far level (proxy_from has no target)
        if ((maxError <= 0 && r.T.Length / 3 > Math.Max(2 * target, 64)) || r.T.Length / 3 >= tris)
        {
            progress?.Invoke($"proxy: {r.T.Length / 3:N0} triangles is too far above the target {target:N0}, using the simplified level");
            return null;
        }
        var geo = ProxyGeometry(r, holder.Name.Length > 0 ? holder.Name : "object", states, ctx.Scene, notes, ClassKeys(sources.Select(s => s.Extra)));
        FltFaceCodes.Carry(geo, used.Select(d => (d, 1.0)), ctx.Scene, notes);
        return geo;
    }

    /// <summary>The drawables of a LOD holder: a geode's children, or the geometry itself.</summary>
    static List<Node> LodDrawables(Node holder) => holder.Kind == Kind.Geode ? holder.Children.ToList() : new List<Node> { holder };

    /// <summary>The object a LOD holder belongs to: the nearest selected target at or above it, else the holder itself.</summary>
    static Node LodObject(OptContext ctx, Node holder)
    {
        if (ctx.Targets == null) return holder;
        var selected = new HashSet<Node>(ctx.Targets, ReferenceEqualityComparer.Instance);
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<Node>();
        queue.Enqueue(holder);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (!seen.Add(n)) continue;
            if (selected.Contains(n)) return n;
            foreach (var p in ctx.ParentsOf(n)) queue.Enqueue(p);
        }
        return holder;
    }


    /// <summary>generate_lod's far level with proxy_parts = per_part: the holders are grouped by their object
    /// (<see cref="LodObject"/>). Per object the frame (the world scale that takes e into local units), the grid (gap: 2
    /// cells of the object's extent over the voxels, within: e; the small rule with the object's diagonal) and the ground
    /// (the object's lowest world point) are those of the whole object; each holder is only the geometry source: its
    /// drawables become pieces in its own frame, <see cref="PartProxy.OfHolder"/> finds the parts and
    /// <see cref="PartProxy.BuildParts"/> builds them; then ONE <see cref="PartProxy.BakeAtlas"/> over all holders of the
    /// object, one state set. Returns the far level per holder (a geode with the proxy geometry and any kept or decimated
    /// geometries); a holder without one (nothing drawn, no proxy, or a proxy with more triangles than the holder) keeps
    /// the simplified far level. Every geometry made remembers the object's result line.</summary>
    static Dictionary<Node, Node> PartFarLevels(OptContext ctx, List<Node> holders, ProxyParams pp, double eWorld, int texSize,
        Dictionary<string, RawImage?> cache, List<string> notes, ExtraFill fill, ExtraFill classFill)
    {
        var far = new Dictionary<Node, Node>(ReferenceEqualityComparer.Instance);
        int size = Math.Clamp(texSize, 16, 8192);
        foreach (var obj in holders.GroupBy(h => LodObject(ctx, h), (IEqualityComparer<Node>)ReferenceEqualityComparer.Instance))
        {
            string label = Label(obj.Key);
            ctx.Progress($"per-part proxy far level for {label} ...");
            // the whole object, in its parent's frame: extent, diagonal and lowest world point
            var drawn = DrawnBelow(ctx, obj.Key);
            if (drawn.Count == 0) continue;
            var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            var w = SoftRender.ParentMatrix(obj.Key, ctx.Parents);
            double lowZ = double.PositiveInfinity;
            foreach (var (g, _, _) in drawn)
                for (int i = 0; i < g.Vertices!.Data.Length / 3; i++)
                {
                    double x = g.Vertices.Data[3 * i], y = g.Vertices.Data[3 * i + 1], z = g.Vertices.Data[3 * i + 2];
                    lo[0] = Math.Min(lo[0], x); lo[1] = Math.Min(lo[1], y); lo[2] = Math.Min(lo[2], z);
                    hi[0] = Math.Max(hi[0], x); hi[1] = Math.Max(hi[1], y); hi[2] = Math.Max(hi[2], z);
                    lowZ = Math.Min(lowZ, w[8] * x + w[9] * y + w[10] * z + w[11]);
                }
            double ext = Enumerable.Range(0, 3).Max(c => hi[c] - lo[c]);
            double diag = Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (hi[c] - lo[c]) * (hi[c] - lo[c])));
            double worldScale = ObjectWorldScale(ctx, obj.Key);
            double gap = KeepGap(eWorld, worldScale, ext, pp.Voxels);
            PartProxy.BuildOptions Options(GroundPlane? plane) => new()
            {
                TextureSize = size, Fill = fill, ClassFill = classFill, Progress = ctx.Progress, Ground = pp.Ground, WorldScale = worldScale, ErrorMetres = eWorld,
                Normals = pp.Normals, Crease = pp.Crease, Plane = plane,
            };

            var built = new List<(Node Holder, PartProxy.ObjectResult R, List<Proxy.Source> Sources, Dictionary<PartProxy.Piece, StateSet?> States, long Tris)>();
            foreach (var holder in obj)
            {
                // Assemble would rewrite a moving node's children in place and lose its full-detail level
                if (Moving(holder)) { notes.Add($"{Label(holder)} is a moving node, kept the simplified level"); continue; }
                var above = StatesAbove(ctx, holder);
                var pieces = new List<PartProxy.Piece>();
                foreach (var d in LodDrawables(holder))
                {
                    if (d.Kind != Kind.Geometry || d.Geom?.Vertices == null) continue;
                    var (g, _) = Normalized(d.Geom);
                    var T = g.Triangles();
                    int nv = g.NumVertices;
                    if (g.Vertices == null || T.Length == 0 || T.Any(v => v < 0 || v >= nv)) continue;
                    bool self = ReferenceEquals(d, holder);
                    pieces.Add(new PartProxy.Piece(d, g, Pos3(g.Vertices), T, MergedState(self ? above : above.Append(d.StateSet)), self ? new[] { d } : new[] { holder, d }));
                }
                if (pieces.Count == 0) continue;
                var states = new Dictionary<PartProxy.Piece, StateSet?>(ReferenceEqualityComparer.Instance);
                foreach (var p in pieces) states[p] = p.State;
                var sources = PieceSources(pieces, states, cache, notes);
                long tris = sources.Sum(s => (long)s.T.Length / 3);
                LayerNotes(sources.Select(s => s.Extra), fill, classFill, notes);
                var asm = PartProxy.OfHolder(pieces, holder, gap, diag);
                var o = Options(pp.Ground == ProxyGround.Cut ? PartProxy.GroundAt(asm, SoftRender.ParentMatrix(holder, ctx.Parents), lowZ) : null);
                var r = PartProxy.BuildParts(asm, sources, pp, o);
                if (r == null) { notes.AddRange(o.Notes); continue; }
                if (r.Report.TrisAfter >= tris) continue;   // already simpler than its proxy: the simplified far level
                notes.AddRange(o.Notes);
                notes.AddRange(r.Notes);
                built.Add((holder, r, sources, states, tris));
            }
            if (built.Count == 0) continue;

            // one atlas and one state set for every holder of the object
            var (tex, extra) = PartProxy.BakeAtlas(built.Select(b => b.R).ToList(), built.Select(b => (IReadOnlyList<Proxy.Source>)b.Sources).ToList(), Options(null));
            var atlas = AtlasState(ctx, label, tex, extra, built.SelectMany(b => b.R.Asm.AllPieces().Select(p => b.States[p])).ToList(), built.SelectMany(b => b.Sources), notes);
            long before = 0, after = 0, drawBefore = 0, drawAfter = 0;
            var made = new List<(PartProxy.ObjectResult R, Dictionary<PartProxy.ProxyMesh, Node> Geos, PartProxy.Assembled Made)>();
            foreach (var b in built)
            {
                var geos = MeshGeometries(ctx, b.R, atlas, Label(b.Holder), notes);
                var m = AssembleParts(ctx, b.Holder, b.R, geos, b.States, Label(b.Holder), notes);
                // a holder is a geode or a geometry: what Assemble made is geodes, the far level is one geode with their drawables
                far[b.Holder] = m.Holder.Kind == Kind.Geode ? m.Holder : new Node(Kind.Geode, "osg::Geode", Label(b.Holder) + " proxy")
                {
                    Children = m.Holder.Children.SelectMany(c => c.Kind == Kind.Geode ? c.Children : new List<Node> { c }).ToList(),
                };
                before += b.Tris; after += b.R.Report.TrisAfter; drawBefore += b.Sources.Count; drawAfter += m.DrawCalls;
                made.Add((b.R, geos, m));
            }
            string line = $"far level: per-part proxy, {PyFormat.Int(before)} -> {PyFormat.Int(after)} triangles, draw calls {PyFormat.Int(drawBefore)} -> {PyFormat.Int(drawAfter)}"
                          + PartProxy.FarDetail(made.Select(x => (x.R, x.Made)).ToList());
            foreach (var (r, geos, m) in made) MarkMadeFrom(ctx, r, geos, m, line);
        }
        return far;
    }

    // ------------------------------------------------------------------------------ textures

    static string Downscale(OptContext ctx)
    {
        int limit = ctx.PInt("downscale_textures", "max_size");
        bool classMaps = ctx.PBool("downscale_textures", "resize_class_maps");
        bool keepCoverage = ctx.PBool("downscale_textures", "keep_coverage");
        if (ImageResizer.Current == null) return "image scaling is not available - textures not changed";
        int count = 0, embedded = 0, companions = 0, kept = 0;
        var seen = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
        foreach (var n in ctx.ScopeNodes())
        {
            if (n.StateSet == null) continue;
            foreach (var t in n.StateSet.Textures)
            {
                if (seen.Contains(t) || t.Path == null || !t.Exists)
                {
                    if (t.Embedded && !seen.Contains(t) && Math.Max(t.Width, t.Height) > limit) embedded++;
                    seen.Add(t);
                    continue;
                }
                seen.Add(t);
                int w = t.Width, h = t.Height;
                if (Math.Max(w, h) <= limit) continue;
                if (t.IsClassMap && !classMaps) { kept++; continue; }   // class maps keep their size unless asked (then nearest)
                while (Math.Max(w, h) > limit) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
                t.Resize = (w, h);
                t.Width = w; t.Height = h;
                t.ResizeClassMaps = classMaps;
                t.KeepCoverage = keepCoverage && !t.IsClassMap;   // class maps are resized nearest, never by coverage
                count++;
                companions += t.Companions?.Values.Count(c => classMaps || !c.IsClassMap) ?? 0;
            }
        }
        string extra = embedded > 0 ? $"{embedded} embedded image(s) skipped - export them as files first" : "";
        if (companions > 0) extra = (extra.Length > 0 ? extra + "; " : "") + $"{companions} companion image(s) resized with them";
        if (kept > 0) extra = (extra.Length > 0 ? extra + "; " : "") + $"{kept} class map(s) kept at full size (\"Resize class maps too\" is off)";
        return Summ(count, "texture(s) will be downscaled when saving", extra);
    }
}
