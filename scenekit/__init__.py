"""scenekit - load, inspect and optimise 3D scene graphs (OpenSceneGraph, OpenFlight) without Blender.

Main entry points::

    scene = scenekit.load_scene("city.osgt")
    report = scenekit.analyze.analyze(scene)
    scenekit.optimize.run(scene, ["weld", "share_duplicates", ...])
    scenekit.save_scene(scene, "city.optimized.osgt")

Only numpy is required. ``osgconv`` (OpenSceneGraph) is used when present for
formats without a built-in reader (.osgb, .ive, .osgx, ...) and for saving to them.
"""

import os
import shutil
import subprocess
import tempfile

from . import model

NATIVE_EXTS = {".osgt", ".osg", ".flt"}
OSGCONV_EXTS = {".osgb", ".ive", ".osgx", ".osgs", ".obj", ".3ds", ".dae", ".lwo", ".ac", ".x", ".ply", ".stl",
                ".3dc", ".dxf", ".shp", ".txp", ".iv", ".wrl", ".md2", ".bsp", ".mdl"}
SAVE_NATIVE = {".osgt", ".osg"}


class LoadOptions:
    def __init__(self, follow_proxy=True, follow_paged=False, max_depth=4, max_files=200, osgconv=None,
                 progress=None):
        self.follow_proxy = follow_proxy    # load ProxyNode / FLT external references
        self.follow_paged = follow_paged    # load PagedLOD child files (can be thousands for paged databases)
        self.max_depth = max_depth
        self.max_files = max_files
        self.osgconv = osgconv              # path of osgconv, '' to disable, None to look on PATH
        self.progress = progress or (lambda msg: None)


def find_osgconv(hint=None):
    if hint == "":
        return None
    for cand in (hint, os.environ.get("OSGCONV_PATH"), "osgconv"):
        if not cand:
            continue
        if os.path.isfile(cand):
            return cand
        found = shutil.which(cand)
        if found:
            return found
    return None


def osgconv_convert(exe, src, dst, timeout=600):
    env = dict(os.environ)
    env["OSG_OPTIMIZER"] = "OFF"   # we want the file as it is, not osgconv's optimised version
    env.setdefault("OSG_NOTIFY_LEVEL", "WARN")
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    res = subprocess.run([exe, src, dst], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                         timeout=timeout, env=env, creationflags=flags)
    out = res.stdout.decode("utf-8", "replace").strip()
    if res.returncode != 0 or not os.path.isfile(dst) or os.path.getsize(dst) == 0:
        raise RuntimeError(f"osgconv failed ({res.returncode}): {out[-400:]}")
    return out


def _load_file(path, opts):
    from . import osgread, osgtext
    ext = os.path.splitext(path)[1].lower()
    if ext in (".osgt", ".osg"):
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()
        opts.progress(f"parsing {os.path.basename(path)} ({len(text) / 1e6:,.0f} MB) ...")
        doc = osgtext.parse(text, osgtext.sniff_dialect(text), opts.progress)
        del text                        # the document holds everything; free the file text before building
        opts.progress("building the scene ...")
        scene = model.Scene(path, doc.dialect)
        osgread.read(doc, scene, opts.progress)
        if doc.dialect == "osgt" and doc.version and doc.version < 147:
            scene.warnings.append(f"old .osgt file version {doc.version}: saving writes the current format (version 161)")
        return scene
    if ext == ".flt":
        from . import fltread
        return fltread.read(path)
    exe = find_osgconv(opts.osgconv)
    if not exe:
        raise LookupError(f"No built-in reader for '{ext}'. Install OpenSceneGraph (osgconv) to inspect this format.")
    tmpdir = tempfile.mkdtemp(prefix="scenekit_")
    try:
        tmp = os.path.join(tmpdir, os.path.splitext(os.path.basename(path))[0] + ".osgt")
        opts.progress(f"converting {os.path.basename(path)} with osgconv ...")
        osgconv_convert(exe, path, tmp)
        scene = _load_file(tmp, opts)
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)
    scene.converted_from = path
    scene.path = path
    scene.fmt = ext.lstrip(".")
    scene.warnings.insert(0, f"read through osgconv ({os.path.basename(exe)}); textures are resolved relative to the original file")
    # textures were resolved relative to the temp dir: resolve again next to the original
    from . import images
    for n in scene.iter_nodes():
        for ss in ([n.stateset] if n.stateset else []):
            for t in ss.textures:
                if not t.embedded and not t.exists:
                    images.fill_texture(t, os.path.dirname(path))
    return scene


