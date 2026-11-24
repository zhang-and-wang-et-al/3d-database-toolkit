using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SceneKit;
using Xunit;

/// <summary>Golden guard of the car package: with default options, replace_proxy, the proxy far level of generate_lod
/// and remove_occluded give exactly what they gave before the per-part work started. The expected fingerprints were
/// recorded on that code (each fixture run twice to prove they are deterministic); no later task edits them. Geometry,
/// class maps and the tree are compared exactly; the base colour images (whose averages Bake adds up in thread order) are
/// compared with a tolerance against the raw bytes in data/golden.</summary>
public class ProxyGoldenTests
{
    internal static readonly Dictionary<string, string> Expected = new()
    {
        ["replace_proxy remove_occluded_car"] = "20 14c9763cecd78852 5b46804c2dc7b7d7 6ca2b322fcbbfcdd",
        ["replace_proxy synth_split"] = "1066 96bad275f32ccc50 4652b8731f110f19 6ca2b322fcbbfcdd",
        ["replace_proxy synth_merged"] = "1066 bf3726f74dcb1e84 3964ce841ea25130 6ca2b322fcbbfcdd",
        ["generate_lod synth_split"] = "26837 205176a6f32a4d90 fd4fc14f59ecb5b4 c85583736ad6b070",
        ["generate_lod remove_occluded_car"] = "24 452668aa172c5883 e3b0c44298fc1c14 80b55b2f14039dff",
        ["remove_occluded synth_split"] = "19818 2fb97ed6ce79b569",
        // final fix wave (ruling F-5): the split car's generate_lod row at proxy_voxels 24 (a third of the time), recorded on
        // 2026-10-22 on the car-package code whose 48-voxel output still matched the row above (run twice, identical)
        ["generate_lod synth_split vox24"] = "26949 c4e8e8a55e31923d a2ba49d2e011185d 4a0020c54afb102c",
    };

    internal static (Scene sc, Node target) Fixture(string name) => name switch
    {
        "remove_occluded_car" => RemoveOccludedTests.Car() is var c ? (c.sc, c.car) : default,
        "synth_split" => Meshes.SynthCar(true),
        "synth_merged" => Meshes.SynthCar(false),
        _ => throw new ArgumentException(name),
    };

    static void Run(Scene sc, Node target, string pass, Dictionary<string, object> prms)
    {
        var (results, _, _) = Optimizer.Run(sc, new[] { pass }, prms, targets: new List<Node> { target });
        Assert.Null(results[0].Error);
    }

    [Theory]
    [InlineData("remove_occluded_car")]
    [InlineData("synth_split")]
    [InlineData("synth_merged")]
    public void ReplaceProxyDefaultsReproduceToday(string fixture)
    {
        var (sc, target) = Fixture(fixture);
        Run(sc, target, "replace_proxy", new() { ["replace_proxy.texture"] = 64, ["replace_proxy.voxels"] = 48, ["replace_proxy.share"] = false });
        Same(Expected["replace_proxy " + fixture], Fingerprint(sc));
        NearReference("replace_proxy_" + fixture, BasePixels(sc));
    }

    [Theory]
    [InlineData("remove_occluded_car")]
    public void GenerateLodProxyDefaultsReproduceToday(string fixture)
    {
        var (sc, target) = Fixture(fixture);
        Run(sc, target, "generate_lod", new() { ["generate_lod.far_level"] = "proxy", ["generate_lod.proxy_texture"] = 64, ["generate_lod.proxy_voxels"] = 48 });
        Same(Expected["generate_lod " + fixture], Fingerprint(sc));
        NearReference("generate_lod_" + fixture, BasePixels(sc));
    }

    /// <summary>The synth_split row of <see cref="GenerateLodProxyDefaultsReproduceToday"/> (the fingerprint recorded before
    /// the per-part work), opt-in (SCENEKIT_BENCH=1): over a minute, the slowest test of the suite (ruling F-5).
    /// <see cref="GenerateLodProxyOnTheSplitCarAtVoxels24"/> guards the same path in the regular suite.</summary>
    [BenchFact]
    public void GenerateLodProxyDefaultsReproduceTodayOnTheSplitCar() => GenerateLodProxyDefaultsReproduceToday("synth_split");

    /// <summary>The split car's generate_lod proxy far level at proxy_voxels 24 against its fingerprint and base pixels,
    /// recorded in the final fix wave on code whose 48-voxel output still matched today's (ruling F-5).</summary>
    [Fact]
    public void GenerateLodProxyOnTheSplitCarAtVoxels24()
    {
        var (sc, target) = Fixture("synth_split");
        Run(sc, target, "generate_lod", new() { ["generate_lod.far_level"] = "proxy", ["generate_lod.proxy_texture"] = 64, ["generate_lod.proxy_voxels"] = 24 });
        Same(Expected["generate_lod synth_split vox24"], Fingerprint(sc));
        NearReference("generate_lod_synth_split_vox24", BasePixels(sc));
    }

