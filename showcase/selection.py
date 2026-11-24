"""Which folders / files are included in a run.

Model: every path is *included* unless an explicit override on the path or its
nearest ancestor says otherwise. Toggling a folder clears the overrides below
it, so "exclude this folder" and then "include this one file inside it" both
work, and the state stays small even for huge libraries (no enumeration).

A folder containing a file named ``.noshowcase`` is excluded by default.
"""

import os

from . import schema

IGNORE_MARKER = ".noshowcase"
SKIP_DIRS = {".git", ".svn", "__pycache__", "$RECYCLE.BIN", "System Volume Information"}


def norm(path):
    return os.path.normcase(os.path.normpath(os.path.abspath(path)))


def is_model_file(name):
    return os.path.splitext(name)[1].lower() in schema.ALL_EXTENSIONS


class Selection:
    def __init__(self, roots=(), overrides=None):
        self.roots = []
        self.overrides = {}
        self._marker_cache = {}
        for r in roots:
            self.add_root(r)
        for p, v in (overrides or {}).items():
            self.overrides[norm(p)] = bool(v)

    # -- roots ------------------------------------------------------------
    def add_root(self, path):
        path = os.path.abspath(path)
        if norm(path) not in {norm(r) for r in self.roots}:
            self.roots.append(path)

    def remove_root(self, path):
        n = norm(path)
        self.roots = [r for r in self.roots if norm(r) != n]
        prefix = n + os.sep
        self.overrides = {k: v for k, v in self.overrides.items() if k != n and not k.startswith(prefix)}

    def root_of(self, path):
        n = norm(path)
        best = None
        for r in self.roots:
            rn = norm(r)
            if n == rn or n.startswith(rn.rstrip(os.sep) + os.sep):
                if best is None or len(rn) > len(norm(best)):
                    best = r
        return best

    # -- state ------------------------------------------------------------
    def explicit(self, path):
        return self.overrides.get(norm(path))

    def is_included(self, path):
        n = norm(path)
        root = self.root_of(path)
        stop = norm(root) if root else None
        cur = n
        while True:
            if cur in self.overrides:
                return self.overrides[cur]
            if self._has_marker(cur):
                return False
            if cur == stop:
                return True
            parent = os.path.dirname(cur)
            if parent == cur:
                return True
            cur = parent

    def _has_marker(self, folder):
        hit = self._marker_cache.get(folder)
        if hit is None:
            hit = self._marker_cache[folder] = os.path.isfile(os.path.join(folder, IGNORE_MARKER))
        return hit

    def set(self, path, included):
        n = norm(path)
        prefix = n.rstrip(os.sep) + os.sep
        for k in [k for k in self.overrides if k.startswith(prefix)]:
            del self.overrides[k]
        # only store an override when it differs from the inherited state
        self.overrides.pop(n, None)
        if self.is_included(n) != bool(included):
            self.overrides[n] = bool(included)

    def toggle(self, path):
        self.set(path, not self.is_included(path))

    def has_included_below(self, path):
        prefix = norm(path).rstrip(os.sep) + os.sep
        return any(v and k.startswith(prefix) for k, v in self.overrides.items())

    def has_excluded_below(self, path):
        prefix = norm(path).rstrip(os.sep) + os.sep
        return any((not v) and k.startswith(prefix) for k, v in self.overrides.items())

    def dir_state(self, path):
        """'on', 'off' or 'partial' for a folder (for tri-state checkboxes)."""
        inc = self.is_included(path)
        if inc and self.has_excluded_below(path):
            return "partial"
        if not inc and self.has_included_below(path):
            return "partial"
        return "on" if inc else "off"

    # -- enumeration --------------------------------------------------------
    def iter_models(self, stop=None, skip_dirs=(), on_error=None):
        """Yield (root, model_path) for every included model file.

        ``on_error(msg)`` is called for folders that cannot be read (permissions,
        disconnected network share, ...); they are skipped."""
        def walk_error(exc):
            if on_error is not None:
                on_error(f"{getattr(exc, 'filename', '')}: {exc.strerror or exc}")

        skip = {norm(d) for d in skip_dirs if d}
        self._marker_cache.clear()
        for root in self.roots:
            if not os.path.isdir(root):
                walk_error(OSError(2, "library folder not reachable", root))
                continue
            for dirpath, dirnames, filenames in os.walk(root, onerror=walk_error):
                if stop is not None and stop.is_set():
                    return
                dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS and not d.startswith("."))
                keep = []
                for d in dirnames:
                    full = os.path.join(dirpath, d)
                    if norm(full) in skip:
                        continue
                    if self.is_included(full) or self.has_included_below(full):
                        keep.append(d)
                dirnames[:] = keep
                for f in sorted(filenames):
                    if is_model_file(f):
                        full = os.path.join(dirpath, f)
                        if self.is_included(full):
                            yield root, full

    def to_overrides(self):
        return dict(self.overrides)
