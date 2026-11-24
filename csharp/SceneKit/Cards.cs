namespace SceneKit;

/// <summary>Billboard / cross cards for trees and bushes (the "impostor" approach of image generators and LOD tools):
/// the object is rendered orthographically from several horizontal directions into one RGBA atlas (colour = texture
/// x material x vertex colour, unlit; alpha = coverage, texture cut-outs stay holes), and each image goes onto a
/// vertical quad through the object's centre. Z is up. Texture units above 0 (detail / light maps) and companion images
/// (infrared, see <see cref="Companions"/>) get parallel atlases with the same layout: the texel shows that layer's image
/// raw (no material or vertex colour, alpha 255) at the surface point that won the base image; a part without the layer
/// gets the fill (<see cref="ExtraFill"/>, white by default).</summary>
public static class Cards
{
    /// <summary>One source drawable in the object's frame.</summary>
    public sealed class Item
    {
        public double[] V = Array.Empty<double>();
        public int[] T = Array.Empty<int>();
        public double[]? UV;
        public RawImage? Image;
        public double[] Color = { 1, 1, 1, 1 };
        public double[]? VertexColors;
        /// <summary>Layers (texture units above 0, companion images): UVs (2 x n), image, and whether it is a class map
        /// (nearest lookup, never blended).</summary>
        public Dictionary<LayerKey, (double[] UV, RawImage Image, bool ClassMap)> Extra = new();
    }

    /// <summary>One card: the view direction it was rendered along (the card faces the other way, towards the viewer),
    /// its UV rectangle in the atlas and its extent across (s) and up (z) in the object frame. The view frame
    /// (<see cref="Right"/>, <see cref="Up"/>), its extents along them (A, B, measured from the centre), the offset along
    /// <see cref="Dir"/> and the atlas cell (index into <see cref="Result.Cells"/>) describe the same card for any view.</summary>
    public sealed class Card
    {
        public double[] Dir = new double[3];
        public double U0, V0, U1, V1;
        public double S0, S1, Z0, Z1;
        public double[] Right = new double[3], Up = new double[3];
        public double A0, A1, B0, B1, Offset;
        public int Cell;
    }

    public sealed class Result
    {
        public RawImage Atlas = new(1, 1, 4);
        /// <summary>Atlas per layer (texture unit above 0, companion image), laid out like Atlas.</summary>
        public Dictionary<LayerKey, RawImage> Extra = new();
        public List<Card> Cards = new();
        public double[] Center = new double[3];
        /// <summary>Bounds of the items (object frame).</summary>
        public double[] Lo = new double[3], Hi = new double[3];
        /// <summary>Atlas cells in texels (index = <see cref="Card.Cell"/>).</summary>
        public List<CellRect> Cells = new();
    }

    /// <summary>One picture of the object: looking along <see cref="Dir"/>, <see cref="Right"/> to the right, <see cref="Up"/>
    /// up. All measured from <see cref="Result.Center"/>: <see cref="Offset"/> (where the card sits) and <see cref="Slab"/>
    /// (only geometry at these depths is drawn) along Dir, <see cref="Extent"/> along Right (A) and Up (B); a null extent
    /// fits the items' projection. <see cref="Weight"/> is for the area-based cell rule.</summary>
    public sealed class View
    {
        public double[] Dir = new double[3], Right = new double[3], Up = new double[3];
        public double Offset;
        public (double Lo, double Hi)? Slab;
        public double Weight = 1;
        public (double A0, double A1, double B0, double B1)? Extent;
    }

    /// <summary>Looking horizontally along the azimuth (degrees; 0 = along +x), Z up.</summary>
    public static View Vertical(double azimuthDeg)
    {
        double a = azimuthDeg * Math.PI / 180;
        var d = new[] { Math.Cos(a), Math.Sin(a), 0.0 };
        return new View { Dir = d, Right = new[] { d[1], -d[0], 0.0 }, Up = new[] { 0.0, 0.0, 1.0 } };
    }

    /// <summary>Looking down (along -z): +x right, +y up in the picture.</summary>
    public static View Top() => new() { Dir = new[] { 0.0, 0.0, -1.0 }, Right = new[] { 1.0, 0.0, 0.0 }, Up = new[] { 0.0, 1.0, 0.0 } };

