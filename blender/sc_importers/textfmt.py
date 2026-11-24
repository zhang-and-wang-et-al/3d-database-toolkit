"""Readers for simple formats: OFF, SMD, AC3D, VTK legacy (ASCII + binary), DXF (ASCII + binary), ASE, DirectX .x.

Each ``load_*(path, ctx)`` builds Blender objects and returns a details dict.
Geometry comes first; materials are kept simple (diffuse colours / textures).
"""

import os
import re
import struct

import numpy as np

from . import meshbuild as mb


def _read_text(path):
    with open(path, "rb") as fh:
        raw = fh.read()
    return raw.decode("utf-8", "replace") if raw[:3] != b"\xef\xbb\xbf" else raw[3:].decode("utf-8", "replace")


def _name(path):
    return os.path.splitext(os.path.basename(path))[0]


# ---------------------------------------------------------------------------
# OFF (Object File Format)
# ---------------------------------------------------------------------------

def load_off(path, ctx):
    words = []
    for line in _read_text(path).splitlines():
        line = line.split("#", 1)[0].strip()
        if line:
            words.append(line.split())
    if not words or "OFF" not in words[0][0].upper():
        raise ValueError("missing OFF header")
    head = words[0]
    if len(head) > 1 and head[1].upper() == "BINARY":
        raise NotImplementedError("binary OFF is not supported by the built-in reader")
    has_st, dim4 = head[0].upper().startswith("ST"), "4" in head[0]
    rest = head[1:]
    li = 1
    if not rest:
        rest = words[1]
        li = 2
    nv, nf = int(rest[0]), int(rest[1])
    ncoord = 4 if dim4 else 3
    verts = np.array([[float(x) for x in words[li + i][:ncoord]] for i in range(nv)], dtype=np.float64)
    if dim4:
        verts = verts[:, :3] / np.where(verts[:, 3:4] == 0, 1, verts[:, 3:4])
    faces, colors = [], []
    for w in words[li + nv:li + nv + nf]:
        k = int(w[0])
        faces.append([int(x) for x in w[1:1 + k]])
        extra = w[1 + k:]
        if len(extra) >= 3:
            c = np.array([float(x) for x in extra[:3]])
            colors.append(c / 255.0 if c.max() > 1.0 else c)  # 0-255 ints or 0-1 floats
    kw = {}
    if colors and len(colors) == len(faces):
        kw = dict(face_colors=np.array(colors), materials=[mb.color_attribute_material()])
    mb.build_mesh(_name(path), verts, faces, **kw)
    return {"vertices_declared": nv, "faces_declared": nf, "st": has_st}


# ---------------------------------------------------------------------------
# SMD (Valve studiomdl)
# ---------------------------------------------------------------------------

def load_smd(path, ctx):
    lines = _read_text(path).splitlines()
    try:
        start = next(i for i, l in enumerate(lines) if l.strip().lower() == "triangles")
    except StopIteration:
        raise ValueError("no 'triangles' section (animation-only SMD?)")
    pos, uvs, mats, names = [], [], [], {}
    i = start + 1
    while i + 3 < len(lines) and lines[i].strip().lower() != "end":
        mat = lines[i].strip()
        tri = []
        for j in range(1, 4):
            p = lines[i + j].split()
            tri.append(p)
        if all(len(p) >= 9 for p in tri):
            for p in tri:
                pos.append([float(p[1]), float(p[2]), float(p[3])])
                uvs.append([float(p[7]), float(p[8])])
            mats.append(names.setdefault(mat, len(names)))
        i += 4
    if not pos:
        raise ValueError("SMD contains no triangles")
    verts, inv = mb.weld(pos)
    faces = inv.reshape(-1, 3)
    base = os.path.dirname(path)
    materials = []
    for n in names:
        found = mb.resolve_file(n, base)
        if not os.path.splitext(n)[1]:
            for ext in (".tga", ".png", ".bmp", ".jpg"):
                cand = mb.resolve_file(n + ext, base)
                if os.path.isfile(cand):
                    found = cand
                    break
        materials.append(mb.image_material(n, found) if os.path.isfile(found) else mb.diffuse_material(n, (0.8, 0.8, 0.8)))
    mb.build_mesh(_name(path), verts, (faces.ravel(), np.full(len(faces), 3)), loop_uvs=np.array(uvs),
                  face_materials=np.array(mats), materials=materials)
    return {"materials": list(names)}


