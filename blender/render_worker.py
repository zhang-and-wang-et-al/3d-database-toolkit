"""Blender-side worker: import ONE model, analyse it, render showcase images.

Run by the batch runner, never directly by users::

    blender -b --python render_worker.py -- \
        --input  /models/chair.fbx \
        --out-base /models/chair.fbx \
        --result /tmp/chair.result.json \
        --settings /tmp/settings.json

It also works with the ``bpy`` pip module (``python render_worker.py -- ...``).

Writes ``<out-base>.preview.<ext>`` (hero render) and
``<out-base>.showcase.<ext>`` (contact sheet with an info banner), and a
partial record (JSON) to ``--result``. The runner adds file information and
writes the final ``.info.json`` / ``.info.txt`` files.
"""

import argparse
import json
import math
import os
import shutil
import sys
import tempfile
import time
import traceback

import bpy
import numpy as np
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from showcase import __version__ as TOOL_VERSION  # noqa: E402
from showcase import schema  # noqa: E402
import sc_importers  # noqa: E402
from sc_importers import converters  # noqa: E402

GEOMETRY_TYPES = {"MESH", "CURVE", "SURFACE", "META", "FONT", "CURVES", "POINTCLOUD", "VOLUME"}
CONVERTIBLE_TYPES = {"CURVE", "SURFACE", "META", "FONT"}  # evaluated to_mesh() works

# Formats without reliable axis information: "auto" orientation may rotate these.
# Everything else (FBX, glTF, USD, FLT, DAE, ...) is trusted to be upright already.
AXIS_UNKNOWN = {".obj", ".stl", ".ply", ".off", ".3mf", ".amf", ".vtk", ".dxf"}

# extension -> list of (operator idname, extra kwargs); first available wins.
IMPORTERS = {
    ".obj": [("wm.obj_import", {}), ("import_scene.obj", {})],
    ".fbx": [("import_scene.fbx", {})],
    ".gltf": [("import_scene.gltf", {})],
    ".glb": [("import_scene.gltf", {})],
    ".stl": [("wm.stl_import", {}), ("import_mesh.stl", {})],
    ".ply": [("wm.ply_import", {}), ("import_mesh.ply", {})],
    ".dae": [("wm.collada_import", {})],
    ".abc": [("wm.alembic_import", {})],
    ".usd": [("wm.usd_import", {})],
    ".usda": [("wm.usd_import", {})],
    ".usdc": [("wm.usd_import", {})],
    ".usdz": [("wm.usd_import", {})],
    ".x3d": [("import_scene.x3d", {})],
    ".wrl": [("import_scene.x3d", {})],
    ".3ds": [("import_scene.max3ds", {}), ("import_scene.autodesk_3ds", {})],
    # formats that only import through optional add-ons / extensions
    ".dxf": [("import_scene.dxf", {})],
    ".3mf": [("import_mesh.threemf", {})],
    ".3dm": [("import_3dm.some_data", {})],
    ".lwo": [("import_scene.lwo", {})],
    ".ms3d": [("import_scene.ms3d", {})],
    ".x": [("import_scene.x", {})],
    ".ac": [("import_scene.import_ac3d", {}), ("import_scene.ac3d", {})],
    ".md2": [("import_scene.md2", {})],
    ".md3": [("import_scene.md3", {})],
    ".b3d": [("import_scene.blitz3d_b3d", {})],
    ".pmx": [("mmd_tools.import_model", {})],
    ".pmd": [("mmd_tools.import_model", {})],
    ".vrm": [("import_scene.vrm", {})],
}

TRI_SAMPLE_TARGET = 300_000   # triangles kept for orientation / view analysis
POINT_SAMPLE_TARGET = 60_000  # vertices kept for camera framing
DUPLICATE_CHECK_MAX = 5_000_000
CHUNK = 1_000_000

DEFAULT_SETTINGS = {
    "engine": "auto",            # auto | eevee | cycles | workbench
    "samples": 32,
    "resolution": 640,
    "views": ["hero", "front", "side", "back", "top", "wire"],
    "image_format": "JPEG",      # JPEG | PNG
    "jpeg_quality": 88,
    "orientation": "auto",       # auto | importer | detect
    "ground": True,
    "fix_textures": True,
    "gpu": True,
    "poly_tiers": [1_000, 10_000, 100_000, 1_000_000],
    "wire_max_faces": 400_000,
    "cycles_time_limit": 0,      # seconds per view, 0 = unlimited
    "background": "light",       # light | dark
    "assimp_path": "",           # optional converters (auto-detected on PATH)
    "osgconv_path": "",
    "converter_timeout": 300,
}


def log(msg):
    print(f"SHOWCASE: {msg}", flush=True)


# ---------------------------------------------------------------------------
# Scene preparation / import
# ---------------------------------------------------------------------------

def op_available(idname):
    mod, name = idname.split(".", 1)
    try:
        getattr(getattr(bpy.ops, mod), name).get_rna_type()
        return True
    except Exception:
        return False


def get_op(idname):
    mod, name = idname.split(".", 1)
    return getattr(getattr(bpy.ops, mod), name)


def clear_scene():
    ids = []
    for coll in (bpy.data.objects, bpy.data.meshes, bpy.data.materials, bpy.data.images,
                 bpy.data.textures, bpy.data.curves, bpy.data.armatures, bpy.data.actions,
                 bpy.data.cameras, bpy.data.lights, bpy.data.node_groups, bpy.data.collections,
                 bpy.data.worlds):
        ids.extend(coll)
    if ids:
        bpy.data.batch_remove(ids)


def _short(exc):
    return f"{type(exc).__name__}: {str(exc).strip().splitlines()[-1][:300] if str(exc).strip() else ''}"


def import_model(path, ext, settings, result):
    """Try, in order: Blender importers (built-in or installed add-ons), the
    built-in Python readers, then external converters (assimp / osgconv).

    Returns (importer description, format details, errors of earlier attempts). Raises LookupError when no
    reader exists for the format, RuntimeError when every reader failed.
    """
    if ext == ".blend":
        bpy.ops.wm.open_mainfile(filepath=path, load_ui=False, use_scripts=False)
        return "wm.open_mainfile", {}, []
    errors = []
    attempted = False
    unsupported_variant = None
    for idname, kwargs in IMPORTERS.get(ext, []):
        if not op_available(idname):
            continue
        attempted = True
        try:
            res = get_op(idname)(filepath=path, **kwargs)
            if "FINISHED" in res:
                return idname, {}, []
            errors.append(f"{idname}: returned {sorted(res)}")
        except Exception as exc:
            errors.append(f"{idname}: {_short(exc)}")
        clear_scene()
        break
    loader = sc_importers.BUILTIN.get(ext)
    if loader is not None:
        attempted = True
        ctx = sc_importers.Context(log, lambda code, msg=None: schema.add_issue(result, code, msg))
        try:
            details = loader(path, ctx) or {}
            return f"built-in {ext.lstrip('.')} reader", details, errors
        except NotImplementedError as exc:  # a variant of the format the reader does not handle
            unsupported_variant = str(exc)
            errors.append(f"built-in reader: {exc}")
            clear_scene()
        except Exception as exc:
            errors.append(f"built-in reader: {_short(exc)}")
            log(f"built-in reader failed: {_short(exc)}")
            clear_scene()
    tools = converters.available(settings)
    conv = converters.candidates(ext, tools)
    if unsupported_variant and not conv and len(errors) == 1:
        tool = converters.needed_tool(ext)
        raise LookupError(f"{unsupported_variant} ({converters.HINTS.get(tool, 'no other reader available')}).")
    no_reader = []
    for tool in conv:
        attempted = True
        log(f"trying {tool}")
        try:
            desc = converters.convert_and_import(tool, tools[tool], path, int(settings.get("converter_timeout", 300)))
            return desc, {}, errors
        except converters.NoReader as exc:
            no_reader.append(str(exc))
            clear_scene()
        except Exception as exc:
            errors.append(f"{tool}: {_short(exc)}")
            clear_scene()
    if no_reader and not errors:  # nobody could even open this file type: unsupported, not broken
        raise LookupError(f"No reader for '{ext}' in this setup ({'; '.join(no_reader)}).")
    errors.extend(no_reader)
    if not attempted:
        tool = converters.needed_tool(ext)
        hint = converters.HINTS.get(tool, "enable a matching Blender add-on/extension")
        raise LookupError(f"No reader for '{ext}' in this setup ({hint}).")
    raise RuntimeError(" | ".join(errors) or "import failed")


