"""OpenFlight (.flt) -> scene model, for inspection and optimisation.

The record hierarchy is kept (header, groups, objects, LODs, switches, DOFs,
instances, external references). Faces of an object are gathered into one
geometry per texture, so a 20 000 face object shows up as one or a few
drawables - which is also how OpenSceneGraph's OpenFlight loader builds it.
"""

import os
import struct

import numpy as np

from . import images
from . import flt_records as F
from . import model as M

KIND = {
    F.HEADER: (M.GROUP, "FLT Header"), F.GROUP: (M.GROUP, "FLT Group"), F.OBJECT: (M.GEODE, "FLT Object"),
    F.LOD: (M.LOD, "FLT LOD"), F.SWITCH: (M.SWITCH, "FLT Switch"), F.DOF: (M.TRANSFORM, "FLT DOF"),
    F.EXT_REF: (M.PROXY, "FLT External Reference"), F.INST_DEF: (M.GROUP, "FLT Instance Definition"),
    F.BSP: (M.GROUP, "FLT BSP"), F.LIGHT_PT: (M.OTHER, "FLT Light Point"), F.INDEXED_LP: (M.OTHER, "FLT Light Point"),
    90: (M.OTHER, "FLT Light Source"), 91: (M.OTHER, "FLT Sound"), 95: (M.OTHER, "FLT Text"),
    98: (M.OTHER, "FLT Road Segment"), 100: (M.OTHER, "FLT Road Path"), 101: (M.OTHER, "FLT Clip"),
    112: (M.OTHER, "FLT CAT"), 114: (M.OTHER, "FLT Extension"), 115: (M.OTHER, "FLT Curve"),
    127: (M.OTHER, "FLT Light Point System"), 129: (M.OTHER, "FLT Light Point"),
}


