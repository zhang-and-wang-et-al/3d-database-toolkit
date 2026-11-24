"""Readers for XML based formats: 3MF, AMF and COLLADA (.dae / .zae).

COLLADA is needed because Blender 5.0 removed its built-in Collada importer.
"""

import io
import math
import os
import re
import shutil
import tempfile
import zipfile
import xml.etree.ElementTree as ET

import bpy
import numpy as np

from . import meshbuild as mb

UNIT_SCALE = {"micron": 1e-6, "millimeter": 1e-3, "centimeter": 1e-2, "inch": 0.0254, "foot": 0.3048, "meter": 1.0}


def _name(path):
    return os.path.splitext(os.path.basename(path))[0]


def _strip_ns(root):
    for el in root.iter():
        if isinstance(el.tag, str) and "}" in el.tag:
            el.tag = el.tag.split("}", 1)[1]
        for k in list(el.attrib):
            if "}" in k:
                el.attrib[k.split("}", 1)[1]] = el.attrib.pop(k)
    return root


def _parse_xml(data):
    return _strip_ns(ET.fromstring(data))


# ---------------------------------------------------------------------------
# 3MF
# ---------------------------------------------------------------------------

def _3mf_matrix(s):
    if not s:
        return np.eye(4)
    v = [float(x) for x in s.split()]
    M = np.eye(4)
    M[:3, :3] = np.array(v[:9]).reshape(3, 3).T  # row-vector convention -> column
    M[:3, 3] = v[9:12]
    return M


def load_3mf(path, ctx):
    zf = zipfile.ZipFile(path)
    names = zf.namelist()
    model_path = "3D/3dmodel.model"
    try:
        rels = _parse_xml(zf.read("_rels/.rels"))
        for r in rels.iter("Relationship"):
            if r.get("Type", "").endswith("/3dmodel"):
                model_path = r.get("Target", model_path).lstrip("/")
    except KeyError:
        pass
    if model_path not in names:
        model_path = next((n for n in names if n.lower().endswith(".model")), None)
        if model_path is None:
            raise ValueError("no .model part in 3MF archive")
    models = {}

    def load_model(part):
        if part not in models:
            root = _parse_xml(zf.read(part))
            objs = {o.get("id"): o for o in root.iter("object")}
            colors = {}
            for bm in root.iter("basematerials"):
                cols = []
                for base in bm.iter("base"):
                    c = base.get("displaycolor", "#CCCCCC").lstrip("#")
                    cols.append(tuple(int(c[k:k + 2], 16) / 255.0 for k in (0, 2, 4)) if len(c) >= 6 else (0.8, 0.8, 0.8))
                colors[bm.get("id")] = cols
            models[part] = (root, objs, colors, UNIT_SCALE.get(root.get("unit", "millimeter"), 1e-3))
        return models[part]

    acc = mb.MeshAccumulator()
    mi = acc.material_index("color", mb.color_attribute_material)
    count = {"items": 0, "meshes": 0}

    def emit(part, oid, M, depth=0):
        if depth > 32:
            return
        root, objs, colors, scale = load_model(part)
        obj = objs.get(oid)
        if obj is None:
            return
        mesh = obj.find("mesh")
        if mesh is not None:
            vs = np.array([[float(v.get(a, 0)) for a in "xyz"] for v in mesh.iter("vertex")]).reshape(-1, 3)
            tris = list(mesh.iter("triangle"))
            if len(vs) and tris:
                faces = np.array([[int(t.get(a)) for a in ("v1", "v2", "v3")] for t in tris], dtype=np.int64)
                opid, opidx = obj.get("pid"), obj.get("pindex")
                fc = []
                for t in tris:
                    pid, p1 = t.get("pid", opid), t.get("p1", opidx)
                    cols = colors.get(pid)
                    fc.append(cols[int(p1)] if cols and p1 is not None and int(p1) < len(cols) else (0.8, 0.8, 0.8))
                w = (vs * scale) @ M[:3, :3].T + M[:3, 3] * 1.0
                acc.add(w, (faces.ravel(), np.full(len(faces), 3)), face_colors=np.array(fc), face_material=mi)
                count["meshes"] += 1
        comps = obj.find("components")
        if comps is not None:
            for c in comps.iter("component"):
                T = _3mf_matrix(c.get("transform"))
                T[:3, 3] *= scale
                emit(c.get("path", "/" + part).lstrip("/"), c.get("objectid"), M @ T, depth + 1)

    root, objs, colors, scale = load_model(model_path)
    build = root.find("build")
    items = list(build.iter("item")) if build is not None else [ET.Element("item", objectid=k) for k in objs]
    for it in items:
        T = _3mf_matrix(it.get("transform"))
        T[:3, 3] *= scale
        emit((it.get("path") or "/" + model_path).lstrip("/"), it.get("objectid"), T)
        count["items"] += 1
    if not acc.face_count:
        raise ValueError("3MF contains no triangles")
    acc.build(_name(path))
    return dict(count, unit=root.get("unit", "millimeter"))


