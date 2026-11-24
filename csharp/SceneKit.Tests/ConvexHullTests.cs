using SceneKit;
using Xunit;

public class ConvexHullTests
{
    /// <summary>64-segment cylinder, radius 1, height 2: two rings of 64 points, shifted by (ox, oy, 0).</summary>
    static double[] Cylinder(double ox = 0, double oy = 0)
    {
        const int n = 64;
        var p = new double[2 * n * 3];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            for (int k = 0; k < 2; k++)
            {
                int r = (k * n + i) * 3;
                p[r] = ox + Math.Cos(a);
                p[r + 1] = oy + Math.Sin(a);
                p[r + 2] = k == 0 ? -1 : 1;
            }
        }
        return p;
    }

    static double Diagonal(double[] p)
    {
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        for (int i = 0; i < p.Length; i++) { lo[i % 3] = Math.Min(lo[i % 3], p[i]); hi[i % 3] = Math.Max(hi[i % 3], p[i]); }
        return Math.Sqrt((hi[0] - lo[0]) * (hi[0] - lo[0]) + (hi[1] - lo[1]) * (hi[1] - lo[1]) + (hi[2] - lo[2]) * (hi[2] - lo[2]));
    }

    /// <summary>Unit normal and offset of face f, so that n.x - d is the signed distance (positive = outside).</summary>
    static (double nx, double ny, double nz, double d) Plane(double[] V, int[] T, int f)
    {
        int a = T[3 * f] * 3, b = T[3 * f + 1] * 3, c = T[3 * f + 2] * 3;
        double ux = V[b] - V[a], uy = V[b + 1] - V[a + 1], uz = V[b + 2] - V[a + 2];
        double vx = V[c] - V[a], vy = V[c + 1] - V[a + 1], vz = V[c + 2] - V[a + 2];
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        nx /= len; ny /= len; nz /= len;
        return (nx, ny, nz, nx * V[a] + ny * V[a + 1] + nz * V[a + 2]);
    }

    static void AssertContainsAndOutward(double[] points, (double[] V, int[] T) hull)
    {
        var (V, T) = hull;
        double tol = 1e-9 * Diagonal(points);
        double cx = 0, cy = 0, cz = 0;
        int vn = V.Length / 3;
        for (int i = 0; i < vn; i++) { cx += V[3 * i]; cy += V[3 * i + 1]; cz += V[3 * i + 2]; }
        cx /= vn; cy /= vn; cz /= vn;
        for (int f = 0; f < T.Length / 3; f++)
        {
            var (nx, ny, nz, d) = Plane(V, T, f);
            Assert.True(nx * cx + ny * cy + nz * cz - d < 0, "centroid must be behind every face");
            for (int i = 0; i < points.Length; i += 3)
                Assert.True(nx * points[i] + ny * points[i + 1] + nz * points[i + 2] - d <= tol, "input point outside a face plane");
        }
    }

    /// <summary>Every undirected edge must appear in exactly two faces; returns the edge count.</summary>
    static int AssertClosedManifold(int[] T)
    {
        var count = new Dictionary<(int, int), int>();
        for (int f = 0; f < T.Length / 3; f++)
            for (int k = 0; k < 3; k++)
            {
                int a = T[3 * f + k], b = T[3 * f + (k + 1) % 3];
                var e = a < b ? (a, b) : (b, a);
                count[e] = count.GetValueOrDefault(e) + 1;
            }
        Assert.All(count.Values, c => Assert.Equal(2, c));
        return count.Count;
    }

    [Fact]
    public void CylinderHullContainsEveryPoint()
    {
        var pts = Cylinder();
        var hull = ConvexHull.Build(pts);
        Assert.NotNull(hull);
        AssertContainsAndOutward(pts, hull!.Value);
        AssertClosedManifold(hull.Value.T);
    }

    [Fact]
    public void RandomPointsGiveAClosedHull()
    {
        var rng = new Random(12345);
        const int n = 2000;
        var pts = new double[n * 3];
        for (int i = 0; i < pts.Length; i++) pts[i] = rng.NextDouble() * 10 - 5;
        var hull = ConvexHull.Build(pts);
        Assert.NotNull(hull);
        int v = hull!.Value.V.Length / 3, f = hull.Value.T.Length / 3;
        int e = AssertClosedManifold(hull.Value.T);
        Assert.Equal(2, v - e + f);
        Assert.True(f <= 2 * n - 4);
        AssertContainsAndOutward(pts, hull.Value);
    }

    [Fact]
    public void DegenerateInputDoesNotThrow()
    {
        var grid = new List<double>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++) { grid.Add(i); grid.Add(j); grid.Add(2); }
        Assert.Null(ConvexHull.Build(grid.ToArray()));

        var line = new double[30];
        for (int i = 0; i < 10; i++) { line[3 * i] = i; line[3 * i + 1] = 2 * i; line[3 * i + 2] = -i; }
        Assert.Null(ConvexHull.Build(line));

        var same = new double[150];
        for (int i = 0; i < 50; i++) { same[3 * i] = 1; same[3 * i + 1] = 2; same[3 * i + 2] = 3; }
        Assert.Null(ConvexHull.Build(same));

        Assert.Null(ConvexHull.Build(new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }));
        Assert.Null(ConvexHull.Build(Array.Empty<double>()));
    }

    [Fact]
    public void ThinBoxOfAFlatPartIsTwoSided()
    {
        // 2 x 1 flat sign in the plane z = 3, with interior and edge points that must not become vertices
        var pts = new List<double>();
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 2; j++) { pts.Add(i * 0.5); pts.Add(j * 0.5); pts.Add(3); }
        var box = ConvexHull.ThinBox(pts.ToArray(), 0.01);
        Assert.NotNull(box);
        var (V, T) = box!.Value;
        Assert.Equal(12, T.Length / 3);
        Assert.Equal(8, V.Length / 3);
        AssertClosedManifold(T);
        double zMin = double.MaxValue, zMax = double.MinValue;
        for (int i = 2; i < V.Length; i += 3) { zMin = Math.Min(zMin, V[i]); zMax = Math.Max(zMax, V[i]); }
        Assert.InRange(zMax - zMin, 0.01 - 1e-9, 0.01 + 1e-9);
        AssertContainsAndOutward(V, (V, T));
    }

    [Fact]
    public void FarFromOriginHullContainsEveryPoint()
    {
        var pts = Cylinder(600000, 5300000);
        var hull = ConvexHull.Build(pts);
        Assert.NotNull(hull);
        AssertContainsAndOutward(pts, hull!.Value);
        AssertClosedManifold(hull.Value.T);
        // every output row is an input row, bit for bit
        var input = new HashSet<(long, long, long)>();
        for (int i = 0; i < pts.Length; i += 3)
            input.Add((BitConverter.DoubleToInt64Bits(pts[i]), BitConverter.DoubleToInt64Bits(pts[i + 1]), BitConverter.DoubleToInt64Bits(pts[i + 2])));
        var V = hull.Value.V;
        for (int i = 0; i < V.Length; i += 3)
            Assert.Contains((BitConverter.DoubleToInt64Bits(V[i]), BitConverter.DoubleToInt64Bits(V[i + 1]), BitConverter.DoubleToInt64Bits(V[i + 2])), input);
    }
}
