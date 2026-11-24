"""Readers for binary formats: 3DS (fallback when the add-on is missing), LightWave LWO, Quake MD2."""

import os
import struct

import numpy as np

from . import meshbuild as mb


def _name(path):
    return os.path.splitext(os.path.basename(path))[0]


def _cstring(d, off):
    end = d.index(b"\x00", off)
    return d[off:end].decode("latin-1", "replace"), end + 1


# ---------------------------------------------------------------------------
# 3D Studio (.3ds)
# ---------------------------------------------------------------------------

def load_3ds(path, ctx):
    with open(path, "rb") as fh:
        d = fh.read()
    if len(d) < 6 or struct.unpack_from("<H", d, 0)[0] != 0x4D4D:
        raise ValueError("not a 3DS file (missing 0x4D4D main chunk)")
    materials = {}  # name -> (rgb, texture)
    objects = []    # (name, verts, faces, uvs, [(matname, face indices)])

    def chunks(start, end):
        i = start
        while i + 6 <= end:
            cid, ln = struct.unpack_from("<HI", d, i)
            if ln < 6 or i + ln > end:
                break
            yield cid, i + 6, i + ln
            i += ln

    def color(start, end):
        for cid, s, e in chunks(start, end):
            if cid in (0x0011, 0x0012) and e - s >= 3:
                return tuple(b / 255.0 for b in d[s:s + 3])
            if cid in (0x0010, 0x0013) and e - s >= 12:
                return struct.unpack_from("<3f", d, s)
        return None

    def material(start, end):
        name, rgb, tex = "", (0.8, 0.8, 0.8), None
        for cid, s, e in chunks(start, end):
            if cid == 0xA000:
                name, _ = _cstring(d, s)
            elif cid == 0xA020:
                rgb = color(s, e) or rgb
            elif cid == 0xA200:
                for c2, s2, _e2 in chunks(s, e):
                    if c2 == 0xA300:
                        tex, _ = _cstring(d, s2)
        materials[name] = (rgb, tex)

    def trimesh(name, start, end):
        verts = faces = uvs = None
        fmats = []
        for cid, s, e in chunks(start, end):
            if cid == 0x4110:
                n = struct.unpack_from("<H", d, s)[0]
                verts = np.frombuffer(d, dtype="<f4", count=n * 3, offset=s + 2).reshape(-1, 3).astype(np.float64)
            elif cid == 0x4140:
                n = struct.unpack_from("<H", d, s)[0]
                uvs = np.frombuffer(d, dtype="<f4", count=n * 2, offset=s + 2).reshape(-1, 2).astype(np.float64)
            elif cid == 0x4120:
                n = struct.unpack_from("<H", d, s)[0]
                faces = np.frombuffer(d, dtype="<u2", count=n * 4, offset=s + 2).reshape(-1, 4)[:, :3].astype(np.int64)
                for c2, s2, e2 in chunks(s + 2 + n * 8, e):
                    if c2 == 0x4130:
                        mname, p = _cstring(d, s2)
                        k = struct.unpack_from("<H", d, p)[0]
                        fmats.append((mname, np.frombuffer(d, dtype="<u2", count=k, offset=p + 2).astype(np.int64)))
        if verts is not None and faces is not None and len(faces):
            objects.append((name, verts, faces, uvs, fmats))

    def walk(start, end):
        for cid, s, e in chunks(start, end):
            if cid in (0x4D4D, 0x3D3D):
                walk(s, e)
            elif cid == 0xAFFF:
                material(s, e)
            elif cid == 0x4000:
                name, p = _cstring(d, s)
                for c2, s2, e2 in chunks(p, e):
                    if c2 == 0x4100:
                        trimesh(name, s2, e2)

    walk(0, len(d))
    if not objects:
        raise ValueError("no triangle meshes found")
    base = os.path.dirname(path)
    acc = mb.MeshAccumulator()
    for name, verts, faces, uvs, fmats in objects:
        fm = np.full(len(faces), -1, dtype=np.int64)
        names = []
        for k, (mname, idx) in enumerate(fmats):
            fm[idx[idx < len(faces)]] = k
            names.append(mname)
        for k in np.unique(fm):
            sel = faces[fm == k]
            mname = names[k] if k >= 0 else None
            rgb, tex = materials.get(mname, ((0.8, 0.8, 0.8), None))
            loop_uv = uvs[sel.ravel()] if uvs is not None and len(uvs) == len(verts) else None
            if tex:
                tpath = mb.resolve_file(tex, base)
                mi = acc.material_index(("tex", tpath), lambda: mb.image_material(os.path.basename(tpath), tpath))
                acc.add(verts, (sel.ravel(), np.full(len(sel), 3)), loop_uvs=loop_uv, face_material=mi)
            else:
                mi = acc.material_index("color", mb.color_attribute_material)
                acc.add(verts, (sel.ravel(), np.full(len(sel), 3)), loop_uvs=loop_uv,
                        face_colors=np.tile(rgb, (len(sel), 1)), face_material=mi)
    acc.build(_name(path))
    return {"objects": len(objects), "materials": len(materials)}