# ---------------------------------------------------------------------------
# AMF
# ---------------------------------------------------------------------------

def load_amf(path, ctx):
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:2] == b"PK":
        zf = zipfile.ZipFile(io.BytesIO(data))
        data = zf.read(zf.namelist()[0])
    root = _parse_xml(data)
    scale = UNIT_SCALE.get(root.get("unit", "millimeter"), 1e-3)
    mats = {}
    for m in root.iter("material"):
        c = m.find("color")
        if c is not None:
            mats[m.get("id")] = tuple(float(c.findtext(k, "0.8")) for k in "rgb")
    acc = mb.MeshAccumulator()
    mi = acc.material_index("color", mb.color_attribute_material)
    nobj = 0
    for obj in root.iter("object"):
        mesh = obj.find("mesh")
        if mesh is None:
            continue
        vs = np.array([[float(v.findtext(f"coordinates/{a}", "0")) for a in "xyz"] for v in mesh.iter("vertex")]).reshape(-1, 3)
        for vol in mesh.iter("volume"):
            faces = np.array([[int(t.findtext(k)) for k in ("v1", "v2", "v3")] for t in vol.iter("triangle")], dtype=np.int64)
            if len(faces) and len(vs):
                col = mats.get(vol.get("materialid"), (0.8, 0.8, 0.8))
                acc.add(vs * scale, (faces.ravel(), np.full(len(faces), 3)),
                        face_colors=np.tile(col, (len(faces), 1)), face_material=mi)
        nobj += 1
    if not acc.face_count:
        raise ValueError("AMF contains no triangles")
    acc.build(_name(path))
    return {"objects": nobj, "unit": root.get("unit", "millimeter")}


# ---------------------------------------------------------------------------
# COLLADA
# ---------------------------------------------------------------------------

def _floats(text):
    return np.array(text.split(), dtype=np.float64) if text else np.zeros(0)


def _ints(text):
    return np.array(text.split(), dtype=np.int64) if text else np.zeros(0, dtype=np.int64)


def _node_matrix(node):
    M = np.eye(4)
    for el in node:
        tag = el.tag
        if tag == "matrix":
            M = M @ _floats(el.text).reshape(4, 4)
        elif tag == "translate":
            T = np.eye(4)
            T[:3, 3] = _floats(el.text)[:3]
            M = M @ T
        elif tag == "scale":
            S = np.eye(4)
            S[[0, 1, 2], [0, 1, 2]] = _floats(el.text)[:3]
            M = M @ S
        elif tag == "rotate":
            x, y, z, a = _floats(el.text)[:4]
            n = math.sqrt(x * x + y * y + z * z) or 1.0
            x, y, z = x / n, y / n, z / n
            c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
            R = np.eye(4)
            R[:3, :3] = [[c + x * x * (1 - c), x * y * (1 - c) - z * s, x * z * (1 - c) + y * s],
                         [y * x * (1 - c) + z * s, c + y * y * (1 - c), y * z * (1 - c) - x * s],
                         [z * x * (1 - c) - y * s, z * y * (1 - c) + x * s, c + z * z * (1 - c)]]
            M = M @ R
    return M


