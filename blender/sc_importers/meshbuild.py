"""Helpers shared by the built-in importers: fast mesh creation and simple materials."""

import itertools
import os

import bpy
import numpy as np
from mathutils import Vector
from mathutils.geometry import tessellate_polygon


def y_up_to_z_up(v):
    """(x, y, z) Y-up right-handed -> Blender Z-up: (x, -z, y)."""
    v = np.asarray(v, dtype=np.float64).reshape(-1, 3)
    return np.stack([v[:, 0], -v[:, 2], v[:, 1]], axis=1)


def lh_y_up_to_z_up(v):
    """Left-handed Y-up (DirectX, LightWave) -> Blender: (x, z, y) (mirror fixes handedness)."""
    v = np.asarray(v, dtype=np.float64).reshape(-1, 3)
    return v[:, [0, 2, 1]].copy()


def srgb_to_linear(c):
    c = np.clip(np.asarray(c, dtype=np.float64), 0.0, 1.0)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def _principled(mat):
    try:
        mat.use_nodes = True
    except Exception:
        pass
    nt = mat.node_tree
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None:
        nt.nodes.clear()
        bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
        out = nt.nodes.new("ShaderNodeOutputMaterial")
        nt.links.new(bsdf.outputs[0], out.inputs[0])
    return nt, bsdf


