"""OpenSceneGraph text formats (.osgt, legacy .osg) - pure Python reader for the render pipeline.

Parsing is shared with the scene inspector (``scenekit``). Transforms (MatrixTransform,
PositionAttitudeTransform), instancing, ProxyNode external files, switches and node
masks are honoured; of an LOD only the most detailed child is built (like the
OpenFlight reader). PagedLOD files are not followed: the tile's own (coarse) children
are shown. OSG is Z-up like Blender, so no axis conversion is needed.
"""

import os

import bpy
import numpy as np

from scenekit import LoadOptions, load_scene
from scenekit import model as M
from scenekit.osgread import decode_inline, to_int

from . import meshbuild as mb

MAX_NODES = 2_000_000


def _lod_children(n):
    """Most detailed child of an LOD: the one whose range starts closest to the eye."""
    kids = n.all_children()
    if n.kind not in (M.LOD, M.PAGED_LOD) or len(kids) <= 1 or not n.ranges:
        return kids, 0
    inline = n.children
    if not inline:
        return kids, 0
    k = min(range(min(len(inline), len(n.ranges))), key=lambda i: (n.ranges[i][0], -n.ranges[i][1]))
    return [inline[k]], len(kids) - 1


def _sibling_lods(kids):
    """OpenFlight-style detail levels: several single-level LOD nodes side by side (one per FLT LOD
    record). Keep only the levels that start closest to the eye, like the OpenFlight reader does."""
    lods = [c for c in kids if c.kind == M.LOD and c.ranges and len(c.children) <= 1]
    if len(lods) < 2:
        return kids, 0
    near = min(c.ranges[0][0] for c in lods)
    drop = {id(c) for c in lods if c.ranges[0][0] > near}
    return [c for c in kids if id(c) not in drop], len(drop)


