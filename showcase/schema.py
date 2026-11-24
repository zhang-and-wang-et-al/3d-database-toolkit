"""Shared record schema: issue catalogue, status/tier rules and the text report.

Pure Python (no bpy, no third-party modules) so it can be used both by the
Blender worker and by the batch runner / GUI.

A record (``<model>.info.json``) looks like::

    {
      "schema_version": 1,
      "tool_version": "1.0.0",
      "id": "<sha256 of the file, or of the path when hashing is disabled>",
      "status": "ok" | "warning" | "broken" | "error",
      "issues": [{"code": ..., "severity": "info|warning|critical|error", "message": ...}],
      "file": {...}, "import": {...}, "scene": {...}, "geometry": {...},
      "bounds": {...}, "materials": {...}, "animation": {...},
      "orientation": {...}, "classification": {...}, "render": {...},
      "processed_at": "2026-01-01T12:00:00+00:00"
    }

``flatten()`` turns a record into one flat row (used for SQLite / CSV).
"""

import datetime
import json

SCHEMA_VERSION = 1

# Model formats the tool renders. Values are a human-readable format name.
# Each file is read by (first that works): a Blender importer (built-in or an
# installed add-on/extension), a built-in Python reader, or an external
# converter (assimp / OpenSceneGraph osgconv) when installed.
MODEL_EXTENSIONS = {
    # Blender built-in importers
    ".blend": "Blender",
    ".fbx": "FBX",
    ".obj": "Wavefront OBJ",
    ".gltf": "glTF",
    ".glb": "glTF Binary",
    ".stl": "STL",
    ".ply": "PLY",
    ".abc": "Alembic",
    ".usd": "USD",
    ".usda": "USD ASCII",
    ".usdc": "USD Crate",
    ".usdz": "USDZ",
    # built-in Python readers (no add-on needed)
    ".flt": "OpenFlight",
    ".dae": "COLLADA",
    ".zae": "COLLADA (zipped)",
    ".3ds": "3D Studio",
    ".off": "Object File Format",
    ".3mf": "3D Manufacturing Format",
    ".amf": "Additive Manufacturing Format",
    ".ac": "AC3D",
    ".ac3d": "AC3D",
    ".x": "DirectX",
    ".lwo": "LightWave Object",
    ".ase": "3ds Max ASCII Scene",
    ".smd": "Valve SMD",
    ".md2": "Quake II Model",
    ".vtk": "VTK legacy",
    ".dxf": "AutoCAD DXF",
    # Blender add-ons / extensions if installed, otherwise assimp / osgconv
    ".x3d": "X3D",
    ".wrl": "VRML",
    ".3dm": "Rhino",
    ".ms3d": "MilkShape 3D",
    ".md3": "Quake III Model",
    ".md5mesh": "Doom 3 MD5",
    ".mdl": "MDL (Quake/Half-Life)",
    ".mdc": "Return to Castle Wolfenstein MDC",
    ".b3d": "Blitz3D",
    ".cob": "Caligari trueSpace",
    ".hmp": "3D GameStudio terrain",
    ".ifc": "Industry Foundation Classes",
    ".ifczip": "IFC (zipped)",
    ".iqm": "Inter-Quake Model",
    ".irr": "Irrlicht scene",
    ".irrmesh": "Irrlicht mesh",
    ".lws": "LightWave scene",
    ".lxo": "Modo",
    ".m3d": "Model 3D",
    ".ndo": "Nendo",
    ".nff": "Neutral File Format",
    ".enff": "Neutral File Format (extended)",
    ".ogex": "Open Game Engine Exchange",
    ".pmx": "MikuMikuDance PMX",
    ".pmd": "MikuMikuDance PMD",
    ".q3o": "Quick3D object",
    ".q3s": "Quick3D scene",
    ".sib": "Silo",
    ".ter": "Terragen terrain",
    ".vrm": "VRM avatar",
    ".x3db": "X3D binary",
    ".xgl": "XGL",
    ".zgl": "XGL (zipped)",
    # OpenSceneGraph (needs osgconv)
    ".osg": "OpenSceneGraph",
    ".osgt": "OpenSceneGraph text",
    ".osgb": "OpenSceneGraph binary",
    ".osgx": "OpenSceneGraph XML",
    ".ive": "OpenSceneGraph IVE",
}

