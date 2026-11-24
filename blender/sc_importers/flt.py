"""OpenFlight (.flt) reader - pure Python, no add-on needed.

Supports the records that carry visible geometry in real-world databases:
header (units), vertex palette (68-71), face + vertex list / morph list, mesh
+ local vertex pool + mesh primitives (strips, fans, quad strips, polygons),
continuation records, matrices, groups (animated groups show frame 0),
objects (shadow objects skipped), LODs (only the most detailed LOD of a set of
siblings is kept), switches (current mask), instance definitions/references,
external references (loaded recursively), texture palette and colour palette.
Light points, hidden faces and extension blocks are skipped.
"""

import os
import struct

import numpy as np

# record parsing is shared with the scene inspector and needs no Blender
from scenekit.flt_records import (EXT_REF, FACE, GROUP, INST_DEF, INST_REF, LOD, MAX_XREF_DEPTH, MESH, OBJECT,
                                  SKIP_SUBTREE, SWITCH, FltFile, _cstr, face_attributes, lvp_arrays,
                                  primitive_faces)

from . import meshbuild as mb


class Loader:
    def __init__(self, path, ctx):
        self.ctx = ctx
        self.cache = {}
        self.missing_refs = []
        self.acc = mb.MeshAccumulator()
        self.face_items = {}  # (file id, matrix id) -> [file, matrix, [(offsets, tex, color)]]
        self.lods_dropped = 0
        self.main = FltFile(path)
        self.cache[self.main.path] = self.main
        self._keep = []

    def xref(self, flt, node, depth):
        ref = _cstr(node.data[4:204]).split("<", 1)[0].strip()
        if not ref:
            return None
        path = mb.resolve_file(ref, flt.dir)
        key = os.path.abspath(path)
        if key in self.cache:
            return self.cache[key]
        sub = None
        if depth < MAX_XREF_DEPTH and os.path.isfile(path):
            try:
                sub = FltFile(path)
            except Exception as exc:
                self.ctx.log(f"external reference {ref} unreadable: {exc}")
        if sub is None:
            self.missing_refs.append(ref)
        self.cache[key] = sub
        return sub

    def walk(self, flt, node, M, depth=0, xdepth=0):
        if depth > 500:
            return
        if node.matrix is not None:
            M = M @ node.matrix
            self._keep.append(M)
        op = node.op
        if op in SKIP_SUBTREE:
            return
        if op == OBJECT and len(node.data) >= 16 and struct.unpack_from(">I", node.data, 12)[0] & 0x04000000:
            return  # shadow object
        if op == FACE:
            hidden, tex, color = face_attributes(node.data, 0, flt)
            if not hidden:
                offsets = np.concatenate(node.vlists) if node.vlists else None
                if offsets is not None and len(offsets) >= 3:
                    item = self.face_items.setdefault((id(flt), id(M)), [flt, M, []])
                    item[2].append((offsets, tex, color))
        elif op == MESH and node.lvp is not None:
            hidden, tex, color = face_attributes(node.data, 4, flt)
            if not hidden:
                self.add_mesh(flt, node, M, tex, color)
        elif op == EXT_REF:
            sub = self.xref(flt, node, xdepth)
            if sub is not None:
                self.walk(sub, sub.root, M, depth + 1, xdepth + 1)
            return
        elif op == INST_REF and len(node.data) >= 8:
            target = flt.inst_defs.get(struct.unpack_from(">h", node.data, 6)[0])
            if target is not None:
                for c in target.children:
                    self.walk(flt, c, M, depth + 1, xdepth)
            return
        elif op == INST_DEF:
            return  # only drawn through instance references
        children = node.children
        if op == GROUP and len(node.data) >= 20:
            flags = struct.unpack_from(">I", node.data, 16)[0]
            if flags & (0x40000000 | 0x20000000 | 0x02000000):  # animated group: first frame only
                children = children[:1]
        elif op == SWITCH:
            children = self.switch_children(node)
        lods = [c for c in children if c.op == LOD and len(c.data) >= 32]
        if len(lods) > 1:
            best = min(lods, key=lambda c: (struct.unpack_from(">d", c.data, 24)[0], -struct.unpack_from(">d", c.data, 16)[0]))
            self.lods_dropped += len(lods) - 1
            children = [c for c in children if c.op != LOD or c is best]
        for c in children:
            self.walk(flt, c, M, depth + 1, xdepth)

    @staticmethod
    def switch_children(node):
        d = node.data
        try:
            cur, words, nmasks = struct.unpack_from(">iii", d, 16)
            if not (0 <= cur < nmasks) or words <= 0:
                return node.children
            masks = struct.unpack_from(f">{words}I", d, 28 + cur * words * 4)
        except struct.error:
            return node.children
        return [c for i, c in enumerate(node.children) if (masks[i // 32] >> (i % 32)) & 1] if len(masks) * 32 >= len(node.children) else node.children

    def material_for(self, flt, tex):
        if tex is not None and tex >= 0 and tex in flt.textures:
            path = mb.resolve_file(flt.textures[tex], flt.dir)
            return self.acc.material_index(("tex", path), lambda: mb.image_material(os.path.basename(path), path))
        return self.acc.material_index("color", mb.color_attribute_material)

    def add_mesh(self, flt, node, M, tex, color):
        pos, uv = lvp_arrays(node.lvp)
        if not len(pos):
            return
        faces = []
        for p in node.prims:
            faces.extend(primitive_faces(p))
        if not faces:
            return
        pos = pos * flt.scale
        verts = pos @ M[:3, :3].T + M[:3, 3]
        loop_uv = None
        if uv is not None:
            loop_uv = uv[np.fromiter((i for f in faces for i in f), dtype=np.int64)]
        col = np.tile(color if color is not None else (0.8, 0.8, 0.8), (len(faces), 1))
        self.acc.add(verts, faces, loop_uvs=loop_uv, face_colors=col, face_material=self.material_for(flt, tex))

    def flush_faces(self):
        for flt, M, items in self.face_items.values():
            groups = {}
            for offsets, tex, color in items:
                groups.setdefault(tex, []).append((offsets, color))
            for tex, faces in groups.items():
                lists, totals, cols = [], [], []
                for offsets, color in faces:
                    idx = flt.palette_indices(offsets)
                    if idx is None:
                        continue
                    lists.append(idx)
                    totals.append(len(idx))
                    cols.append(color if color is not None else (0.8, 0.8, 0.8))
                if not lists:
                    continue
                allp = np.concatenate(lists)
                uniq, inv = np.unique(allp, return_inverse=True)
                verts = flt.positions[uniq] @ M[:3, :3].T + M[:3, 3]
                self.acc.add(verts, (inv.reshape(-1), np.array(totals)), loop_uvs=flt.uvs[allp],
                             face_colors=np.array(cols), face_material=self.material_for(flt, tex))


def load(path, ctx):
    loader = Loader(path, ctx)
    loader.walk(loader.main, loader.main.root, np.eye(4))
    loader.flush_faces()
    obj = loader.acc.build(os.path.splitext(os.path.basename(path))[0])
    f = loader.main
    stats = dict(f.stats)
    for sub in loader.cache.values():
        if sub is not None and sub is not f:
            for k, v in sub.stats.items():
                stats[k] = stats.get(k, 0) + v
    details = {
        "openflight_revision": f.revision,
        "units": f.unit,
        "records": stats,
        "lods_hidden": loader.lods_dropped,
        "external_refs_loaded": sum(1 for p, s in loader.cache.items() if s is not None and s is not f),
        "external_refs_missing": sorted(set(loader.missing_refs)),
        "texture_palette": len(f.textures),
    }
    if loader.missing_refs:
        ctx.add_issue("missing_references", f"{len(set(loader.missing_refs))} external reference(s) not found: "
                      + ", ".join(sorted(set(loader.missing_refs))[:5]))
    if loader.lods_dropped:
        ctx.add_issue("lods_hidden", f"{loader.lods_dropped} lower-detail LOD(s) not rendered")
    if obj is None:
        ctx.log("OpenFlight file contains no visible faces")
    return details
