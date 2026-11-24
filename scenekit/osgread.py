"""Build the scene model (``model``) from an OSG text document (``osgtext``).

Handles both the ``.osgt`` serializer format (old ``VertexData`` style and the
current ``VertexArray`` / object primitive set style) and the legacy ``.osg`` format.
"""

import base64

import numpy as np

from . import images
from . import model as M
from .osgtext import Num, Obj, Q

BINDINGS = {
    "BIND_OFF": "off", "BIND_OVERALL": "overall", "BIND_PER_PRIMITIVE_SET": "primitive_set",
    "BIND_PER_PRIMITIVE": "primitive", "BIND_PER_VERTEX": "vertex", "BIND_UNDEFINED": "undefined",
    "OFF": "off", "OVERALL": "overall", "PER_PRIMITIVE_SET": "primitive_set", "PER_PRIMITIVE": "primitive",
    "PER_VERTEX": "vertex",
}
OLD_ARRAY_TYPES = {"Vec2fArray": "Vec2Array", "Vec3fArray": "Vec3Array", "Vec4fArray": "Vec4Array",
                   "UByte4Array": "Vec4ubArray", "Vec4ubArray": "Vec4ubArray"}
TRANSFORM_CLASSES = {"MatrixTransform", "PositionAttitudeTransform", "AutoTransform", "Transform", "Camera",
                     "DOFTransform", "CameraView", "PositionAttitudeTransform"}
NON_CHILD_LEGACY = {"StateSet", "Matrix", "UserData", "UserDataContainer", "Descriptions", "ValueList",
                    "RangeList", "FileNameList", "Shape", "InitialBound"}


def short(cls):
    return cls.split("::")[-1]


def to_float(t, default=0.0):
    try:
        return float(t)
    except (TypeError, ValueError):
        return default


def to_int(t, default=0):
    try:
        return int(t, 0) if isinstance(t, str) and t.lower().startswith("0x") else int(float(t))
    except (TypeError, ValueError):
        return default


def numbers(stmt):
    """Numbers of a statement: from its block (Num) or its tokens."""
    if stmt is None:
        return np.zeros(0)
    if isinstance(stmt.block, Num):
        return stmt.block.data
    if isinstance(stmt.block, list):
        vals = []
        for s in stmt.block:
            vals.extend(to_float(t, np.nan) for t in s.toks)
        return np.array(vals, dtype=np.float64)
    return np.zeros(0)


def int_numbers(stmt):
    """Integer data of a statement (indices, lengths). The document keeps the same int64 array, so
    the numbers are not held twice (float64 in the document + int64 in the scene)."""
    data = numbers(stmt)
    if data.dtype == np.int64:
        return data
    ints = data.astype(np.int64)
    if stmt is not None and isinstance(stmt.block, Num) and np.array_equal(ints, data):
        stmt.block.data = ints
    return ints


def first_obj(stmt):
    """The object inside ``Key TRUE { osg::X {...} }``."""
    if stmt is None:
        return None
    if isinstance(stmt.block, Obj):
        return stmt.block
    if isinstance(stmt.block, list):
        for s in stmt.block:
            if isinstance(s.block, Obj):
                return s.block
    return None


def obj_list(stmt):
    if stmt is None or not isinstance(stmt.block, list):
        return []
    return [s.block for s in stmt.block if isinstance(s.block, Obj)]


