"""Image size probing from file headers (no Pillow needed) and texture path resolving."""

import os
import struct

# DDS / KTX compressed formats: bytes per pixel
_DDS_FOURCC = {b"DXT1": 0.5, b"DXT2": 1, b"DXT3": 1, b"DXT4": 1, b"DXT5": 1, b"ATI1": 0.5, b"ATI2": 1,
               b"BC4U": 0.5, b"BC5U": 1}


def probe(path):
    """-> dict(width, height, channels, fmt, bpp) or None. Reads only the header."""
    try:
        with open(path, "rb") as fh:
            head = fh.read(256)
            if head[:3] == b"\xff\xd8\xff":
                fh.seek(0)
                return _jpeg(fh)
    except OSError:
        return None
    return _from_header(head, os.path.splitext(path)[1].lower())


def probe_bytes(data, ext=""):
    if data[:3] == b"\xff\xd8\xff":
        import io
        return _jpeg(io.BytesIO(data))
    return _from_header(data[:256], ext)


def _from_header(h, ext):
    if h[:8] == b"\x89PNG\r\n\x1a\n" and len(h) >= 26:
        w, ht = struct.unpack(">II", h[16:24])
        ctype = h[25]
        ch = {0: 1, 2: 3, 3: 3, 4: 2, 6: 4}.get(ctype, 4)
        return dict(width=w, height=ht, channels=ch, fmt="png", bpp=None)
    if h[:2] == b"BM" and len(h) >= 30:
        w, ht = struct.unpack("<ii", h[18:26])
        bits = struct.unpack("<H", h[28:30])[0]
        return dict(width=abs(w), height=abs(ht), channels=4 if bits == 32 else 3, fmt="bmp", bpp=None)
    if h[:4] == b"DDS " and len(h) >= 88:
        ht, w = struct.unpack("<II", h[12:20])
        fourcc = h[84:88]
        bpp = _DDS_FOURCC.get(fourcc)
        return dict(width=w, height=ht, channels=4, fmt="dds" + (f"/{fourcc.decode('latin-1')}" if bpp else ""), bpp=bpp)
    if h[:6] in (b"GIF87a", b"GIF89a"):
        w, ht = struct.unpack("<HH", h[6:10])
        return dict(width=w, height=ht, channels=4, fmt="gif", bpp=None)
    if h[:2] == b"\x01\xda" and len(h) >= 12:  # SGI .rgb / .rgba / .int / .inta
        w, ht, zs = struct.unpack(">HHH", h[6:12])
        return dict(width=w, height=ht, channels=max(1, min(zs, 4)), fmt="sgi", bpp=None)
    if h[:12] == b"\xabKTX 11\xbb\r\n\x1a\n" and len(h) >= 44:
        endian = "<" if h[12:16] == b"\x01\x02\x03\x04" else ">"
        gl_type, = struct.unpack(endian + "I", h[16:20])
        w, ht = struct.unpack(endian + "II", h[36:44])
        return dict(width=w, height=ht, channels=4, fmt="ktx", bpp=(None if gl_type else 1))
    if h[:4] in (b"II*\x00", b"MM\x00*"):
        return _tiff(h)
    if ext == ".tga" and len(h) >= 18:
        w, ht = struct.unpack("<HH", h[12:16])
        bits = h[16]
        return dict(width=w, height=ht, channels=4 if bits == 32 else (1 if bits == 8 else 3), fmt="tga", bpp=None)
    return None


def _jpeg(fh):
    fh.read(2)
    while True:
        b = fh.read(1)
        while b and b != b"\xff":
            b = fh.read(1)
        while b == b"\xff":
            b = fh.read(1)
        if not b:
            return None
        marker = b[0]
        if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7:
            continue
        seg = fh.read(2)
        if len(seg) < 2:
            return None
        ln = struct.unpack(">H", seg)[0]
        if marker in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
            d = fh.read(6)
            if len(d) < 6:
                return None
            ht, w = struct.unpack(">HH", d[1:5])
            return dict(width=w, height=ht, channels=d[5], fmt="jpeg", bpp=None)
        fh.seek(ln - 2, 1)


def _tiff(h):
    e = "<" if h[:2] == b"II" else ">"
    try:
        off = struct.unpack(e + "I", h[4:8])[0]
        if off + 2 > len(h):
            return dict(width=0, height=0, channels=4, fmt="tiff", bpp=None)
        n = struct.unpack(e + "H", h[off:off + 2])[0]
        w = ht = 0
        spp = 3
        for i in range(n):
            p = off + 2 + i * 12
            if p + 12 > len(h):
                break
            tag, typ = struct.unpack(e + "HH", h[p:p + 4])
            val = struct.unpack(e + ("H" if typ == 3 else "I"), h[p + 8:p + (10 if typ == 3 else 12)])[0]
            if tag == 256:
                w = val
            elif tag == 257:
                ht = val
            elif tag == 277:
                spp = val
        return dict(width=w, height=ht, channels=spp, fmt="tiff", bpp=None)
    except struct.error:
        return None


def resolve(name, base_dir, search_dirs=()):
    """Find a texture / external file referenced from a scene file."""
    if not name:
        return None
    cand = name.replace("\\", "/")
    if os.path.isabs(cand) and os.path.isfile(cand):
        return cand
    dirs = [base_dir] + [d for d in search_dirs if d]
    for d in dirs:
        p = os.path.normpath(os.path.join(d, cand))
        if os.path.isfile(p):
            return p
    base = os.path.basename(cand)
    for d in dirs:
        for sub in ("", "textures", "Textures", "tex", "images", "maps", "..", "../textures"):
            p = os.path.normpath(os.path.join(d, sub, base))
            if os.path.isfile(p):
                return p
        # case-insensitive match in the directory itself
        try:
            low = base.lower()
            for f in os.listdir(d):
                if f.lower() == low:
                    return os.path.join(d, f)
        except OSError:
            pass
    return None


def fill_texture(tex, base_dir, search_dirs=()):
    """Resolve ``tex.file`` and read its size."""
    if tex.embedded:
        return tex
    path = resolve(tex.file, base_dir, search_dirs)
    tex.path = path
    tex.exists = path is not None
    if path:
        try:
            tex.file_bytes = os.path.getsize(path)
        except OSError:
            pass
        info = probe(path)
        if info:
            if not tex.width:
                tex.width, tex.height = info["width"], info["height"]
            tex.channels = info["channels"]
            tex.fmt = info["fmt"]
            tex.bpp = info["bpp"]
    return tex
