namespace SceneKit;

/// <summary>D3 replacement passes: a selected object becomes billboard / cross cards or one textured proxy, at the same
/// place and size. Undo brings the original back.</summary>
public static partial class Optimizer
{
    /// <summary>The geometry drawn below a target (nearest LOD level, switches as they are), in the target's parent
    /// frame (the target's own matrix applied), with the state set that applies to it.</summary>
    static List<(GeomData g, StateSet? ss, Node node)> DrawnBelow(OptContext ctx, Node target)
    {
        var above = ctx.ParentsOf(target).FirstOrDefault() is Node p ? StatesAbove(ctx, p) : new List<StateSet?>();
        var o = new List<(GeomData, StateSet?, Node)>();
        foreach (var (n, w, _, path) in SoftRender.GeometryPaths(target))
        {
            var ss = MergedState(above.Concat(path.Select(x => x.StateSet)));
            var (g, _) = Normalized(n.Geom!);
            if (g.Vertices == null || g.Triangles().Length == 0) continue;
            var P = Pos3(g.Vertices);
            var X = new double[P.Length];
            for (int i = 0; i < P.Length / 3; i++)
                for (int c = 0; c < 3; c++)
                    X[3 * i + c] = w[4 * c] * P[3 * i] + w[4 * c + 1] * P[3 * i + 1] + w[4 * c + 2] * P[3 * i + 2] + w[4 * c + 3];
            g.Vertices = new VArray(X, 3, "Vec3Array");
            o.Add((g, ss, n));
        }
        return o;
    }

    static string Label(Node n) => n.Name.Length > 0 ? n.Name : "object";

    /// <summary>Puts nw where old was. Inside a geode only drawables may go: a plain geode around one geometry is
    /// unwrapped there.</summary>
    static void ReplaceEverywhere(OptContext ctx, Node old, Node nw)
    {
        foreach (var p in ctx.ParentsOf(old).ToList())
        {
            var put = p.Kind == Kind.Geode && nw.Kind == Kind.Geode && nw.Cls == "osg::Geode" && nw.Children.Count == 1 ? nw.Children[0] : nw;
            ctx.Replace(p, old, new[] { put });
        }
    }

    /// <summary>A billboard cannot sit inside a geode: for a selected drawable, its geode is replaced when the drawable is
    /// all it holds; otherwise null.</summary>
    static Node? BillboardTarget(OptContext ctx, Node target)
    {
        var ps = ctx.ParentsOf(target);
        if (!ps.Any(p => p.Kind == Kind.Geode)) return target;
        return ps.Count == 1 && ps[0].Children.Count == 1 ? ps[0] : null;
    }

    static string ReplaceCards(OptContext ctx)
    {
        if (ctx.Targets == null) return "nothing changed: select the trees or bushes to replace with cards";
        var notes = new List<string>();
        var layout = CardLayout.FromContext(ctx, notes);
        int size = Math.Clamp(ctx.PInt("replace_cards", "texture"), 16, 8192);
        // today's axial billboard (a camera-facing osg::Billboard); a billboard with crown planes is one group (the billboard and
        // the fixed crown), placed whole: every copy, tilted too, shares it under its transform, never baked again
        bool share = ctx.PBool("replace_cards", "share"), billboard = layout.Mode == "billboard" && !(layout.Horizontal > 0 && !layout.Legacy);
        bool crowned = layout.Mode == "billboard" && layout.Horizontal > 0 && !layout.Legacy;   // a billboard turns whatever back / cells / padding say
        var fill = ExtraFill.Parse(ctx.PStr("replace_cards", "extra_fill"));
        var classFill = ExtraFill.ClassFill(ctx.PInt("replace_cards", "class_fill"));
        var cache = new Dictionary<string, RawImage?>();
        long before = 0, after = 0;
        int done = 0, skipped = 0, bakes = 0, crownedSkips = 0;
        var targets = new List<Node>();
        var cardBakes = new List<CardBake>();
        foreach (var selected in ctx.Targets.Distinct(ReferenceEqualityComparer.Instance).Cast<Node>())
        {
            var target = billboard || crowned ? BillboardTarget(ctx, selected) : selected;
            if (target == null || ctx.ParentsOf(target).Count == 0) { skipped++; continue; }
            if (!targets.Contains(target, ReferenceEqualityComparer.Instance)) targets.Add(target);
        }
        foreach (var (rep, copies) in CopySets(ctx, targets, share))
        {
            var placed = copies.Select(c => (c.node, m: CopyMatrix(ctx, rep, c.node, c.fit))).ToList();   // before the first one's place changes
            var built = BuildCards(ctx, rep, layout, size, cache, notes, fill, classFill);
            if (built == null) { skipped += 1 + copies.Count; continue; }
            var (holder, tris, cardTris, bake) = built.Value;
            Judge(ctx, holder, bake, cardBakes, notes);
            ReplaceEverywhere(ctx, rep, holder);
            before += tris; after += cardTris; done++; bakes++;
            foreach (var (copy, m) in placed)
            {
                var put = m == null ? null : billboard ? PlacedBillboard(holder, m, Label(copy)) : PlacedCopy(ctx, copy, m, holder, Label(copy) + " cards");
                if (put != null) { ReplaceEverywhere(ctx, copy, put); before += tris; after += cardTris; done++; continue; }
                if (crowned) { skipped++; crownedSkips++; continue; }
                var own = BuildCards(ctx, copy, layout, size, cache, notes, fill, classFill);   // e.g. a tilted copy of a billboard
                if (own == null) { skipped++; continue; }
                Judge(ctx, own.Value.holder, own.Value.bake, cardBakes, notes);
                ReplaceEverywhere(ctx, copy, own.Value.holder);
                before += own.Value.tris; after += own.Value.cardTris; done++; bakes++;
            }
        }
        ctx.Record.AddRange(CardRecord(cardBakes));
        string what = !layout.Legacy ? $"{layout.Label} cards" : billboard ? "an axial billboard" : "cross cards";
        string msg = Summ(done, "object(s) replaced with " + what, $"{PyFormat.Int(before)} -> {PyFormat.Int(after)} triangles");
        if (share && done > 0) msg += $"; {done} objects -> {bakes} atlases";
        // a new layout says first why an object was skipped (flat: nothing to stand cards on), then the generic reasons
        var why = layout.Legacy ? new List<string>() : notes.Distinct().Where(n => n.Contains(" skipped: ")).ToList();
        foreach (var w in why) msg += "; " + w;
        int unexplained = skipped - why.Count - crownedSkips;
        if (crownedSkips > 0) msg += $"; {crownedSkips} skipped (a tilted copy of a crowned billboard: inside a geode, cannot share the card group)";
        if (unexplained > 0) msg += $"; {unexplained} skipped (nothing drawn, the scene root, or a drawable that shares its geode: select the geode for a billboard)";
        if (done > 0 || !layout.Legacy) foreach (var note in notes.Distinct().Except(why)) msg += "; " + note;
        return msg;
    }

    /// <summary>The targets as sets of copies that look the same (one bake each); without share every target alone.</summary>
    static List<(Node rep, List<(Node node, double[] fit)> copies)> CopySets(OptContext ctx, List<Node> targets, bool share) =>
        share ? SimilarObjects.Copies(ctx.Parents, targets).Select(s => (s.Rep, s.Copies.Select(c => (c.Node, c.Fit)).ToList())).ToList()
              : targets.Select(t => (t, new List<(Node, double[])>())).ToList();

    /// <summary>The matrix that puts what was made for <paramref name="rep"/> (in its parent's frame) at the place of
    /// <paramref name="copy"/> (in the copy's parent's frame), from the world fit rep -> copy. Null when the copy's
    /// parent frame cannot be inverted.</summary>
    static double[]? CopyMatrix(OptContext ctx, Node rep, Node copy, double[] fit)
    {
        var inv = Mat.Invert(SoftRender.ParentMatrix(copy, ctx.Parents));
        return inv == null ? null : Mat.Mul(Mat.Mul(inv, fit), SoftRender.ParentMatrix(rep, ctx.Parents));
    }

    /// <summary>A node showing <paramref name="shared"/> at a copy's place: the shared node itself when nothing moves,
    /// else a MatrixTransform around it. Null when a transform cannot go there (the copy is a drawable in a geode).</summary>
    static Node? PlacedCopy(OptContext ctx, Node copy, double[] m, Node shared, string name)
    {
        if (Mat.IsIdentity(m, 1e-9, 1e-9)) return shared;
        if (ctx.ParentsOf(copy).Any(p => p.Kind == Kind.Geode)) return null;
        return new Node(Kind.Transform, "osg::MatrixTransform", name) { Matrix = m, Children = { shared } };
    }

