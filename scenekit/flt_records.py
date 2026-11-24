"""OpenFlight (.flt) record parsing - pure Python + numpy, shared by the Blender
importer (``blender/sc_importers/flt.py``) and the scene inspector (``fltread``).

Reads the record stream into a node tree (groups, objects, LODs, switches, DOFs,
instances, external references, faces, meshes) plus the vertex, texture and
colour palettes.
"""

import os
import struct

import numpy as np

MAX_XREF_DEPTH = 6

HEADER, GROUP, OBJECT, FACE, PUSH, POP, DOF = 1, 2, 4, 5, 10, 11, 14
PUSH_SUB, POP_SUB, PUSH_EXT, POP_EXT, CONT = 19, 20, 21, 22, 23
COLOR_PAL, LONG_ID, MATRIX, BSP = 32, 33, 49, 55
INST_REF, INST_DEF, EXT_REF, TEX_PAL = 61, 62, 63, 64
VTX_PAL, VTX_C, VTX_CN, VTX_CNT, VTX_CT, VTX_LIST, LOD = 67, 68, 69, 70, 71, 72, 73
MESH, LVP, MESH_PRIM, MORPH_LIST, SWITCH = 84, 85, 86, 89, 96
LIGHT_PT, INDEXED_LP, PUSH_ATTR, POP_ATTR = 111, 130, 122, 123
MATERIAL_PAL = 113
NODE_OPS = {GROUP, OBJECT, FACE, DOF, BSP, INST_REF, INST_DEF, EXT_REF, LOD, MESH, SWITCH,
            LIGHT_PT, INDEXED_LP, 90, 91, 95, 98, 100, 101, 112, 114, 115, 127, 129}
SKIP_SUBTREE = {LIGHT_PT, INDEXED_LP, 91, 98, 101, 112, 114}

UNITS = {0: 1.0, 1: 1000.0, 4: 0.3048, 5: 0.0254, 8: 1852.0}
UNIT_NAMES = {0: "meters", 1: "kilometers", 4: "feet", 5: "inches", 8: "nautical miles"}

def _cstr(b):
    return b.split(b"\x00", 1)[0].decode("latin-1", "replace").strip()


class Node:
    __slots__ = ("op", "data", "children", "matrix", "vlists", "lvp", "prims", "name")

    def __init__(self, op, data):
        self.op = op
        self.data = data
        self.children = []
        self.matrix = None
        self.vlists = []
        self.lvp = None
        self.prims = []
        self.name = None


def read_records(data):
    """-> list of [opcode, bytes, file_offset]; continuation records are merged."""
    recs = []
    i, n = 0, len(data)
    while i + 4 <= n:
        op, ln = struct.unpack_from(">HH", data, i)
        if ln < 4:
            raise ValueError(f"corrupt OpenFlight record (opcode {op}, length {ln}) at byte {i}")
        ln = min(ln, n - i)
        if op == CONT and recs:
            recs[-1][1].append(data[i + 4:i + ln])
        else:
            recs.append([op, [data[i:i + ln]], i])
        i += ln
    for r in recs:
        r[1] = r[1][0] if len(r[1]) == 1 else b"".join(r[1])
    return recs


