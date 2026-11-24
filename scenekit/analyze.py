"""What makes a scene big? Per-node cost roll-up and a list of findings with fixes.

Numbers per node (its whole subtree):

* ``tris / verts / draws`` - rendered load: shared subtrees count once per use,
  every LOD level and switch child is counted.
* ``vis_tris`` - worst case visible triangles: only the most detailed LOD child,
  only enabled switch children, nothing below node mask 0.
* ``geo_bytes / tex_bytes`` - memory: each shared geometry / texture counted once.

Findings point at nodes and name the optimizer pass that fixes them.
"""

import math

import numpy as np

from . import meshops
from . import model as M

DEFAULTS = {
    "heavy_tris": 100_000,          # a single geometry above this is "heavy"
    "heavy_share": 0.25,            # ... or above this share of the scene (and > 10k tris)
    "density_factor": 25.0,         # triangles per size compared with the median geometry
    "density_min_tris": 2_000,
    "large_texture": 2048,
    "many_drawables": 8,            # sibling drawables with the same state that could be merged
    "small_drawable_tris": 1_000,
    "many_primsets": 16,            # draw calls in one geometry
    "large_coord": 1e5,             # float32 precision drops to ~1 cm at 100 km
    "deep": 32,
    "weld_min_saving": 0.2,         # report when merging duplicate vertices saves at least this share
    "max_check_verts": 3_000_000,   # skip expensive per-vertex checks above this
    "acmr_min_tris": 10_000,
    "acmr_bad": 1.0,
}

SEVERITY_ORDER = {"high": 0, "medium": 1, "low": 2, "info": 3}


def human_bytes(n):
    n = float(n or 0)
    for unit in ("B", "KB", "MB", "GB"):
        if abs(n) < 1024 or unit == "GB":
            return f"{n:.0f} {unit}" if unit == "B" else f"{n:.1f} {unit}"
        n /= 1024.0
    return f"{n:.1f} GB"


def human_count(n):
    n = int(n or 0)
    if n >= 1_000_000:
        return f"{n / 1e6:.2f}M"
    if n >= 10_000:
        return f"{n / 1e3:.1f}k"
    return str(n)


class NodeStats:
    __slots__ = ("tris", "verts", "lines", "points", "draws", "vis_tris", "geoms", "nodes",
                 "geo_bytes", "tex_bytes", "bbox", "depth")

    def __init__(self):
        self.tris = self.verts = self.lines = self.points = self.draws = self.vis_tris = 0
        self.geoms = self.nodes = 0
        self.geo_bytes = self.tex_bytes = 0
        self.bbox = None
        self.depth = 0

    @property
    def bytes(self):
        return self.geo_bytes + self.tex_bytes


class GeoInfo:
    """Facts about one unique geometry."""
    __slots__ = ("node", "tris", "lines", "points", "verts", "vertex_bytes", "index_bytes", "draws", "instances",
                 "bbox", "size", "world_size", "dup_vertices", "degenerate", "unused", "acmr", "key", "max_coord",
                 "modes", "kinds", "index_types", "double_arrays", "const_color", "under_lod")

    def __init__(self, node):
        self.node = node
        self.dup_vertices = self.degenerate = self.unused = 0
        self.acmr = None
        self.key = None
        self.under_lod = False
        self.const_color = False

    @property
    def bytes(self):
        return self.vertex_bytes + self.index_bytes


class Finding:
    __slots__ = ("code", "severity", "title", "message", "nodes", "fix", "saving_bytes", "saving_tris",
                 "saving_draws", "textures", "scope")

    def __init__(self, code, severity, title, message, nodes=(), fix=None, saving_bytes=0, saving_tris=0,
                 saving_draws=0, textures=(), scope=None):
        self.code = code
        self.severity = severity
        self.title = title
        self.message = message
        self.nodes = list(nodes)
        self.fix = fix
        self.saving_bytes = int(saving_bytes)
        self.saving_tris = int(saving_tris)
        self.saving_draws = int(saving_draws)
        self.textures = list(textures)
        self.scope = list(scope) if scope is not None else None  # nodes the fix should run on (default: nodes)

    @property
    def fix_targets(self):
        return self.scope if self.scope is not None else self.nodes

    def as_dict(self, path_of=None):
        return {
            "code": self.code, "severity": self.severity, "title": self.title, "message": self.message,
            "fix": self.fix, "saving_bytes": self.saving_bytes, "saving_tris": self.saving_tris,
            "saving_draws": self.saving_draws,
            "nodes": [path_of(n) if path_of else (n.name or n.cls) for n in self.nodes[:50]],
            "textures": [t.label for t in self.textures[:50]],
        }