# ---------------------------------------------------------------------------
# AC3D (.ac) - used e.g. by FlightGear
# ---------------------------------------------------------------------------

def load_ac(path, ctx):
    lines = _read_text(path).splitlines()
    if not lines or not lines[0].startswith("AC3D"):
        raise ValueError("missing AC3D header")
    it = iter(range(1, len(lines)))
    materials = []
    acc = mb.MeshAccumulator()
    base = os.path.dirname(path)
    counts = {"objects": 0}

    def next_line():
        for idx in it:
            s = lines[idx].strip()
            if s:
                return s
        return None

    def parse_object(first, parent_M):
        counts["objects"] += 1
        rot = np.eye(3)
        loc = np.zeros(3)
        verts = None
        texture = None
        faces, fuvs, fmats = [], [], []
        while True:
            s = next_line()
            if s is None:
                break
            tok = s.split()
            key = tok[0]
            if key == "data":
                n = int(tok[1])
                got = 0
                while got < n:
                    t = next_line()
                    if t is None:
                        break
                    got += len(t) + 1
            elif key == "texture":
                texture = s.split(None, 1)[1].strip().strip('"')
            elif key == "rot":
                rot = np.array([float(x) for x in tok[1:10]]).reshape(3, 3)
            elif key == "loc":
                loc = np.array([float(x) for x in tok[1:4]])
            elif key == "numvert":
                n = int(tok[1])
                verts = np.array([[float(x) for x in next_line().split()[:3]] for _ in range(n)]).reshape(-1, 3)
            elif key == "numsurf":
                for _ in range(int(tok[1])):
                    flags = int(next_line().split()[1], 0)
                    mat = 0
                    t = next_line().split()
                    if t[0] == "mat":
                        mat = int(t[1])
                        t = next_line().split()
                    nref = int(t[1])
                    refs = [next_line().split() for _ in range(nref)]
                    if flags & 0xF == 0 and nref >= 3:
                        faces.append([int(r[0]) for r in refs])
                        fuvs.extend([[float(r[1]), float(r[2])] if len(r) >= 3 else [0.0, 0.0] for r in refs])
                        fmats.append(mat)
            elif key == "kids":
                M = np.eye(4)
                M[:3, :3] = rot.T
                M[:3, 3] = loc
                M = parent_M @ M
                if verts is not None and faces:
                    w = verts @ M[:3, :3].T + M[:3, 3]
                    if texture:
                        tpath = mb.resolve_file(texture, base)
                        mi = acc.material_index(("tex", tpath), lambda: mb.image_material(os.path.basename(tpath), tpath))
                        acc.add(mb.y_up_to_z_up(w), faces, loop_uvs=np.array(fuvs), face_material=mi)
                    else:
                        cols = [materials[m] if m < len(materials) else (0.8, 0.8, 0.8) for m in fmats]
                        mi = acc.material_index("color", mb.color_attribute_material)
                        acc.add(mb.y_up_to_z_up(w), faces, loop_uvs=np.array(fuvs), face_colors=np.array(cols), face_material=mi)
                for _ in range(int(tok[1])):
                    s2 = next_line()
                    if s2 and s2.startswith("OBJECT"):
                        parse_object(s2, M)
                return

    while True:
        s = next_line()
        if s is None:
            break
        if s.startswith("MATERIAL"):
            m = re.search(r"\brgb\s+([-\d.eE]+)\s+([-\d.eE]+)\s+([-\d.eE]+)", s)
            materials.append(tuple(float(x) for x in m.groups()) if m else (0.8, 0.8, 0.8))
        elif s.startswith("OBJECT"):
            parse_object(s, np.eye(4))
    acc.build(_name(path))
    return {"objects": counts["objects"], "materials": len(materials)}


