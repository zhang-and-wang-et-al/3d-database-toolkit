"""Settings, presets, job files and Blender discovery."""

import dataclasses
import glob
import json
import os
import platform
import re
import shutil
from dataclasses import dataclass, field

ALL_VIEWS = ["hero", "front", "side", "back", "top", "wire"]

PRESETS = {
    # fast triage of huge libraries: one hero shot + wireframe
    "quick": dict(resolution=480, samples=12, views=["hero", "wire"]),
    # default: full contact sheet
    "standard": dict(resolution=640, samples=32, views=list(ALL_VIEWS)),
    # nicer images for a catalogue
    "high": dict(resolution=1024, samples=96, views=list(ALL_VIEWS)),
}

WORKER_KEYS = ("engine", "samples", "resolution", "views", "image_format", "jpeg_quality",
               "orientation", "ground", "fix_textures", "gpu", "poly_tiers", "wire_max_faces",
               "cycles_time_limit", "background", "assimp_path", "osgconv_path", "converter_timeout")
# (the runner also passes "assimp_python": an interpreter with the assimp_py package)


@dataclass
class Settings:
    # --- Blender / processing ---
    blender: str = ""
    factory_startup: bool = False
    jobs: int = 1                      # parallel Blender processes
    timeout: int = 600                 # seconds per model (+60 s per 50 MB of file size)
    skip_existing: bool = True         # skip models whose .info.json is up to date
    retry_errors: bool = True          # ...but re-run models that ended with status "error"
    hash_files: bool = True            # SHA-256 per file (duplicate detection, stable id)
    record_unsupported: bool = True    # list proprietary files (.max, .c4d, ...) as "unsupported"
    # --- optional external converters for rare formats ("" = look on PATH) ---
    assimp_path: str = ""
    osgconv_path: str = ""
    converter_timeout: int = 300
    # --- output ---
    output_mode: str = "beside"        # beside | mirror
    output_dir: str = ""               # used when output_mode == "mirror"
    index_path: str = ""               # "" = showcase_index.sqlite in each root / output_dir
    fallback_output_dir: str = ""      # results for read-only folders ("" = ~/3DDatabaseToolkit/output)
    # --- rendering (forwarded to the Blender worker) ---
    preset: str = "standard"
    engine: str = "auto"               # auto | eevee | cycles | workbench
    samples: int = 32
    resolution: int = 640
    views: list = field(default_factory=lambda: list(ALL_VIEWS))
    image_format: str = "JPEG"         # JPEG | PNG
    jpeg_quality: int = 88
    orientation: str = "auto"          # auto | importer | detect
    ground: bool = True
    fix_textures: bool = True
    gpu: bool = True
    poly_tiers: list = field(default_factory=lambda: [1_000, 10_000, 100_000, 1_000_000])
    wire_max_faces: int = 400_000
    cycles_time_limit: int = 0
    background: str = "light"         # light | dark

    def apply_preset(self, name):
        self.preset = name
        for k, v in PRESETS.get(name, {}).items():
            setattr(self, k, list(v) if isinstance(v, list) else v)

    def worker_settings(self):
        return {k: getattr(self, k) for k in WORKER_KEYS}

    def to_dict(self):
        return dataclasses.asdict(self)

    @classmethod
    def from_dict(cls, data):
        known = {f.name for f in dataclasses.fields(cls)}
        return cls(**{k: v for k, v in (data or {}).items() if k in known})


# ---------------------------------------------------------------------------
# Job files: roots + include/exclude overrides + settings
# ---------------------------------------------------------------------------

def save_job(path, roots, overrides, settings):
    data = {"version": 1, "roots": list(roots), "overrides": dict(overrides), "settings": settings.to_dict()}
    tmp = path + ".tmp"
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2)
    os.replace(tmp, path)


def load_job(path):
    with open(path, encoding="utf-8") as fh:
        data = json.load(fh)
    return data.get("roots", []), data.get("overrides", {}), Settings.from_dict(data.get("settings"))


