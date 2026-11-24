using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SceneKit;
using Xunit;

/// <summary>Golden hashes of the card output (R3-C1), taken on the code before any card change: geometry, node tree, the
/// saved scene text and every baked texture. <c>data/cards_golden.json</c> holds one dictionary per case; the tests compare,
/// and with SCENEKIT_GOLDEN_WRITE=1 they write the case instead (only done once, on the unchanged code).
/// Run-dependent bytes are normalised: the output folder in the .osgt text becomes "&lt;out&gt;". Every texture file is
/// hashed by its bytes (<c>texbytes/</c>, binding) and by its decoded pixels (<c>tex/</c>, diagnostic). The saved .flt
/// (<see cref="CaptureFlt"/>) carries no date or time (the writer patches the original records), so nothing needs normalising.</summary>
internal static class CardGolden
{
    static string Hex(byte[] h) => Convert.ToHexString(h).ToLowerInvariant();

    static void Doubles(IncrementalHash h, double[]? d)
    {
        h.AppendData(BitConverter.GetBytes(d?.Length ?? -1));
        if (d == null) return;
        foreach (double x in d) h.AppendData(BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(x)));
    }

    internal static string Hash(RawImage img)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(BitConverter.GetBytes(img.Width));
        h.AppendData(BitConverter.GetBytes(img.Height));
        h.AppendData(BitConverter.GetBytes(img.Channels));
        h.AppendData(img.Pixels);
        return Hex(h.GetHashAndReset());
    }

    internal static string Hash(GeomData g)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Doubles(h, g.Vertices?.Data);
        Doubles(h, g.Normals?.Data);
        foreach (var (unit, uv) in g.TexCoords) { h.AppendData(BitConverter.GetBytes(unit)); Doubles(h, uv.Data); }
        var tri = g.Triangles();
        h.AppendData(BitConverter.GetBytes(tri.Length));
        foreach (int i in tri) h.AppendData(BitConverter.GetBytes(i));
        return Hex(h.GetHashAndReset());
    }

    static string Sha(byte[] b) => Hex(SHA256.HashData(b));

    static string Num(double[]? d) => d == null ? "-" : string.Join(",", d.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));

    static string TreeText(Scene sc)
    {
        var sb = new StringBuilder();
        foreach (var n in sc.IterNodes())
            sb.Append(n.Kind).Append('|').Append(n.Cls).Append('|').Append(n.Name).Append('|').Append(Num(n.Matrix)).Append('|')
              .Append(Num(n.Center)).Append('|').Append(n.Children.Count).Append('\n');
        return sb.ToString();
    }

    /// <summary>Hash of a file written by a save: the bytes (binding identity).</summary>
    static string FileHash(string path) => Sha(File.ReadAllBytes(path));

    /// <summary>Keys of every file in the textures folders under <paramref name="outDir"/>: <c>texbytes/&lt;file&gt;</c> is the SHA-256
    /// of the file bytes (the binding identity), <c>tex/&lt;file&gt;</c> the hash of the decoded pixels (diagnostic: tells a
    /// pixel change from a pure encoding change).</summary>
    static IEnumerable<KeyValuePair<string, string>> TextureFiles(string outDir)
    {
        foreach (var folder in Directory.GetDirectories(outDir, "*_textures", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            foreach (var f in Directory.GetFiles(folder).OrderBy(x => x, StringComparer.Ordinal))
            {
                string n = Path.GetFileName(f);
                yield return new("texbytes/" + n, FileHash(f));
                if (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && ImageCodecs.Read(f) is { } img) yield return new("tex/" + n, Hash(img));
            }
    }

    static void Geometry(Scene sc, Dictionary<string, string> r)
    {
        int gi = 0;
        foreach (var n in sc.IterNodes())
            if (n.Geom != null) r[$"geom/{gi++}/{n.Name}"] = Hash(n.Geom);
        r["tree"] = Sha(Encoding.UTF8.GetBytes(TreeText(sc)));
    }

    /// <summary>Geometry, tree, the saved .osgt text and the textures of an .osgt save into <paramref name="outDir"/>.</summary>
    internal static Dictionary<string, string> Capture(Scene sc, string outDir)
    {
        var r = new Dictionary<string, string>(StringComparer.Ordinal);
        Geometry(sc, r);

        Directory.CreateDirectory(outDir);
        string osgt = Path.Combine(outDir, "out.osgt");
        SceneIO.Save(sc, osgt);
        string text = File.ReadAllText(osgt);
        string full = Path.GetFullPath(outDir).TrimEnd('\\', '/');
        text = text.Replace(full.Replace('\\', '/'), "<out>").Replace(full, "<out>").Replace(full.Replace("\\", "\\\\"), "<out>");
        // textures the scene still reads from its source folder are referenced as "../<folder name>/x.png": the folder is a fresh temp one per run
        if (Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(sc.Path))) is { Length: > 0 } srcName) text = text.Replace(srcName, "<src>");
        r["osgt"] = Sha(Encoding.UTF8.GetBytes(text));
        foreach (var kv in TextureFiles(outDir)) r[kv.Key] = kv.Value;
        return r;
    }

    /// <summary>Geometry, tree, the saved .flt bytes (<c>flt</c>) and the textures it wrote, for a scene saved to
    /// <c>&lt;outDir&gt;/x.flt</c>.</summary>
    internal static Dictionary<string, string> CaptureFlt(Scene sc, string outDir)
    {
        var r = new Dictionary<string, string>(StringComparer.Ordinal);
        Geometry(sc, r);
        Directory.CreateDirectory(outDir);
        string flt = Path.Combine(outDir, "x.flt");
        SceneIO.Save(sc, flt);
        r["flt"] = Sha(File.ReadAllBytes(flt));
        foreach (var kv in TextureFiles(outDir)) r[kv.Key] = kv.Value;
        return r;
    }

    static string? SourceFile()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SceneKit.Tests.csproj"))) return Path.Combine(d.FullName, "data", "cards_golden.json");
        return null;
    }

    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static SortedDictionary<string, SortedDictionary<string, string>> Read(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<SortedDictionary<string, SortedDictionary<string, string>>>(File.ReadAllText(path)) ?? new() : new();

    internal static void AssertMatches(string caseName, IReadOnlyDictionary<string, string> actual)
    {
        string? src = SourceFile();
        lock (Gate)
        {
            if (Environment.GetEnvironmentVariable("SCENEKIT_GOLDEN_WRITE") == "1")
            {
                Assert.NotNull(src);
                var all = Read(src!);
                all[caseName] = new SortedDictionary<string, string>(actual.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
                File.WriteAllText(src!, JsonSerializer.Serialize(all, Json) + "\n");
                return;
            }
            string path = src != null && File.Exists(src) ? src : Path.Combine(AppContext.BaseDirectory, "data", "cards_golden.json");
            Assert.True(File.Exists(path), "golden file missing: " + path);
            var golden = Read(path);
            Assert.True(golden.TryGetValue(caseName, out var want), $"case '{caseName}' is not in {path}");
            var diff = new List<string>();
            foreach (var k in want!.Keys.Union(actual.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                bool inWant = want.TryGetValue(k, out var w), inGot = actual.TryGetValue(k, out var a);
                if (!inWant) diff.Add($"{k}: new key (not in golden)");
                else if (!inGot) diff.Add($"{k}: missing");
                else if (w != a) diff.Add($"{k}: expected {w![..12]}..., got {a![..12]}...");
            }
            Assert.True(diff.Count == 0, $"{caseName} differs from the golden data in {diff.Count} key(s):\n  " + string.Join("\n  ", diff));
        }
    }
}
