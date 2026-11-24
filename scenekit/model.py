"""Format independent scene graph used by the inspector, the analysis and the optimizer.

Sharing is expressed by identity: a ``Node`` (or ``StateSet`` / ``Texture``) that is
referenced from several parents is the same Python object in all places.
Matrices are 4x4 numpy arrays in column-vector convention (``p' = M @ p``).
"""

import numpy as np

# node kinds
GROUP, TRANSFORM, LOD, PAGED_LOD, SWITCH, PROXY, GEODE, GEOMETRY, OTHER = (
    "Group", "Transform", "LOD", "PagedLOD", "Switch", "Proxy", "Geode", "Geometry", "Other")
EXTERNAL = "External"   # an external file shown as a child (FLT external reference, ProxyNode, PagedLOD file)

TRIANGLE_MODES = {"TRIANGLES", "TRIANGLE_STRIP", "TRIANGLE_FAN", "QUADS", "QUAD_STRIP", "POLYGON"}
LINE_MODES = {"LINES", "LINE_STRIP", "LINE_LOOP"}

# bytes per component of the array types we know
ARRAY_TYPES = {
    # name: (components, numpy dtype)
    "Vec2Array": (2, np.float32), "Vec3Array": (3, np.float32), "Vec4Array": (4, np.float32),
    "Vec2dArray": (2, np.float64), "Vec3dArray": (3, np.float64), "Vec4dArray": (4, np.float64),
    "FloatArray": (1, np.float32), "DoubleArray": (1, np.float64),
    "Vec4ubArray": (4, np.uint8), "Vec3ubArray": (3, np.uint8), "Vec2ubArray": (2, np.uint8),
    "Vec4bArray": (4, np.int8), "Vec3bArray": (3, np.int8), "Vec2bArray": (2, np.int8),
    "Vec4usArray": (4, np.uint16), "Vec3usArray": (3, np.uint16), "Vec2usArray": (2, np.uint16),
    "Vec4sArray": (4, np.int16), "Vec3sArray": (3, np.int16), "Vec2sArray": (2, np.int16),
    "Vec4uiArray": (4, np.uint32), "Vec3uiArray": (3, np.uint32), "Vec2uiArray": (2, np.uint32),
    "Vec4iArray": (4, np.int32), "Vec3iArray": (3, np.int32), "Vec2iArray": (2, np.int32),
    "UByteArray": (1, np.uint8), "ByteArray": (1, np.int8), "UShortArray": (1, np.uint16),
    "ShortArray": (1, np.int16), "UIntArray": (1, np.uint32), "IntArray": (1, np.int32),
}
INDEX_TYPES = {"DrawElementsUByte": 1, "DrawElementsUShort": 2, "DrawElementsUInt": 4}


class Array:
    """A vertex attribute array. ``binding`` is 'vertex', 'overall', 'primitive_set', 'primitive' or 'off'."""
    __slots__ = ("data", "type", "binding", "normalize", "src")

    def __init__(self, data, type_name="Vec3Array", binding="vertex", normalize=False, src=None):
        self.data = data
        self.type = type_name
        self.binding = binding
        self.normalize = normalize
        self.src = src

    @property
    def nbytes(self):
        comps, dt = ARRAY_TYPES.get(self.type, (self.data.shape[1] if self.data.ndim > 1 else 1, np.float32))
        return int(len(self.data) * comps * np.dtype(dt).itemsize)

    def copy_with(self, data):
        return Array(data, self.type, self.binding, self.normalize)


class Primitive:
    """One primitive set. ``kind`` is 'arrays', 'lengths' or 'elements'."""
    __slots__ = ("mode", "kind", "first", "count", "lengths", "indices", "index_type", "instances")

    def __init__(self, mode, kind, first=0, count=0, lengths=None, indices=None, index_type=None, instances=0):
        self.mode = mode
        self.kind = kind
        self.first = int(first)
        self.count = int(count)
        self.lengths = lengths
        self.indices = indices
        self.index_type = index_type
        self.instances = int(instances)

    def vertex_indices(self):
        """Indices into the vertex arrays in drawing order."""
        if self.kind == "elements":
            return self.indices
        if self.kind == "lengths":
            return np.arange(self.first, self.first + int(np.sum(self.lengths)), dtype=np.int64)
        return np.arange(self.first, self.first + self.count, dtype=np.int64)

    @property
    def draw_calls(self):
        return len(self.lengths) if self.kind == "lengths" else 1

    @property
    def index_bytes(self):
        if self.kind == "elements":
            return len(self.indices) * INDEX_TYPES.get(self.index_type, 4)
        if self.kind == "lengths":
            return len(self.lengths) * 4
        return 0