# Proprietary / CAD formats nothing here can read. They still get an info
# record (status "unsupported") so the database knows they exist, without
# starting Blender.
RECORD_ONLY_EXTENSIONS = {
    ".max": "3ds Max scene",
    ".ma": "Maya ASCII",
    ".mb": "Maya binary",
    ".c4d": "Cinema 4D",
    ".skp": "SketchUp",
    ".hip": "Houdini",
    ".hipnc": "Houdini (non-commercial)",
    ".hiplc": "Houdini Indie",
    ".ztl": "ZBrush tool",
    ".zpr": "ZBrush project",
    ".mud": "Mudbox",
    ".spp": "Substance Painter project",
    ".mesh": "Ogre / other mesh",
    ".step": "STEP (CAD)",
    ".stp": "STEP (CAD)",
    ".iges": "IGES (CAD)",
    ".igs": "IGES (CAD)",
    ".jt": "Siemens JT",
    ".sldprt": "SolidWorks part",
    ".sldasm": "SolidWorks assembly",
    ".catpart": "CATIA part",
    ".prt": "NX / Creo part",
    ".dwg": "AutoCAD drawing",
    ".u3d": "Universal 3D",
    ".dff": "RenderWare model",
    ".nif": "NetImmerse / Gamebryo",
    ".mdx": "Warcraft III model",
    ".w3d": "Westwood 3D",
}

ALL_EXTENSIONS = {**MODEL_EXTENSIONS, **RECORD_ONLY_EXTENSIONS}

# Suffixes of the files written next to each model.
SUFFIX_JSON = ".info.json"
SUFFIX_TXT = ".info.txt"
SUFFIX_SHEET = ".showcase"   # + image extension, contact sheet with all views
SUFFIX_HERO = ".preview"     # + image extension, single hero render

SEVERITY_ORDER = {"info": 0, "warning": 1, "unsupported": 2, "error": 3, "critical": 4}
STATUS_BY_SEVERITY = {0: "ok", 1: "warning", 2: "unsupported", 3: "error", 4: "broken"}

# code -> (severity, short description). Severity decides the overall status:
#   critical    -> "broken"      (the model itself is unusable)
#   error       -> "error"       (the tool failed, e.g. timeout; worth re-running)
#   unsupported -> "unsupported" (no reader for this format here; not a verdict on the file)
#   warning     -> "warning"     (usable, but something needs attention)
#   info        -> "ok"          (just a fact worth knowing)
ISSUES = {
    "import_failed": ("critical", "The file could not be imported by any available reader"),
    "unsupported_format": ("unsupported", "No reader for this format in this setup"),
    "blender_crash": ("critical", "Blender crashed while importing / analysing the file"),
    "no_geometry": ("critical", "The file contains no renderable geometry"),
    "zero_size": ("critical", "The geometry has no extent (all vertices in one point)"),
    "invalid_coordinates": ("critical", "Vertices contain NaN / infinite coordinates"),
    "timeout": ("error", "Processing exceeded the time limit"),
    "render_failed": ("error", "Rendering failed"),
    "worker_error": ("error", "Unexpected error in the render worker"),
    "source_unavailable": ("error", "The file could not be accessed (network share offline?)"),
    "missing_textures": ("warning", "Referenced texture files could not be found"),
    "missing_references": ("warning", "Referenced external model files could not be found"),
    "degenerate_faces": ("warning", "Many zero-area faces"),
    "non_manifold": ("warning", "Edges shared by more than two faces"),
    "extreme_scale": ("warning", "Unusually large or small dimensions (unit problem?)"),
    "missing_uvs": ("warning", "Textured meshes without UV coordinates"),
    "points_only": ("warning", "Only vertices / edges, no faces (point cloud or wire)"),
    "auto_rotated": ("info", "Model was rotated to stand upright for rendering"),
    "textures_relinked": ("info", "Missing textures were found in nearby folders"),
    "open_boundaries": ("info", "Mesh is not closed (has boundary edges)"),
    "loose_geometry": ("info", "Loose vertices or edges not connected to faces"),
    "duplicate_vertices": ("info", "Overlapping vertices (unmerged seams)"),
    "far_from_origin": ("info", "Geometry is far away from the world origin"),
    "no_materials": ("info", "No materials assigned"),
    "ngons": ("info", "Contains faces with more than four corners"),
    "has_animation": ("info", "Contains animation data"),
    "engine_fallback": ("info", "Rendered with Cycles because the GPU engine failed"),
    "import_fallback": ("info", "The preferred reader failed; another reader was used"),
    "lods_hidden": ("info", "Only the most detailed level of detail was rendered"),
}

DEFAULT_POLY_TIERS = (
    # (name, max triangles inclusive)
    ("very_low", 1_000),
    ("low", 10_000),
    ("medium", 100_000),
    ("high", 1_000_000),
    ("very_high", None),
)


