namespace SceneKit;

/// <summary>Uniform grid over triangles for closest-point queries (proxy projection and texture baking).</summary>
public sealed class TriIndex
{
    readonly double[] V;
    readonly int[] T;
    readonly double cell;
    readonly double[] lo = new double[3];
    readonly int[] dim = new int[3];
    readonly Dictionary<long, List<int>> cells = new();
    public readonly double[] Normals;   // unit face normals (m x 3)

    public int Count => T.Length / 3;

    public TriIndex(double[] V, int[] T, double cellSize = 0)
    {
        this.V = V; this.T = T;
        int m = T.Length / 3;
        Normals = new double[3 * m];
        var hi = new double[3];
        for (int c = 0; c < 3; c++) { lo[c] = double.PositiveInfinity; hi[c] = double.NegativeInfinity; }
        foreach (int v in T) for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], V[3 * v + c]); hi[c] = Math.Max(hi[c], V[3 * v + c]); }
        if (m == 0) { cell = 1; return; }
        double area = 0;
        for (int t = 0; t < m; t++)
        {
            var (nx, ny, nz) = Cross(t);
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            area += l / 2;
            if (l > 0) { Normals[3 * t] = nx / l; Normals[3 * t + 1] = ny / l; Normals[3 * t + 2] = nz / l; }
        }
        double ext = Math.Max(Math.Max(hi[0] - lo[0], hi[1] - lo[1]), hi[2] - lo[2]);
        cell = cellSize > 0 ? cellSize : Math.Max(Math.Sqrt(area / m) * 2, ext * 1e-4);
        if (cell <= 0) cell = 1;
        for (int c = 0; c < 3; c++) dim[c] = Math.Max(1, (int)Math.Ceiling((hi[c] - lo[c]) / cell) + 1);
        Span<int> a = stackalloc int[3], b = stackalloc int[3];
        for (int t = 0; t < m; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                double mn = Math.Min(V[3 * T[3 * t] + c], Math.Min(V[3 * T[3 * t + 1] + c], V[3 * T[3 * t + 2] + c]));
                double mx = Math.Max(V[3 * T[3 * t] + c], Math.Max(V[3 * T[3 * t + 1] + c], V[3 * T[3 * t + 2] + c]));
                a[c] = Idx(mn, c); b[c] = Idx(mx, c);
            }
            for (int x = a[0]; x <= b[0]; x++)
                for (int y = a[1]; y <= b[1]; y++)
                    for (int z = a[2]; z <= b[2]; z++)
                    {
                        long k = Key(x, y, z);
                        if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>(4);
                        l.Add(t);
                    }
        }
    }

    int Idx(double v, int c) => Math.Clamp((int)Math.Floor((v - lo[c]) / cell), 0, dim[c] - 1);
    long Key(int x, int y, int z) => ((long)x * dim[1] + y) * dim[2] + z;

    (double, double, double) Cross(int t)
    {
        int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
        double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
        double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
        return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }

    public record struct Hit(int Tri, double D2, double U, double V, double W);

    /// <summary>Closest triangle within maxDist. With a normal given, triangles facing away (dot &lt; minDot) are skipped
    /// unless nothing else is in range; among the rest, distance decides, with a small bias for similar orientation. With
    /// <paramref name="partOfTri"/> set, only triangles with partOfTri[t] == <paramref name="part"/> are candidates (the
    /// atlas bake: a texel looks up the original of its own part only).</summary>
    public Hit? Closest(double px, double py, double pz, double maxDist, double nx = 0, double ny = 0, double nz = 0, double minDot = 0.0,
        int[]? partOfTri = null, int part = -1)
    {
        bool useN = nx != 0 || ny != 0 || nz != 0;
        Hit? best = null, any = null;
        int r = Math.Max(0, (int)Math.Ceiling(maxDist / cell));
        int cx = Idx(px, 0), cy = Idx(py, 1), cz = Idx(pz, 2);
        double max2 = maxDist * maxDist;
        var seen = new HashSet<int>();
        for (int ring = 0; ring <= r; ring++)
        {
            for (int x = cx - ring; x <= cx + ring; x++)
                for (int y = cy - ring; y <= cy + ring; y++)
                    for (int z = cz - ring; z <= cz + ring; z++)
                    {
                        if (Math.Max(Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)), Math.Abs(z - cz)) != ring) continue;
                        if (x < 0 || y < 0 || z < 0 || x >= dim[0] || y >= dim[1] || z >= dim[2]) continue;
                        if (!cells.TryGetValue(Key(x, y, z), out var l)) continue;
                        foreach (int t in l)
                        {
                            if (!seen.Add(t)) continue;
                            if (partOfTri != null && partOfTri[t] != part) continue;
                            var (d2, u, v, w) = Geo.ClosestOnTri(px, py, pz, V, T[3 * t], T[3 * t + 1], T[3 * t + 2]);
                            if (d2 > max2) continue;
                            var h = new Hit(t, d2, u, v, w);
                            if (any == null || d2 < any.Value.D2) any = h;
                            if (useN && Normals[3 * t] * nx + Normals[3 * t + 1] * ny + Normals[3 * t + 2] * nz < minDot) continue;
                            if (best == null || d2 < best.Value.D2) best = h;
                        }
                    }
            // anything further out than this ring cannot be closer than what was found
            double reach = ring * cell;
            if (best != null && best.Value.D2 <= reach * reach) break;
        }
        return best ?? any;
    }

    /// <summary>The triangles within maxDist of a point (closest point on the triangle), added to <paramref name="into"/>.</summary>
    public void Near(double px, double py, double pz, double maxDist, List<int> into)
    {
        double max2 = maxDist * maxDist;
        var seen = new HashSet<int>();
        int[] a = { Idx(px - maxDist, 0), Idx(py - maxDist, 1), Idx(pz - maxDist, 2) }, b = { Idx(px + maxDist, 0), Idx(py + maxDist, 1), Idx(pz + maxDist, 2) };
        for (int x = a[0]; x <= b[0]; x++)
            for (int y = a[1]; y <= b[1]; y++)
                for (int z = a[2]; z <= b[2]; z++)
                {
                    if (!cells.TryGetValue(Key(x, y, z), out var l)) continue;
                    foreach (int t in l)
                        if (seen.Add(t) && Geo.ClosestOnTri(px, py, pz, V, T[3 * t], T[3 * t + 1], T[3 * t + 2]).d2 <= max2) into.Add(t);
                }
    }
}

