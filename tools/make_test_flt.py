#!/usr/bin/env python3
"""Write a large OpenFlight test database the way modelling tools (Creator & co.) write them.

    python tools/make_test_flt.py OUT_DIR [--tiles 4] [--objects 60] [--grid 12]

Creates ``OUT_DIR/master.flt`` plus one external-reference file per tile
(``tile_<x>_<y>.flt``) and a texture. The master holds its own geometry
(buildings under LODs, an instanced tree) and references the tiles. Typical
properties of real databases that the optimiser should find:

* every face has its own vertices in the vertex palette (nothing shared -> ``weld``)
* many small objects with the same texture/material (-> ``merge_geometries``)
* identical objects stored several times (-> ``share_duplicates``)
* quads / polygons (-> ``triangulate``), a few degenerate faces (-> ``clean``)
* groups with a single child, empty groups (-> ``remove_redundant`` / ``remove_empty``)
"""

import argparse
import math
import os
import struct

PUSH, POP = 10, 11


def rec(op, body):
    return struct.pack(">HH", op, 4 + len(body)) + body


def pad(b, n):
    return b[:n] + b"\0" * (n - len(b[:n]))


class FltWriter:
    def __init__(self):
        self.verts = []          # (x, y, z, u, v)
        self.body = bytearray()

    # -- palettes -------------------------------------------------------------
    def vertex(self, x, y, z, u=0.0, v=0.0):
        self.verts.append((x, y, z, u, v))
        return len(self.verts) - 1

    @staticmethod
    def header(name):
        b = pad(name.encode(), 8) + struct.pack(">ii", 1640, 0) + pad(b"2026", 32) + struct.pack(">hhhhh", 0, 0, 0, 0, 1)
        return rec(1, pad(b + bytes([0]), 320))  # unit byte (record offset 62) = 0 -> metres

    def palettes(self, textures, materials):
        out = bytearray()
        cols = [0] * 1024
        cols[1], cols[2], cols[3] = 0xFF4060C0, 0xFF30A030, 0xFF909090   # a,b,g,r
        out += rec(32, b"\0" * 128 + struct.pack(">1024I", *cols))
        for i, t in enumerate(textures):
            out += rec(64, pad(t.encode(), 200) + struct.pack(">iii", i, 0, 0))
        for i, (name, rgb) in enumerate(materials):
            body = struct.pack(">i", i) + pad(name.encode(), 12) + struct.pack(">i", 0)
            body += struct.pack(">3f", 0.2, 0.2, 0.2) + struct.pack(">3f", *rgb) + struct.pack(">3f", 0, 0, 0)
            body += struct.pack(">3f", 0, 0, 0) + struct.pack(">ff", 10.0, 1.0) + struct.pack(">i", 0)
            out += rec(113, body)
        return out

    def vertex_palette(self):
        recs = b"".join(rec(71, struct.pack(">Hh3d2fII", 0, 0, x, y, z, u, v, 0, 0))
                        for x, y, z, u, v in self.verts)   # vertex with colour + uv: 48 bytes
        return rec(67, struct.pack(">i", 8 + len(recs))) + recs

    # -- hierarchy --------------------------------------------------------------
    def node(self, op, name, extra_len):
        self.body += rec(op, pad(name.encode(), 8) + b"\0" * extra_len)

    def group(self, name):
        self.node(2, name, 28)

    def obj(self, name):
        self.node(4, name, 24)

    def lod(self, name, switch_in, switch_out, center=(0, 0, 0)):
        # 80-byte record: id, reserved, switch in / out (16, 24), effect ids, flags (36), centre (40), transition
        # range and significant size (64, 72)
        self.body += rec(73, pad(name.encode(), 8) + struct.pack(">idd", 0, switch_in, switch_out)
                         + struct.pack(">hhi3d", 0, 0, 0, *center) + struct.pack(">dd", 0, 0))

    def push(self):
        self.body += rec(PUSH, b"")

    def pop(self):
        self.body += rec(POP, b"")

    def matrix_translate(self, x, y, z):
        self.body += rec(49, struct.pack(">16f", 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1))

    def xref(self, path):
        self.body += rec(63, pad(path.encode(), 200) + b"\0" * 16)

    def inst_def(self, num):
        self.body += rec(62, struct.pack(">hh", 0, num))

    def inst_ref(self, num):
        self.body += rec(61, struct.pack(">hh", 0, num))

    def face(self, vidx, tex=-1, mat=-1, color_index=3 * 128 + 127):
        b = bytearray(76)
        b[0:8] = b"face\0\0\0\0"
        struct.pack_into(">h", b, 24, tex)          # texture pattern (record offset 28)
        struct.pack_into(">h", b, 26, mat)          # material index (record offset 30)
        struct.pack_into(">I", b, 64, color_index)  # primary colour index (record offset 68)
        self.body += rec(5, bytes(b))
        self.body += rec(72, b"".join(struct.pack(">i", 8 + 48 * i) for i in vidx))

    # -- geometry helpers: each face gets its OWN vertices (like most exporters) --------
    def quad(self, p0, p1, p2, p3, **kw):
        ids = [self.vertex(*p0, 0, 0), self.vertex(*p1, 1, 0), self.vertex(*p2, 1, 1), self.vertex(*p3, 0, 1)]
        self.face(ids, **kw)

    def box(self, x, y, w, d, h, **kw):
        x1, y1 = x + w, y + d
        self.quad((x, y, 0), (x, y1, 0), (x1, y1, 0), (x1, y, 0), **kw)
        self.quad((x, y, h), (x1, y, h), (x1, y1, h), (x, y1, h), **kw)
        self.quad((x, y, 0), (x1, y, 0), (x1, y, h), (x, y, h), **kw)
        self.quad((x1, y, 0), (x1, y1, 0), (x1, y1, h), (x1, y, h), **kw)
        self.quad((x1, y1, 0), (x, y1, 0), (x, y1, h), (x1, y1, h), **kw)
        self.quad((x, y1, 0), (x, y, 0), (x, y, h), (x, y1, h), **kw)

    def terrain(self, ox, oy, size, grid, **kw):
        s = size / grid
        z = lambda x, y: 3 * math.sin(x * 0.05) * math.cos(y * 0.04)  # noqa: E731
        for i in range(grid):
            for j in range(grid):
                x0, y0 = ox + i * s, oy + j * s
                self.quad((x0, y0, z(x0, y0)), (x0 + s, y0, z(x0 + s, y0)), (x0 + s, y0 + s, z(x0 + s, y0 + s)),
                          (x0, y0 + s, z(x0, y0 + s)), **kw)

    def save(self, path, name, textures, materials):
        # like real files: header + palettes, then one push level holding the whole hierarchy
        data = (self.header(name) + self.palettes(textures, materials) + self.vertex_palette()
                + rec(PUSH, b"") + bytes(self.body) + rec(POP, b""))
        with open(path, "wb") as fh:
            fh.write(data)
        return len(data)