def now_iso():
    return datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat()


def make_issue(code, message=None, **details):
    severity, default_msg = ISSUES.get(code, ("warning", code))
    issue = {"code": code, "severity": severity, "message": message or default_msg}
    if details:
        issue["details"] = details
    return issue


def add_issue(record, code, message=None, **details):
    issues = record.setdefault("issues", [])
    if any(i["code"] == code for i in issues):
        return
    issues.append(make_issue(code, message, **details))


def compute_status(issues):
    worst = max((SEVERITY_ORDER.get(i.get("severity"), 1) for i in issues), default=0)
    return STATUS_BY_SEVERITY[worst]


def poly_tier(triangles, tiers=DEFAULT_POLY_TIERS):
    if not triangles:
        return "none"
    for name, limit in tiers:
        if limit is None or triangles <= limit:
            return name
    return tiers[-1][0]


def tiers_from_limits(limits):
    """``limits`` = [very_low, low, medium, high] maximum triangle counts."""
    names = [t[0] for t in DEFAULT_POLY_TIERS]
    return tuple(zip(names, list(limits) + [None]))


def finalize(record):
    """Sort issues by severity and (re)compute the overall status."""
    issues = record.setdefault("issues", [])
    issues.sort(key=lambda i: -SEVERITY_ORDER.get(i.get("severity"), 1))
    record["status"] = compute_status(issues)
    record["schema_version"] = SCHEMA_VERSION
    return record


# --------------------------------------------------------------------------
# Flat row for databases
# --------------------------------------------------------------------------

FLAT_COLUMNS = [
    # (column, sqlite type)
    ("id", "TEXT"),
    ("path", "TEXT"),
    ("relative_path", "TEXT"),
    ("root", "TEXT"),
    ("name", "TEXT"),
    ("extension", "TEXT"),
    ("format", "TEXT"),
    ("size_bytes", "INTEGER"),
    ("modified", "TEXT"),
    ("sha256", "TEXT"),
    ("status", "TEXT"),
    ("issue_codes", "TEXT"),
    ("poly_tier", "TEXT"),
    ("objects", "INTEGER"),
    ("mesh_objects", "INTEGER"),
    ("vertices", "INTEGER"),
    ("edges", "INTEGER"),
    ("faces", "INTEGER"),
    ("triangles", "INTEGER"),
    ("quads", "INTEGER"),
    ("ngons", "INTEGER"),
    ("materials", "INTEGER"),
    ("textures", "INTEGER"),
    ("missing_textures", "INTEGER"),
    ("uv_maps", "INTEGER"),
    ("armatures", "INTEGER"),
    ("bones", "INTEGER"),
    ("shape_keys", "INTEGER"),
    ("actions", "INTEGER"),
    ("rigged", "INTEGER"),
    ("animated", "INTEGER"),
    ("textured", "INTEGER"),
    ("watertight", "INTEGER"),
    ("non_manifold_edges", "INTEGER"),
    ("boundary_edges", "INTEGER"),
    ("degenerate_faces", "INTEGER"),
    ("surface_area", "REAL"),
    ("volume", "REAL"),
    ("size_x", "REAL"),
    ("size_y", "REAL"),
    ("size_z", "REAL"),
    ("scale_hint", "TEXT"),
    ("up_axis_detected", "TEXT"),
    ("auto_rotated", "INTEGER"),
    ("tags", "TEXT"),
    ("preview_image", "TEXT"),
    ("sheet_image", "TEXT"),
    ("info_json", "TEXT"),
    ("importer", "TEXT"),
    ("blender_version", "TEXT"),
    ("render_engine", "TEXT"),
    ("processing_seconds", "REAL"),
    ("processed_at", "TEXT"),
]


def _b(v):
    return None if v is None else int(bool(v))