class no_gc:
    """Pause Python's cyclic garbage collector while millions of objects are created.

    Every full collection walks the whole heap; with the document of a 1 GB scene in memory that turns
    loading, analysing and optimising into minutes. Reference counting still frees everything."""
    depth = 0

    def __enter__(self):
        import gc
        no_gc.depth += 1
        if no_gc.depth == 1:
            self.was = gc.isenabled()
            gc.disable()
        return self

    def __exit__(self, *exc):
        import gc
        no_gc.depth -= 1
        if no_gc.depth == 0:
            if getattr(self, "was", True):
                gc.enable()
            release_memory()
        return False


_LIBC = []


def release_memory():
    """Give freed heap memory back to the operating system (glibc keeps it otherwise: after analysing
    a big scene the process would stay gigabytes larger than what it really uses). No-op elsewhere."""
    if not _LIBC:
        import ctypes
        import sys
        lib = None
        if sys.platform.startswith("linux"):
            try:
                lib = ctypes.CDLL("libc.so.6")
                lib.malloc_trim  # noqa: B018 - glibc only (musl has none)
            except (OSError, AttributeError):
                lib = None
        _LIBC.append(lib)
    if _LIBC[0] is not None:
        _LIBC[0].malloc_trim(0)


def load_scene(path, options=None):
    """Load a scene file and (optionally) its external references."""
    opts = options or LoadOptions()
    path = os.path.abspath(path)
    opts.progress(f"reading {os.path.basename(path)} ...")
    with no_gc():
        scene = _load_file(path, opts)
        _load_externals(scene, opts)
    return scene


def _load_externals(scene, opts):
    from . import images
    cache = {os.path.normcase(scene.path): scene}
    loaded = [0]

    def visit(sc, depth):
        for n in sc.iter_nodes():
            if not n.files:
                continue
            follow = opts.follow_paged if n.kind == model.PAGED_LOD else opts.follow_proxy
            ext_nodes = []
            for f in n.files:
                if not f:
                    continue
                base = os.path.dirname(sc.path)
                p = images.resolve(f, base)
                en = model.Node(model.EXTERNAL, "external file", os.path.basename(f))
                en.external = p or f
                if p is None:
                    en.notes.append("file not found")
                    sc.warnings.append(f"external file not found: {f}")
                elif follow and depth < opts.max_depth and loaded[0] < opts.max_files:
                    key = os.path.normcase(p)
                    sub = cache.get(key)
                    if sub is None and key not in cache:
                        cache[key] = None
                        try:
                            opts.progress(f"reading external {os.path.basename(p)} ...")
                            sub = _load_file(p, opts)
                            loaded[0] += 1
                            cache[key] = sub
                            scene.externals[p] = sub
                            visit(sub, depth + 1)
                        except Exception as exc:
                            en.notes.append(f"could not be read: {exc}")
                            scene.externals[p] = None
                    if sub is not None and sub.root is not None:
                        en.children = [sub.root]
                else:
                    en.notes.append("not loaded (paged child)" if n.kind == model.PAGED_LOD else "not loaded")
                ext_nodes.append(en)
            if ext_nodes:
                n.extra["externals"] = ext_nodes

    visit(scene, 0)
    if loaded[0] >= opts.max_files:
        scene.warnings.append(f"stopped loading external files after {opts.max_files}")


def _own_nodes(sc):
    return sc.iter_nodes() if sc.root is not None else []


def scene_modified(sc):
    """True when the optimiser / editor changed anything in this file (not counting its external files)."""
    for n in _own_nodes(sc):
        if n.dirty:
            return True
        if n.stateset is not None and any(getattr(t, "resize", None) for t in n.stateset.textures):
            return True
    return False


