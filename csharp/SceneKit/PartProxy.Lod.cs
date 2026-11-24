namespace SceneKit;

/// <summary>Per-part proxies for generate_lod's far level: the holder of a LOD (a geode, or a geometry on its own) is the
/// geometry source; the object it belongs to (the selected target that contains it, else the holder) gives the grid,
/// the small rule and the ground, one per object, so all holders of an object share them and one atlas.</summary>
public static partial class PartProxy
{
    /// <summary>The small rule of the clustering: a unit whose bounding box diagonal is below
    /// <see cref="Limits.SmallShareOfD"/> of <paramref name="objectDiag"/> never joins a cluster.</summary>
    internal static Func<Unit, bool> SmallRule(List<Piece> pieces, double objectDiag) => u =>
    {
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        Grow(pieces[u.Piece].V, UnitT(pieces, u), lo, hi);
        return Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (hi[c] - lo[c]) * (hi[c] - lo[c]))) < Limits.SmallShareOfD * objectDiag;
    };

    /// <summary>The parts of one LOD holder: its pieces (positions in the holder's own frame, so its frame is the identity)
    /// split into connected components and clustered within <paramref name="gap"/> (<see cref="FromPieces"/>, steps 4 to 7
    /// of the discovery only: a holder is a geode, it has no DOF or FLT-group split), with the small rule of the object
    /// (<paramref name="objectDiag"/>).</summary>
    internal static Assembly OfHolder(List<Piece> pieces, Node holder, double gap, double objectDiag) =>
        FromPieces(pieces, holder, Mat.Identity(), gap, SmallRule(pieces, objectDiag));

    /// <summary>The detail of a far level's result line, summed over the holders of an object (the fragments of
    /// <see cref="Detail(ObjectResult, Assembled, double)"/>: moving nodes kept separate, the deviation reached (the worst),
    /// tiny parts dropped, kept parts, LOD alternatives dropped, the ground rule).</summary>
    internal static string FarDetail(IReadOnlyList<(ObjectResult R, Assembled A)> items)
    {
        if (items.Count == 0) return "";
        var nodes = items.SelectMany(x => AllMoving(x.R)).Select(x => x.M.Node).Distinct(ReferenceEqualityComparer.Instance).Cast<Node>().ToList();
        int switches = nodes.Count(n => n.Kind == Kind.Switch), dofs = nodes.Count - switches;
        bool wheels = dofs > 0 && nodes.Where(n => n.Kind != Kind.Switch).All(n => n.Name.Contains("wheel", StringComparison.OrdinalIgnoreCase));
        var sep = new List<string>();
        if (dofs > 0) sep.Add($"{PyFormat.Int(dofs)} " + (wheels ? "wheels" : "DOF parts"));
        if (switches > 0) sep.Add($"{PyFormat.Int(switches)} " + (switches == 1 ? "switch" : "switches"));
        string s = sep.Count > 0 ? $" ({string.Join(" and ", sep)} kept separate)" : "";
        var first = items[0].R;
        bool within = first.Report.TargetM is double e && e > 0;
        if (within)
        {
            var reached = items.Select(x => x.R.Report.ReachedM).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (reached.Count > 0) s += "; " + WithinReachedFragment(first.Report.TargetM!.Value, reached.Max());
        }
        int tiny = items.Sum(x => Owners(x.R.Asm).Sum(o => o.A.TinyDropped));
        if (tiny > 0) s += $"; {PyFormat.Int(tiny)} tiny parts dropped, within {ErrorTarget.Distance(within ? first.Report.TargetM!.Value : 2 * first.Cell)}";
        int kept = items.Sum(x => x.A.KeptParts.Count), keptDraws = items.Sum(x => x.A.KeptDraws);
        if (kept > 0) s += $"; {PyFormat.Int(kept)} parts kept original or decimated, +{PyFormat.Int(keptDraws)} draw calls ({string.Join(", ", items.SelectMany(x => x.A.KeptParts))})";
        int lod = items.Sum(x => Owners(x.R.Asm).Sum(o => o.A.LodAlternativesDropped));
        if (lod > 0) s += $"; {PyFormat.Int(lod)} LOD alternatives dropped (the nearest level is used)";
        if (first.Report.Ground == "cut") s += "; underside cut";
        else if (first.Report.Ground == "above") s += "; underside not seen from below";
        return s;
    }

    /// <summary>The object's ground in a holder's frame: horizontal in world Z (as <see cref="GroundOf"/>; the normal is
    /// world up taken into that frame) at the world height <paramref name="worldLowZ"/> (the lowest point of the whole
    /// object, not of this holder). <paramref name="holderWorld"/> maps the holder's frame to the world.</summary>
    internal static GroundPlane GroundAt(Assembly asm, double[] holderWorld, double worldLowZ)
    {
        var g = GroundOf(asm, holderWorld);
        var w = holderWorld;
        double gx = w[8], gy = w[9], gz = w[10], g2 = gx * gx + gy * gy + gz * gz;
        if (!(g2 > 0)) return g;
        // move the holder's lowest point along the gradient of world z until it is at the object's lowest height
        var p = g.Point;
        double z = gx * p[0] + gy * p[1] + gz * p[2] + w[11];
        double t = (worldLowZ - z) / g2;
        return new GroundPlane(new[] { p[0] + gx * t, p[1] + gy * t, p[2] + gz * t }, g.Normal);
    }
}