# ---------------------------------------------------------------------------
# VTK legacy ASCII (POLYDATA / UNSTRUCTURED_GRID)
# ---------------------------------------------------------------------------

VTK_CELL_FACES = {
    10: [(0, 2, 1), (0, 1, 3), (1, 2, 3), (0, 3, 2)],                                     # tetra
    12: [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)],  # hexahedron
    13: [(0, 1, 2), (3, 5, 4), (0, 3, 4, 1), (1, 4, 5, 2), (2, 5, 3, 0)],                  # wedge
    14: [(0, 3, 2, 1), (0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4)],                        # pyramid
}


_VTK_TYPES = {"float": ">f4", "double": ">f8", "int": ">i4", "unsigned_int": ">u4", "short": ">i2",
              "unsigned_short": ">u2", "char": ">i1", "unsigned_char": ">u1", "long": ">i8",
              "unsigned_long": ">u8", "vtktypeint64": ">i8", "vtktypeint32": ">i4", "vtkidtype": ">i4"}


class _VtkCursor:
    """Reads a legacy VTK file: keyword lines are text, data blocks are text or big-endian binary."""

    def __init__(self, data, binary):
        self.data, self.pos, self.binary = data, 0, binary
        self.pending = []

    def line(self):
        while self.pos < len(self.data):
            end = self.data.find(b"\n", self.pos)
            end = len(self.data) if end < 0 else end
            text = self.data[self.pos:end].decode("latin-1").strip()
            self.pos = end + 1
            if text:
                return text
        return None

    def peek_line(self):
        pos = self.pos
        text = self.line()
        self.pos = pos
        return text

    def values(self, n, vtype):
        if self.binary:
            dt = np.dtype(_VTK_TYPES.get(vtype.lower(), ">f4"))
            arr = np.frombuffer(self.data, dtype=dt, count=n, offset=self.pos)
            self.pos += n * dt.itemsize
            return arr.astype(np.float64 if dt.kind == "f" else np.int64)
        out = list(self.pending)
        while len(out) < n:
            text = self.line()
            if text is None:
                break
            out.extend(text.split())
        self.pending = out[n:]
        return np.array(out[:n], dtype=np.float64 if vtype.lower() in ("float", "double") else np.int64)


def _vtk_cells(cur, n, size):
    """Cell lists in both layouts: classic 'n size' (count-prefixed) and VTK 5.1 OFFSETS/CONNECTIVITY."""
    nxt = cur.peek_line() or ""
    if nxt.upper().startswith("OFFSETS"):
        offsets = cur.values(n, cur.line().split()[1])
        conn = cur.values(size, cur.line().split()[1])
        return [conn[offsets[i]:offsets[i + 1]].tolist() for i in range(len(offsets) - 1)]
    vals = cur.values(size, "int")
    cells, j = [], 0
    while j < len(vals):
        k = int(vals[j])
        cells.append(vals[j + 1:j + 1 + k].tolist())
        j += 1 + k
    return cells