def _referenced(sc):
    """normcased paths of the external files ``sc`` references directly."""
    return {os.path.normcase(en.external) for n in _own_nodes(sc) for en in n.externals if en.external}


def plan_external_copies(scene, out_path):
    """Loaded external files that must be written as optimised copies: the ones that changed and every
    file that references one of those (its reference changes). -> {normcased source: (source, Scene, target)}"""
    subs = {}
    for p, sub in scene.externals.items():
        if sub is not None and sub.root is not None:
            subs.setdefault(os.path.normcase(p), (p, sub))
    changed = {k for k, (_, sub) in subs.items() if scene_modified(sub)}
    refs = {k: _referenced(sub) for k, (_, sub) in subs.items()}
    grew = True
    while grew:
        grew = False
        for k in subs:
            if k not in changed and refs[k] & changed:
                changed.add(k)
                grew = True
    from . import osgwrite
    folder = os.path.splitext(os.path.abspath(out_path))[0] + "_files"
    plan, used = {}, set()
    for k in sorted(changed):
        p, sub = subs[k]
        stem, ext = os.path.splitext(os.path.basename(p))[0], osgwrite.native_ext(sub)
        name, i = stem + ext, 2
        while name.lower() in used:
            name, i = f"{stem}_{i}{ext}", i + 1
        used.add(name.lower())
        plan[k] = (p, sub, os.path.join(folder, name))
    return plan


def _point_references(sc, sc_out, plan):
    """Make references in ``sc`` (written to ``sc_out``) point at the optimised copies in ``plan``."""
    from . import images
    touched = []
    base = os.path.dirname(sc.path)
    for n in _own_nodes(sc):
        if not n.files or not n.externals:
            continue
        mapping = {}
        for f in n.files:
            p = images.resolve(f, base) if f else None
            hit = plan.get(os.path.normcase(p)) if p else None
            if hit is not None:
                mapping[f] = os.path.relpath(hit[2], os.path.dirname(os.path.abspath(sc_out))).replace("\\", "/")
        if mapping:
            n.extra["files_out"] = mapping
            touched.append(n)
    return touched


def save_scene(scene, path, osgconv=None, externals=True):
    """Write the scene. The source's own text dialect (.osgt, or .osg for legacy files) is written
    natively; any other OSG format (.osgb, .ive, .osg <-> .osgt, .flt, ...) goes through osgconv.

    With ``externals`` loaded external files that were changed (by the optimiser or editing) are
    written as optimised copies into ``<output name>_files/`` and the references point there; unchanged
    external files keep pointing at the originals. ``scene.saved_externals`` lists the copies."""
    with no_gc():
        return _save_scene(scene, path, osgconv, externals)


def _save_scene(scene, path, osgconv, externals):
    from . import osgwrite
    plan = plan_external_copies(scene, path) if externals else {}
    touched = []
    scene.saved_externals = []
    try:
        touched += _point_references(scene, path, plan)
        for _, sub, target in plan.values():
            touched += _point_references(sub, target, plan)
        for _, sub, target in plan.values():
            os.makedirs(os.path.dirname(target), exist_ok=True)
            osgwrite.write(sub, target)
            scene.saved_externals.append(target)
        return _save_main(scene, path, osgconv)
    finally:
        for n in touched:
            n.extra.pop("files_out", None)


def _save_main(scene, path, osgconv):
    from . import osgwrite
    ext = os.path.splitext(path)[1].lower()
    native = osgwrite.native_ext(scene)
    if ext == native:
        osgwrite.write(scene, path)
        return path
    exe = find_osgconv(osgconv)
    if not exe:
        raise LookupError(f"Saving as '{ext}' needs OpenSceneGraph's osgconv; save as {native} instead.")
    # written next to the target so relative texture paths stay valid for osgconv
    tmp = os.path.join(os.path.dirname(os.path.abspath(path)), f".{os.path.basename(path)}.tmp{native}")
    try:
        osgwrite.write(scene, tmp)
        osgconv_convert(exe, tmp, path)
    finally:
        try:
            os.remove(tmp)
        except OSError:
            pass
    return path