def flatten(record):
    f = record.get("file", {})
    g = record.get("geometry", {})
    s = record.get("scene", {})
    m = record.get("materials", {})
    a = record.get("animation", {})
    b = record.get("bounds", {})
    o = record.get("orientation", {})
    c = record.get("classification", {})
    r = record.get("render", {})
    size = b.get("size") or [None, None, None]
    return {
        "id": record.get("id"),
        "path": f.get("path"),
        "relative_path": f.get("relative_path"),
        "root": f.get("root"),
        "name": f.get("name"),
        "extension": f.get("extension"),
        "format": f.get("format"),
        "size_bytes": f.get("size_bytes"),
        "modified": f.get("modified"),
        "sha256": f.get("sha256"),
        "status": record.get("status"),
        "issue_codes": ",".join(i["code"] for i in record.get("issues", [])),
        "poly_tier": c.get("poly_tier"),
        "objects": s.get("objects"),
        "mesh_objects": s.get("mesh_objects"),
        "vertices": g.get("vertices"),
        "edges": g.get("edges"),
        "faces": g.get("faces"),
        "triangles": g.get("triangles"),
        "quads": g.get("quads"),
        "ngons": g.get("ngons"),
        "materials": m.get("count"),
        "textures": m.get("textures"),
        "missing_textures": len(m.get("missing_textures") or []) if m else None,
        "uv_maps": m.get("uv_maps"),
        "armatures": s.get("armatures"),
        "bones": s.get("bones"),
        "shape_keys": a.get("shape_keys"),
        "actions": a.get("actions"),
        "rigged": _b(c.get("rigged")),
        "animated": _b(c.get("animated")),
        "textured": _b(c.get("textured")),
        "watertight": _b(g.get("watertight")),
        "non_manifold_edges": g.get("non_manifold_edges"),
        "boundary_edges": g.get("boundary_edges"),
        "degenerate_faces": g.get("degenerate_faces"),
        "surface_area": g.get("surface_area"),
        "volume": g.get("volume"),
        "size_x": size[0],
        "size_y": size[1],
        "size_z": size[2],
        "scale_hint": b.get("scale_hint"),
        "up_axis_detected": o.get("detected_up"),
        "auto_rotated": _b(o.get("rotation_applied")),
        "tags": ",".join(c.get("tags") or []),
        "preview_image": r.get("preview_image"),
        "sheet_image": r.get("sheet_image"),
        "info_json": record.get("info_json"),
        "importer": record.get("import", {}).get("importer"),
        "blender_version": record.get("import", {}).get("blender_version"),
        "render_engine": r.get("engine"),
        "processing_seconds": record.get("processing_seconds"),
        "processed_at": record.get("processed_at"),
    }


# --------------------------------------------------------------------------
# Human-readable report
# --------------------------------------------------------------------------

def _num(v):
    if v is None:
        return "-"
    if isinstance(v, float):
        return f"{v:,.4g}" if abs(v) < 1e4 else f"{v:,.0f}"
    if isinstance(v, int):
        return f"{v:,}"
    return str(v)


def human_bytes(n):
    if n is None:
        return "-"
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return f"{n:.0f} {unit}" if unit == "B" else f"{n:.1f} {unit}"
        n /= 1024.0