def render_objects(view_layer):
    return [o for o in view_layer.objects if not o.hide_render]


# ---------------------------------------------------------------------------
# Textures
# ---------------------------------------------------------------------------

def image_file_path(img):
    path = bpy.path.abspath(img.filepath, library=img.library)
    if img.source == "TILED":
        path = path.replace("<UDIM>", "1001").replace("<UVTILE>", "u1_v1")
    return os.path.normpath(path)


def is_missing(img):
    if img.source not in {"FILE", "SEQUENCE", "TILED"} or img.packed_file is not None:
        return False
    if not img.filepath:
        return not img.has_data
    return not os.path.exists(image_file_path(img))


def build_file_index(model_dir, max_files=5000):
    """basename(lower) -> path for files near the model (textures often live in subfolders)."""
    index = {}
    count = 0

    def walk(top, max_depth):
        nonlocal count
        top_depth = top.rstrip(os.sep).count(os.sep)
        for root, dirs, files in os.walk(top):
            if root.count(os.sep) - top_depth >= max_depth:
                dirs[:] = []
            for f in files:
                index.setdefault(f.lower(), os.path.join(root, f))
                count += 1
                if count >= max_files:
                    return

    walk(model_dir, 4)
    parent = os.path.dirname(model_dir)
    if parent and parent != model_dir and count < max_files:
        try:
            for d in os.listdir(parent):
                full = os.path.join(parent, d)
                if os.path.isdir(full) and any(k in d.lower() for k in ("tex", "map", "image", "material", "img")):
                    walk(full, 2)
        except OSError:
            pass
    return index