    /// <summary>An axial billboard at a copy's place, sharing the card geometry: how the copy is turned about Z does not
    /// matter (the card turns to the viewer), its position and scale do. Null for a tilted copy.</summary>
    static Node? PlacedBillboard(Node holder, double[] m, string name)
    {
        double s = Math.Sqrt(m[0] * m[0] + m[4] * m[4]), tol = 1e-4 * s;
        if (s < 1e-12 || Math.Abs(m[2]) > tol || Math.Abs(m[6]) > tol || Math.Abs(m[8]) > tol || Math.Abs(m[9]) > tol
            || Math.Abs(m[0] - m[5]) > tol || Math.Abs(m[1] + m[4]) > tol || Math.Abs(m[10] - s) > tol) return null;
        var c = holder.Center ?? new double[3];
        var (x, y, z) = Mat.Apply(m, c[0], c[1], c[2]);
        if (Math.Abs(s - 1) < 1e-6)
            return new Node(Kind.Geode, "osg::Billboard", name + " billboard") { Center = new[] { x, y, z }, Children = { holder.Children[0] } };
        var t = Mat.Identity();
        t[0] = t[5] = t[10] = s; t[3] = x; t[7] = y; t[11] = z;
        var bb = new Node(Kind.Geode, "osg::Billboard", name + " billboard") { Center = new double[3], Children = { holder.Children[0] } };
        return new Node(Kind.Transform, "osg::MatrixTransform", name + " place") { Matrix = t, Children = { bb } };
    }