class Reader:
    def __init__(self, doc, scene):
        self.doc = doc
        self.scene = scene
        self.osgt = doc.dialect == "osgt"
        self.version = doc.version if self.osgt else 0
        self.nodes = {}
        self.statesets = {}
        self.textures = {}
        self.arrays = {}
        self.array_ids = {}
        self.legacy_uids = {}       # legacy .osg 'VertexArray UniqueID Vec3Array_4 ...' -> Array
        self.base_dir = scene.path and __import__("os").path.dirname(scene.path)
        self.progress = None

    def warn(self, msg):
        if msg not in self.scene.warnings:
            self.scene.warnings.append(msg)

    # ------------------------------------------------------------------ nodes
    def read(self):
        roots = self.doc.root_objects()
        if not roots:
            raise ValueError("no scene objects found in the file")
        if len(roots) == 1:
            return self.node(roots[0])
        top = M.Node(M.GROUP, "(file)", "")
        top.extra["virtual_root"] = True
        top.children = [self.node(o) for o in roots]
        return top

    def node(self, obj):
        n = self.nodes.get(id(obj))
        if n is not None:
            return n
        if self.progress is not None and len(self.nodes) % 20000 == 19999:
            self.progress(f"building the scene ... {len(self.nodes) + 1:,} nodes")
        cls = obj.cls
        s = short(cls)
        kind = self.kind_of(obj, s)
        n = M.Node(kind, cls)
        self.nodes[id(obj)] = n
        n.src = obj
        n.name = str(obj.value("Name" if self.osgt else "name", "") or "")
        mask = obj.value("NodeMask" if self.osgt else "nodeMask")
        if mask is not None:
            n.node_mask = to_int(mask, 0xFFFFFFFF)
        n.stateset = self.stateset_of(obj)
        if kind == M.GEOMETRY:
            try:
                n.geom = self.geometry(obj)
            except Exception as exc:  # keep going: a broken drawable must not stop the inspection
                n.geom = M.GeomData()
                n.geom.optimizable = False
                n.notes.append(f"geometry could not be read: {exc}")
                self.warn(f"{cls} {n.name!r}: geometry could not be read ({exc})")
            if cls != "osg::Geometry" and s != "Geometry":
                n.geom.optimizable = False
                n.notes.append(f"{cls}: geometry is shown but not modified by the optimizer")
        if s == "MatrixTransform":
            vals = numbers(obj.get("Matrix"))
            if len(vals) == 16:
                n.matrix = M.osg_matrix_to_np(vals)
        elif s == "PositionAttitudeTransform":
            n.matrix = self.pat(obj)
        elif kind == M.TRANSFORM:
            n.notes.append(f"{s}: transform is computed at runtime (treated as identity)")
        if kind in (M.LOD, M.PAGED_LOD):
            self.lod(obj, n)
        if kind == M.SWITCH:
            vl = obj.get("ValueList")
            if vl is not None:
                vals = []
                if isinstance(vl.block, Num):
                    vals = [bool(v) for v in vl.block.data.tolist()]
                elif isinstance(vl.block, list):
                    for st in vl.block:
                        vals.extend(t.upper() in ("TRUE", "1", "ON") for t in st.toks)
                elif isinstance(vl.block, Obj):
                    for st in vl.block.stmts:
                        vals.extend(t.upper() in ("TRUE", "1", "ON") for t in st.toks)
                n.switch_values = vals
        if kind == M.PROXY:
            fl = obj.get("FileNames" if self.osgt else "FileNameList")
            n.files = self.strings(fl)
        n.children = [self.node(c) for c in self.child_objs(obj, kind)]
        return n

    def kind_of(self, obj, s):
        if s in ("Geometry",) or obj.get("PrimitiveSetList") is not None or obj.get("VertexArray") is not None \
                or obj.get("VertexData") is not None or obj.get("PrimitiveSets") is not None \
                or obj.get("Primitives") is not None:
            return M.GEOMETRY
        if s == "PagedLOD":
            return M.PAGED_LOD
        if s == "LOD":
            return M.LOD
        if s in ("Switch", "MultiSwitch"):
            return M.SWITCH
        if s == "ProxyNode":
            return M.PROXY
        if s in TRANSFORM_CLASSES or s.endswith("Transform"):
            return M.TRANSFORM
        if s in ("Geode", "Billboard"):
            return M.GEODE
        if s == "Group" or obj.get("Children") is not None or obj.get("num_children") is not None:
            return M.GROUP
        if obj.get("Drawables") is not None or obj.get("num_drawables") is not None:
            return M.GEODE
        return M.OTHER

    def child_objs(self, obj, kind):
        if self.osgt:
            out = obj_list(obj.get("Children"))
            out += obj_list(obj.get("Drawables"))
            return out
        if kind == M.GEOMETRY:
            return []
        out = []
        for st in obj.stmts:
            b = st.block
            if isinstance(b, Obj) and b.cls not in NON_CHILD_LEGACY and not b.cls.endswith("Callback"):
                if kind == M.OTHER and not (b.get("num_children") or b.get("num_drawables") or b.get("PrimitiveSets")
                                               or b.get("Primitives")):
                    continue
                out.append(b)
        return out

    def pat(self, obj):
        k = (lambda a, b: a) if self.osgt else (lambda a, b: b)
        pos = numbers_tokens(obj.get(k("Position", "position")), 3, [0, 0, 0])
        att = numbers_tokens(obj.get(k("Attitude", "attitude")), 4, [0, 0, 0, 1])
        scl = numbers_tokens(obj.get(k("Scale", "scale")), 3, [1, 1, 1])
        piv = numbers_tokens(obj.get(k("PivotPoint", "pivotPoint")), 3, [0, 0, 0])
        return M.pat_matrix(pos, att, scl, piv)

    def lod(self, obj, n):
        rl = obj.get("RangeList")
        vals = numbers(rl)
        n.ranges = [(float(vals[i]), float(vals[i + 1])) for i in range(0, len(vals) - 1, 2)]
        rm = obj.value("RangeMode")
        if rm:
            n.range_mode = rm
        if self.osgt:
            uc = obj.get("UserCenter")
            if uc is not None and len(uc.toks) >= 5:
                n.center = tuple(to_float(t) for t in uc.toks[1:5])
            if n.kind == M.PAGED_LOD:
                n.files = self.strings(obj.get("RangeDataList"))
        else:
            c = obj.get("Center")
            r = obj.value("Radius")
            if c is not None and len(c.toks) >= 4:
                n.center = tuple(to_float(t) for t in c.toks[1:4]) + (to_float(r, -1),)
            if n.kind == M.PAGED_LOD:
                n.files = self.strings(obj.get("FileNameList"))

    @staticmethod
    def strings(stmt):
        if stmt is None or not isinstance(stmt.block, list):
            return []
        out = []
        for st in stmt.block:
            out.extend(str(t) for t in st.toks)
        return out

    # ------------------------------------------------------------------ geometry
    def geometry(self, obj):
        g = M.GeomData()
        if self.osgt:
            self.prims_osgt(obj.get("PrimitiveSetList"), g)
            if obj.get("VertexData") is not None or obj.get("NormalData") is not None:
                self.old_arrays(obj, g)
            else:
                g.vertices = self.array_obj(first_obj(obj.get("VertexArray")))
                g.normals = self.array_obj(first_obj(obj.get("NormalArray")))
                g.colors = self.array_obj(first_obj(obj.get("ColorArray")))
                for name in ("SecondaryColorArray", "FogCoordArray"):
                    a = self.array_obj(first_obj(obj.get(name)))
                    if a is not None:
                        g.extra[name] = a
                for unit, ao in enumerate(self.list_entries(obj.get("TexCoordArrayList"))):
                    a = self.array_obj(ao)
                    if a is not None:
                        g.texcoords[unit] = a
                for idx, ao in enumerate(self.list_entries(obj.get("VertexAttribArrayList"))):
                    a = self.array_obj(ao)
                    if a is not None:
                        g.extra[f"VertexAttrib{idx}"] = a
        else:
            self.legacy_geometry(obj, g)
        self.fix_bindings(g)
        return g

    @staticmethod
    def list_entries(stmt):
        """Entries of a vector-of-objects serializer, keeping positions of NULL entries."""
        if stmt is None or not isinstance(stmt.block, list):
            return []
        return [s.block if isinstance(s.block, Obj) else None for s in stmt.block]

    def array_obj(self, ao):
        if ao is None:
            return None
        if id(ao) in self.arrays:
            return self.arrays[id(ao)]
        t = short(ao.cls)
        comps = M.ARRAY_TYPES.get(t, (1, np.float32))[0]
        vec = ao.get("vector")
        data = numbers(vec)
        binding = BINDINGS.get(ao.value("Binding", "BIND_UNDEFINED"), "undefined")
        a = M.Array(reshape(data, comps), t, binding, to_int(ao.value("Normalize", "0")) != 0, src=ao)
        self.arrays[id(ao)] = a
        return a

    def prims_osgt(self, stmt, g):
        if stmt is None or not isinstance(stmt.block, list):
            return
        for st in stmt.block:
            if isinstance(st.block, Obj):
                o = st.block
                s = short(o.cls)
                mode = str(o.value("Mode", "POINTS")).replace("GL_", "")
                inst = to_int(o.value("NumInstances", "0"))
                if s == "DrawArrays":
                    g.prims.append(M.Primitive(mode, "arrays", to_int(o.value("First", "0")), to_int(o.value("Count", "0")),
                                               instances=inst))
                elif s == "DrawArrayLengths":
                    g.prims.append(M.Primitive(mode, "lengths", to_int(o.value("First", "0")),
                                               lengths=int_numbers(o.get("vector")), instances=inst))
                elif s in M.INDEX_TYPES:
                    g.prims.append(M.Primitive(mode, "elements", indices=int_numbers(o.get("vector")),
                                               index_type=s, instances=inst))
                else:
                    g.optimizable = False
                    g.notes.append(f"unsupported primitive set {o.cls}")
            elif st.toks:
                self.old_prim(st, g)

    def old_prim(self, st, g):
        """``DrawElementsUShort GL_TRIANGLES [instances] 36 { ... }`` and friends."""
        t = st.toks
        s = t[0]
        if len(t) < 2:
            return
        if s == "DrawArraysLength":  # old osgt spelling
            s = "DrawArrayLengths"
        mode = str(t[1]).replace("GL_", "")
        nums = [to_int(x) for x in t[2:]]
        if s == "DrawArrays" and len(nums) >= 2:
            g.prims.append(M.Primitive(mode, "arrays", nums[-2], nums[-1], instances=nums[0] if len(nums) > 2 else 0))
        elif s == "DrawArrayLengths" and len(nums) >= 2:
            g.prims.append(M.Primitive(mode, "lengths", nums[-2], lengths=int_numbers(st),
                                       instances=nums[0] if len(nums) > 2 else 0))
        elif s in M.INDEX_TYPES:
            g.prims.append(M.Primitive(mode, "elements", indices=int_numbers(st), index_type=s,
                                       instances=nums[0] if len(nums) > 1 else 0))
        else:
            g.optimizable = False
            g.notes.append(f"unsupported primitive set {s}")

    def old_arrays(self, obj, g):
        """``VertexData { Array TRUE ArrayID 1 Vec3fArray 8 {...} Indices FALSE Binding ... }``."""
        def one(block_stmts):
            arr = None
            idx = None
            binding = "undefined"
            norm = False
            for st in block_stmts:
                k = st.key
                if k == "Array":
                    arr = self.old_array(st)
                elif k == "Indices":
                    idx = self.old_array(st)
                elif k == "Binding" and len(st.toks) > 1:
                    binding = BINDINGS.get(st.toks[1], "undefined")
                elif k == "Normalize" and len(st.toks) > 1:
                    norm = to_int(st.toks[1]) != 0
            if arr is None:
                return None
            a = M.Array(arr[1], arr[0], binding, norm)
            if idx is not None:
                a.data = a.data[np.clip(idx[1].reshape(-1).astype(np.int64), 0, max(len(a.data) - 1, 0))]
                g.notes.append("deprecated index arrays were expanded")
            return a

        for key, attr in (("VertexData", "vertices"), ("NormalData", "normals"), ("ColorData", "colors")):
            st = obj.get(key)
            if st is not None and isinstance(st.block, list):
                setattr(g, attr, one(st.block))
        for key in ("SecondaryColorData", "FogCoordData"):
            st = obj.get(key)
            if st is not None and isinstance(st.block, list):
                a = one(st.block)
                if a is not None:
                    g.extra[key.replace("Data", "Array")] = a
        for key, prefix in (("TexCoordData", None), ("VertexAttribData", "VertexAttrib")):
            st = obj.get(key)
            if st is None or not isinstance(st.block, list):
                continue
            unit = 0
            for d in st.block:
                if d.key == "Data" and isinstance(d.block, list):
                    a = one(d.block)
                    if a is not None:
                        if prefix:
                            g.extra[f"{prefix}{unit}"] = a
                        else:
                            g.texcoords[unit] = a
                    unit += 1
        g.notes.append(f"old osgt geometry layout (file version {self.version})")

    def old_array(self, st):
        """Array TRUE ArrayID 3 Vec2fArray 24 {...}  /  Array TRUE ArrayID 3 (shared)."""
        t = st.toks
        if len(t) < 2 or str(t[1]).upper() != "TRUE":
            return None
        aid = t[3] if len(t) > 3 and t[2] == "ArrayID" else None
        if len(t) <= 4 and aid is not None:
            return self.array_ids.get(aid)
        tname = t[4] if aid is not None else t[2]
        tname = OLD_ARRAY_TYPES.get(tname, tname)
        comps = M.ARRAY_TYPES.get(tname, (1, np.float32))[0]
        res = (tname, reshape(numbers(st), comps))
        if aid is not None:
            self.array_ids[aid] = res
        return res

    def legacy_geometry(self, obj, g):
        bindings = {}
        indices = {}
        for st in obj.stmts:
            k = st.key
            t = st.toks
            if k in ("PrimitiveSets", "Primitives") and isinstance(st.block, list):  # "Primitives": OSG 1.x
                for p in st.block:
                    self.old_prim(p, g)
            elif k in ("NormalBinding", "ColorBinding", "SecondaryColorBinding", "FogCoordBinding") and len(t) > 1:
                bindings[k.replace("Binding", "")] = BINDINGS.get(t[1], "undefined")
            elif k in ("VertexArray", "NormalArray", "ColorArray", "SecondaryColorArray", "FogCoordArray"):
                a = self.legacy_array(t[1:], st)
                if a is not None:
                    name = k.replace("Array", "")
                    if k == "VertexArray":
                        g.vertices = a
                    elif k == "NormalArray":
                        g.normals = a
                    elif k == "ColorArray":
                        g.colors = a
                    else:
                        g.extra[k] = a
                    a.binding = "vertex" if name == "Vertex" else a.binding
            elif k == "TexCoordArray" and len(t) > 2:
                # 'TexCoordArray 0 [UniqueID id] Vec2Array 24 {...}' or 'TexCoordArray Use id' (unit 0)
                unit, rest = (0, t[1:]) if t[1] == "Use" else (to_int(t[1]), t[2:])
                a = self.legacy_array(rest, st)
                if a is not None:
                    g.texcoords[unit] = a
            elif k == "VertexAttribArray" and len(t) > 2:
                a = self.legacy_array(t[2:], st)
                if a is not None:
                    g.extra[f"VertexAttrib{to_int(t[1])}"] = a
            elif k.endswith("Indices") and isinstance(st.block, Num):
                indices[k] = st.block.data.astype(np.int64)
        for name, attr in (("Normal", "normals"), ("Color", "colors")):
            a = getattr(g, attr)
            if a is not None:
                a.binding = bindings.get(name, "vertex")
        for name in ("SecondaryColor", "FogCoord"):
            a = g.extra.get(name + "Array")
            if a is not None:
                a.binding = bindings.get(name, "vertex")
        if indices:
            # deprecated per-attribute index arrays: expand to plain per-vertex arrays
            def expand(a, key):
                if a is not None and key in indices and len(a.data):
                    a.data = a.data[np.clip(indices[key], 0, len(a.data) - 1)]
            expand(g.vertices, "VertexIndices")
            expand(g.normals, "NormalIndices")
            expand(g.colors, "ColorIndices")
            for u, a in g.texcoords.items():
                expand(a, "TexCoordIndices")
            g.notes.append("deprecated index arrays were expanded")

    def legacy_array(self, t, st):
        """``Vec3Array 12`` + block, very old ``12`` + block, ``UniqueID id Vec3Array 12`` + block
        (shared later with ``Use id``)."""
        if len(t) >= 2 and t[0] == "Use":
            a = self.legacy_uids.get(t[1])
            return None if a is None else M.Array(a.data, a.type, a.binding, a.normalize)
        uid = None
        if len(t) >= 3 and t[0] == "UniqueID":
            uid, t = t[1], t[2:]
        if not isinstance(st.block, Num):
            return None
        tname = "Vec3Array" if (t and str(t[0]).isdigit()) else (OLD_ARRAY_TYPES.get(t[0], t[0]) if t else "Vec3Array")
        comps = M.ARRAY_TYPES.get(tname, (1, np.float32))[0]
        a = M.Array(reshape(st.block.data, comps), tname, "vertex")
        if uid is not None:
            self.legacy_uids[uid] = a
        return a

    @staticmethod
    def fix_bindings(g):
        nv = g.num_vertices
        for _, a in g.arrays():
            if a.binding == "undefined":
                if len(a.data) == nv:
                    a.binding = "vertex"
                elif len(a.data) == 1:
                    a.binding = "overall"
                else:
                    a.binding = "vertex"
        for _, a in g.arrays():
            if a.binding in ("primitive", "primitive_set"):
                g.optimizable = False
                g.notes.append(f"{a.binding.replace('_', ' ')} binding (deprecated) - geometry is not re-indexed")
                break
            if a.binding == "vertex" and len(a.data) != nv and a is not g.vertices:
                g.optimizable = False
                g.notes.append("attribute array length differs from the vertex count")
                break

    # ------------------------------------------------------------------ state
    def stateset_of(self, obj):
        if self.osgt:
            so = first_obj(obj.get("StateSet"))
        else:
            so = None
            for st in obj.stmts:
                if isinstance(st.block, Obj) and st.block.cls == "StateSet":
                    so = st.block
                    break
        if so is None:
            return None
        ss = self.statesets.get(id(so))
        if ss is not None:
            return ss
        ss = M.StateSet()
        ss.src = so
        self.statesets[id(so)] = ss
        try:
            if self.osgt:
                self.stateset_osgt(so, ss)
            else:
                self.stateset_legacy(so, ss)
        except Exception as exc:
            self.warn(f"state set could not be fully read: {exc}")
        return ss

    def stateset_osgt(self, so, ss):
        ml = so.get("ModeList")
        if ml is not None and isinstance(ml.block, list):
            for st in ml.block:
                if len(st.toks) >= 2:
                    ss.modes[str(st.toks[0])] = str(st.toks[1])
        for ao in obj_list(so.get("AttributeList")):
            self.attribute(ao, ss)
        tal = so.get("TextureAttributeList")
        if tal is not None and isinstance(tal.block, list):
            unit = 0
            for d in tal.block:
                if d.key == "Data":
                    for ao in obj_list(d):
                        self.attribute(ao, ss, unit)
                    unit += 1
        hint = so.value("RenderingHint")
        if hint is not None and to_int(hint) == 2:
            ss.transparent = True
        if ss.modes.get("GL_BLEND", "").startswith("ON"):
            ss.transparent = True

    def stateset_legacy(self, so, ss):
        for st in so.stmts:
            k = st.key
            if isinstance(st.block, Obj):
                self.attribute(st.block, ss)
            elif k == "textureUnit" and isinstance(st.block, list):
                unit = to_int(st.toks[1]) if len(st.toks) > 1 else 0
                for sub in st.block:
                    if isinstance(sub.block, Obj):
                        self.attribute(sub.block, ss, unit)
            elif k.startswith("GL_") and len(st.toks) >= 2:
                ss.modes[k] = str(st.toks[1])
            elif k == "rendering_hint" and len(st.toks) > 1 and st.toks[1] == "TRANSPARENT_BIN":
                ss.transparent = True
        if ss.modes.get("GL_BLEND", "").startswith("ON"):
            ss.transparent = True

    def attribute(self, ao, ss, unit=None):
        s = short(ao.cls)
        if s == "Material":
            ss.material_name = str(ao.value("Name" if self.osgt else "name", "") or "")
            if self.osgt:
                d = ao.get("Diffuse")
                if d is not None and len(d.toks) >= 7:
                    ss.diffuse = tuple(to_float(x) for x in d.toks[3:7])
            else:
                d = ao.get("diffuseColor")
                if d is not None and len(d.toks) >= 5:
                    ss.diffuse = tuple(to_float(x) for x in d.toks[1:5])
            if ss.diffuse is not None and ss.diffuse[3] < 0.999:
                ss.transparent = True
            ss.summary.append("Material" + (f" '{ss.material_name}'" if ss.material_name else ""))
        elif s.startswith("Texture") and s not in ("TexEnv", "TexGen", "TexMat", "TexEnvCombine"):
            tex = self.texture(ao)
            tex.unit = unit or 0
            ss.textures.append(tex)
            ss.summary.append(f"{s}[{tex.unit}] {tex.label}")
        else:
            ss.summary.append(s)

    def texture(self, to):
        tex = self.textures.get(id(to))
        if tex is not None:
            return tex
        tex = M.Texture()
        tex.src = to
        self.textures[id(to)] = tex
        if self.osgt:
            img = None
            for st in to.stmts:
                if isinstance(st.block, Obj) and (st.block.style == "image" or short(st.block.cls) == "Image"):
                    img = st.block
                    break
            if img is not None:
                tex.image_src = img
                tex.file = str(img.value("FileName", "") or "")
                size = img.get("Size")
                if size is not None and len(size.toks) >= 3:
                    tex.width, tex.height = to_int(size.toks[1]), to_int(size.toks[2])
                data = img.get("Data")
                if data is not None:
                    tex.embedded = True
                    tex.file_bytes = embedded_size(data)
                    pf = to_int(img.value("PixelFormat", "0"))
                    tex.channels = {6407: 3, 6408: 4, 6409: 1, 6410: 2, 32992: 3, 32993: 4}.get(pf, 4)
                    tex.fmt = "inline"
        else:
            tex.file = str(to.value("file", "") or "")
        if tex.file and not tex.embedded:
            images.fill_texture(tex, self.base_dir or ".")
        elif tex.embedded:
            tex.exists = True
        return tex


