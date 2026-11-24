#!/usr/bin/env python3
"""Write a small OpenFlight test scene to check what an image generator / viewer supports.

    python tools/make_compat_scene.py OUT_DIR        (needs Pillow for the textures)

Load OUT_DIR/compat.flt in the target application. Left to right along +X, 15 m apart, all standing on z = 0:

lodtest  a box under an LOD: red up to 60 m, green 60-200 m, blue 200-2000 m. Move away from it: the colour must
         change at those distances (and you can watch how visible the switch is).
bbtree   a tree as ONE billboard face (template "axial rotate", alpha texture): it must turn to face you around
         its vertical axis, with the background visible between the leaves.
xtree    the same tree as two crossed fixed cards (alpha texture, no back-face culling): works without billboards.
bush     a low-poly core with five billboard cards around it (the kind of replacement for bushes).
tex256, tex1024, tex4096
         panels with SGI .rgb textures of 256, 1024 and 4096 pixels (a checker with the size written on it):
         shows whether large textures load and how fine detail looks.
texpng   a panel with a PNG texture: shows whether the application reads PNG at all (baked textures could be
         written as .rgb or .png).

README.txt in OUT_DIR repeats this list.
"""

import os
import struct
import sys

from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from make_test_flt import PUSH, POP, FltWriter, rec  # noqa: E402

# colour palette entries (a, b, g, r packed like the writer does); face colour index = entry * 128 + 127
RED, GREEN, BLUE, WHITE, BROWN, LEAF = 1, 2, 3, 4, 5, 6
PALETTE = {RED: 0xFF2020E0, GREEN: 0xFF20C020, BLUE: 0xFFE04020, WHITE: 0xFFFFFFFF, BROWN: 0xFF204070, LEAF: 0xFF308040}

TEXTURES = ["textures/tree.rgba", "textures/bush.rgba", "textures/panel256.rgb", "textures/panel1024.rgb",
            "textures/panel4096.rgb", "textures/panel.png"]
TREE, BUSH, P256, P1024, P4096, PPNG = range(6)

FIXED_ALPHA, AXIAL_ROTATE = 1, 2      # face record "template" (billboard) values
NO_CULL = 1                           # face record draw type: solid, both sides


def col(entry):
    return entry * 128 + 127


class CompatWriter(FltWriter):
    def palettes(self, textures, materials):
        out = bytearray()
        cols = [0] * 1024
        for k, v in PALETTE.items():
            cols[k] = v
        out += rec(32, b"\0" * 128 + struct.pack(">1024I", *cols))
        for i, t in enumerate(textures):
            out += rec(64, t.encode().ljust(200, b"\0") + struct.pack(">iii", i, 0, 0))
        return out

    def face(self, vidx, tex=-1, mat=-1, color_index=col(WHITE), template=0, draw_type=0):
        b = bytearray(76)
        b[0:8] = b"face\0\0\0\0"
        b[14] = draw_type                            # draw type (record offset 18)
        b[21] = template                             # template / billboard (record offset 25)
        struct.pack_into(">h", b, 24, tex)           # texture pattern (record offset 28)
        struct.pack_into(">h", b, 26, mat)           # material index (record offset 30)
        struct.pack_into(">I", b, 64, color_index)   # primary colour index (record offset 68)
        self.body += rec(5, bytes(b))
        self.body += rec(72, b"".join(struct.pack(">i", 8 + 48 * i) for i in vidx))

    def card(self, cx, cy, w, h, ax, ay, **kw):
        """Upright quad of width w and height h centred on (cx, cy), spanning direction (ax, ay), uv 0..1."""
        dx, dy = ax * w / 2, ay * w / 2
        self.quad((cx - dx, cy - dy, 0), (cx + dx, cy + dy, 0), (cx + dx, cy + dy, h), (cx - dx, cy - dy, h), **kw)


# -- textures -------------------------------------------------------------------------------------------------

def write_sgi(img, path):
    """Uncompressed SGI .rgb / .rgba (rows bottom up, one plane per channel)."""
    img = img.convert("RGBA" if path.endswith(".rgba") else "RGB")
    w, h = img.size
    z = len(img.getbands())
    head = struct.pack(">hbbHHHHii", 474, 0, 1, 3, w, h, z, 0, 255).ljust(512, b"\0")
    flipped = img.transpose(Image.FLIP_TOP_BOTTOM)
    with open(path, "wb") as fh:
        fh.write(head)
        for band in flipped.split():
            fh.write(band.tobytes())


