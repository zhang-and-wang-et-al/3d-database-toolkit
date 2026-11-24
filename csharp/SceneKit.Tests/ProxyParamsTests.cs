using SceneKit;
using Xunit;

/// <summary>R3-C3: the proxy settings record and the new pass keys (record only, the passes do not read them yet).</summary>
public class ProxyParamsTests
{
    [Fact]
    public void DefaultsAreTodaysBehaviour()
    {
        var p = new ProxyParams();
        Assert.Equal(ProxyParts.Whole, p.Parts);
        Assert.False(p.Within);
        Assert.Equal(0.05, p.Ratio);
        Assert.Equal(128, p.Voxels);
        Assert.Equal(ProxyGround.Full, p.Ground);
        Assert.Equal(ProxyNormals.Hull, p.Normals);
        Assert.Equal(45, p.Crease);
        Assert.Equal(p, ProxyParams.FromPassParams(new Dictionary<string, object>()));
    }

    [Fact]
    public void RoundTripsThroughPassParams()
    {
        var p = new ProxyParams { Parts = ProxyParts.PerPart, Within = true, MaxErrorPx = 1, AtDistance = 80, Ground = ProxyGround.Cut, Normals = ProxyNormals.Original, Crease = 30, Ratio = 0.1, Voxels = 64, MaxError = 0.02 };
        Assert.Equal(p, ProxyParams.FromPassParams(p.ToPassParams()));
        foreach (var k in ProxyParams.PassKeys) Assert.True(p.ToPassParams().ContainsKey("replace_proxy." + k), k);
    }

    [Fact]
    public void OnePixelAtHundredMetresIsSevenPointSevenCentimetres()
        => Assert.Equal(0.0767, new ProxyParams { MaxErrorPx = 1, AtDistance = 100 }.ErrorMetres(), 4);

    [Fact]
    public void LodKeysMapOntoTheSameRecord()
    {
        var p = ProxyParams.FromLodParams(new Dictionary<string, object> { ["generate_lod.proxy_from"] = 100.0, ["generate_lod.pixel_error"] = 1.0, ["generate_lod.proxy_parts"] = "per_part" });
        Assert.True(p.Within);
        Assert.Equal(0.0767, p.MaxError, 4);
        Assert.Equal(ProxyParts.PerPart, p.Parts);
        Assert.Equal(0.06, p.Ratio);
        Assert.False(ProxyParams.FromLodParams(new Dictionary<string, object> { ["generate_lod.proxy_from"] = 0.0 }).Within);
    }

    [Fact]
    public void DescribeReadsLikeTheSpec()
    {
        Assert.Equal("one hull, keep 5 % of the triangles", new ProxyParams().Describe());
        Assert.Equal("per part, within 4 cm, underside cut, shading from the original",
            new ProxyParams { Parts = ProxyParts.PerPart, Within = true, Ground = ProxyGround.Cut, Normals = ProxyNormals.Original }.Describe());
        Assert.Equal("one hull, within 1 px at 100 m (7.7 cm)", new ProxyParams { Within = true, MaxErrorPx = 1 }.Describe());
        Assert.Equal("one hull, keep 5 % of the triangles, underside not seen from below", new ProxyParams { Ground = ProxyGround.Above }.Describe());
    }

    [Fact]
    public void NewKeysAreListedWithTodaysDefaults()
    {
        object Def(string pass, string key) => Optimizer.Passes[pass].Params.Single(x => x.Key == key).Default;
        Assert.Equal("whole", Def("replace_proxy", "parts"));
        Assert.Equal("keep", Def("replace_proxy", "target"));
        Assert.Equal("full", Def("replace_proxy", "ground"));
        Assert.Equal("hull", Def("replace_proxy", "normals"));
        Assert.Equal(0.05, Def("replace_proxy", "ratio"));
        Assert.Equal(512, Def("replace_proxy", "texture"));
        Assert.Equal(128, Def("replace_proxy", "voxels"));
        Assert.Equal(true, Def("replace_proxy", "share"));
        Assert.Equal("white", Def("replace_proxy", "extra_fill"));
        Assert.Equal(0, Def("replace_proxy", "class_fill"));
        Assert.Equal("full", Def("remove_occluded", "ground"));
        Assert.Equal("whole", Def("generate_lod", "proxy_parts"));
        Assert.Equal(0.0, Def("generate_lod", "proxy_from"));
        Assert.Equal("full", Def("generate_lod", "proxy_ground"));
        Assert.Equal("hull", Def("generate_lod", "proxy_normals"));
        Assert.Equal(512, Def("generate_lod", "proxy_texture"));
        Assert.Equal(128, Def("generate_lod", "proxy_voxels"));
    }

    [Fact]
    public void CliStyleValuesAreRead()
    {
        var p = ProxyParams.FromPassParams(new Dictionary<string, object> { ["replace_proxy.parts"] = "per_part", ["replace_proxy.max_error"] = "0.04", ["replace_proxy.target"] = "within" });
        Assert.Equal(ProxyParts.PerPart, p.Parts);
        Assert.Equal(0.04, p.MaxError);
        Assert.True(p.Within);
    }

    [Fact]
    public void JsonElementValuesAreRead()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"n\": 0.07, \"s\": \"0.02\", \"v\": 64, \"t\": \"per_part\"}");
        var r = doc.RootElement;
        var p = ProxyParams.FromPassParams(new Dictionary<string, object> { ["replace_proxy.max_error"] = r.GetProperty("n"), ["replace_proxy.crease"] = r.GetProperty("s"), ["replace_proxy.voxels"] = r.GetProperty("v"), ["replace_proxy.parts"] = r.GetProperty("t") });
        Assert.Equal(0.07, p.MaxError);
        Assert.Equal(0.02, p.Crease);
        Assert.Equal(64, p.Voxels);
        Assert.Equal(ProxyParts.PerPart, p.Parts);
    }

    [Fact]
    public void MalformedNumbersFallBackToTheDefault()
    {
        var p = ProxyParams.FromPassParams(new Dictionary<string, object> { ["replace_proxy.max_error"] = "", ["replace_proxy.crease"] = "abc", ["replace_proxy.voxels"] = "x" });
        Assert.Equal(0.04, p.MaxError);
        Assert.Equal(45, p.Crease);
        Assert.Equal(128, p.Voxels);
    }

    [Fact]
    public void InvalidEnumStringsFallBackToTheDefault()
        => Assert.Equal(ProxyGround.Full, ProxyParams.FromPassParams(new Dictionary<string, object> { ["replace_proxy.ground"] = "cutt" }).Ground);

    [Fact]
    public void DescribeUsesTheGivenScreen()
        => Assert.Equal("one hull, within 1 px at 100 m (3.8 cm)", new ProxyParams { Within = true, MaxErrorPx = 1 }.Describe(2160));
}
