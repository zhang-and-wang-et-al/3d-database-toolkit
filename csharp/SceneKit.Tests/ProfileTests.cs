using SceneKit;
using Xunit;

/// <summary>R3-C10: named profiles (embedded built-ins and user JSON files), their validation, application and record.</summary>
public class ProfileTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sk-profiles-" + Guid.NewGuid().ToString("N"));

    public ProfileTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static Profile Built(string name) => Assert.Single(Profiles.BuiltIn(), p => p.Name == name);

    static (HashSet<string> passes, Dictionary<string, object> prms) Applied(Profile p)
    {
        var passes = new HashSet<string>();
        var prms = new Dictionary<string, object>();
        Profiles.Apply(p, passes, prms);
        return (passes, prms);
    }

    /// <summary>Runs the passes on ReplaceTests.Tree() with the tree selected; the triangles of what replaced it.</summary>
    static (List<PassResult> results, long tris) RunOnTree(IEnumerable<string> passes, Dictionary<string, object> prms)
    {
        var (sc, tree) = ReplaceTests.Tree();
        var (results, _, _) = Optimizer.Run(sc, Optimizer.PipelineSort(passes), prms, targets: new List<Node> { tree });
        foreach (var r in results) Assert.Null(r.Error);
        var put = Assert.Single(sc.Root!.Children);
        return (results, SoftRender.GeometryItems(put).Sum(x => (long)x.node.Geom!.Counts().tris));
    }

    static object Parse(string v)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (v is "true" or "false") return v == "true";
        if (int.TryParse(v, System.Globalization.NumberStyles.AllowLeadingSign, inv, out var i)) return i;
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var d)) return d;
        return v;
    }

    [Fact]
    public void BuiltInsLoadAndValidate()
    {
        var (all, warnings) = Profiles.LoadAll(dir);
        Assert.Empty(warnings);
        Assert.Equal(11, all.Count);
        Assert.Equal(11, Profiles.BuiltIn().Count);
        Assert.All(all, p => Assert.True(p.BuiltIn));
        Assert.Equal(11, all.Select(p => p.Name).Distinct().Count());
        foreach (var name in new[] { "Safe", "Aggressive", "LOD" }) Assert.Contains(all, p => p.Name == name);
        foreach (var p in all)
        {
            Assert.True(p.Version >= 1, p.Name);
            Assert.NotEmpty(p.Passes);
            foreach (var (pass, values) in p.Passes)
            {
                Assert.True(Optimizer.Passes.TryGetValue(pass, out var spec), $"{p.Name}: {pass}");
                foreach (var key in values.Keys) Assert.Contains(spec!.Params, pp => pp.Key == key);
            }
        }
        // presets: version 1, no values, no pixel target
        var lod = Built("LOD");
        Assert.Equal(Optimizer.Presets["lod"], lod.Passes.Keys);
        Assert.All(lod.Passes.Values, v => Assert.Empty(v));
        Assert.Null(lod.PixelError);
        Assert.Equal("LOD@1", lod.Tag);
    }

    [Fact]
    public void BuiltInValuesMatchTheSpec()
    {
        var (passes, prms) = Applied(Built("Tree, ground level"));
        Assert.Equal(new[] { "replace_cards" }, passes);
        Assert.Equal("ground", prms["replace_cards.layout"]);
        Assert.Equal(512, prms["replace_cards.texture"]);
        Assert.Equal("full", prms["replace_cards.padding"]);
        Assert.Equal("coverage", prms["replace_cards.alpha_mips"]);
        Assert.DoesNotContain(prms.Keys, k => k.StartsWith("generate_lod.", StringComparison.Ordinal));
        Assert.Equal("vegetation", Built("Tree, ground level").Class);
        Assert.Equal("ground view", Built("Tree, ground level").Use);
        Assert.Contains("coverage mips only take effect with .dds output", Built("Tree, ground level").Description);

        (passes, prms) = Applied(Built("Tree LOD chain"));
        Assert.Equal(new[] { "generate_lod" }, passes);
        Assert.Equal("cards", prms["generate_lod.far_level"]);
        Assert.Equal("star3", prms["generate_lod.far_layout"]);
        Assert.Equal(6.0, Convert.ToDouble(prms["generate_lod.pixel_error"]));

        (passes, prms) = Applied(Built("Vehicle far"));
        Assert.True(passes.SetEquals(new[] { "remove_occluded", "simplify", "generate_lod" }));
        Assert.Equal("permissive", prms["simplify.seams"]);
        Assert.Equal("proxy", prms["generate_lod.far_level"]);
        Assert.Equal("error", prms["generate_lod.switching"]);
        Assert.Equal(1.0, Convert.ToDouble(prms["generate_lod.pixel_error"]));

        (_, prms) = Applied(Built("Building far"));
        Assert.Equal(0.05, Convert.ToDouble(prms["replace_proxy.ratio"]));
        Assert.Equal(3.0, Built("Building far").PixelError);
        Assert.Equal("box", Applied(Built("Building box")).prms["replace_cards.layout"]);
    }

    [Fact]
    public void AppliedNamedLayoutIsNotOverriddenByItsDefaultRows()
    {
        // T3-d: Apply writes every replace_cards row explicitly; they are the rows of the profile's layout, so aerial stays aerial
        var (passes, prms) = Applied(Built("Tree, aerial"));
        Assert.Equal(3, prms["replace_cards.horizontal"]);
        Assert.Equal("layered", prms["replace_cards.mode"]);
        prms["replace_cards.texture"] = 64;   // speed only; the layout decides the triangles
        var (_, tris) = RunOnTree(passes, prms);
        Assert.Equal(18, tris);   // aerial, not crossed4 (16)
    }

    static Dictionary<string, object> EditorParams(string layout, params (string key, object value)[] changes)
    {
        // what the editor window holds: the rows of the layout, the layout name and the other replace_cards defaults
        var e = new Dictionary<string, object>();
        foreach (var pp in Optimizer.Passes["replace_cards"].Params) e[$"replace_cards.{pp.Key}"] = pp.Default;
        foreach (var (k, v) in CardLayout.Named(layout).Rows()) e[$"replace_cards.{k}"] = v;
        e["replace_cards.layout"] = layout;
        foreach (var (k, v) in changes) e[$"replace_cards.{k}"] = v;
        return e;
    }

    [Fact]
    public void ProfileBoxLabels()
    {
        var aerial = Built("Tree, aerial");
        Assert.Equal("Tree, aerial", InspectorModel.ProfileBoxLabel(aerial, null));
        Assert.Equal("Tree, aerial", InspectorModel.ProfileBoxLabel(aerial, aerial));
        Assert.Equal("Custom (from Tree, aerial)", InspectorModel.ProfileBoxLabel(null, aerial));
        Assert.Equal("Custom", InspectorModel.ProfileBoxLabel(null, null));
    }

    [Fact]
    public void EditorStateWithoutTheLayoutRowsIsACustomLayout_T10d()
    {
        // layout=ground but crossed4's rows (the Layout row had NOT written ground's rows): custom, crossed4-based (16 triangles)
        var wrong = EditorParams("crossed4");
        wrong["replace_cards.layout"] = "ground";
        var (passes, prms) = Applied(Profiles.FromEditor("Wrong", new[] { "replace_cards" }, wrong));
        prms["replace_cards.texture"] = 64;
        var (results, tris) = RunOnTree(passes, prms);
        Assert.Equal(16, tris);
        Assert.DoesNotContain("ground", results[0].Message);   // a custom crossed4-based layout, not ground

        // after choosing ground the Layout row writes ground's rows: ground
        var right = EditorParams("ground");
        foreach (var (k, v) in CardLayout.Named("ground").Rows()) right[$"replace_cards.{k}"] = v;
        var (passes2, prms2) = Applied(Profiles.FromEditor("Right", new[] { "replace_cards" }, right));
        Assert.Equal("ground", prms2["replace_cards.layout"]);
        prms2["replace_cards.texture"] = 64;
        var (_, tris2) = RunOnTree(passes2, prms2);
        Assert.Equal(16, tris2);
    }

    [Fact]
    public void EditorSavedProfileKeepsItsLayout()
    {
        var saved = Profiles.FromEditor("Mine", new[] { "replace_cards" }, EditorParams("aerial"));
        var back = Profiles.Parse(Profiles.ToJson(saved), "mine.json").Profile!;
        var (passes, prms) = Applied(back);
        Assert.Equal("aerial", prms["replace_cards.layout"]);
        prms["replace_cards.texture"] = 64;
        var (results, tris) = RunOnTree(passes, prms);
        Assert.Equal(18, tris);   // aerial, not crossed4 (16)
        Assert.Contains("aerial", results[0].Message);

        var edited = Profiles.FromEditor("Mine2", new[] { "replace_cards" }, EditorParams("aerial", ("horizontal", 2)));
        var back2 = Profiles.Parse(Profiles.ToJson(edited), "mine2.json").Profile!;
        var (passes2, prms2) = Applied(back2);
        Assert.Equal(2, prms2["replace_cards.horizontal"]);
        prms2["replace_cards.texture"] = 64;
        var (results2, _) = RunOnTree(passes2, prms2);
        Assert.Contains("custom (from aerial)", results2[0].Message);
    }

    [Fact]
    public void UnknownLayoutNameWarnsOnApply()
    {
        var (p, _) = Profiles.Parse("""{"name": "X", "passes": {"replace_cards": {"layout": "ground"}}}""", "x.json");
        p!.Passes["replace_cards"]["layout"] = "nope";
        var warnings = new List<string>();
        Profiles.Apply(p, new HashSet<string>(), new Dictionary<string, object>(), warnings);
        Assert.Contains(warnings, w => w.Contains("nope"));
    }

    [Fact]
    public void UnknownPassAndKeyLoadWarnAndRunTheRest()
    {
        var (p, warnings) = Profiles.Parse("""{"name": "Odd", "passes": {"no_such_pass": {}, "replace_cards": {"layout": "cross2", "wobble": 3}}}""", "odd.json");
        Assert.NotNull(p);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("no_such_pass"));
        Assert.Contains(warnings, w => w.Contains("wobble"));
        Assert.Equal(new[] { "replace_cards" }, p!.Passes.Keys);
        var (passes, prms) = Applied(p);
        prms["replace_cards.texture"] = 64;
        var (results, tris) = RunOnTree(passes, prms);
        Assert.Null(results[0].Error);
        Assert.Equal(8, tris);
    }

    [Fact]
    public void BrokenUserProfileIsSkippedWithWarning()
    {
        File.WriteAllText(Path.Combine(dir, "bad.json"), "{ \"name\": \"Bad\", ");
        File.WriteAllText(Path.Combine(dir, "wrong.json"), """{"name": "Wrong", "passes": {"replace_cards": {"layout": "cross2", "texture": "big"}}}""");
        File.WriteAllText(Path.Combine(dir, "ok.json"), """{"name": "Fine", "version": 3, "passes": {"replace_cards": {"layout": "star3"}}}""");
        var (all, warnings) = Profiles.LoadAll(dir);
        Assert.Equal(13, all.Count);
        Assert.DoesNotContain(all, p => p.Name == "Bad");
        var ok = Assert.Single(all, p => p.Name == "Fine");
        Assert.False(ok.BuiltIn);
        Assert.Equal("Fine@3", ok.Tag);
        Assert.Equal(Path.Combine(dir, "ok.json"), ok.Path);
        var wrong = Assert.Single(all, p => p.Name == "Wrong");
        Assert.False(wrong.Passes["replace_cards"].ContainsKey("texture"));
        Assert.Equal("cross2", wrong.Passes["replace_cards"]["layout"]);
        Assert.Contains(warnings, w => w.Contains("bad.json"));
        Assert.Contains(warnings, w => w.Contains("texture"));
        // a value outside the choices is a warning too
        var (p2, w2) = Profiles.Parse("""{"name": "X", "passes": {"replace_cards": {"layout": "tulip"}}}""", "x.json");
        Assert.Empty(p2!.Passes["replace_cards"]);
        Assert.Contains("tulip", Assert.Single(w2));
    }

    [Fact]
    public void BuiltInNameIsRefusedOnSave()
    {
        var p = new Profile { Name = "Clutter", Passes = { ["replace_cards"] = new() } };
        var e = Assert.Throws<InvalidOperationException>(() => Profiles.Save(p, dir));
        Assert.Equal("built-in profiles are read-only; save as a new name", e.Message);
        Assert.Empty(Directory.GetFiles(dir));
        Assert.Throws<InvalidOperationException>(() => Profiles.Delete(Built("Clutter")));
    }

    [Theory]
    [InlineData("CON")] [InlineData("nul")] [InlineData("Aux.txt")] [InlineData("COM1")] [InlineData("lpt9")] [InlineData("PRN.v2")]
    public void WindowsDeviceNamesAreRefusedOnSave(string name)
    {
        var p = new Profile { Name = name, Passes = { ["replace_cards"] = new() } };
        var e = Assert.Throws<InvalidOperationException>(() => Profiles.Save(p, dir));
        Assert.Contains("device name", e.Message);
        Assert.Empty(Directory.GetFiles(dir));
        // names that only start like a device are fine
        Assert.EndsWith("CONSOLE.json", Profiles.Save(new Profile { Name = "CONSOLE", Passes = { ["replace_cards"] = new() } }, dir));
    }

    [Fact]
    public void SaveRefusesAFileNameUsedByAnotherProfile()
    {
        var a = Profiles.FromEditor("A/B", new[] { "replace_cards" }, new Dictionary<string, object>());
        Profiles.Save(a, dir);
        var b = Profiles.FromEditor("A_B", new[] { "replace_cards" }, new Dictionary<string, object> { ["replace_cards.layout"] = "ground" });
        var e = Assert.Throws<InvalidOperationException>(() => Profiles.Save(b, dir));
        Assert.Contains("A/B", e.Message);
        Assert.Equal("A/B", Assert.Single(Profiles.LoadAll(dir).Profiles, x => !x.BuiltIn).Name);
        // the same name again replaces its own file, and leaves no temp file behind
        Profiles.Save(Profiles.FromEditor("a/b", new[] { "replace_cards" }, new Dictionary<string, object> { ["replace_cards.layout"] = "ground" }), dir);
        Assert.Single(Directory.GetFiles(dir));
        Assert.Equal("ground", Assert.Single(Profiles.LoadAll(dir).Profiles, x => !x.BuiltIn).Passes["replace_cards"]["layout"]);
    }

    [Fact]
    public void SaveStoresAllValues()
    {
        var p = Profiles.FromEditor("Mine", new[] { "replace_cards" }, new Dictionary<string, object> { ["replace_cards.layout"] = "ground" }, "vegetation", "test", "select trees");
        var json = Profiles.ToJson(p);
        foreach (var pp in Optimizer.Passes["replace_cards"].Params) Assert.Contains($"\"{pp.Key}\"", json);
        Assert.Equal(Optimizer.Passes["replace_cards"].Params.Length, p.Passes["replace_cards"].Count);
        var (back, warnings) = Profiles.Parse(json, "mine.json");
        Assert.Empty(warnings);
        Assert.Equal(json, Profiles.ToJson(back!));
        Assert.Equal(p.Name, back!.Name);
        Assert.Equal(p.Class, back.Class);
        Assert.Equal(p.SelectHint, back.SelectHint);
        foreach (var (k, v) in p.Passes["replace_cards"]) Assert.Equal(v, back.Passes["replace_cards"][k]);
        var path = Profiles.Save(p, dir);
        Assert.Equal(Path.Combine(dir, "Mine.json"), path);
        var loaded = Assert.Single(Profiles.LoadAll(dir).Profiles, x => x.Name == "Mine");
        Assert.Equal(json, Profiles.ToJson(loaded));
        Profiles.Delete(loaded);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ComposeKeepsArgumentOrder()
    {
        var after = Profiles.Compose(new[] { ("profile", "Tree, aerial"), ("set", "replace_cards.texture=2048") }, Parse, dir);
        Assert.Equal(2048, after.Params["replace_cards.texture"]);
        Assert.Equal("Tree, aerial@1", after.ProfileTag);
        Assert.Equal(new[] { "replace_cards" }, after.Passes);
        var before = Profiles.Compose(new[] { ("set", "replace_cards.texture=2048"), ("profile", "Tree, aerial") }, Parse, dir);
        Assert.Equal(1024, before.Params["replace_cards.texture"]);
        // a --set of a pass the profile does not tick survives
        var other = Profiles.Compose(new[] { ("set", "simplify.ratio=0.3"), ("profile", "Clutter") }, Parse, dir);
        Assert.Equal(0.3, other.Params["simplify.ratio"]);
        // a JSON path works too
        var file = Path.Combine(dir, "elsewhere", "my.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"name": "Mine", "version": 4, "passes": {"replace_cards": {"layout": "star3"}}}""");
        var byPath = Profiles.Compose(new[] { ("profile", file) }, Parse, dir);
        Assert.Equal("Mine@4", byPath.ProfileTag);
        Assert.Equal("star3", byPath.Params["replace_cards.layout"]);
        Assert.Throws<ArgumentException>(() => Profiles.Compose(new[] { ("profile", "No such profile") }, Parse, dir));
        Assert.Throws<ArgumentException>(() => Profiles.Compose(new[] { ("set", "replace_cards.texture") }, Parse, dir));
    }

    [Fact]
    public void RecordNamesProfileAtVersion()
    {
        var (passes, prms) = Applied(Built("Tree, aerial"));
        var rec = InspectorModel.RecordParams("Tree, aerial@1", passes, prms);
        Assert.StartsWith("profile=Tree, aerial@1, ", rec);
        Assert.Contains("replace_cards.layout=aerial", rec);
        Assert.Contains("replace_cards.horizontal=3", rec);   // the rows of the layout, written by Apply (T3-d)
        Assert.Contains("replace_cards.class_fill=0", rec);    // defaults are listed too
        // all params: defaults included, sorted
        var all = Optimizer.AllParams(new[] { "replace_cards" }, new Dictionary<string, object> { ["replace_cards.layout"] = "aerial" });
        Assert.Contains("replace_cards.horizontal=0", all);
        Assert.Equal(Optimizer.Passes["replace_cards"].Params.Length, all.Count);
        Assert.Equal(all.OrderBy(x => x, StringComparer.Ordinal), all);
        // without a profile: today's string
        var plain = new Dictionary<string, object> { ["simplify.ratio"] = 0.25, ["simplify.min_tris"] = 10000 };
        Assert.Equal(string.Join(", ", Optimizer.NonDefaultParams(new[] { "simplify" }, plain)),
            InspectorModel.RecordParams(null, new[] { "simplify" }, plain));
        Assert.Equal("simplify.ratio=0.25", InspectorModel.RecordParams(null, new[] { "simplify" }, plain));
        // record keys of the results follow
        var r = new PassResult("replace_cards", "") { Record = { "cards.layout=aerial" } };
        Assert.Equal("simplify.ratio=0.25, cards.layout=aerial", InspectorModel.RecordParams(null, new[] { "simplify" }, plain, new[] { r }));
        Assert.EndsWith(", cards.layout=aerial", InspectorModel.RecordParams("Tree, aerial@1", passes, prms, new[] { r }));
    }

    [Fact]
    public void EditingAProfileLeavesOldRecordsAlone()
    {
        var file = Path.Combine(dir, "mine.json");
        File.WriteAllText(file, """{"name": "My trees", "version": 1, "passes": {"replace_cards": {"layout": "cross2", "texture": 64}}}""");
        var w = new List<string>();
        var p1 = Profiles.Find("My trees", dir, w)!;
        Assert.Empty(w);
        var (passes, prms) = Applied(p1);
        var (results, _) = RunOnTree(passes, prms);
        var r1 = InspectorModel.RecordParams(p1.Tag, passes, prms, results);
        var kept = new string(r1.AsSpan());
        Assert.Contains("profile=My trees@1, ", r1);
        Assert.Contains("replace_cards.texture=64", r1);

        File.WriteAllText(file, """{"name": "My trees", "version": 2, "passes": {"replace_cards": {"layout": "cross2", "texture": 2048}}}""");
        var p2 = Profiles.Find("My trees", dir, w)!;
        var (passes2, prms2) = Applied(p2);
        var r2 = InspectorModel.RecordParams(p2.Tag, passes2, prms2);
        Assert.Equal(kept, r1);
        Assert.Contains("@2", r2);
        Assert.Contains("replace_cards.texture=2048", r2);
        Assert.DoesNotContain("@2", r1);
    }

    [Fact]
    public void FarLayoutDefaultIsTodaysCrossed4()
    {
        var spec = Optimizer.Passes["generate_lod"];
        var fl = Assert.Single(spec.Params, pp => pp.Key == "far_layout");
        Assert.Equal("crossed4", fl.Default);
        Assert.Equal(CardLayout.Names, fl.Choices);
        Assert.Equal("far_layout", spec.Params[^1].Key);
        var (sc, _) = ReplaceTests.Tree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object>
        {
            ["generate_lod.min_tris"] = 100, ["generate_lod.far_level"] = "cards", ["generate_lod.far_layout"] = "star3", ["generate_lod.proxy_texture"] = 128,
        });
        Assert.Null(results[0].Error);
        var lod = Assert.Single(sc.IterNodes().Where(n => n.Kind == Kind.Lod).ToList());
        var geo = Assert.Single(SoftRender.GeometryItems(lod.Children[^1])).node;
        Assert.Equal(12, geo.Geom!.Counts().tris);
    }

    [Theory]
    [InlineData("Safe")] [InlineData(" aggressive ")] [InlineData("LOD")] [InlineData("None")] [InlineData("CUSTOM")]
    public void PresetNamesAreReservedForProfiles(string name) => Assert.True(InspectorModel.IsReservedProfileName(name));

    [Fact]
    public void OtherNamesAreNotReservedAndSentenceSurvivesEmpty()
    {
        Assert.False(InspectorModel.IsReservedProfileName("Tree"));
        Assert.False(InspectorModel.IsReservedProfileName("My safe"));
        Assert.Equal("", InspectorModel.Sentence(""));
        Assert.Equal("Bad name.", InspectorModel.Sentence("bad name"));
        Assert.Equal("Bad name.", InspectorModel.Sentence("bad name."));
    }

    [Fact]
    public void RecordWithoutProfileTagHasNoProfileKey()
    {
        var prms = new Dictionary<string, object>();
        Assert.DoesNotContain("profile=", InspectorModel.RecordParams(null, new[] { "simplify" }, prms));
        Assert.StartsWith("profile=Tree@1", InspectorModel.RecordParams("Tree@1", new[] { "simplify" }, prms));
    }
}
