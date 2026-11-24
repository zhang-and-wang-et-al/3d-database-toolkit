namespace SceneKit;

/// <summary>A layer baked next to the base image with the same layout: a texture unit above 0 (Companion null), or the
/// companion image (by its pattern, see <see cref="Companions"/>) of the texture on unit <see cref="Unit"/> (0 too).</summary>
public readonly record struct LayerKey(int Unit, string? Companion = null)
{
    public static implicit operator LayerKey(int unit) => new(unit);
    /// <summary>Units first (ascending), then companions by unit and pattern.</summary>
    public static IOrderedEnumerable<T> Sort<T>(IEnumerable<T> items, Func<T, LayerKey> key) =>
        items.OrderBy(x => key(x).Companion != null).ThenBy(x => key(x).Unit).ThenBy(x => key(x).Companion, StringComparer.Ordinal);
    public override string ToString() => Companion == null ? $"unit {Unit}" : $"{Companions.Label(Companion)} companion of unit {Unit}";
}

/// <summary>What a baked layer (texture unit above 0, companion image) shows where a part has no such layer: white
/// (neutral for modulating maps; in infrared often "hot"), black, the luminance of the baked base colour at that texel
/// (Grey null), or a grey value 0-255. Parameter 'extra_fill' of the baking passes.</summary>
public sealed record ExtraFill(double? Grey)
{
    public static readonly ExtraFill White = new(1.0), Black = new(0.0), Luminance = new((double?)null);

    /// <summary>"white", "black", "luminance" or a number 0-255.</summary>
    public static ExtraFill Parse(string? text)
    {
        string s = (text ?? "").Trim().ToLowerInvariant();
        switch (s)
        {
            case "" or "white": return White;
            case "black": return Black;
            case "luminance" or "lum" or "grey" or "gray": return Luminance;
        }
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 255)
            return new ExtraFill(Math.Round(v) / 255.0);
        throw new ArgumentException($"extra_fill '{text}': use white, black, luminance or a number 0-255");
    }

    /// <summary>Set for a class layer's fill (parameter 'class_fill'): R = the class ID, G = B = 0.</summary>
    public int? ClassId { get; init; }

    /// <summary>The fill of a class layer (class map): R = id, G = 0, B = 0, A = 255.</summary>
    public static ExtraFill ClassFill(int id) =>
        id is >= 0 and <= 255 ? new ExtraFill(id / 255.0) { ClassId = id } : throw new ArgumentException("class_fill: a class ID 0-255");

    /// <summary>The fill colour (RGB 0..1, alpha 1) where the base colour is r g b.</summary>
    public void Apply(Span<double> o, double r, double g, double b)
    {
        o[3] = 1;
        if (ClassId != null) { o[0] = Grey!.Value; o[1] = o[2] = 0; return; }
        double v = Grey ?? Math.Clamp(0.299 * r + 0.587 * g + 0.114 * b, 0, 1);
        o[0] = o[1] = o[2] = v;
    }

    public override string ToString() => ClassId is int id ? $"class_fill {id}" : Grey switch
    {
        null => "the luminance of the base colour",
        1.0 => "white",
        0.0 => "black",
        double g => $"grey {Math.Round(g * 255)}",
    };
}

/// <summary>Bakes the colour of an original model onto a new mesh with its own UV atlas. For every texel (2x2 samples)
/// the matching point on the original surface is the closest one within MaxDist, preferring faces that point the same
/// way (so thin walls and the back side of panels do not bleed through). Colour = original texture (bilinear, repeat)
/// x material colour x vertex colour. Empty texels next to charts are filled by dilation so mip-maps do not show seams.
/// Texture units above 0 and companion images (Layers) are baked from the same surface point into images with the same
/// layout: raw texture (no material or vertex colour), alpha 1; a source without the layer gets the layer's fill or
/// <see cref="Input.Fill"/>. Class-map layers (<see cref="Layer.Nearest"/>) are never blended.</summary>
public static class Bake
{
    /// <summary>One layer (texture unit above 0, companion image): UVs per original vertex and the image per source (null:
    /// the fill). A Nearest layer (class map) is sampled by nearest lookup, takes one sample per texel instead of
    /// averaging (the first one of the lowest-numbered triangle reaching it, so the result does not depend on thread
    /// order) and dilates by copying a neighbour verbatim, so it only holds values of its images or its fill.</summary>
    public sealed class Layer
    {
        public double[] OUV = Array.Empty<double>();
        public RawImage?[] Images = Array.Empty<RawImage?>();
        public bool Nearest;
        /// <summary>The fill of this layer (null: <see cref="Input.Fill"/>).</summary>
        public ExtraFill? Fill;
    }