    /// <summary>Cards for everything drawn below a node, in its parent's frame: (node to put in its place, triangles
    /// before, card triangles, what was baked). Null when nothing is drawn (or, for a new layout, the object is flat). A
    /// <see cref="CardLayout.Legacy"/> layout builds today's crossed / billboard cards byte for byte; any other goes through
    /// <see cref="LayoutCards"/>. Texture units above 0 get atlases of their own, companion
    /// images (infrared) companion atlases; parts without such a layer get <paramref name="fill"/>, class maps
    /// <paramref name="classFill"/>. The card geometry carries the OpenFlight face codes of the source face covering the
    /// largest area.</summary>
    static (Node holder, long tris, long cardTris, CardBake bake)? BuildCards(OptContext ctx, Node target, CardLayout layout, int size,
        Dictionary<string, RawImage?> cache, List<string>? notes = null, ExtraFill? fill = null, ExtraFill? classFill = null)
    {
        fill ??= ExtraFill.White;
        classFill ??= ExtraFill.ClassFill(0);
        var drawn = DrawnBelow(ctx, target);
        if (drawn.Count == 0) return null;
        ctx.Progress($"cards for {Label(target)} ...");
        var items = drawn.Select(x => new Cards.Item
        {
            V = x.g.Vertices!.Data, T = x.g.Triangles(), UV = Uv0(x.g), Image = LoadImage(x.ss?.Textures.FirstOrDefault(t => t.Unit == 0), cache),
            Color = x.ss?.Diffuse is { } dc ? dc.Concat(new[] { 1.0, 1.0, 1.0, 1.0 }).Take(4).ToArray() : new[] { 1.0, 1.0, 1.0, 1.0 },
            VertexColors = VertexColors(x.g), Extra = ExtraUnits(x.g, x.ss, cache, notes),
        }).ToList();
        LayerNotes(items.Select(it => it.Extra), fill, classFill, notes);
        if (!layout.Legacy) return LayoutCards(ctx, target, layout, size, drawn, items, notes, fill, classFill);
        string mode = layout.Mode;
        int planes = layout.Planes;
        bool billboard = mode == "billboard";
        // the Billboard's card lies in the x-z plane facing -y: rendered looking along +y (azimuth 90)
        var az = billboard ? new List<double> { 90 }
            : Enumerable.Range(0, planes).SelectMany(p => new[] { 180.0 * p / planes, 180.0 * p / planes + 180 }).ToList();
        var res = Cards.Render(items, az, size, fill, classFill);
        if (res == null) return null;
        var c0 = billboard ? res.Center : new double[3];
        var V = new List<double>(); var N = new List<double>(); var UV = new List<double>(); var T = new List<int>();
        foreach (var card in res.Cards)
        {
            double rx = card.Dir[1], ry = -card.Dir[0];
            int b = V.Count / 3;
            foreach (var (s, z, u, v) in new[] { (card.S0, card.Z0, card.U0, card.V0), (card.S1, card.Z0, card.U1, card.V0), (card.S1, card.Z1, card.U1, card.V1), (card.S0, card.Z1, card.U0, card.V1) })
            {
                V.AddRange(new[] { res.Center[0] + rx * s - c0[0], res.Center[1] + ry * s - c0[1], z - c0[2] });
                N.AddRange(new[] { 0.0, 0.0, 1.0 });   // lit like the ground below, from every side
                UV.AddRange(new[] { u, v });
            }
            T.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        var geo = CardGeometry(ctx, target, V, N, UV, T, res, drawn, items, notes, billboard, ownMips: layout.AlphaMips == "coverage");
        string name = Label(target);
        ctx.MadeFrom(geo, drawn[0].g, billboard ? "axial billboard card" : $"{planes} cross cards");
        var holder = billboard
            ? new Node(Kind.Geode, "osg::Billboard", name + " billboard") { Center = (double[])res.Center.Clone(), Children = { geo } }
            : new Node(Kind.Geode, "osg::Geode", name + " cards") { Children = { geo } };
        var bake = new CardBake { Layout = layout, Size = size, Pictures = res.Cards.Count, CellPx = CellPx(res), Triangles = T.Count / 3, Geometry = geo };
        MeasureBake(bake, res, geo, items, classFill);
        return (holder, drawn.Sum(x => (long)x.g.Triangles().Length / 3), T.Count / 3, bake);
    }

    /// <summary>R3-C9: what a bake keeps of its leaf area at 1/2, 1/4 and 1/8 size (one <see cref="AlphaMips.Measure"/> on
    /// the base atlas with its cells; the written bytes do not change), who makes the alpha mips, the atlas extension and
    /// the parts whose class map was filled with <paramref name="classFill"/>.</summary>
    static void MeasureBake(CardBake bake, Cards.Result res, Node geo, List<Cards.Item> items, ExtraFill classFill)
    {
        var atlas = geo.StateSet?.Textures.FirstOrDefault(t => t.Unit == 0);
        bake.Ext = atlas == null ? "" : System.IO.Path.GetExtension(atlas.File).ToLowerInvariant();
        bake.Mips = bake.Layout.AlphaMips != "coverage" ? "renderer" : bake.Ext == ".dds" ? "coverage written" : $"coverage not written: {bake.Ext}";
        if (res.Atlas != null)
        {
            var (box, kept) = AlphaMips.Measure(res.Atlas, res.Cells);
            bake.CoverageBox = box; bake.CoverageKept = kept;
        }
        else bake.CoverageBox = bake.CoverageKept = new[] { double.NaN, double.NaN, double.NaN };
        var classKeys = ClassKeys(items.Select(it => it.Extra));
        bake.ClassFilledParts = classKeys.Count == 0 ? 0 : items.Count(it => classKeys.Any(k => !it.Extra.ContainsKey(k)));
        bake.ClassFillId = classFill.ClassId ?? 0;
    }

    /// <summary>A percentage for the verdict lines. Within 0.5 points of the 80 % limit one decimal, rounded down, so a Check
    /// never reads "80 %" (79.96 shows 79.9).</summary>
    internal static string Pct(double v)
    {
        if (!double.IsFinite(v)) return "?";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double pct = 100 * v;
        return Math.Abs(pct - 80) < 0.5 ? (Math.Floor(pct * 10 + 1e-9) / 10).ToString("0.0", inv) : Math.Round(pct).ToString("0", inv);
    }

    static bool Measured(double[]? a) => a is { Length: >= 3 } && a.Take(3).All(double.IsFinite);

    /// <summary>The bake's notes: the measured leaf area, and with alpha_mips coverage on a format without mips, that they
    /// were not written.</summary>
    static IEnumerable<string> CardNotes(CardBake b)
    {
        if (Measured(b.CoverageBox) && Measured(b.CoverageKept))
            yield return $"leaf area at 1/2, 1/4, 1/8 size: box {string.Join("/", b.CoverageBox.Take(3).Select(Pct))} %, kept {string.Join("/", b.CoverageKept.Take(3).Select(Pct))} %";
        if (b.Mips.StartsWith("coverage not written", StringComparison.Ordinal))
            yield return $"coverage mips not written: {b.Ext} holds one level; the renderer's mips keep {(Measured(b.CoverageBox) ? Pct(b.CoverageBox[2]) : "?")} % of the leaf area at 1/8 size";
    }

    /// <summary>The cards verdict of one bake (house style, never OK on something not measured): OK when the leaf area at
    /// 1/8 size (the coverage-kept chain when it is written into a .dds, else the box-filtered one the renderer makes) is
    /// at least 80 % and no part's class map was filled; otherwise Check with one warning per reason.</summary>
    internal static QualityReport CardVerdict(CardBake b)
    {
        var q = new QualityReport { Verdict = "check" };
        bool coverage = b.Mips == "coverage written";
        var shownChain = coverage ? b.CoverageKept : b.CoverageBox;
        if (!Measured(shownChain))
        {
            q.Warnings.Add("leaf area not measured");
            if (b.ClassFilledParts > 0) q.Warnings.Add($"class map: {b.ClassFilledParts} parts filled with class {b.ClassFillId}");
            return q;
        }
        double shown = shownChain[2];
        string p = Pct(shown);
        q.Headline = $"leaf area at distance {p} %";
        if (shown < 0.80)
        {
            if (b.Ext != ".dds") q.Warnings.Add($"leaf area at distance {p} %: renderer mips will thin the leaves; use .dds with keep_coverage");
            else if (!coverage) q.Warnings.Add($"leaf area at distance {p} %: write .dds with coverage mips, or use more texture");
            else q.Warnings.Add($"leaf area at distance {p} %: use more texture");
        }
        if (b.ClassFilledParts > 0) q.Warnings.Add($"class map: {b.ClassFilledParts} parts filled with class {b.ClassFillId}");
        if (q.Warnings.Count == 0) q.Verdict = "ok";
        return q;
    }

    /// <summary>The cards.* record keys of a run's bakes: layout, triangles and pictures (the largest bake), the smallest
    /// cell, the worst leaf area per column (two decimals) and who makes the mips ("mixed" when the bakes differ).</summary>
    internal static List<string> CardRecord(IReadOnlyList<CardBake> bakes)
    {
        var o = new List<string>();
        if (bakes.Count == 0) return o;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var labels = bakes.Select(b => b.Layout.Label).Distinct().ToList();
        o.Add($"cards.layout={(labels.Count == 1 ? labels[0] : "mixed")}");
        o.Add($"cards.triangles={bakes.Max(b => b.Triangles)}");
        o.Add($"cards.pictures={bakes.Max(b => b.Pictures)}");
        o.Add($"cards.cell_px={bakes.Min(b => b.CellPx)}");
        string Worst(Func<CardBake, double[]> col, int k)
        {
            var v = bakes.Select(col).Where(a => a is { Length: >= 3 }).Select(a => a[k]).Where(double.IsFinite).ToList();
            return v.Count == 0 ? "nan" : v.Min().ToString("0.00", inv);
        }
        o.Add($"cards.coverage=box {string.Join("/", Enumerable.Range(0, 3).Select(k => Worst(b => b.CoverageBox, k)))} kept {string.Join("/", Enumerable.Range(0, 3).Select(k => Worst(b => b.CoverageKept, k)))}");
        var mips = bakes.Select(b => b.Mips).Distinct().ToList();
        o.Add($"cards.mips={(mips.Count == 1 ? mips[0] : "mixed")}");
        return o;
    }

    /// <summary>The geometry nodes below a holder (the change list's nodes: a crowned billboard has two).</summary>
    static IEnumerable<Node> GeometriesBelow(Node n)
    {
        if (n.Kind == Kind.Geometry) yield return n;
        foreach (var c in n.Children)
            foreach (var g in GeometriesBelow(c)) yield return g;
    }

    /// <summary>A bake's verdict on every card geometry its holder shows, its notes and its place in the record.</summary>
    static void Judge(OptContext ctx, Node holder, CardBake bake, List<CardBake> bakes, List<string> notes)
    {
        var q = CardVerdict(bake);
        foreach (var g in GeometriesBelow(holder)) ctx.Verdicts[g] = q;
        notes.AddRange(CardNotes(bake));
        bakes.Add(bake);
    }

    static int CellPx(Cards.Result res) => res.Cells.Count == 0 ? 0 : res.Cells.Min(c => Math.Min(c.W, c.H));

    /// <summary>The card geometry both card paths share: the quads (V, N, UV, T) as triangles, the cut-out state set
    /// (alpha test, back faces culled) with the baked atlas on unit 0 and the extra units / companions, marked new and
    /// carrying the OpenFlight face codes of the source face covering the largest area. A billboard's drawable is kept
    /// out of merging and flattening. With <paramref name="shared"/> (a second geometry of the same bake) the state set
    /// takes that one's baked Texture objects instead of baking again. <paramref name="ownMips"/> (alpha_mips coverage):
    /// the atlas keeps its passing share per cell in its own mips, extra units are box-averaged, class maps nearest
    /// (written into a .dds; other formats leave the mipmaps to the renderer).</summary>
    static Node CardGeometry(OptContext ctx, Node target, List<double> V, List<double> N, List<double> UV, List<int> T, Cards.Result res,
        List<(GeomData g, StateSet? ss, Node node)> drawn, List<Cards.Item> items, List<string>? notes, bool billboard = false, StateSet? shared = null,
        bool ownMips = false)
    {
        var g = new GeomData { Vertices = new VArray(V.ToArray(), 3, "Vec3Array"), Normals = new VArray(N.ToArray(), 3, "Vec3Array") };
        g.TexCoords[0] = new VArray(UV.ToArray(), 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Count, indices: T.ToArray(), indexType: MeshOps.SmallestIndexType(V.Count / 3)));
        string name = Label(target);
        var ss = new StateSet
        {
            MaterialName = name + " cards", Diffuse = new[] { 1.0, 1.0, 1.0, 1.0 },
            Modes = { ["GL_ALPHA_TEST"] = "ON", ["GL_CULL_FACE"] = "ON" },
        };
        var states = drawn.Select(x => x.ss).ToList();
        if (shared != null)
        {
            ss.Textures.AddRange(shared.Textures);
            foreach (var t in shared.Textures.Where(t => t.Unit > 0)) g.TexCoords[t.Unit] = new VArray(UV.ToArray(), 2, "Vec2Array");
        }
        else
        {
            ss.Textures.Add(BakedTexture(name + "_cards", 0, res.Atlas, states, ctx.Scene, notes, coverage: true,
                mips: ownMips ? MipRule.Coverage : MipRule.Renderer, cells: ownMips ? res.Cells : null));
            AddExtraUnits(g, ss, res.Extra, name + "_cards", states, ctx.Scene, notes, res.Atlas, ClassKeys(items.Select(it => it.Extra)), ownMips);
        }
        // like loaded billboards: merging or flattening would move the card away from the billboard's position
        if (billboard) { g.Optimizable = false; g.Notes.Add("drawable of an osg::Billboard: kept as it is"); }
        var geo = new Node(Kind.Geometry, "osg::Geometry", name + " cards") { Geom = g, StateSet = ss };
        geo.Mark("geometry");
        FltFaceCodes.Carry(geo, drawn.Select(x => (x.node, AreaScale(x.g, x.node))), ctx.Scene, notes);
        return geo;
    }

