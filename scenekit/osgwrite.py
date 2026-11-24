"""Write the scene model back as OpenSceneGraph text (.osgt or legacy .osg).

Nodes that came from a file are written by *patching* their original document
objects: only what the model manages (children, geometry data, matrices, LOD
ranges, state set references, file names) is regenerated, everything else
(callbacks, shaders, user data, unknown classes) is copied unchanged. Nodes
created by the optimizer, and scenes read from other formats (OpenFlight), are
generated from scratch in the current osgt layout (file version 161).
"""

import os

import numpy as np

from . import model as M
from .osgtext import NullSink, Obj, Q, Writer, fmt_token, format_numbers

OSGT_VERSION = 161
GENERATOR = "#Generator OpenSceneGraph 3.6.5"   # osgDB reads exactly two tokens after #Generator

GEOMETRY_KEYS_OSGT = {"PrimitiveSetList", "VertexArray", "NormalArray", "ColorArray", "SecondaryColorArray",
                      "FogCoordArray", "TexCoordArrayList", "VertexAttribArrayList", "VertexData", "NormalData",
                      "ColorData", "SecondaryColorData", "FogCoordData", "TexCoordData", "VertexAttribData",
                      "FastPathHint"}
GEOMETRY_KEYS_OSG = {"PrimitiveSets", "Primitives", "VertexArray", "NormalBinding", "NormalArray", "ColorBinding", "ColorArray",
                     "SecondaryColorBinding", "SecondaryColorArray", "FogCoordBinding", "FogCoordArray",
                     "TexCoordArray", "VertexAttribBinding", "VertexAttribArray", "VertexAttribNormalize",
                     "VertexIndices", "NormalIndices", "ColorIndices", "SecondaryColorIndices", "FogCoordIndices",
                     "TexCoordIndices", "VertexAttribIndices"}
NODE_HEAD_KEYS = {"Name", "DataVariance", "UserData", "UserDataContainer", "InitialBound",
                  "ComputeBoundingSphereCallback", "UpdateCallback", "EventCallback", "CullCallback", "CullingActive",
                  "NodeMask", "Descriptions", "StateSet"}
BINDING_OSGT = {"vertex": "BIND_PER_VERTEX", "overall": "BIND_OVERALL", "primitive_set": "BIND_PER_PRIMITIVE_SET",
                "primitive": "BIND_PER_PRIMITIVE", "off": "BIND_OFF", "undefined": "BIND_PER_VERTEX"}
BINDING_OSG = {"vertex": "PER_VERTEX", "overall": "OVERALL", "primitive_set": "PER_PRIMITIVE_SET",
               "primitive": "PER_PRIMITIVE", "off": "OFF", "undefined": "PER_VERTEX"}
CLASS_FOR_KIND = {M.GROUP: "osg::Group", M.GEODE: "osg::Geode", M.TRANSFORM: "osg::MatrixTransform",
                  M.LOD: "osg::LOD", M.SWITCH: "osg::Switch", M.PROXY: "osg::ProxyNode", M.GEOMETRY: "osg::Geometry",
                  M.PAGED_LOD: "osg::PagedLOD", M.OTHER: "osg::Group"}
DOUBLE_TYPES = {"Vec2dArray", "Vec3dArray", "Vec4dArray", "DoubleArray"}


