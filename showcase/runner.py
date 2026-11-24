"""Batch runner: one isolated Blender process per model.

Isolation means a model that crashes or hangs Blender only affects its own
record (status ``broken`` / ``error``); the batch keeps going.
"""

import collections
import datetime
import hashlib
import importlib.util
import json
import os
import platform
import re
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from concurrent.futures import ThreadPoolExecutor, as_completed

from . import __version__, config, index_db, netpaths, schema


def file_info(root, path, do_hash):
    st = netpaths.retry(os.stat, path)
    ext = os.path.splitext(path)[1].lower()
    info = {
        "path": os.path.abspath(path),
        "name": os.path.basename(path),
        "stem": os.path.splitext(os.path.basename(path))[0],
        "extension": ext,
        "format": schema.ALL_EXTENSIONS.get(ext, ext.lstrip(".").upper()),
        "size_bytes": st.st_size,
        "modified": datetime.datetime.fromtimestamp(st.st_mtime, datetime.timezone.utc).replace(microsecond=0).isoformat(),
        "root": os.path.abspath(root),
        "relative_path": os.path.relpath(path, root).replace("\\", "/"),
        "folder": os.path.relpath(os.path.dirname(path), root).replace("\\", "/"),
        "sha256": None,
    }
    info["network"] = netpaths.is_network_path(root)
    if do_hash:
        def digest():
            h = hashlib.sha256()
            with open(path, "rb") as fh:
                for chunk in iter(lambda: fh.read(4 << 20), b""):
                    h.update(chunk)
            return h.hexdigest()
        info["sha256"] = netpaths.retry(digest)
    return info


def root_key(root):
    """Readable, unique folder name for a library root (also for UNC paths like //server/share)."""
    key = re.sub(r"[\\/:]+", "_", os.path.normpath(os.path.abspath(root))).strip("_. ")
    return key[-100:] or "root"


# Windows (without the long-path opt-in) and Blender cannot write paths of 260+ characters;
# the longest file we write is "<model>.showcase.jpg.tmp.jpg".
WIN_MAX_PATH = 259
LONGEST_SUFFIX = len(".showcase.jpg.tmp.jpg") + 4


def too_long(prefix, system=None):
    return (system or platform.system()) == "Windows" and len(os.path.abspath(prefix)) + LONGEST_SUFFIX > WIN_MAX_PATH


def short_base(root, path, settings):
    """Short output prefix for models whose normal output path would be too long on Windows."""
    base = settings.fallback_output_dir or config.default_fallback_dir()
    digest = hashlib.sha1(os.path.abspath(path).encode("utf-8")).hexdigest()[:10]
    return os.path.join(base, root_key(root)[-40:], "_long_paths", f"{digest}_{os.path.basename(path)[-60:]}")


def fallback_base(root, path, settings):
    """Output prefix used when the model's own folder is read-only (e.g. a protected share)."""
    base = settings.fallback_output_dir or config.default_fallback_dir()
    out = os.path.join(base, root_key(root), os.path.relpath(path, root))
    return short_base(root, path, settings) if too_long(out) else out


def output_base(root, path, settings, check_writable=False):
    """Path prefix for all output files of a model (``<prefix>.info.json`` ...)."""
    if settings.output_mode == "mirror" and settings.output_dir:
        rel = os.path.relpath(path, root)
        out = os.path.join(settings.output_dir, os.path.basename(os.path.normpath(root)), rel)
        return short_base(root, path, settings) if too_long(out) else out
    if too_long(path):
        return fallback_base(root, path, settings)
    if check_writable and not netpaths.is_writable_dir(os.path.dirname(path)):
        return fallback_base(root, path, settings)
    return path


def candidate_bases(root, path, settings):
    """Where a model's results may live (normal location first, then the read-only fallback)."""
    first = output_base(root, path, settings)
    bases = [first] if first != path else [path, fallback_base(root, path, settings)]
    if platform.system() == "Windows":
        short = short_base(root, path, settings)
        if short not in bases:
            bases.append(short)
    return bases


def find_record(root, path, settings):
    """(record, output prefix) of an already processed model, or (None, normal prefix)."""
    bases = candidate_bases(root, path, settings)
    for b in bases:
        rec = load_record(b + schema.SUFFIX_JSON)
        if rec is not None:
            return rec, b
    return None, bases[0]


def index_path_for(root, settings):
    if settings.index_path:
        return settings.index_path
    if settings.output_mode == "mirror" and settings.output_dir:
        return os.path.join(settings.output_dir, index_db.INDEX_NAME)
    if not netpaths.is_writable_dir(root):
        return os.path.join(settings.fallback_output_dir or config.default_fallback_dir(), root_key(root), index_db.INDEX_NAME)
    return os.path.join(root, index_db.INDEX_NAME)


