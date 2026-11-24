"""Helpers for network folders (UNC paths, mapped drives, SMB/NFS/AFP mounts).

Network shares behave differently from local disks: they can be slow,
read-only, briefly unreachable, have coarse timestamps, and SQLite's file
locking is unreliable on them. The runner uses these helpers to cope.
"""

import os
import platform
import re
import subprocess
import threading
import time
import urllib.parse

NETWORK_FS = {"cifs", "smb", "smb2", "smb3", "smbfs", "nfs", "nfs4", "afpfs", "webdav", "davfs", "fuse.sshfs",
              "sshfs", "9p", "fuse.rclone", "ncpfs", "fuse.gvfsd-fuse", "fuse.gvfs-fuse-daemon", "osxfuse", "macfuse"}

_mount_cache = None
_mount_lock = threading.Lock()


def _posix_mounts():
    """[(mount_point, fs_type)] sorted longest mount point first."""
    global _mount_cache
    with _mount_lock:
        if _mount_cache is not None:
            return _mount_cache
        mounts = []
        try:
            if os.path.exists("/proc/mounts"):
                with open("/proc/mounts", encoding="utf-8", errors="replace") as fh:
                    for line in fh:
                        parts = line.split()
                        if len(parts) >= 3:
                            mounts.append((parts[1].replace("\\040", " "), parts[2].lower()))
            else:  # macOS / BSD: "//user@server/share on /Volumes/share (smbfs, ...)"
                out = subprocess.run(["mount"], capture_output=True, text=True, timeout=10).stdout
                for line in out.splitlines():
                    m = re.match(r".+? on (.+) \(([^,)]+)", line)
                    if m:
                        mounts.append((m.group(1), m.group(2).lower()))
        except (OSError, subprocess.SubprocessError):
            pass
        _mount_cache = sorted(mounts, key=lambda m: -len(m[0]))
        return _mount_cache


def is_network_path(path):
    path = os.path.abspath(path)
    if platform.system() == "Windows":
        if path.startswith("\\\\"):
            return True
        try:
            import ctypes
            drive = os.path.splitdrive(path)[0] + "\\"
            return ctypes.windll.kernel32.GetDriveTypeW(drive) == 4  # DRIVE_REMOTE
        except Exception:
            return False
    for mount, fstype in _posix_mounts():
        if path == mount or path.startswith(mount.rstrip("/") + "/"):
            return fstype in NETWORK_FS or fstype.startswith("fuse.") and "gvfs" in fstype
    return False


def resolve_user_path(text):
    """Turn what a user typed/pasted into a filesystem path.

    Accepts normal paths, ``~``, environment variables, ``file://`` URLs and
    ``smb://server/share/dir`` (mapped to \\\\server\\share on Windows, the
    GVFS mount on Linux, /Volumes/share on macOS). Raises ValueError with a
    helpful message when the location is not reachable.
    """
    text = os.path.expandvars(os.path.expanduser(text.strip().strip('"')))
    if text.lower().startswith("file://"):
        text = urllib.parse.unquote(urllib.parse.urlparse(text).path)
        if platform.system() == "Windows" and re.match(r"^/[A-Za-z]:", text):
            text = text[1:]
    elif re.match(r"^(smb|cifs)://", text, re.I):
        u = urllib.parse.urlparse(text)
        server = u.hostname or ""
        parts = [p for p in urllib.parse.unquote(u.path).split("/") if p]
        if not parts:
            raise ValueError("an smb:// address needs a share name, e.g. smb://server/share/models")
        share, rest = parts[0], parts[1:]
        candidates = []
        if platform.system() == "Windows":
            candidates.append("\\\\" + "\\".join([server, share] + rest))
        elif platform.system() == "Darwin":
            candidates += [os.path.join("/Volumes", share, *rest)]
            candidates += [os.path.join(v, *rest) for v in _glob_volumes(share)]
        else:
            uid = os.getuid()
            gvfs = f"/run/user/{uid}/gvfs"
            if os.path.isdir(gvfs):
                for d in os.listdir(gvfs):
                    if d.startswith("smb-share:") and f"server={server.lower()}" in d.lower() and f"share={share.lower()}" in d.lower():
                        candidates.append(os.path.join(gvfs, d, *rest))
            for mount, fstype in _posix_mounts():
                if fstype in NETWORK_FS and os.path.basename(mount.rstrip("/")).lower() == share.lower():
                    candidates.append(os.path.join(mount, *rest))
        for c in candidates:
            if reachable(c):
                return os.path.abspath(c)
        raise ValueError(f"'{text}' is not mounted. Open/connect the share in your file manager first "
                         "(or mount it), then add the mounted folder.")
    if not reachable(text):
        raise ValueError(f"Folder not reachable: {text}")
    return os.path.abspath(text)


def _glob_volumes(share):
    try:
        return [os.path.join("/Volumes", d) for d in os.listdir("/Volumes") if d.lower().startswith(share.lower())]
    except OSError:
        return []


def reachable(path, timeout=15.0):
    """os.path.isdir with a timeout (an offline UNC path can block for a long time)."""
    result = {}

    def probe():
        result["ok"] = os.path.isdir(path)

    t = threading.Thread(target=probe, daemon=True)
    t.start()
    t.join(timeout)
    return bool(result.get("ok"))


_writable_cache = {}


def is_writable_dir(folder):
    """True when files can be created in ``folder`` (tested once per folder)."""
    folder = os.path.abspath(folder)
    if folder in _writable_cache:
        return _writable_cache[folder]
    ok = False
    probe = os.path.join(folder, f".showcase_write_test_{os.getpid()}_{threading.get_ident()}")
    try:
        with open(probe, "wb") as fh:
            fh.write(b"x")
        os.remove(probe)
        ok = True
    except OSError:
        ok = False
    _writable_cache[folder] = ok
    return ok


def retry(fn, *args, attempts=4, delay=1.5, exceptions=(OSError,), **kwargs):
    """Call ``fn`` and retry on transient I/O errors (network hiccups, files locked by scanners)."""
    for i in range(attempts):
        try:
            return fn(*args, **kwargs)
        except exceptions:
            if i == attempts - 1:
                raise
            time.sleep(delay * (i + 1))


def write_text_atomic(path, text):
    def _write():
        tmp = f"{path}.{os.getpid()}.tmp"
        with open(tmp, "w", encoding="utf-8") as fh:
            fh.write(text)
        try:
            os.replace(tmp, path)
        except OSError:
            # some SMB servers refuse replace over an open/locked file: fall back to delete + rename
            try:
                os.remove(path)
            except OSError:
                pass
            os.replace(tmp, path)
    retry(_write)