def color_attribute_material(name="FaceColor"):
    """Material whose base colour comes from the 'Col' colour attribute."""
    mat = bpy.data.materials.get(name)
    if mat is not None:
        return mat
    mat = bpy.data.materials.new(name)
    nt, bsdf = _principled(mat)
    attr = nt.nodes.new("ShaderNodeAttribute")
    attr.attribute_name = "Col"
    nt.links.new(attr.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.7
    return mat


def diffuse_material(name, rgb):
    mat = bpy.data.materials.new(name)
    nt, bsdf = _principled(mat)
    lin = tuple(srgb_to_linear(rgb[:3]))
    bsdf.inputs["Base Color"].default_value = (*lin, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.7
    mat.diffuse_color = (*lin, 1.0)
    return mat


def load_image(path):
    """Load an image; a missing file still gets an image datablock (flagged as missing later)."""
    name = os.path.basename(path) or "texture"
    if os.path.isfile(path):
        try:
            return bpy.data.images.load(path, check_existing=True)
        except RuntimeError:
            pass
    img = bpy.data.images.new(name, 8, 8)
    img.source = "FILE"
    img.filepath = path
    return img


def image_material(name, image_path, base_dir=None):
    path = resolve_file(image_path, base_dir) if base_dir else image_path
    mat = bpy.data.materials.new(name)
    nt, bsdf = _principled(mat)
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = load_image(path)
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.7
    return mat


def resolve_file(ref, base_dir):
    """Find a referenced file: as given, relative to base_dir, or by name in base_dir."""
    if not ref:
        return ref
    ref = ref.strip().strip('"').replace("\\", "/")
    candidates = [ref, os.path.join(base_dir, ref), os.path.join(base_dir, os.path.basename(ref))]
    for c in candidates:
        if os.path.isfile(c):
            return os.path.normpath(c)
    lower = os.path.basename(ref).lower()
    try:
        for f in os.listdir(base_dir):
            if f.lower() == lower:
                return os.path.join(base_dir, f)
    except OSError:
        pass
    return os.path.normpath(os.path.join(base_dir, ref)) if not os.path.isabs(ref) else ref


def build_mesh(name, verts, faces, *, loop_uvs=None, face_colors=None, face_materials=None,
               materials=None, collection=None):
    """Create a mesh object.

    verts          (N, 3) array
    faces          list of index sequences (or (flat_indices, totals) tuple of arrays)
    loop_uvs       (sum(len(face)), 2) array, one UV per face corner
    face_colors    (F, 3|4) sRGB 0..1 colours -> 'Col' face attribute
    face_materials (F,) material index per face
    """
    verts = np.asarray(verts, dtype=np.float64).reshape(-1, 3)
    if isinstance(faces, tuple):
        flat, totals = (np.asarray(a, dtype=np.int64) for a in faces)
    else:
        totals = np.fromiter((len(f) for f in faces), dtype=np.int64, count=len(faces))
        flat = np.fromiter(itertools.chain.from_iterable(faces), dtype=np.int64, count=int(totals.sum()))
    nv = len(verts)
    keep = totals >= 3
    bad = (flat < 0) | (flat >= nv)
    if bad.any():  # faces pointing at non-existing vertices
        face_of_loop = np.repeat(np.arange(len(totals)), totals)
        keep[np.unique(face_of_loop[bad])] = False
    if not keep.all():
        loop_mask = np.repeat(keep, totals)
        flat = flat[loop_mask]
        if loop_uvs is not None:
            loop_uvs = np.asarray(loop_uvs)[loop_mask]
        totals = totals[keep]
        if face_colors is not None:
            face_colors = np.asarray(face_colors)[keep]
        if face_materials is not None:
            face_materials = np.asarray(face_materials)[keep]
    flat, totals, loop_uvs, face_colors, face_materials = _repair_polygons(
        verts, flat, totals, loop_uvs, face_colors, face_materials)
    starts = np.zeros(len(totals), dtype=np.int64)
    if len(totals):
        starts[1:] = np.cumsum(totals)[:-1]

    me = bpy.data.meshes.new(name)
    me.vertices.add(nv)
    me.vertices.foreach_set("co", verts.astype(np.float32).ravel())
    me.loops.add(len(flat))
    me.loops.foreach_set("vertex_index", flat.astype(np.int32))
    me.polygons.add(len(totals))
    me.polygons.foreach_set("loop_start", starts.astype(np.int32))
    try:  # read-only (derived from loop_start) in Blender 4.0+
        me.polygons.foreach_set("loop_total", totals.astype(np.int32))
    except (AttributeError, TypeError, RuntimeError):
        pass
    if loop_uvs is not None and len(flat):
        uv = me.uv_layers.new(name="UVMap")
        uv.data.foreach_set("uv", np.asarray(loop_uvs, dtype=np.float32).reshape(-1, 2).ravel())
    for m in materials or ():
        me.materials.append(m)
    if face_materials is not None and len(totals):
        me.polygons.foreach_set("material_index", np.asarray(face_materials, dtype=np.int32))
    if face_colors is not None and len(totals):
        fc = np.asarray(face_colors, dtype=np.float64)
        rgba = np.ones((len(fc), 4))
        rgba[:, :3] = srgb_to_linear(fc[:, :3])
        if fc.shape[1] > 3:
            rgba[:, 3] = fc[:, 3]
        attr = me.color_attributes.new("Col", "FLOAT_COLOR", "FACE")
        attr.data.foreach_set("color", rgba.astype(np.float32).ravel())
    me.update(calc_edges=True)
    me.validate(clean_customdata=False)
    obj = bpy.data.objects.new(name, me)
    (collection or bpy.context.scene.collection).objects.link(obj)
    return obj


def _repair_polygons(verts, flat, totals, loop_uvs, face_colors, face_materials):
    """Polygons that reuse a vertex (holes bridged into the outline, e.g. exported
    concave polygons) would be deleted by Blender's mesh validation. Remove
    repeated neighbours and triangulate what is left (ear clipping)."""
    big = np.nonzero(totals >= 4)[0]
    if not len(big):
        return flat, totals, loop_uvs, face_colors, face_materials
    starts = np.zeros(len(totals), dtype=np.int64)
    starts[1:] = np.cumsum(totals)[:-1]
    quads = np.nonzero(totals == 4)[0]
    q = flat[starts[quads][:, None] + np.arange(4)]
    qbad = quads[(q[:, 0] == q[:, 1]) | (q[:, 0] == q[:, 2]) | (q[:, 0] == q[:, 3])
                 | (q[:, 1] == q[:, 2]) | (q[:, 1] == q[:, 3]) | (q[:, 2] == q[:, 3])]
    ngons = np.nonzero(totals > 4)[0]
    bad = sorted(qbad.tolist() + [int(f) for f in ngons
                                  if len(set(flat[starts[f]:starts[f] + totals[f]].tolist())) < totals[f]])
    if not bad:
        return flat, totals, loop_uvs, face_colors, face_materials
    new_loops, new_face_src = [], []
    for f in bad:
        loops = list(range(starts[f], starts[f] + totals[f]))
        loops = [l for k, l in enumerate(loops) if flat[l] != flat[loops[k - 1]]]  # drop repeated neighbours
        if len(loops) < 3:
            continue
        if len(set(flat[loops].tolist())) == len(loops):
            new_loops.append(loops)
            new_face_src.append(f)
            continue
        try:
            tris = tessellate_polygon([[Vector(verts[flat[l]]) for l in loops]])
        except Exception:
            tris = [(0, k, k + 1) for k in range(1, len(loops) - 1)]
        for t in tris:
            tri = [loops[i] for i in t]
            if len({int(flat[l]) for l in tri}) == 3:
                new_loops.append(tri)
                new_face_src.append(f)
    keep = np.ones(len(totals), dtype=bool)
    keep[bad] = False
    loop_keep = np.repeat(keep, totals)
    old_loop_ids = np.nonzero(loop_keep)[0]
    add_loops = np.array([l for lp in new_loops for l in lp], dtype=np.int64)
    all_loops = np.concatenate([old_loop_ids, add_loops])
    face_src = np.concatenate([np.nonzero(keep)[0], np.array(new_face_src, dtype=np.int64)])
    totals = np.concatenate([totals[keep], np.array([len(lp) for lp in new_loops], dtype=np.int64)])
    pick = lambda a: None if a is None else np.asarray(a)[face_src]  # noqa: E731
    uvs = None if loop_uvs is None else np.asarray(loop_uvs)[all_loops]
    return flat[all_loops], totals, uvs, pick(face_colors), pick(face_materials)


class MeshAccumulator:
    """Collects geometry from many parts and builds a single mesh object."""

    def __init__(self):
        self.verts, self.flat, self.totals = [], [], []
        self.uvs, self.colors, self.mats = [], [], []
        self.nverts = 0
        self.has_uv = False
        self.has_color = False
        self.materials = []
        self._mat_index = {}

    def material_index(self, key, factory):
        if key not in self._mat_index:
            self._mat_index[key] = len(self.materials)
            self.materials.append(factory())
        return self._mat_index[key]

    def add(self, verts, faces, *, loop_uvs=None, face_colors=None, face_material=0):
        verts = np.asarray(verts, dtype=np.float64).reshape(-1, 3)
        if isinstance(faces, tuple):
            flat, totals = (np.asarray(a, dtype=np.int64) for a in faces)
        else:
            totals = np.fromiter((len(f) for f in faces), dtype=np.int64, count=len(faces))
            flat = np.fromiter(itertools.chain.from_iterable(faces), dtype=np.int64, count=int(totals.sum()))
        if not len(totals):
            return
        self.verts.append(verts)
        self.flat.append(flat + self.nverts)
        self.totals.append(totals)
        self.nverts += len(verts)
        if loop_uvs is not None:
            self.has_uv = True
            self.uvs.append(np.asarray(loop_uvs, dtype=np.float64).reshape(-1, 2))
        else:
            self.uvs.append(np.zeros((len(flat), 2)))
        if face_colors is not None:
            self.has_color = True
            fc = np.asarray(face_colors, dtype=np.float64).reshape(len(totals), -1)[:, :3]
            self.colors.append(fc)
        else:
            self.colors.append(np.full((len(totals), 3), 0.8))
        fm = np.broadcast_to(np.asarray(face_material, dtype=np.int64), (len(totals),))
        self.mats.append(fm)

    @property
    def face_count(self):
        return int(sum(len(t) for t in self.totals))

    def build(self, name, collection=None):
        if not self.totals:
            return None
        return build_mesh(
            name, np.concatenate(self.verts),
            (np.concatenate(self.flat), np.concatenate(self.totals)),
            loop_uvs=np.concatenate(self.uvs) if self.has_uv else None,
            face_colors=np.concatenate(self.colors) if self.has_color else None,
            face_materials=np.concatenate(self.mats) if self.materials else None,
            materials=self.materials, collection=collection)


def weld(positions, tol=0.0):
    """Merge identical positions. Returns (unique_positions, inverse_index)."""
    p = np.asarray(positions, dtype=np.float64).reshape(-1, 3)
    if not len(p):
        return p, np.zeros(0, dtype=np.int64)
    key = np.round(p / tol) if tol > 0 else p
    _, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    return p[first], inv.reshape(-1)