def _strip_tris(idx):
    n = len(idx) - 2
    if n <= 0:
        return np.zeros((0, 3), np.int64)
    a = idx[:-2]
    b = idx[1:-1]
    c = idx[2:]
    odd = (np.arange(n) % 2) == 1
    t = np.stack([np.where(odd, b, a), np.where(odd, a, b), c], axis=1)
    return t


def _fan_tris(idx):
    n = len(idx) - 2
    if n <= 0:
        return np.zeros((0, 3), np.int64)
    return np.stack([np.full(n, idx[0]), idx[1:-1], idx[2:]], axis=1)


def _quad_tris(idx):
    q = idx[: len(idx) // 4 * 4].reshape(-1, 4)
    return np.concatenate([q[:, [0, 1, 2]], q[:, [0, 2, 3]]]) if len(q) else np.zeros((0, 3), np.int64)


def _quad_strip_tris(idx):
    n = (len(idx) - 2) // 2
    if n <= 0:
        return np.zeros((0, 3), np.int64)
    i = np.arange(n) * 2
    a, b, c, d = idx[i], idx[i + 1], idx[i + 3], idx[i + 2]
    return np.concatenate([np.stack([a, b, c], 1), np.stack([a, c, d], 1)])


def mode_triangles(mode, idx):
    idx = np.asarray(idx, dtype=np.int64)
    if mode == "TRIANGLES":
        return idx[: len(idx) // 3 * 3].reshape(-1, 3)
    if mode == "TRIANGLE_STRIP":
        return _strip_tris(idx)
    if mode in ("TRIANGLE_FAN", "POLYGON"):
        return _fan_tris(idx)
    if mode == "QUADS":
        return _quad_tris(idx)
    if mode == "QUAD_STRIP":
        return _quad_strip_tris(idx)
    return np.zeros((0, 3), np.int64)


def mode_lines(mode, idx):
    idx = np.asarray(idx, dtype=np.int64)
    if mode == "LINES":
        return idx[: len(idx) // 2 * 2].reshape(-1, 2)
    if mode == "LINE_STRIP" and len(idx) > 1:
        return np.stack([idx[:-1], idx[1:]], 1)
    if mode == "LINE_LOOP" and len(idx) > 1:
        return np.stack([idx, np.roll(idx, -1)], 1)
    return np.zeros((0, 2), np.int64)


class GeomData:
    """Vertex arrays + primitive sets of one osg::Geometry (or an equivalent bucket)."""

    def __init__(self):
        self.vertices = None        # Array
        self.normals = None         # Array
        self.colors = None          # Array
        self.texcoords = {}         # unit -> Array
        self.extra = {}             # name -> Array (secondary colours, fog coords, vertex attribs)
        self.prims = []             # [Primitive]
        self.notes = []             # reader notes (e.g. deprecated index arrays expanded)
        self.optimizable = True     # False when bindings make re-indexing unsafe

    # ---- derived data --------------------------------------------------------
    @property
    def num_vertices(self):
        return 0 if self.vertices is None else len(self.vertices.data)

    def arrays(self):
        """(name, Array) for all arrays."""
        out = []
        for name in ("vertices", "normals", "colors"):
            a = getattr(self, name)
            if a is not None:
                out.append((name, a))
        for u, a in sorted(self.texcoords.items()):
            out.append((f"texcoord{u}", a))
        out.extend(sorted(self.extra.items()))
        return out

    def per_vertex_arrays(self):
        return [(n, a) for n, a in self.arrays() if a.binding == "vertex" and len(a.data) == self.num_vertices]

    def triangles(self):
        tris = [mode_triangles(p.mode, idx) for p, idx in self._prim_runs() if p.mode in TRIANGLE_MODES]
        tris = [t for t in tris if len(t)]
        return np.concatenate(tris) if tris else np.zeros((0, 3), np.int64)

    def lines(self):
        ls = [mode_lines(p.mode, idx) for p, idx in self._prim_runs() if p.mode in LINE_MODES]
        ls = [x for x in ls if len(x)]
        return np.concatenate(ls) if ls else np.zeros((0, 2), np.int64)

    def points(self):
        pts = [idx for p, idx in self._prim_runs() if p.mode == "POINTS"]
        return np.concatenate(pts) if pts else np.zeros(0, np.int64)

    def _prim_runs(self):
        """(primitive, index run) - DrawArrayLengths yields one run per length."""
        for p in self.prims:
            if p.kind == "lengths":
                start = p.first
                for ln in np.asarray(p.lengths, dtype=np.int64).tolist():
                    yield p, np.arange(start, start + ln, dtype=np.int64)
                    start += ln
            else:
                yield p, p.vertex_indices()

    def counts(self):
        """Cheap counts without building index arrays."""
        tris = lines = points = 0
        for p in self.prims:
            if p.kind == "lengths":
                runs = np.asarray(p.lengths, dtype=np.int64)
            else:
                runs = np.array([len(p.indices) if p.kind == "elements" else p.count], dtype=np.int64)
            m = p.mode
            if m == "TRIANGLES":
                c = int(np.sum(runs // 3))
            elif m in ("TRIANGLE_STRIP", "TRIANGLE_FAN", "POLYGON"):
                c = int(np.sum(np.maximum(runs - 2, 0)))
            elif m == "QUADS":
                c = int(np.sum(runs // 4)) * 2
            elif m == "QUAD_STRIP":
                c = int(np.sum(np.maximum(runs - 2, 0) // 2)) * 2
            else:
                c = 0
            tris += c
            if m == "LINES":
                lines += int(np.sum(runs // 2))
            elif m == "LINE_STRIP":
                lines += int(np.sum(np.maximum(runs - 1, 0)))
            elif m == "LINE_LOOP":
                lines += int(np.sum(runs))
            elif m == "POINTS":
                points += int(np.sum(runs))
        return tris, lines, points

    @property
    def draw_calls(self):
        return sum(p.draw_calls for p in self.prims)

    @property
    def vertex_bytes(self):
        return sum(a.nbytes for _, a in self.arrays())

    @property
    def index_bytes(self):
        return sum(p.index_bytes for p in self.prims)

    def bbox(self):
        if self.vertices is None or not len(self.vertices.data):
            return None
        v = np.asarray(self.vertices.data, dtype=np.float64)
        v = v[np.all(np.isfinite(v), axis=1)]
        if not len(v):
            return None
        return v.min(axis=0), v.max(axis=0)

    def content_key(self):
        """Hash of the geometry content (arrays + primitives) for duplicate detection."""
        import hashlib
        h = hashlib.blake2b(digest_size=16)
        for name, a in self.arrays():
            h.update(name.encode())
            h.update(a.type.encode())
            h.update(a.binding.encode())
            h.update(np.ascontiguousarray(a.data).tobytes())
        for p in self.prims:
            h.update(f"{p.mode}|{p.kind}|{p.first}|{p.count}|{p.instances}".encode())
            if p.lengths is not None:
                h.update(np.asarray(p.lengths, dtype=np.int64).tobytes())
            if p.indices is not None:
                h.update(np.asarray(p.indices, dtype=np.int64).tobytes())
        return h.hexdigest()


class Texture:
    __slots__ = ("file", "path", "exists", "width", "height", "channels", "file_bytes", "embedded",
                 "unit", "src", "image_src", "fmt", "bpp", "resize")

    def __init__(self, file=""):
        self.file = file            # as written in the scene
        self.path = None            # resolved path on disk
        self.exists = False
        self.width = self.height = 0
        self.channels = 4
        self.file_bytes = 0
        self.embedded = False       # image data stored inside the scene file
        self.unit = 0
        self.src = None             # format specific handle (DOM object of the texture)
        self.image_src = None       # DOM object of the image
        self.fmt = ""
        self.bpp = None             # bytes per pixel on the GPU when known (compressed DDS/KTX)
        self.resize = None          # (w, h) when the optimizer asked for a smaller copy

    @property
    def gpu_bytes(self):
        """Uncompressed GPU memory incl. a full mip chain (4/3); RGB is padded to RGBA by most drivers."""
        if not self.width or not self.height:
            return 0
        bpp = self.bpp if self.bpp else (4 if self.channels >= 3 else max(self.channels, 1))
        return int(self.width * self.height * bpp * 4 / 3)

    @property
    def label(self):
        return self.file or ("<embedded>" if self.embedded else "<no file>")


class StateSet:
    __slots__ = ("textures", "material_name", "diffuse", "modes", "transparent", "src", "summary")

    def __init__(self):
        self.textures = []          # [Texture] (unit stored on the texture)
        self.material_name = ""
        self.diffuse = None
        self.modes = {}
        self.transparent = False
        self.src = None
        self.summary = []           # short descriptions of the attributes, for display

    def key(self):
        return (tuple(id(t) for t in self.textures), self.material_name, self.diffuse,
                tuple(sorted(self.modes.items())), self.transparent)


class Node:
    __slots__ = ("kind", "cls", "name", "children", "matrix", "stateset", "geom", "ranges", "range_mode",
                 "switch_values", "files", "node_mask", "src", "dirty", "notes", "external", "center",
                 "extra", "__weakref__")

    def __init__(self, kind, cls="", name=""):
        self.kind = kind
        self.cls = cls or kind
        self.name = name or ""
        self.children = []
        self.matrix = None          # local transform (4x4) for transforms
        self.stateset = None
        self.geom = None            # GeomData for GEOMETRY nodes
        self.ranges = None          # LOD: [(min, max)] per child
        self.range_mode = "DISTANCE_FROM_EYE_POINT"
        self.switch_values = None   # Switch: [bool] per child
        self.files = []             # external files (PagedLOD per child / ProxyNode / FLT xref)
        self.node_mask = 0xFFFFFFFF
        self.src = None             # format specific handle (DOM Obj, FLT record, ...)
        self.dirty = set()          # what changed: {'children', 'geometry', 'matrix', 'ranges', 'stateset'}
        self.notes = []
        self.external = None        # for EXTERNAL nodes: the file path
        self.center = None          # LOD user center (x, y, z, radius)
        self.extra = {}

    def __repr__(self):
        return f"<Node {self.cls} {self.name!r} ({len(self.children)} children)>"

    @property
    def label(self):
        return self.name or ""

    @property
    def is_drawable(self):
        return self.kind == GEOMETRY

    def mark(self, what):
        self.dirty.add(what)

    @property
    def externals(self):
        """Loaded external files (ProxyNode / PagedLOD / FLT external reference) - read only."""
        return self.extra.get("externals", [])

    def all_children(self):
        ext = self.extra.get("externals")
        return self.children + ext if ext else self.children


class Scene:
    def __init__(self, path, fmt):
        self.path = path
        self.fmt = fmt              # 'osgt', 'osg', 'flt', ...
        self.root = None
        self.doc = None             # osgtext.Document for OSG text formats
        self.version = None
        self.warnings = []
        self.info = {}              # format specific facts (units, revision, generator, ...)
        self.converted_from = None  # original file when loaded through osgconv
        self.externals = {}         # path -> Scene | None (loaded external references)
        self.editable = True        # False when the scene cannot be written back natively

    def iter_nodes(self, unique=True, externals=False):
        """Depth-first over all nodes (each shared node once when ``unique``).
        External files are only included with ``externals``."""
        seen = set()
        stack = [self.root] if self.root is not None else []
        while stack:
            n = stack.pop()
            if unique:
                if id(n) in seen:
                    continue
                seen.add(id(n))
            yield n
            stack.extend(reversed(n.all_children() if externals else n.children))

    def parents(self):
        """id(node) -> [parent nodes]."""
        par = {}
        for n in self.iter_nodes():
            for c in n.children:
                par.setdefault(id(c), []).append(n)
        return par


def compose(parent, local):
    if local is None:
        return parent
    return parent @ local


def transform_points(M, pts):
    pts = np.asarray(pts, dtype=np.float64)
    return pts @ M[:3, :3].T + M[:3, 3]


def osg_matrix_to_np(values16):
    """OSG writes matrices row-major for row vectors (translation in the last row)."""
    m = np.asarray(values16, dtype=np.float64).reshape(4, 4)
    return m.T.copy()


def np_to_osg_matrix(M):
    return np.asarray(M, dtype=np.float64).T.reshape(-1)


def quat_to_matrix(x, y, z, w):
    n = x * x + y * y + z * z + w * w
    if n < 1e-20:
        return np.eye(3)
    s = 2.0 / n
    return np.array([
        [1 - s * (y * y + z * z), s * (x * y - z * w), s * (x * z + y * w)],
        [s * (x * y + z * w), 1 - s * (x * x + z * z), s * (y * z - x * w)],
        [s * (x * z - y * w), s * (y * z + x * w), 1 - s * (x * x + y * y)],
    ])


def pat_matrix(position, attitude, scale, pivot):
    """PositionAttitudeTransform: M = T(position) * R(attitude) * S(scale) * T(-pivot)."""
    M = np.eye(4)
    R = quat_to_matrix(*attitude)
    M[:3, :3] = R @ np.diag(scale)
    M[:3, 3] = np.asarray(position, dtype=np.float64) - M[:3, :3] @ np.asarray(pivot, dtype=np.float64)
    return M
