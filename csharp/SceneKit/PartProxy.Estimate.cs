using System.Text.RegularExpressions;

namespace SceneKit;

/// <summary>What a per-part proxy would roughly give, before building it (<see cref="PartProxy.Estimate"/>): the parts
/// (static parts plus moving nodes, a switch once), the moving nodes, the triangles and draw calls, today's single hull
/// for comparison and the line the window shows.</summary>
public sealed record ProxyEstimate(int Parts, int Moving, long Tris, int DrawCalls, long SingleHullTris, string Text);

public static partial class PartProxy
{
    /// <summary>The estimate of a per-part proxy of <paramref name="target"/>: steps 1 and 2 only (discovery with the gap
    /// and the grid the build uses, classification), no hull. Triangles: <see cref="Limits.SmallPartTris"/> per small, thin
    /// or floating tiny part (none for one inside a closed bulk part, which step 8 removes), 12 x ceil(a / max(e, cell)) per bulk part (a: its largest side), clamped to [12, 20 000];
    /// one unit, one part and no moving parts is today's single hull. Draw calls: the static proxy plus one per moving part
    /// (every switch child, as the build counts them). The single hull is today's target, max(12, triangles x ratio). With
    /// parts = whole only the single hull is shown. The text warns when the parts need more triangles than the single hull
    /// (<see cref="MoreThanSingleHull"/>), and instead more strongly above <see cref="Limits.EstimateWarnFactor"/> times.
    /// <paramref name="parents"/> gives the world scale of the target (within mode: e in its parent frame).
    /// <paramref name="errorMetres"/>: within mode's resolved error in metres (the pass reads generate_lod's screen_height
    /// and fov, see <see cref="ProxyParams.ErrorMetres"/>); null takes the defaults of <see cref="ProxyParams.ErrorMetres"/>.
    /// Above <see cref="Limits.EstimateMaxTris"/> triangles below the target nothing is discovered and the text says the
    /// selection is too large (the cap is inside this method; the signature has no extra overload). With parts = whole
    /// there is no clustering: the triangles and the moving nodes come from the gathered pieces only.</summary>
    public static ProxyEstimate Estimate(Node target, ProxyParams p, IReadOnlyDictionary<Node, List<Node>> parents, double? errorMetres = null)
    {
        if (CountTris(target, Limits.EstimateMaxTris) > Limits.EstimateMaxTris)
            return new ProxyEstimate(0, 0, 0, 0, 0, "Selection too large to estimate.");
        if (p.Parts == ProxyParts.Whole) return EstimateWhole(target, p);
        double worldScale = Optimizer.InstanceMatrices(target, parents).Max(Mat.MaxScale);
        if (!(worldScale > 0) || !double.IsFinite(worldScale)) worldScale = 1;
        double eWorld = p.Within ? errorMetres ?? p.ErrorMetres() : 0;
        if (!(eWorld > 0)) eWorld = 0;
        double ext = 0, diag = 0;
        var asm = Discover(target, a =>
        {
            (ext, diag) = ObjectExtent(a);
            return Optimizer.KeepGap(eWorld, worldScale, ext, p.Voxels);
        });
        long before = asm.AllPieces().Sum(pc => (long)pc.T.Length / 3);
        if (before == 0 || !(ext > 0)) return new ProxyEstimate(0, 0, 0, 0, 0, "Nothing drawn below the selection.");
        long single = (long)Math.Max(12, before * p.Ratio);

        // the moving nodes (a switch once, its children as states) and what the single hull merges
        var movingNodes = MovingNodes(asm);
        var plain = movingNodes.Where(x => x.Node.Kind != Kind.Switch).Select(x => x.Node).ToList();
        string singleText = SingleText(single, movingNodes);
        // step 2 and the triangle heuristic
        double e = eWorld / worldScale;
        double cell = ObjectCell(ext, e, p.Voxels);
        long tris;
        int draws;
        if (asm.Moving.Count == 0 && asm.Parts.Count == 1 && asm.Parts[0].Units.Count == 1) (tris, draws) = (single, 1);
        else
        {
            Classify(asm, e, cell, diag);
            // a small part inside a closed bulk part (an engine in the body) is never seen: step 8 removes it
            var bulk = asm.Parts.Where(x => x.Class == PartClass.Bulk).ToList();
            asm.Parts.RemoveAll(x => x.Class != PartClass.Bulk && bulk.Any(b => Inside(Centre(x), b)));
            double step = Math.Max(e, cell);
            tris = Owners(asm).SelectMany(x => x.A.Parts).Sum(part => part.Class == PartClass.Bulk
                ? Math.Clamp(12 * (long)Math.Ceiling(part.A / step), Limits.EstimateMinBulkTris, Limits.EstimateMaxBulkTris)
                : (long)Limits.SmallPartTris);
            draws = 1 + Owners(asm).Skip(1).Count(x => x.A.Parts.Count > 0);   // one per moving part, every switch child
        }

        // "9 parts: body, 2 mirrors, antenna (convex), 4 wheels moving (DOF), lights switch (2 states)"
        var names = new List<string>();
        foreach (var grp in asm.Parts.OrderBy(x => x.Class == PartClass.Bulk ? 0 : 1)
                     .GroupBy(x => (Base: BaseName(x.Name), Bulk: x.Class == PartClass.Bulk)))
            names.Add(Counted(grp.Count(), grp.Key.Base) + (grp.Key.Bulk ? "" : " (convex)"));
        foreach (var grp in plain.GroupBy(n => (Base: BaseName(n.Name), Kind: MovingKind(n))))
            names.Add($"{Counted(grp.Count(), grp.Key.Base)} moving ({grp.Key.Kind})");
        foreach (var (node, states) in movingNodes.Where(x => x.Node.Kind == Kind.Switch))
            names.Add($"{(node.Name.Length > 0 ? node.Name : "switch")} switch ({PyFormat.Int(states)} {(states == 1 ? "state" : "states")})");
        int parts = asm.Parts.Count + movingNodes.Count;
        string text = $"{PyFormat.Int(parts)} {(parts == 1 ? "part" : "parts")}: {string.Join(", ", names)}. " +
                      $"About {About(tris)} triangles, {PyFormat.Int(draws)} {(draws == 1 ? "draw call" : "draw calls")} ({singleText}).";
        if (tris > Limits.EstimateWarnFactor * single) text += $" More than {PyFormat.G(Limits.EstimateWarnFactor)}x the single hull's triangles.";
        else if (MoreThanSingleHull(tris, single)) text += " More triangles than the single hull.";
        return new ProxyEstimate(parts, movingNodes.Count, tris, draws, single, text);
    }

