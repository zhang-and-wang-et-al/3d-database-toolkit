namespace SceneKit;

/// <summary>Texture edits from the inspector (TX4): resize, replace the image file, remove a texture unit. Each edit
/// checks first and, when it can be done, pushes an undo snapshot onto <c>history</c> before changing anything.</summary>
public static class TextureEdits
{
    /// <summary>Outcome of an edit; Message says what happens (or why nothing did).</summary>
    public sealed record Result(bool Ok, string Message);

    static Result No(string why) => new(false, why);

    static void Push(UndoHistory? history, Scene scene, string label) => history?.Push(new Snapshot(scene, label));

    /// <summary>Write the texture at w x h when saving (like downscale_textures; needs <see cref="ImageResizer"/>).
    /// The image's own size clears the resize. Baked textures are scaled right away. Larger than the image is refused.
    /// Companion images (infrared ...) go with it: saved at the same size under their naming rule. A class map (file or
    /// baked) keeps its size unless <paramref name="resizeClassMaps"/>, and is then resized nearest, never filtered.</summary>
    public static Result SetResize(Scene scene, Texture tex, int w, int h, UndoHistory? history = null, bool resizeClassMaps = false)
    {
        if (w < 1 || h < 1) return No("the size must be at least 1 x 1");
        if (tex.Embedded) return No("embedded image: resizing is not supported yet (export it as a file first)");
        if (tex.Baked != null)
        {
            if (w > tex.Baked.Width || h > tex.Baked.Height) return No($"larger than the baked image ({tex.Baked.Width}x{tex.Baked.Height})");
            if (tex.IsClassMap && !resizeClassMaps && (w, h) != (tex.Baked.Width, tex.Baked.Height)) return ClassMapKept(tex);
            Push(history, scene, $"resize {tex.Label} to {w}x{h}");
            Node.Edited();
            tex.Baked = tex.IsClassMap ? ImageCodecs.ResizeNearest(tex.Baked, w, h) : ImageCodecs.Resize(tex.Baked, w, h);
            tex.Width = w; tex.Height = h;
            if (tex.Companions != null)   // a new dictionary: undo snapshots keep the old one
                tex.Companions = tex.Companions.ToDictionary(kv => kv.Key, kv =>
                {
                    var c = kv.Value.ShallowCopy();
                    if (c.Baked == null || (c.IsClassMap && !resizeClassMaps)) return c;   // class maps keep their size unless asked
                    c.Baked = c.IsClassMap ? ImageCodecs.ResizeNearest(c.Baked, w, h) : ImageCodecs.Resize(c.Baked, w, h);
                    c.Width = w; c.Height = h;
                    return c;
                });
            return new(true, $"{tex.Label}: baked image scaled to {w}x{h}" + WithCompanions(tex, resizeClassMaps));
        }
        if (tex.Path == null || !tex.Exists) return No($"{tex.Label}: image file not found");
        var info = Images.Probe(tex.Path);
        int ow = info?.Width ?? (tex.Resize == null ? tex.Width : 0), oh = info?.Height ?? (tex.Resize == null ? tex.Height : 0);
        if (ow == 0 || oh == 0) return No($"{tex.Label}: the image size is unknown");
        if (w > ow || h > oh) return No($"larger than the image ({ow}x{oh})");
        if (tex.IsClassMap && !resizeClassMaps && (w, h) != (ow, oh)) return ClassMapKept(tex);
        Push(history, scene, $"resize {tex.Label} to {w}x{h}");
        Node.Edited();
        tex.Resize = w == ow && h == oh ? null : (w, h);
        tex.Width = w; tex.Height = h;
        tex.ResizeClassMaps = resizeClassMaps;
        return new(true, (tex.Resize == null ? $"{tex.Label}: written at its own size {w}x{h}" : $"{tex.Label}: written at {w}x{h} when saving") + WithCompanions(tex, resizeClassMaps));
    }

    static Result ClassMapKept(Texture t) => No($"{t.Label}: the class map keeps its size (\"Resize class maps too\" is off)");

    static string WithCompanions(Texture t, bool resizeClassMaps = false) => t.Companions is { Count: > 0 } c
        ? $" (with its {string.Join(", ", c.Keys.Select(Companions.Label).Distinct())} companion{(c.Count == 1 ? "" : "s")}"
          + (c.Values.Any(x => x.IsClassMap) ? (resizeClassMaps ? "; class map resized (nearest)" : "; class map left alone") : "") + ")" : "";