TEXTURES = ["textures/facade.rgb", "textures/ground.rgb"]
MATERIALS = [("concrete", (0.75, 0.73, 0.7)), ("grass", (0.35, 0.6, 0.3))]


def write_tile(path, tx, ty, grid):
    w = FltWriter()
    w.group("tile")
    w.push()
    w.group("ground")
    w.push()
    w.obj("terrain")
    w.push()
    w.terrain(tx * 200.0, ty * 200.0, 200.0, grid, tex=1, mat=1)
    w.pop()
    w.pop()
    w.group("props")              # many tiny objects with the same state -> merge candidates
    w.push()
    for k in range(40):
        w.obj(f"post{k}")
        w.push()
        w.box(tx * 200.0 + 5 * k, ty * 200.0 + 3, 0.3, 0.3, 2.5, tex=0, mat=0)
        w.pop()
    w.pop()
    w.pop()
    return w.save(path, "tile", TEXTURES, MATERIALS)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--tiles", type=int, default=4, help="tiles per side (external references)")
    ap.add_argument("--objects", type=int, default=60, help="buildings in the master file")
    ap.add_argument("--grid", type=int, default=40, help="terrain quads per tile side")
    a = ap.parse_args()
    os.makedirs(os.path.join(a.out, "textures"), exist_ok=True)
    for t in TEXTURES:  # 4x4 SGI .rgb so textures resolve
        p = os.path.join(a.out, t)
        hdr = struct.pack(">hbbHHHHii", 474, 0, 1, 2, 4, 4, 3, 0, 255)
        with open(p, "wb") as fh:
            fh.write(pad(hdr, 512) + bytes(range(48)))

    w = FltWriter()
    w.group("db")
    w.push()
    w.group("city")
    w.push()
    for k in range(a.objects):
        x, y = (k % 10) * 30.0, (k // 10) * 30.0
        w.group(f"blk{k}")                 # single-child group -> remove_redundant
        w.push()
        w.lod(f"lod{k}", 500.0, 0.0, (x, y, 0))
        w.push()
        w.obj(f"bld{k}")
        w.push()
        # every building is the same shape at a different place -> duplicates once re-centred;
        # every fifth building is an exact copy at the same spot as the previous one
        bx = x if k % 5 else x - 30.0 * (k % 10 != 0)
        w.box(bx, y, 12, 10, 8 + (k % 3) * 4, tex=0, mat=0)
        w.pop()
        w.pop()
        w.lod(f"far{k}", 3000.0, 500.0, (x, y, 0))
        w.push()
        w.obj(f"bld{k}_far")
        w.push()
        w.quad((x, y, 0), (x + 12, y, 0), (x + 12, y, 8), (x, y, 8), tex=0, mat=0)
        w.pop()
        w.pop()
        w.pop()
    w.group("empty")                       # empty group -> remove_empty
    w.pop()
    # degenerate faces (zero area) -> clean
    w.obj("junk")
    w.push()
    w.quad((0, 0, 0), (0, 0, 0), (1, 0, 0), (1, 0, 0), tex=0, mat=0)
    w.pop()
    # an instanced tree (instance definition + 5 references)
    w.inst_def(1)
    w.push()
    w.obj("tree")
    w.push()
    w.box(0, 0, 1, 1, 6, mat=1)
    w.pop()
    w.pop()
    for k in range(5):
        w.group(f"tree{k}")
        w.matrix_translate(320 + 10 * k, 20, 0)
        w.push()
        w.inst_ref(1)
        w.pop()
    w.pop()
    # tiles as external references (the typical "many sub-files" database)
    w.group("tiles")
    w.push()
    total = 0
    for tx in range(a.tiles):
        for ty in range(a.tiles):
            name = f"tile_{tx}_{ty}.flt"
            total += write_tile(os.path.join(a.out, name), tx, ty, a.grid)
            w.xref(name)
    w.pop()
    w.pop()
    size = w.save(os.path.join(a.out, "master.flt"), "master", TEXTURES, MATERIALS)
    print(f"master.flt: {size / 1e6:.1f} MB, {a.tiles * a.tiles} tile files: {total / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
