using System.Buffers.Binary;

namespace SceneKit;

/// <summary>The semantic codes of an OpenFlight face that sensor (infrared) image generators and terrain tools read:
/// IR colour code, IR material code, surface material code (SMC) and feature ID.</summary>
public readonly record struct FaceCodes(int IrColor, int IrMaterial, int Smc, int Feature)
{
    public override string ToString() => $"IR colour {IrColor}, IR material {IrMaterial}, SMC {Smc}, feature ID {Feature}";
}

/// <summary>Face codes of OpenFlight records and the face template of geometry the optimiser builds.
/// <para>Face record (opcode 5), OpenFlight 15.x / 16.x, offsets from the start of the record (header included):
/// 12 int32 IR colour code, 16 int16 relative priority, 18 draw type, 25 template (billboard), 28 int16 texture pattern,
/// 30 int16 material, 32 int16 surface material code, 34 int16 feature ID, 36 int32 IR material code, 40 uint16
/// transparency, 44 uint32 flags, 48 uint8 light mode (0 face colour / 1 vertex colour, not illuminated; 2 face colour /
/// 3 vertex colour, lit with vertex normals), 56 packed colour, 68 colour index. A mesh record (opcode 84) is a face record with
/// 4 more (reserved) bytes at offset 12, so its fields from 12 on are 4 bytes later.</para>
/// <para>Cards, billboards, proxies and LOD levels are new geometry with no face record of their own: they carry the
/// source face whose codes cover the largest triangle area (<see cref="Carry"/>, kept in Node.Extra under
/// <see cref="Key"/>), and the OpenFlight writer copies its codes into the new faces (texture, material, colour, draw
/// type and template are still set for the new geometry).</para></summary>
public static class FltFaceCodes
{
    public const int IrColorAt = 12, SmcAt = 32, FeatureAt = 34, IrMaterialAt = 36;
    public const int LightModeAt = 48;
    /// <summary>Node.Extra key: the face record (face layout, at least 40 bytes) new faces take their codes from.</summary>
    public const string Key = "flt_face";
    public const string DifferNote = "IR / material codes differ between parts: used the most common (by area)";