/// <summary>Small geometry helpers shared by the LOD tools.</summary>
public static class Geo
{
    public static double Dist2(double ax, double ay, double az, double bx, double by, double bz) =>
        (ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz);

    /// <summary>Squared distance and barycentric coordinates (of vertices pa, pb, pc) of the closest point on a triangle
    /// (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    public static (double d2, double u, double v, double w) ClosestOnTri(double px, double py, double pz, double[] X, int pa, int pb, int pc)
    {
        double ax = X[3 * pa], ay = X[3 * pa + 1], az = X[3 * pa + 2];
        double abx = X[3 * pb] - ax, aby = X[3 * pb + 1] - ay, abz = X[3 * pb + 2] - az;
        double acx = X[3 * pc] - ax, acy = X[3 * pc + 1] - ay, acz = X[3 * pc + 2] - az;
        double apx = px - ax, apy = py - ay, apz = pz - az;
        double d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
        double u, v, w;
        if (d1 <= 0 && d2 <= 0) { u = 1; v = 0; w = 0; }
        else
        {
            double bpx = apx - abx, bpy = apy - aby, bpz = apz - abz;
            double d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
            double cpx = apx - acx, cpy = apy - acy, cpz = apz - acz;
            double d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
            double vc = d1 * d4 - d3 * d2, vb = d5 * d2 - d1 * d6, va = d3 * d6 - d5 * d4;
            if (d3 >= 0 && d4 <= d3) { u = 0; v = 1; w = 0; }
            else if (d6 >= 0 && d5 <= d6) { u = 0; v = 0; w = 1; }
            else if (vc <= 0 && d1 >= 0 && d3 <= 0) { double t = d1 / (d1 - d3); u = 1 - t; v = t; w = 0; }
            else if (vb <= 0 && d2 >= 0 && d6 <= 0) { double t = d2 / (d2 - d6); u = 1 - t; v = 0; w = t; }
            else if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { double t = (d4 - d3) / ((d4 - d3) + (d5 - d6)); u = 0; v = 1 - t; w = t; }
            else { double den = 1.0 / (va + vb + vc); v = vb * den; w = vc * den; u = 1 - v - w; }
        }
        double qx = ax + v * abx + w * acx, qy = ay + v * aby + w * acy, qz = az + v * abz + w * acz;
        return (Dist2(px, py, pz, qx, qy, qz), u, v, w);
    }
}
