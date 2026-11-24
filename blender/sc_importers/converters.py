"""External converter fallbacks: assimp CLI (~40 formats) and OpenSceneGraph's osgconv.

The converted file (glTF binary / OBJ) is imported with Blender's own importer.
"""

import os
import shutil
import subprocess
import tempfile

import bpy
from mathutils import Vector

# Formats the assimp command line tool can read (import side).
ASSIMP_EXTS = {
    ".3d", ".3ds", ".3mf", ".ac", ".ac3d", ".acc", ".amf", ".ase", ".ask", ".b3d", ".blend", ".bvh", ".cob",
    ".dae", ".dxf", ".enff", ".fbx", ".glb", ".gltf", ".hmp", ".ifc", ".ifczip", ".iqm", ".irr",
    ".irrmesh", ".lwo", ".lws", ".lxo", ".m3d", ".md2", ".md3", ".md5mesh", ".mdc", ".mdl", ".ms3d", ".ndo",
    ".nff", ".obj", ".off", ".ogex", ".ply", ".pmx", ".q3o", ".q3s", ".sib", ".smd", ".stl", ".ter",
    ".x", ".x3d", ".x3db", ".xgl", ".zae", ".zgl",
}
# Formats OpenSceneGraph (osgconv) reads well.
OSG_EXTS = {".osg", ".osgt", ".osgb", ".osgx", ".ive", ".flt", ".3ds", ".lwo", ".ac", ".x", ".dae"}

# Assimp passes some formats' native axes through unchanged although its output is
# meant to be Y-up. Blender's importer then turns that "Y-up" into Z-up, so the model's
# real up direction ends up here (checked against the assimp sample models):
CONVERTED_UP = {
    ".cob": (0, -1, 0),   # Caligari trueSpace: Z-up
    ".xgl": (0, -1, 0),   # XGL / ZGL: Z-up
    ".zgl": (0, -1, 0),
    ".q3o": (0, 1, 0),    # Quick3D: -Z up
    ".q3s": (0, 1, 0),
}

HINTS = {
    "assimp": "run the launcher/setup_env.py to install the assimp-py package, or install the assimp tool",
    "osgconv": "install OpenSceneGraph (osgconv) to read this format",
}


def _run(cmd, timeout):
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                         timeout=timeout, creationflags=flags)
    return res.returncode, res.stdout.decode("utf-8", "replace")[-800:]


def available(settings):
    tools = {}
    for key, setting in (("assimp", "assimp_path"), ("osgconv", "osgconv_path")):
        # the runner passes resolved paths ("" = not installed); look on PATH only when run standalone
        exe = settings[setting] if setting in settings and "assimp_python" in settings else (settings.get(setting) or key)
        if not exe:
            continue
        found = exe if os.path.isfile(exe) else shutil.which(exe)
        if found:
            tools[key] = found
    py = settings.get("assimp_python")
    if py and os.path.isfile(py):
        tools["assimp_py"] = py  # pip package installed by the launcher: no separate download needed
    return tools


OSG_NATIVE = {".osg", ".osgt", ".osgb", ".osgx", ".ive", ".flt"}


def candidates(ext, tools):
    """Converters to try, best first: osgconv for OpenSceneGraph's own formats, assimp otherwise."""
    order = ("osgconv", "assimp", "assimp_py") if ext in OSG_NATIVE else ("assimp", "assimp_py", "osgconv")
    usable = {"osgconv": ext in OSG_EXTS, "assimp": ext in ASSIMP_EXTS, "assimp_py": ext in ASSIMP_EXTS}
    return [t for t in order if usable[t] and t in tools]


def needed_tool(ext):
    if ext in (".osg", ".osgt", ".osgb", ".osgx", ".ive"):
        return "osgconv"
    if ext in ASSIMP_EXTS:
        return "assimp"
    return None


class NoReader(Exception):
    """The converter has no reader for this file type."""


def _fix_axes(ext):
    up = CONVERTED_UP.get(ext)
    if up is None:
        return None
    R = Vector(up).rotation_difference(Vector((0, 0, 1))).to_matrix().to_4x4()
    for o in bpy.context.scene.objects:
        if o.parent is None:
            o.matrix_world = R @ o.matrix_world
    bpy.context.view_layer.update()
    return {(0, -1, 0): "Z-up source", (0, 1, 0): "-Z-up source"}.get(up, str(up))


def convert_and_import(tool, exe, path, timeout):
    """Convert ``path`` and import the result into the current scene. Returns a description."""
    tmp = tempfile.mkdtemp(prefix="showcase_conv_")
    try:
        if tool == "assimp":
            out = os.path.join(tmp, "converted.glb")
            rc, log = _run([exe, "export", path, out, "-fglb2"], timeout)
            if rc != 0 or not os.path.isfile(out):
                out = os.path.join(tmp, "converted.obj")
                rc, log = _run([exe, "export", path, out, "-fobj"], timeout)
            if rc != 0 or not os.path.isfile(out):
                if "no suitable reader" in log.lower() or "unknown file format" in log.lower():
                    raise NoReader("assimp has no reader for this file type")
                raise RuntimeError(f"assimp failed: {log.strip()[-300:]}")
        elif tool == "assimp_py":
            out = os.path.join(tmp, "converted.obj")
            script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "assimp_py_convert.py")
            rc, log = _run([exe, script, path, out], timeout)
            if rc != 0 or not os.path.isfile(out):
                if "no suitable reader" in log.lower():
                    raise NoReader("assimp-py has no reader for this file type")
                raise RuntimeError(f"assimp_py failed: {log.strip()[-300:]}")
        else:
            out = os.path.join(tmp, "converted.obj")
            rc, log = _run([exe, path, out], timeout)
            if rc != 0 or not os.path.isfile(out):
                raise RuntimeError(f"osgconv failed: {log.strip()[-300:]}")
        if out.endswith(".glb"):
            bpy.ops.import_scene.gltf(filepath=out)
        elif tool == "osgconv":  # OSG is Z-up
            bpy.ops.wm.obj_import(filepath=out, up_axis="Z", forward_axis="Y")
        else:
            bpy.ops.wm.obj_import(filepath=out)
        if not any(o.type == "MESH" and len(o.data.polygons) for o in bpy.data.objects):
            raise RuntimeError(f"{tool} produced no geometry")
        fixed = _fix_axes(os.path.splitext(path)[1].lower()) if tool != "osgconv" else None
        # converted textures may live in the temp folder: pack them before it is deleted
        for img in bpy.data.images:
            fp = bpy.path.abspath(img.filepath)
            if fp.startswith(tmp) and os.path.isfile(fp):
                try:
                    img.pack()
                except RuntimeError:
                    pass
        return f"{tool} -> {os.path.splitext(out)[1].lstrip('.')}" + (f" ({fixed} corrected)" if fixed else "")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