def tree_image(size, bush=False):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = size
    if not bush:
        d.rectangle([s * 0.46, s * 0.55, s * 0.54, s], fill=(90, 60, 30, 255))   # trunk
        blobs = [(0.5, 0.3, 0.28), (0.32, 0.45, 0.2), (0.68, 0.45, 0.2), (0.5, 0.5, 0.22)]
    else:
        blobs = [(0.5, 0.55, 0.3), (0.3, 0.65, 0.22), (0.7, 0.65, 0.22), (0.5, 0.4, 0.22)]
    for i, (x, y, r) in enumerate(blobs):
        g = 110 + 30 * (i % 3)
        d.ellipse([s * (x - r), s * (y - r), s * (x + r), s * (y + r)], fill=(40, g, 40, 255))
    for k in range(60):   # a few holes so the cut-out is obvious
        x, y = (k * 37 % 100) / 100, (k * 61 % 100) / 100
        d.ellipse([s * x, s * y, s * x + s * 0.03, s * y + s * 0.03], fill=(0, 0, 0, 0))
    return img


def panel_image(size, label):
    img = Image.new("RGB", (size, size))
    d = ImageDraw.Draw(img)
    cells = 16
    c = size / cells
    for i in range(cells):
        for j in range(cells):
            d.rectangle([i * c, j * c, (i + 1) * c - 1, (j + 1) * c - 1], fill=(235, 235, 235) if (i + j) % 2 else (40, 70, 160))
    d.line([0, 0, size, size], fill=(220, 40, 40), width=max(1, size // 128))   # fine diagonal: shows filtering
    font = ImageFont.load_default(size=max(10, size // 6))
    d.rectangle([size * 0.1, size * 0.36, size * 0.9, size * 0.64], fill=(255, 255, 255))
    d.text((size / 2, size / 2), label, fill=(0, 0, 0), font=font, anchor="mm")
    d.text((size * 0.05, size * 0.88), "top up", fill=(220, 40, 40), font=ImageFont.load_default(size=max(8, size // 16)))
    return img


# -- scene ------------------------------------------------------------------------------------------------------

def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    out = sys.argv[1]
    os.makedirs(os.path.join(out, "textures"), exist_ok=True)
    write_sgi(tree_image(512), os.path.join(out, TEXTURES[TREE]))
    write_sgi(tree_image(256, bush=True), os.path.join(out, TEXTURES[BUSH]))
    for size, idx in ((256, P256), (1024, P1024), (4096, P4096)):
        write_sgi(panel_image(size, f"{size} px"), os.path.join(out, TEXTURES[idx]))
    panel_image(512, "PNG").save(os.path.join(out, TEXTURES[PPNG]))

    w = CompatWriter()
    w.group("compat")
    w.push()

    # LOD: switch-in = far distance, switch-out = near distance
    w.group("lodtest")
    w.push()
    for name, near, far, colour in (("lodnear", 0.0, 60.0, RED), ("lodmid", 60.0, 200.0, GREEN), ("lodfar", 200.0, 2000.0, BLUE)):
        w.lod(name, far, near, (2.0, 2.0, 2.0))
        w.push()
        w.obj(name)
        w.push()
        w.box(0, 0, 4, 4, 4, color_index=col(colour))
        w.pop()
        w.pop()
    w.pop()

    w.group("bbtree")
    w.push()
    w.obj("bbtree")
    w.push()
    w.box(14.8, -0.2, 0.4, 0.4, 3, color_index=col(BROWN))
    w.card(15, 0, 6, 8, 1, 0, tex=TREE, template=AXIAL_ROTATE, draw_type=NO_CULL)
    w.pop()
    w.pop()

    w.group("xtree")
    w.push()
    w.obj("xtree")
    w.push()
    w.card(30, 0, 6, 8, 1, 0, tex=TREE, template=FIXED_ALPHA, draw_type=NO_CULL)
    w.card(30, 0, 6, 8, 0, 1, tex=TREE, template=FIXED_ALPHA, draw_type=NO_CULL)
    w.pop()
    w.pop()

    w.group("bush")
    w.push()
    w.obj("bushcore")
    w.push()
    w.box(44.2, -0.8, 1.6, 1.6, 1.2, color_index=col(LEAF))
    w.pop()
    w.obj("bushbb")
    w.push()
    for k, (ox, oy) in enumerate(((0, 0), (0.9, 0.5), (-0.9, 0.5), (0.6, -0.8), (-0.6, -0.8))):
        w.card(45 + ox, oy, 2.5, 2.0, 1, 0, tex=BUSH, template=AXIAL_ROTATE, draw_type=NO_CULL)
    w.pop()
    w.pop()

    for k, (name, idx) in enumerate((("tex256", P256), ("tex1024", P1024), ("tex4096", P4096), ("texpng", PPNG))):
        w.group(name)
        w.push()
        w.obj(name)
        w.push()
        w.card(60 + 6 * k, 0, 4, 4, 1, 0, tex=idx, draw_type=NO_CULL)
        w.pop()
        w.pop()

    w.pop()
    size = w.save(os.path.join(out, "compat.flt"), "compat", TEXTURES, [])
    with open(os.path.join(out, "README.txt"), "w", encoding="utf-8") as fh:
        fh.write(__doc__.split("Load ", 1)[1].replace("OUT_DIR/", ""))
    print(f"compat.flt: {size / 1e3:.0f} KB, textures in {os.path.join(out, 'textures')}")


if __name__ == "__main__":
    main()