def relink_textures(model_dir, missing_imgs):
    index = build_file_index(model_dir)
    relinked = []
    for img in missing_imgs:
        base = os.path.basename(img.filepath.replace("\\", "/")).lower()
        found = index.get(base)
        if not found:
            # same stem, different extension (e.g. .tga referenced, .png shipped)
            stem = os.path.splitext(base)[0]
            for k, v in index.items():
                if os.path.splitext(k)[0] == stem and os.path.splitext(k)[1] in (".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".bmp", ".exr", ".webp", ".dds"):
                    found = v
                    break
        if found:
            img.filepath = found
            try:
                img.reload()
            except Exception:
                pass
            relinked.append(img.name)
    return relinked


def material_images(mat):
    """Images used by a material, following node groups."""
    found = set()
    seen = set()

    def visit(tree):
        if tree is None or tree.name in seen:
            return
        seen.add(tree.name)
        for node in tree.nodes:
            if node.type == "TEX_IMAGE" and node.image is not None:
                found.add(node.image.name)
            elif node.type == "GROUP":
                visit(node.node_tree)

    if mat is not None and mat.node_tree is not None:
        visit(mat.node_tree)
    return found


# ---------------------------------------------------------------------------
# Geometry analysis (vectorised with numpy)
# ---------------------------------------------------------------------------

def mesh_arrays(me):
    nv = len(me.vertices)
    co = np.empty(nv * 3, np.float32)
    me.vertices.foreach_get("co", co)
    return co.reshape(-1, 3).astype(np.float64)


def unique_rows(q):
    q = np.ascontiguousarray(q)
    v = q.view(np.dtype((np.void, q.dtype.itemsize * q.shape[1])))
    return len(np.unique(v))


class GeometryAccumulator:
    def __init__(self, total_tris_estimate, rng):
        self.rng = rng
        self.sample_p = min(1.0, TRI_SAMPLE_TARGET / max(1, total_tris_estimate))
        self.totals = dict(vertices=0, edges=0, faces=0, triangles=0, tri_faces=0, quads=0, ngons=0,
                           boundary_edges=0, non_manifold_edges=0, loose_vertices=0, loose_edges=0,
                           degenerate_faces=0, duplicate_vertices=0, surface_area=0.0, volume=0.0)
        self.dup_checked = True
        self.watertight = True
        self.has_faces = False
        self.bb_min = np.full(3, np.inf)
        self.bb_max = np.full(3, -np.inf)
        self.invalid = False
        self.tri_n, self.tri_c, self.tri_a, self.tri_w = [], [], [], []
        self.points = []
        self.edge_lengths = []
        self.objects = []

    def add_mesh(self, obj, me, mw):
        t = self.totals
        co = mesh_arrays(me)
        nv = len(co)
        if nv == 0:
            return
        if not np.isfinite(co).all():
            self.invalid = True
            co = np.nan_to_num(co, nan=0.0, posinf=0.0, neginf=0.0)
        M = np.array(mw, dtype=np.float64)
        w = co @ M[:3, :3].T + M[:3, 3]
        self.bb_min = np.minimum(self.bb_min, w.min(0))
        self.bb_max = np.maximum(self.bb_max, w.max(0))
        diag = float(np.linalg.norm(w.max(0) - w.min(0))) or 1e-9

        # points for camera framing: random subset + the extreme points
        k = min(nv, max(200, POINT_SAMPLE_TARGET // 4))
        idx = self.rng.choice(nv, k, replace=False) if nv > k else np.arange(nv)
        ext = np.concatenate([w.argmin(0), w.argmax(0)])
        self.points.append(w[np.concatenate([idx, ext])])

        ne = len(me.edges)
        ev = np.empty(ne * 2, np.int32)
        me.edges.foreach_get("vertices", ev)
        npoly = len(me.polygons)
        loop_total = np.empty(npoly, np.int32)
        me.polygons.foreach_get("loop_total", loop_total)
        nl = len(me.loops)
        loop_edges = np.empty(nl, np.int32)
        me.loops.foreach_get("edge_index", loop_edges)

        t["vertices"] += nv
        t["edges"] += ne
        t["faces"] += npoly
        t["tri_faces"] += int((loop_total == 3).sum())
        t["quads"] += int((loop_total == 4).sum())
        t["ngons"] += int((loop_total > 4).sum())
        tris = int(nl - 2 * npoly)
        t["triangles"] += tris

        edge_faces = np.bincount(loop_edges, minlength=ne) if ne else np.zeros(0, np.int64)
        boundary = int((edge_faces == 1).sum())
        nonman = int((edge_faces > 2).sum())
        t["boundary_edges"] += boundary
        t["non_manifold_edges"] += nonman
        t["loose_edges"] += int((edge_faces == 0).sum()) if npoly else ne
        used = np.bincount(ev, minlength=nv) if ne else np.zeros(nv, np.int64)
        t["loose_vertices"] += int((used == 0).sum())
        if npoly:
            self.has_faces = True
            if boundary or nonman:
                self.watertight = False
        if ne:
            e = ev.reshape(-1, 2)
            ks = min(ne, 20000)
            sel = self.rng.choice(ne, ks, replace=False) if ne > ks else np.arange(ne)
            self.edge_lengths.append(np.linalg.norm(w[e[sel, 0]] - w[e[sel, 1]], axis=1))

        if nv <= DUPLICATE_CHECK_MAX:
            q = np.round(co / (diag * 1e-6)).astype(np.int64)
            t["duplicate_vertices"] += nv - unique_rows(q)
        else:
            self.dup_checked = False

        # triangles: area, volume, degenerate faces, samples for orientation
        if npoly:
            me.calc_loop_triangles()
            nlt = len(me.loop_triangles)
            tv = np.empty(nlt * 3, np.int32)
            me.loop_triangles.foreach_get("vertices", tv)
            tv = tv.reshape(-1, 3)
            eps = (diag ** 2) * 1e-12
            for s in range(0, nlt, CHUNK):
                v = w[tv[s:s + CHUNK]]
                cr = np.cross(v[:, 1] - v[:, 0], v[:, 2] - v[:, 0])
                ln = np.linalg.norm(cr, axis=1)
                area = 0.5 * ln
                t["surface_area"] += float(area.sum())
                t["volume"] += float(np.einsum("ij,ij->i", v[:, 0], np.cross(v[:, 1], v[:, 2])).sum() / 6.0)
                t["degenerate_faces"] += int((area <= eps).sum())
                if self.sample_p < 1.0:
                    keep = self.rng.random(len(v)) < self.sample_p
                else:
                    keep = np.ones(len(v), bool)
                keep &= ln > 0
                self.tri_n.append(cr[keep] / ln[keep, None])
                self.tri_c.append(v[keep].mean(1))
                self.tri_a.append(area[keep] / self.sample_p)
                self.tri_w.append(np.full(int(keep.sum()), 1.0 / self.sample_p))

        self.objects.append({
            "name": obj.name, "type": obj.type, "vertices": nv, "faces": npoly,
            "triangles": tris, "materials": len(obj.material_slots),
            "uv_maps": len(getattr(me, "uv_layers", [])),
        })

    def add_bbox_only(self, obj):
        M = np.array(obj.matrix_world, dtype=np.float64)
        corners = np.array([list(c) for c in obj.bound_box], dtype=np.float64)
        w = corners @ M[:3, :3].T + M[:3, 3]
        self.bb_min = np.minimum(self.bb_min, w.min(0))
        self.bb_max = np.maximum(self.bb_max, w.max(0))
        self.points.append(w)
        self.objects.append({"name": obj.name, "type": obj.type})

    def arrays(self):
        cat = lambda lst, shape: np.concatenate(lst) if lst else np.zeros(shape)  # noqa: E731
        return (cat(self.tri_n, (0, 3)), cat(self.tri_c, (0, 3)), cat(self.tri_a, (0,)),
                cat(self.tri_w, (0,)), cat(self.points, (0, 3)))


def analyse_scene(result, settings, objs, model_dir):
    dg = bpy.context.evaluated_depsgraph_get()
    rng = np.random.default_rng(1234)

    counts = {"objects": len(objs), "mesh_objects": 0, "other_geometry_objects": 0,
              "armatures": 0, "bones": 0, "cameras": 0, "lights": 0, "empties": 0}
    for o in objs:
        if o.type == "MESH":
            counts["mesh_objects"] += 1
        elif o.type in GEOMETRY_TYPES:
            counts["other_geometry_objects"] += 1
        elif o.type == "ARMATURE":
            counts["armatures"] += 1
            counts["bones"] += len(o.data.bones)
        elif o.type == "CAMERA":
            counts["cameras"] += 1
        elif o.type == "LIGHT":
            counts["lights"] += 1
        elif o.type == "EMPTY":
            counts["empties"] += 1
    counts["collections"] = len(bpy.data.collections)
    result["scene"] = counts

    geo_objs = [o for o in objs if o.type in GEOMETRY_TYPES]
    est = 0
    for o in geo_objs:
        if o.type == "MESH":
            est += max(0, len(o.data.loops) - 2 * len(o.data.polygons))
    acc = GeometryAccumulator(est, rng)

    shape_keys = 0
    uv_maps = 0
    color_attrs = 0
    meshes_without_uv = set()
    point_meshes = []
    for o in geo_objs:
        if o.type == "MESH":
            me = o.data
            acc.add_mesh(o, me, o.matrix_world)
            if me.shape_keys:
                shape_keys += max(0, len(me.shape_keys.key_blocks) - 1)
            uv_maps += len(me.uv_layers)
            color_attrs += len(getattr(me, "color_attributes", []))
            if len(me.polygons) and not len(me.uv_layers):
                meshes_without_uv.add(o.name)
            if len(me.vertices) and not len(me.polygons):
                point_meshes.append(o)
        elif o.type in CONVERTIBLE_TYPES:
            oe = o.evaluated_get(dg)
            try:
                me = oe.to_mesh()
            except RuntimeError:
                me = None
            if me is not None:
                acc.add_mesh(o, me, oe.matrix_world)
                oe.to_mesh_clear()
            else:
                acc.add_bbox_only(o)
        else:
            acc.add_bbox_only(o)

    t = acc.totals
    t["watertight"] = bool(acc.has_faces and acc.watertight)
    t["volume"] = abs(t["volume"])
    if not acc.dup_checked:
        t["duplicate_vertices"] = None
    t["mean_edge_length"] = float(np.mean(np.concatenate(acc.edge_lengths))) if acc.edge_lengths else None
    result["geometry"] = t
    result["objects_detail"] = acc.objects[:200]

    # materials / textures
    mats = set()
    textured_objects = set()
    for o in geo_objs:
        for slot in getattr(o, "material_slots", []):
            if slot.material is not None:
                mats.add(slot.material.name)
                if material_images(slot.material):
                    textured_objects.add(o.name)
    images = [i for i in bpy.data.images if i.source in {"FILE", "SEQUENCE", "TILED"}]
    missing = [i for i in images if is_missing(i)]
    relinked = []
    if missing and settings.get("fix_textures", True):
        relinked = relink_textures(model_dir, missing)
        missing = [i for i in images if is_missing(i)]
    result["materials"] = {
        "count": len(mats),
        "names": sorted(mats)[:100],
        "textures": len(images),
        "packed_textures": sum(1 for i in images if i.packed_file is not None),
        "missing_textures": sorted({os.path.basename(i.filepath.replace("\\", "/")) or i.name for i in missing}),
        "relinked_textures": relinked,
        "uv_maps": uv_maps,
        "color_attributes": color_attrs,
    }

    # animation
    actions = list(bpy.data.actions)
    frame_start = frame_end = None
    if actions:
        frame_start = min(int(a.frame_range[0]) for a in actions)
        frame_end = max(int(a.frame_range[1]) for a in actions)
    result["animation"] = {"actions": len(actions), "frame_start": frame_start,
                           "frame_end": frame_end, "shape_keys": shape_keys}

    # issues
    geo = t
    if not geo_objs or (geo["vertices"] == 0 and not any(o.type not in {"MESH"} | CONVERTIBLE_TYPES for o in geo_objs)):
        schema.add_issue(result, "no_geometry")
    elif geo["faces"] == 0 and geo["vertices"] > 0:
        schema.add_issue(result, "points_only", f"{geo['vertices']:,} vertices but no faces")
    if acc.invalid:
        schema.add_issue(result, "invalid_coordinates")
    if missing:
        schema.add_issue(result, "missing_textures",
                         f"{len(missing)} texture file(s) not found: " + ", ".join(result["materials"]["missing_textures"][:5]))
    if relinked:
        schema.add_issue(result, "textures_relinked", f"{len(relinked)} texture(s) found in nearby folders")
    if textured_objects & meshes_without_uv:
        schema.add_issue(result, "missing_uvs", f"{len(textured_objects & meshes_without_uv)} textured mesh(es) without UVs")
    if geo["faces"]:
        if geo["degenerate_faces"] > max(10, 0.01 * geo["triangles"]):
            schema.add_issue(result, "degenerate_faces", f"{geo['degenerate_faces']:,} zero-area triangles")
        if geo["non_manifold_edges"] > max(5, 0.001 * geo["edges"]):
            schema.add_issue(result, "non_manifold", f"{geo['non_manifold_edges']:,} non-manifold edges")
        if geo["boundary_edges"]:
            schema.add_issue(result, "open_boundaries", f"{geo['boundary_edges']:,} boundary edges")
        if geo["ngons"]:
            schema.add_issue(result, "ngons", f"{geo['ngons']:,} n-gons")
    if geo["loose_vertices"] or (geo["loose_edges"] and geo["faces"]):
        schema.add_issue(result, "loose_geometry",
                         f"{geo['loose_vertices']:,} loose vertices, {geo['loose_edges']:,} loose edges")
    if geo.get("duplicate_vertices"):
        schema.add_issue(result, "duplicate_vertices", f"{geo['duplicate_vertices']:,} overlapping vertices")
    if geo_objs and not mats:
        schema.add_issue(result, "no_materials")
    if actions or shape_keys:
        schema.add_issue(result, "has_animation",
                         f"{len(actions)} action(s), {shape_keys} shape key(s)")

    textured = bool(images)
    rigged = counts["armatures"] > 0
    animated = bool(actions)
    tags = []
    for flag, name in ((textured, "textured"), (rigged, "rigged"), (animated, "animated"),
                       (shape_keys > 0, "shape_keys"), (t["watertight"], "watertight"),
                       (geo["faces"] and geo["quads"] > 0.6 * geo["faces"], "quad_dominant"),
                       (bool(point_meshes) and not geo["faces"], "point_cloud")):
        if flag:
            tags.append(name)
    result["classification"] = {"textured": textured, "rigged": rigged, "animated": animated, "tags": tags}
    return acc, point_meshes


# ---------------------------------------------------------------------------
# Orientation & viewpoint
# ---------------------------------------------------------------------------

AXES = {
    "+Z": (0, 0, 1), "-Z": (0, 0, -1), "+Y": (0, 1, 0),
    "-Y": (0, -1, 0), "+X": (1, 0, 0), "-X": (-1, 0, 0),
}


def base_support(tri_n, tri_c, tri_a, points, axis):
    """Share of surface area lying flat on the lowest level when ``axis`` is up."""
    a = np.array(axis, dtype=np.float64)
    hv = points @ a
    lo, hi = hv.min(), hv.max()
    height = hi - lo
    if height <= 0 or tri_a.sum() <= 0:
        return 0.0, 1.0
    h = tri_c @ a
    near = (h - lo) <= 0.02 * height
    flat = np.abs(tri_n @ a) > 0.95
    support = float(tri_a[near & flat].sum() / tri_a.sum())
    com = float(((tri_c @ a) * tri_a).sum() / tri_a.sum())
    return support, (com - lo) / height


def detect_up(tri_n, tri_c, tri_a, points):
    """Return (axis_name, confidence, scores). Conservative: prefers +Z."""
    scores = {name: base_support(tri_n, tri_c, tri_a, points, ax) for name, ax in AXES.items()}
    current, _ = scores["+Z"]
    best = max((n for n in scores if n != "+Z"), key=lambda n: scores[n][0])
    b_support, b_com = scores[best]
    if current < 0.005 and b_support >= 0.05 and b_com <= 0.5:
        conf = b_support / (b_support + current + 1e-9)
        return best, min(1.0, conf), scores
    conf = (current + 1e-3) / (current + b_support + 2e-3)
    return "+Z", conf, scores


def view_dir(az_deg, el_deg):
    az, el = math.radians(az_deg), math.radians(el_deg)
    return Vector((math.cos(el) * math.cos(az), math.cos(el) * math.sin(az), math.sin(el)))


def view_score(tri_n, tri_a, tri_w, d):
    if not len(tri_n):
        return 0.0, 0.0
    dots = tri_n @ np.array(d, dtype=np.float64)
    detail = float(tri_w[dots > 0.25].sum())
    proj = float((tri_a * np.clip(dots, 0, None)).sum())
    return detail, proj


def pick_front(tri_n, tri_a, tri_w, axis_aware):
    """Camera azimuth (deg) looking at the model's front. Blender front = -Y = -90 deg."""
    cands = [-90, 0, 90, 180]
    raw = {az: view_score(tri_n, tri_a, tri_w, view_dir(az, 10)) for az in cands}
    md = max(v[0] for v in raw.values()) or 1.0
    mp = max(v[1] for v in raw.values()) or 1.0
    bonus = 0.15 if axis_aware else 0.05
    scores = {az: 0.6 * raw[az][0] / md + 0.4 * raw[az][1] / mp + (bonus if az == -90 else 0.0) for az in cands}
    front = max(cands, key=lambda az: scores[az])
    # 3/4 hero view: front-right (front + 45) unless front-left shows clearly more
    right = view_score(tri_n, tri_a, tri_w, view_dir(front + 45, 25))
    left = view_score(tri_n, tri_a, tri_w, view_dir(front - 45, 25))
    hero = front - 45 if (left[0] + left[1] / mp * md) > 1.05 * (right[0] + right[1] / mp * md) else front + 45
    return front, hero, scores


# ---------------------------------------------------------------------------
# Render setup
# ---------------------------------------------------------------------------

def set_engine(scene, name):
    options = {
        "eevee": ["BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"],
        "cycles": ["CYCLES"],
        "workbench": ["BLENDER_WORKBENCH"],
    }[name]
    for opt in options:
        try:
            scene.render.engine = opt
            return opt
        except TypeError:
            continue
    raise RuntimeError(f"Render engine '{name}' not available")


def enable_gpu():
    try:
        prefs = bpy.context.preferences.addons["cycles"].preferences
    except KeyError:
        return None
    for dev_type in ("OPTIX", "CUDA", "HIP", "METAL", "ONEAPI"):
        try:
            prefs.compute_device_type = dev_type
        except TypeError:
            continue
        try:
            (getattr(prefs, "refresh_devices", None) or prefs.get_devices)()
        except Exception:
            continue
        devices = [d for d in prefs.devices if d.type == dev_type]
        if devices:
            for d in prefs.devices:
                d.use = d.type in (dev_type, "CPU")
            return dev_type
    try:
        prefs.compute_device_type = "NONE"
    except TypeError:
        pass
    return None


def setup_render(scene, settings, heavy):
    engine = settings.get("engine", "auto")
    if engine == "auto":
        engine = "eevee"
    engine_id = set_engine(scene, engine)
    samples = int(settings.get("samples", 32))
    if heavy:
        samples = max(8, samples // 2)
    r = scene.render
    r.resolution_x = r.resolution_y = int(settings.get("resolution", 640))
    r.resolution_percentage = 100
    r.film_transparent = False
    r.image_settings.file_format = "PNG"
    r.image_settings.color_mode = "RGB"
    r.image_settings.color_depth = "8"
    device = "CPU"
    if engine_id == "CYCLES":
        c = scene.cycles
        c.samples = samples
        c.use_adaptive_sampling = True
        c.adaptive_threshold = 0.03
        c.use_denoising = True
        c.max_bounces = 6
        c.diffuse_bounces = 3
        c.glossy_bounces = 3
        c.transparent_max_bounces = 8
        if settings.get("cycles_time_limit"):
            c.time_limit = float(settings["cycles_time_limit"])
        if settings.get("gpu", True):
            dev = enable_gpu()
            if dev:
                c.device = "GPU"
                device = dev
    elif engine_id.startswith("BLENDER_EEVEE"):
        ee = scene.eevee
        if hasattr(ee, "taa_render_samples"):
            ee.taa_render_samples = samples
        device = "GPU"
    elif engine_id == "BLENDER_WORKBENCH":
        sh = scene.display.shading
        sh.light = "STUDIO"
        sh.color_type = "TEXTURE"
        sh.show_cavity = True
        sh.show_shadows = True
        device = "GPU"
    vs = scene.view_settings
    for vt in ("AgX", "Filmic", "Standard"):
        try:
            vs.view_transform = vt
            break
        except TypeError:
            continue
    try:
        vs.look = "None"
    except TypeError:
        pass
    vs.exposure = 0.0
    vs.gamma = 1.0
    return engine_id, samples, device


def make_material(name, color, roughness):
    mat = bpy.data.materials.new(name)
    try:
        mat.use_nodes = True
    except Exception:
        pass
    mat.diffuse_color = (*color, 1.0)
    mat.roughness = roughness
    nt = mat.node_tree
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None:
        nt.nodes.clear()
        bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
        out = nt.nodes.new("ShaderNodeOutputMaterial")
        nt.links.new(bsdf.outputs[0], out.inputs[0])
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    return mat


def setup_world(scene, color, strength):
    world = bpy.data.worlds.new("SC_World")
    scene.world = world
    world.color = color
    try:
        world.use_nodes = True
    except Exception:
        pass
    nt = world.node_tree
    bg = next((n for n in nt.nodes if n.type == "BACKGROUND"), None)
    if bg is None:
        nt.nodes.clear()
        bg = nt.nodes.new("ShaderNodeBackground")
        out = nt.nodes.new("ShaderNodeOutputWorld")
        nt.links.new(bg.outputs[0], out.inputs[0])
    bg.inputs[0].default_value = (*color, 1.0)
    bg.inputs[1].default_value = strength


def add_light_rig(scene):
    rig = {}
    for name, strength, angle in (("key", 3.2, 12.0), ("fill", 0.9, 30.0), ("rim", 2.2, 8.0)):
        data = bpy.data.lights.new(f"SC_{name}", "SUN")
        data.energy = strength
        data.angle = math.radians(angle)
        obj = bpy.data.objects.new(f"SC_{name}", data)
        obj.rotation_mode = "QUATERNION"
        scene.collection.objects.link(obj)
        rig[name] = obj
    return rig


def aim_light_rig(rig, cam_az):
    for name, (daz, el) in {"key": (50, 55), "fill": (-70, 20), "rim": (165, 40)}.items():
        d = view_dir(cam_az + daz, el)
        rig[name].rotation_quaternion = (-d).to_track_quat("-Z", "Y")


def add_ground(scene, center, zmin, radius, color):
    s = radius * 50.0
    me = bpy.data.meshes.new("SC_Ground")
    cx, cy = center[0], center[1]
    z = zmin - radius * 0.002
    me.from_pydata([(cx - s, cy - s, z), (cx + s, cy - s, z), (cx + s, cy + s, z), (cx - s, cy + s, z)], [], [(0, 1, 2, 3)])
    me.materials.append(make_material("SC_GroundMat", color, 0.9))
    obj = bpy.data.objects.new("SC_Ground", me)
    scene.collection.objects.link(obj)
    return obj


def place_camera(cam, center, radius, flat_points, az, el, ortho, margin=0.08):
    d = view_dir(az, el)
    cam.data.type = "ORTHO" if ortho else "PERSP"
    cam.data.lens = 50.0
    cam.data.sensor_fit = "AUTO"
    cam.rotation_mode = "QUATERNION"
    cam.rotation_quaternion = (-d).to_track_quat("-Z", "Y")
    cam.location = center + d * radius * 4.0
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    loc, scale = cam.camera_fit_coords(dg, flat_points)
    loc = Vector(loc)
    if ortho:
        cam.data.ortho_scale = max(scale, 1e-6) * (1.0 + margin)
        loc = loc + d * radius * 3.0
    else:
        loc = loc + d * ((loc - center).length * margin)
    cam.location = loc
    dist = (loc - center).length
    cam.data.clip_start = max(1e-5, radius * 1e-3)
    cam.data.clip_end = dist + radius * 200.0


def add_point_display(objs, radius_value):
    """Give vertex-only meshes (point clouds) visible points via Geometry Nodes."""
    try:
        ng = bpy.data.node_groups.new("SC_Points", "GeometryNodeTree")
        ng.interface.new_socket("Geometry", in_out="INPUT", socket_type="NodeSocketGeometry")
        ng.interface.new_socket("Geometry", in_out="OUTPUT", socket_type="NodeSocketGeometry")
        nin = ng.nodes.new("NodeGroupInput")
        nout = ng.nodes.new("NodeGroupOutput")
        m2p = ng.nodes.new("GeometryNodeMeshToPoints")
        m2p.inputs["Radius"].default_value = radius_value
        ng.links.new(nin.outputs[0], m2p.inputs["Mesh"])
        ng.links.new(m2p.outputs[0], nout.inputs[0])
        for o in objs:
            o.modifiers.new("SC_Points", "NODES").node_group = ng
        return True
    except Exception as exc:  # older Blender without the interface API
        log(f"point display unavailable: {exc}")
        return False


def apply_clay_and_wire(geo_objs, clay, wire, wire_thickness):
    for o in geo_objs:
        if not hasattr(o.data, "materials"):
            continue
        if len(o.material_slots) == 0:
            o.data.materials.append(None)
        for slot in o.material_slots:
            slot.link = "OBJECT"
            slot.material = clay
        if wire is not None and o.type == "MESH" and len(o.data.polygons):
            o.data.materials.append(None)
            wslot = o.material_slots[len(o.material_slots) - 1]
            wslot.link = "OBJECT"
            wslot.material = wire
            mod = o.modifiers.new("SC_Wire", "WIREFRAME")
            mod.thickness = wire_thickness
            mod.use_replace = False
            mod.use_even_offset = False  # even offset explodes on near-degenerate triangles
            mod.use_relative_offset = False
            mod.offset = 0.0
            mod.material_offset = len(o.material_slots) - 1


# ---------------------------------------------------------------------------
# Contact sheet (numpy image ops + tiny bitmap font)
# ---------------------------------------------------------------------------

_FONT = {
    "A": ".###.|#...#|#...#|#####|#...#|#...#|#...#", "B": "####.|#...#|#...#|####.|#...#|#...#|####.",
    "C": ".###.|#...#|#....|#....|#....|#...#|.###.", "D": "####.|#...#|#...#|#...#|#...#|#...#|####.",
    "E": "#####|#....|#....|####.|#....|#....|#####", "F": "#####|#....|#....|####.|#....|#....|#....",
    "G": ".###.|#...#|#....|#.###|#...#|#...#|.####", "H": "#...#|#...#|#...#|#####|#...#|#...#|#...#",
    "I": ".###.|..#..|..#..|..#..|..#..|..#..|.###.", "J": "..###|...#.|...#.|...#.|...#.|#..#.|.##..",
    "K": "#...#|#..#.|#.#..|##...|#.#..|#..#.|#...#", "L": "#....|#....|#....|#....|#....|#....|#####",
    "M": "#...#|##.##|#.#.#|#.#.#|#...#|#...#|#...#", "N": "#...#|#...#|##..#|#.#.#|#..##|#...#|#...#",
    "O": ".###.|#...#|#...#|#...#|#...#|#...#|.###.", "P": "####.|#...#|#...#|####.|#....|#....|#....",
    "Q": ".###.|#...#|#...#|#...#|#.#.#|#..#.|.##.#", "R": "####.|#...#|#...#|####.|#.#..|#..#.|#...#",
    "S": ".####|#....|#....|.###.|....#|....#|####.", "T": "#####|..#..|..#..|..#..|..#..|..#..|..#..",
    "U": "#...#|#...#|#...#|#...#|#...#|#...#|.###.", "V": "#...#|#...#|#...#|#...#|#...#|.#.#.|..#..",
    "W": "#...#|#...#|#...#|#.#.#|#.#.#|#.#.#|.#.#.", "X": "#...#|#...#|.#.#.|..#..|.#.#.|#...#|#...#",
    "Y": "#...#|#...#|.#.#.|..#..|..#..|..#..|..#..", "Z": "#####|....#|...#.|..#..|.#...|#....|#####",
    "0": ".###.|#...#|#..##|#.#.#|##..#|#...#|.###.", "1": "..#..|.##..|..#..|..#..|..#..|..#..|.###.",
    "2": ".###.|#...#|....#|...#.|..#..|.#...|#####", "3": "####.|....#|....#|.###.|....#|....#|####.",
    "4": "...#.|..##.|.#.#.|#..#.|#####|...#.|...#.", "5": "#####|#....|####.|....#|....#|#...#|.###.",
    "6": ".###.|#....|#....|####.|#...#|#...#|.###.", "7": "#####|....#|...#.|..#..|.#...|.#...|.#...",
    "8": ".###.|#...#|#...#|.###.|#...#|#...#|.###.", "9": ".###.|#...#|#...#|.####|....#|....#|.###.",
    " ": ".....|.....|.....|.....|.....|.....|.....", ".": ".....|.....|.....|.....|.....|.##..|.##..",
    ",": ".....|.....|.....|.....|.##..|..#..|.#...", ":": ".....|.##..|.##..|.....|.##..|.##..|.....",
    "-": ".....|.....|.....|#####|.....|.....|.....", "_": ".....|.....|.....|.....|.....|.....|#####",
    "/": "....#|...#.|...#.|..#..|.#...|.#...|#....", "(": "...#.|..#..|.#...|.#...|.#...|..#..|...#.",
    ")": ".#...|..#..|...#.|...#.|...#.|..#..|.#...", "%": "##..#|##..#|...#.|..#..|.#...|#..##|#..##",
    "|": "..#..|..#..|..#..|..#..|..#..|..#..|..#..", "+": ".....|..#..|..#..|#####|..#..|..#..|.....",
    "?": ".###.|#...#|....#|...#.|..#..|.....|..#..", "=": ".....|.....|#####|.....|#####|.....|.....",
    "[": ".###.|.#...|.#...|.#...|.#...|.#...|.###.", "]": ".###.|...#.|...#.|...#.|...#.|...#.|.###.",
    "'": "..#..|..#..|.....|.....|.....|.....|.....", "#": ".#.#.|#####|.#.#.|.#.#.|.#.#.|#####|.#.#.",
}
_GLYPHS = {k: np.array([[c == "#" for c in row] for row in v.split("|")], dtype=bool) for k, v in _FONT.items()}


def text_width(text, scale):
    return len(text) * 6 * scale


def draw_text(img, x, y, text, scale, color):
    """Draw ``text`` onto a top-down float RGB(A) image at (x, y) = top-left."""
    h, w = img.shape[:2]
    for ch in text.upper():
        g = _GLYPHS.get(ch, _GLYPHS["?"])
        big = np.kron(g, np.ones((scale, scale), dtype=bool))
        gh, gw = big.shape
        x0, y0 = max(0, x), max(0, y)
        x1, y1 = min(w, x + gw), min(h, y + gh)
        if x1 > x0 and y1 > y0:
            region = img[y0:y1, x0:x1, :3]
            mask = big[y0 - y:y1 - y, x0 - x:x1 - x]
            region[mask] = color
        x += 6 * scale
        if x >= w:
            break


def fit_text(text, max_width, scale):
    max_chars = max(1, max_width // (6 * scale))
    return text if len(text) <= max_chars else text[:max_chars - 2] + ".."


def load_png(path):
    img = bpy.data.images.load(path, check_existing=False)
    try:
        img.colorspace_settings.name = "Non-Color"
    except TypeError:
        pass
    w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)[::-1, :, :3].copy()  # top-down RGB


def save_image(arr, path, fmt, quality):
    """``arr`` = top-down float RGB in 0..1 (display referred)."""
    h, w = arr.shape[:2]
    rgba = np.ones((h, w, 4), np.float32)
    rgba[:, :, :3] = np.clip(arr, 0, 1)
    img = bpy.data.images.new("SC_Out", w, h, alpha=False)
    try:
        img.colorspace_settings.name = "Non-Color"
    except TypeError:
        pass
    img.pixels.foreach_set(rgba[::-1].ravel())
    img.file_format = fmt
    tmp = path + ".tmp" + (".jpg" if fmt == "JPEG" else ".png")
    img.filepath_raw = tmp
    try:
        img.save(filepath=tmp, quality=int(quality))
    except TypeError:
        img.save()
    bpy.data.images.remove(img)
    os.replace(tmp, path)


STATUS_COLORS = {"ok": (0.35, 0.85, 0.45), "warning": (1.0, 0.78, 0.25), "unsupported": (0.6, 0.7, 0.9),
                 "error": (1.0, 0.5, 0.2), "broken": (1.0, 0.3, 0.3)}


def banner_lines(result, file_name, file_size):
    g = result.get("geometry", {})
    b = result.get("bounds", {})
    m = result.get("materials", {})
    c = result.get("classification", {})
    size = b.get("size") or [0, 0, 0]
    dims = " X ".join(f"{v:.3g}" for v in size)
    line2 = (f"{g.get('triangles', 0):,} TRIS ({c.get('poly_tier', '-').replace('_', ' ')})"
             f" | {g.get('vertices', 0):,} VERTS | {result.get('scene', {}).get('objects', 0)} OBJ"
             f" | {m.get('count', 0)} MAT | {m.get('textures', 0)} TEX | {dims} M | {schema.human_bytes(file_size)}")
    codes = [i["code"] for i in result.get("issues", []) if i["severity"] != "info"]
    line3 = "ISSUES: " + (", ".join(codes) if codes else "NONE") + "  | TAGS: " + (", ".join(c.get("tags", [])) or "-")
    return file_name, line2, line3


def compose_sheet(tiles, result, file_name, file_size, dark):
    names = [t[0] for t in tiles]
    imgs = [t[1] for t in tiles]
    n = len(imgs)
    th, tw = imgs[0].shape[:2]
    cols = n if n <= 3 else (2 if n == 4 else 3)
    rows = math.ceil(n / cols)
    scale = max(1, tw // 320)
    line_h = 9 * scale
    banner_h = 4 * line_h + 2 * scale
    bg = 0.1 if dark else 0.12
    sheet = np.full((banner_h + rows * th, cols * tw, 3), bg, np.float32)
    status = result.get("status", "ok")
    l1, l2, l3 = banner_lines(result, file_name, file_size)
    width = sheet.shape[1] - 4 * scale
    tag = f"[{status.upper()}] "
    draw_text(sheet, 2 * scale, line_h // 2, tag, scale, STATUS_COLORS.get(status, (1, 1, 1)))
    draw_text(sheet, 2 * scale + text_width(tag, scale), line_h // 2, fit_text(l1, width - text_width(tag, scale), scale), scale, (0.95, 0.95, 0.95))
    draw_text(sheet, 2 * scale, line_h // 2 + line_h + scale, fit_text(l2, width, scale), scale, (0.8, 0.8, 0.8))
    draw_text(sheet, 2 * scale, line_h // 2 + 2 * (line_h + scale), fit_text(l3, width, scale), scale, (0.65, 0.65, 0.65))
    for i, (name, img) in enumerate(zip(names, imgs)):
        r, c = divmod(i, cols)
        y, x = banner_h + r * th, c * tw
        sheet[y:y + th, x:x + tw] = img
        label = name.upper()
        lw = text_width(label, scale) + 3 * scale
        sheet[y:y + line_h + scale, x:x + lw] *= 0.35
        draw_text(sheet, x + 2 * scale, y + scale, label, scale, (1, 1, 1))
    return sheet


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def write_result(path, result):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(result, fh, indent=1, default=lambda o: o.tolist() if hasattr(o, "tolist") else str(o))
    os.replace(tmp, path)


def scale_hint(max_dim):
    if max_dim < 0.01:
        return "tiny (<1 cm)"
    if max_dim < 0.3:
        return "small (<30 cm)"
    if max_dim < 3:
        return "human scale (<3 m)"
    if max_dim < 30:
        return "large (<30 m)"
    if max_dim < 1000:
        return "very large (<1 km)"
    return "huge (>1 km, wrong units?)"


def process(args, settings, result):
    path = os.path.abspath(args.input)
    ext = os.path.splitext(path)[1].lower()
    model_dir = os.path.dirname(path)
    result["import"] = {"blender_version": bpy.app.version_string}

    # 1. import -----------------------------------------------------------
    if ext != ".blend":
        clear_scene()
    t0 = time.time()
    log(f"importing {path}")
    try:
        imported = import_model(path, ext, settings, result)
    except LookupError as exc:
        schema.add_issue(result, "unsupported_format", str(exc))
        return
    except Exception as exc:
        schema.add_issue(result, "import_failed", f"{type(exc).__name__}: {str(exc).strip()[:500]}")
        return
    importer, details, earlier_errors = imported
    result["import"].update({"importer": importer, "seconds": round(time.time() - t0, 3)})
    if details:
        result["import"]["format_details"] = details
    if earlier_errors:
        result["import"]["failed_attempts"] = earlier_errors
        schema.add_issue(result, "import_fallback", f"read with {importer} after: " + " | ".join(earlier_errors)[:400])

    scene = bpy.context.scene
    view_layer = bpy.context.view_layer
    if ext != ".blend":
        # importers may create objects that are not in the view layer yet
        view_layer.update()
    objs = render_objects(view_layer)

    # 2. analyse ----------------------------------------------------------
    log("analysing geometry")
    t0 = time.time()
    acc, point_meshes = analyse_scene(result, settings, objs, model_dir)
    result["import"]["analysis_seconds"] = round(time.time() - t0, 3)
    for o in objs:  # the file's own cameras/lights would interfere with ours
        if o.type in {"CAMERA", "LIGHT"}:
            o.hide_render = True

    g = result["geometry"]
    tiers = schema.tiers_from_limits(settings.get("poly_tiers", DEFAULT_SETTINGS["poly_tiers"]))
    result["classification"]["poly_tier"] = schema.poly_tier(g["triangles"], tiers)
    if any(i["code"] in ("no_geometry", "invalid_coordinates") for i in result["issues"]):
        return
    if not np.isfinite(acc.bb_min).all():
        schema.add_issue(result, "no_geometry")
        return

    tri_n, tri_c, tri_a, tri_w, points = acc.arrays()
    bb_min, bb_max = acc.bb_min.copy(), acc.bb_max.copy()
    diag = float(np.linalg.norm(bb_max - bb_min))
    if diag <= 1e-12:
        schema.add_issue(result, "zero_size")
        return

    # 3. orientation ------------------------------------------------------
    mode = settings.get("orientation", "auto")
    orient = {"mode": mode, "detected_up": "+Z", "rotation_applied": False, "confidence": None}
    if len(tri_n) and mode != "importer":
        up, conf, scores = detect_up(tri_n, tri_c, tri_a, points)
        orient.update(detected_up=up, confidence=round(conf, 3),
                      base_support={k: round(v[0], 4) for k, v in scores.items()})
        converted = result["import"].get("importer", "").startswith(("assimp", "osgconv"))
        apply = up != "+Z" and (mode == "detect" or ext in AXIS_UNKNOWN or converted)
        if apply:
            R = Vector(AXES[up]).rotation_difference(Vector((0, 0, 1))).to_matrix()
            R4 = R.to_4x4()
            for o in scene.objects:
                if o.parent is None:
                    o.matrix_world = R4 @ o.matrix_world
            view_layer.update()
            Rn = np.array(R, dtype=np.float64)
            tri_n, tri_c, points = tri_n @ Rn.T, tri_c @ Rn.T, points @ Rn.T
            corners = np.array([[x, y, z] for x in (bb_min[0], bb_max[0]) for y in (bb_min[1], bb_max[1]) for z in (bb_min[2], bb_max[2])])
            corners = corners @ Rn.T
            bb_min, bb_max = corners.min(0), corners.max(0)
            orient["rotation_applied"] = True
            schema.add_issue(result, "auto_rotated", f"'{up}' was detected as up and rotated to +Z (confidence {conf:.2f})")
    front_az, hero_az, front_scores = pick_front(tri_n, tri_a, tri_w, ext not in AXIS_UNKNOWN)
    names = {-90: "-Y (Blender front)", 0: "+X (right)", 90: "+Y (back)", 180: "-X (left)"}
    orient["front"] = names.get(front_az, str(front_az))
    orient["front_azimuth_deg"] = front_az
    orient["hero_azimuth_deg"] = hero_az
    result["orientation"] = orient

    size = bb_max - bb_min
    center = (bb_max + bb_min) / 2.0
    max_dim = float(size.max())
    result["bounds"] = {"min": bb_min.round(6).tolist(), "max": bb_max.round(6).tolist(),
                        "size": size.round(6).tolist(), "center": center.round(6).tolist(),
                        "diagonal": round(diag, 6), "scale_hint": scale_hint(max_dim)}
    if max_dim > 1000 or max_dim < 0.001:
        schema.add_issue(result, "extreme_scale", f"largest dimension is {max_dim:.4g} units")
    if np.linalg.norm(center) > 10 * diag and np.linalg.norm(center) > 10:
        schema.add_issue(result, "far_from_origin", f"center at distance {np.linalg.norm(center):.4g}")
    schema.finalize(result)

    # 4. render -----------------------------------------------------------
    result["stage"] = "render"
    write_result(args.result, result)  # lets the runner tell import crashes from render crashes
    t_render = time.time()
    heavy = g["triangles"] > 2_000_000
    engine_id, samples, device = setup_render(scene, settings, heavy)
    log(f"rendering with {engine_id} ({device}), {samples} samples")
    frame = result["animation"].get("frame_start")
    if frame is not None:
        scene.frame_set(frame)

    dark = settings.get("background") == "dark"
    bg_color = (0.045, 0.047, 0.052) if dark else (0.42, 0.43, 0.45)
    setup_world(scene, bg_color, 0.6 if dark else 0.55)
    rig = add_light_rig(scene)

    radius = float(np.linalg.norm(points - center, axis=1).max()) if len(points) else diag / 2
    radius = max(radius, 1e-6)
    center_v = Vector(center.tolist())
    if settings.get("ground", True):
        add_ground(scene, center, bb_min[2], radius, tuple(c * 0.9 for c in bg_color))
    if point_meshes:
        spacing = diag / max(1.0, math.sqrt(max(1, g["vertices"])))
        add_point_display(point_meshes, spacing * 0.6)

    cam_data = bpy.data.cameras.new("SC_Camera")
    cam = bpy.data.objects.new("SC_Camera", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    if len(points) > POINT_SAMPLE_TARGET:
        points = points[np.random.default_rng(7).choice(len(points), POINT_SAMPLE_TARGET, replace=False)]
    flat_points = points.astype(np.float32).ravel().tolist()

    ratio = size[2] / max(size[0], size[1], 1e-9)
    hero_el = 35.0 if ratio < 0.3 else (18.0 if ratio > 2.0 else 25.0)
    view_defs = {
        "hero": (hero_az, hero_el, False),
        "front": (front_az, 4.0, True),
        "side": (front_az + 90, 4.0, True),
        "back": (front_az + 180, 4.0, True),
        "top": (front_az, 89.5, True),
        "wire": (hero_az, hero_el, False),
    }
    wanted = [v for v in settings.get("views", DEFAULT_SETTINGS["views"]) if v in view_defs] or ["hero"]
    if "wire" in wanted:
        wanted = [v for v in wanted if v != "wire"] + ["wire"]

    tmpdir = tempfile.mkdtemp(prefix="showcase_")
    tiles = []
    views_meta = []
    try:
        for view in wanted:
            az, el, ortho = view_defs[view]
            label = view
            if view == "wire":
                clay = make_material("SC_Clay", (0.62, 0.6, 0.57), 0.6)
                wire_mat = None
                if g["faces"] and g["faces"] <= int(settings.get("wire_max_faces", 400_000)):
                    wire_mat = make_material("SC_WireMat", (0.015, 0.02, 0.03), 0.5)
                    mel = g.get("mean_edge_length") or diag * 0.01
                    thickness = min(max(mel * 0.06, radius * 0.0004), radius * 0.006)
                else:
                    label = "clay"
                    thickness = 0.0
                apply_clay_and_wire([o for o in objs if o.type in GEOMETRY_TYPES], clay, wire_mat, thickness)
            place_camera(cam, center_v, radius, flat_points, az, el, ortho)
            aim_light_rig(rig, az)
            out = os.path.join(tmpdir, f"{view}.png")
            scene.render.filepath = out
            t0 = time.time()
            bpy.ops.render.render(write_still=True)
            log(f"view '{label}' rendered in {time.time() - t0:.1f}s")
            tiles.append((label, load_png(out)))
            views_meta.append({"name": label, "azimuth_deg": az % 360, "elevation_deg": el,
                               "projection": "orthographic" if ortho else "perspective"})
    except Exception as exc:
        schema.add_issue(result, "render_failed", f"{type(exc).__name__}: {exc}")
        log(traceback.format_exc())

    fmt = settings.get("image_format", "JPEG").upper()
    img_ext = ".jpg" if fmt == "JPEG" else ".png"
    render_meta = {"engine": engine_id, "device": device, "samples": samples,
                   "resolution": int(settings.get("resolution", 640)), "views": views_meta}
    if tiles:
        schema.finalize(result)
        out_base = args.out_base
        hero_path = out_base + schema.SUFFIX_HERO + img_ext
        sheet_path = out_base + schema.SUFFIX_SHEET + img_ext
        q = settings.get("jpeg_quality", 88)
        save_image(tiles[0][1], hero_path, fmt, q)
        sheet = compose_sheet(tiles, result, os.path.basename(path), os.path.getsize(path), dark)
        save_image(sheet, sheet_path, fmt, q)
        render_meta["preview_image"] = os.path.basename(hero_path)
        render_meta["sheet_image"] = os.path.basename(sheet_path)
    render_meta["seconds"] = round(time.time() - t_render, 3)
    result["render"] = render_meta
    shutil.rmtree(tmpdir, ignore_errors=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--input", required=True)
    p.add_argument("--out-base", required=True, help="path prefix for the images (usually the model path)")
    p.add_argument("--result", required=True, help="where to write the partial JSON record")
    p.add_argument("--settings", help="JSON file with render settings")
    args = p.parse_args(argv)

    settings = dict(DEFAULT_SETTINGS)
    if args.settings:
        with open(args.settings, encoding="utf-8") as fh:
            settings.update(json.load(fh))

    result = {"schema_version": schema.SCHEMA_VERSION, "tool_version": TOOL_VERSION,
              "issues": [], "stage": "import", "settings": settings}
    t0 = time.time()
    try:
        process(args, settings, result)
    except Exception as exc:
        schema.add_issue(result, "worker_error", f"{type(exc).__name__}: {exc}")
        result["traceback"] = traceback.format_exc()
        log(result["traceback"])
    result["worker_seconds"] = round(time.time() - t0, 3)
    result["stage"] = "done"
    schema.finalize(result)
    write_result(args.result, result)
    log(f"done: status={result['status']}")


if __name__ == "__main__":
    main()