def default_fallback_dir():
    return os.path.join(os.path.expanduser("~"), "3DDatabaseToolkit", "output")


def user_cache_dir():
    base = os.environ.get("LOCALAPPDATA") or os.environ.get("XDG_CACHE_HOME") or os.path.join(os.path.expanduser("~"), ".cache")
    return os.path.join(base, "3d_database_toolkit")


def user_config_dir():
    base = os.environ.get("APPDATA") or os.path.join(os.path.expanduser("~"), ".config")
    return os.path.join(base, "3d_database_toolkit")


# ---------------------------------------------------------------------------
# Blender discovery
# ---------------------------------------------------------------------------

def _version_key(path):
    nums = re.findall(r"(\d+)\.(\d+)", path)
    return tuple(int(x) for x in nums[-1]) if nums else (0, 0)


def find_blender():
    """Best guess for the Blender executable, or '' if none was found."""
    env = os.environ.get("BLENDER_PATH") or os.environ.get("BLENDER")
    if env and os.path.isfile(env):
        return env
    found = shutil.which("blender")
    if found:
        return found
    candidates = []
    system = platform.system()
    if system == "Windows":
        for base in (os.environ.get("ProgramFiles", r"C:\Program Files"),
                     os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")):
            candidates += glob.glob(os.path.join(base, "Blender Foundation", "Blender*", "blender.exe"))
            candidates += glob.glob(os.path.join(base, "Steam", "steamapps", "common", "Blender", "blender.exe"))
        local = os.environ.get("LOCALAPPDATA", "")
        if local:
            candidates += glob.glob(os.path.join(local, "Programs", "Blender Foundation", "Blender*", "blender.exe"))
    elif system == "Darwin":
        candidates += glob.glob("/Applications/Blender*.app/Contents/MacOS/Blender")
        candidates += glob.glob(os.path.expanduser("~/Applications/Blender*.app/Contents/MacOS/Blender"))
    else:
        candidates += [p for p in ("/usr/bin/blender", "/usr/local/bin/blender", "/snap/bin/blender") if os.path.isfile(p)]
        candidates += glob.glob(os.path.expanduser("~/blender*/blender"))
        candidates += glob.glob("/opt/blender*/blender")
    candidates = [c for c in candidates if os.path.isfile(c)]
    return max(candidates, key=_version_key) if candidates else ""


def find_tool(configured, name):
    """Path of an optional command line tool, or '' when it is not installed."""
    if configured:
        return configured if os.path.isfile(configured) else (shutil.which(configured) or "")
    found = shutil.which(name)
    if found:
        return found
    if platform.system() == "Windows":
        pf = os.environ.get("ProgramFiles", r"C:\Program Files")
        pats = {"assimp": [os.path.join(pf, "Assimp", "bin", "*", "assimp.exe"), os.path.join(pf, "Assimp", "bin", "assimp.exe")],
                "osgconv": [os.path.join(pf, "OpenSceneGraph*", "bin", "osgconv.exe")]}.get(name, [])
        hits = [h for p in pats for h in glob.glob(p)]
        if hits:
            return hits[0]
    return ""


class BlenderNotFound(Exception):
    pass


def worker_script():
    return os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "blender", "render_worker.py")


def blender_command(settings, args):
    """Command line that runs the worker with ``args`` (list after ``--``).

    ``settings.blender`` may also point to a Python interpreter that has the
    ``bpy`` module installed (pip install bpy) - handy on render servers.
    """
    exe = settings.blender or find_blender()
    if not exe:
        raise BlenderNotFound("Blender executable not found - set it in the settings (or BLENDER_PATH).")
    if os.path.basename(exe).lower().startswith("python"):
        return [exe, worker_script(), "--"] + args
    cmd = [exe, "-b", "-noaudio"]
    if settings.factory_startup:
        cmd.append("--factory-startup")
    return cmd + ["--python-exit-code", "1", "--python", worker_script(), "--"] + args