    /// <summary>Point the texture at another image file. Saving writes the new reference (osgt / osg FileName, OpenFlight
    /// texture palette, generated state sets). Not for embedded images or osgt images without an image object.</summary>
    public static Result ReplaceFile(Scene scene, Texture tex, string newPath, UndoHistory? history = null)
    {
        if (tex.Embedded) return No("embedded image: replacing it is not supported yet");
        if (!File.Exists(newPath)) return No($"{newPath} does not exist");
        if (scene.Doc != null && tex.Src is Obj)
        {
            var obj = scene.Doc.Dialect == "osgt" ? tex.ImageSrc : tex.Src as Obj;
            if (obj?.Get(scene.Doc.Dialect == "osgt" ? "FileName" : "file") is not { Toks.Length: > 1 })
                return No($"{tex.Label}: this file stores the image reference in a form that cannot be rewritten yet");
        }
        var full = System.IO.Path.GetFullPath(newPath);
        var info = Images.Probe(full);
        Push(history, scene, $"replace {tex.Label} with {System.IO.Path.GetFileName(full)}");
        Node.Edited();
        var oldCompanions = tex.Companions;
        var newCompanions = Companions.Find(full, scene.CompanionPatterns, tex.Unit);
        var lost = (oldCompanions?.Keys ?? Enumerable.Empty<string>()).Where(p => newCompanions == null || !newCompanions.ContainsKey(p)).ToList();
        string sceneDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(scene.ConvertedFrom ?? (scene.Path.Length > 0 ? scene.Path : full)))!;
        tex.File = OsgWriterBase.RelPath(full, sceneDir);
        tex.Path = full;
        tex.Exists = true;
        tex.Baked = null;
        tex.Resize = null;
        tex.Repointed = true;
        try { tex.FileBytes = new FileInfo(full).Length; } catch (IOException) { }
        tex.Width = info?.Width ?? 0; tex.Height = info?.Height ?? 0;
        if (info != null) { tex.Channels = info.Channels; tex.Fmt = info.Fmt; tex.Bpp = info.Bpp; }
        else { tex.Fmt = ""; tex.Bpp = null; }
        tex.Companions = newCompanions;
        string msg = $"texture unit {tex.Unit} now uses {tex.File}" + (info == null ? " (image size unknown)" : $" ({info.Width}x{info.Height})");
        if (lost.Count > 0)
            msg += $"; warning: the new file has no {string.Join(", ", lost.Select(Companions.Label).Distinct())} companion like the old one had (" +
                   string.Join(", ", lost.Select(p => System.IO.Path.GetFileName(Companions.Apply(p, full)))) + " not found)";
        return new(true, msg);
    }

    /// <summary>Set the wrap / filter modes and the maximum anisotropy (TX6); null = unset (OSG default: REPEAT,
    /// LINEAR_MIPMAP_LINEAR, LINEAR, 1). Saving as .osgt / .osg writes them into the texture object (loaded or
    /// generated); OpenFlight keeps them in the image's .attr file, which is not changed (only the .attr copy of a baked or
    /// downscaled image written next to a .flt takes them).</summary>
    public static Result SetSampling(Scene scene, Texture tex, string? wrapS, string? wrapT, string? minFilter, string? magFilter,
        double? anisotropy, UndoHistory? history = null)
    {
        wrapS = Texture.NormMode(wrapS); wrapT = Texture.NormMode(wrapT);
        minFilter = Texture.NormMode(minFilter); magFilter = Texture.NormMode(magFilter);
        foreach (var wm in new[] { wrapS, wrapT })
            if (wm != null && !Texture.WrapModes.Contains(wm)) return No($"unknown wrap mode {wm} (one of {string.Join(", ", Texture.WrapModes)})");
        if (minFilter != null && !Texture.MinFilters.Contains(minFilter)) return No($"unknown minification filter {minFilter}");
        if (magFilter != null && !Texture.MagFilters.Contains(magFilter)) return No($"magnification filter {magFilter}: only NEAREST or LINEAR");
        if (anisotropy is double a && (!double.IsFinite(a) || a < 1)) return No("the anisotropy must be 1 or more");
        if (tex.WrapS == wrapS && tex.WrapT == wrapT && tex.MinFilter == minFilter && tex.MagFilter == magFilter && tex.MaxAnisotropy == anisotropy)
            return No("nothing changed");
        Push(history, scene, $"sampling of {tex.Label}");
        Node.Edited();
        tex.WrapS = wrapS; tex.WrapT = wrapT; tex.MinFilter = minFilter; tex.MagFilter = magFilter; tex.MaxAnisotropy = anisotropy;
        tex.SamplingEdited = true;
        string msg = $"{tex.Label}: wrap {tex.WrapText}, filter {tex.FilterText}";
        if (scene.Flt != null) msg += tex.Baked != null || tex.Resize != null
            ? " (for .flt: written into the .attr copy of the saved image, when its source image has an .attr file)"
            : " (not written for .flt: OpenFlight keeps these in the image's .attr file; saved as .osgt they are kept)";
        return new(true, msg);
    }

    /// <summary>The state set is written from the model (made by the optimiser), so its texture list can be edited;
    /// state sets read from a file are copied from the file when saving.</summary>
    public static bool IsGenerated(Scene scene, StateSet ss) =>
        ss.Src is not Obj && !(scene.Flt?.StateKeys.ContainsKey(ss) ?? false);

    /// <summary>Remove the texture(s) on <paramref name="unit"/> from the state set (the geometry's texcoord set stays).
    /// Only for generated state sets; others return "not supported yet".</summary>
    public static Result RemoveUnit(Scene scene, StateSet ss, int unit, UndoHistory? history = null)
    {
        if (!ss.Textures.Any(t => t.Unit == unit)) return No($"the state set has no texture on unit {unit}");
        if (!IsGenerated(scene, ss))
            return No("removing a texture unit from a state set read from the file is not supported yet (only from state sets the optimiser made)");
        Push(history, scene, $"remove texture unit {unit}");
        Node.Edited();
        ss.Textures = ss.Textures.Where(t => t.Unit != unit).ToList();
        ss.Summary.RemoveAll(s => s.Contains($"[{unit}]", StringComparison.Ordinal));
        foreach (var n in scene.IterNodes(externals: true)) if (ReferenceEquals(n.StateSet, ss)) n.Mark("stateset");
        return new(true, $"texture unit {unit} removed");
    }
}