    public sealed class Input
    {
        public double[] V = Array.Empty<double>();
        public int[] T = Array.Empty<int>();
        public double[] UV = Array.Empty<double>();
        public int Size = 512;
        public TriIndex Original = null!;
        public double[] OV = Array.Empty<double>();
        public int[] OT = Array.Empty<int>();
        public int[] TriSource = Array.Empty<int>();
        public double[] OUV = Array.Empty<double>();
        public double[] OColor = Array.Empty<double>();
        public bool[] HasUV = Array.Empty<bool>();
        public RawImage?[] Images = Array.Empty<RawImage?>();
        public double MaxDist = 1;
        public int Dilate = 16;
        public Dictionary<LayerKey, Layer> Layers = new();
        /// <summary>What a layer shows where the source has no image for it.</summary>
        public ExtraFill Fill = ExtraFill.White;
        /// <summary>The part of each triangle of <see cref="T"/> and of each original triangle (<see cref="OT"/>); with both
        /// set, a texel looks up the original of its own triangle's part only (no colour bleeding between parts).</summary>
        public int[]? PartOfProxyTri;
        public int[]? PartOfOriginalTri;
        /// <summary>The search distance per part (indexed by <see cref="PartOfProxyTri"/>); null: <see cref="MaxDist"/>.</summary>
        public double[]? MaxDistOfPart;
    }

    public static RawImage Run(Input inp) => RunAll(inp).img;

