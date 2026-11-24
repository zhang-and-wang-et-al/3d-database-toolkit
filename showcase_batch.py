#!/usr/bin/env python3
"""Command-line batch renderer (the GUI uses the same engine).

Examples::

    # render everything below a folder, results next to each model
    python showcase_batch.py D:/Models

    # quick triage of a huge library with 4 Blender processes
    python showcase_batch.py D:/Models --preset quick --jobs 4

    # skip some folders, write everything to a separate folder
    python showcase_batch.py D:/Models --exclude "D:/Models/old" --output-dir D:/Showcase

    # run a job saved from the GUI
    python showcase_batch.py --job my_library.showcase-job.json

    # only list what would be processed
    python showcase_batch.py D:/Models --list

    # rebuild the SQLite/CSV index from existing .info.json files
    python showcase_batch.py D:/Models --reindex
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from showcase import config, index_db  # noqa: E402
from showcase.runner import Runner  # noqa: E402
from showcase.selection import Selection  # noqa: E402


def main(argv=None):
    p = argparse.ArgumentParser(description="Render showcase images + info files for every 3D model in a folder.",
                                formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    p.add_argument("roots", nargs="*", help="folder(s) to scan")
    p.add_argument("--job", help="job file saved by the GUI (roots, include/exclude, settings)")
    p.add_argument("--blender", help="path to blender executable (or a python with the bpy module)")
    p.add_argument("--preset", choices=sorted(config.PRESETS), help="quick / standard / high")
    p.add_argument("--engine", choices=["auto", "eevee", "cycles", "workbench"])
    p.add_argument("--samples", type=int)
    p.add_argument("--resolution", type=int, help="pixel size of each (square) view")
    p.add_argument("--views", help="comma list of: " + ",".join(config.ALL_VIEWS))
    p.add_argument("--format", choices=["jpg", "png"])
    p.add_argument("--orientation", choices=["auto", "importer", "detect"],
                   help="auto = fix orientation only for formats without axis info (OBJ/STL/PLY)")
    p.add_argument("--background", choices=["light", "dark"])
    p.add_argument("--no-ground", action="store_true", help="no floor / shadow plane")
    p.add_argument("--no-gpu", action="store_true", help="force Cycles to use the CPU")
    p.add_argument("--no-hash", action="store_true", help="skip SHA-256 hashing (faster on huge files)")
    p.add_argument("--jobs", type=int, help="parallel Blender processes")
    p.add_argument("--timeout", type=int, help="seconds per model")
    p.add_argument("--output-dir", help="write results to this folder (mirrored tree) instead of next to the models")
    p.add_argument("--index", help="path of the SQLite index file")
    p.add_argument("--exclude", action="append", default=[], help="folder or file to exclude (repeatable)")
    p.add_argument("--include", action="append", default=[], help="re-include a folder/file inside an excluded one")
    p.add_argument("--force", action="store_true", help="re-render even if results are up to date")
    p.add_argument("--list", action="store_true", help="only list the selected model files")
    p.add_argument("--reindex", action="store_true", help="rebuild index from existing .info.json files and exit")
    a = p.parse_args(argv)

    if a.job:
        roots, overrides, settings = config.load_job(a.job)
    else:
        roots, overrides, settings = [], {}, config.Settings()
    roots = list(roots) + list(a.roots)
    if not roots:
        p.error("give at least one folder (or --job)")

    if a.reindex:
        for r in roots:
            index_db.rebuild(a.output_dir or r, a.index)
        return 0

    if a.preset:
        settings.apply_preset(a.preset)
    for attr, val in (("blender", a.blender), ("engine", a.engine), ("samples", a.samples),
                      ("resolution", a.resolution), ("orientation", a.orientation), ("jobs", a.jobs),
                      ("timeout", a.timeout), ("background", a.background), ("index_path", a.index)):
        if val is not None:
            setattr(settings, attr, val)
    if a.views:
        settings.views = [v.strip() for v in a.views.split(",") if v.strip() in config.ALL_VIEWS]
    if a.format:
        settings.image_format = "JPEG" if a.format == "jpg" else "PNG"
    if a.no_ground:
        settings.ground = False
    if a.no_gpu:
        settings.gpu = False
    if a.no_hash:
        settings.hash_files = False
    if a.force:
        settings.skip_existing = False
    if a.output_dir:
        settings.output_mode, settings.output_dir = "mirror", os.path.abspath(a.output_dir)

    sel = Selection(roots, overrides)
    for path in a.exclude:
        sel.set(path, False)
    for path in a.include:
        sel.set(path, True)

    if a.list:
        n = 0
        for _, path in sel.iter_models():
            print(path)
            n += 1
        print(f"{n} model file(s)", file=sys.stderr)
        return 0

    if not (settings.blender or config.find_blender()):
        p.error("Blender not found - use --blender PATH or set BLENDER_PATH")

    def on_event(e):
        if e["type"] == "log":
            print(e["message"], flush=True)
        elif e["type"] == "progress" and e["total"] and e["current"]:
            print(f"[{e['done']}/{e['total']}]", flush=True)

    runner = Runner(settings, on_event)
    try:
        summary = runner.run(sel)
    except KeyboardInterrupt:
        runner.stop()
        return 130
    return 1 if summary.get("error") else 0


if __name__ == "__main__":
    sys.exit(main())