    /// <summary>The cards of a new layout (<see cref="CardLayout.Legacy"/> false): one picture per view of
    /// <see cref="CardLayout.Views"/> in a power-of-two atlas, one quad per picture at Center + A Right + B Up + Offset Dir,
    /// wound to face the viewer (-Dir), with its cell's UVs (half a texel inside) and the up normal of today's cards. A
    /// flat object (no height) is skipped with a note.</summary>
    static (Node holder, long tris, long cardTris, CardBake bake)? LayoutCards(OptContext ctx, Node target, CardLayout layout, int size,
        List<(GeomData g, StateSet? ss, Node node)> drawn, List<Cards.Item> items, List<string>? notes, ExtraFill fill, ExtraFill classFill)
    {
        string name = Label(target);
        var (lo, hi) = Cards.Bounds(items);
        if (!(hi[2] > lo[2]))
        {
            notes?.Add($"{name} skipped: it is flat, so there is no height to stand cards on");
            return null;
        }
        int atlas = CardLayout.NextPow2(size);
        var res = Cards.Render(items, layout.Views(lo, hi).ToList(), atlas, layout.Cells == "by_area" ? Cards.CellRule.ByArea : Cards.CellRule.Equal,
            fill, classFill, layout.Padding == "full" ? Cards.Padding.Full : Cards.Padding.Eight);
        if (res == null)
        {
            // only with no views left: a box on an object without footprint (a pole along z) has no face with area
            notes?.Add($"{name} skipped: it has no footprint, so no card has an area");
            return null;
        }
        if (res.Cells.Any(c => Math.Min(c.W, c.H) < 8)) notes?.Add("pictures smaller than 8 px: use more texture");
        if (layout.Mode == "box" && res.Cards.Count < layout.Pictures)
            notes?.Add($"{layout.Pictures - res.Cards.Count} box faces left out: the object is flat there, so they have no area");
        // a billboard with crown planes: its vertical card goes to a camera-facing osg::Billboard (around the render centre),
        // the crown quads stay fixed; both are one group and one bake
        bool crowned = layout.Mode == "billboard" && layout.Horizontal > 0, plainBillboard = layout.Mode == "billboard" && !crowned;
        var fixedQ = (V: new List<double>(), N: new List<double>(), UV: new List<double>(), T: new List<int>());
        var turnQ = (V: new List<double>(), N: new List<double>(), UV: new List<double>(), T: new List<int>());
        foreach (var card in res.Cards)
        {
            bool turns = layout.Mode == "billboard" && Math.Abs(card.Dir[2]) < 0.5;
            var (V, N, UV, T) = turns ? turnQ : fixedQ;
            var c0 = turns ? res.Center : new double[3];
            int b = V.Count / 3;
            foreach (var (a, h, u, v) in new[] { (card.A0, card.B0, card.U0, card.V0), (card.A1, card.B0, card.U1, card.V0), (card.A1, card.B1, card.U1, card.V1), (card.A0, card.B1, card.U0, card.V1) })
            {
                for (int c = 0; c < 3; c++) V.Add(res.Center[c] + a * card.Right[c] + h * card.Up[c] + card.Offset * card.Dir[c] - c0[c]);
                N.AddRange(new[] { 0.0, 0.0, 1.0 });   // lit like the ground below, as today's cards
                UV.AddRange(new[] { u, v });
            }
            // the corners run along Right then Up: their winding faces Right x Up, which must be -Dir (towards the viewer)
            var n = new[] { card.Right[1] * card.Up[2] - card.Right[2] * card.Up[1], card.Right[2] * card.Up[0] - card.Right[0] * card.Up[2], card.Right[0] * card.Up[1] - card.Right[1] * card.Up[0] };
            bool facing = n[0] * card.Dir[0] + n[1] * card.Dir[1] + n[2] * card.Dir[2] < 0;
            T.AddRange(facing ? new[] { b, b + 1, b + 2, b, b + 2, b + 3 } : new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            // back = mirror: a vertical plane's back quad sits on the same corners, faces +Dir and shows the same cell with U flipped
            // (seen from behind, the picture would run backwards otherwise); horizontal planes and the box have no mirrored back
            if (layout.Back == "mirror" && layout.Mode is "crossed" or "layered" && Math.Abs(card.Dir[2]) < 0.5)
            {
                int m = V.Count / 3;
                for (int k = 0; k < 4; k++)
                {
                    for (int c = 0; c < 3; c++) V.Add(V[3 * (b + k) + c]);
                    N.AddRange(new[] { 0.0, 0.0, 1.0 });
                    UV.AddRange(new[] { card.U0 + card.U1 - UV[2 * (b + k)], UV[2 * (b + k) + 1] });
                }
                T.AddRange(facing ? new[] { m, m + 2, m + 1, m, m + 3, m + 2 } : new[] { m, m + 1, m + 2, m, m + 2, m + 3 });
            }
        }
        var geo = plainBillboard
            ? CardGeometry(ctx, target, turnQ.V, turnQ.N, turnQ.UV, turnQ.T, res, drawn, items, notes, billboard: true, ownMips: layout.AlphaMips == "coverage")
            : CardGeometry(ctx, target, fixedQ.V, fixedQ.N, fixedQ.UV, fixedQ.T, res, drawn, items, notes, ownMips: layout.AlphaMips == "coverage");
        int tris = (fixedQ.T.Count + turnQ.T.Count) / 3, pictures = res.Cards.Count;
        string counts = layout.Mode == "box" ? $"{pictures} faces"
            : layout.Horizontal > 0 ? $"{layout.VerticalCount} + {layout.Horizontal} cards" : $"{layout.VerticalCount} cards";
        // the layout key is recorded in the note for a future reuse path; nothing checks it today (legacy holders carry no key)
        ctx.MadeFrom(geo, drawn[0].g, $"{layout.Label}: {counts}, {tris} triangles, {pictures} pictures in {atlas} px; layout={layout.Key}");
        if (layout.Horizontal > 0 && layout.Mode != "box")
        {
            if (layout.Faces == "top") notes?.Add("the crown's underside is not baked: from below, horizontal planes are invisible");
            double first = layout.Heights.Count > 0 ? layout.Heights[0] * (hi[2] - lo[2]) : 0;
            notes?.Add($"lowest horizontal plane {first.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} m above the base");
        }
        var holder = plainBillboard
            ? new Node(Kind.Geode, "osg::Billboard", name + " billboard") { Center = (double[])res.Center.Clone(), Children = { geo } }
            : new Node(Kind.Geode, "osg::Geode", name + " cards") { Children = { geo } };
        if (crowned)
        {
            var bbGeo = CardGeometry(ctx, target, turnQ.V, turnQ.N, turnQ.UV, turnQ.T, res, drawn, items, notes, billboard: true, shared: geo.StateSet);
            ctx.MadeFrom(bbGeo, drawn[0].g, $"axial billboard card; layout={layout.Key}");
            var bb = new Node(Kind.Geode, "osg::Billboard", name + " billboard") { Center = (double[])res.Center.Clone(), Children = { bbGeo } };
            holder = new Node(Kind.Group, "osg::Group", name + " cards") { Children = { bb, holder } };
        }
        var bake = new CardBake { Layout = layout, Size = atlas, Pictures = pictures, CellPx = CellPx(res), Triangles = tris, Geometry = geo };
        MeasureBake(bake, res, geo, items, classFill);
        return (holder, drawn.Sum(x => (long)x.g.Triangles().Length / 3), tris, bake);
    }

    /// <summary>Texture units above 0 on a new mesh: unit 0's UVs (copied) and the baked image per unit; then the baked
    /// companion images, kept with the new texture of their unit (saving writes them next to it, named by their pattern).
    /// <paramref name="baseImg"/>: the new base atlas, whose alpha (coverage) companions with alpha take.
    /// <paramref name="classMaps"/>: the layers baked from class maps; their baked textures are class maps too.
    /// <paramref name="ownMips"/>: the layers get their own mips (box-averaged; class maps nearest).</summary>
    static void AddExtraUnits(GeomData g, StateSet ss, Dictionary<LayerKey, RawImage> extra, string file, IReadOnlyList<StateSet?> states,
        Scene? scene, List<string>? notes, RawImage? baseImg = null, ISet<LayerKey>? classMaps = null, bool ownMips = false)
    {
        foreach (var (k, img) in LayerKey.Sort(extra, kv => kv.Key))
        {
            if (k.Companion != null) continue;
            int u = k.Unit;
            g.TexCoords[u] = new VArray((double[])g.TexCoords[0].Data.Clone(), 2, "Vec2Array");
            bool isClass = classMaps?.Contains(k) == true;
            var t = BakedTexture($"{file}_u{u}", u, img, states, scene, notes,
                mips: !ownMips ? MipRule.Renderer : isClass ? MipRule.Nearest : MipRule.Box);
            t.IsClassMap = isClass;
            ss.Textures.Add(t);
        }
        foreach (var (k, img) in LayerKey.Sort(extra, kv => kv.Key))
        {
            if (k.Companion == null) continue;
            var owner = ss.Textures.FirstOrDefault(t => t.Unit == k.Unit && t.Baked != null);
            if (owner == null) { notes?.Add($"{k} not baked: the unit has no baked texture"); continue; }
            var comp = BakedCompanion(owner, k.Companion, img, baseImg, states, scene, notes, classMaps?.Contains(k) == true, ownMips);
            owner.Companions = new Dictionary<string, Texture>(owner.Companions ?? new Dictionary<string, Texture>()) { [k.Companion] = comp };
        }
    }

    /// <summary>A baked companion atlas of <paramref name="owner"/> (a baked atlas) under a pattern: named by the pattern
    /// applied to the owner's file, written in the format of the companion images it was baked from (raw: their channel
    /// layout); with alpha, that alpha is the base atlas's (coverage). <paramref name="ownMips"/>: its own mips, box-averaged
    /// (class maps nearest).</summary>
    internal static Texture BakedCompanion(Texture owner, string pattern, RawImage img, RawImage? baseImg, IEnumerable<StateSet?> states,
        Scene? scene, List<string>? notes, bool classMap = false, bool ownMips = false)
    {
        var c = new Texture
        {
            File = Companions.Apply(pattern, owner.File), Unit = owner.Unit, IsCompanion = true, IsClassMap = classMap || Companions.IsClassMap(pattern), Baked = img, Width = img.Width, Height = img.Height,
            Channels = 4, Exists = true, WrapS = owner.WrapS, WrapT = owner.WrapT,
            BakedFrom = TextureFormats.Sources(states.Select(st => st?.Textures.FirstOrDefault(x => x.Unit == owner.Unit)?.Companions?.GetValueOrDefault(pattern))),
        };
        if (ownMips) c.Mips = c.IsClassMap ? MipRule.Nearest : MipRule.Box;
        var f = TextureFormats.Choose(c, scene, scene?.Flt != null);
        if (f.Note != null) notes?.Add(f.Note);
        c.Channels = f.Channels;
        if (f.Channels is 2 or 4 && baseImg != null && baseImg.Width == img.Width && baseImg.Height == img.Height && baseImg.Channels == 4 && img.Channels == 4)
        {
            var a = new RawImage(img.Width, img.Height, 4);
            Array.Copy(img.Pixels, a.Pixels, a.Pixels.Length);
            for (int i = 3; i < a.Pixels.Length; i += 4) a.Pixels[i] = baseImg.Pixels[i];
            c.Baked = a;
        }
        c.Fmt = System.IO.Path.GetExtension(c.File).ToLowerInvariant().TrimStart('.');
        return c;
    }

    /// <summary>Notes for layers (texture units above 0, companions) that only some parts have: the others get the fill
    /// (class maps: the class fill).</summary>
    static void LayerNotes(IEnumerable<Dictionary<LayerKey, (double[] UV, RawImage Image, bool ClassMap)>> perPart, ExtraFill fill, ExtraFill classFill, List<string>? notes)
    {
        if (notes == null) return;
        var list = perPart.ToList();
        var classMaps = ClassKeys(list);
        var parts = list.Select(d => d.Keys.ToHashSet()).ToList();
        foreach (var k in LayerKey.Sort(parts.SelectMany(p => p).Distinct(), k => k))
        {
            int without = parts.Count(p => !p.Contains(k));
            if (without > 0) notes.Add($"{k}: {without} of {parts.Count} part(s) without it baked with " + (classMaps.Contains(k) ? $"{classFill}" : $"{fill} (extra_fill)"));
        }
    }

    /// <summary>The layers baked from class maps (in any part).</summary>
    static HashSet<LayerKey> ClassKeys(IEnumerable<Dictionary<LayerKey, (double[] UV, RawImage Image, bool ClassMap)>> perPart) =>
        perPart.SelectMany(d => d).Where(kv => kv.Value.ClassMap).Select(kv => kv.Key).ToHashSet();

    /// <summary>How a geometry's placement scales its area: area as drawn / area of the node's own geometry.</summary>
    static double AreaScale(GeomData drawn, Node node)
    {
        double a = FltFaceCodes.Area(node.Geom);
        return a > 0 ? FltFaceCodes.Area(drawn) / a : 1;
    }

    /// <summary>A baked atlas on texture unit <paramref name="unit"/>, remembering the textures that unit had in the
    /// source states (saving writes it in their format, see TextureFormats). The file name gets the extension it will be
    /// written with in the scene's own format; a format change is noted. <paramref name="mips"/> / <paramref name="cells"/>:
    /// the texture's mip rule (R3-C8; written into a .dds only).</summary>
    internal static Texture BakedTexture(string stem, int unit, RawImage? img, IEnumerable<StateSet?> states, Scene? scene,
        List<string>? notes, bool coverage = false, MipRule mips = MipRule.Renderer, IReadOnlyList<Cards.CellRect>? cells = null)
    {
        var t = new Texture
        {
            File = stem, Unit = unit, Baked = img, Width = img?.Width ?? 0, Height = img?.Height ?? 0, Channels = 4, Exists = img != null,
            WrapS = Texture.AtlasWrap, WrapT = Texture.AtlasWrap, BakedCoverage = coverage, Mips = mips, MipCells = cells,
            BakedFrom = TextureFormats.Sources(states.Select(st => st?.Textures.FirstOrDefault(x => x.Unit == unit))),
        };
        if (img == null) { t.File = stem + ".png"; t.Fmt = "png"; return t; }
        var f = TextureFormats.Choose(t, scene, scene?.Flt != null);
        t.File = stem + f.Ext;
        t.Fmt = f.Ext.TrimStart('.');
        t.Channels = f.Channels;
        if (f.Note != null) notes?.Add(f.Note);
        return t;
    }

    static string ReplaceProxy(OptContext ctx)
    {
        if (ctx.Targets == null) return "nothing changed: select the objects to replace with a textured proxy";
        var pp = ProxyParams.FromPassParams(ctx.Params);
        if (pp.Parts == ProxyParts.PerPart) return ReplacePartProxy(ctx, pp);
        double ratio = ctx.PDouble("replace_proxy", "ratio");
        int size = Math.Clamp(ctx.PInt("replace_proxy", "texture"), 16, 8192);
        int voxels = ctx.PInt("replace_proxy", "voxels");
        bool share = ctx.PBool("replace_proxy", "share");
        var fill = ExtraFill.Parse(ctx.PStr("replace_proxy", "extra_fill"));
        var classFill = ExtraFill.ClassFill(ctx.PInt("replace_proxy", "class_fill"));
        var cache = new Dictionary<string, RawImage?>();
        var notes = new List<string>();
        var failed = new List<string>();   // within: "could not stay within it", said even when nothing was replaced
        long before = 0, after = 0;
        int done = 0, skipped = 0, simpler = 0, bakes = 0;
        var merged = new List<string>();
        var targets = new List<Node>();
        foreach (var target in ctx.Targets.Distinct(ReferenceEqualityComparer.Instance).Cast<Node>())
            if (ctx.ParentsOf(target).Count == 0) skipped++; else targets.Add(target);
        foreach (var (rep, copies) in CopySets(ctx, targets, share))
        {
            var placed = copies.Select(c => (c.node, m: CopyMatrix(ctx, rep, c.node, c.fit))).ToList();   // before the first one's place changes
            var built = BuildProxy(ctx, rep, ratio, size, voxels, cache, notes, out bool keep, fill, classFill, pp, failed);
            if (built == null) { if (keep) simpler += 1 + copies.Count; else skipped += 1 + copies.Count; continue; }
            var (holder, tris, proxyTris) = built.Value;
            foreach (var n in MovingBelow(rep)) if (!merged.Contains(n.Name)) merged.Add(n.Name);
            ReplaceEverywhere(ctx, rep, holder);
            before += tris; after += proxyTris; done++; bakes++;
            foreach (var (copy, m) in placed)
            {
                var put = m == null ? null : PlacedCopy(ctx, copy, m, holder, Label(copy) + " proxy");
                if (put != null) { ReplaceEverywhere(ctx, copy, put); before += tris; after += proxyTris; done++; continue; }
                var own = BuildProxy(ctx, copy, ratio, size, voxels, cache, notes, out keep, fill, classFill, pp, failed);
                if (own == null) { if (keep) simpler++; else skipped++; continue; }
                ReplaceEverywhere(ctx, copy, own.Value.holder);
                before += own.Value.tris; after += own.Value.proxyTris; done++; bakes++;
            }
        }
        string msg = Summ(done, "object(s) replaced with a textured proxy", $"{PyFormat.Int(before)} -> {PyFormat.Int(after)} triangles");
        if (share && done > 0) msg += $"; {done} objects -> {bakes} atlases";
        if (skipped > 0) msg += $"; {skipped} skipped (nothing drawn, the scene root, or no hull could be built)";
        if (simpler > 0) msg += $"; {simpler} kept (already simpler than a proxy would be)";
        if (merged.Count > 0) msg += $"; moving parts merged into the hull: {string.Join(", ", merged)} (parts = per_part keeps them)";
        if (pp.Ground != ProxyGround.Full) msg += "; ground applies with parts = per_part";
        if (done > 0) foreach (var note in notes.Distinct()) msg += "; " + note;
        foreach (var note in failed.Distinct()) msg += "; " + note;
        return msg;
    }

    /// <summary>The moving nodes below a target (the outermost ones, in scene order) that have something drawn below them:
    /// what a single hull merges.</summary>
    static List<Node> MovingBelow(Node target)
    {
        var o = new List<Node>();
        var stack = new Stack<Node>();
        for (int i = target.Children.Count - 1; i >= 0; i--) stack.Push(target.Children[i]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.NodeMask == 0) continue;
            if (Moving(n)) { if (SoftRender.GeometryItems(n, maxItems: 1).Count > 0 && !o.Contains(n)) o.Add(n); continue; }
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
        return o;
    }

    /// <summary>The largest scale of an object's world matrices (takes e into the object's parent frame); 1 when it is not usable.</summary>
    static double ObjectWorldScale(OptContext ctx, Node obj)
    {
        double s = InstanceMatrices(obj, ctx.Parents).Max(Mat.MaxScale);
        return s > 0 && double.IsFinite(s) ? s : 1;
    }

    /// <summary>The discovery gap of a per-part build: e in the object's frame (within), else 2 cells of its grid (extent / voxels).</summary>
    internal static double KeepGap(double eWorld, double worldScale, double extent, int voxels) =>
        eWorld > 0 ? eWorld / worldScale : 2 * extent / Math.Clamp(voxels, 16, 512);

    /// <summary>The first words of a per-part result line, for one object or the sum of several.</summary>
    static string PartLineHead(long n, long before, long after, long drawBefore, long drawAfter) =>
        $"{n} object(s) replaced with a per-part proxy: {PyFormat.Int(before)} -> {PyFormat.Int(after)} triangles, draw calls {PyFormat.Int(drawBefore)} -> {PyFormat.Int(drawAfter)}";

    /// <summary>replace_proxy with parts = per_part: one per-part proxy per target (share: per representative, placed at
    /// every copy as today). The message sums the objects and adds the first one's detail.</summary>
    static string ReplacePartProxy(OptContext ctx, ProxyParams pp)
    {
        int size = Math.Clamp(ctx.PInt("replace_proxy", "texture"), 16, 8192);
        bool share = ctx.PBool("replace_proxy", "share");
        var fill = ExtraFill.Parse(ctx.PStr("replace_proxy", "extra_fill"));
        var classFill = ExtraFill.ClassFill(ctx.PInt("replace_proxy", "class_fill"));
        var cache = new Dictionary<string, RawImage?>();
        var notes = new List<string>();
        long before = 0, after = 0, drawBefore = 0, drawAfter = 0;
        int done = 0, skipped = 0, simpler = 0, bakes = 0;
        string? detail = null;
        var targets = new List<Node>();
        foreach (var target in ctx.Targets!.Distinct(ReferenceEqualityComparer.Instance).Cast<Node>())
            if (ctx.ParentsOf(target).Count == 0) skipped++; else targets.Add(target);
        void Count((Node holder, long tris, long proxyTris, int drawBefore, int drawAfter, string line) b)
        {
            before += b.tris; after += b.proxyTris; drawBefore += b.drawBefore; drawAfter += b.drawAfter; done++;
            detail ??= b.line[PartLineHead(1, b.tris, b.proxyTris, b.drawBefore, b.drawAfter).Length..];
        }
        foreach (var (rep, copies) in CopySets(ctx, targets, share))
        {
            var placed = copies.Select(c => (c.node, m: CopyMatrix(ctx, rep, c.node, c.fit))).ToList();   // before the first one's place changes
            var built = BuildPartProxy(ctx, rep, pp, size, cache, notes, out bool keep, fill, classFill);
            if (built == null) { if (keep) simpler += 1 + copies.Count; else skipped += 1 + copies.Count; continue; }
            var holder = built.Value.holder;
            if (!ReferenceEquals(holder, rep)) ReplaceEverywhere(ctx, rep, holder);
            Count(built.Value); bakes++;
            foreach (var (copy, m) in placed)
            {
                var put = m == null ? null : PlacedCopy(ctx, copy, m, holder, Label(copy) + " proxy");
                if (put != null) { ReplaceEverywhere(ctx, copy, put); Count(built.Value); continue; }
                var own = BuildPartProxy(ctx, copy, pp, size, cache, notes, out keep, fill, classFill);
                if (own == null) { if (keep) simpler++; else skipped++; continue; }
                if (!ReferenceEquals(own.Value.holder, copy)) ReplaceEverywhere(ctx, copy, own.Value.holder);
                Count(own.Value); bakes++;
            }
        }
        string msg = done > 0 ? PartLineHead(done, before, after, drawBefore, drawAfter) + detail : "0 object(s) replaced with a per-part proxy";
        if (share && done > 0) msg += $"; {done} objects -> {bakes} atlases";
        if (skipped > 0) msg += $"; {skipped} skipped (nothing drawn, the scene root, or no hull could be built)";
        if (simpler > 0) msg += $"; {simpler} kept (already simpler than a proxy would be)";
        // the notes also when nothing was replaced: why (within: "could not stay within it (best ...); left unchanged")
        foreach (var note in notes.Distinct()) msg += "; " + note;
        return msg;
    }

    /// <summary>Per-part proxy for everything below a node: discovery with a gap of 2 cells of the object's grid (within: e),
    /// <see cref="PartProxy.BuildParts"/>, one atlas (<see cref="PartProxy.BakeAtlas"/>), re-assembly
    /// (<see cref="PartProxy.Assemble"/>). Returns (node to put in its place, which is the target itself when a moving
    /// target keeps its place; triangles before and after; draw calls before (the geometries drawn below it) and after
    /// (1 for the static parts, 1 per moving part, 1 per kept or decimated geometry: a part has one per piece with its own state set);
    /// triangles and draw calls on both sides count drawn geometry only, a switch child that is off is not drawn; the object's result line). Null when
    /// nothing is drawn or no proxy could be built, and (with <paramref name="simpler"/> set) when the proxy would have
    /// more triangles than the object. Nothing in the graph changes before the build succeeded.</summary>
    static (Node holder, long tris, long proxyTris, int drawBefore, int drawAfter, string line)? BuildPartProxy(OptContext ctx, Node target,
        ProxyParams pp, int size, Dictionary<string, RawImage?> cache, List<string> notes, out bool simpler, ExtraFill fill, ExtraFill classFill)
    {
        simpler = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var drawn = DrawnBelow(ctx, target);
        if (drawn.Count == 0) return null;
        string label = Label(target);
        ctx.Progress($"per-part proxy for {label} ...");
        // the keep-mode gap: 2 cells of the object's grid (its extent / voxels)
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var (g, _, _) in drawn)
            for (int i = 0; i < g.Vertices!.Data.Length / 3; i++)
                for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], g.Vertices.Data[3 * i + c]); hi[c] = Math.Max(hi[c], g.Vertices.Data[3 * i + c]); }
        // within: e from metres or pixels at a distance (screen and field of view as generate_lod reads them), taken into
        // the target's parent frame by the largest scale of its world matrix (as ErrorFrames does); the gap is e
        double worldScale = ObjectWorldScale(ctx, target);
        double eWorld = pp.Within ? pp.ErrorMetres(ctx.PInt("generate_lod", "screen_height"), Math.Clamp(ctx.PDouble("generate_lod", "fov"), 1, 179)) : 0;
        double gap = KeepGap(eWorld, worldScale, Enumerable.Range(0, 3).Max(c => hi[c] - lo[c]), pp.Voxels);
        var asm = PartProxy.Discover(target, gap);

        var above = ctx.ParentsOf(target).FirstOrDefault() is Node parent ? StatesAbove(ctx, parent) : new List<StateSet?>();
        var pieces = asm.AllPieces().ToList();
        var states = new Dictionary<PartProxy.Piece, StateSet?>(ReferenceEqualityComparer.Instance);
        foreach (var p in pieces) states[p] = MergedState(above.Concat(p.Path.Select(x => x.StateSet)));
        var sources = PieceSources(pieces, states, cache, notes);
        // the triangles drawn (a switch child that is off is not), as the draw calls count drawn geometry on both sides
        long tris = pieces.Where(PartProxy.DrawnPiece).Sum(p => (long)p.T.Length / 3);
        LayerNotes(sources.Select(s => s.Extra), fill, classFill, notes);
        var o = new PartProxy.BuildOptions
        {
            TextureSize = size, Fill = fill, ClassFill = classFill, Progress = ctx.Progress, Ground = pp.Ground, WorldScale = worldScale, ErrorMetres = eWorld,
            Normals = pp.Normals, Crease = pp.Crease,
            Plane = pp.Ground == ProxyGround.Cut ? PartProxy.GroundOf(asm, SoftRender.ParentMatrix(target, ctx.Parents)) : null,
        };
        var r = PartProxy.BuildParts(asm, sources, pp, o);
        if (r == null) { notes.AddRange(o.Notes); return null; }
        // a proxy of a low-poly object can need more triangles than the object itself: keep the object
        if (r.Report.TrisAfter >= tris) { simpler = true; return null; }
        notes.AddRange(o.Notes);   // within without a positive max_error says so here
        notes.AddRange(r.Notes);
        var (tex, extra) = PartProxy.BakeAtlas(new[] { r }, new[] { (IReadOnlyList<Proxy.Source>)sources }, o);

        var atlas = AtlasState(ctx, label, tex, extra, pieces.Select(p => states[p]).ToList(), sources, notes);
        var geos = MeshGeometries(ctx, r, atlas, label, notes);
        var made = AssembleParts(ctx, target, r, geos, states, label, notes);
        int drawAfter = made.DrawCalls;
        long proxyTris = r.Report.TrisAfter;
        string line = PartLineHead(1, tris, proxyTris, drawn.Count, drawAfter) + PartProxy.Detail(r, made);
        MarkMadeFrom(ctx, r, geos, made, line);
        r.Report = r.Report with { DrawCallsBefore = drawn.Count, DrawCallsAfter = drawAfter, BuildMs = watch.ElapsedMilliseconds };
        return (made.Holder, tris, proxyTris, drawn.Count, drawAfter, line);
    }

    /// <summary>One proxy source per piece (in the order given): positions and triangles of the piece, its unit 0
    /// coordinates and image, material and vertex colours, extra units and its authored normals.</summary>
    static List<Proxy.Source> PieceSources(IEnumerable<PartProxy.Piece> pieces, Dictionary<PartProxy.Piece, StateSet?> states,
        Dictionary<string, RawImage?> cache, List<string> notes) => pieces.Select(p => new Proxy.Source
        {
            V = p.V, T = p.T, UV = Uv0(p.G), Image = LoadImage(states[p]?.Textures.FirstOrDefault(t => t.Unit == 0), cache),
            Color = states[p]?.Diffuse is { } dc ? dc.Concat(new[] { 1.0, 1.0, 1.0, 1.0 }).Take(4).ToArray() : new[] { 1.0, 1.0, 1.0, 1.0 },
            VertexColors = VertexColors(p.G), Extra = ExtraUnits(p.G, states[p], cache, notes), N = PartProxy.PieceNormals(p),
        }).ToList();

    /// <summary>One state set for every proxy mesh of an atlas: the atlas and one image per extra unit or companion, as
    /// ProxyGeometry makes it; and the texture units the meshes need coordinates for (unit 0 and the extra ones).</summary>
    static (StateSet State, List<int> Units) AtlasState(OptContext ctx, string label, RawImage? tex, Dictionary<LayerKey, RawImage> extra,
        List<StateSet?> srcStates, IEnumerable<Proxy.Source> sources, List<string> notes)
    {
        var state = new StateSet { MaterialName = label + " proxy", Diffuse = new[] { 1.0, 1.0, 1.0, 1.0 } };
        if (tex != null) state.Textures.Add(BakedTexture(label + "_proxy", 0, tex, srcStates, ctx.Scene, notes));
        var units = new GeomData();
        units.TexCoords[0] = new VArray(Array.Empty<double>(), 2, "Vec2Array");
        AddExtraUnits(units, state, extra, label + "_proxy", srcStates, ctx.Scene, notes, tex, ClassKeys(sources.Select(s => s.Extra)));
        return (state, units.TexCoords.Keys.ToList());
    }

    /// <summary>A geometry node per proxy mesh of <paramref name="r"/> (filled by <see cref="PartProxy.BakeAtlas"/>), all
    /// with the atlas state set; the static one is "&lt;label&gt; proxy". OpenFlight face codes per part.</summary>
    static Dictionary<PartProxy.ProxyMesh, Node> MeshGeometries(OptContext ctx, PartProxy.ObjectResult r, (StateSet State, List<int> Units) atlas,
        string label, List<string> notes)
    {
        var geos = new Dictionary<PartProxy.ProxyMesh, Node>(ReferenceEqualityComparer.Instance);
        foreach (var mesh in r.Meshes)
        {
            var g = new GeomData { Vertices = new VArray(mesh.V, 3, "Vec3Array"), Normals = new VArray(mesh.N, 3, "Vec3Array") };
            foreach (var u in atlas.Units) g.TexCoords[u] = new VArray((double[])mesh.UV.Clone(), 2, "Vec2Array");
            g.Prims.Add(new Primitive("TRIANGLES", "elements", count: mesh.T.Length, indices: mesh.T, indexType: MeshOps.SmallestIndexType(mesh.V.Length / 3)));
            var geo = new Node(Kind.Geometry, "osg::Geometry", mesh.Owner == null ? label + " proxy" : mesh.Name) { Geom = g, StateSet = atlas.State };
            geo.Mark("geometry");
            var from = mesh.Owner == null ? r.Asm.Pieces : mesh.Owner.Inner.Pieces;   // face codes per part
            FltFaceCodes.Carry(geo, from.Select(p => (p.Geom, PieceAreaScale(p))), ctx.Scene, notes);
            geos[mesh] = geo;
        }
        return geos;
    }

    /// <summary><see cref="PartProxy.Assemble"/>, then the face codes of the kept geometries and a note per moving part
    /// kept as it is.</summary>
    static PartProxy.Assembled AssembleParts(OptContext ctx, Node target, PartProxy.ObjectResult r, Dictionary<PartProxy.ProxyMesh, Node> geos,
        Dictionary<PartProxy.Piece, StateSet?> states, string label, List<string> notes)
    {
        var made = PartProxy.Assemble(ctx, target, r, geos, p => states.GetValueOrDefault(p), label);
        foreach (var (geo, from) in made.Kept) FltFaceCodes.Carry(geo, new[] { (from.Geom, PieceAreaScale(from)) }, ctx.Scene, notes);
        foreach (var name in made.KeptAsIs) notes.Add($"{name} kept as it is (no proxy could be built)");
        foreach (var name in made.Animated) notes.Add($"{name} kept as it is (animated group)");
        return made;
    }

    /// <summary>Every geometry a per-part proxy made (proxy meshes and kept geometries) remembers what it was made from,
    /// with the object's result line.</summary>
    static void MarkMadeFrom(OptContext ctx, PartProxy.ObjectResult r, Dictionary<PartProxy.ProxyMesh, Node> geos, PartProxy.Assembled made, string line)
    {
        var first = r.Asm.AllPieces().First().G;
        foreach (var mesh in r.Meshes)
            ctx.MadeFrom(geos[mesh], (mesh.Owner == null ? r.Asm.Pieces : mesh.Owner.Inner.Pieces).FirstOrDefault()?.G ?? first, line);
        foreach (var (geo, from) in made.Kept) ctx.MadeFrom(geo, from.G, line);
    }

    /// <summary>How a piece's placement scales its area (as <see cref="AreaScale"/> for a drawn geometry).</summary>
    static double PieceAreaScale(PartProxy.Piece p)
    {
        var placed = p.G.ShallowCopy();
        placed.Vertices = new VArray(p.V, 3, "Vec3Array");
        return AreaScale(placed, p.Geom);
    }

    /// <summary>Textured proxy for everything drawn below a node, in its parent's frame: (node to put in its place,
    /// triangles before, proxy triangles). Null when nothing is drawn or no hull could be built, and (with
    /// <paramref name="simpler"/> set) when the object already has fewer triangles than its proxy would. With
    /// <c>target = within</c> (<paramref name="pp"/>) the single hull is built within e (metres or pixels at a distance,
    /// taken into the node's parent frame by the largest scale of its world matrix) with the tighten loop generate_lod's
    /// proxy_from uses (<see cref="TightenWithin{T}"/>, each attempt measured with <see cref="Quality.Compare"/> against
    /// the drawn original): "within 4 cm (reached 3.2 cm)" goes to <paramref name="notes"/>; when no attempt stays within
    /// e the node is left unchanged (null) and "Within 4 cm: could not stay within it (best 6.1 cm); left unchanged" goes
    /// to <paramref name="failed"/>.</summary>
    static (Node holder, long tris, long proxyTris)? BuildProxy(OptContext ctx, Node target, double ratio, int size, int voxels,
        Dictionary<string, RawImage?> cache, List<string> notes, out bool simpler, ExtraFill? fill = null, ExtraFill? classFill = null, ProxyParams? pp = null,
        List<string>? failed = null)
    {
        pp ??= new ProxyParams();
        fill ??= ExtraFill.White;
        classFill ??= ExtraFill.ClassFill(0);
        simpler = false;
        var drawn = DrawnBelow(ctx, target);
        if (drawn.Count == 0) return null;
        ctx.Progress($"proxy for {Label(target)} ...");
        var sources = drawn.Select(x => new Proxy.Source
        {
            V = x.g.Vertices!.Data, T = x.g.Triangles(), UV = Uv0(x.g), Image = LoadImage(x.ss?.Textures.FirstOrDefault(t => t.Unit == 0), cache),
            Color = x.ss?.Diffuse is { } dc ? dc.Concat(new[] { 1.0, 1.0, 1.0, 1.0 }).Take(4).ToArray() : new[] { 1.0, 1.0, 1.0, 1.0 },
            VertexColors = VertexColors(x.g), Extra = ExtraUnits(x.g, x.ss, cache, notes),
            N = x.g.Normals is { Comps: 3, Binding: "vertex" } on && on.Count == x.g.Vertices!.Count ? on.Data : null,   // in the frame of V
        }).ToList();
        long tris = sources.Sum(s => (long)s.T.Length / 3);
        LayerNotes(sources.Select(s => s.Extra), fill, classFill, notes);
        Proxy.Options Options(double? me) => new()
        {
            TargetTris = (int)Math.Max(12, tris * ratio), Resolution = voxels, TextureSize = size, Progress = ctx.Progress, Fill = fill, ClassFill = classFill, Normals = pp.Normals, Crease = pp.Crease,
            MaxError = me,
        };
        double eWorld = pp.Within ? pp.ErrorMetres(ctx.PInt("generate_lod", "screen_height"), Math.Clamp(ctx.PDouble("generate_lod", "fov"), 1, 179)) : 0;
        if (pp.Within && !(eWorld > 0)) { notes.Add("within needs max_error > 0"); eWorld = 0; }
        Proxy.Result? r;
        if (eWorld > 0)
        {
            // within: the single hull within e (in the node's parent frame), measured and tightened as proxy_from does
            double worldScale = ObjectWorldScale(ctx, target);
            var orig = new GeomData();
            var ov = new List<double>(); var ot = new List<int>();
            foreach (var s in sources) { int b = ov.Count / 3; ov.AddRange(s.V); ot.AddRange(s.T.Select(x => x + b)); }
            orig.Vertices = new VArray(ov.ToArray(), 3, "Vec3Array");
            orig.Prims.Add(new Primitive("TRIANGLES", "elements", indices: ot.ToArray()));
            var (got, dev, _) = TightenWithin(eWorld / worldScale, me => Proxy.Build(sources, Options(me)), x =>
            {
                var g = new GeomData { Vertices = new VArray(x.V, 3, "Vec3Array") };
                g.Prims.Add(new Primitive("TRIANGLES", "elements", indices: x.T));
                return Quality.Compare(orig, g).MaxDev;
            });
            if (got == null)
            {
                failed?.Add(double.IsFinite(dev)
                    ? $"Within {ErrorTarget.Distance(eWorld)}: could not stay within it (best {ErrorTarget.Distance(dev * worldScale)}); left unchanged"
                    : $"Within {ErrorTarget.Distance(eWorld)}: could not stay within it; left unchanged");
                return null;
            }
            notes.Add($"within {ErrorTarget.Distance(eWorld)} (reached {ErrorTarget.Distance(dev * worldScale)})");
            r = got;
        }
        else r = Proxy.Build(sources, Options(null));
        if (r == null) return null;
        // a proxy of a low-poly object can need more triangles than the object itself: keep the object
        if (r.T.Length / 3 >= tris) { simpler = true; return null; }
        var geo = ProxyGeometry(r, Label(target), drawn.Select(x => x.ss).ToList(), ctx.Scene, notes, ClassKeys(sources.Select(s => s.Extra)));
        FltFaceCodes.Carry(geo, drawn.Select(x => (x.node, AreaScale(x.g, x.node))), ctx.Scene, notes);
        ctx.MadeFrom(geo, drawn[0].g, "textured proxy");
        return (new Node(Kind.Geode, "osg::Geode", Label(target) + " proxy") { Children = { geo } }, tris, r.T.Length / 3);
    }

    /// <summary>Geometry node for a proxy result: one mesh, one baked texture (plus one per extra unit).</summary>
    static Node ProxyGeometry(Proxy.Result r, string baseName, IReadOnlyList<StateSet?> states, Scene? scene, List<string>? notes, ISet<LayerKey>? classMaps = null)
    {
        var geo = new GeomData
        {
            Vertices = new VArray(r.V, 3, "Vec3Array"),
            Normals = new VArray(r.N, 3, "Vec3Array"),
        };
        geo.TexCoords[0] = new VArray(r.UV, 2, "Vec2Array");
        geo.Prims.Add(new Primitive("TRIANGLES", "elements", count: r.T.Length, indices: r.T, indexType: MeshOps.SmallestIndexType(r.V.Length / 3)));
        var state = new StateSet { MaterialName = baseName + " proxy", Diffuse = new[] { 1.0, 1.0, 1.0, 1.0 } };
        if (r.Texture != null) state.Textures.Add(BakedTexture(baseName + "_proxy", 0, r.Texture, states, scene, notes));
        AddExtraUnits(geo, state, r.Extra, baseName + "_proxy", states, scene, notes, r.Texture, classMaps);
        var node = new Node(Kind.Geometry, "osg::Geometry", baseName + " proxy") { Geom = geo, StateSet = state };
        node.Mark("geometry");
        return node;
    }

    // ------------------------------------------------------------------------------ S3 make instances

    static string MakeInstances(OptContext ctx)
    {
        var crit = new SimilarCriteria { RigidTolerance = ctx.PDouble("make_instances", "tolerance") };
        var root = ctx.Scene.Root;
        var lists = ctx.Targets != null
            ? new List<List<Node>> { ctx.Targets.Distinct(ReferenceEqualityComparer.Instance).Cast<Node>().ToList() }
            : SimilarObjects.Find(Analyzer.Analyze(ctx.Scene, deepChecks: false, findings: false), crit, ctx.Progress)
                .Where(g => g.Class != Similarity.Instanced).Select(g => g.Nodes.ToList()).ToList();
        int replaced = 0, kept = 0;
        var shared = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        foreach (var list in lists)
        {
            var live = list.Where(n => !ReferenceEquals(n, root) && ctx.ParentsOf(n).Count > 0).ToList();   // not replaced by an earlier group
            foreach (var set in SimilarObjects.Copies(ctx.Parents, live, crit))
                foreach (var (copy, fit, match) in set.Copies)
                {
                    if (match == Similarity.Instanced) continue;   // already the same content
                    var m = CopyMatrix(ctx, set.Rep, copy, fit);
                    var put = m != null && PlainBelow(copy) && SimilarObjects.SameLook(ctx.Parents, set.Rep, copy) ? PlacedCopy(ctx, copy, m, set.Rep, copy.Name) : null;
                    if (put == null) { kept++; continue; }
                    foreach (var p in ctx.ParentsOf(copy).ToList()) ctx.Replace(p, copy, new[] { put });
                    replaced++;
                    shared.Add(set.Rep);
                }
        }
        string msg = Summ(replaced, "copies replaced by instances", $"{shared.Count} shared object(s)");
        if (kept > 0) msg += $"; {kept} kept (a drawable in a geode, special nodes below it, or another look where it stands)";
        return msg;
    }

    /// <summary>Nothing below the node (geometry aside) that replacing it by an equal copy would lose: callbacks, DOFs,
    /// external references, class-specific state. LOD and switch nodes are compared by the copy search.</summary>
    static bool PlainBelow(Node n)
    {
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Node>();
        stack.Push(n);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (!seen.Add(x) || x.Kind == Kind.Geometry) continue;
            if (x.ExternalNodes.Count > 0 || !(IsPlain(x) || x.Kind is Kind.Lod or Kind.Switch)) return false;
            foreach (var c in x.Children) stack.Push(c);
        }
        return true;
    }
}