    /// <summary>The base image and one image per layer.</summary>
    public static (RawImage img, Dictionary<LayerKey, RawImage> extra) RunAll(Input inp)
    {
        int S = inp.Size;
        var layers = LayerKey.Sort(inp.Layers, kv => kv.Key).ToList();
        int nl = 1 + layers.Count, C = 4 * nl;   // channels per texel: base RGBA, then RGBA per layer
        var near = new bool[nl];                 // nearest layers (class maps): one sample, no averaging
        for (int l = 0; l < layers.Count; l++) near[l + 1] = layers[l].Value.Nearest;
        var sum = new double[S * S * C];
        var cnt = new int[S * S];
        var owner = new int[S * S];              // the triangle whose sample a nearest layer holds: the lowest one wins
        Array.Fill(owner, int.MaxValue);
        int m = inp.T.Length / 3;
        Parallel.For(0, m, () => new double[C], (t, _, col) =>
        {
            int a = inp.T[3 * t], b = inp.T[3 * t + 1], c = inp.T[3 * t + 2];
            double ua = inp.UV[2 * a] * S, va = (1 - inp.UV[2 * a + 1]) * S;
            double ub = inp.UV[2 * b] * S, vb = (1 - inp.UV[2 * b + 1]) * S;
            double uc = inp.UV[2 * c] * S, vc = (1 - inp.UV[2 * c + 1]) * S;
            double den = (vb - vc) * (ua - uc) + (uc - ub) * (va - vc);
            if (Math.Abs(den) < 1e-12) return col;
            // face normal
            double ex = inp.V[3 * b] - inp.V[3 * a], ey = inp.V[3 * b + 1] - inp.V[3 * a + 1], ez = inp.V[3 * b + 2] - inp.V[3 * a + 2];
            double fx = inp.V[3 * c] - inp.V[3 * a], fy = inp.V[3 * c + 1] - inp.V[3 * a + 1], fz = inp.V[3 * c + 2] - inp.V[3 * a + 2];
            double nx = ey * fz - ez * fy, ny = ez * fx - ex * fz, nz = ex * fy - ey * fx;
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl > 0) { nx /= nl; ny /= nl; nz /= nl; }
            // the part filter: this triangle's part and its search distance
            int part = inp.PartOfProxyTri?[t] ?? -1;
            int[]? partOf = inp.PartOfProxyTri != null ? inp.PartOfOriginalTri : null;
            double maxDist = inp.MaxDistOfPart != null && part >= 0 ? inp.MaxDistOfPart[part] : inp.MaxDist;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ua, Math.Min(ub, uc)))), x1 = Math.Min(S - 1, (int)Math.Floor(Math.Max(ua, Math.Max(ub, uc))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(va, Math.Min(vb, vc)))), y1 = Math.Min(S - 1, (int)Math.Floor(Math.Max(va, Math.Max(vb, vc))));
            Span<double> sp = stackalloc double[] { 0.25, 0.75 };
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    for (int sy = 0; sy < 2; sy++)
                        for (int sx = 0; sx < 2; sx++)
                        {
                            double px = x + sp[sx], py = y + sp[sy];
                            double l0 = ((vb - vc) * (px - uc) + (uc - ub) * (py - vc)) / den;
                            double l1 = ((vc - va) * (px - uc) + (ua - uc) * (py - vc)) / den;
                            double l2 = 1 - l0 - l1;
                            const double e = -1e-6;
                            if (l0 < e || l1 < e || l2 < e) continue;
                            double qx = l0 * inp.V[3 * a] + l1 * inp.V[3 * b] + l2 * inp.V[3 * c];
                            double qy = l0 * inp.V[3 * a + 1] + l1 * inp.V[3 * b + 1] + l2 * inp.V[3 * c + 1];
                            double qz = l0 * inp.V[3 * a + 2] + l1 * inp.V[3 * b + 2] + l2 * inp.V[3 * c + 2];
                            if (!Sample(inp, layers, qx, qy, qz, nx, ny, nz, maxDist, partOf, part, col)) continue;
                            int pi = y * S + x;
                            lock (cnt)
                            {
                                bool take = t < owner[pi];
                                if (take) owner[pi] = t;
                                for (int k = 0; k < C; k++)
                                    if (!near[k >> 2]) sum[C * pi + k] += col[k];
                                    else if (take) sum[C * pi + k] = col[k];
                                cnt[pi]++;
                            }
                        }
            return col;
        }, _ => { });

        var filled = new bool[S * S];
        var rgba = new double[S * S * C];
        for (int i = 0; i < S * S; i++)
        {
            if (cnt[i] == 0) continue;
            filled[i] = true;
            for (int k = 0; k < C; k++) rgba[C * i + k] = near[k >> 2] ? sum[C * i + k] : sum[C * i + k] / cnt[i];
        }
        // dilation: grow chart colours into the gutter (nearest layers: the first filled neighbour's values, verbatim)
        for (int pass = 0; pass < inp.Dilate; pass++)
        {
            var add = new List<(int i, double[] v)>();
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int i = y * S + x;
                    if (filled[i]) continue;
                    double[]? v = null; int n = 0, first = -1;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= S || yy >= S) continue;
                            int j = yy * S + xx;
                            if (!filled[j]) continue;
                            v ??= new double[C];
                            for (int k = 0; k < C; k++) v[k] += rgba[C * j + k];
                            if (n++ == 0) first = j;
                        }
                    if (n == 0) continue;
                    for (int k = 0; k < C; k++) v![k] /= n;
                    for (int l = 1; l < nl; l++)
                        if (near[l]) Array.Copy(rgba, C * first + 4 * l, v!, 4 * l, 4);
                    add.Add((i, v!));
                }
            if (add.Count == 0) break;
            foreach (var (i, v) in add) { filled[i] = true; Array.Copy(v, 0, rgba, C * i, C); }
        }
        // what a texel no chart reached shows: grey; a nearest layer its fill
        var empty = new double[C];
        for (int k = 0; k < C; k++) empty[k] = k % 4 == 3 ? 1 : 0.5;
        for (int l = 0; l < layers.Count; l++)
            if (near[l + 1]) (layers[l].Value.Fill ?? inp.Fill).Apply(empty.AsSpan(4 * (l + 1), 4), 0.5, 0.5, 0.5);
        var imgs = new RawImage[nl];
        for (int l = 0; l < nl; l++)
        {
            var img = imgs[l] = new RawImage(S, S, 4);
            for (int i = 0; i < S * S; i++)
                for (int k = 0; k < 4; k++)
                    img.Pixels[4 * i + k] = (byte)Math.Clamp(Math.Round((k == 3 && l > 0 ? 1 : filled[i] ? rgba[C * i + 4 * l + k] : empty[4 * l + k]) * 255), 0, 255);
        }
        var extra = new Dictionary<LayerKey, RawImage>();
        for (int l = 0; l < layers.Count; l++) extra[layers[l].Key] = imgs[l + 1];
        return (imgs[0], extra);
    }

    static bool Sample(Input inp, List<KeyValuePair<LayerKey, Layer>> layers, double x, double y, double z, double nx, double ny, double nz,
        double maxDist, int[]? partOf, int part, double[] col)
    {
        var h = inp.Original.Closest(x, y, z, maxDist, nx, ny, nz, 0.35, partOf, part);   // ignore faces more than ~70 degrees off
        if (h == null) return false;
        var hit = h.Value;
        int a = inp.OT[3 * hit.Tri], b = inp.OT[3 * hit.Tri + 1], c = inp.OT[3 * hit.Tri + 2];
        for (int k = 0; k < 4; k++) col[k] = hit.U * inp.OColor[4 * a + k] + hit.V * inp.OColor[4 * b + k] + hit.W * inp.OColor[4 * c + k];
        int s = inp.TriSource[hit.Tri];
        var im = inp.Images[s];
        if (im != null && inp.HasUV[s])
        {
            double u = hit.U * inp.OUV[2 * a] + hit.V * inp.OUV[2 * b] + hit.W * inp.OUV[2 * c];
            double v = hit.U * inp.OUV[2 * a + 1] + hit.V * inp.OUV[2 * b + 1] + hit.W * inp.OUV[2 * c + 1];
            Span<double> tex = stackalloc double[4];
            Bilinear(im, u, v, tex);
            for (int k = 0; k < 4; k++) col[k] *= tex[k];
        }
        for (int l = 0; l < layers.Count; l++)
        {
            var layer = layers[l].Value;
            var li = layer.Images[s];
            Span<double> o = col.AsSpan(4 * (l + 1), 4);
            if (li == null) { (layer.Fill ?? inp.Fill).Apply(o, col[0], col[1], col[2]); continue; }
            double u = hit.U * layer.OUV[2 * a] + hit.V * layer.OUV[2 * b] + hit.W * layer.OUV[2 * c];
            double v = hit.U * layer.OUV[2 * a + 1] + hit.V * layer.OUV[2 * b + 1] + hit.W * layer.OUV[2 * c + 1];
            if (layer.Nearest) Nearest(li, u, v, o);
            else Bilinear(li, u, v, o);
            o[3] = 1;
        }
        return true;
    }

    /// <summary>Bilinear sample with repeat wrapping; v = 0 is the bottom row (OpenGL convention).</summary>
    public static void Bilinear(RawImage im, double u, double v, Span<double> o)
    {
        double fx = (u - Math.Floor(u)) * im.Width - 0.5, fy = (1 - (v - Math.Floor(v))) * im.Height - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        Span<double> p = stackalloc double[4];
        for (int k = 0; k < 4; k++) o[k] = 0;
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int xx = ((x0 + i) % im.Width + im.Width) % im.Width, yy = ((y0 + j) % im.Height + im.Height) % im.Height;
                Pixel(im, xx, yy, p);
                double w = (i == 0 ? 1 - tx : tx) * (j == 0 ? 1 - ty : ty);
                for (int k = 0; k < 4; k++) o[k] += w * p[k];
            }
    }

    /// <summary>The nearest texel with repeat wrapping, same v convention as <see cref="Bilinear"/>: for class maps, whose
    /// values are IDs and must never be blended.</summary>
    public static void Nearest(RawImage im, double u, double v, Span<double> o)
    {
        int x = (int)Math.Floor((u - Math.Floor(u)) * im.Width), y = (int)Math.Floor((1 - (v - Math.Floor(v))) * im.Height);
        Pixel(im, (x % im.Width + im.Width) % im.Width, (y % im.Height + im.Height) % im.Height, o);
    }

    static void Pixel(RawImage im, int x, int y, Span<double> o)
    {
        int c = im.Channels, i = (y * im.Width + x) * c;
        var px = im.Pixels;
        switch (c)
        {
            case 1: o[0] = o[1] = o[2] = px[i] / 255.0; o[3] = 1; break;
            case 2: o[0] = o[1] = o[2] = px[i] / 255.0; o[3] = px[i + 1] / 255.0; break;
            case 3: o[0] = px[i] / 255.0; o[1] = px[i + 1] / 255.0; o[2] = px[i + 2] / 255.0; o[3] = 1; break;
            default: o[0] = px[i] / 255.0; o[1] = px[i + 1] / 255.0; o[2] = px[i + 2] / 255.0; o[3] = px[i + 3] / 255.0; break;
        }
    }
}