    /// <summary>Parts = whole: the triangles of the gathered pieces and the moving nodes, without clustering.</summary>
    static ProxyEstimate EstimateWhole(Node target, ProxyParams p)
    {
        var asm = Gather(target, new[] { target }, Array.Empty<Node>(), target.Matrix != null ? (double[])target.Matrix.Clone() : Mat.Identity(), 0);
        long before = asm.AllPieces().Sum(pc => (long)pc.T.Length / 3);
        if (before == 0) return new ProxyEstimate(0, 0, 0, 0, 0, "Nothing drawn below the selection.");
        long single = (long)Math.Max(12, before * p.Ratio);
        string t = SingleText(single, MovingNodes(asm));
        return new ProxyEstimate(1, 0, single, 1, single, char.ToUpperInvariant(t[0]) + t[1..] + ".");
    }

    /// <summary>The moving nodes of an assembly (a switch once, with its child count as states).</summary>
    static List<(Node Node, int States)> MovingNodes(Assembly asm)
    {
        var movingNodes = new List<(Node Node, int States)>();
        foreach (var m in AllMovingParts(asm))
        {
            int at = movingNodes.FindIndex(x => ReferenceEquals(x.Node, m.Node));
            if (at < 0) movingNodes.Add((m.Node, 1)); else movingNodes[at] = (m.Node, movingNodes[at].States + 1);
        }
        return movingNodes;
    }

    /// <summary>"today's single hull: about 900 triangles, 1 draw call, wheels and switch merged".</summary>
    static string SingleText(long single, List<(Node Node, int States)> movingNodes)
    {
        var plain = movingNodes.Where(x => x.Node.Kind != Kind.Switch).Select(x => x.Node).ToList();
        var merged = new List<string>();
        if (plain.Count > 0) merged.Add(plain.All(n => n.Name.Contains("wheel", StringComparison.OrdinalIgnoreCase)) ? "wheels" : "moving parts");
        int switches = movingNodes.Count - plain.Count;
        if (switches > 0) merged.Add(switches == 1 ? "switch" : "switches");
        return $"today's single hull: about {About(single)} triangles, 1 draw call" +
               (merged.Count > 0 ? $", {string.Join(" and ", merged)} merged" : "");
    }

    /// <summary>The triangles below a node (moving nodes entered), counted from the geometry only; stops as soon as the
    /// count passes <paramref name="cap"/> (the result is then only known to be above it).</summary>
    static long CountTris(Node root, long cap)
    {
        long n = 0;
        var todo = new Stack<Node>();
        todo.Push(root);
        while (todo.Count > 0)
        {
            var r = todo.Pop();
            if (r.NodeMask == 0) continue;
            var stopped = new List<(Node node, double[] world, Node[] path)>();
            foreach (var (g, _, _, _) in SoftRender.GeometryPaths(r, null, Optimizer.Moving, stopped, out _))
            {
                if (g.Geom != null) n += g.Geom.Triangles().Length / 3;
                if (n > cap) return n;
            }
            foreach (var (s, _, _) in stopped) foreach (var k in s.AllChildren()) todo.Push(k);
        }
        return n;
    }