class Report:
    def __init__(self, scene):
        self.scene = scene
        self.stats = {}             # id(node) -> NodeStats
        self.instances = {}         # id(node) -> number of times it is drawn
        self.parents = {}           # id(node) -> [parents]
        self.geoms = []             # [GeoInfo] (unique geometries)
        self.geo_info = {}          # id(node) -> GeoInfo
        self.textures = []          # unique textures
        self.tex_users = {}         # id(texture) -> [nodes]
        self.findings = []
        self.settings = dict(DEFAULTS)
        self.node_count = 0
        self.max_depth = 0

    @property
    def total(self):
        return self.stats.get(id(self.scene.root)) or NodeStats()

    def stat(self, node):
        return self.stats.get(id(node)) or NodeStats()

    def path_of(self, node):
        """'root/child/...' for the first path to ``node``."""
        parts = []
        cur = node
        seen = set()
        while cur is not None and id(cur) not in seen:
            seen.add(id(cur))
            parts.append(cur.name or cur.cls.split("::")[-1])
            ps = self.parents.get(id(cur))
            cur = ps[0] if ps else None
        return "/".join(reversed(parts))

    def summary(self):
        t = self.total
        sc = self.scene
        bb = t.bbox
        size = (bb[1] - bb[0]).tolist() if bb is not None else None
        return {
            "file": sc.path, "format": sc.fmt, "version": sc.version, "nodes": self.node_count,
            "unique_geometries": len(self.geoms), "geometry_instances": t.geoms,
            "triangles": t.tris, "visible_triangles": t.vis_tris, "vertices": t.verts, "lines": t.lines,
            "points": t.points, "draw_calls": t.draws, "geometry_bytes": t.geo_bytes, "texture_bytes": t.tex_bytes,
            "textures": len(self.textures), "statesets": len({id(n.stateset) for n in self.all_nodes() if n.stateset}),
            "max_depth": self.max_depth, "size": size,
            "findings": {s: sum(1 for f in self.findings if f.severity == s) for s in SEVERITY_ORDER},
        }

    def all_nodes(self):
        seen = set()
        stack = [self.scene.root]
        while stack:
            n = stack.pop()
            if id(n) in seen:
                continue
            seen.add(id(n))
            yield n
            stack.extend(n.all_children())


def _mat_scale(Mx):
    if Mx is None:
        return 1.0
    return float(np.max(np.linalg.norm(Mx[:3, :3], axis=0))) or 1.0


def _transform_bbox(bb, Mx):
    if bb is None or Mx is None:
        return bb
    lo, hi = bb
    corners = np.array([[x, y, z] for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])])
    p = M.transform_points(Mx, corners)
    return p.min(axis=0), p.max(axis=0)


def _union(a, b):
    if a is None:
        return b
    if b is None:
        return a
    return np.minimum(a[0], b[0]), np.maximum(a[1], b[1])


def _add_id(acc, key, size):
    """acc: [dict id -> bytes, byte sum] of shared objects (None = empty)."""
    if acc is None:
        return [{key: size}, size]
    if key not in acc[0]:
        acc[0][key] = size
        acc[1] += size
    return acc


def _merge_ids(acc, other, may_take):
    """Union of two [dict, byte sum] pairs. ``other`` is reused when its owner no longer needs it
    (``may_take``); the smaller one is always merged into the larger one."""
    if not other:
        return acc
    if acc is None:
        return other if may_take else [dict(other[0]), other[1]]
    if may_take and len(other[0]) > len(acc[0]):
        acc, other = other, acc
    d = acc[0]
    for k, size in other[0].items():
        if k not in d:
            d[k] = size
            acc[1] += size
    return acc


def analyze(scene, settings=None, progress=None, deep_checks=True, findings=True):
    """Analyse ``scene`` -> Report. ``findings=False`` only computes the statistics (fast)."""
    from . import no_gc
    with no_gc():
        return _analyze(scene, settings, progress, deep_checks, findings)