def load_vtk(path, ctx):
    with open(path, "rb") as fh:
        data = fh.read()
    cur = _VtkCursor(data, False)
    head = cur.line() or ""
    if "vtk" not in head.lower():
        raise ValueError("missing VTK header")
    cur.line()  # title
    mode = (cur.line() or "").upper()
    if mode not in ("ASCII", "BINARY"):
        raise ValueError(f"unknown VTK encoding '{mode}'")
    cur.binary = mode == "BINARY"
    pts, faces, cells, types = None, [], [], None
    while True:
        text = cur.line()
        if text is None:
            break
        tok = text.split()
        key = tok[0].upper()
        if key == "DATASET" and tok[1].upper() not in ("POLYDATA", "UNSTRUCTURED_GRID"):
            raise NotImplementedError(f"VTK dataset type {tok[1]} is not supported by the built-in reader")
        if key == "POINTS":
            n = int(tok[1])
            pts = cur.values(3 * n, tok[2]).reshape(-1, 3)
        elif key in ("POLYGONS", "TRIANGLE_STRIPS", "VERTICES", "LINES", "CELLS"):
            got = _vtk_cells(cur, int(tok[1]), int(tok[2]))
            if key == "POLYGONS":
                faces.extend(got)
            elif key == "TRIANGLE_STRIPS":
                for c in got:
                    faces.extend([(c[a], c[a + 1], c[a + 2]) if a % 2 == 0 else (c[a + 1], c[a], c[a + 2]) for a in range(len(c) - 2)])
            elif key == "CELLS":
                cells = got
        elif key == "CELL_TYPES":
            types = cur.values(int(tok[1]), "int").tolist()
        elif key in ("POINT_DATA", "CELL_DATA", "FIELD", "METADATA"):
            break
    if pts is None:
        raise ValueError("no POINTS")
    if cells and types:
        count = {}
        cell_faces = []
        for c, ct in zip(cells, types):
            if ct in (5, 7, 9):
                cell_faces.append(tuple(c))
            elif ct == 6:
                cell_faces.extend([(c[a], c[a + 1], c[a + 2]) for a in range(len(c) - 2)])
            elif ct in VTK_CELL_FACES:
                for f in VTK_CELL_FACES[ct]:
                    face = tuple(c[x] for x in f)
                    count[frozenset(face)] = count.get(frozenset(face), 0) + 1
                    cell_faces.append(face)
        # volume cells: keep only the outer (boundary) faces
        faces.extend(f for f in cell_faces if count.get(frozenset(f), 1) == 1)
    mb.build_mesh(_name(path), pts, faces)
    return {"points": len(pts), "cells": len(cells) or len(faces), "encoding": mode.lower()}


# ---------------------------------------------------------------------------
# DXF (ASCII): 3DFACE, polyface meshes and polygon meshes
# ---------------------------------------------------------------------------

def _dxf_entities(pairs, i, n, stop):
    """Read entities from index i until a ('0', stop) pair. Returns (entities, next index).

    An entity is (type, {code: [values]}, [vertex dicts]) - POLYLINEs carry their VERTEX list.
    """
    ents = []
    while i < n and pairs[i] != ("0", stop) and pairs[i] != ("0", "ENDSEC"):
        if pairs[i][0] != "0":
            i += 1
            continue
        etype = pairs[i][1]
        d = {}
        i += 1
        while i < n and pairs[i][0] != "0":
            d.setdefault(pairs[i][0], []).append(pairs[i][1])
            i += 1
        verts = []
        if etype == "POLYLINE":
            while i < n and pairs[i] == ("0", "VERTEX"):
                vd = {}
                i += 1
                while i < n and pairs[i][0] != "0":
                    vd.setdefault(pairs[i][0], []).append(pairs[i][1])
                    i += 1
                verts.append(vd)
            if i < n and pairs[i] == ("0", "SEQEND"):
                i += 1
                while i < n and pairs[i][0] != "0":
                    i += 1
        ents.append((etype, d, verts))
    return ents, i


def _dxf_float(d, code, default=0.0):
    try:
        return float(d[code][0])
    except (KeyError, IndexError, ValueError):
        return default


_DXF_BINARY_SENTINEL = b"AutoCAD Binary DXF\r\n\x1a\x00"