class _Base:
    def __init__(self, scene, out_path):
        self.scene = scene
        self.out_path = os.path.abspath(out_path)
        self.out_dir = os.path.dirname(os.path.abspath(out_path))
        self.src_dir = os.path.dirname(os.path.abspath(scene.converted_from or scene.path or out_path))
        self.relocate = os.path.normcase(self.out_dir) != os.path.normcase(self.src_dir)

    def rel_file(self, name, resolved=None):
        """A file reference that still works from the output folder."""
        if not name or not self.relocate:
            return name
        if os.path.isabs(name.replace("\\", "/")) and not resolved:
            return name
        target = resolved or os.path.normpath(os.path.join(self.src_dir, name.replace("\\", "/")))
        try:
            return os.path.relpath(target, self.out_dir).replace("\\", "/")
        except ValueError:  # different drive on Windows
            return target

    def file_ref(self, n, name):
        """External file reference as written: an optimised copy (``files_out``, set by save_scene) or
        the original file, relative to the output folder."""
        out = n.extra.get("files_out")
        if out and name in out:
            return out[name]
        return self.rel_file(name)

    def fix_texture_names(self):
        """Point texture references at files that work from the output folder; write downscaled copies."""
        resized = self.export_resized()
        if not self.relocate and not resized:
            return
        for n in self.scene.iter_nodes():
            ss = n.stateset
            if ss is None:
                continue
            for t in ss.textures:
                if t.embedded or not t.file:
                    continue
                new = resized.get(id(t)) or self.rel_file(t.file, t.path)
                if self.scene.doc is None:
                    t.file = new
                    continue
                obj = t.image_src if self.scene.doc.dialect == "osgt" else t.src
                key = "FileName" if self.scene.doc.dialect == "osgt" else "file"
                st = obj.get(key) if obj is not None else None
                if st is not None and len(st.toks) > 1:
                    st.toks[1] = Q(new)

    def export_resized(self):
        """Write smaller copies of textures marked by the 'downscale_textures' pass. -> {id(tex): new name}."""
        todo = {}
        for n in self.scene.iter_nodes():
            if n.stateset is not None:
                for t in n.stateset.textures:
                    if t.resize and t.path and not t.embedded:
                        todo[id(t)] = t
        if not todo:
            return {}
        try:
            from PIL import Image
        except ImportError:
            return {}
        base = os.path.splitext(os.path.basename(self.out_path))[0]
        folder = os.path.join(self.out_dir, base + "_textures")
        os.makedirs(folder, exist_ok=True)
        out = {}
        used = set()
        for t in todo.values():
            stem, ext = os.path.splitext(os.path.basename(t.path))
            if ext.lower() not in (".png", ".jpg", ".jpeg", ".bmp", ".tga", ".tif", ".tiff", ".rgb", ".rgba", ".sgi"):
                ext = ".png"
            name = stem + ext
            k = 1
            while name.lower() in used:
                name = f"{stem}_{k}{ext}"
                k += 1
            used.add(name.lower())
            dst = os.path.join(folder, name)
            try:
                with Image.open(t.path) as im:
                    im = im.resize(tuple(t.resize), Image.LANCZOS)
                    if ext.lower() in (".jpg", ".jpeg") and im.mode not in ("RGB", "L"):
                        im = im.convert("RGB")
                    im.save(dst)
            except Exception:
                continue
            out[id(t)] = os.path.relpath(dst, self.out_dir).replace("\\", "/")
        return out


# ====================================================================================== osgt

