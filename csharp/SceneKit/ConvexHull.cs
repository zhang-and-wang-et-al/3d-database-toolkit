namespace SceneKit;

/// <summary>3D convex hull (Quickhull) with outward-wound triangles, and a two-sided thin box for flat point sets.
/// Points are flat xyz triples. Degenerate input returns null and never throws.</summary>
public static class ConvexHull
{
    sealed class Face
    {
        public int A, B, C;
        public double Nx, Ny, Nz, D;           // unit normal and offset: n.x - D is the signed distance, positive = outside
        public List<int> Outside = new();      // points in front of this face
        public bool Alive = true;
    }

    /// <summary>Convex hull of the points as (V, T): V holds the original coordinates of the hull vertices only, T the
    /// outward-wound triangles. <paramref name="eps"/> = 0 means 1e-12 x bounding-box diagonal. Null if the points are
    /// fewer than 4 distinct ones, collinear, coplanar or not finite.</summary>
    public static (double[] V, int[] T)? Build(double[] points, double eps = 0)
    {
        int n = points.Length / 3;
        if (n < 4) return null;
        foreach (var x in points) if (!double.IsFinite(x)) return null;

        // Recentre on the bounding-box centre so large world coordinates keep their precision.
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        for (int i = 0; i < n * 3; i++) { lo[i % 3] = Math.Min(lo[i % 3], points[i]); hi[i % 3] = Math.Max(hi[i % 3], points[i]); }
        double dx = hi[0] - lo[0], dy = hi[1] - lo[1], dz = hi[2] - lo[2];
        double diag = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (diag == 0) return null;
        if (eps <= 0) eps = 1e-12 * diag;
        var p = new double[n * 3];
        for (int i = 0; i < n * 3; i++) p[i] = points[i] - (lo[i % 3] + hi[i % 3]) / 2;

        var seed = InitialSimplex(p, n, eps);
        if (seed == null) return null;
        var (s0, s1, s2, s3) = seed.Value;

        double cx = (p[3 * s0] + p[3 * s1] + p[3 * s2] + p[3 * s3]) / 4;
        double cy = (p[3 * s0 + 1] + p[3 * s1 + 1] + p[3 * s2 + 1] + p[3 * s3 + 1]) / 4;
        double cz = (p[3 * s0 + 2] + p[3 * s1 + 2] + p[3 * s2 + 2] + p[3 * s3 + 2]) / 4;

        var faces = new List<Face>();
        foreach (var (a, b, c) in new[] { (s0, s1, s2), (s0, s3, s1), (s1, s3, s2), (s2, s3, s0) })
        {
            var f = MakeFace(p, a, b, c);
            if (f.Nx * cx + f.Ny * cy + f.Nz * cz - f.D > 0) f = MakeFace(p, a, c, b);   // wind away from the centroid
            faces.Add(f);
        }
        for (int i = 0; i < n; i++)
        {
            if (i == s0 || i == s1 || i == s2 || i == s3) continue;
            foreach (var f in faces)
                if (Dist(f, p, i) > eps) { f.Outside.Add(i); break; }
        }

        // Quickhull: take the farthest outside point of a face, remove all faces it sees, and cap the hole with a cone.
        while (true)
        {
            Face? from = faces.Find(f => f.Outside.Count > 0);
            if (from == null) break;
            int apex = from.Outside[0];
            double best = Dist(from, p, apex);
            foreach (int i in from.Outside)
            {
                double d = Dist(from, p, i);
                if (d > best) { best = d; apex = i; }
            }

            var visible = faces.FindAll(f => Dist(f, p, apex) > eps);
            var edges = new HashSet<(int, int)>();
            foreach (var f in visible) { edges.Add((f.A, f.B)); edges.Add((f.B, f.C)); edges.Add((f.C, f.A)); }

            var fresh = new List<Face>();
            foreach (var (a, b) in edges)
                if (!edges.Contains((b, a))) fresh.Add(MakeFace(p, a, b, apex));     // horizon edge keeps its direction

            foreach (var f in visible)
            {
                f.Alive = false;
                foreach (int i in f.Outside)
                {
                    if (i == apex) continue;
                    foreach (var g in fresh)
                        if (Dist(g, p, i) > eps) { g.Outside.Add(i); break; }
                }
                f.Outside = new();
            }
            faces.RemoveAll(f => !f.Alive);
            faces.AddRange(fresh);
        }

        // Keep only the hull vertices, with the caller's original doubles.
        var map = new Dictionary<int, int>();
        var V = new List<double>();
        var T = new int[faces.Count * 3];
        for (int f = 0; f < faces.Count; f++)
        {
            int[] corner = { faces[f].A, faces[f].B, faces[f].C };
            for (int k = 0; k < 3; k++)
            {
                if (!map.TryGetValue(corner[k], out int idx))
                {
                    idx = map[corner[k]] = V.Count / 3;
                    V.Add(points[3 * corner[k]]); V.Add(points[3 * corner[k] + 1]); V.Add(points[3 * corner[k] + 2]);
                }
                T[3 * f + k] = idx;
            }
        }
        return (V.ToArray(), T);
    }