def format_report(record):
    f = record.get("file", {})
    g = record.get("geometry", {})
    s = record.get("scene", {})
    m = record.get("materials", {})
    a = record.get("animation", {})
    b = record.get("bounds", {})
    o = record.get("orientation", {})
    c = record.get("classification", {})
    r = record.get("render", {})
    imp = record.get("import", {})
    issues = record.get("issues", [])

    lines = []
    add = lines.append

    def row(label, value):
        add(f"  {label:<22}: {value}")

    add("=" * 72)
    add(f" 3D MODEL REPORT - {f.get('name', '?')}")
    add("=" * 72)
    counts = {}
    for i in issues:
        counts[i["severity"]] = counts.get(i["severity"], 0) + 1
    summary = ", ".join(f"{n} {sev}" for sev, n in sorted(counts.items(), key=lambda kv: -SEVERITY_ORDER[kv[0]]))
    row("Status", f"{record.get('status', '?').upper()}" + (f"  ({summary})" if summary else ""))
    if g:
        row("Polygon tier", f"{c.get('poly_tier', '-').upper().replace('_', ' ')}  ({_num(g.get('triangles'))} triangles)")
    if c.get("tags"):
        row("Tags", ", ".join(c["tags"]))
    add("")
    add("[FILE]")
    row("Path", f.get("path"))
    row("Relative path", f.get("relative_path"))
    row("Format", f"{f.get('format')} ({f.get('extension')})")
    row("File size", f"{human_bytes(f.get('size_bytes'))} ({_num(f.get('size_bytes'))} bytes)")
    row("Modified", f.get("modified"))
    row("SHA-256", f.get("sha256") or "-")

    if s:
        add("")
        add("[SCENE]")
        row("Objects", _num(s.get("objects")))
        row("Mesh objects", _num(s.get("mesh_objects")))
        row("Other geometry objects", _num(s.get("other_geometry_objects")))
        row("Armatures / bones", f"{_num(s.get('armatures'))} / {_num(s.get('bones'))}")
        row("Cameras / lights (file)", f"{_num(s.get('cameras'))} / {_num(s.get('lights'))}")
        row("Empties", _num(s.get("empties")))

    if g:
        add("")
        add("[GEOMETRY]")
        row("Vertices", _num(g.get("vertices")))
        row("Edges", _num(g.get("edges")))
        row("Faces", _num(g.get("faces")))
        row("Triangles", _num(g.get("triangles")))
        row("Tris / quads / ngons", f"{_num(g.get('tri_faces'))} / {_num(g.get('quads'))} / {_num(g.get('ngons'))}")
        row("Watertight", "yes" if g.get("watertight") else "no")
        row("Boundary edges", _num(g.get("boundary_edges")))
        row("Non-manifold edges", _num(g.get("non_manifold_edges")))
        row("Loose verts / edges", f"{_num(g.get('loose_vertices'))} / {_num(g.get('loose_edges'))}")
        row("Degenerate faces", _num(g.get("degenerate_faces")))
        row("Duplicate vertices", _num(g.get("duplicate_vertices")))
        row("Surface area", _num(g.get("surface_area")))
        row("Volume", _num(g.get("volume")) + ("" if g.get("watertight") else "  (unreliable: not watertight)"))
        if g.get("evaluated_triangles") and g.get("evaluated_triangles") != g.get("triangles"):
            row("Triangles w/ modifiers", _num(g.get("evaluated_triangles")))

    if b:
        add("")
        add("[DIMENSIONS]  (Blender units, usually metres, after orientation fix)")
        size = b.get("size") or [0, 0, 0]
        row("Size X / Y / Z", " x ".join(_num(v) for v in size))
        row("Diagonal", _num(b.get("diagonal")))
        row("Center", ", ".join(_num(v) for v in b.get("center") or []))
        row("Scale hint", b.get("scale_hint"))

    if m:
        add("")
        add("[MATERIALS & TEXTURES]")
        row("Materials", _num(m.get("count")))
        if m.get("names"):
            row("Material names", ", ".join(m["names"][:12]) + (" ..." if len(m["names"]) > 12 else ""))
        row("Textures (images)", _num(m.get("textures")))
        row("Packed / embedded", _num(m.get("packed_textures")))
        row("UV maps (total)", _num(m.get("uv_maps")))
        row("Vertex color layers", _num(m.get("color_attributes")))
        if m.get("missing_textures"):
            row("Missing textures", ", ".join(m["missing_textures"][:10]) + (" ..." if len(m["missing_textures"]) > 10 else ""))
        if m.get("relinked_textures"):
            row("Relinked textures", _num(len(m["relinked_textures"])))

    if a:
        add("")
        add("[ANIMATION]")
        row("Actions", _num(a.get("actions")))
        row("Frame range", f"{_num(a.get('frame_start'))} - {_num(a.get('frame_end'))}")
        row("Shape keys", _num(a.get("shape_keys")))

    if o:
        add("")
        add("[ORIENTATION]")
        row("Mode", o.get("mode"))
        row("Detected up axis", o.get("detected_up"))
        row("Rotation applied", "yes" if o.get("rotation_applied") else "no")
        if o.get("confidence") is not None:
            row("Confidence", f"{o['confidence']:.2f}")
        row("Front direction", o.get("front"))

    add("")
    add("[ISSUES]")
    if not issues:
        add("  none")
    for i in issues:
        add(f"  [{i['severity'].upper():<8}] {i['code']}: {i['message']}")

    if r:
        add("")
        add("[RENDER]")
        row("Engine", r.get("engine"))
        row("Views", ", ".join(v.get("name") for v in r.get("views", [])))
        row("Contact sheet", r.get("sheet_image") or "-")
        row("Preview", r.get("preview_image") or "-")
        row("Render time (s)", _num(r.get("seconds")))

    add("")
    add("[PROCESSING]")
    row("Importer", imp.get("importer"))
    for k, v in (imp.get("format_details") or {}).items():
        row(f"  {k}"[:22], v if not isinstance(v, dict) else ", ".join(f"{a}={b}" for a, b in v.items() if b))
    for err in imp.get("failed_attempts") or []:
        row("  failed attempt", err[:120])
    row("Import time (s)", _num(imp.get("seconds")))
    row("Blender", imp.get("blender_version"))
    row("Total time (s)", _num(record.get("processing_seconds")))
    row("Processed at", record.get("processed_at"))
    row("Tool version", record.get("tool_version"))
    row("Record id", record.get("id"))
    add("")
    add("Machine-readable data: see the matching .info.json file.")
    return "\n".join(lines) + "\n"


def dumps(record):
    return json.dumps(record, indent=2, ensure_ascii=False, sort_keys=False)
