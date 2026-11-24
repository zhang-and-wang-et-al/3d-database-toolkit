using SceneKit;

/// <summary>Synthetic inputs of the card tests (R3-C1): a textured tree with a leaf atlas, class maps and a unit-1 detail
/// map, two rigid copies of it, the same tree in memory as <see cref="Cards.Item"/>s, and a building with windows.
/// Everything is deterministic (seeded). Reused by the later card tasks: keep the signatures.</summary>
internal static class CardFixtures
{
    public const string ClassPattern = "{stem}_Classes.png";

    // ---- images -------------------------------------------------------------------------------------------------

    static RawImage Rgba(int w, int h, Func<int, int, (byte r, byte g, byte b, byte a)> f)
    {
        var img = new RawImage(w, h, 4);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = f(x, y);
                int i = 4 * (y * w + x);
                img.Pixels[i] = r; img.Pixels[i + 1] = g; img.Pixels[i + 2] = b; img.Pixels[i + 3] = a;
            }
        return img;
    }

    static bool InDisc(int x, int y) => (x - 7.5) * (x - 7.5) + (y - 7.5) * (y - 7.5) <= 7.5 * 7.5;
    static bool Blossom(int x, int y) => x >= 8 && y < 8;

    /// <summary>16 x 16 RGBA: alpha 0 outside a disc, green, with a red blossom quadrant.</summary>
    internal static RawImage LeafImage() => Rgba(16, 16, (x, y) =>
        !InDisc(x, y) ? ((byte)0, (byte)0, (byte)0, (byte)0) : Blossom(x, y) ? ((byte)220, (byte)40, (byte)60, (byte)255) : ((byte)40, (byte)160, (byte)50, (byte)255));

    /// <summary>Red channel 1 for the leaf, 2 for the blossom quadrant, 0 outside.</summary>
    internal static RawImage LeafClasses() => Rgba(16, 16, (x, y) => ((byte)(!InDisc(x, y) ? 0 : Blossom(x, y) ? 2 : 1), (byte)0, (byte)0, (byte)255));

    /// <summary>8 x 8 opaque brown.</summary>
    internal static RawImage BarkImage() => Rgba(8, 8, (x, y) => ((byte)110, (byte)75, (byte)45, (byte)255));

    /// <summary>8 x 8, solid class 0.</summary>
    internal static RawImage BarkClasses() => Rgba(8, 8, (x, y) => ((byte)0, (byte)0, (byte)0, (byte)255));

    /// <summary>8 x 8 grey (a diagonal ramp, so a wrong lookup shows).</summary>
    internal static RawImage DetailImage() => Rgba(8, 8, (x, y) => { byte g = (byte)(64 + 16 * ((x + y) % 8)); return (g, g, g, (byte)255); });

    // ---- geometry -----------------------------------------------------------------------------------------------

    static GeomData Mesh(List<double> v, List<double> n, List<double> uv, List<int> t)
    {
        var g = new GeomData { Vertices = new VArray(v.ToArray(), 3, "Vec3Array"), Normals = new VArray(n.ToArray(), 3, "Vec3Array") };
        g.TexCoords[0] = new VArray(uv.ToArray(), 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: t.Count, indices: t.ToArray(), indexType: MeshOps.SmallestIndexType(v.Count / 3)));
        return g;
    }

    /// <summary>12-sided cylinder, radius 0.3, z 0..3 at (10, 20, 0), with vertex colours (darker at the top).</summary>
    static GeomData Trunk(out double[] colors)
    {
        var v = new List<double>(); var n = new List<double>(); var uv = new List<double>(); var t = new List<int>(); var col = new List<double>();
        const int sides = 12;
        for (int i = 0; i <= sides; i++)
        {
            double a = 2 * Math.PI * i / sides, c = Math.Cos(a), s = Math.Sin(a);
            foreach (double z in new[] { 0.0, 3.0 })
            {
                v.AddRange(new[] { 10 + 0.3 * c, 20 + 0.3 * s, z });
                n.AddRange(new[] { c, s, 0.0 });
                uv.AddRange(new[] { (double)i / sides, z / 3 });
                col.AddRange(new[] { 0.7 + 0.1 * c, 0.6 - 0.2 * z / 3, 0.5, 1.0 });
            }
        }
        for (int i = 0; i < sides; i++)
        {
            int b = 2 * i;
            t.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }
        colors = col.ToArray();
        return Mesh(v, n, uv, t);
    }

    /// <summary>The crown: <paramref name="leaves"/> quads of 0.4 x 0.4 in random orientations inside the ellipsoid centred at
    /// (10, 20, 4.5), radii (2, 2, 1.5).</summary>
    static GeomData Crown(int leaves, int seed)
    {
        var rnd = new Random(seed);
        var v = new List<double>(); var n = new List<double>(); var uv = new List<double>(); var t = new List<int>();
        for (int k = 0; k < leaves; k++)
        {
            double px, py, pz;
            do { px = rnd.NextDouble() * 2 - 1; py = rnd.NextDouble() * 2 - 1; pz = rnd.NextDouble() * 2 - 1; } while (px * px + py * py + pz * pz > 1);
            double cx = 10 + 2 * px, cy = 20 + 2 * py, cz = 4.5 + 1.5 * pz;
            double[] nm;
            double len;
            do { nm = new[] { rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1 }; len = Math.Sqrt(nm[0] * nm[0] + nm[1] * nm[1] + nm[2] * nm[2]); } while (len < 0.1 || len > 1);
            for (int i = 0; i < 3; i++) nm[i] /= len;
            double[] helper = Math.Abs(nm[2]) < 0.9 ? new[] { 0.0, 0.0, 1.0 } : new[] { 1.0, 0.0, 0.0 };
            var u = Cross(helper, nm);
            double ul = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
            for (int i = 0; i < 3; i++) u[i] /= ul;
            var w = Cross(nm, u);
            double spin = rnd.NextDouble() * 2 * Math.PI, cs = Math.Cos(spin), sn = Math.Sin(spin);
            var a = new double[3]; var b = new double[3];
            for (int i = 0; i < 3; i++) { a[i] = cs * u[i] + sn * w[i]; b[i] = -sn * u[i] + cs * w[i]; }
            var centre = new[] { cx, cy, cz };
            int bi = v.Count / 3;
            foreach (var (s, r, uu, vv) in new[] { (-1.0, -1.0, 0.0, 0.0), (1.0, -1.0, 1.0, 0.0), (1.0, 1.0, 1.0, 1.0), (-1.0, 1.0, 0.0, 1.0) })
            {
                for (int i = 0; i < 3; i++) v.Add(centre[i] + 0.2 * (s * a[i] + r * b[i]));
                n.AddRange(nm);
                uv.AddRange(new[] { uu, vv });
            }
            t.AddRange(new[] { bi, bi + 1, bi + 2, bi, bi + 2, bi + 3 });
        }
        return Mesh(v, n, uv, t);
    }

    static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

    static double[] Translate(double x, double y, double z) { var m = Mat.Identity(); m[3] = x; m[7] = y; m[11] = z; return m; }

    static Texture Tex(string dir, string file, int unit, RawImage img) =>
        new()
        {
            File = file, Path = System.IO.Path.Combine(dir, file), Exists = true, Unit = unit, Width = img.Width, Height = img.Height,
            Channels = img.Channels, FileBytes = new FileInfo(System.IO.Path.Combine(dir, file)).Length, Fmt = "png",
        };

    static void Put(string dir, string file, RawImage img) => File.WriteAllBytes(System.IO.Path.Combine(dir, file), ImageCodecs.WritePng(img));

    /// <summary>The tree node (a Geode "tree": trunk and crown) with its textures taken from <paramref name="dir"/>.</summary>
    static Node TreeNode(string dir, int leaves, int seed, bool unit1)
    {
        var trunkGeom = Trunk(out var colors);
        trunkGeom.Colors = new VArray(colors, 4, "Vec4Array");
        var crownGeom = Crown(leaves, seed);
        var trunk = new Node(Kind.Geometry, "osg::Geometry", "trunk")
        {
            Geom = trunkGeom,
            StateSet = new StateSet { MaterialName = "bark", Diffuse = new[] { 1.0, 1.0, 1.0, 1.0 }, Textures = { Tex(dir, "bark.png", 0, BarkImage()) } },
        };
        var ss = new StateSet { MaterialName = "leaf", Diffuse = new[] { 1.0, 1.0, 1.0, 1.0 }, Textures = { Tex(dir, "leaf.png", 0, LeafImage()) } };
        if (unit1)
        {
            ss.Textures.Add(Tex(dir, "detail.png", 1, DetailImage()));
            crownGeom.TexCoords[1] = new VArray((double[])crownGeom.TexCoords[0].Data.Clone(), 2, "Vec2Array");
        }
        ss.Modes["GL_ALPHA_TEST"] = "ON";
        var crown = new Node(Kind.Geometry, "osg::Geometry", "crown") { Geom = crownGeom, StateSet = ss };
        return new Node(Kind.Geode, "osg::Geode", "tree") { Children = { trunk, crown } };
    }

    static void WriteImages(string dir, bool classMaps, bool unit1)
    {
        Directory.CreateDirectory(dir);
        Put(dir, "leaf.png", LeafImage());
        Put(dir, "leaf_Classes.png", LeafClasses());
        Put(dir, "bark.png", BarkImage());
        if (classMaps) Put(dir, "bark_Classes.png", BarkClasses());
        if (unit1) Put(dir, "detail.png", DetailImage());
    }

    static Scene MakeScene(string dir, Node root)
    {
        var sc = new Scene(System.IO.Path.Combine(dir, "tree.osgt"), "osgt") { Root = root };
        Companions.Detect(sc);   // default patterns: finds leaf_Classes.png / bark_Classes.png next to the textures
        return sc;
    }

    // ---- public fixtures ----------------------------------------------------------------------------------------

    /// <summary>The textured tree on disk (<paramref name="dir"/>) and as a scene.</summary>
    internal static (Scene sc, Node tree) TexturedTree(string dir, int leaves = 60, int seed = 7, bool classMaps = true, bool unit1 = true)
    {
        WriteImages(dir, classMaps, unit1);
        var tree = TreeNode(dir, leaves, seed, unit1);
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { tree } };
        return (MakeScene(dir, root), tree);
    }

    /// <summary>Only the tree node, with its image files written into <paramref name="dir"/> (for grafting into a scene of another
    /// format, e.g. an OpenFlight host whose folder is <paramref name="dir"/>).</summary>
    internal static Node TexturedTreeNode(string dir, int leaves = 60, int seed = 7, bool classMaps = true, bool unit1 = true)
    {
        WriteImages(dir, classMaps, unit1);
        return TreeNode(dir, leaves, seed, unit1);
    }

    /// <summary>The tree plus a rigid copy that stands at (30, 20, 0), turned 30 degrees about Z and tilted <paramref name="tiltDeg"/>
    /// about X (a MatrixTransform).</summary>
    internal static (Scene sc, Node a, Node b) TwoCopies(string dir, double tiltDeg = 0)
    {
        WriteImages(dir, true, true);
        var a = TreeNode(dir, 60, 7, true);
        var copy = TreeNode(dir, 60, 7, true);
        double z = 30 * Math.PI / 180, x = tiltDeg * Math.PI / 180;
        var rz = new[] { Math.Cos(z), -Math.Sin(z), 0, 0, Math.Sin(z), Math.Cos(z), 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var rx = new[] { 1.0, 0, 0, 0, 0, Math.Cos(x), -Math.Sin(x), 0, 0, Math.Sin(x), Math.Cos(x), 0, 0, 0, 0, 1 };
        var m = Mat.Mul(Translate(30, 20, 0), Mat.Mul(Mat.Mul(rz, rx), Translate(-10, -20, 0)));
        var b = new Node(Kind.Transform, "osg::MatrixTransform", "copy") { Matrix = m, Children = { copy } };
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { a, b } };
        return (MakeScene(dir, root), a, b);
    }

    /// <summary>The same tree in memory: images and layers set directly (class maps on unit 0's companion pattern, detail on unit 1).</summary>
    internal static List<Cards.Item> Items(int leaves = 60, int seed = 7)
    {
        var trunkGeom = Trunk(out var colors);
        var crownGeom = Crown(leaves, seed);
        var trunk = new Cards.Item
        {
            V = trunkGeom.Vertices!.Data, T = trunkGeom.Triangles(), UV = trunkGeom.TexCoords[0].Data, Image = BarkImage(),
            Color = new[] { 1.0, 1.0, 1.0, 1.0 }, VertexColors = colors,
        };
        trunk.Extra[new LayerKey(0, ClassPattern)] = (trunkGeom.TexCoords[0].Data, BarkClasses(), true);
        var crown = new Cards.Item
        {
            V = crownGeom.Vertices!.Data, T = crownGeom.Triangles(), UV = crownGeom.TexCoords[0].Data, Image = LeafImage(),
            Color = new[] { 1.0, 1.0, 1.0, 1.0 },
        };
        crown.Extra[new LayerKey(1)] = (crownGeom.TexCoords[0].Data, DetailImage(), false);
        crown.Extra[new LayerKey(0, ClassPattern)] = (crownGeom.TexCoords[0].Data, LeafClasses(), true);
        return new List<Cards.Item> { trunk, crown };
    }

    /// <summary>A 10 x 10 x 20 box with dark window quads (2 columns x 5 rows, 2 x 2) on its four sides.</summary>
    internal static (Scene sc, Node building) Building()
    {
        var (bv, bt) = OcclusionTests.Box(-5, -5, 0, 5, 5, 20);
        var wall = new GeomData { Vertices = new VArray(bv, 3, "Vec3Array") };
        wall.Prims.Add(new Primitive("TRIANGLES", "elements", count: bt.Length, indices: bt, indexType: "DrawElementsUInt"));
        var v = new List<double>(); var t = new List<int>();
        for (int side = 0; side < 4; side++)
        {
            double a = side * Math.PI / 2, c = Math.Cos(a), s = Math.Sin(a);
            for (int col = 0; col < 2; col++)
                for (int row = 0; row < 5; row++)
                {
                    double s0 = -3.5 + 3.5 * col, z0 = 1.5 + 3.5 * row;
                    int bi = v.Count / 3;
                    foreach (var (ss, zz) in new[] { (s0, z0), (s0 + 2, z0), (s0 + 2, z0 + 2), (s0, z0 + 2) })
                        v.AddRange(new[] { 5.02 * c - ss * s, 5.02 * s + ss * c, zz });   // just outside the wall of this side
                    t.AddRange(new[] { bi, bi + 1, bi + 2, bi, bi + 2, bi + 3 });
                }
        }
        var win = new GeomData { Vertices = new VArray(v.ToArray(), 3, "Vec3Array") };
        win.Prims.Add(new Primitive("TRIANGLES", "elements", count: t.Count, indices: t.ToArray(), indexType: MeshOps.SmallestIndexType(v.Count / 3)));
        var building = new Node(Kind.Geode, "osg::Geode", "building")
        {
            Children =
            {
                new Node(Kind.Geometry, "osg::Geometry", "walls") { Geom = wall, StateSet = new StateSet { MaterialName = "walls", Diffuse = new[] { 0.7, 0.7, 0.65, 1.0 } } },
                new Node(Kind.Geometry, "osg::Geometry", "windows") { Geom = win, StateSet = new StateSet { MaterialName = "windows", Diffuse = new[] { 0.05, 0.07, 0.1, 1.0 } } },
            },
        };
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { building } };
        return (new Scene("building.osgt", "osgt") { Root = root }, building);
    }
}