def _analyze(scene, settings, progress, deep_checks, findings=True):
    progress = progress or (lambda msg: None)
    rep = Report(scene)
    if settings:
        rep.settings.update(settings)
    S = rep.settings
    root = scene.root
    if root is None:
        return rep

    # ---- topology: post order, parents, instance counts --------------------------------
    order = []          # post order (children before parents)
    state = {}
    stack = [(root, 0)]
    depth_of = {id(root): 0}
    while stack:
        n, d = stack.pop()
        if state.get(id(n)) == 2:
            continue
        if state.get(id(n)) == 1:
            state[id(n)] = 2
            order.append(n)
            continue
        state[id(n)] = 1
        stack.append((n, d))
        for c in n.all_children():
            rep.parents.setdefault(id(c), []).append(n)
            if state.get(id(c)) is None:
                depth_of[id(c)] = d + 1
                stack.append((c, d + 1))
            # (state 1 = cycle; ignore the back edge)
    rep.node_count = len(order)
    rep.max_depth = max(depth_of.values()) if depth_of else 0
    progress(f"{rep.node_count} nodes")

    # instance counts and world scale, top-down (reverse post order is a topological order)
    inst = {id(root): 1}
    scale = {id(root): _mat_scale(root.matrix)}
    under_lod = {id(root): False}
    for n in reversed(order):
        k = inst.get(id(n), 0)
        s = scale.get(id(n), 1.0)
        lod = under_lod.get(id(n), False) or n.kind in (M.LOD, M.PAGED_LOD)
        for c in n.all_children():
            inst[id(c)] = inst.get(id(c), 0) + k
            cs = s * _mat_scale(c.matrix)
            if cs > scale.get(id(c), 0):
                scale[id(c)] = cs
            under_lod[id(c)] = under_lod.get(id(c), False) or lod
    rep.instances = inst

    # ---- bottom-up statistics ----------------------------------------------------------
    # Memory of shared objects must count once per subtree. Geometry reached by a single path is simply
    # summed; only shared geometry and textures are tracked in id sets. The sets are merged small into
    # large and dropped once every parent has used them, so flat or huge scenes stay linear.
    tex_bytes = {}
    geo_bytes = {}
    plain_bytes = {}                     # id(node) -> bytes of unshared geometry below it
    gsets, tsets = {}, {}                # id(node) -> [set of ids, byte sum]
    pending = {k: len(v) for k, v in rep.parents.items()}   # parent references not processed yet
    for n in order:
        st = NodeStats()
        st.nodes = 1
        own_plain = 0
        gset = tset = None
        if n.stateset is not None:
            for t in n.stateset.textures:
                tex_bytes[id(t)] = t.gpu_bytes
                users = rep.tex_users.setdefault(id(t), [])
                if not users:
                    rep.textures.append(t)
                users.append(n)
                tset = _add_id(tset, id(t), t.gpu_bytes)
        if n.kind == M.GEOMETRY and n.geom is not None:
            g = n.geom
            gi = GeoInfo(n)
            tris, lines, points = g.counts()
            gi.tris, gi.lines, gi.points = tris, lines, points
            gi.verts = g.num_vertices
            gi.vertex_bytes = g.vertex_bytes
            gi.index_bytes = g.index_bytes
            gi.draws = g.draw_calls
            gi.instances = inst.get(id(n), 1)
            gi.bbox = g.bbox()
            gi.size = float(np.linalg.norm(gi.bbox[1] - gi.bbox[0])) if gi.bbox is not None else 0.0
            gi.world_size = gi.size * scale.get(id(n), 1.0)
            gi.max_coord = float(np.max(np.abs(np.concatenate(gi.bbox)))) if gi.bbox is not None else 0.0
            gi.modes = sorted({p.mode for p in g.prims})
            gi.kinds = sorted({p.kind for p in g.prims})
            gi.index_types = sorted({p.index_type for p in g.prims if p.index_type})
            gi.double_arrays = [name for name, a in g.arrays() if a.type.endswith("dArray") or a.type == "DoubleArray"]
            gi.under_lod = under_lod.get(id(n), False)
            rep.geoms.append(gi)
            rep.geo_info[id(n)] = gi
            geo_bytes[id(n)] = gi.bytes
            st.tris, st.lines, st.points = tris, lines, points
            st.verts = gi.verts
            st.draws = gi.draws
            st.vis_tris = tris if n.node_mask else 0
            st.geoms = 1
            if gi.instances > 1:
                gset = _add_id(gset, id(n), gi.bytes)
            else:
                own_plain = gi.bytes
            st.bbox = gi.bbox
        kids = n.all_children()
        vis_children = []
        if n.kind in (M.LOD, M.PAGED_LOD):
            vis_children = []  # handled below: max over children
        for i, c in enumerate(kids):
            cs = rep.stats[id(c)] if id(c) in rep.stats else NodeStats()
            st.tris += cs.tris
            st.verts += cs.verts
            st.lines += cs.lines
            st.points += cs.points
            st.draws += cs.draws
            st.geoms += cs.geoms
            st.nodes += cs.nodes
            if n.kind == M.SWITCH and n.switch_values is not None and i < len(n.switch_values) and not n.switch_values[i]:
                pass
            else:
                vis_children.append(cs.vis_tris)
            st.bbox = _union(st.bbox, _transform_bbox(cs.bbox, c.matrix))
            own_plain += plain_bytes.get(id(c), 0)
            last = pending.get(id(c), 0) == 1   # this is the child's last parent: its sets can be reused
            gset = _merge_ids(gset, gsets.get(id(c)), last)
            tset = _merge_ids(tset, tsets.get(id(c)), last)
        if vis_children:
            st.vis_tris += max(vis_children) if n.kind in (M.LOD, M.PAGED_LOD) else sum(vis_children)
        if not n.node_mask:
            st.vis_tris = 0
        plain_bytes[id(n)] = own_plain
        st.geo_bytes = own_plain + (gset[1] if gset else 0)
        st.tex_bytes = tset[1] if tset else 0
        if gset:
            gsets[id(n)] = gset
        if tset:
            tsets[id(n)] = tset
        for c in kids:                   # children whose parents are all done: free their sets
            k = id(c)
            left = pending.get(k, 0) - 1
            pending[k] = left
            if left <= 0:
                gsets.pop(k, None)
                tsets.pop(k, None)
                plain_bytes.pop(k, None)
        st.depth = depth_of.get(id(n), 0)
        rep.stats[id(n)] = st
    progress("statistics done")
    if findings:
        _findings(rep, S, deep_checks, progress)
    rep.findings.sort(key=lambda f: (SEVERITY_ORDER.get(f.severity, 9), -(f.saving_bytes + f.saving_tris * 40)))
    return rep