class OsgtWriter(_Base):
    def __init__(self, scene, out_path):
        super().__init__(scene, out_path)
        self.w = Writer("osgt")
        self.dom = scene.doc is not None and scene.doc.dialect == "osgt"
        self.regen_all_geometry = self.dom and (scene.doc.version or 0) < 147
        self.ss_objs = {}      # id(StateSet) -> generated Obj (for statesets without a source)

    def new_id(self):
        i = self.w.next_id
        self.w.next_id += 1
        return i

    def header(self):
        lines = ["#Ascii Scene ", f"#Version {OSGT_VERSION} ", GENERATOR + " "]
        if self.dom:
            lines += [h + " " for h in self.scene.doc.header if h.startswith("#CustomDomain")]
        return "\n".join(lines) + "\n\n"

    def write(self, sink=None):
        """The whole file as a string, or streamed into ``sink`` (a text file) when given."""
        self.fix_texture_names()
        head = self.header()
        if sink is not None:
            sink.write(head)
            self.w.sink = sink
        root = self.scene.root
        if root.extra.get("virtual_root"):
            for c in root.children:
                self.node(c, 0)
        else:
            self.node(root, 0)
        if sink is not None:
            self.w.flush()
            return None
        return head + self.w.text()

    # ---------------------------------------------------------------- nodes
    def node(self, n, depth):
        src = n.src if (self.dom and isinstance(n.src, Obj)) else None
        if src is not None:
            obj = src
        else:
            obj = n.extra.get("_gen_obj")
            if obj is None:
                cls = CLASS_FOR_KIND.get(n.kind, "osg::Group")
                if n.kind == M.TRANSFORM and n.matrix is None:
                    cls = "osg::Group"
                if n.kind not in CLASS_FOR_KIND and n.kind == M.EXTERNAL:
                    cls = "osg::Group"
                obj = Obj(cls, None, [], "osgt")
                n.extra["_gen_obj"] = obj  # keeps sharing of generated nodes
        body = (lambda d: self.patched_body(n, obj, d)) if src is not None else (lambda d: self.generated_body(n, obj, d))
        self.w.write_obj([obj.cls], obj, depth, body=body)

    def patched_body(self, n, obj, depth):
        regen_geo = n.kind == M.GEOMETRY and n.geom is not None and ("geometry" in n.dirty or self.regen_all_geometry)
        children_done = False
        ss_done = False
        child_key = "Drawables" if obj.get("Drawables") is not None else "Children"
        cls = obj.cls.split("::")[-1]
        stmts = list(obj.stmts)
        has_children_stmt = any(s.key in ("Children", "Drawables") for s in stmts)
        has_ss_stmt = any(s.key == "StateSet" for s in stmts)
        for i, s in enumerate(stmts):
            k = s.key
            if not ss_done and not has_ss_stmt and n.stateset is not None and k not in NODE_HEAD_KEYS - {"StateSet"}:
                self.stateset_stmt(n.stateset, depth)
                ss_done = True
            if not has_children_stmt and not children_done and n.children and n.kind != M.GEOMETRY and \
                    self._children_insert_here(cls, stmts, i):
                self.children_stmt(n, child_key, depth)
                children_done = True
            if k in ("Children", "Drawables"):
                if not children_done:
                    self.children_stmt(n, child_key, depth)
                    children_done = True
                continue
            if k == "StateSet":
                if n.stateset is not None:
                    self.stateset_stmt(n.stateset, depth)
                ss_done = True
                continue
            if k == "Name" and "name" in n.dirty:
                if n.name:
                    self.w.line(depth, f"Name {fmt_token(Q(n.name))} ")
                continue
            if k == "NodeMask" and "node_mask" in n.dirty:
                self.w.line(depth, f"NodeMask 0x{n.node_mask:x} ")
                continue
            if k == "Matrix" and n.matrix is not None and "matrix" in n.dirty:
                self.matrix_stmt(n.matrix, depth)
                continue
            if k == "RangeList" and n.ranges is not None and "ranges" in n.dirty:
                self.ranges_stmt(n.ranges, depth)
                continue
            if k == "ValueList" and n.switch_values is not None and "children" in n.dirty:
                self.values_stmt(n.switch_values, depth)
                continue
            if regen_geo and k in GEOMETRY_KEYS_OSGT:
                continue
            if (self.relocate or n.extra.get("files_out")) and k in ("FileNames", "RangeDataList") \
                    and isinstance(s.block, list):
                self.w.line(depth, " ".join(fmt_token(t) for t in s.toks) + " {")
                for sub in s.block:
                    self.w.line(depth + 1, " ".join(fmt_token(Q(self.file_ref(n, str(t)))) if str(t) else '""'
                                                    for t in sub.toks) + " ")
                self.w.line(depth, "}")
                continue
            self.w.write_stmt(s, depth)
        if not ss_done and n.stateset is not None:
            self.stateset_stmt(n.stateset, depth)
        if not children_done and n.children and n.kind != M.GEOMETRY:
            self.children_stmt(n, child_key, depth)
        if regen_geo:
            self.geometry_stmts(n.geom, depth)

    @staticmethod
    def _children_insert_here(cls, stmts, i):
        """Where a missing Children list goes: after the node header keys (before class specific ones);
        PagedLOD keeps its Children last, ProxyNode right after FileNames."""
        k = stmts[i].key
        if cls == "PagedLOD":
            return False
        if cls == "ProxyNode":
            return i > 0 and stmts[i - 1].key == "FileNames"
        return k not in NODE_HEAD_KEYS

    def generated_body(self, n, obj, depth):
        if n.name:
            self.w.line(depth, f"Name {fmt_token(Q(n.name))} ")
        if n.node_mask != 0xFFFFFFFF:
            self.w.line(depth, f"NodeMask 0x{n.node_mask:x} ")
        if n.stateset is not None:
            self.stateset_stmt(n.stateset, depth)
        cls = obj.cls.split("::")[-1]
        if n.kind == M.GEOMETRY:
            if n.geom is not None:
                self.geometry_stmts(n.geom, depth)
            return
        kids = n.children
        if cls == "Geode":
            if kids:
                self.w.line(depth, f"Drawables {len(kids)} {{")
                for c in kids:
                    self.node(c, depth + 1)
                self.w.line(depth, "}")
            return
        if cls == "ProxyNode":
            files = [self.file_ref(n, f) for f in n.files]
            self.w.line(depth, f"FileNames {len(files)} {{")
            for f in files:
                self.w.line(depth + 1, fmt_token(Q(f)) + " ")
            self.w.line(depth, "}")
            if kids:
                self.children_stmt(n, "Children", depth)
            return
        if kids:
            self.children_stmt(n, "Children", depth)
        if cls == "MatrixTransform":
            self.matrix_stmt(n.matrix if n.matrix is not None else np.eye(4), depth)
        elif cls == "LOD":
            if n.center is not None and len(n.center) >= 4:
                self.w.line(depth, "CenterMode USER_DEFINED_CENTER ")
                self.w.line(depth, "UserCenter " + " ".join(_num(v) for v in n.center[:4]) + " ")
            self.w.line(depth, f"RangeMode {n.range_mode} ")
            self.ranges_stmt(n.ranges or [(0.0, 1e30)] * len(kids), depth)
        elif cls == "Switch":
            vals = n.switch_values if n.switch_values is not None else [True] * len(kids)
            self.values_stmt(vals, depth)

    def children_stmt(self, n, key, depth):
        kids = n.children
        if not kids:
            return
        self.w.line(depth, f"{key} {len(kids)} {{")
        for c in kids:
            self.node(c, depth + 1)
        self.w.line(depth, "}")

    def matrix_stmt(self, Mx, depth):
        self.w.line(depth, "Matrix {")
        vals = M.np_to_osg_matrix(Mx)
        for ln in format_numbers(vals, 4, double=True):
            self.w.line(depth + 1, ln + " ")
        self.w.line(depth, "}")

    def ranges_stmt(self, ranges, depth):
        self.w.line(depth, f"RangeList {len(ranges)} {{")
        for a, b in ranges:
            self.w.line(depth + 1, f"{_num(a)} {_num(b)} ")
        self.w.line(depth, "}")

    def values_stmt(self, vals, depth):
        self.w.line(depth, f"ValueList {len(vals)} {{")
        self.w.line(depth + 1, " ".join("TRUE" if v else "FALSE" for v in vals) + " ")
        self.w.line(depth, "}")

    # ---------------------------------------------------------------- state
    def stateset_stmt(self, ss, depth):
        self.w.line(depth, "StateSet TRUE {")
        if self.dom and isinstance(ss.src, Obj):
            self.w.write_obj([ss.src.cls], ss.src, depth + 1)
        else:
            obj = self.ss_objs.get(id(ss))
            if obj is None:
                obj = Obj("osg::StateSet", None, [], "osgt")
                self.ss_objs[id(ss)] = obj
            self.w.write_obj(["osg::StateSet"], obj, depth + 1, body=lambda d: self.generated_stateset(ss, d))
        self.w.line(depth, "}")

    def generated_stateset(self, ss, depth):
        line = self.w.line
        line(depth, "DataVariance STATIC ")
        line(depth, "AttributeList 1 {")
        line(depth + 1, "osg::Material {")
        line(depth + 2, f"UniqueID {self.new_id()} ")
        if ss.material_name:
            line(depth + 2, f"Name {fmt_token(Q(ss.material_name))} ")
        line(depth + 2, "ColorMode AMBIENT_AND_DIFFUSE ")
        d = ss.diffuse or (0.8, 0.8, 0.8, 1.0)
        line(depth + 2, "Ambient TRUE Front 0.2 0.2 0.2 1 Back 0.2 0.2 0.2 1 ")
        dd = " ".join(_num(v) for v in (list(d) + [1.0])[:4])
        line(depth + 2, f"Diffuse TRUE Front {dd} Back {dd} ")
        line(depth + 2, "Specular TRUE Front 0 0 0 1 Back 0 0 0 1 ")
        line(depth + 2, "Emission TRUE Front 0 0 0 1 Back 0 0 0 1 ")
        line(depth + 2, "Shininess TRUE Front 0 Back 0 ")
        line(depth + 1, "}")
        line(depth + 1, "Value OFF ")
        line(depth, "}")
        units = {}
        for t in ss.textures:
            units.setdefault(t.unit, t)
        if units:
            n_units = max(units) + 1
            line(depth, f"TextureModeList {n_units} {{")
            for u in range(n_units):
                if u in units:
                    line(depth + 1, "Data 1 {")
                    line(depth + 2, "GL_TEXTURE_2D ON ")
                    line(depth + 1, "}")
                else:
                    line(depth + 1, "Data 0 {")
                    line(depth + 1, "}")
            line(depth, "}")
            line(depth, f"TextureAttributeList {n_units} {{")
            for u in range(n_units):
                t = units.get(u)
                if t is None:
                    line(depth + 1, "Data 0 {")
                    line(depth + 1, "}")
                    continue
                line(depth + 1, "Data 1 {")
                self.generated_texture(t, depth + 2)
                line(depth + 2, "Value OFF ")
                line(depth + 1, "}")
            line(depth, "}")
        if ss.transparent:
            line(depth, "ModeList 1 {")
            line(depth + 1, "GL_BLEND ON ")
            line(depth, "}")
            line(depth, "RenderingHint 2 ")
            line(depth, "RenderBinMode USE_RENDERBIN_DETAILS ")
            line(depth, "BinNumber 10 ")
            line(depth, 'BinName "DepthSortedBin" ')

    def generated_texture(self, t, depth):
        key = ("tex", id(t))
        obj = self.ss_objs.get(key)
        if obj is None:
            obj = Obj("osg::Texture2D", None, [], "osgt")
            self.ss_objs[key] = obj
        img_key = ("img", id(t))
        img = self.ss_objs.get(img_key)
        if img is None:
            img = Obj("osg::Image", None, [], "image")
            self.ss_objs[img_key] = img

        def body(d):
            for k in ("WRAP_S", "WRAP_T", "WRAP_R"):
                self.w.line(d, f"{k} REPEAT ")
            self.w.line(d, "MIN_FILTER LINEAR_MIPMAP_LINEAR ")
            self.w.line(d, "MAG_FILTER LINEAR ")

            def img_body(d2):
                self.w.line(d2, f"FileName {fmt_token(Q(self.rel_file(t.file, t.path) if self.scene.doc is None else t.file))} ")
                self.w.line(d2, "WriteHint 0 2 ")
                self.w.line(d2, "DataVariance STATIC ")
            self.w.write_obj(["Image", "TRUE"], img, d, body=img_body)
        self.w.write_obj(["osg::Texture2D"], obj, depth, body=body)

    # ---------------------------------------------------------------- geometry
    def geometry_stmts(self, g, depth):
        line = self.w.line
        prims = g.prims
        if prims:
            line(depth, f"PrimitiveSetList {len(prims)} {{")
            for p in prims:
                self.primitive(p, depth + 1)
            line(depth, "}")
        if g.vertices is not None:
            self.array_stmt("VertexArray", g.vertices, depth)
        if g.normals is not None:
            self.array_stmt("NormalArray", g.normals, depth)
        if g.colors is not None:
            self.array_stmt("ColorArray", g.colors, depth)
        for name in ("SecondaryColorArray", "FogCoordArray"):
            if name in g.extra:
                self.array_stmt(name, g.extra[name], depth)
        if g.texcoords:
            n_units = max(g.texcoords) + 1
            line(depth, f"TexCoordArrayList {n_units} {{")
            for u in range(n_units):
                a = g.texcoords.get(u)
                if a is None:
                    line(depth + 1, "NULL")
                else:
                    self.array_obj(a, depth + 1)
            line(depth, "}")
        attribs = {int(k[len("VertexAttrib"):]): a for k, a in g.extra.items() if k.startswith("VertexAttrib")}
        if attribs:
            n_attr = max(attribs) + 1
            line(depth, f"VertexAttribArrayList {n_attr} {{")
            for i in range(n_attr):
                a = attribs.get(i)
                if a is None:
                    line(depth + 1, "NULL")
                else:
                    self.array_obj(a, depth + 1)
            line(depth, "}")

    def primitive(self, p, depth):
        line = self.w.line
        if p.kind == "arrays":
            cls = "DrawArrays"
        elif p.kind == "lengths":
            cls = "DrawArrayLengths"
        else:
            cls = p.index_type or "DrawElementsUInt"
            if cls == "DrawElementsUByte" and len(p.indices) and int(np.max(p.indices)) > 255:
                cls = "DrawElementsUShort"
            if cls == "DrawElementsUShort" and len(p.indices) and int(np.max(p.indices)) > 65535:
                cls = "DrawElementsUInt"
        line(depth, f"osg::{cls} {{")
        line(depth + 1, f"UniqueID {self.new_id()} ")
        if p.instances:
            line(depth + 1, f"NumInstances {p.instances} ")
        line(depth + 1, f"Mode {p.mode} ")
        if p.kind == "arrays":
            if p.first:
                line(depth + 1, f"First {p.first} ")
            line(depth + 1, f"Count {p.count} ")
        elif p.kind == "lengths":
            if p.first:
                line(depth + 1, f"First {p.first} ")
            self.vector(np.asarray(p.lengths), 4, depth + 1)
        else:
            self.vector(np.asarray(p.indices), 4, depth + 1)
        line(depth, "}")

    def array_stmt(self, key, a, depth):
        self.w.line(depth, f"{key} TRUE {{")
        self.array_obj(a, depth + 1)
        self.w.line(depth, "}")

    def array_obj(self, a, depth):
        line = self.w.line
        line(depth, f"osg::{a.type} {{")
        line(depth + 1, f"UniqueID {self.new_id()} ")
        line(depth + 1, f"Binding {BINDING_OSGT.get(a.binding, 'BIND_PER_VERTEX')} ")
        if a.normalize:
            line(depth + 1, "Normalize TRUE ")
        comps = M.ARRAY_TYPES.get(a.type, (1,))[0]
        self.vector(np.asarray(a.data), comps if comps > 1 else 4, depth + 1, double=a.type in DOUBLE_TYPES)
        line(depth, "}")

    def vector(self, data, per_line, depth, double=False):
        data = np.asarray(data)
        self.w.line(depth, f"vector {len(data)} {{")
        for ln in format_numbers(data.reshape(-1), per_line, double=double):
            self.w.line(depth + 1, ln + " ")
        self.w.line(depth, "}")