class FltFile:
    def __init__(self, path):
        self.path = os.path.abspath(path)
        self.dir = os.path.dirname(self.path)
        with open(path, "rb") as fh:
            data = fh.read()
        if len(data) < 4 or struct.unpack_from(">H", data, 0)[0] != HEADER:
            raise ValueError("not an OpenFlight file (no header record)")
        recs = read_records(data)
        self.stats = {"faces": 0, "meshes": 0, "groups": 0, "objects": 0, "lods": 0, "switches": 0,
                      "external_refs": 0, "instances": 0, "light_points": 0, "dofs": 0}
        self.textures = {}
        self.colors = None
        self.materials = {}
        self.inst_defs = {}
        hdr = recs[0][1]
        self.revision = struct.unpack_from(">i", hdr, 12)[0] if len(hdr) >= 16 else None
        unit_code = hdr[62] if len(hdr) > 62 else 0
        self.unit = UNIT_NAMES.get(unit_code, f"code {unit_code}")
        self.scale = UNITS.get(unit_code, 1.0)
        self.root = Node(HEADER, hdr)
        self.root.name = _cstr(hdr[4:12]) if len(hdr) >= 12 else ""
        self._build(recs)

    # -- parsing --------------------------------------------------------------
    def _build(self, recs):
        pos_list, uv_list, nrm_list, keys = [], [], [], []
        vpal_start = 0
        parent, last = self.root, self.root
        stack = []
        skip = 0
        for op, d, off in recs[1:]:
            if skip:
                if op in (PUSH_EXT, PUSH_ATTR):
                    skip += 1
                elif op in (POP_EXT, POP_ATTR):
                    skip -= 1
                continue
            if op in (PUSH_EXT, PUSH_ATTR):
                skip = 1
            elif op == VTX_PAL:
                vpal_start = off
            elif VTX_C <= op <= VTX_CT:
                x, y, z = struct.unpack_from(">3d", d, 8)
                pos_list.append((x, y, z))
                if op == VTX_CNT and len(d) >= 52:
                    uv_list.append(struct.unpack_from(">2f", d, 44))
                elif op == VTX_CT and len(d) >= 40:
                    uv_list.append(struct.unpack_from(">2f", d, 32))
                else:
                    uv_list.append((0.0, 0.0))
                if op in (VTX_CN, VTX_CNT) and len(d) >= 44:
                    nrm_list.append(struct.unpack_from(">3f", d, 32))
                else:
                    nrm_list.append((0.0, 0.0, 0.0))
                keys.append(off - vpal_start)
            elif op == TEX_PAL and len(d) >= 208:
                self.textures[struct.unpack_from(">i", d, 204)[0]] = _cstr(d[4:204])
            elif op == COLOR_PAL and len(d) >= 136:
                n = min(1024, (len(d) - 132) // 4)
                abgr = np.frombuffer(d, dtype=np.uint8, count=n * 4, offset=132).reshape(n, 4)
                self.colors = abgr[:, [3, 2, 1]].astype(np.float64) / 255.0
            elif op in (PUSH, PUSH_SUB):
                stack.append(parent)
                parent = last
            elif op in (POP, POP_SUB):
                last = parent
                parent = stack.pop() if stack else self.root
            elif op in NODE_OPS:
                node = Node(op, d)
                if len(d) >= 12:
                    node.name = _cstr(d[4:12])
                parent.children.append(node)
                last = node
                self._count(node)
            elif op == MATRIX and len(d) >= 68:
                m = np.array(struct.unpack_from(">16f", d, 4), dtype=np.float64).reshape(4, 4)
                last.matrix = m.T  # OpenFlight uses row vectors
                last.matrix[:3, 3] *= self.scale  # translations are in database units, like the vertices
            elif op == VTX_LIST:
                last.vlists.append(np.frombuffer(d, dtype=">i4", offset=4).astype(np.int64))
            elif op == MORPH_LIST:
                last.vlists.append(np.frombuffer(d, dtype=">i4", offset=4).astype(np.int64)[0::2])
            elif op == LVP:
                last.lvp = d
            elif op == MESH_PRIM:
                last.prims.append(d)
            elif op == LONG_ID:
                last.name = _cstr(d[4:])
            elif op == MATERIAL_PAL and len(d) >= 80:
                idx = struct.unpack_from(">i", d, 4)[0]
                self.materials[idx] = (_cstr(d[8:20]), struct.unpack_from(">3f", d, 36), struct.unpack_from(">f", d, 76)[0])
        self.positions = np.array(pos_list, dtype=np.float64).reshape(-1, 3) * self.scale
        self.uvs = np.array(uv_list, dtype=np.float64).reshape(-1, 2)
        self.normals = np.array(nrm_list, dtype=np.float64).reshape(-1, 3)
        self.has_normals = bool(len(self.normals)) and bool(np.any(self.normals))
        order = np.argsort(np.array(keys, dtype=np.int64))
        self.vkeys = np.array(keys, dtype=np.int64)[order]
        self.vvals = order

    def _count(self, node):
        s = self.stats
        op = node.op
        key = {FACE: "faces", MESH: "meshes", GROUP: "groups", OBJECT: "objects", LOD: "lods",
               SWITCH: "switches", EXT_REF: "external_refs", INST_REF: "instances",
               LIGHT_PT: "light_points", INDEXED_LP: "light_points", DOF: "dofs"}.get(op)
        if key:
            s[key] += 1
        if op == INST_DEF and len(node.data) >= 8:
            self.inst_defs[struct.unpack_from(">h", node.data, 6)[0]] = node

    def palette_indices(self, offsets):
        pos = np.searchsorted(self.vkeys, offsets)
        pos = np.clip(pos, 0, max(0, len(self.vkeys) - 1))
        ok = len(self.vkeys) > 0 and np.all(self.vkeys[pos] == offsets)
        return self.vvals[pos] if ok else None

    def color(self, index):
        if self.colors is None:
            return None
        pal, intensity = divmod(int(index), 128)
        if pal >= len(self.colors):
            return None
        return self.colors[pal] * (max(intensity, 1) / 127.0)


def face_attributes(d, shift, flt):
    """Visibility, texture pattern and colour of a face (shift=0) or mesh (shift=4) record."""
    def get(fmt, off, default=None):
        off += shift
        return struct.unpack_from(fmt, d, off)[0] if len(d) >= off + struct.calcsize(fmt) else default

    draw_type = get(">b", 18, 0)
    flags = get(">I", 44, 0)
    tex = get(">h", 28, -1)
    hidden = bool(flags & 0x04000000) or draw_type in (8, 9, 10)
    color = None
    if flags & 0x10000000:  # packed colour
        packed = get(">I", 56)
        if packed is not None:
            color = np.array([packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF], dtype=np.float64) / 255.0
    elif not flags & 0x40000000:  # has colour
        idx = get(">I", 68)
        if idx is None or idx == 0xFFFFFFFF:
            idx = get(">H", 20)
        if idx is not None and idx != 0xFFFF:
            color = flt.color(idx)
    mat = get(">h", 30, -1)  # material palette index: diffuse modulates the face colour (like OSG's loader)
    materials = getattr(flt, "materials", None)
    if materials and mat is not None and mat >= 0 and mat in materials:
        diffuse = np.clip(np.asarray(materials[mat][1], dtype=np.float64), 0.0, 1.0)
        color = diffuse if color is None else np.asarray(color, dtype=np.float64)[:3] * diffuse
    return hidden, tex, color


def lvp_arrays(d):
    n, mask = struct.unpack_from(">II", d, 4)
    fields = []
    if mask & 0x80000000:
        fields.append(("pos", ">f8", (3,)))
    if mask & 0x40000000:
        fields.append(("ci", ">u4"))
    if mask & 0x20000000:
        fields.append(("rgba", "u1", (4,)))
    if mask & 0x10000000:
        fields.append(("nrm", ">f4", (3,)))
    if mask & 0x08000000:
        fields.append(("uv", ">f4", (2,)))
    for k in range(7):
        if mask & (0x04000000 >> k):
            fields.append((f"uv{k + 1}", ">f4", (2,)))
    if not fields or not mask & 0x80000000:
        return np.zeros((0, 3)), None
    arr = np.frombuffer(d, dtype=np.dtype(fields), count=n, offset=12)
    return arr["pos"].astype(np.float64), (arr["uv"].astype(np.float64) if "uv" in arr.dtype.names else None)


def primitive_faces(d):
    ptype, isize, count = struct.unpack_from(">hHI", d, 4)
    dt = {1: "u1", 2: ">u2", 4: ">u4"}.get(isize)
    if dt is None or count == 0:
        return []
    idx = np.frombuffer(d, dtype=dt, count=count, offset=12).astype(np.int64)
    if ptype == 1:  # triangle strip
        faces = [(idx[i], idx[i + 1], idx[i + 2]) if i % 2 == 0 else (idx[i + 1], idx[i], idx[i + 2])
                 for i in range(count - 2)]
    elif ptype == 2:  # triangle fan
        faces = [(idx[0], idx[i], idx[i + 1]) for i in range(1, count - 1)]
    elif ptype == 3:  # quadrilateral strip
        faces = [(idx[i], idx[i + 1], idx[i + 3], idx[i + 2]) for i in range(0, count - 3, 2)]
    elif ptype == 4:  # indexed polygon
        faces = [tuple(idx)]
    else:
        return []
    return [f for f in faces if len(set(f)) >= 3]


