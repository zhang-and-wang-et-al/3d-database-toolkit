using SceneKit;
using Xunit;

/// <summary>D2 "permissive" seams: normal seams (hard edges, flat shading) no longer block collapses, UV seams do.</summary>
public class PermissiveTests
{
    static GeomData Geom(List<double> V, List<double> N, List<double> UV, List<int> T)
    {
        var g = new GeomData
        {
            Vertices = new VArray(V.ToArray(), 3, "Vec3Array"),
            Normals = new VArray(N.ToArray(), 3, "Vec3Array"),
        };
        g.TexCoords[0] = new VArray(UV.ToArray(), 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Count, indices: T.ToArray(), indexType: "DrawElementsUInt"));
        return g;
    }

    static void Face(List<double> V, List<double> N, List<double> UV, List<int> T, double[][] p, double[][] uv)
    {
        double ux = p[1][0] - p[0][0], uy = p[1][1] - p[0][1], uz = p[1][2] - p[0][2];
        double vx = p[2][0] - p[0][0], vy = p[2][1] - p[0][1], vz = p[2][2] - p[0][2];
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        for (int s = 0; s < 3; s++)
        {
            T.Add(V.Count / 3);
            V.AddRange(p[s]); N.AddRange(new[] { nx / l, ny / l, nz / l }); UV.AddRange(uv[s]);
        }
    }

    /// <summary>A gently wavy grid, flat shaded: every triangle has its own three vertices (own normal, shared UVs).</summary>
    internal static GeomData FacetedGrid(int n = 24)
    {
        var V = new List<double>(); var N = new List<double>(); var UV = new List<double>(); var T = new List<int>();
        double[] P(int i, int j) { double x = (double)i / n, y = (double)j / n; return new[] { x, y, 0.03 * Math.Sin(x * 5) * Math.Cos(y * 4) }; }
        double[] U(int i, int j) => new[] { (double)i / n, (double)j / n };
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                Face(V, N, UV, T, new[] { P(i, j), P(i + 1, j), P(i + 1, j + 1) }, new[] { U(i, j), U(i + 1, j), U(i + 1, j + 1) });
                Face(V, N, UV, T, new[] { P(i, j), P(i + 1, j + 1), P(i, j + 1) }, new[] { U(i, j), U(i + 1, j + 1), U(i, j + 1) });
            }
        return Geom(V, N, UV, T);
    }

    /// <summary>A unit cube, each side an n x n grid with its own vertices, normals and UV island (hard edges).</summary>
    internal static GeomData Cube(int n = 8)
    {
        var V = new List<double>(); var N = new List<double>(); var UV = new List<double>(); var T = new List<int>();
        for (int axis = 0; axis < 3; axis++)
            foreach (int sgn in new[] { -1, 1 })
            {
                int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                double[] P(int i, int j)
                {
                    var p = new double[3];
                    p[axis] = sgn * 0.5; p[a1] = (double)i / n - 0.5; p[a2] = (double)j / n - 0.5;
                    return p;
                }
                double[] U(int i, int j) => new[] { (double)i / n, (double)j / n };
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        // winding so the normal points out
                        if (sgn > 0)
                        {
                            Face(V, N, UV, T, new[] { P(i, j), P(i + 1, j), P(i + 1, j + 1) }, new[] { U(i, j), U(i + 1, j), U(i + 1, j + 1) });
                            Face(V, N, UV, T, new[] { P(i, j), P(i + 1, j + 1), P(i, j + 1) }, new[] { U(i, j), U(i + 1, j + 1), U(i, j + 1) });
                        }
                        else
                        {
                            Face(V, N, UV, T, new[] { P(i, j), P(i + 1, j + 1), P(i + 1, j) }, new[] { U(i, j), U(i + 1, j + 1), U(i + 1, j) });
                            Face(V, N, UV, T, new[] { P(i, j), P(i, j + 1), P(i + 1, j + 1) }, new[] { U(i, j), U(i, j + 1), U(i + 1, j + 1) });
                        }
                    }
            }
        return Geom(V, N, UV, T);
    }

    [Fact]
    public void FlatShadedMeshReducesMuchMoreThanWithKeep()
    {
        var g = FacetedGrid();
        long m = g.Counts().tris;
        var keep = Optimizer.SimplifyGeom(g, new[] { 0.1 }, "qem", minTris: 4, seams: "keep")[0];
        var perm = Optimizer.SimplifyGeom(g, new[] { 0.1 }, "qem", minTris: 4, seams: "permissive")[0];
        Assert.NotNull(perm);
        Assert.True(perm!.Counts().tris <= m * 0.1 + 2, $"{perm.Counts().tris} of {m}");
        Assert.True(keep == null || keep.Counts().tris > perm.Counts().tris * 2);
        var q = Quality.Compare(g, perm);
        Assert.True(q.MaxDevPct < Quality.CheckDev);
        Assert.NotNull(perm.Normals);
        Assert.Equal(perm.NumVertices, perm.Normals!.Count);
    }

    [Fact]
    public void HardEdgesStayHardAndNormalsPointOut()
    {
        var g = Cube();
        var perm = Optimizer.SimplifyGeom(g, new[] { 0.1 }, "qem", minTris: 4, seams: "permissive")[0];
        Assert.NotNull(perm);
        var V = perm!.Vertices!.Data; var N = perm.Normals!.Data;
        for (int i = 0; i < perm.NumVertices; i++)
        {
            // every normal is one of the six axes (flat sides, no smoothing over the 90 degree edges), pointing out
            double ax = Math.Abs(N[3 * i]), ay = Math.Abs(N[3 * i + 1]), az = Math.Abs(N[3 * i + 2]);
            Assert.True(Math.Max(ax, Math.Max(ay, az)) > 0.999, $"normal {N[3 * i]} {N[3 * i + 1]} {N[3 * i + 2]}");
            double dot = V[3 * i] * N[3 * i] + V[3 * i + 1] * N[3 * i + 1] + V[3 * i + 2] * N[3 * i + 2];
            Assert.True(dot > 0.4);
        }
    }

    [Fact]
    public void UvSeamsStayClosed()
    {
        // the cube's sides are separate UV islands: the edges are UV seams and must not open
        var g = Cube();
        var perm = Optimizer.SimplifyGeom(g, new[] { 0.1 }, "qem", minTris: 4, seams: "permissive")[0];
        var q = Quality.Compare(g, perm!);
        Assert.True(q.BoundaryAfter <= q.BoundaryBefore);
        Assert.True(q.MaxDevPct < Quality.CheckDev);
        foreach (var u in perm!.TexCoords[0].Data) Assert.InRange(u, -0.01, 1.01);
    }

    [Fact]
    public void PassOffersPermissive()
    {
        var spec = Optimizer.Passes["simplify"].Params.First(p => p.Key == "seams");
        Assert.Contains("permissive", spec.Choices!);
    }
}