def _dxf_value_kind(code):
    """Value type of a group code in binary DXF: 's'tring, 'd'ouble, 'h' int16, 'i' int32, 'q' int64, 'b'ool, 'x' binary."""
    if 0 <= code <= 9 or code in (100, 101, 102, 105, 999) or 300 <= code <= 309 or 320 <= code <= 369 \
            or 390 <= code <= 399 or 410 <= code <= 419 or 430 <= code <= 439 or 470 <= code <= 481 \
            or 1000 <= code <= 1003 or 1005 <= code <= 1009:
        return "s"
    if 10 <= code <= 59 or 110 <= code <= 149 or 210 <= code <= 239 or 460 <= code <= 469 or 1010 <= code <= 1059:
        return "d"
    if 90 <= code <= 99 or 420 <= code <= 429 or 440 <= code <= 459 or code == 1071:
        return "i"
    if 160 <= code <= 169:
        return "q"
    if 290 <= code <= 299:
        return "b"
    if 310 <= code <= 319 or code == 1004:
        return "x"
    return "h"  # 60-79, 170-179, 270-289, 370-389, 400-409, 1060-1070


def _dxf_binary_pairs(data):
    """Decode a binary DXF into the same (code, value) string pairs the text reader uses."""
    pos = len(_DXF_BINARY_SENTINEL)
    two_byte = data[pos:pos + 2] == b"\x00\x00"  # R13+ writes 2-byte group codes, R12 1-byte (+255 escape)
    pairs = []
    n = len(data)
    while pos < n:
        if two_byte:
            if pos + 2 > n:
                break
            code = struct.unpack_from("<h", data, pos)[0]
            pos += 2
        else:
            code = data[pos]
            pos += 1
            if code == 255:
                code = struct.unpack_from("<h", data, pos)[0]
                pos += 2
        kind = _dxf_value_kind(code)
        if kind == "s":
            end = data.find(b"\x00", pos)
            end = n if end < 0 else end
            value = data[pos:end].decode("cp1252", "replace")
            pos = end + 1
        elif kind == "d":
            value = repr(struct.unpack_from("<d", data, pos)[0])
            pos += 8
        elif kind == "i":
            value = str(struct.unpack_from("<i", data, pos)[0])
            pos += 4
        elif kind == "q":
            value = str(struct.unpack_from("<q", data, pos)[0])
            pos += 8
        elif kind == "b":
            value = str(data[pos])
            pos += 1
        elif kind == "x":
            pos += 1 + data[pos]
            value = ""
        else:
            value = str(struct.unpack_from("<h", data, pos)[0])
            pos += 2
        pairs.append((str(code), value))
        if code == 0 and value == "EOF":
            break
    return pairs