class Builder:
    def __init__(self, flt, scene):
        self.flt = flt
        self.scene = scene
        self.textures = {}
        self.statesets = {}
        self.inst_nodes = {}
        self.hidden_faces = 0

    def texture(self, idx):
        if idx is None or idx < 0 or idx not in self.flt.textures:
            return None
        t = self.textures.get(idx)
        if t is None:
            t = M.Texture(self.flt.textures[idx])
            images.fill_texture(t, self.flt.dir)
            self.textures[idx] = t
        return t

    def stateset(self, tex_idx, mat_idx=None):
        key = (tex_idx, mat_idx)
        ss = self.statesets.get(key)
        if ss is None:
            ss = M.StateSet()
            t = self.texture(tex_idx)
            if t is not None:
                ss.textures.append(t)
                ss.summary.append(f"Texture2D[0] {t.label}")
            mat = self.flt.materials.get(mat_idx) if mat_idx is not None and mat_idx >= 0 else None
            if mat is not None:
                ss.material_name = mat[0]
                ss.diffuse = (*[float(x) for x in mat[1]], float(mat[2]))
                ss.transparent = mat[2] < 0.999
                ss.summary.append(f"Material '{mat[0]}'")
            if t is None and mat is None:
                ss.summary.append("vertex colours")
            self.statesets[key] = ss
        return ss

    def build(self):
        return self.node(self.flt.root, 0)

    def node(self, rec, depth):
        return _with_transform(self._node(rec, depth))

    def _node(self, rec, depth):
        op = rec.op
        if op == F.INST_REF and len(rec.data) >= 8:
            num = struct.unpack_from(">h", rec.data, 6)[0]
            target = self.flt.inst_defs.get(num)
            n = M.Node(M.GROUP, "FLT Instance Reference", f"instance {num}")
            n.src = rec
            if target is not None:
                inst = self.inst_nodes.get(num)
                if inst is None:
                    inst = self.node(target, depth + 1)
                    self.inst_nodes[num] = inst
                n.children.append(inst)
            else:
                n.notes.append(f"instance definition {num} not found")
            self._matrix(rec, n)
            return n
        kind, cls = KIND.get(op, (M.OTHER, f"FLT record {op}"))
        n = M.Node(kind, cls, rec.name or "")
        n.src = rec
        self._matrix(rec, n)
        d = rec.data
        if op == F.LOD and len(d) >= 32:
            switch_in, switch_out = struct.unpack_from(">dd", d, 16)
            n.ranges = [(switch_out, switch_in)]
            n.notes.append(f"switch in {switch_in:g} / out {switch_out:g}")
        elif op == F.SWITCH:
            vals = self._switch_mask(rec)
            if vals is not None:
                n.switch_values = vals
        elif op == F.EXT_REF:
            ref = F._cstr(d[4:204]).split("<", 1)[0].strip() if len(d) >= 8 else ""
            n.files = [ref] if ref else []
            n.name = n.name or os.path.basename(ref)
        elif op == F.OBJECT and len(d) >= 16 and struct.unpack_from(">I", d, 12)[0] & 0x04000000:
            n.notes.append("shadow object")
        elif op == F.GROUP and len(d) >= 20:
            flags = struct.unpack_from(">I", d, 16)[0]
            if flags & (0x40000000 | 0x20000000 | 0x02000000):
                n.notes.append("animated group")
        faces = []
        meshes = []
        for c in rec.children:
            if c.op == F.FACE:
                faces.append(c)
                continue
            if c.op == F.MESH:
                meshes.append(c)
                continue
            if c.op == F.INST_DEF:
                continue  # drawn through instance references
            if depth > 400:
                n.notes.append("hierarchy too deep, truncated")
                break
            n.children.append(self.node(c, depth + 1))
        if faces:
            n.children[:0] = self.face_geometries(faces)
        for mrec in meshes:
            g = self.mesh_geometry(mrec)
            if g is not None:
                n.children.append(g)
        return n

    def _matrix(self, rec, n):
        if rec.matrix is not None:
            n.matrix = rec.matrix.copy()

    @staticmethod
    def _switch_mask(rec):
        d = rec.data
        try:
            cur, words, nmasks = struct.unpack_from(">iii", d, 16)
            if not (0 <= cur < nmasks) or words <= 0:
                return None
            masks = struct.unpack_from(f">{words}I", d, 28 + cur * words * 4)
        except struct.error:
            return None
        cnt = len(rec.children)
        return [bool((masks[i // 32] >> (i % 32)) & 1) if i // 32 < len(masks) else True for i in range(cnt)]

    def face_geometries(self, faces):
        """Faces of one parent -> one Geometry node per texture."""
        flt = self.flt
        buckets = {}
        for f in faces:
            hidden, tex, color = F.face_attributes(f.data, 0, flt)
            if hidden:
                self.hidden_faces += 1
                continue
            if not f.vlists:
                continue
            offs = np.concatenate(f.vlists)
            if len(offs) < 3:
                continue
            idx = flt.palette_indices(offs)
            if idx is None:
                continue
            draw_type = struct.unpack_from(">b", f.data, 18)[0] if len(f.data) > 18 else 0
            # some writers (e.g. osgconv) put a matrix record on single faces
            mat = struct.unpack_from(">h", f.data, 30)[0] if len(f.data) >= 32 else -1
            # one geometry per texture + material, like OpenSceneGraph's own OpenFlight loader
            buckets.setdefault((tex if tex is not None else -1, mat), []).append((idx, color, draw_type, f.matrix))
        out = []
        for (tex, mat), items in buckets.items():
            g = M.GeomData()
            col_ids = {}
            mat_ids = {}
            mats = []
            keys = []
            tris = []
            lines = []
            for idx, color, draw_type, fmat in items:
                ckey = None if color is None else tuple(np.round(np.asarray(color, dtype=np.float64), 4))
                cid = col_ids.setdefault(ckey, len(col_ids))
                mid = -1
                if fmat is not None:
                    mk = tuple(np.round(fmat, 9).ravel())
                    mid = mat_ids.get(mk)
                    if mid is None:
                        mid = mat_ids[mk] = len(mats)
                        mats.append(fmat)
                ks = [(int(i), cid, mid) for i in idx]
                start = len(keys)
                keys.extend(ks)
                local = np.arange(start, start + len(ks), dtype=np.int64)
                if draw_type in (2, 3):  # closed / open wireframe
                    lines.append(M.mode_lines("LINE_LOOP" if draw_type == 2 else "LINE_STRIP", local))
                else:
                    tris.append(M.mode_triangles("POLYGON", local))
            # weld identical (palette vertex, colour) pairs
            karr = np.array(keys, dtype=np.int64).reshape(-1, 3)
            uniq, inv = np.unique(karr, axis=0, return_inverse=True)
            inv = inv.reshape(-1)
            pal = uniq[:, 0]
            pos = flt.positions[pal].astype(np.float64)
            nrm = flt.normals[pal].astype(np.float64) if getattr(flt, "has_normals", False) else None
            for mid, fm in enumerate(mats):
                sel = uniq[:, 2] == mid
                pos[sel] = pos[sel] @ fm[:3, :3].T + fm[:3, 3]
                if nrm is not None:
                    nn = nrm[sel] @ np.linalg.inv(fm[:3, :3])
                    ln = np.linalg.norm(nn, axis=1, keepdims=True)
                    nrm[sel] = np.where(ln > 0, nn / np.where(ln > 0, ln, 1), nn)
            g.vertices = M.Array(pos, "Vec3Array")
            if nrm is not None and np.any(nrm):
                g.normals = M.Array(nrm, "Vec3Array")
            if tex >= 0:
                g.texcoords[0] = M.Array(flt.uvs[pal].astype(np.float64), "Vec2Array")
            colors = [None] * len(col_ids)
            for ck, ci in col_ids.items():
                colors[ci] = ck
            if any(c is not None for c in colors):
                cv = np.array([(c if c is not None else (0.8, 0.8, 0.8)) for c in colors], dtype=np.float64)
                cv = np.concatenate([cv[:, :3], np.ones((len(cv), 1))], axis=1)
                if len(cv) == 1:
                    g.colors = M.Array(cv, "Vec4Array", "overall")
                else:
                    g.colors = M.Array(cv[uniq[:, 1]], "Vec4Array")
            if tris:
                t = inv[np.concatenate(tris)].reshape(-1)
                g.prims.append(M.Primitive("TRIANGLES", "elements", indices=t, index_type=_index_type(len(pal))))
            if lines:
                ln = inv[np.concatenate(lines)].reshape(-1)
                g.prims.append(M.Primitive("LINES", "elements", indices=ln, index_type=_index_type(len(pal))))
            n = M.Node(M.GEOMETRY, "FLT Faces", (os.path.basename(self.flt.textures.get(tex, "")) if tex >= 0 else "untextured"))
            n.geom = g
            n.stateset = self.stateset(tex if tex >= 0 else None, mat)
            n.extra["faces"] = len(items)
            out.append(n)
        return out

    def mesh_geometry(self, rec):
        if rec.lvp is None:
            return None
        hidden, tex, color = F.face_attributes(rec.data, 4, self.flt)
        if hidden:
            self.hidden_faces += 1
            return None
        pos, uv = F.lvp_arrays(rec.lvp)
        if not len(pos):
            return None
        g = M.GeomData()
        g.vertices = M.Array(pos * self.flt.scale, "Vec3Array")
        if uv is not None and tex is not None and tex >= 0:
            g.texcoords[0] = M.Array(uv, "Vec2Array")
        for p in rec.prims:
            ptype, isize, count = struct.unpack_from(">hHI", p, 4)
            dt = {1: "u1", 2: ">u2", 4: ">u4"}.get(isize)
            if dt is None or count == 0:
                continue
            idx = np.frombuffer(p, dtype=dt, count=count, offset=12).astype(np.int64)
            mode = {1: "TRIANGLE_STRIP", 2: "TRIANGLE_FAN", 3: "QUAD_STRIP", 4: "POLYGON"}.get(ptype)
            if mode:
                g.prims.append(M.Primitive(mode, "elements", indices=idx, index_type=_index_type(len(pos))))
        if color is not None:
            g.colors = M.Array(np.array([[*color[:3], 1.0]]), "Vec4Array", "overall")
        n = M.Node(M.GEOMETRY, "FLT Mesh", rec.name or "")
        n.geom = g
        n.stateset = self.stateset(tex if tex is not None and tex >= 0 else None)
        n.src = rec
        return n


def _with_transform(n):
    """FLT matrix records can sit on any node. Make them explicit so analysis, the optimiser and the
    OSG writer all see a transform: groups become transforms, other nodes get a transform parent."""
    if n.matrix is None or n.kind in (M.TRANSFORM, M.GEOMETRY):
        return n
    if n.kind == M.GROUP:
        n.kind = M.TRANSFORM
        return n
    wrap = M.Node(M.TRANSFORM, "FLT Matrix", n.name)
    wrap.matrix, n.matrix = n.matrix, None
    wrap.children = [n]
    return wrap


def _index_type(nverts):
    return "DrawElementsUShort" if nverts < 65536 else "DrawElementsUInt"


def read(path):
    flt = F.FltFile(path)
    scene = M.Scene(os.path.abspath(path), "flt")
    b = Builder(flt, scene)
    scene.root = b.build()
    scene.root.name = scene.root.name or os.path.basename(path)
    scene.info.update({
        "openflight_revision": flt.revision, "units": flt.unit, "records": dict(flt.stats),
        "texture_palette": len(flt.textures), "vertex_palette": int(len(flt.positions)),
        "hidden_faces": b.hidden_faces,
    })
    if flt.scale != 1.0:
        scene.info["unit_scale"] = flt.scale
    scene.editable = False  # FLT itself is not written back; the optimised result is saved as OSG
    return scene