def assimp_python():
    """This interpreter, if it has the assimp_py package (used as converter for rare formats)."""
    try:
        return sys.executable if importlib.util.find_spec("assimp_py") and importlib.util.find_spec("numpy") else ""
    except (ImportError, ValueError):
        return ""


def load_record(json_path):
    try:
        with open(json_path, encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


def is_up_to_date(json_path, path, settings):
    rec = load_record(json_path)
    if not rec:
        return False, None
    f = rec.get("file", {})
    try:
        st = os.stat(path)
    except OSError:
        return False, rec
    try:
        recorded = datetime.datetime.fromisoformat(f.get("modified", "")).timestamp()
    except (TypeError, ValueError):
        return False, rec
    # network file systems (SMB/FAT) keep timestamps with ~2 s precision
    if f.get("size_bytes") != st.st_size or abs(recorded - int(st.st_mtime)) > 2:
        return False, rec
    if settings.retry_errors and rec.get("status") == "error":
        return False, rec
    folder = os.path.dirname(json_path)
    sheet = rec.get("render", {}).get("sheet_image")
    if rec.get("status") not in ("broken", "unsupported") and not (sheet and os.path.exists(os.path.join(folder, sheet))):
        return False, rec
    return True, rec


class Runner:
    """Runs a batch. ``on_event(dict)`` receives:

    {"type": "log", "message": str}
    {"type": "progress", "done": int, "total": int, "current": str}
    {"type": "result", "path": str, "record": dict, "skipped": bool, "json": str}
    {"type": "finished", "summary": dict, "cancelled": bool}
    """

    def __init__(self, settings, on_event=None):
        self.settings = settings
        self.on_event = on_event or (lambda e: None)
        self.stop_event = threading.Event()
        self._procs = set()
        self._lock = threading.Lock()
        self._indexes = {}
        self._readonly_logged = set()
        self._offline = {}
        self._index_errors = set()
        self._assimp_python = assimp_python()

    # -- helpers ---------------------------------------------------------------
    def log(self, msg):
        self.on_event({"type": "log", "message": msg})

    def stop(self):
        self.stop_event.set()
        with self._lock:
            for p in list(self._procs):
                try:
                    p.kill()
                except OSError:
                    pass

    def _index(self, root):
        path = index_path_for(root, self.settings)
        with self._lock:
            if path not in self._indexes:
                self._indexes[path] = index_db.Index(path)
            return self._indexes[path]

    # -- one model -------------------------------------------------------------
    def _run_worker(self, path, out_base, worker_settings, tmpdir, timeout):
        """Run Blender once. Returns (result_dict_or_None, returncode, timed_out, output_tail)."""
        tag = uuid.uuid4().hex[:10]
        settings_file = os.path.join(tmpdir, f"{tag}.settings.json")
        result_file = os.path.join(tmpdir, f"{tag}.result.json")
        with open(settings_file, "w", encoding="utf-8") as fh:
            json.dump(worker_settings, fh)
        cmd = config.blender_command(self.settings, [
            "--input", path, "--out-base", out_base, "--result", result_file, "--settings", settings_file])
        tail = collections.deque(maxlen=40)
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                text=True, encoding="utf-8", errors="replace", creationflags=flags)
        with self._lock:
            self._procs.add(proc)
        timed_out = threading.Event()

        def kill():
            timed_out.set()
            proc.kill()

        timer = threading.Timer(timeout, kill)
        timer.daemon = True
        timer.start()
        name = os.path.basename(path)
        try:
            for line in proc.stdout:
                line = line.rstrip()
                if not line or line.startswith("Fra:"):
                    continue
                tail.append(line)
                if line.startswith("SHOWCASE: "):
                    self.log(f"  [{name}] {line[10:]}")
            rc = proc.wait()
        finally:
            timer.cancel()
            with self._lock:
                self._procs.discard(proc)
        result = load_record(result_file)
        for f in (settings_file, result_file):
            try:
                os.remove(f)
            except OSError:
                pass
        return result, rc, timed_out.is_set(), list(tail)

    def process(self, root, path, tmpdir):
        s = self.settings
        if s.skip_existing:
            for base in candidate_bases(root, path, s):
                ok, rec = is_up_to_date(base + schema.SUFFIX_JSON, path, s)
                if ok:
                    return rec, base + schema.SUFFIX_JSON, True
        out_base = output_base(root, path, s, check_writable=True)
        if out_base != path and s.output_mode != "mirror":
            folder = os.path.dirname(path)
            with self._lock:
                first = folder not in self._readonly_logged
                self._readonly_logged.add(folder)
            if first:
                self.log(f"Folder is read-only (or the path is too long for Windows), results go to {os.path.dirname(out_base)}")
        json_path = out_base + schema.SUFFIX_JSON
        netpaths.retry(os.makedirs, os.path.dirname(out_base), exist_ok=True)
        t0 = time.time()
        ext = os.path.splitext(path)[1].lower()
        if ext in schema.RECORD_ONLY_EXTENSIONS:
            # proprietary format: inventory record only, no Blender run
            record = {"issues": [schema.make_issue(
                "unsupported_format",
                f"{schema.RECORD_ONLY_EXTENSIONS[ext]} files can only be opened in their original application; "
                "export them to FBX / glTF / OBJ to get a preview")], "import": {}}
            return self._finish(root, path, out_base, json_path, record, t0)
        size = netpaths.retry(os.path.getsize, path)
        timeout = s.timeout + 60 * (size // (50 * 1024 * 1024))
        if netpaths.is_network_path(path):
            timeout = int(timeout * 1.5)  # slower reads (model, textures, references) over the network
        worker_settings = s.worker_settings()
        for key, tool in (("assimp_path", "assimp"), ("osgconv_path", "osgconv")):
            worker_settings[key] = config.find_tool(worker_settings.get(key), tool)
        worker_settings["assimp_python"] = self._assimp_python

        result, rc, timed_out, tail = self._run_worker(path, out_base, worker_settings, tmpdir, timeout)
        fallback = False
        render_problem = result is not None and (
            result.get("stage") == "render"  # Blender died while rendering (e.g. no GPU / EGL)
            or any(i.get("code") == "render_failed" for i in result.get("issues", [])))
        if render_problem and not timed_out and not self.stop_event.is_set() and s.engine != "cycles":
            self.log(f"  [{os.path.basename(path)}] rendering failed with {s.engine}; retrying with Cycles")
            worker_settings = dict(worker_settings, engine="cycles")
            result, rc, timed_out, tail = self._run_worker(path, out_base, worker_settings, tmpdir, timeout)
            fallback = True

        if self.stop_event.is_set() and (result is None or result.get("stage") != "done"):
            return None, json_path, False  # cancelled by the user: leave no record
        record = result or {"issues": []}
        record.pop("settings", None)
        stage = record.pop("stage", None)
        if timed_out:
            schema.add_issue(record, "timeout", f"no result after {timeout}s (stage: {stage or 'import'})")
        elif result is None or stage not in (None, "done"):
            where = "rendering" if stage == "render" else "import/analysis"
            code = "render_failed" if stage == "render" else "blender_crash"
            schema.add_issue(record, code, f"Blender exited with code {rc} during {where}")
            record["log_tail"] = tail[-15:]
        if fallback:
            schema.add_issue(record, "engine_fallback")
        return self._finish(root, path, out_base, json_path, record, t0)

    def _finish(self, root, path, out_base, json_path, record, t0):
        s = self.settings
        info = file_info(root, path, s.hash_files)
        record["file"] = info
        record["id"] = info["sha256"] or hashlib.sha256(info["path"].encode("utf-8")).hexdigest()
        record["tool_version"] = __version__
        record["processed_at"] = schema.now_iso()
        record["processing_seconds"] = round(time.time() - t0, 3)
        record.setdefault("import", {})
        schema.finalize(record)
        ordered = {k: record[k] for k in ("schema_version", "tool_version", "id", "status", "issues", "file")
                   if k in record}
        ordered.update({k: v for k, v in record.items() if k not in ordered})
        netpaths.write_text_atomic(json_path, schema.dumps(ordered))
        netpaths.write_text_atomic(out_base + schema.SUFFIX_TXT, schema.format_report(ordered))
        return ordered, json_path, False

    # -- batch -------------------------------------------------------------------
    def run(self, selection):
        s = self.settings
        self.log("Scanning folders ...")
        skip_dirs = [s.output_dir] if s.output_mode == "mirror" else []
        unreadable = []
        models = list(selection.iter_models(self.stop_event, skip_dirs=skip_dirs, on_error=unreadable.append))
        for err in unreadable[:20]:
            self.log(f"Cannot read folder: {err}")
        if len(unreadable) > 20:
            self.log(f"... and {len(unreadable) - 20} more unreadable folders")
        for root in selection.roots:
            if netpaths.is_network_path(root):
                self.log(f"Network folder: {root}")
        if not s.record_unsupported:
            models = [m for m in models if os.path.splitext(m[1])[1].lower() not in schema.RECORD_ONLY_EXTENSIONS]
        total = len(models)
        self.log(f"{total} model file(s) selected")
        summary = collections.Counter()
        done = 0
        tmpdir = tempfile.mkdtemp(prefix="showcase_run_")
        self.on_event({"type": "progress", "done": 0, "total": total, "current": ""})
        try:
            with ThreadPoolExecutor(max_workers=max(1, int(s.jobs))) as pool:
                futures = {}
                for root, path in models:
                    futures[pool.submit(self._safe_process, root, path, tmpdir)] = (root, path)
                for fut in as_completed(futures):
                    root, path = futures[fut]
                    rec, json_path, skipped = fut.result()
                    done += 1
                    if rec is not None:
                        summary["skipped" if skipped else rec.get("status", "?")] += 1
                        try:
                            idx = self._index(root)
                            idx.upsert(rec, json_path)
                            if done % 200 == 0:
                                idx.publish()  # keep the copy on a network share reasonably fresh
                        except Exception as exc:  # index problems must not stop rendering
                            key = index_path_for(root, s)
                            if key not in self._index_errors:
                                self._index_errors.add(key)
                                self.log(f"Index update failed ({key}): {exc} - further index errors are not logged; "
                                         "rebuild later with --reindex")
                        self.on_event({"type": "result", "path": path, "record": rec,
                                       "skipped": skipped, "json": json_path})
                        if not skipped:
                            self.log(f"{rec.get('status', '?').upper():<8} {path}")
                    self.on_event({"type": "progress", "done": done, "total": total, "current": path})
                    if self.stop_event.is_set():
                        for f in futures:
                            f.cancel()
        finally:
            for idx in self._indexes.values():
                try:
                    csv_path = idx.export_csv()
                    idx.close()
                    self.log(f"Index: {idx.path}  (CSV: {csv_path})")
                except Exception as exc:
                    kept = f" A working copy is kept at {idx.work_path}." if idx.work_path != idx.path else ""
                    self.log(f"Could not write index {idx.path}: {exc}.{kept} Rebuild it later with --reindex.")
            self._indexes.clear()
            try:
                os.rmdir(tmpdir)
            except OSError:
                pass
        cancelled = self.stop_event.is_set()
        self.log(("Cancelled. " if cancelled else "Finished. ") + ", ".join(f"{k}: {v}" for k, v in sorted(summary.items())))
        self.on_event({"type": "finished", "summary": dict(summary), "cancelled": cancelled})
        return dict(summary)

    def _wait_for_source(self, root, path):
        """A network share may drop out for a moment: wait a bit before giving up.

        If the library root stays unreachable it is marked offline for a minute, so the
        remaining files fail fast (status "error", retried on the next run) instead of
        each waiting again."""
        with self._lock:
            since = self._offline.get(root)
        if since and time.time() - since < 60:
            return False
        for attempt in range(6):
            if self.stop_event.is_set():
                return False
            if netpaths.reachable(root, timeout=10):
                with self._lock:
                    self._offline.pop(root, None)
                return os.path.exists(path)  # share is there: the file exists or is really gone
            if attempt == 0:
                self.log(f"Library folder not reachable ({root}) - waiting up to a minute ...")
            self.stop_event.wait(10)
        with self._lock:
            self._offline[root] = time.time()
        self.log(f"Library folder offline: {root} - its models are marked 'error' and retried next run")
        return False

    def _source_unavailable(self, root, path):
        out_base = output_base(root, path, self.settings)
        rec = schema.finalize({"issues": [schema.make_issue("source_unavailable", f"could not access {path}")],
                               "file": {"path": path, "name": os.path.basename(path), "root": root,
                                        "relative_path": os.path.relpath(path, root).replace("\\", "/")},
                               "processed_at": schema.now_iso(), "tool_version": __version__})
        rec["id"] = hashlib.sha256(path.encode("utf-8")).hexdigest()
        try:
            netpaths.write_text_atomic(out_base + schema.SUFFIX_JSON, schema.dumps(rec))
        except OSError:
            pass
        return rec, out_base + schema.SUFFIX_JSON, False

    def _safe_process(self, root, path, tmpdir):
        if self.stop_event.is_set():
            return None, None, False
        if not os.path.exists(path) and not self._wait_for_source(root, path):
            if self.stop_event.is_set():
                return None, None, False
            self.log(f"ERROR source not available: {path}")
            return self._source_unavailable(root, path)
        try:
            return self.process(root, path, tmpdir)
        except config.BlenderNotFound as exc:
            self.log(f"ERROR {exc}")
            self.stop()
            return None, None, False
        except Exception as exc:
            if isinstance(exc, OSError) and not os.path.exists(path):  # vanished mid-run (share dropped)
                self.log(f"ERROR source disappeared while processing: {path}")
                return self._source_unavailable(root, path)
            self.log(f"ERROR processing {path}: {type(exc).__name__}: {exc}")
            return None, None, False