def load_dxf(path, ctx):
    with open(path, "rb") as fh:
        raw = fh.read()
    if raw.startswith(_DXF_BINARY_SENTINEL):
        pairs = _dxf_binary_pairs(raw)
    else:
        lines = _read_text(path).splitlines()
        pairs = [(lines[k].strip(), lines[k + 1].strip()) for k in range(0, len(lines) - 1, 2)]
    n = len(pairs)
    blocks, entities = {}, []
    i = 0
    while i < n:
        if pairs[i] == ("0", "SECTION") and i + 1 < n:
            name = pairs[i + 1][1]
            i += 2
            if name == "ENTITIES":
                entities, i = _dxf_entities(pairs, i, n, "ENDSEC")
            elif name == "BLOCKS":
                while i < n and pairs[i] != ("0", "ENDSEC"):
                    if pairs[i] == ("0", "BLOCK"):
                        d = {}
                        i += 1
                        while i < n and pairs[i][0] != "0":
                            d.setdefault(pairs[i][0], []).append(pairs[i][1])
                            i += 1
                        ents, i = _dxf_entities(pairs, i, n, "ENDBLK")
                        base = np.array([_dxf_float(d, c) for c in ("10", "20", "30")])
                        blocks[d.get("2", [""])[0]] = (base, ents)
                    i += 1
        else:
            i += 1
    verts, faces = [], []
    counts = {"3DFACE": 0, "POLYFACE": 0, "POLYMESH": 0, "INSERT": 0}

    def emit(ents, M, depth):
        for etype, d, pverts in ents:
            if etype == "3DFACE":
                pts = [(_dxf_float(d, f"1{k}"), _dxf_float(d, f"2{k}"), _dxf_float(d, f"3{k}")) for k in range(4)]
                if pts[3] == pts[2]:
                    pts = pts[:3]
                b = len(verts)
                verts.extend(np.array(pts) @ M[:3, :3].T + M[:3, 3])
                faces.append(list(range(b, b + len(pts))))
                counts["3DFACE"] += 1
            elif etype == "POLYLINE":
                flags = int(_dxf_float(d, "70"))
                m, nn = int(_dxf_float(d, "71")), int(_dxf_float(d, "72"))
                pv, pf = [], []
                for vd in pverts:
                    vf = int(_dxf_float(vd, "70"))
                    if flags & 64 and vf & 128 and not vf & 64:
                        idx = [abs(int(_dxf_float(vd, c))) for c in ("71", "72", "73", "74") if int(_dxf_float(vd, c)) != 0]
                        pf.append([x - 1 for x in idx])
                    else:
                        pv.append((_dxf_float(vd, "10"), _dxf_float(vd, "20"), _dxf_float(vd, "30")))
                b = len(verts)
                if flags & 64 and pv:
                    verts.extend(np.array(pv) @ M[:3, :3].T + M[:3, 3])
                    faces.extend([[b + x for x in f] for f in pf if len(f) >= 3 and max(f) < len(pv)])
                    counts["POLYFACE"] += 1
                elif flags & 16 and m * nn == len(pv) and pv:
                    verts.extend(np.array(pv) @ M[:3, :3].T + M[:3, 3])
                    for r in range(m - 1):
                        for c in range(nn - 1):
                            faces.append([b + r * nn + c, b + r * nn + c + 1, b + (r + 1) * nn + c + 1, b + (r + 1) * nn + c])
                    counts["POLYMESH"] += 1
            elif etype == "INSERT" and depth < 16:
                blk = blocks.get(d.get("2", [""])[0])
                if blk is None:
                    continue
                base, sub = blk
                ins = np.array([_dxf_float(d, c) for c in ("10", "20", "30")])
                sx, sy, sz = (_dxf_float(d, c, 1.0) for c in ("41", "42", "43"))
                a = np.radians(_dxf_float(d, "50"))
                T = np.eye(4)
                T[:3, :3] = np.array([[np.cos(a), -np.sin(a), 0], [np.sin(a), np.cos(a), 0], [0, 0, 1]]) @ np.diag([sx, sy, sz])
                T[:3, 3] = ins - T[:3, :3] @ base
                counts["INSERT"] += 1
                emit(sub, M @ T, depth + 1)

    emit(entities, np.eye(4), 0)
    if not faces:
        raise ValueError("no 3D faces in DXF (2D drawing or unsupported entities)")
    v, inv = mb.weld(verts)
    faces = [[int(inv[x]) for x in f] for f in faces]
    mb.build_mesh(_name(path), v, faces)
    return {"entities": counts, "blocks": len(blocks)}


# ---------------------------------------------------------------------------
# ASE (3ds Max ASCII Scene Export)
# ---------------------------------------------------------------------------