# ------------------------------------------------------------------------------------ findings

def _small_checks(gis):
    """Duplicate vertices, degenerate triangles and unused vertices of many small geometries at once
    (same results as the per-geometry checks). Returns the ids of the GeoInfos that were done."""
    done = set()
    stats = meshops.batch_stats([g.node.geom for g in gis])
    for g, st in zip(gis, stats):
        if st is None:
            continue
        g.dup_vertices = st["dup_vertices"]
        g.degenerate = st["degenerate"]
        g.unused = st["unused"]
        geom = g.node.geom
        if geom.colors is not None and geom.colors.binding == "vertex" and len(geom.colors.data) > 1:
            c = np.asarray(geom.colors.data)
            g.const_color = bool(np.all(c == c[0]))
        done.add(id(g))
    return done


def _sev(share, high=0.10, medium=0.02):
    return "high" if share >= high else ("medium" if share >= medium else "low")


def _findings(rep, S, deep_checks, progress):
    F = rep.findings
    tot = rep.total
    total_tris = max(tot.tris, 1)
    total_bytes = max(tot.bytes, 1)
    total_draws = max(tot.draws, 1)
    scene_diag = float(np.linalg.norm(tot.bbox[1] - tot.bbox[0])) if tot.bbox is not None else 0.0
    editable_geoms = [g for g in rep.geoms if g.node.geom is not None]

    # --- heavy geometry / LOD suggestions
    heavy = [g for g in editable_geoms if g.tris * g.instances >= S["heavy_tris"]
             or (g.tris * g.instances >= 10_000 and g.tris * g.instances / total_tris >= S["heavy_share"])]
    heavy.sort(key=lambda g: -g.tris * g.instances)
    for g in heavy[:25]:
        share = g.tris * g.instances / total_tris
        inst = f" (drawn {g.instances}x)" if g.instances > 1 else ""
        F.append(Finding("heavy_geometry", _sev(share, 0.25, 0.05),
                         f"Heavy geometry: {human_count(g.tris)} triangles{inst}",
                         f"{rep.path_of(g.node)} holds {share:.0%} of all triangles. Simplify it (e.g. to 50 %) or, "
                         + ("it is already inside a LOD." if g.under_lod else "add LOD levels so the full detail is only drawn up close."),
                         [g.node], fix="simplify" if g.under_lod else "generate_lod",
                         saving_tris=g.tris * g.instances // 2))

    # --- over-tessellated: many triangles for a small object
    cands = [g for g in editable_geoms if g.tris >= 50 and g.world_size > 0]
    if scene_diag > 0 and len(cands) >= 3:
        dens = np.array([g.tris / max(g.world_size / scene_diag, 1e-9) ** 2 for g in cands])
        med = float(np.median(dens))
        for g, d in sorted(zip(cands, dens), key=lambda x: -x[1])[:30]:
            if g.tris >= S["density_min_tris"] and med > 0 and d > S["density_factor"] * med:
                rel = g.world_size / scene_diag
                rel_txt = f"{rel:.1%}" if rel >= 0.001 else "<0.1%"
                F.append(Finding("over_tessellated", _sev(g.tris * g.instances / total_tris, 0.1, 0.01),
                                 f"Small but detailed: {human_count(g.tris)} triangles at {rel_txt} of the scene size",
                                 f"{rep.path_of(g.node)} has {d / med:,.0f}x more triangles per area than a typical object in this scene. "
                                 "Simplifying it rarely shows.", [g.node], fix="simplify",
                                 saving_tris=int(g.tris * 0.75) * g.instances))

    # --- duplicated geometry that could be shared (instancing)
    groups = {}
    if deep_checks:
        progress("looking for duplicate geometry ...")
        for g in editable_geoms:
            if g.verts and g.verts <= S["max_check_verts"]:
                g.key = (g.node.geom.content_key(), id(g.node.stateset) if g.node.stateset else None)
                groups.setdefault(g.key, []).append(g)
    for key, gs in groups.items():
        if len(gs) < 2:
            continue
        saving = sum(g.bytes for g in gs[1:])
        F.append(Finding("duplicate_geometry", _sev(saving / total_bytes),
                         f"{len(gs)} identical copies of a geometry ({human_count(gs[0].tris)} tris each)",
                         f"The same vertex data is stored {len(gs)} times. Sharing one copy saves {human_bytes(saving)}.",
                         [g.node for g in gs], fix="share_duplicates", saving_bytes=saving))

    # --- per geometry data checks (small meshes in batches: one numpy pass for thousands of them)
    batched = set()
    if deep_checks:
        small = [g for g in editable_geoms if g.node.geom.optimizable and g.node.geom.prims and 0 < g.verts <= 4096]
        if small:
            progress(f"checking {len(small):,} small geometries ...")
            batched = _small_checks(small)
    for g in editable_geoms:
        n = g.node
        geom = n.geom
        if not geom.optimizable:
            continue
        if not geom.prims:
            F.append(Finding("empty_geometry", "low", "Geometry without primitives",
                             f"{rep.path_of(n)} has {g.verts} vertices but draws nothing.", [n], fix="remove_empty",
                             saving_bytes=g.bytes, scope=rep.parents.get(id(n), [])))
            continue
        if deep_checks and 0 < g.verts <= S["max_check_verts"] and id(g) not in batched:
            mat, _ = meshops.vertex_matrix(geom)
            first, _ = meshops.unique_rows(mat)
            g.dup_vertices = g.verts - len(first)
            tris = geom.triangles()
            if len(tris):
                g.degenerate = int(np.count_nonzero(meshops.degenerate_mask(geom.vertices.data, tris)))
            g.unused = int(np.count_nonzero(~meshops.referenced(geom)))
            if g.tris >= S["acmr_min_tris"] and g.tris <= 400_000 and "elements" in g.kinds:
                g.acmr = meshops.acmr(tris)
            if geom.colors is not None and geom.colors.binding == "vertex" and len(geom.colors.data) > 1:
                c = np.asarray(geom.colors.data)
                g.const_color = bool(np.all(c == c[0]))
        share_dup = g.dup_vertices / max(g.verts, 1)
        if g.dup_vertices and share_dup >= S["weld_min_saving"]:
            per_vertex = g.vertex_bytes / max(g.verts, 1)
            saving = int(g.dup_vertices * per_vertex)
            unindexed = "arrays" in g.kinds or "lengths" in g.kinds
            F.append(Finding("duplicate_vertices", _sev(saving / total_bytes),
                             f"{share_dup:.0%} duplicate vertices" + (" (not indexed)" if unindexed else ""),
                             f"{rep.path_of(n)}: {human_count(g.dup_vertices)} of {human_count(g.verts)} vertices are exact copies. "
                             "Merging them (and drawing with an index list) saves memory and vertex work.",
                             [n], fix="weld", saving_bytes=saving))
        if g.degenerate:
            F.append(Finding("degenerate_triangles", "low" if g.degenerate < 1000 else "medium",
                             f"{human_count(g.degenerate)} degenerate triangles",
                             f"{rep.path_of(n)}: triangles with zero area or a repeated corner are drawn for nothing.",
                             [n], fix="clean", saving_tris=g.degenerate * g.instances))
        if g.unused and g.unused >= max(16, g.verts * 0.05):
            F.append(Finding("unused_vertices", "low", f"{human_count(g.unused)} unused vertices",
                             f"{rep.path_of(n)}: vertices that no primitive references.", [n], fix="clean",
                             saving_bytes=int(g.unused * g.vertex_bytes / max(g.verts, 1))))
        if "DrawElementsUInt" in g.index_types and g.verts <= 65536:
            idx_count = sum(len(p.indices) for p in geom.prims if p.kind == "elements")
            F.append(Finding("index_type", "low", "32-bit indices for a small mesh",
                             f"{rep.path_of(n)}: {g.verts} vertices fit in 16-bit indices.", [n], fix="triangulate",
                             saving_bytes=idx_count * 2))
        if g.draws >= S["many_primsets"]:
            F.append(Finding("many_primitive_sets", _sev(g.draws / total_draws, 0.2, 0.05),
                             f"{g.draws} draw calls in one geometry",
                             f"{rep.path_of(n)} draws {len(geom.prims)} primitive set(s) / strips one by one. "
                             "One indexed triangle list draws it in a single call.", [n], fix="triangulate",
                             saving_draws=(g.draws - 1) * g.instances))
        elif any(m in ("QUADS", "QUAD_STRIP", "POLYGON") for m in g.modes):
            F.append(Finding("legacy_primitives", "info", "Quads / polygons",
                             f"{rep.path_of(n)} uses {', '.join(m for m in g.modes if m in ('QUADS', 'QUAD_STRIP', 'POLYGON'))}, "
                             "which modern (core profile) OpenGL cannot draw directly.", [n], fix="triangulate"))
        if g.acmr is not None and g.acmr > S["acmr_bad"]:
            F.append(Finding("vertex_cache", "low", f"Poor vertex cache order (ACMR {g.acmr:.2f})",
                             f"{rep.path_of(n)}: reordering the triangles typically brings this to ~0.7 and makes drawing faster.",
                             [n], fix="vertex_cache"))
        if g.const_color:
            F.append(Finding("constant_colors", "low", "Per-vertex colour array with a single colour",
                             f"{rep.path_of(n)}: one overall colour is enough.", [n], fix="compact_arrays",
                             saving_bytes=geom.colors.nbytes))
        if g.max_coord >= S["large_coord"]:
            F.append(Finding("large_coordinates", "medium",
                             f"Vertex coordinates up to {g.max_coord:,.0f}",
                             f"{rep.path_of(n)} stores far-from-origin coordinates in its vertices. 32-bit floats only keep "
                             f"~{g.max_coord * 6e-8:.3g} units precision there (jitter, z-fighting). Moving the offset into a "
                             "MatrixTransform fixes it.", [n], fix="recenter"))
        if g.double_arrays:
            saving = sum(a.nbytes // 2 for name, a in geom.arrays() if name in g.double_arrays)
            F.append(Finding("double_arrays", "low", "Double precision vertex arrays",
                             f"{rep.path_of(n)}: {', '.join(g.double_arrays)} use 64-bit floats. GPUs draw 32-bit floats; "
                             "re-center first if the coordinates are large.", [n], fix="float_arrays", saving_bytes=saving))

    # --- many small drawables with the same state under one parent
    for n in rep.all_nodes():
        geos = [c for c in n.children if c.kind == M.GEOMETRY and c.geom is not None and c.geom.optimizable]
        geos += [c.children[0] for c in n.children
                 if c.kind == M.GEODE and len(c.children) == 1 and c.matrix is None
                 and c.children[0].kind == M.GEOMETRY and c.children[0].geom is not None and c.children[0].geom.optimizable
                 and len(rep.parents.get(id(c), [])) == 1]
        if len(geos) < S["many_drawables"]:
            continue
        by_state = {}
        for c in geos:
            gi = rep.geo_info.get(id(c))
            if gi is not None and gi.tris < S["small_drawable_tris"]:
                holder = rep.parents.get(id(c), [None])[0]
                hss = holder.stateset if holder is not None and holder is not n else None
                by_state.setdefault((id(c.stateset) if c.stateset else None, id(hss) if hss else None), []).append(c)
        for key, cs in by_state.items():
            if len(cs) >= S["many_drawables"]:
                F.append(Finding("many_small_drawables", _sev(len(cs) / total_draws, 0.2, 0.05),
                                 f"{len(cs)} small drawables with the same state",
                                 f"{rep.path_of(n)}: {len(cs)} geometries under 1k triangles each are drawn one by one. "
                                 "Merging them reduces draw calls.", cs, fix="merge_geometries",
                                 saving_draws=len(cs) - 1, scope=[n]))

    # --- textures
    by_path = {}
    for t in rep.textures:
        users = rep.tex_users.get(id(t), [])
        if not t.exists and not t.embedded and t.file:
            F.append(Finding("missing_texture", "high", f"Missing texture {t.file}",
                             f"Used by {len(users)} node(s); the image could not be found next to the scene.",
                             users, textures=[t]))
            continue
        if t.path:
            by_path.setdefault(t.path, []).append(t)
        if max(t.width, t.height) > S["large_texture"]:
            saving = t.gpu_bytes - t.gpu_bytes // 4
            F.append(Finding("large_texture", _sev(saving / total_bytes, 0.1, 0.02),
                             f"Large texture {t.width}x{t.height} ({human_bytes(t.gpu_bytes)} on the GPU)",
                             f"{t.label}: halving the resolution saves {human_bytes(saving)}.", users,
                             fix="downscale_textures", saving_bytes=saving, textures=[t]))
        if t.width and t.height and (t.width & (t.width - 1) or t.height & (t.height - 1)):
            F.append(Finding("npot_texture", "info", f"Non power-of-two texture {t.width}x{t.height}",
                             f"{t.label}: OpenSceneGraph rescales such images when loading (slower load, blurrier).",
                             users, textures=[t]))
        if t.embedded and t.file_bytes > 256 * 1024:
            F.append(Finding("embedded_texture", "info", f"Embedded image data ({human_bytes(t.file_bytes)})",
                             f"{t.label} is stored inside the scene file, which makes the text file large and slow to parse.",
                             users, textures=[t]))
    for path, ts in by_path.items():
        if len(ts) > 1:
            saving = sum(t.gpu_bytes for t in ts[1:])
            users = [u for t in ts for u in rep.tex_users.get(id(t), [])]
            F.append(Finding("duplicate_texture", _sev(saving / total_bytes), f"Image loaded {len(ts)} times",
                             f"{ts[0].label} is referenced by {len(ts)} separate texture objects, each uploading its own copy.",
                             users, fix="share_duplicates", saving_bytes=saving, textures=ts))

    # --- duplicate state sets
    ss_groups = {}
    for n in rep.all_nodes():
        if n.stateset is not None and n.stateset.src is not None:
            ss_groups.setdefault(_stateset_signature(n.stateset), {})[id(n.stateset)] = n.stateset
    dup_ss = sum(len(v) - 1 for v in ss_groups.values() if len(v) > 1)
    if dup_ss >= 4:
        F.append(Finding("duplicate_statesets", "low", f"{dup_ss} duplicate state sets",
                         "Identical materials/texture settings exist as separate objects; sharing them lets OSG sort by state "
                         "and switch state less often.", [], fix="share_duplicates"))

    # --- structure
    for n in rep.all_nodes():
        st = rep.stats.get(id(n))
        if st is None:
            continue
        if not n.node_mask and st.tris:
            F.append(Finding("hidden_subtree", _sev(st.tris / total_tris), f"Hidden subtree ({human_count(st.tris)} tris)",
                             f"{rep.path_of(n)} has node mask 0: it is loaded but never drawn.", [n], fix="remove_hidden",
                             saving_bytes=st.bytes, saving_tris=st.tris, scope=rep.parents.get(id(n), [])))
        if n.kind in (M.LOD,) and len(n.children) >= 2:
            ts = [rep.stats[id(c)].tris for c in n.children if id(c) in rep.stats]
            if ts and max(ts) > 1000 and min(ts) >= 0.7 * max(ts):
                F.append(Finding("ineffective_lod", "medium", "LOD levels with almost the same detail",
                                 f"{rep.path_of(n)}: the coarsest level still has {min(ts) / max(ts):.0%} of the triangles "
                                 "of the finest one.", [n], fix=None, saving_tris=max(ts) - min(ts) // 2))
        if n.kind == M.GROUP and not n.children and not n.externals and n is not rep.scene.root:
            F.append(Finding("empty_node", "info", "Empty group", f"{rep.path_of(n)} has no children.", [n],
                             fix="remove_empty", scope=rep.parents.get(id(n), [])))
        if n.kind == M.GEODE and not n.children:
            F.append(Finding("empty_node", "info", "Empty geode", f"{rep.path_of(n)} has no drawables.", [n],
                             fix="remove_empty", scope=rep.parents.get(id(n), [])))
        if n.kind == M.TRANSFORM and n.matrix is not None and np.allclose(n.matrix, np.eye(4)) and n.cls.endswith("Transform"):
            F.append(Finding("identity_transform", "info", "Identity transform",
                             f"{rep.path_of(n)} does not move anything.", [n], fix="remove_redundant"))
        for en in n.externals:
            if "file not found" in en.notes:
                F.append(Finding("missing_external", "high", f"Missing external file {en.name}",
                                 f"{rep.path_of(n)} references {en.external}, which does not exist.", [n]))
    # collapse the many 'info' empty/identity findings into one each
    _collapse(F, ("empty_node", "identity_transform", "npot_texture", "unused_vertices", "index_type", "duplicate_vertices",
                  "constant_colors", "legacy_primitives", "vertex_cache", "double_arrays", "degenerate_triangles"))
    single = [n for n in rep.all_nodes() if n.kind == M.GROUP and len(n.children) == 1 and n.stateset is None
              and n.matrix is None and n.cls in ("osg::Group", "Group") and not n.externals
              and n is not rep.scene.root and len(rep.parents.get(id(n), [])) == 1]
    if len(single) >= 4:
        F.append(Finding("redundant_groups", "info", f"{len(single)} groups with a single child",
                         "They add traversal work without grouping anything.", single, fix="remove_redundant"))
    if rep.max_depth > S["deep"]:
        F.append(Finding("deep_hierarchy", "info", f"Deep hierarchy ({rep.max_depth} levels)",
                         "Every level costs a little traversal and culling time.", [], fix="remove_redundant"))


def _collapse(F, codes):
    for code in codes:
        items = [f for f in F if f.code == code]
        if len(items) <= 3:
            continue
        for f in items:
            F.remove(f)
        worst = min(items, key=lambda f: SEVERITY_ORDER.get(f.severity, 9))
        nodes = [n for f in items for n in f.nodes]
        tex = [t for f in items for t in f.textures]
        scope = None
        if any(f.scope is not None for f in items):
            scope = [n for f in items for n in f.fix_targets]
        F.append(Finding(code, worst.severity, f"{len(items)}x {items[0].title.split(':')[0]}",
                         f"{len(items)} places, e.g. {items[0].message}", nodes, fix=items[0].fix,
                         saving_bytes=sum(f.saving_bytes for f in items), saving_tris=sum(f.saving_tris for f in items),
                         saving_draws=sum(f.saving_draws for f in items), textures=tex, scope=scope))


def _stateset_signature(ss):
    tex = tuple((t.unit, t.path or t.file) for t in ss.textures)
    src = ss.src
    body = None
    if src is not None and hasattr(src, "stmts"):
        try:
            from .osgtext import Writer
            w = Writer("osgt")
            # ids differ between copies: compare everything but object ids
            w.write_stmts(src.stmts, 0)
            body = "\n".join(line for line in w.out if "UniqueID" not in line)
        except Exception:
            body = None
    return (tex, ss.material_name, ss.diffuse, tuple(sorted(ss.modes.items())), ss.transparent, body)


def stateset_signature(ss):
    return _stateset_signature(ss)


def top_nodes(rep, key="tris", limit=20, kinds=None):
    """Nodes with the highest *own subtree* cost that are not just ancestors of a bigger child."""
    items = []
    for n in rep.all_nodes():
        if kinds and n.kind not in kinds:
            continue
        st = rep.stats.get(id(n))
        if st is None:
            continue
        items.append((getattr(st, key) if key != "bytes" else st.bytes, n))
    items.sort(key=lambda x: -x[0])
    return items[:limit]


def fmt_ratio(a, b):
    return f"{a / b:.1%}" if b else "-"


def log2_ceil(x):
    return 1 << max(0, math.ceil(math.log2(max(1, x))))