    /// <summary>The centre of a part's bounding box.</summary>
    static double[] Centre(Part part)
    {
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        Grow(part.V, part.T, lo, hi);
        return Enumerable.Range(0, 3).Select(c => (lo[c] + hi[c]) / 2).ToArray();
    }

    /// <summary>Whether a point lies inside a (closed) part: an odd number of crossings along at least two of three
    /// skewed rays.</summary>
    static bool Inside(double[] q, Part part)
    {
        double[][] dirs = { new[] { 1, 0.123, 0.0571 }, new[] { -0.0613, 1, 0.137 }, new[] { 0.0917, -0.0711, 1 } };
        int odd = 0;
        foreach (var d in dirs)
        {
            int hits = 0;
            for (int t = 0; t < part.T.Length / 3; t++)
                if (RayHits(q, d, part.V, part.T[3 * t], part.T[3 * t + 1], part.T[3 * t + 2])) hits++;
            if (hits % 2 == 1) odd++;
        }
        return odd >= 2;
    }

    /// <summary>Möller-Trumbore: whether the ray from <paramref name="o"/> along <paramref name="d"/> crosses the triangle.</summary>
    static bool RayHits(double[] o, double[] d, double[] V, int a, int b, int c)
    {
        double e1x = V[3 * b] - V[3 * a], e1y = V[3 * b + 1] - V[3 * a + 1], e1z = V[3 * b + 2] - V[3 * a + 2];
        double e2x = V[3 * c] - V[3 * a], e2y = V[3 * c + 1] - V[3 * a + 1], e2z = V[3 * c + 2] - V[3 * a + 2];
        double px = d[1] * e2z - d[2] * e2y, py = d[2] * e2x - d[0] * e2z, pz = d[0] * e2y - d[1] * e2x;
        double det = e1x * px + e1y * py + e1z * pz;
        if (Math.Abs(det) < 1e-18) return false;
        double sx = o[0] - V[3 * a], sy = o[1] - V[3 * a + 1], sz = o[2] - V[3 * a + 2];
        double u = (sx * px + sy * py + sz * pz) / det;
        if (u < 0 || u > 1) return false;
        double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
        double v = (d[0] * qx + d[1] * qy + d[2] * qz) / det;
        if (v < 0 || u + v > 1) return false;
        return (e2x * qx + e2y * qy + e2z * qz) / det > 0;
    }

    /// <summary>Every moving part below an assembly, depth first (a switch: one per child).</summary>
    static IEnumerable<MovingPart> AllMovingParts(Assembly a) => a.Moving.SelectMany(m => new[] { m }.Concat(AllMovingParts(m.Inner)));

    /// <summary>A name without its numbering or side: "body_1" and "mirror_l" are "body" and "mirror".</summary>
    static string BaseName(string name)
    {
        var m = Regex.Match(name, @"^(.+?)[_\-. ]+(\d+|[A-Za-z]{1,2})$");
        string b = m.Success ? m.Groups[1].Value : name;
        return b.Length > 0 ? b : "part";
    }

    /// <summary>"antenna", "2 mirrors", "3 boxes", "2 bodies".</summary>
    static string Counted(int n, string name)
    {
        if (n == 1) return name;
        string many = name.EndsWith('s') || name.EndsWith('x') || name.EndsWith("ch") || name.EndsWith("sh") ? name + "es"
            : name.Length > 1 && name.EndsWith('y') && !"aeiou".Contains(name[^2]) ? name[..^1] + "ies"
            : name + "s";
        return $"{PyFormat.Int(n)} {many}";
    }

    /// <summary>Why a node moves, for the estimate: DOF, sequence or animated.</summary>
    static string MovingKind(Node n) =>
        n.Cls.Contains("DOF", StringComparison.OrdinalIgnoreCase) ? "DOF"
        : n.Cls.Contains("Sequence", StringComparison.OrdinalIgnoreCase) ? "sequence" : "animated";

    /// <summary>An estimated count, rounded to two significant digits from 100 on ("1 400").</summary>
    static string About(long n)
    {
        if (n < 100) return PyFormat.Int(n);
        long unit = (long)Math.Pow(10, Math.Floor(Math.Log10(n)) - 1);
        return PyFormat.Int((long)Math.Round((double)n / unit, MidpointRounding.AwayFromZero) * unit);
    }
}