def load_collada(path, ctx):
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:2] != b"PK":
        return _load_collada(data, os.path.dirname(path), path, ctx)
    # .zae: unpack next to a temp dir so textures inside the archive are found, then pack them
    zf = zipfile.ZipFile(io.BytesIO(data))
    dae = next((n for n in zf.namelist() if n.lower().endswith(".dae")), None)
    if dae is None:
        raise ValueError("no .dae inside archive")
    tmp = tempfile.mkdtemp(prefix="showcase_zae_")
    try:
        zf.extractall(tmp)
        details = _load_collada(zf.read(dae), os.path.join(tmp, os.path.dirname(dae)), path, ctx)
        for img in bpy.data.images:
            fp = bpy.path.abspath(img.filepath)
            if fp.startswith(tmp) and os.path.isfile(fp):
                img.pack()
        return details
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def _load_collada(data, base, path, ctx):
    root = _parse_xml(data)
    ids = {el.get("id"): el for el in root.iter() if el.get("id")}
    unit = root.find("asset/unit")
    scale = float(unit.get("meter", 1.0)) if unit is not None else 1.0
    up = (root.findtext("asset/up_axis") or "Y_UP").strip().upper()

    def source(ref):
        el = ids.get(ref.lstrip("#"))
        if el is None:
            return None
        if el.tag == "vertices":
            inp = next((i for i in el.iter("input") if i.get("semantic") == "POSITION"), None)
            return source(inp.get("source")) if inp is not None else None
        arr = el.find("float_array")
        acc = el.find("technique_common/accessor")
        stride = int(acc.get("stride", 3)) if acc is not None else 3
        vals = _floats(arr.text if arr is not None else "")
        return vals[: len(vals) // stride * stride].reshape(-1, stride)

    # materials: id -> (rgb, texture path)
    def material_info(mat_id):
        mat = ids.get(mat_id)
        if mat is None:
            return (0.8, 0.8, 0.8), None
        ie = mat.find("instance_effect")
        eff = ids.get(ie.get("url", "").lstrip("#")) if ie is not None else None
        if eff is None:
            return (0.8, 0.8, 0.8), None
        diff = next(iter(eff.iter("diffuse")), None)
        if diff is None:
            return (0.8, 0.8, 0.8), None
        col = diff.find("color")
        tex = diff.find("texture")
        img_path = None
        if tex is not None:
            ref = tex.get("texture")
            # sampler -> surface -> image, or directly an image id
            for p in eff.iter("newparam"):
                if p.get("sid") == ref:
                    src = p.findtext("sampler2D/source")
                    if src:
                        for p2 in eff.iter("newparam"):
                            if p2.get("sid") == src:
                                ref = p2.findtext("surface/init_from") or ref
                    else:
                        ref = p.findtext("sampler2D/instance_image") or ref
            img = ids.get(ref) if ref else None
            if img is not None:
                init = img.findtext("init_from") or img.findtext("init_from/ref") or ""
                init = re.sub(r"^file:/+", "/" if os.name != "nt" else "", init.strip())
                img_path = mb.resolve_file(init.replace("%20", " "), base)
        rgb = tuple(_floats(col.text)[:3]) if col is not None else (0.8, 0.8, 0.8)
        return rgb, img_path

    acc = mb.MeshAccumulator()
    stats = {"geometries": 0, "instances": 0}

    def emit_geometry(geo_id, M, bindings):
        geo = ids.get(geo_id)
        mesh = geo.find("mesh") if geo is not None else None
        if mesh is None:
            return
        stats["instances"] += 1
        for prim in mesh:
            if prim.tag not in ("triangles", "polylist", "polygons", "trifans", "tristrips"):
                continue
            inputs = prim.findall("input")
            if not inputs:
                continue
            stride = max(int(i.get("offset", 0)) for i in inputs) + 1
            vin = next((i for i in inputs if i.get("semantic") == "VERTEX"), None)
            tin = next((i for i in inputs if i.get("semantic") == "TEXCOORD"), None)
            if vin is None:
                continue
            pos = source(vin.get("source"))
            if pos is None or not len(pos):
                continue
            uvs = source(tin.get("source")) if tin is not None else None
            voff = int(vin.get("offset", 0))
            toff = int(tin.get("offset", 0)) if tin is not None else None
            faces_v, faces_t = [], []
            plist = [_ints(p.text) for p in prim.findall("p")]
            if prim.tag == "triangles" and plist:
                P = plist[0][: len(plist[0]) // stride * stride].reshape(-1, stride)
                n = len(P) // 3 * 3
                faces_v = P[:n, voff].reshape(-1, 3)
                faces_t = P[:n, toff].reshape(-1, 3) if toff is not None else None
                totals = np.full(len(faces_v), 3)
                flat_v = faces_v.ravel()
                flat_t = faces_t.ravel() if faces_t is not None else None
            else:
                if prim.tag == "polylist" and plist:
                    vc = _ints(prim.findtext("vcount"))
                    P = plist[0][: len(plist[0]) // stride * stride].reshape(-1, stride)
                    polys, k = [], 0
                    for c in vc:
                        polys.append(P[k:k + c])
                        k += c
                else:
                    polys = [p[: len(p) // stride * stride].reshape(-1, stride) for p in plist]
                    if prim.tag in ("trifans", "tristrips"):
                        tri = []
                        for p in polys:
                            for a in range(len(p) - 2):
                                if prim.tag == "trifans":
                                    tri.append(p[[0, a + 1, a + 2]])
                                else:
                                    tri.append(p[[a, a + 1, a + 2]] if a % 2 == 0 else p[[a + 1, a, a + 2]])
                        polys = tri
                polys = [p for p in polys if len(p) >= 3]
                if not polys:
                    continue
                totals = np.array([len(p) for p in polys])
                allp = np.concatenate(polys)
                flat_v = allp[:, voff]
                flat_t = allp[:, toff] if toff is not None else None
            if not len(totals):
                continue
            w = pos[:, :3] @ M[:3, :3].T + M[:3, 3]
            loop_uv = None
            if uvs is not None and flat_t is not None and len(uvs):
                loop_uv = uvs[np.clip(flat_t, 0, len(uvs) - 1), :2]
            symbol = prim.get("material")
            rgb, tex = material_info(bindings.get(symbol, symbol or ""))
            if tex:
                mi = acc.material_index(("tex", tex), lambda: mb.image_material(os.path.basename(tex), tex))
                acc.add(w, (flat_v, totals), loop_uvs=loop_uv, face_material=mi)
            else:
                mi = acc.material_index("color", mb.color_attribute_material)
                acc.add(w, (flat_v, totals), loop_uvs=loop_uv, face_colors=np.tile(rgb, (len(totals), 1)), face_material=mi)

    def walk(node, M, depth=0):
        if depth > 64:
            return
        M = M @ _node_matrix(node)
        for el in node:
            if el.tag == "instance_geometry":
                bindings = {im.get("symbol"): im.get("target", "").lstrip("#")
                            for im in el.iter("instance_material")}
                emit_geometry(el.get("url", "").lstrip("#"), M, bindings)
            elif el.tag == "instance_node":
                target = ids.get(el.get("url", "").lstrip("#"))
                if target is not None:
                    walk(target, M, depth + 1)
            elif el.tag == "node":
                walk(el, M, depth + 1)

    scene = root.find("scene/instance_visual_scene")
    vs = ids.get(scene.get("url", "").lstrip("#")) if scene is not None else None
    if vs is None:
        vs = root.find("library_visual_scenes/visual_scene")
    if vs is not None:
        for n in vs.findall("node"):
            walk(n, np.eye(4))
    else:
        for geo in root.iter("geometry"):
            emit_geometry(geo.get("id"), np.eye(4), {})
    stats["geometries"] = len(list(root.iter("geometry")))
    if not acc.face_count:
        raise ValueError("no polygon geometry found in COLLADA file")
    for i, v in enumerate(acc.verts):
        v = v * scale
        if up == "Y_UP":
            v = mb.y_up_to_z_up(v)
        elif up == "X_UP":
            v = np.stack([-v[:, 2], v[:, 1], v[:, 0]], axis=1)
        acc.verts[i] = v
    acc.build(_name(path))
    return dict(stats, up_axis=up, unit_meter=scale)