def _num(v):
    v = float(v)
    if v == int(v) and abs(v) < 1e15:
        return str(int(v))
    return repr(v) if abs(v) < 1e16 else f"{v:g}"


# ====================================================================================== legacy .osg

class OsgWriter(_Base):
    def __init__(self, scene, out_path):
        super().__init__(scene, out_path)
        self.w = Writer("osg")
        self.generated = {}

    def write(self, sink=None):
        self.fix_texture_names()
        # dry run: count how often every object is written, so UniqueID / Use are only
        # emitted for objects that really are shared in the output
        self.w = Writer("osg", NullSink())
        self._emit()
        calls = self.w.calls
        self.w = Writer("osg", sink)
        self.w.use_count = calls
        self._emit()
        if sink is not None:
            self.w.flush()
            return None
        return self.w.text()

    def _emit(self):
        root = self.scene.root
        if root.extra.get("virtual_root"):
            for c in root.children:
                self.node(c, 0)
        else:
            self.node(root, 0)

    def _obj(self, n):
        if isinstance(n.src, Obj):
            return n.src
        o = self.generated.get(id(n))
        if o is None:
            cls = {M.GROUP: "Group", M.GEODE: "Geode", M.TRANSFORM: "MatrixTransform", M.LOD: "LOD",
                   M.SWITCH: "Switch", M.GEOMETRY: "Geometry", M.PROXY: "ProxyNode"}.get(n.kind, "Group")
            if n.kind == M.TRANSFORM and n.matrix is None:
                cls = "Group"
            o = Obj(cls, None, [], "osg")
            self.generated[id(n)] = o
        return o

    def node(self, n, depth):
        obj = self._obj(n)
        if isinstance(n.src, Obj):
            body = lambda d: self.patched_body(n, obj, d)  # noqa: E731
        else:
            body = lambda d: self.generated_body(n, obj, d)  # noqa: E731
        self.w.write_obj([obj.cls], obj, depth, body=body)

    def patched_body(self, n, obj, depth):
        regen_geo = n.kind == M.GEOMETRY and n.geom is not None and "geometry" in n.dirty
        child_objs = {id(self._obj(c)) for c in n.children}
        old_children = set()
        for s in obj.stmts:
            if isinstance(s.block, Obj) and s.block.cls != "StateSet" and not s.block.cls.endswith("Callback") \
                    and s.block.cls not in ("UserData", "Descriptions", "ValueList", "Matrix"):
                old_children.add(id(s.block))
        count_key = "num_drawables" if n.kind == M.GEODE else "num_children"
        children_done = False
        ss_done = False
        for s in obj.stmts:
            k = s.key
            b = s.block
            if isinstance(b, Obj) and b.cls == "StateSet":
                if n.stateset is not None and not ss_done:
                    self.stateset(n.stateset, depth)
                ss_done = True
                continue
            if k in ("num_children", "num_drawables"):
                if not children_done:
                    self.children(n, count_key, depth)
                    children_done = True
                continue
            if isinstance(b, Obj) and (id(b) in old_children or id(b) in child_objs):
                if not children_done:
                    self.children(n, count_key, depth)
                    children_done = True
                continue
            if k == "name" and "name" in n.dirty:
                if n.name:
                    self.w.line(depth, f"name {fmt_token(Q(n.name))}")
                continue
            if k == "nodeMask" and "node_mask" in n.dirty:
                self.w.line(depth, f"nodeMask 0x{n.node_mask:x}")
                continue
            if k == "Matrix" and n.matrix is not None and "matrix" in n.dirty:
                self.matrix(n.matrix, depth)
                continue
            if k == "RangeList" and n.ranges is not None and "ranges" in n.dirty:
                self.ranges(n.ranges, depth)
                continue
            if regen_geo and k in GEOMETRY_KEYS_OSG:
                continue
            if k == "FileNameList" and (self.relocate or n.extra.get("files_out")) and isinstance(b, list):
                self.w.line(depth, " ".join(fmt_token(t) for t in s.toks) + " {")
                for sub in b:
                    self.w.line(depth + 1, " ".join(fmt_token(Q(self.file_ref(n, str(t)))) if str(t) else '""' for t in sub.toks))
                self.w.line(depth, "}")
                continue
            self.w.write_stmt(s, depth)
        if not ss_done and n.stateset is not None:
            self.stateset(n.stateset, depth)
        if not children_done and n.children and n.kind != M.GEOMETRY:
            self.children(n, count_key, depth)
        if regen_geo:
            self.geometry(n.geom, depth)

    def generated_body(self, n, obj, depth):
        line = self.w.line
        if n.name:
            line(depth, f"name {fmt_token(Q(n.name))}")
        if n.node_mask != 0xFFFFFFFF:
            line(depth, f"nodeMask 0x{n.node_mask:x}")
        if n.stateset is not None:
            self.stateset(n.stateset, depth)
        if n.kind == M.GEOMETRY:
            if n.geom is not None:
                self.geometry(n.geom, depth)
            return
        if obj.cls == "MatrixTransform":
            line(depth, "referenceFrame RELATIVE")
            self.matrix(n.matrix if n.matrix is not None else np.eye(4), depth)
        elif obj.cls == "LOD":
            line(depth, "CenterMode USE_BOUNDING_SPHERE_CENTER")
            line(depth, f"RangeMode {n.range_mode}")
            self.ranges(n.ranges or [(0.0, 1e30)] * len(n.children), depth)
        elif obj.cls == "Switch":
            vals = n.switch_values if n.switch_values is not None else [True] * len(n.children)
            line(depth, "NewChildDefaultValue 1")
            line(depth, "ValueList {")
            for v in vals:
                line(depth + 1, "1" if v else "0")
            line(depth, "}")
        self.children(n, "num_drawables" if obj.cls == "Geode" else "num_children", depth)

    def children(self, n, key, depth):
        self.w.line(depth, f"{key} {len(n.children)}")
        for c in n.children:
            self.node(c, depth)

    def stateset(self, ss, depth):
        if isinstance(ss.src, Obj):
            self.w.write_obj([ss.src.cls], ss.src, depth)

    def matrix(self, Mx, depth):
        self.w.line(depth, "Matrix {")
        for ln in format_numbers(M.np_to_osg_matrix(Mx), 4, double=True):
            self.w.line(depth + 1, ln)
        self.w.line(depth, "}")

    def ranges(self, ranges, depth):
        self.w.line(depth, f"RangeList {len(ranges)} {{")
        for a, b in ranges:
            self.w.line(depth + 1, f"{_num(a)} {_num(b)}")
        self.w.line(depth, "}")

    def geometry(self, g, depth):
        line = self.w.line
        line(depth, f"PrimitiveSets {len(g.prims)}")
        line(depth, "{")
        for p in g.prims:
            if p.kind == "arrays":
                line(depth + 1, f"DrawArrays {p.mode} {p.first} {p.count}")
            elif p.kind == "lengths":
                line(depth + 1, f"DrawArrayLengths {p.mode} {p.first} {len(p.lengths)}")
                self._block(np.asarray(p.lengths), 10, depth + 1)
            else:
                cls = p.index_type or "DrawElementsUInt"
                mx = int(np.max(p.indices)) if len(p.indices) else 0
                if cls == "DrawElementsUByte" and mx > 255:
                    cls = "DrawElementsUShort"
                if cls == "DrawElementsUShort" and mx > 65535:
                    cls = "DrawElementsUInt"
                line(depth + 1, f"{cls} {p.mode} {len(p.indices)}")
                self._block(np.asarray(p.indices), 10, depth + 1)
        line(depth, "}")
        if g.vertices is not None:
            self._array("VertexArray", g.vertices, depth)
        if g.normals is not None:
            line(depth, f"NormalBinding {BINDING_OSG.get(g.normals.binding, 'PER_VERTEX')}")
            self._array("NormalArray", g.normals, depth)
        if g.colors is not None:
            line(depth, f"ColorBinding {BINDING_OSG.get(g.colors.binding, 'PER_VERTEX')}")
            self._array("ColorArray", g.colors, depth)
        for name in ("SecondaryColorArray", "FogCoordArray"):
            a = g.extra.get(name)
            if a is not None:
                line(depth, f"{name.replace('Array', 'Binding')} {BINDING_OSG.get(a.binding, 'PER_VERTEX')}")
                self._array(name, a, depth)
        for u, a in sorted(g.texcoords.items()):
            self._array(f"TexCoordArray {u}", a, depth)

    def _array(self, key, a, depth):
        comps = M.ARRAY_TYPES.get(a.type, (1,))[0]
        self.w.line(depth, f"{key} {a.type} {len(a.data)}")
        self._block(np.asarray(a.data), comps if comps > 1 else 10, depth, double=a.type in DOUBLE_TYPES)

    def _block(self, data, per_line, depth, double=False):
        self.w.line(depth, "{")
        for ln in format_numbers(data.reshape(-1), per_line, double=double):
            self.w.line(depth + 1, ln)
        self.w.line(depth, "}")


def write(scene, path):
    """Write ``scene`` to ``path`` (.osgt or .osg)."""
    ext = os.path.splitext(path)[1].lower()
    if ext == ".osg":
        if scene.doc is None or scene.doc.dialect != "osg":
            raise ValueError("legacy .osg output is only written for scenes read from .osg; save as .osgt "
                             "(or let osgconv convert it)")
        writer = OsgWriter(scene, path)
    else:
        if scene.doc is not None and scene.doc.dialect == "osg":
            raise ValueError("this scene was read from a legacy .osg file; save it as .osg "
                             "(or let osgconv convert it)")
        writer = OsgtWriter(scene, path)
    tmp = path + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8", newline="\n", buffering=1 << 20) as fh:
            writer.write(fh)          # streamed: a 1 GB scene is never held as one big string
        os.replace(tmp, path)
    except BaseException:
        try:
            os.remove(tmp)
        except OSError:
            pass
        raise
    return path


def native_ext(scene):
    """The format this scene can be written to without osgconv."""
    return ".osg" if scene.doc is not None and scene.doc.dialect == "osg" else ".osgt"