    /// <summary>Codes of a face (shift 0) or mesh (shift 4) record; null when the record is too short.</summary>
    public static FaceCodes? Read(byte[] d, int shift = 0)
    {
        if (d.Length < IrMaterialAt + shift + 4) return null;
        return new FaceCodes(BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(IrColorAt + shift)), BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(IrMaterialAt + shift)),
            BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(SmcAt + shift)), BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(FeatureAt + shift)));
    }

    /// <summary>Light mode of a face-layout record (0-3, see the class comment); null when the record is too short.</summary>
    public static int? LightMode(byte[] faceLayout) => faceLayout.Length > LightModeAt ? faceLayout[LightModeAt] : null;

    public static FaceCodes? Of(FltFile.Rec r) => r.Op == FltFile.MESH ? Read(r.Data, 4) : r.Op == FltFile.FACE ? Read(r.Data) : null;

    /// <summary>Put the codes into a face record (face layout).</summary>
    public static void Write(byte[] face, FaceCodes c)
    {
        if (face.Length < IrMaterialAt + 4) return;
        BinaryPrimitives.WriteInt32BigEndian(face.AsSpan(IrColorAt), c.IrColor);
        BinaryPrimitives.WriteInt16BigEndian(face.AsSpan(SmcAt), (short)c.Smc);
        BinaryPrimitives.WriteInt16BigEndian(face.AsSpan(FeatureAt), (short)c.Feature);
        BinaryPrimitives.WriteInt32BigEndian(face.AsSpan(IrMaterialAt), c.IrMaterial);
    }

    /// <summary>A face-layout copy of a face or mesh record (the mesh's 4 extra bytes at 12 removed), opcode 5.</summary>
    public static byte[] FaceLayout(FltFile.Rec r)
    {
        byte[] d;
        if (r.Op == FltFile.MESH && r.Data.Length >= 16)
        {
            d = new byte[r.Data.Length - 4];
            Buffer.BlockCopy(r.Data, 0, d, 0, 12);
            Buffer.BlockCopy(r.Data, 16, d, 12, r.Data.Length - 16);
        }
        else d = (byte[])r.Data.Clone();
        if (d.Length >= 2) BinaryPrimitives.WriteUInt16BigEndian(d, FltFile.FACE);
        return d;
    }

    /// <summary>The template a node carries (made by a pass), or null.</summary>
    public static byte[]? Carried(Node n) => n.Extra != null && n.Extra.TryGetValue(Key, out var o) && o is byte[] b ? b : null;

    /// <summary>The OpenFlight file a face record was read from (the scene's or a loaded external file's).</summary>
    static FltFile? FileOf(Scene? scene, FltFile.Rec r)
    {
        if (scene == null) return null;
        if (scene.Flt != null && scene.Flt.Represented.Contains(r)) return scene.Flt;
        foreach (var sub in scene.Externals.Values)
            if (sub?.Flt != null && sub.Flt.Represented.Contains(r)) return sub.Flt;
        return null;
    }

    /// <summary>Triangle area of a geometry (positions as they are).</summary>
    public static double Area(GeomData? g)
    {
        var v = g?.Vertices;
        if (v == null || v.Comps < 2) return 0;
        var T = g!.Triangles();
        double s = 0;
        for (int t = 0; t + 2 < T.Length; t += 3) s += Tri(v, T[t], T[t + 1], T[t + 2]);
        return s;
    }

    static double Tri(VArray v, int a, int b, int c)
    {
        if (a >= v.Count || b >= v.Count || c >= v.Count) return 0;
        double P(int i, int k) => k < v.Comps ? v.Data[i * v.Comps + k] : 0;
        double ux = P(b, 0) - P(a, 0), uy = P(b, 1) - P(a, 1), uz = P(b, 2) - P(a, 2);
        double wx = P(c, 0) - P(a, 0), wy = P(c, 1) - P(a, 1), wz = P(c, 2) - P(a, 2);
        double x = uy * wz - uz * wy, y = uz * wx - ux * wz, z = ux * wy - uy * wx;
        return Math.Sqrt(x * x + y * y + z * z) / 2;
    }

    /// <summary>Area of a face record's polygon(s) from its file's vertex palette (1 when it cannot be measured).</summary>
    static double FaceArea(FltFile? flt, FltFile.Rec f)
    {
        if (flt == null || f.VLists.Count == 0) return 1;
        double s = 0;
        foreach (var vl in f.VLists)
        {
            var idx = flt.PaletteIndices(vl);
            if (idx == null || idx.Length < 3) continue;
            var P = flt.Positions;
            for (int i = 1; i + 1 < idx.Length; i++)
            {
                int a = idx[0], b = idx[i], c = idx[i + 1];
                double ux = P[3 * b] - P[3 * a], uy = P[3 * b + 1] - P[3 * a + 1], uz = P[3 * b + 2] - P[3 * a + 2];
                double wx = P[3 * c] - P[3 * a], wy = P[3 * c + 1] - P[3 * a + 1], wz = P[3 * c + 2] - P[3 * a + 2];
                double x = uy * wz - uz * wy, y = uz * wx - ux * wz, z = ux * wy - uy * wx;
                s += Math.Sqrt(x * x + y * y + z * z) / 2;
            }
        }
        return s > 0 ? s : 1e-12;
    }

    /// <summary>The faces behind a geometry node as (codes, area, face-layout record): its own face records (area from the
    /// file's vertex palette), its mesh record, or the template a pass gave it (area of the geometry). Empty for geometry
    /// that did not come from OpenFlight.</summary>
    public static List<(FaceCodes codes, double area, Func<byte[]> face)> Faces(Node g, Scene? scene)
    {
        var o = new List<(FaceCodes, double, Func<byte[]>)>();
        if (g.Src is List<FltFile.Rec> l)
        {
            FltFile? flt = null;
            foreach (var f in l)
            {
                if (Of(f) is not { } c) continue;
                flt ??= FileOf(scene, f);
                var rec = f;
                o.Add((c, FaceArea(flt, f), () => FaceLayout(rec)));
            }
        }
        else if (g.Src is FltFile.Rec { Op: FltFile.MESH } m && Of(m) is { } mc) o.Add((mc, Math.Max(Area(g.Geom), 1e-12), () => FaceLayout(m)));
        else if (Carried(g) is { } t && Read(t) is { } tc) o.Add((tc, Math.Max(Area(g.Geom), 1e-12), () => (byte[])t.Clone()));
        return o;
    }

    /// <summary>The source face whose codes cover the largest area: (face-layout record, codes, the sources' codes differ).
    /// <paramref name="sources"/>: geometry nodes with a factor for their area (how their placement scales it). Null when
    /// no source came from OpenFlight.</summary>
    public static (byte[] face, FaceCodes codes, bool differ)? Dominant(IEnumerable<(Node node, double scale)> sources, Scene? scene)
    {
        var sum = new Dictionary<FaceCodes, double>();
        var best = new Dictionary<FaceCodes, (double area, Func<byte[]> face)>();
        var order = new List<FaceCodes>();
        foreach (var (n, scale) in sources)
            foreach (var (c, a0, face) in Faces(n, scene))
            {
                double a = a0 * (scale > 0 && double.IsFinite(scale) ? scale : 1);
                if (!sum.ContainsKey(c)) { sum[c] = 0; order.Add(c); best[c] = (a, face); }
                sum[c] += a;
                if (a > best[c].area) best[c] = (a, face);
            }
        if (order.Count == 0) return null;
        var top = order.OrderByDescending(c => sum[c]).First();   // stable: ties keep the first seen
        return (best[top].face(), top, order.Count > 1);
    }

    /// <summary>Give a new geometry node the dominant source face (see <see cref="Dominant"/>); notes get
    /// <see cref="DifferNote"/> when the sources' codes differ. False when no source came from OpenFlight.</summary>
    public static bool Carry(Node newGeo, IEnumerable<(Node node, double scale)> sources, Scene? scene, List<string>? notes)
    {
        var pick = Dominant(sources, scene);
        if (pick == null) return false;
        (newGeo.Extra ??= new())[Key] = pick.Value.face;
        if (pick.Value.differ) notes?.Add(DifferNote);
        return true;
    }

    /// <summary>Lines for the inspector: the codes of a geometry's faces with how many faces have them (most first), or
    /// the template a pass gave it. Empty for geometry without OpenFlight faces.</summary>
    public static List<string> Describe(Node g)
    {
        var o = new List<string>();
        if (g.Src is List<FltFile.Rec> l)
        {
            var counts = new Dictionary<FaceCodes, int>();
            var order = new List<FaceCodes>();
            foreach (var f in l)
                if (Of(f) is { } c) { if (!counts.ContainsKey(c)) { counts[c] = 0; order.Add(c); } counts[c]++; }
            foreach (var c in order.OrderByDescending(c => counts[c]).Take(8)) o.Add($"{c}   ({counts[c]} face{(counts[c] == 1 ? "" : "s")})");
            if (order.Count > 8) o.Add($"... {order.Count - 8} more combinations");
        }
        else if (g.Src is FltFile.Rec { Op: FltFile.MESH } m && Of(m) is { } mc) o.Add($"{mc}   (mesh)");
        else if (Carried(g) is { } t && Read(t) is { } tc) o.Add($"{tc}   (taken from the source face covering the largest area)");
        return o;
    }
}