def embedded_size(stmt):
    """Bytes of inline image data (osgt writes it as quoted, encoded strings)."""
    total = 0
    if isinstance(stmt.block, list):
        for st in stmt.block:
            for t in st.toks:
                if isinstance(t, Q):
                    total += len(t) * 3 // 4
    return total


def numbers_tokens(stmt, n, default):
    if stmt is None or len(stmt.toks) < n + 1:
        return default
    return [to_float(x) for x in stmt.toks[1:n + 1]]


def reshape(data, comps):
    data = np.asarray(data, dtype=np.float64)
    if comps <= 1:
        return data.reshape(-1)
    k = len(data) // comps * comps
    return data[:k].reshape(-1, comps)


def read(doc, scene, progress=None):
    """Fill ``scene.root`` from ``doc``."""
    r = Reader(doc, scene)
    r.progress = progress
    scene.doc = doc
    scene.version = doc.version if doc.dialect == "osgt" else None
    for line in doc.header:
        if line.startswith("#Generator"):
            scene.info["generator"] = line[len("#Generator"):].strip()
    scene.root = r.read()
    scene.info["statesets"] = len(r.statesets)
    scene.info["textures"] = len(r.textures)
    return scene


def decode_inline(stmt):
    """Best effort decode of inline image data (used for exporting embedded textures)."""
    chunks = []
    if isinstance(stmt.block, list):
        for st in stmt.block:
            for t in st.toks:
                if isinstance(t, Q):
                    chunks.append(t.replace("\n", ""))
    try:
        return base64.b64decode("".join(chunks))
    except Exception:
        return None