# ---------------------------------------------------------------------------
# LightWave object (.lwo: LWO2 and old LWOB)
# ---------------------------------------------------------------------------

def load_lwo(path, ctx):
    with open(path, "rb") as fh:
        d = fh.read()
    if d[:4] != b"FORM" or d[8:12] not in (b"LWO2", b"LWOB", b"LWLO", b"LWO3"):
        raise ValueError("not a LightWave object")
    kind = d[8:12]
    points = []
    npts = 0
    layer_base = 0
    polys = []        # (indices, surface index or None)
    poly_block = []   # polys of the last POLS chunk (for PTAG)
    tags = []
    surf_colors = {}
    i = 12
    end = min(len(d), 8 + struct.unpack_from(">I", d, 4)[0])

    def vx(off):
        if d[off] == 0xFF:
            return struct.unpack_from(">I", d, off)[0] & 0x00FFFFFF, off + 4
        return struct.unpack_from(">H", d, off)[0], off + 2

    while i + 8 <= end:
        cid = d[i:i + 4]
        size = struct.unpack_from(">I", d, i + 4)[0]
        s, e = i + 8, i + 8 + size
        if cid == b"LAYR":
            layer_base = npts
        elif cid == b"PNTS":
            p = np.frombuffer(d, dtype=">f4", count=size // 4, offset=s).reshape(-1, 3).astype(np.float64)
            points.append(p)
            npts += len(p)
        elif cid == b"POLS" and kind != b"LWOB":
            ptype = d[s:s + 4]
            j = s + 4
            poly_block = []
            while j < e:
                nv = struct.unpack_from(">H", d, j)[0] & 0x03FF
                j += 2
                idx = []
                for _ in range(nv):
                    v, j = vx(j)
                    idx.append(layer_base + v)
                if ptype in (b"FACE", b"PTCH", b"SUBD") and nv >= 3:
                    poly_block.append(len(polys))
                    polys.append([idx, None])
                else:
                    poly_block.append(None)
        elif cid == b"POLS":  # LWOB
            j = s
            while j + 2 <= e:
                nv = struct.unpack_from(">H", d, j)[0]
                j += 2
                idx = list(struct.unpack_from(f">{nv}H", d, j))
                j += 2 * nv
                surf = struct.unpack_from(">h", d, j)[0]
                j += 2
                if surf < 0:
                    j += 2  # detail polygon count follows; they are listed as normal polygons
                    surf = -surf
                if nv >= 3:
                    polys.append([idx, surf - 1])
        elif cid in (b"TAGS", b"SRFS"):
            tags = [t.decode("latin-1") for t in d[s:e].split(b"\x00") if t]
        elif cid == b"PTAG" and d[s:s + 4] == b"SURF":
            j = s + 4
            while j < e:
                pi, j = vx(j)
                tag = struct.unpack_from(">H", d, j)[0]
                j += 2
                if pi < len(poly_block) and poly_block[pi] is not None:
                    polys[poly_block[pi]][1] = tag
        elif cid == b"SURF":
            name, p = _cstring(d, s)
            if p % 2:
                p += 1
            if kind == b"LWO2":
                _, p = _cstring(d, p)  # source
                if p % 2:
                    p += 1
            while p + 6 <= e:
                sid = d[p:p + 4]
                ssz = struct.unpack_from(">H", d, p + 4)[0]
                if sid == b"COLR":
                    if kind == b"LWOB":
                        surf_colors[name] = tuple(b / 255.0 for b in d[p + 6:p + 9])
                    else:
                        surf_colors[name] = struct.unpack_from(">3f", d, p + 6)
                p += 6 + ssz + (ssz % 2)
        i = e + (size % 2)
    if not points or not polys:
        raise ValueError("no polygons found")
    verts = mb.lh_y_up_to_z_up(np.concatenate(points))
    faces = [p[0] for p in polys]
    cols = []
    for _, surf in polys:
        name = tags[surf] if surf is not None and 0 <= surf < len(tags) else None
        cols.append(surf_colors.get(name, (0.8, 0.8, 0.8)))
    mb.build_mesh(_name(path), verts, faces, face_colors=np.array(cols), materials=[mb.color_attribute_material()])
    return {"format": kind.decode(), "surfaces": len(surf_colors)}


# ---------------------------------------------------------------------------
# Quake II model (.md2) - first frame
# ---------------------------------------------------------------------------

def load_md2(path, ctx):
    with open(path, "rb") as fh:
        d = fh.read()
    if d[:4] != b"IDP2":
        raise ValueError("not an MD2 file")
    (version, skin_w, skin_h, frame_size, n_skins, n_xyz, n_st, n_tris, _n_gl, n_frames,
     ofs_skins, ofs_st, ofs_tris, ofs_frames, _ofs_gl, _ofs_end) = struct.unpack_from("<16i", d, 4)
    if n_frames < 1 or n_xyz < 1:
        raise ValueError("MD2 without frames")
    scale = np.array(struct.unpack_from("<3f", d, ofs_frames))
    trans = np.array(struct.unpack_from("<3f", d, ofs_frames + 12))
    packed = np.frombuffer(d, dtype=np.uint8, count=n_xyz * 4, offset=ofs_frames + 40).reshape(-1, 4)
    verts = packed[:, :3].astype(np.float64) * scale + trans
    tris = np.frombuffer(d, dtype="<i2", count=n_tris * 6, offset=ofs_tris).reshape(-1, 6).astype(np.int64)
    st = np.frombuffer(d, dtype="<i2", count=n_st * 2, offset=ofs_st).reshape(-1, 2).astype(np.float64)
    faces = tris[:, [2, 1, 0]]  # MD2 uses clockwise winding
    uv_idx = tris[:, [5, 4, 3]]
    uv = None
    if n_st and skin_w and skin_h:
        uv = st[np.clip(uv_idx.ravel(), 0, n_st - 1)] / [skin_w, skin_h]
        uv[:, 1] = 1.0 - uv[:, 1]
    skins = [d[ofs_skins + 64 * k: ofs_skins + 64 * (k + 1)].split(b"\x00")[0].decode("latin-1") for k in range(n_skins)]
    mats = None
    if skins:
        base = os.path.dirname(path)
        cand = [mb.resolve_file(s, base) for s in skins]
        cand += [os.path.splitext(c)[0] + e for c in cand for e in (".png", ".tga", ".jpg")]
        found = next((c for c in cand if os.path.isfile(c) and not c.lower().endswith(".pcx")), None)
        if found:
            mats = [mb.image_material(os.path.basename(found), found)]
    mb.build_mesh(_name(path), verts, (faces.ravel(), np.full(len(faces), 3)), loop_uvs=uv, materials=mats)
    return {"version": version, "frames": n_frames, "skins": skins}