def load_ase(path, ctx):
    text = _read_text(path)
    if "*3DSMAX_ASCIIEXPORT" not in text[:200]:
        raise ValueError("missing *3DSMAX_ASCIIEXPORT header")
    # top-level material diffuse colours (and sub-materials)
    mats = {}
    for m in re.finditer(r"\*MATERIAL\s+(\d+)\s*\{", text):
        seg = text[m.end():m.end() + 4000]
        d = re.search(r"\*MATERIAL_DIFFUSE\s+([-\d.eE]+)\s+([-\d.eE]+)\s+([-\d.eE]+)", seg)
        if d:
            mats[int(m.group(1))] = tuple(float(x) for x in d.groups())
    acc = mb.MeshAccumulator()
    mi = acc.material_index("color", mb.color_attribute_material)
    objects = 0
    for g in re.finditer(r"\*GEOMOBJECT\s*\{", text):
        end = text.find("*GEOMOBJECT", g.end())
        seg = text[g.end(): end if end > 0 else len(text)]
        vs = re.findall(r"\*MESH_VERTEX\s+\d+\s+([-\d.eE]+)\s+([-\d.eE]+)\s+([-\d.eE]+)", seg)
        fs = re.findall(r"\*MESH_FACE\s+\d+:\s+A:\s*(\d+)\s+B:\s*(\d+)\s+C:\s*(\d+)", seg)
        if not vs or not fs:
            continue
        objects += 1
        ref = re.search(r"\*MATERIAL_REF\s+(\d+)", seg)
        color = mats.get(int(ref.group(1)), (0.8, 0.8, 0.8)) if ref else (0.8, 0.8, 0.8)
        faces = np.array(fs, dtype=np.int64)
        acc.add(np.array(vs, dtype=np.float64), (faces.ravel(), np.full(len(faces), 3)),
                face_colors=np.tile(color, (len(faces), 1)), face_material=mi)
    if not objects:
        raise ValueError("no mesh objects found")
    acc.build(_name(path))
    return {"objects": objects, "materials": len(mats)}


# ---------------------------------------------------------------------------
# DirectX .x (text)
# ---------------------------------------------------------------------------

_X_TOKEN = re.compile(r'"[^"]*"|[{}]|[A-Za-z_][\w\-.]*|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?')