    /// <summary>Looking up (along +z): +x right, -y up in the picture (seen from below, not mirrored).</summary>
    public static View Bottom() => new() { Dir = new[] { 0.0, 0.0, 1.0 }, Right = new[] { 1.0, 0.0, 0.0 }, Up = new[] { 0.0, -1.0, 0.0 } };

    /// <summary>How the atlas is divided between the views: equal cells, or cells sized by projected area x weight.</summary>
    public enum CellRule { Equal, ByArea }

    /// <summary>Edge padding of a cell: 8 texels of dilation, or the whole cell (every texel takes its nearest covered colour).</summary>
    public enum Padding { Eight, Full }

    /// <summary>A cell of the atlas in texels (x right, y down from the top-left).</summary>
    public readonly record struct CellRect(int X, int Y, int W, int H);

    /// <summary>A view drawn with today's arithmetic: Z up, horizontal, no slab (every view of the azimuth path).</summary>
    static bool IsUpright(View v) =>
        v.Slab == null && v.Up[0] == 0 && v.Up[1] == 0 && v.Up[2] == 1 && v.Dir[2] == 0 && v.Right[2] == 0;

    /// <summary>Bounds of the items' triangle corners (object frame).</summary>
    internal static (double[] lo, double[] hi) Bounds(IList<Item> items)
    {
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var it in items)
            foreach (int v in it.T)
                for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], it.V[3 * v + c]); hi[c] = Math.Max(hi[c], it.V[3 * v + c]); }
        return (lo, hi);
    }

    static (List<LayerKey> units, HashSet<LayerKey> classUnits) Layers(IList<Item> items) =>
        (LayerKey.Sort(items.SelectMany(it => it.Extra.Keys).Distinct(), k => k).ToList(),
         items.SelectMany(it => it.Extra).Where(kv => kv.Value.ClassMap).Select(kv => kv.Key).ToHashSet());

    /// <summary>One opaque layer atlas per unit, filled with the unit's fill (where no cell lies).</summary>
    static void FillLayers(Result res, List<LayerKey> units, HashSet<LayerKey> classUnits, int size, ExtraFill fill, ExtraFill classFill)
    {
        Span<double> bg = stackalloc double[4];
        foreach (var u in units)
        {
            (classUnits.Contains(u) ? classFill : fill).Apply(bg, 0, 0, 0);
            var img = res.Extra[u] = new RawImage(size, size, 4);
            for (int i = 0; i < size * size; i++)   // the fill (opaque) where no cell lies
            {
                for (int k = 0; k < 3; k++) img.Pixels[4 * i + k] = (byte)Math.Round(bg[k] * 255);
                img.Pixels[4 * i + 3] = 255;
            }
        }
    }

    static void Blit(RawImage src, RawImage dst, int ox, int oy)
    {
        for (int y = 0; y < src.Height; y++) Array.Copy(src.Pixels, y * src.Width * 4, dst.Pixels, ((oy + y) * dst.Width + ox) * 4, src.Width * 4);
    }

    /// <summary>Render the items along the given azimuths (degrees; 0 = looking along +x) into a size x size atlas.
    /// Class-map layers get <paramref name="classFill"/> where a part has none.</summary>
    public static Result? Render(IList<Item> items, IList<double> azimuths, int size, ExtraFill? fill = null, ExtraFill? classFill = null)
    {
        fill ??= ExtraFill.White;
        classFill ??= ExtraFill.ClassFill(0);
        var (lo, hi) = Bounds(items);
        if (!(hi[2] > lo[2]) || azimuths.Count == 0) return null;
        var res = new Result { Center = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2] }, Lo = lo, Hi = hi };
        int n = azimuths.Count, cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (n + cols - 1) / cols;
        int cw = size / cols, ch = size / rows;
        var atlas = new RawImage(size, size, 4);
        var (units, classUnits) = Layers(items);
        FillLayers(res, units, classUnits, size, fill, classFill);
        for (int k = 0; k < n; k++)
        {
            double a = azimuths[k] * Math.PI / 180;
            var d = new[] { Math.Cos(a), Math.Sin(a), 0.0 };
            var r = new[] { d[1], -d[0], 0.0 };   // d x up: the viewer's right
            double s0 = double.PositiveInfinity, s1 = double.NegativeInfinity;
            foreach (var it in items)
                foreach (int v in it.T)
                {
                    double s = (it.V[3 * v] - res.Center[0]) * r[0] + (it.V[3 * v + 1] - res.Center[1]) * r[1];
                    s0 = Math.Min(s0, s); s1 = Math.Max(s1, s);
                }
            if (!(s1 > s0)) { s0 -= 0.5; s1 += 0.5; }
            int ox = k % cols * cw, oy = k / cols * ch;
            var view = new View { Dir = d, Right = r, Up = new[] { 0.0, 0.0, 1.0 } };
            double b0 = lo[2] - res.Center[2], b1 = hi[2] - res.Center[2];
            var (cell, extra) = Raster(items, units, classUnits, view, res.Center, s0, s1, b0, b1, lo[2], hi[2], cw, ch, fill, classFill);
            Blit(cell, atlas, ox, oy);
            foreach (var (u, img) in extra) Blit(img, res.Extra[u], ox, oy);
            res.Cells.Add(new CellRect(ox, oy, cw, ch));
            // half a texel inside the cell, so filtering never reaches the neighbour
            res.Cards.Add(new Card
            {
                Dir = d, S0 = s0, S1 = s1, Z0 = lo[2], Z1 = hi[2],
                U0 = (ox + 0.5) / size, U1 = (ox + cw - 0.5) / size, V1 = 1 - (oy + 0.5) / size, V0 = 1 - (oy + ch - 0.5) / size,
                Right = r, Up = view.Up, A0 = s0, A1 = s1, B0 = b0, B1 = b1, Cell = k,
            });
        }
        res.Atlas = atlas;
        return res;
    }

    /// <summary>Render the items once per view into a size x size atlas. <see cref="CellRule.Equal"/>: equal grid, cols =
    /// ceil(sqrt n). <see cref="CellRule.ByArea"/>: cells packed by <see cref="Pack"/>, each with an area proportional to
    /// the view's <see cref="View.Weight"/> and the aspect of its extent. <see cref="Padding.Full"/> floods every
    /// transparent texel of a cell with its nearest covered texel (<see cref="FloodPad"/>). Upright views without a slab
    /// (<see cref="Vertical"/>) draw with the azimuth render's arithmetic, so the same azimuths give the same bytes.</summary>
    public static Result? Render(IList<Item> items, IList<View> views, int size, CellRule cells = CellRule.Equal, ExtraFill? fill = null,
        ExtraFill? classFill = null, Padding padding = Padding.Eight)
    {
        fill ??= ExtraFill.White;
        classFill ??= ExtraFill.ClassFill(0);
        var (lo, hi) = Bounds(items);
        if (!(hi[2] > lo[2]) || views.Count == 0) return null;
        var res = new Result { Center = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2] }, Lo = lo, Hi = hi };
        int n = views.Count, cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (n + cols - 1) / cols;
        var frames = views.Select(v => Frame(items, v, res.Center, lo[2], hi[2])).ToList();
        List<CellRect> rects;
        if (cells == CellRule.ByArea)
        {
            // cell area ~ the view's weight (world area), aspect = the extent's
            var planes = new List<(double W, double H)>();
            for (int k = 0; k < n; k++)
            {
                double ea = frames[k].a1 - frames[k].a0, eb = frames[k].b1 - frames[k].b0, f = Math.Sqrt(Math.Max(views[k].Weight, 1e-12) / (ea * eb));
                planes.Add((ea * f, eb * f));
            }
            rects = Pack(planes, size, out _);
        }
        else
        {
            int gw = size / cols, gh = size / rows;
            rects = Enumerable.Range(0, n).Select(k => new CellRect(k % cols * gw, k / cols * gh, gw, gh)).ToList();
        }
        var atlas = new RawImage(size, size, 4);
        var (units, classUnits) = Layers(items);
        FillLayers(res, units, classUnits, size, fill, classFill);
        for (int k = 0; k < n; k++)
        {
            var view = views[k];
            var (a0, a1, b0, b1, z0, z1) = frames[k];
            var (ox, oy, cw, ch) = rects[k];
            var (cell, extra) = Raster(items, units, classUnits, view, res.Center, a0, a1, b0, b1, z0, z1, cw, ch, fill, classFill, padding == Padding.Full);
            Blit(cell, atlas, ox, oy);
            foreach (var (u, img) in extra) Blit(img, res.Extra[u], ox, oy);
            res.Cells.Add(rects[k]);
            res.Cards.Add(new Card
            {
                Dir = (double[])view.Dir.Clone(), S0 = a0, S1 = a1, Z0 = z0, Z1 = z1,
                U0 = (ox + 0.5) / size, U1 = (ox + cw - 0.5) / size, V1 = 1 - (oy + 0.5) / size, V0 = 1 - (oy + ch - 0.5) / size,
                Right = (double[])view.Right.Clone(), Up = (double[])view.Up.Clone(), A0 = a0, A1 = a1, B0 = b0, B1 = b1, Offset = view.Offset, Cell = k,
            });
        }
        res.Atlas = atlas;
        return res;
    }

    /// <summary>Shelf packing of planes (world width x height; only the area and aspect matter) into a size x size atlas.
    /// Each cell's area is proportional to its plane's area (scale^2 x W x H) and its aspect follows the plane; cells are whole
    /// texels, at least 1 x 1. Shelves are filled tallest first; a binary search finds the largest scale that fits. The
    /// cells come back in the order of the planes; <paramref name="scale"/> is texels per world unit.</summary>
    internal static List<CellRect> Pack(IReadOnlyList<(double W, double H)> planes, int size, out double scale)
    {
        int n = planes.Count;
        CellRect[]? Try(double sc)
        {
            var sz = planes.Select(p => (w: Math.Max(1, (int)Math.Floor(p.W * sc)), h: Math.Max(1, (int)Math.Floor(p.H * sc)))).ToArray();
            var idx = Enumerable.Range(0, n).OrderByDescending(i => sz[i].h).ThenBy(i => i).ToArray();
            var o = new CellRect[n];
            int x = 0, y = 0, shelf = 0;
            foreach (int i in idx)
            {
                var (w, h) = sz[i];
                if (w > size) return null;
                if (x + w > size) { y += shelf; x = 0; shelf = 0; }
                if (y + h > size) return null;
                o[i] = new CellRect(x, y, w, h);
                x += w; shelf = Math.Max(shelf, h);
            }
            return o;
        }
        double sLo = 0, sHi = 1;
        while (Try(sHi) != null && sHi < 1e9) sHi *= 2;
        var best = Try(sLo) ?? throw new InvalidOperationException($"{n} pictures do not fit a {size} x {size} atlas");
        for (int it = 0; it < 50; it++)
        {
            double mid = (sLo + sHi) / 2;
            var t = Try(mid);
            if (t != null) { sLo = mid; best = t; } else sHi = mid;
        }
        scale = sLo;
        return best.ToList();
    }

    /// <summary>Every transparent texel (alpha 0) of the cell takes the RGB of its nearest covered texel (alpha above 0;
    /// multi-source breadth-first search over the 4-neighbours inside the cell). Alpha stays; values are copied, never
    /// averaged, so a class map keeps only its IDs. A cell without a covered texel is left as it is.</summary>
    internal static void FloodPad(RawImage img, CellRect cell)
    {
        int W = img.Width, w = cell.W, h = cell.H;
        var p = img.Pixels;
        var src = new int[w * h];
        var queue = new int[w * h];
        int head = 0, tail = 0;
        for (int i = 0; i < w * h; i++)
        {
            src[i] = -1;
            if (p[4 * ((cell.Y + i / w) * W + cell.X + i % w) + 3] > 0) { src[i] = i; queue[tail++] = i; }
        }
        if (tail == 0 || tail == w * h) return;
        while (head < tail)
        {
            int i = queue[head++], x = i % w, y = i / w;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), ny = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = ny * w + nx;
                if (src[j] >= 0) continue;
                src[j] = src[i]; queue[tail++] = j;
            }
        }
        for (int i = 0; i < w * h; i++)
        {
            if (src[i] == i) continue;
            int o = 4 * ((cell.Y + i / w) * W + cell.X + i % w), f = 4 * ((cell.Y + src[i] / w) * W + cell.X + src[i] % w);
            p[o] = p[f]; p[o + 1] = p[f + 1]; p[o + 2] = p[f + 2];
        }
    }

    /// <summary>The view's extent (A along Right, B along Up, from the centre) plus the absolute z range an upright view
    /// draws with. A fitted upright extent is measured exactly as the azimuth render measures it (s from x and y only, z =
    /// the items' z range), so its bytes match; Z0 / Z1 of other views are centre z + B.</summary>
    static (double a0, double a1, double b0, double b1, double z0, double z1) Frame(IList<Item> items, View view, double[] ctr, double zLo, double zHi)
    {
        if (view.Extent is { } e)
        {
            // an explicit extent is the caller's frame: it is not widened, so one without area is a caller error
            if (!(e.A1 > e.A0) || !(e.B1 > e.B0)) throw new ArgumentException($"view extent {e} has no area", nameof(view));
            return (e.A0, e.A1, e.B0, e.B1, ctr[2] + e.B0, ctr[2] + e.B1);
        }
        bool upright = IsUpright(view);
        double[] r = view.Right, u = view.Up;
        double a0 = double.PositiveInfinity, a1 = double.NegativeInfinity, b0 = double.PositiveInfinity, b1 = double.NegativeInfinity;
        foreach (var it in items)
            foreach (int v in it.T)
            {
                double x = it.V[3 * v] - ctr[0], y = it.V[3 * v + 1] - ctr[1];
                if (upright) { double s = x * r[0] + y * r[1]; a0 = Math.Min(a0, s); a1 = Math.Max(a1, s); continue; }
                double z = it.V[3 * v + 2] - ctr[2];
                double p = x * r[0] + y * r[1] + z * r[2], q = x * u[0] + y * u[1] + z * u[2];
                a0 = Math.Min(a0, p); a1 = Math.Max(a1, p); b0 = Math.Min(b0, q); b1 = Math.Max(b1, q);
            }
        if (!(a1 > a0)) { a0 -= 0.5; a1 += 0.5; }
        if (upright) return (a0, a1, zLo - ctr[2], zHi - ctr[2], zLo, zHi);
        if (!(b1 > b0)) { b0 -= 0.5; b1 += 0.5; }
        return (a0, a1, b0, b1, ctr[2] + b0, ctr[2] + b1);
    }

    /// <summary>One view's RGBA picture (alpha = coverage, dilated like an atlas cell) of the extent (A along Right, B along
    /// Up, measured from <paramref name="center"/>), w x h texels. For the tests and the card bench.</summary>
    internal static RawImage Snapshot(IList<Item> items, View view, double[] center, (double A0, double A1, double B0, double B1) extent, int w, int h) =>
        Raster(items, new List<LayerKey>(), new HashSet<LayerKey>(), view, center, extent.A0, extent.A1, extent.B0, extent.B1,
            center[2] + extent.B0, center[2] + extent.B1, w, h, ExtraFill.White, ExtraFill.ClassFill(0)).img;

    /// <summary>The shared rasteriser: orthographic view of the extent [a0, a1] x [b0, b1] along the view's Right and Up.
    /// RGBA, alpha = coverage; plus one image per extra unit (raw texture at the winning surface point, alpha 255; a
    /// class-map unit: the first covered sample by nearest lookup on the class fill, never averaged). An upright view
    /// (<see cref="IsUpright"/>) draws with the azimuth render's expressions verbatim (s from x and y, z between the
    /// absolute z0 and z1, depth from x and y); any other view projects by dot products and drops samples outside its slab
    /// before the depth test.</summary>
    static (RawImage img, List<(LayerKey unit, RawImage img)> extra) Raster(IList<Item> items, List<LayerKey> units, HashSet<LayerKey> classUnits,
        View view, double[] ctr, double a0, double a1, double b0, double b1, double z0, double z1, int w, int h, ExtraFill fill, ExtraFill classFill, bool full = false)
    {
        // the projection divides by the extent: one without area would give Inf / NaN screen coordinates
        if (!(a1 > a0) || !(b1 > b0) || !(z1 > z0)) throw new ArgumentException($"extent ({a0}, {a1}) x ({b0}, {b1}), z ({z0}, {z1}) has no area");
        const int SS = 2;   // 2 x 2 samples per texel
        int W = w * SS, H = h * SS;
        bool upright = IsUpright(view), slab = view.Slab != null;
        double[] d = view.Dir, r = view.Right, up = view.Up;
        double s0 = a0, s1 = a1;
        double slo = slab ? view.Slab!.Value.Lo : 0, shi = slab ? view.Slab!.Value.Hi : 0;
        var depth = new double[W * H];
        Array.Fill(depth, double.PositiveInfinity);
        var col = new double[W * H * 3];
        // the surface point that won each sample (item, triangle, barycentrics), for the extra units
        int[]? winItem = null, winTri = null; double[]? winL = null;
        if (units.Count > 0) { winItem = new int[W * H]; winTri = new int[W * H]; winL = new double[W * H * 2]; }
        Span<double> px = stackalloc double[4];
        for (int ii = 0; ii < items.Count; ii++)
        {
            var it = items[ii];
            int m = it.T.Length / 3;
            for (int t = 0; t < m; t++)
            {
                int ia = it.T[3 * t], ib = it.T[3 * t + 1], ic = it.T[3 * t + 2];
                double Dot(int v, double[] e) => (it.V[3 * v] - ctr[0]) * e[0] + (it.V[3 * v + 1] - ctr[1]) * e[1] + (it.V[3 * v + 2] - ctr[2]) * e[2];
                double X(int v) => upright ? ((it.V[3 * v] - ctr[0]) * r[0] + (it.V[3 * v + 1] - ctr[1]) * r[1] - s0) / (s1 - s0) * W
                                           : (Dot(v, r) - a0) / (a1 - a0) * W;
                double Y(int v) => upright ? (z1 - it.V[3 * v + 2]) / (z1 - z0) * H : (b1 - Dot(v, up)) / (b1 - b0) * H;
                double Dp(int v) => upright ? (it.V[3 * v] - ctr[0]) * d[0] + (it.V[3 * v + 1] - ctr[1]) * d[1] : Dot(v, d);
                double xa = X(ia), ya = Y(ia), xb = X(ib), yb = Y(ib), xc = X(ic), yc = Y(ic);
                double den = (yb - yc) * (xa - xc) + (xc - xb) * (ya - yc);
                if (Math.Abs(den) < 1e-12) continue;
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(xa, Math.Min(xb, xc)))), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(xa, Math.Max(xb, xc))));
                int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ya, Math.Min(yb, yc)))), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(ya, Math.Max(yb, yc))));
                double da = Dp(ia), db = Dp(ib), dc = Dp(ic);
                if (slab && (Math.Max(da, Math.Max(db, dc)) < slo || Math.Min(da, Math.Min(db, dc)) > shi)) continue;   // wholly outside the slab
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double qx = x + 0.5, qy = y + 0.5;
                        double l0 = ((yb - yc) * (qx - xc) + (xc - xb) * (qy - yc)) / den;
                        double l1 = ((yc - ya) * (qx - xc) + (xa - xc) * (qy - yc)) / den;
                        double l2 = 1 - l0 - l1;
                        if (l0 < -1e-9 || l1 < -1e-9 || l2 < -1e-9) continue;
                        double z = l0 * da + l1 * db + l2 * dc;
                        if (slab && (z < slo || z > shi)) continue;   // outside the slab: before the depth test
                        int pi = y * W + x;
                        if (z >= depth[pi]) continue;
                        double cr = it.Color[0], cg = it.Color[1], cb = it.Color[2], ca = it.Color.Length > 3 ? it.Color[3] : 1;
                        if (it.Image != null && it.UV != null)
                        {
                            double u = l0 * it.UV[2 * ia] + l1 * it.UV[2 * ib] + l2 * it.UV[2 * ic];
                            double v = l0 * it.UV[2 * ia + 1] + l1 * it.UV[2 * ib + 1] + l2 * it.UV[2 * ic + 1];
                            Bake.Bilinear(it.Image, u, v, px);
                            cr *= px[0]; cg *= px[1]; cb *= px[2]; ca *= px[3];
                        }
                        if (it.VertexColors != null)
                        {
                            var vc = it.VertexColors;
                            cr *= l0 * vc[4 * ia] + l1 * vc[4 * ib] + l2 * vc[4 * ic];
                            cg *= l0 * vc[4 * ia + 1] + l1 * vc[4 * ib + 1] + l2 * vc[4 * ic + 1];
                            cb *= l0 * vc[4 * ia + 2] + l1 * vc[4 * ib + 2] + l2 * vc[4 * ic + 2];
                        }
                        if (ca < 0.5) continue;   // cut-out (leaf texture): what is behind shows through
                        depth[pi] = z;
                        col[3 * pi] = cr; col[3 * pi + 1] = cg; col[3 * pi + 2] = cb;
                        if (winItem != null) { winItem[pi] = ii; winTri![pi] = t; winL![2 * pi] = l0; winL[2 * pi + 1] = l1; }
                    }
            }
        }
        // down-sample: colour = mean of the covered samples, alpha = coverage
        var img = new RawImage(w, h, 4);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double sr = 0, sg = 0, sb = 0; int cov = 0;
                for (int j = 0; j < SS; j++)
                    for (int i = 0; i < SS; i++)
                    {
                        int pi = (y * SS + j) * W + x * SS + i;
                        if (double.IsPositiveInfinity(depth[pi])) continue;
                        sr += col[3 * pi]; sg += col[3 * pi + 1]; sb += col[3 * pi + 2]; cov++;
                    }
                int o = (y * w + x) * 4;
                if (cov == 0) continue;
                img.Pixels[o] = (byte)Math.Clamp(Math.Round(sr / cov * 255), 0, 255);
                img.Pixels[o + 1] = (byte)Math.Clamp(Math.Round(sg / cov * 255), 0, 255);
                img.Pixels[o + 2] = (byte)Math.Clamp(Math.Round(sb / cov * 255), 0, 255);
                img.Pixels[o + 3] = (byte)(cov * 255 / (SS * SS));
            }
        Dilate(img, 8);
        if (full) FloodPad(img, new CellRect(0, 0, w, h));
        var extra = new List<(LayerKey, RawImage)>();
        foreach (var u in units)
        {
            if (classUnits.Contains(u)) { extra.Add((u, ClassView(items, u, depth, winItem!, winTri!, winL!, w, h, classFill, full))); continue; }
            // coverage alpha first, so the dilation grows exactly like the base image; then opaque
            var ex = new RawImage(w, h, 4);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double sr = 0, sg = 0, sb = 0; int cov = 0;
                    for (int j = 0; j < SS; j++)
                        for (int i = 0; i < SS; i++)
                        {
                            int pi = (y * SS + j) * W + x * SS + i;
                            if (double.IsPositiveInfinity(depth[pi])) continue;
                            cov++;
                            var it = items[winItem![pi]];
                            if (!it.Extra.TryGetValue(u, out var layer))   // part without the layer: the fill
                            {
                                fill.Apply(px, col[3 * pi], col[3 * pi + 1], col[3 * pi + 2]);
                                sr += px[0]; sg += px[1]; sb += px[2];
                                continue;
                            }
                            int t = winTri![pi], ia = it.T[3 * t], ib = it.T[3 * t + 1], ic = it.T[3 * t + 2];
                            double l0 = winL![2 * pi], l1 = winL[2 * pi + 1], l2 = 1 - l0 - l1;
                            var uv = layer.UV;
                            Bake.Bilinear(layer.Image, l0 * uv[2 * ia] + l1 * uv[2 * ib] + l2 * uv[2 * ic], l0 * uv[2 * ia + 1] + l1 * uv[2 * ib + 1] + l2 * uv[2 * ic + 1], px);
                            sr += px[0]; sg += px[1]; sb += px[2];
                        }
                    if (cov == 0) continue;
                    int o = (y * w + x) * 4;
                    ex.Pixels[o] = (byte)Math.Clamp(Math.Round(sr / cov * 255), 0, 255);
                    ex.Pixels[o + 1] = (byte)Math.Clamp(Math.Round(sg / cov * 255), 0, 255);
                    ex.Pixels[o + 2] = (byte)Math.Clamp(Math.Round(sb / cov * 255), 0, 255);
                    ex.Pixels[o + 3] = 255;
                }
            Dilate(ex, 8);
            if (full) FloodPad(ex, new CellRect(0, 0, w, h));
            for (int i = 0; i < w * h; i++) ex.Pixels[4 * i + 3] = 255;
            extra.Add((u, ex));
        }
        return (img, extra);
    }

    /// <summary>A class-map unit's image for a view: starts from the class fill, each covered texel takes its first
    /// covered sample (nearest lookup, or the class fill for a part without the map), dilation copies values verbatim;
    /// so it holds only class IDs of the maps and the fill.</summary>
    static RawImage ClassView(IList<Item> items, LayerKey u, double[] depth, int[] winItem, int[] winTri, double[] winL, int w, int h, ExtraFill classFill, bool full = false)
    {
        const int SS = 2;
        int W = w * SS;
        Span<double> px = stackalloc double[4];
        classFill.Apply(px, 0, 0, 0);
        byte f0 = (byte)Math.Round(px[0] * 255), f1 = (byte)Math.Round(px[1] * 255), f2 = (byte)Math.Round(px[2] * 255);
        var ex = new RawImage(w, h, 4);
        for (int i = 0; i < w * h; i++) { ex.Pixels[4 * i] = f0; ex.Pixels[4 * i + 1] = f1; ex.Pixels[4 * i + 2] = f2; }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int pi = -1;
                for (int j = 0; j < SS && pi < 0; j++)
                    for (int i = 0; i < SS && pi < 0; i++)
                    {
                        int q = (y * SS + j) * W + x * SS + i;
                        if (!double.IsPositiveInfinity(depth[q])) pi = q;
                    }
                if (pi < 0) continue;
                int o = (y * w + x) * 4;
                ex.Pixels[o + 3] = 255;
                var it = items[winItem[pi]];
                if (!it.Extra.TryGetValue(u, out var layer)) continue;   // part without the map: the class fill
                int t = winTri[pi], ia = it.T[3 * t], ib = it.T[3 * t + 1], ic = it.T[3 * t + 2];
                double l0 = winL[2 * pi], l1 = winL[2 * pi + 1], l2 = 1 - l0 - l1;
                var uv = layer.UV;
                Bake.Nearest(layer.Image, l0 * uv[2 * ia] + l1 * uv[2 * ib] + l2 * uv[2 * ic], l0 * uv[2 * ia + 1] + l1 * uv[2 * ib + 1] + l2 * uv[2 * ic + 1], px);
                for (int k = 0; k < 3; k++) ex.Pixels[o + k] = (byte)Math.Round(px[k] * 255);
            }
        Dilate(ex, 8);
        if (full) FloodPad(ex, new CellRect(0, 0, w, h));
        for (int i = 0; i < w * h; i++) ex.Pixels[4 * i + 3] = 255;
        return ex;
    }

    /// <summary>Transparent texels take the colour of a covered neighbour (alpha stays 0), so mip-maps and filtering do
    /// not darken the edges.</summary>
    static void Dilate(RawImage img, int steps)
    {
        int w = img.Width, h = img.Height;
        var p = img.Pixels;
        var has = new bool[w * h];
        for (int i = 0; i < w * h; i++) has[i] = p[4 * i + 3] > 0;
        for (int s = 0; s < steps; s++)
        {
            var add = new List<(int i, int from)>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (has[i]) continue;
                    int from = x > 0 && has[i - 1] ? i - 1 : x < w - 1 && has[i + 1] ? i + 1 : y > 0 && has[i - w] ? i - w : y < h - 1 && has[i + w] ? i + w : -1;
                    if (from >= 0) add.Add((i, from));
                }
            if (add.Count == 0) break;
            foreach (var (i, from) in add) { p[4 * i] = p[4 * from]; p[4 * i + 1] = p[4 * from + 1]; p[4 * i + 2] = p[4 * from + 2]; has[i] = true; }
        }
    }
}