    /// <summary>The 2D hull of the points projected onto their best-fit plane (smallest-variance axis as normal),
    /// extruded by thickness/2 to each side into a closed box. Null for fewer than 3 hull corners or bad input.</summary>
    public static (double[] V, int[] T)? ThinBox(double[] points, double thickness)
    {
        int n = points.Length / 3;
        if (n < 3 || !(thickness > 0)) return null;
        for (int i = 0; i < n * 3; i++) if (!double.IsFinite(points[i])) return null;

        double[] mean = new double[3];
        for (int i = 0; i < n * 3; i++) mean[i % 3] += points[i] / n;
        var cov = new double[3, 3];
        for (int i = 0; i < n; i++)
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    cov[r, c] += (points[3 * i + r] - mean[r]) * (points[3 * i + c] - mean[c]);
        var (axes, vals) = Eigen3(cov);
        int lowest = 0;
        for (int k = 1; k < 3; k++) if (vals[k] < vals[lowest]) lowest = k;
        double[] nrm = axes[lowest], u = axes[(lowest + 1) % 3];
        double[] v = Cross(nrm, u);                         // u x v = nrm, so a counter-clockwise outline faces +nrm

        var pts2 = new List<(double X, double Y)>(n);
        for (int i = 0; i < n; i++)
        {
            double rx = points[3 * i] - mean[0], ry = points[3 * i + 1] - mean[1], rz = points[3 * i + 2] - mean[2];
            pts2.Add((rx * u[0] + ry * u[1] + rz * u[2], rx * v[0] + ry * v[1] + rz * v[2]));
        }
        var outline = Hull2D(pts2);
        int m = outline.Count;
        if (m < 3) return null;

        var V = new double[2 * m * 3];
        for (int i = 0; i < m; i++)
            for (int side = 0; side < 2; side++)            // rows 0..m-1 bottom, m..2m-1 top
            {
                double h = (side == 0 ? -0.5 : 0.5) * thickness;
                for (int a = 0; a < 3; a++)
                    V[3 * (side * m + i) + a] = mean[a] + outline[i].X * u[a] + outline[i].Y * v[a] + h * nrm[a];
            }
        var T = new List<int>();
        for (int i = 1; i < m - 1; i++)
        {
            T.AddRange(new[] { m, m + i, m + i + 1 });      // top fan
            T.AddRange(new[] { 0, i + 1, i });              // bottom fan, reversed
        }
        for (int i = 0; i < m; i++)
        {
            int j = (i + 1) % m;
            T.AddRange(new[] { i, j, m + j });
            T.AddRange(new[] { i, m + j, m + i });
        }
        return (V, T.ToArray());
    }

