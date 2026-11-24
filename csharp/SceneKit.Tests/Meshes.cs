using SceneKit;

/// <summary>Small synthetic meshes and scenes for tests.</summary>
internal static class Meshes
{
    /// <summary>A square grid of n x n cells over [0, size]² (z = height(x, y)), two triangles per cell, UVs 0..1
    /// unless <paramref name="uv"/> is false. Built like OptimizeTests.Geom (float positions, one indexed triangle list).</summary>
    public static GeomData Grid(int n, double size, Func<double, double, double>? height = null, bool uv = true)
    {
        var V = new List<double>();
        var UV = new List<double>();
        var T = new List<int>();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                double x = size * i / n, y = size * j / n;
                V.Add(x); V.Add(y); V.Add(height?.Invoke(x, y) ?? 0.0);
                UV.Add((double)i / n); UV.Add((double)j / n);
            }
        int Id(int i, int j) => j * (n + 1) + i;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                T.AddRange(new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1) });
                T.AddRange(new[] { Id(i, j), Id(i + 1, j + 1), Id(i, j + 1) });
            }
        var g = new GeomData { Vertices = new VArray(V.Select(x => (double)(float)x).ToArray(), 3, "Vec3Array") };
        if (uv) g.TexCoords[0] = new VArray(UV.Select(x => (double)(float)x).ToArray(), 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Count, indices: T.ToArray(), indexType: "DrawElementsUInt"));
        return g;
    }

    /// <summary>A textured n x n grid over [0, 1]² (z = 0) with a texture of texSize x texSize texels on unit 0. UVs are the grid
    /// coordinates, mapped through <paramref name="uv"/> when given. <paramref name="unshared"/>: three own vertices per
    /// triangle (so one triangle's UVs can change alone).</summary>
    public static (GeomData g, StateSet ss) TexturedGrid(int n, int texSize, Func<double, double, (double u, double v)>? uv = null, bool unshared = false)
    {
        var V = new List<double>();
        var UV = new List<double>();
        var T = new List<int>();
        void Add(int i, int j)
        {
            double x = (double)i / n, y = (double)j / n;
            V.Add(x); V.Add(y); V.Add(0.0);
            var (u, v) = uv?.Invoke(x, y) ?? (x, y);
            UV.Add(u); UV.Add(v);
        }
        if (!unshared)
        {
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++) Add(i, j);
            int Id(int i, int j) => j * (n + 1) + i;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    T.AddRange(new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1) });
                    T.AddRange(new[] { Id(i, j), Id(i + 1, j + 1), Id(i, j + 1) });
                }
        }
        else
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    foreach (var tri in new[] { new[] { (i, j), (i + 1, j), (i + 1, j + 1) }, new[] { (i, j), (i + 1, j + 1), (i, j + 1) } })
                        foreach (var (a, b) in tri) { T.Add(V.Count / 3); Add(a, b); }
        var g = new GeomData { Vertices = new VArray(V.Select(x => (double)(float)x).ToArray(), 3, "Vec3Array") };
        g.TexCoords[0] = new VArray(UV.Select(x => (double)(float)x).ToArray(), 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Count, indices: T.ToArray(), indexType: "DrawElementsUInt"));
        var ss = new StateSet { Textures = { new Texture { File = "t.png", Width = texSize, Height = texSize, Unit = 0, Exists = texSize > 0 } } };
        return (g, ss);
    }

    /// <summary>Height of the bumpy tile of <see cref="TileWithRoad"/>.</summary>
    public static double Bumps(double x, double y) => 0.4 * Math.Sin(0.9 * x) * Math.Cos(0.7 * y) + 0.25 * Math.Sin(0.5 * x + 0.8 * y);

    /// <summary>A 20 m bumpy terrain tile (40 x 40 cells), a 4 m wide road strip along x at y = 10 (y 8..12, the tile's
    /// own grid and triangulation) draped 0.02 m above it, and a 3 m box 'tree' standing on the tile at (4, 3).
    /// Returns the scene and the three geometry nodes. With <paramref name="place"/>, each part sits below a
    /// MatrixTransform with that matrix.</summary>
    public static (Scene sc, Node tile, Node road, Node tree) TileWithRoad(double[]? place = null)
    {
        var tile = Grid(40, 20, Bumps);
        // the road: 40 x 8 cells over [0, 20] x [8, 12], same 0.5 m spacing and diagonal as the tile, 2 cm above it
        var rv = new List<double>();
        for (int j = 0; j <= 8; j++)
            for (int i = 0; i <= 40; i++)
            {
                double x = 0.5 * i, y = 8 + 0.5 * j;
                rv.Add(x); rv.Add(y); rv.Add(Bumps(x, y) + 0.02);
            }
        var rt = new List<int>();
        int Id(int i, int j) => j * 41 + i;
        for (int j = 0; j < 8; j++)
            for (int i = 0; i < 40; i++)
            {
                rt.AddRange(new[] { Id(i, j), Id(i + 1, j), Id(i + 1, j + 1) });
                rt.AddRange(new[] { Id(i, j), Id(i + 1, j + 1), Id(i, j + 1) });
            }
        var road = new GeomData { Vertices = new VArray(rv.Select(x => (double)(float)x).ToArray(), 3, "Vec3Array") };
        road.Prims.Add(new Primitive("TRIANGLES", "elements", count: rt.Count, indices: rt.ToArray(), indexType: "DrawElementsUInt"));
        // the tree: a closed 3 m box standing on the ground at (4, 3)
        double bx = 4, by = 3, bz = Bumps(bx, by), s = 1.5;
        var bv = new List<double>();
        for (int k = 0; k < 8; k++) { bv.Add(bx + ((k & 1) == 0 ? -s : s)); bv.Add(by + ((k & 2) == 0 ? -s : s)); bv.Add(bz + ((k & 4) == 0 ? 0 : 3)); }
        int[] bt = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
        var box = new GeomData { Vertices = new VArray(bv.ToArray(), 3, "Vec3Array") };
        box.Prims.Add(new Primitive("TRIANGLES", "elements", count: bt.Length, indices: bt, indexType: "DrawElementsUInt"));
        var (sc, root) = SceneOf(("tile", tile, place), ("road", road, place), ("tree", box, place));
        Node Geo(int k) => place == null ? root.Children[k].Children[0] : root.Children[k].Children[0].Children[0];
        return (sc, Geo(0), Geo(1), Geo(2));
    }

    /// <summary>A 4 x 4 scale matrix (internal layout, column vectors).</summary>
    public static double[] Scale(double x, double y, double z)
    {
        var m = Mat.Identity();
        m[0] = x; m[5] = y; m[10] = z;
        return m;
    }

    /// <summary>A scene with one part per entry below the root: a MatrixTransform when the matrix is given, then a Geode
    /// holding the geometry. Returns the scene and its root.</summary>
    public static (Scene sc, Node root) SceneOf(params (string name, GeomData g, double[]? matrix)[] parts)
    {
        var root = new Node(Kind.Group, "osg::Group", "root");
        foreach (var (name, g, matrix) in parts)
        {
            var geo = new Node(Kind.Geometry, "osg::Geometry", name) { Geom = g };
            var geode = new Node(Kind.Geode, "osg::Geode", name + "_geode") { Children = { geo } };
            if (matrix != null)
                root.Children.Add(new Node(Kind.Transform, "osg::MatrixTransform", name + "_place") { Matrix = matrix, Children = { geode } });
            else root.Children.Add(geode);
        }
        return (new Scene("x.osgt", "osgt") { Root = root }, root);
    }

    /// <summary>One piece of <see cref="SynthCar"/>: a closed or one-sided mesh, its class column in the texture (0 body,
    /// 1 wheels, 2 glass, 3 mirrors) and the matrix that places it (a wheel's DOF) or null.</summary>
    sealed class SynthPiece
    {
        public string Name = "";
        public int Column;
        public double[]? Place;
        public readonly List<double> V = new();
        public readonly List<int> T = new();

        /// <summary>One side of a box: the plane <paramref name="ax"/> = <paramref name="c"/>, a grid over the
        /// coordinates <paramref name="us"/> and <paramref name="vs"/> of the next two axes, facing + (hiSide) or -.</summary>
        public SynthPiece Face(int ax, bool hiSide, double c, double[] us, double[] vs)
        {
            int u = (ax + 1) % 3, v = (ax + 2) % 3, b = V.Count / 3;
            foreach (double pv in vs)
                foreach (double pu in us)
                {
                    var p = new double[3];
                    p[ax] = c; p[u] = pu; p[v] = pv;
                    V.AddRange(p);
                }
            int nu = us.Length;
            for (int j = 0; j + 1 < vs.Length; j++)
                for (int i = 0; i + 1 < nu; i++)
                {
                    int a = b + j * nu + i, bb = a + 1, cc = a + nu, d = cc + 1;
                    if (hiSide) T.AddRange(new[] { a, bb, cc, bb, d, cc }); else T.AddRange(new[] { a, cc, bb, bb, cc, d });
                }
            return this;
        }

        /// <summary>A closed box over the grid coordinates xs x ys x zs (all six sides, outward).</summary>
        public SynthPiece Box(double[] xs, double[] ys, double[] zs) =>
            Face(0, false, xs[0], ys, zs).Face(0, true, xs[^1], ys, zs).Face(1, false, ys[0], zs, xs).Face(1, true, ys[^1], zs, xs)
                .Face(2, false, zs[0], xs, ys).Face(2, true, zs[^1], xs, ys);

        /// <summary>A capped cylinder of n segments around the axis <paramref name="ax"/> (1 = y, 2 = z) through
        /// <paramref name="ctr"/>, from <paramref name="a0"/> to <paramref name="a1"/> along it (outward). The cap
        /// centres are vertices.</summary>
        public SynthPiece Cylinder(int ax, double[] ctr, double r, double a0, double a1, int n)
        {
            int q = ax == 1 ? 2 : 1;   // across the cylinder: x and z around y, x and y around z
            int b = V.Count / 3;
            foreach (double a in new[] { a0, a1 })
                for (int k = 0; k < n; k++)
                {
                    double t = 2 * Math.PI * k / n;
                    var pt = (double[])ctr.Clone();
                    pt[0] += r * Math.Cos(t); pt[q] += r * Math.Sin(t); pt[ax] = a;
                    V.AddRange(pt);
                }
            int c0 = V.Count / 3, c1 = c0 + 1;
            var e0 = (double[])ctr.Clone(); e0[ax] = a0; V.AddRange(e0);
            var e1 = (double[])ctr.Clone(); e1[ax] = a1; V.AddRange(e1);
            // (x, y, z) is right-handed around z; (x, z, y) around y is not: flip the winding there
            void Tri(int x, int y, int z) { if (ax == 1) T.AddRange(new[] { x, z, y }); else T.AddRange(new[] { x, y, z }); }
            for (int k = 0; k < n; k++)
            {
                int k1 = (k + 1) % n, A = b + k, A1 = b + k1, B = b + n + k, B1 = b + n + k1;
                Tri(A, A1, B); Tri(A1, B1, B);
                Tri(c0, A1, A); Tri(c1, B, B1);
            }
            return this;
        }

        /// <summary>UVs inside the piece's class column of the texture: planar over its two longest sides.</summary>
        public double[] Uvs()
        {
            int n = V.Count / 3;
            var lo = new double[3]; var hi = new double[3];
            for (int c = 0; c < 3; c++) { lo[c] = double.PositiveInfinity; hi[c] = double.NegativeInfinity; }
            for (int i = 0; i < n; i++) for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], V[3 * i + c]); hi[c] = Math.Max(hi[c], V[3 * i + c]); }
            var axes = Enumerable.Range(0, 3).OrderByDescending(c => hi[c] - lo[c]).ThenBy(c => c).ToArray();
            int s = axes[0], t = axes[1];
            var uv = new double[2 * n];
            for (int i = 0; i < n; i++)
            {
                uv[2 * i] = Column * 0.25 + 0.03 + 0.19 * (V[3 * i + s] - lo[s]) / Math.Max(hi[s] - lo[s], 1e-12);
                uv[2 * i + 1] = 0.03 + 0.94 * (V[3 * i + t] - lo[t]) / Math.Max(hi[t] - lo[t], 1e-12);
            }
            return uv;
        }

        public Node Geometry()
        {
            var g = new GeomData { Vertices = new VArray(V.ToArray(), 3, "Vec3Array") };
            g.TexCoords[0] = new VArray(Uvs(), 2, "Vec2Array");
            g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Count, indices: T.ToArray(), indexType: "DrawElementsUInt"));
            return new Node(Kind.Geometry, "osg::Geometry", Name) { Geom = g };
        }
    }

    /// <summary>The synthetic car of the car package below a root group: Z up, 4.5 x 1.8 x 1.4 m without the antenna,
    /// about 21 000 triangles. Split: the body as 8 one-sided panels (static FLT groups body_1 ... body_8: floor, front,
    /// back, left, right, hood, roof, trunk) and a cabin (an open-bottom box standing on the roof); an engine block inside,
    /// on the floor; 4 wheels (64-segment cylinders, r 0.33 m, w 0.22 m, 3 cm below the floor, the arch), each below an
    /// FLT DOF node wheel_fl / wheel_fr / wheel_rl / wheel_rr that places it; mirrors mirror_l / mirror_r (0.15 x 0.05 x
    /// 0.10 m on 2 cm stalks on the cabin sides); an antenna (r 6 mm, 0.6 m) on the cabin roof; a switch 'lights' with
    /// the children lights_on / lights_off (values true, false). One checker texture on unit 0 (set on the car) with a
    /// class-map companion attached as IrTests does: body 1, wheels 2, glass (the cabin) 3, mirrors 4 (in R). Every corner
    /// two pieces share is computed by the same expression, so the positions are bit-equal.
    /// Merged (<paramref name="split"/> false): the same triangles in one geometry, the lights at their current state
    /// (lights_on only), the DOF matrices baked; with <paramref name="weldWheels"/>, one triangle from the top of each
    /// wheel to the floor point above its centre welds it to the arch. <paramref name="engineLift"/> raises the engine
    /// block that many metres above the floor pan (default 0: it sits flush on it; the bench passes 0.01).</summary>
    public static (Scene sc, Node car) SynthCar(bool split, bool weldWheels = true, double engineLift = 0)
    {
        static double X(int i) => -2.25 + 0.05 * i;
        static double Y(int j) => -0.9 + 0.05 * j;
        static double Z(int k) => 0.69 + 0.05 * k;
        static double[] Xs(int a, int b) => Enumerable.Range(a, b - a + 1).Select(X).ToArray();
        static double[] Ys(int a, int b) => Enumerable.Range(a, b - a + 1).Select(Y).ToArray();
        static double[] Zs(int a, int b) => Enumerable.Range(a, b - a + 1).Select(Z).ToArray();

        var statics = new List<SynthPiece>
        {
            new SynthPiece { Name = "body_1" }.Face(2, false, Z(0), Xs(0, 90), Ys(0, 36)),   // floor
            new SynthPiece { Name = "body_2" }.Face(0, true, X(90), Ys(0, 36), Zs(0, 6)),    // front
            new SynthPiece { Name = "body_3" }.Face(0, false, X(0), Ys(0, 36), Zs(0, 6)),    // back
            new SynthPiece { Name = "body_4" }.Face(1, true, Y(36), Zs(0, 6), Xs(0, 90)),    // left
            new SynthPiece { Name = "body_5" }.Face(1, false, Y(0), Zs(0, 6), Xs(0, 90)),    // right
            new SynthPiece { Name = "body_6" }.Face(2, true, Z(6), Xs(65, 90), Ys(0, 36)),   // hood
            new SynthPiece { Name = "body_7" }.Face(2, true, Z(6), Xs(25, 65), Ys(0, 36)),   // roof
            new SynthPiece { Name = "body_8" }.Face(2, true, Z(6), Xs(0, 25), Ys(0, 36)),    // trunk
            new SynthPiece { Name = "cabin", Column = 2 }.Face(0, false, X(27), Ys(3, 33), Zs(6, 14)).Face(0, true, X(61), Ys(3, 33), Zs(6, 14))
                .Face(1, false, Y(3), Zs(6, 14), Xs(27, 61)).Face(1, true, Y(33), Zs(6, 14), Xs(27, 61)).Face(2, true, Z(14), Xs(27, 61), Ys(3, 33)),
            new SynthPiece { Name = "engine" }.Box(new[] { X(70), X(86) }, new[] { Y(10), Y(26) }, new[] { Z(0) + engineLift, Z(5) + engineLift }),
        };
        // mirrors: a 2 cm stalk from the cabin side, the mirror on its end
        double outL = Y(33) + 0.02, outR = Y(3) - 0.02;
        statics.Add(new SynthPiece { Name = "mirror_l", Column = 3 }.Box(Xs(58, 59), new[] { Y(33), outL }, Zs(8, 9)).Box(Xs(57, 60), new[] { outL, outL + 0.05 }, Zs(8, 10)));
        statics.Add(new SynthPiece { Name = "mirror_r", Column = 3 }.Box(Xs(58, 59), new[] { outR, Y(3) }, Zs(8, 9)).Box(Xs(57, 60), new[] { outR - 0.05, outR }, Zs(8, 10)));
        statics.Add(new SynthPiece { Name = "antenna" }.Cylinder(2, new[] { X(32), Y(18), 0 }, 0.006, Z(14), Z(14) + 0.6, 8));
        // the wheels around their own origin; the DOF places them (centre above floor lattice point i, j)
        var wheelAt = new[] { ("wheel_fl", 75, 33), ("wheel_fr", 75, 3), ("wheel_rl", 15, 33), ("wheel_rr", 15, 3) };
        var wheels = wheelAt.Select(w =>
        {
            var m = Mat.Identity();
            m[3] = X(w.Item2); m[7] = Y(w.Item3); m[11] = 0.33;
            return new SynthPiece { Name = w.Item1, Column = 1, Place = m }.Cylinder(1, new double[3], 0.33, -0.11, 0.11, 64);
        }).ToList();
        SynthPiece Lights(string name, double depth) => new SynthPiece { Name = name }
            .Box(new[] { X(90), X(90) + depth }, Ys(4, 8), Zs(3, 4)).Box(new[] { X(90), X(90) + depth }, Ys(28, 32), Zs(3, 4));
        var lightsOn = Lights("lights_on", 0.02);
        var lightsOff = Lights("lights_off", 0.01);

        var checker = new RawImage(64, 64, 3);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                byte v = (byte)((x / 8 + y / 8) % 2 == 0 ? 230 : 60);
                checker.Pixels[(y * 64 + x) * 3] = v; checker.Pixels[(y * 64 + x) * 3 + 1] = v; checker.Pixels[(y * 64 + x) * 3 + 2] = (byte)(255 - v);
            }
        var classes = new RawImage(8, 8, 3);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) classes.Pixels[(y * 8 + x) * 3] = (byte)(x / 2 + 1);   // 2 texels per column
        var tex = new Texture { File = "car.png", Baked = checker, Exists = true, Channels = 3, Width = 64, Height = 64 };
        tex.Companions = new() { [IrTests.Cls] = IrTests.ClassCompanion("car_Classes.png", classes) };
        var car = new Node(Kind.Group, "FLT Group", "car") { StateSet = new StateSet { Textures = { tex } } };
        static Node Geode(SynthPiece p) => new(Kind.Geode, "FLT Object", p.Name) { Children = { p.Geometry() } };

        if (split)
        {
            foreach (var p in statics) car.Children.Add(new Node(Kind.Group, "FLT Group", p.Name) { Children = { Geode(p) } });
            foreach (var w in wheels) car.Children.Add(new Node(Kind.Transform, "FLT DOF", w.Name) { Matrix = w.Place, Children = { Geode(w) } });
            car.Children.Add(new Node(Kind.Switch, "FLT Switch", "lights") { SwitchValues = new() { true, false }, Children = { Geode(lightsOn), Geode(lightsOff) } });
        }
        else
        {
            var all = new SynthPiece { Name = "car_mesh" };
            var uv = new List<double>();
            foreach (var p in statics.Concat(wheels).Append(lightsOn))
            {
                int b = all.V.Count / 3;
                for (int i = 0; i < p.V.Count / 3; i++)
                {
                    var (x, y, z) = p.Place == null ? (p.V[3 * i], p.V[3 * i + 1], p.V[3 * i + 2]) : Mat.Apply(p.Place, p.V[3 * i], p.V[3 * i + 1], p.V[3 * i + 2]);
                    all.V.AddRange(new[] { x, y, z });
                }
                all.T.AddRange(p.T.Select(t => t + b));
                uv.AddRange(p.Uvs());
            }
            if (weldWheels)
                for (int w = 0; w < wheels.Count; w++)
                {
                    // segment 16 (the top) of both rims, and the floor point above the centre
                    int b = all.V.Count / 3;
                    foreach (int k in new[] { 16, 64 + 16 })
                    {
                        var (x, y, z) = Mat.Apply(wheels[w].Place!, wheels[w].V[3 * k], wheels[w].V[3 * k + 1], wheels[w].V[3 * k + 2]);
                        all.V.AddRange(new[] { x, y, z });
                    }
                    all.V.AddRange(new[] { X(wheelAt[w].Item2), Y(wheelAt[w].Item3), Z(0) });
                    all.T.AddRange(new[] { b, b + 1, b + 2 });
                    uv.AddRange(new[] { 0.1, 0.5, 0.1, 0.5, 0.1, 0.5 });
                }
            var g = new GeomData { Vertices = new VArray(all.V.ToArray(), 3, "Vec3Array") };
            g.TexCoords[0] = new VArray(uv.ToArray(), 2, "Vec2Array");
            g.Prims.Add(new Primitive("TRIANGLES", "elements", count: all.T.Count, indices: all.T.ToArray(), indexType: "DrawElementsUInt"));
            car.Children.Add(new Node(Kind.Geode, "FLT Object", "car_mesh") { Children = { new Node(Kind.Geometry, "osg::Geometry", "car_mesh") { Geom = g } } });
        }
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { car } };
        return (new Scene("x.osgt", "osgt") { Root = root }, car);
    }
}
