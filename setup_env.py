#!/usr/bin/env python3
"""One-click setup + launcher (used by the "Start Toolkit" launchers).

    python setup_env.py                 # create/update .venv and check the system
    python setup_env.py --run gui       # ...then start the GUI
    python setup_env.py --run batch -- D:/Models --preset quick
    python setup_env.py --check         # only report what is installed

Creates a private virtual environment in ``.venv`` next to this file and
installs the optional packages from requirements.txt one by one (a package
that has no build for your Python version is skipped, the tool still works).
Only Blender itself has to be installed separately.
"""

import argparse
import hashlib
import json
import os
import platform
import subprocess
import sys
import venv

HERE = os.path.dirname(os.path.abspath(__file__))
VENV = os.path.join(HERE, ".venv")
REQS = os.path.join(HERE, "requirements.txt")
STAMP = os.path.join(VENV, "showcase_setup.json")
MIN_PY = (3, 9)
WINDOWS = platform.system() == "Windows"


def say(msg=""):
    print(msg, flush=True)


def venv_python(gui=False):
    if WINDOWS:
        exe = "pythonw.exe" if gui else "python.exe"
        return os.path.join(VENV, "Scripts", exe)
    return os.path.join(VENV, "bin", "python")


def requirements():
    reqs = []
    with open(REQS, encoding="utf-8") as fh:
        for line in fh:
            line = line.split("#", 1)[0].strip()
            if line:
                reqs.append(line)
    return reqs


def req_hash():
    h = hashlib.sha1()
    for f in (REQS, os.path.join(VENV, "pyvenv.cfg")):  # requirements + the Python the .venv was made from
        if os.path.isfile(f):
            with open(f, "rb") as fh:
                h.update(fh.read())
    return h.hexdigest()


def has_tkinter(python):
    return subprocess.run([python, "-c", "import tkinter; tkinter.Tcl()"], capture_output=True).returncode == 0


def tkinter_help():
    system = platform.system()
    if system == "Linux":
        return ("Install Tkinter, e.g.  sudo apt install python3-tk   (Debian/Ubuntu)\n"
                "                       sudo dnf install python3-tkinter (Fedora)\n"
                "                       sudo pacman -S tk               (Arch)")
    if system == "Darwin":
        return "Install Python from https://www.python.org/downloads/ (includes Tkinter) or: brew install python-tk"
    return "Re-install Python from https://www.python.org/downloads/ and keep 'tcl/tk and IDLE' ticked."


def ensure_venv():
    py = venv_python()
    if os.path.isfile(py):
        if has_tkinter(py) or not has_tkinter(sys.executable):
            return py
        say("The existing .venv has no Tkinter, rebuilding it with this Python ...")
    say(f"Creating private Python environment in {VENV} ...")
    try:
        venv.EnvBuilder(with_pip=True, clear=os.path.isdir(VENV)).create(VENV)
    except Exception as exc:  # e.g. Debian without python3-venv
        say(f"  Could not create the environment ({exc}).")
        if platform.system() == "Linux":
            say("  Install it with:  sudo apt install python3-venv   - continuing without optional packages.")
        return None
    return py if os.path.isfile(py) else None


def install_packages(py, force=False):
    stamp = {}
    if os.path.isfile(STAMP):
        try:
            with open(STAMP, encoding="utf-8") as fh:
                stamp = json.load(fh)
        except ValueError:
            stamp = {}
    if not force and stamp.get("hash") == req_hash():
        return stamp.get("results", {})
    say("Installing optional packages (first start only, needs internet) ...")
    subprocess.run([py, "-m", "pip", "install", "--disable-pip-version-check", "-q", "--upgrade", "pip"],
                   capture_output=True)
    results = {}
    for req in requirements():
        name = req.split(">")[0].split("=")[0].split("<")[0].strip()
        r = subprocess.run([py, "-m", "pip", "install", "--disable-pip-version-check", "-q",
                            "--only-binary", ":all:", req], capture_output=True, text=True)
        ok = r.returncode == 0
        results[name] = ok
        say(f"  {'ok     ' if ok else 'skipped'} {name}" + ("" if ok else "  (no build for this Python/platform - optional)"))
    os.makedirs(VENV, exist_ok=True)
    with open(STAMP, "w", encoding="utf-8") as fh:
        json.dump({"hash": req_hash(), "results": results}, fh, indent=1)
    return results


def report(py):
    sys.path.insert(0, HERE)
    from showcase import config
    blender = config.find_blender()
    say("")
    say(f"Python      : {sys.version.split()[0]} ({sys.executable})")
    say(f"Environment : {py or 'system Python (no .venv)'}")
    say(f"Blender     : {blender or 'NOT FOUND - set the path in the GUI (Blender field) or BLENDER_PATH'}")
    say(f"assimp tool : {config.find_tool('', 'assimp') or '-'}   osgconv: {config.find_tool('', 'osgconv') or '-'}")
    say(f"Tkinter     : {'ok' if has_tkinter(py or sys.executable) else 'MISSING - ' + tkinter_help()}")
    say("")


def launch(py, target, extra):
    script = os.path.join(HERE, {"gui": "showcase_gui.py"}.get(target, "showcase_batch.py"))
    if target == "gui":
        if not has_tkinter(py):
            say("Tkinter is missing, the GUI cannot start.\n" + tkinter_help())
            say("The command line version still works:  showcase_batch <folder>")
            return 1
        gui_py = venv_python(gui=True) if py == venv_python() and WINDOWS else py
        if WINDOWS:  # detach so the console window can close
            flags = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
            subprocess.Popen([gui_py, script] + extra, cwd=HERE, creationflags=flags, close_fds=True)
            return 0
        os.execv(py, [py, script] + extra)
    return subprocess.call([py, script] + extra, cwd=os.getcwd())


def main():
    if sys.version_info < MIN_PY:
        say(f"Python {MIN_PY[0]}.{MIN_PY[1]} or newer is required (this is {sys.version.split()[0]}).")
        return 1
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", choices=["gui", "batch"],
                   help="start the GUI, or the command line tool afterwards")
    p.add_argument("--check", action="store_true", help="only report the installation")
    p.add_argument("--reinstall", action="store_true", help="re-install the optional packages")
    p.add_argument("rest", nargs=argparse.REMAINDER, help="arguments for the batch tool (after --)")
    a = p.parse_args()
    extra = a.rest[1:] if a.rest[:1] == ["--"] else a.rest

    py = venv_python() if os.path.isfile(venv_python()) else None
    if a.check:
        report(py)
        return 0
    py = ensure_venv()
    if py:
        install_packages(py, force=a.reinstall)
    if not a.run:
        report(py)
        say("Setup done. Start the GUI with the 'Start Toolkit' launcher or:  python setup_env.py --run gui")
        return 0
    return launch(py or sys.executable, a.run, extra)


if __name__ == "__main__":
    sys.exit(main())