    static Face MakeFace(double[] p, int a, int b, int c)
    {
        double ux = p[3 * b] - p[3 * a], uy = p[3 * b + 1] - p[3 * a + 1], uz = p[3 * b + 2] - p[3 * a + 2];
        double vx = p[3 * c] - p[3 * a], vy = p[3 * c + 1] - p[3 * a + 1], vz = p[3 * c + 2] - p[3 * a + 2];
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len > 0) { nx /= len; ny /= len; nz /= len; }
        return new Face { A = a, B = b, C = c, Nx = nx, Ny = ny, Nz = nz, D = nx * p[3 * a] + ny * p[3 * a + 1] + nz * p[3 * a + 2] };
    }

    static double Dist(Face f, double[] p, int i) => f.Nx * p[3 * i] + f.Ny * p[3 * i + 1] + f.Nz * p[3 * i + 2] - f.D;

    /// <summary>Four non-coplanar points to start from: the two farthest apart among the axis extremes, then the point
    /// farthest from their line, then the one farthest from that plane. Null if any step is within eps of degenerate.</summary>
    static (int, int, int, int)? InitialSimplex(double[] p, int n, double eps)
    {
        var ext = new List<int>();
        for (int axis = 0; axis < 3; axis++)
        {
            int mn = 0, mx = 0;
            for (int i = 1; i < n; i++)
            {
                if (p[3 * i + axis] < p[3 * mn + axis]) mn = i;
                if (p[3 * i + axis] > p[3 * mx + axis]) mx = i;
            }
            ext.Add(mn); ext.Add(mx);
        }
        int a = 0, b = 0;
        double far = -1;
        foreach (int i in ext)
            foreach (int j in ext)
            {
                double d = Sq(p, i, j);
                if (d > far) { far = d; a = i; b = j; }
            }
        if (Math.Sqrt(far) <= eps) return null;

        double ex = p[3 * b] - p[3 * a], ey = p[3 * b + 1] - p[3 * a + 1], ez = p[3 * b + 2] - p[3 * a + 2];
        double el = Math.Sqrt(ex * ex + ey * ey + ez * ez);
        int c = -1;
        double best = eps;
        for (int i = 0; i < n; i++)
        {
            double rx = p[3 * i] - p[3 * a], ry = p[3 * i + 1] - p[3 * a + 1], rz = p[3 * i + 2] - p[3 * a + 2];
            double cx = ey * rz - ez * ry, cy = ez * rx - ex * rz, cz = ex * ry - ey * rx;
            double d = Math.Sqrt(cx * cx + cy * cy + cz * cz) / el;
            if (d > best) { best = d; c = i; }
        }
        if (c < 0) return null;

        var plane = MakeFace(p, a, b, c);
        int e = -1;
        best = eps;
        for (int i = 0; i < n; i++)
        {
            double d = Math.Abs(Dist(plane, p, i));
            if (d > best) { best = d; e = i; }
        }
        return e < 0 ? null : (a, b, c, e);
    }

    static double Sq(double[] p, int i, int j)
    {
        double x = p[3 * i] - p[3 * j], y = p[3 * i + 1] - p[3 * j + 1], z = p[3 * i + 2] - p[3 * j + 2];
        return x * x + y * y + z * z;
    }

    static double[] Cross(double[] a, double[] b) =>
        new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

    /// <summary>Counter-clockwise convex outline (Andrew's monotone chain); collinear points are dropped.</summary>
    static List<(double X, double Y)> Hull2D(List<(double X, double Y)> pts)
    {
        var s = pts.OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (s.Count < 3) return new();
        static double Turn((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        double scale = Math.Max(s.Max(q => q.X) - s.Min(q => q.X), s.Max(q => q.Y) - s.Min(q => q.Y));
        double tol = 1e-12 * scale * scale;
        var h = new List<(double X, double Y)>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = h.Count;
            for (int i = 0; i < s.Count; i++)
            {
                var q = s[pass == 0 ? i : s.Count - 1 - i];
                while (h.Count >= start + 2 && Turn(h[^2], h[^1], q) <= tol) h.RemoveAt(h.Count - 1);
                h.Add(q);
            }
            h.RemoveAt(h.Count - 1);
        }
        return h.Count < 3 ? new() : h;
    }

    /// <summary>Eigenvectors (unit, one per entry) and eigenvalues of a symmetric 3x3 matrix by Jacobi rotations.</summary>
    static (double[][] Axes, double[] Vals) Eigen3(double[,] a)
    {
        var m = (double[,])a.Clone();
        var vec = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            if (Math.Abs(m[0, 1]) + Math.Abs(m[0, 2]) + Math.Abs(m[1, 2]) == 0) break;
            for (int pq = 0; pq < 3; pq++)
            {
                int i = pq == 2 ? 1 : 0, j = pq == 0 ? 1 : 2;       // (0,1), (0,2), (1,2)
                if (m[i, j] == 0) continue;
                double theta = (m[j, j] - m[i, i]) / (2 * m[i, j]);
                double t = (theta >= 0 ? 1 : -1) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                for (int k = 0; k < 3; k++)                          // m = J^T m J
                {
                    double ki = m[k, i], kj = m[k, j];
                    m[k, i] = c * ki - s * kj; m[k, j] = s * ki + c * kj;
                }
                for (int k = 0; k < 3; k++)
                {
                    double ik = m[i, k], jk = m[j, k];
                    m[i, k] = c * ik - s * jk; m[j, k] = s * ik + c * jk;
                }
                for (int k = 0; k < 3; k++)
                {
                    double ki = vec[k, i], kj = vec[k, j];
                    vec[k, i] = c * ki - s * kj; vec[k, j] = s * ki + c * kj;
                }
            }
        }
        var axes = new double[3][];
        for (int k = 0; k < 3; k++) axes[k] = new[] { vec[0, k], vec[1, k], vec[2, k] };
        return (axes, new[] { m[0, 0], m[1, 1], m[2, 2] });
    }
}