    [Theory]
    [InlineData("synth_split")]
    public void RemoveOccludedDefaultsReproduceToday(string fixture)
    {
        var (sc, target) = Fixture(fixture);
        Run(sc, target, "remove_occluded", new());
        Same(Expected["remove_occluded " + fixture], Kept(sc));
    }

    /// <summary>Equal, with the whole actual fingerprint in the message (Assert.Equal cuts long strings).</summary>
    static void Same(string expected, string actual) => Assert.True(expected == actual, $"fingerprint: expected {expected}, got {actual}");

    /// <summary>Triangle count, then SHA-256 (first 16 hex digits) of every geometry's positions, triangles, normals and
    /// UVs (doubles and ints as bytes), of the baked textures (the size of every image, base and companions, and the pixels
    /// of the class maps), and of the node kind / name / class sequence; all in scene order.</summary>
    internal static string Fingerprint(Scene sc)
    {
        using var geo = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var tex = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long tris = 0;
        foreach (var n in sc.IterNodes())
        {
            tree.AppendData(Encoding.UTF8.GetBytes($"{n.Kind}|{n.Name}|{n.Cls}\n"));
            if (n.Geom is { } g)
            {
                var T = g.Triangles();
                tris += T.Length / 3;
                Doubles(geo, g.Vertices?.Data);
                geo.AppendData(MemoryMarshal.AsBytes(T.AsSpan()));
                Doubles(geo, g.Normals?.Data);
                foreach (var (unit, uv) in g.TexCoords.OrderBy(x => x.Key)) { geo.AppendData(BitConverter.GetBytes(unit)); Doubles(geo, uv.Data); }
            }
            foreach (var t in Baked(n)) Pixels(tex, t.Baked, t.IsClassMap);
        }
        return $"{tris} {Hex(geo)} {Hex(tex)} {Hex(tree)}";
    }

    /// <summary>Every texture of the node with its companions, in a fixed order.</summary>
    static IEnumerable<Texture> Baked(Node n)
    {
        foreach (var t in n.StateSet?.Textures ?? new List<Texture>())
        {
            yield return t;
            foreach (var (_, c) in (t.Companions ?? new()).OrderBy(x => x.Key, StringComparer.Ordinal)) yield return c;
        }
    }

    /// <summary>The pixels of every baked image that is not a class map, concatenated in scene order.</summary>
    static byte[] BasePixels(Scene sc) =>
        sc.IterNodes().SelectMany(Baked).Where(t => !t.IsClassMap && t.Baked != null).SelectMany(t => t.Baked!.Pixels).ToArray();

    /// <summary>The base images against the reference recorded with the goldens: same length, every byte within 2 and at
    /// most 0.1 % of the bytes different.</summary>
    static void NearReference(string name, byte[] actual)
    {
        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "golden", name + ".bin"));
        Assert.Equal(expected.Length, actual.Length);
        int differ = 0, worst = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            int d = Math.Abs(actual[i] - expected[i]);
            if (d > 0) differ++;
            worst = Math.Max(worst, d);
        }
        Assert.True(worst <= 2 && differ * 1000L <= actual.Length, $"{name}: {differ} of {actual.Length} bytes differ, by up to {worst}");
    }

    /// <summary>The triangles each geometry keeps: name, triangle count and SHA-256 of its positions and triangles.</summary>
    static string Kept(Scene sc)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long tris = 0;
        foreach (var n in sc.IterNodes())
        {
            if (n.Geom is not { } g) continue;
            var T = g.Triangles();
            tris += T.Length / 3;
            h.AppendData(Encoding.UTF8.GetBytes($"{n.Name}|{T.Length / 3}\n"));
            Doubles(h, g.Vertices?.Data);
            h.AppendData(MemoryMarshal.AsBytes(T.AsSpan()));
        }
        return $"{tris} {Hex(h)}";
    }

    static void Doubles(IncrementalHash h, double[]? a)
    {
        h.AppendData(BitConverter.GetBytes(a?.Length ?? -1));
        if (a != null) h.AppendData(MemoryMarshal.AsBytes(a.AsSpan()));
    }

    static void Pixels(IncrementalHash h, RawImage? img, bool withPixels)
    {
        if (img == null) return;
        h.AppendData(Encoding.UTF8.GetBytes($"{img.Width}x{img.Height}x{img.Channels}"));
        if (withPixels) h.AppendData(img.Pixels);
    }

    static string Hex(IncrementalHash h) => Convert.ToHexString(h.GetHashAndReset())[..16].ToLowerInvariant();
}