class Builder:
    def __init__(self, ctx, base_dir):
        self.ctx = ctx
        self.base_dir = base_dir
        self.acc = mb.MeshAccumulator()
        self.lods_hidden = 0
        self.switched_off = 0
        self.hidden = 0
        self.visited = 0
        self.missing_tex = set()
        self.loop_normals = []

    @staticmethod
    def corner_normals(g, tris, verts, W):
        """The file's own normals per triangle corner (world space), so Blender shades like OSG does."""
        n = g.normals
        nm = None
        if n is not None and n.binding in ("vertex", "overall"):
            d = np.asarray(n.data, dtype=np.float64).reshape(len(n.data), -1)
            if d.shape[1] >= 3:
                d = d[:, :3]
                if n.binding == "vertex" and len(d) == len(verts):
                    nm = d[tris]
                elif len(d):
                    nm = np.broadcast_to(d[0], tris.shape + (3,)).copy()
        if nm is None:  # no usable normals: flat face normals
            p = verts[tris]
            fn = np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0])
            return np.repeat(fn[:, None, :], 3, axis=1).reshape(-1, 3)
        try:
            nmat = np.linalg.inv(W[:3, :3]).T
        except np.linalg.LinAlgError:
            nmat = np.eye(3)
        out = nm.reshape(-1, 3) @ nmat.T
        bad = np.linalg.norm(out, axis=1) < 1e-12
        if bad.any():  # zero normals: fall back to the face normal
            p = verts[tris]
            fn = np.repeat(np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0])[:, None, :], 3, axis=1).reshape(-1, 3)
            out[bad] = fn[bad]
        return out

    def apply_normals(self, obj):
        if obj is None or not self.loop_normals:
            return
        ln = np.concatenate(self.loop_normals)
        me = obj.data
        if len(ln) != len(me.loops):
            self.ctx.log("normals not applied (mesh was repaired on import)")
            return
        ln = ln / np.maximum(np.linalg.norm(ln, axis=1, keepdims=True), 1e-12)
        try:
            me.normals_split_custom_set(ln)
        except Exception as exc:  # older Blender: needs a list of tuples
            try:
                me.normals_split_custom_set([tuple(x) for x in ln.tolist()])
            except Exception:
                self.ctx.log(f"normals not applied: {exc}")

    def material(self, ss, use_colors, uniform=None):
        tex = None
        if ss is not None:
            tex = next((t for t in ss.textures if t.unit == 0), None) or (ss.textures[0] if ss.textures else None)
        if tex is not None and tex.embedded:
            img = self.embedded_image(tex)
            if img is not None:
                return self.acc.material_index(("inline", id(tex)), lambda: self.inline_material(tex, img))
        if tex is not None and (tex.path or tex.file):
            path = tex.path or mb.resolve_file(tex.file, self.base_dir)
            if not tex.exists and not tex.embedded:
                self.missing_tex.add(tex.file)
            return self.acc.material_index(("tex", path), lambda: mb.image_material(os.path.basename(path) or "texture", path))
        if use_colors:
            return self.acc.material_index("color", mb.color_attribute_material)
        base = ss.diffuse[:3] if ss is not None and ss.diffuse else (0.8, 0.8, 0.8)
        if uniform is not None:  # vertex colour (with lighting materials usually ColorMode DIFFUSE)
            base = uniform
        rgb = tuple(round(float(c), 3) for c in base)
        name = (ss.material_name if ss is not None and ss.material_name else "OSG") + f" {rgb}"
        return self.acc.material_index(("rgb", rgb, name), lambda: mb.diffuse_material(name, rgb))

    @staticmethod
    def embedded_image(tex):
        """Image data stored inside an .osgt file (raw 8-bit pixels, base64) -> packed Blender image."""
        src = tex.image_src
        if src is None or not tex.width or not tex.height:
            return None
        if to_int(src.value("DataType", "5121")) != 5121:
            return None
        pf = to_int(src.value("PixelFormat", "0"))
        ch = {6407: 3, 6408: 4, 6409: 1, 6410: 2, 32992: 3, 32993: 4}.get(pf)
        raw = decode_inline(src.get("Data")) if ch else None
        n = tex.width * tex.height * (ch or 0)
        if not raw or len(raw) < n:
            return None
        px = np.frombuffer(raw[:n], dtype=np.uint8).reshape(tex.height, tex.width, ch).astype(np.float32) / 255.0
        if pf in (32992, 32993):  # GL_BGR / GL_BGRA
            px = px[..., [2, 1, 0] + ([3] if ch == 4 else [])]
        rgba = np.ones((tex.height, tex.width, 4), dtype=np.float32)
        if ch <= 2:
            rgba[..., :3] = px[..., :1]
            if ch == 2:
                rgba[..., 3] = px[..., 1]
        else:
            rgba[..., :ch] = px
        if to_int(src.value("Origin", "0")) == 1:  # TOP_LEFT: Blender stores rows bottom-up
            rgba = rgba[::-1]
        name = os.path.basename(tex.file) or "embedded"
        img = bpy.data.images.new(name, tex.width, tex.height, alpha=ch in (2, 4))
        img.pixels.foreach_set(rgba.ravel())
        img.pack()
        return img

    @staticmethod
    def inline_material(tex, img):
        mat = mb.image_material(img.name, "")
        for node in mat.node_tree.nodes:
            if node.type == "TEX_IMAGE":
                old = node.image
                node.image = img
                if old is not None and old.users == 0:
                    bpy.data.images.remove(old)
        return mat

    def walk(self, root):
        stack = [(root, np.eye(4), None)]
        while stack:
            n, W, ss = stack.pop()
            self.visited += 1
            if self.visited > MAX_NODES:
                self.ctx.log("scene graph too large, stopped walking")
                break
            if not n.node_mask:
                self.hidden += 1
                continue
            if n.matrix is not None:
                W = W @ n.matrix
            ss = n.stateset or ss
            if n.kind == M.GEOMETRY and n.geom is not None:
                self.add_geometry(n.geom, W, n.stateset or ss)
                continue
            kids, dropped = _lod_children(n)
            self.lods_hidden += dropped
            kids, dropped = _sibling_lods(kids)
            self.lods_hidden += dropped
            if n.kind == M.SWITCH and n.switch_values is not None:
                vals = n.switch_values
                on = [c for i, c in enumerate(n.children) if i >= len(vals) or vals[i]]
                self.switched_off += len(n.children) - len(on)
                kids = on
            for c in reversed(kids):
                stack.append((c, W, ss))

    def add_geometry(self, g, W, ss):
        tris = g.triangles()
        if not len(tris) or g.vertices is None:
            return
        v = np.asarray(g.vertices.data, dtype=np.float64)
        if v.ndim != 2 or v.shape[1] < 3:
            return
        v = v[:, :3]
        ok = np.all((tris >= 0) & (tris < len(v)), axis=1)
        ok &= (tris[:, 0] != tris[:, 1]) & (tris[:, 1] != tris[:, 2]) & (tris[:, 0] != tris[:, 2])
        tris = tris[ok]
        if not len(tris):
            return
        verts = v @ W[:3, :3].T + W[:3, 3]
        if np.linalg.det(W[:3, :3]) < 0:
            tris = tris[:, [0, 2, 1]]
        self.loop_normals.append(self.corner_normals(g, tris, verts, W))
        uv = None
        tc = g.texcoords.get(0)
        if tc is not None and tc.binding == "vertex" and len(tc.data) == len(v):
            d = np.asarray(tc.data, dtype=np.float64).reshape(len(v), -1)
            if d.shape[1] >= 2:
                uv = d[:, :2][tris.reshape(-1)]
        colors = None
        uniform = None
        c = g.colors
        if c is not None:
            cd = np.asarray(c.data, dtype=np.float64)
            cd = cd.reshape(len(cd), -1) if cd.ndim > 1 else cd.reshape(-1, 1)
            if c.type.endswith("ubArray"):
                cd = cd / 255.0
            if cd.shape[1] >= 3:
                if c.binding == "vertex" and len(cd) == len(v) and not np.all(cd[:, :3] == cd[0, :3]):
                    colors = cd[tris][:, :, :3].mean(axis=1)
                elif len(cd):
                    uniform = tuple(float(x) for x in cd[0, :3])  # one colour: a plain material is enough
        mat = self.material(ss, colors is not None, uniform)
        self.acc.add(verts, (tris.reshape(-1), np.full(len(tris), 3)), loop_uvs=uv, face_colors=colors,
                     face_material=mat)