def load_x(path, ctx):
    with open(path, "rb") as fh:
        head = fh.read(16)
    if not head.startswith(b"xof "):
        raise ValueError("missing 'xof' header")
    if head[8:12] != b"txt ":
        raise NotImplementedError(f"DirectX '{head[8:12].decode('latin-1').strip()}' (binary/compressed) is not supported by the built-in reader")
    text = re.sub(r"(//|#)[^\n]*", "", _read_text(path)[16:])
    toks = _X_TOKEN.findall(text)
    pos = 0
    acc = mb.MeshAccumulator()
    base = os.path.dirname(path)
    named_mats = {}
    count = {"meshes": 0, "frames": 0}

    def block():
        """Parse a block starting after '{'. Returns (numbers, strings, children[(type, name, block)])."""
        nonlocal pos
        nums, strs, kids = [], [], []
        while pos < len(toks):
            t = toks[pos]
            pos += 1
            if t == "}":
                break
            if t == "{":
                kids.append(("", "", block()))
            elif t[0] == '"':
                strs.append(t[1:-1])
            elif t[0].isalpha() or t[0] == "_":
                # identifier: child object "Type [name] {", or a reference "{ Name }"
                if pos < len(toks) and toks[pos] == "{":
                    pos += 1
                    kids.append((t, "", block()))
                elif pos + 1 < len(toks) and toks[pos + 1] == "{":
                    name = toks[pos]
                    pos += 2
                    kids.append((t, name, block()))
                else:
                    kids.append(("REF", t, None))
            else:
                nums.append(float(t))
        return nums, strs, kids

    def material_color(blk):
        nums, strs, kids = blk
        tex = None
        for kt, _, kb in kids:
            if kt == "TextureFilename" and kb and kb[1]:
                tex = kb[1][0]
        return (tuple(nums[:3]) if len(nums) >= 3 else (0.8, 0.8, 0.8)), tex

    def add_mesh(blk, M):
        nums, _, kids = blk
        nv = int(nums[0])
        verts = np.array(nums[1:1 + 3 * nv]).reshape(-1, 3)
        k = 1 + 3 * nv
        nf = int(nums[k])
        k += 1
        faces = []
        for _ in range(nf):
            c = int(nums[k])
            faces.append([int(x) for x in nums[k + 1:k + 1 + c]])
            k += 1 + c
        uvs = None
        face_mat = np.zeros(len(faces), dtype=np.int64)
        mats = [((0.8, 0.8, 0.8), None)]
        for kt, _, kb in kids:
            if kt == "MeshTextureCoords" and kb:
                n = int(kb[0][0])
                uv = np.array(kb[0][1:1 + 2 * n]).reshape(-1, 2)
                uv[:, 1] = 1.0 - uv[:, 1]
                uvs = uv
            elif kt == "MeshMaterialList" and kb:
                mn = kb[0]
                nmat, nidx = int(mn[0]), int(mn[1])
                face_mat = np.array(mn[2:2 + nidx], dtype=np.int64)
                if len(face_mat) < len(faces):
                    face_mat = np.resize(face_mat, len(faces))
                mats = []
                for mt, mname, mb_ in kb[2]:
                    if mt == "Material" and mb_:
                        mats.append(material_color(mb_))
                    elif mt in ("REF", "") and (mname in named_mats or (mb_ and mb_[2] and mb_[2][0][1] in named_mats)):
                        mats.append(named_mats.get(mname) or named_mats.get(mb_[2][0][1]))
                mats = mats or [((0.8, 0.8, 0.8), None)] * max(1, nmat)
        w = verts @ M[:3, :3].T + M[:3, 3]
        w = mb.lh_y_up_to_z_up(w)
        face_mat = np.clip(face_mat, 0, len(mats) - 1)
        loop_uv = uvs[np.fromiter((i for f in faces for i in f), dtype=np.int64)] if uvs is not None and len(uvs) == nv else None
        groups = {}
        for fi, m in enumerate(face_mat):
            groups.setdefault(int(m), []).append(fi)
        offsets = np.cumsum([0] + [len(f) for f in faces])
        for m, fids in groups.items():
            color, tex = mats[m]
            sub_faces = [faces[f] for f in fids]
            sub_uv = np.concatenate([loop_uv[offsets[f]:offsets[f + 1]] for f in fids]) if loop_uv is not None else None
            if tex:
                tpath = mb.resolve_file(tex, base)
                mi = acc.material_index(("tex", tpath), lambda: mb.image_material(os.path.basename(tpath), tpath))
                acc.add(w, sub_faces, loop_uvs=sub_uv, face_material=mi)
            else:
                mi = acc.material_index("color", mb.color_attribute_material)
                acc.add(w, sub_faces, loop_uvs=sub_uv, face_colors=np.tile(color, (len(sub_faces), 1)), face_material=mi)
        count["meshes"] += 1

    def visit(kind, name, blk, M):
        if blk is None:
            return
        if kind == "Frame":
            count["frames"] += 1
            local = np.eye(4)
            for kt, _, kb in blk[2]:
                if kt == "FrameTransformMatrix" and kb and len(kb[0]) >= 16:
                    local = np.array(kb[0][:16]).reshape(4, 4).T  # row vectors -> column vectors
            M = M @ local
            for kt, kn, kb in blk[2]:
                if kt != "FrameTransformMatrix":
                    visit(kt, kn, kb, M)
        elif kind == "Mesh":
            add_mesh(blk, M)

    while pos < len(toks):
        t = toks[pos]
        pos += 1
        if t == "{":
            block()
            continue
        name = ""
        if pos < len(toks) and toks[pos] != "{":
            name = toks[pos]
            pos += 1
        if pos < len(toks) and toks[pos] == "{":
            pos += 1
            blk = block()
            if t == "template":
                continue
            if t == "Material":
                named_mats[name] = material_color(blk)
            else:
                visit(t, name, blk, np.eye(4))
    if not count["meshes"]:
        raise ValueError("no Mesh objects found")
    acc.build(_name(path))
    return count