def load(path, ctx):
    scene = load_scene(path, LoadOptions(follow_proxy=True, follow_paged=False, osgconv=""))
    b = Builder(ctx, os.path.dirname(os.path.abspath(path)))
    b.walk(scene.root)
    obj = b.acc.build(os.path.splitext(os.path.basename(path))[0])
    b.apply_normals(obj)
    externals = [p for p, s in scene.externals.items() if s is not None]
    missing_ext = sorted({w.split(": ", 1)[1] for w in scene.warnings if w.startswith("external file not found")})
    paged = sum(1 for n in scene.iter_nodes() if n.kind == M.PAGED_LOD)
    details = {
        "osg_format": scene.fmt,
        "osg_version": scene.version,
        "generator": scene.info.get("generator"),
        "lods_hidden": b.lods_hidden,
        "switched_off": b.switched_off,
        "hidden_nodes": b.hidden,
        "external_files_loaded": len(externals),
        "external_files_missing": missing_ext,
        "paged_lods": paged,
    }
    if missing_ext:
        ctx.add_issue("missing_references", f"{len(missing_ext)} external file(s) not found: " + ", ".join(missing_ext[:5]))
    if b.lods_hidden:
        ctx.add_issue("lods_hidden", f"{b.lods_hidden} lower-detail LOD(s) not rendered")
    for w in scene.warnings:
        if not w.startswith("external file not found"):
            ctx.log(w)
    if obj is None:
        ctx.log("OpenSceneGraph file contains no visible triangles")
    return details
