"""Side-by-side sheets + image metrics (PSNR, block SSIM, changed pixels) for tools/render_variants.py.

    python tools/compare_renders.py <render folder with results.json> <output folder>

Needs numpy and Pillow (both in the .venv the launcher creates).
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

R = sys.argv[1]
OUT = sys.argv[2]
os.makedirs(OUT, exist_ok=True)
res = json.load(open(os.path.join(R, "results.json")))
try:
    FONT = ImageFont.truetype("DejaVuSans.ttf", 15)
    FONTB = ImageFont.truetype("DejaVuSans-Bold.ttf", 17)
except OSError:
    FONT = FONTB = ImageFont.load_default()


def load(p):
    return np.asarray(Image.open(p).convert("RGB"), dtype=np.float64)


def ssim(a, b):
    """Mean SSIM on luminance with 8x8 blocks."""
    ya = a @ [0.299, 0.587, 0.114]
    yb = b @ [0.299, 0.587, 0.114]
    h, w = (ya.shape[0] // 8) * 8, (ya.shape[1] // 8) * 8
    A = ya[:h, :w].reshape(h // 8, 8, w // 8, 8)
    B = yb[:h, :w].reshape(h // 8, 8, w // 8, 8)
    ma, mb = A.mean((1, 3)), B.mean((1, 3))
    va, vb = A.var((1, 3)), B.var((1, 3))
    cov = ((A - ma[:, None, :, None]) * (B - mb[:, None, :, None])).mean((1, 3))
    c1, c2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    s = ((2 * ma * mb + c1) * (2 * cov + c2)) / ((ma ** 2 + mb ** 2 + c1) * (va + vb + c2))
    return float(s.mean())


def metrics(a, b, mask=None):
    d = np.abs(a - b).max(axis=2)
    if mask is not None:
        d = d[mask]
    mse = float(np.mean((a - b) ** 2))
    psnr = 99.0 if mse == 0 else 10 * np.log10(255 ** 2 / mse)
    return {"psnr": psnr, "ssim": ssim(a, b), "changed": float(np.mean(d > 24)),
            "max": float(d.max()) if d.size else 0.0}


def object_box(imgs, margin=24):
    bg = imgs[0][2, 2]
    m = np.zeros(imgs[0].shape[:2], bool)
    for im in imgs:
        m |= np.abs(im - bg).max(axis=2) > 6
    ys, xs = np.nonzero(m)
    if not len(ys):
        return None
    h, w = m.shape
    return (max(xs.min() - margin, 0), max(ys.min() - margin, 0), min(xs.max() + margin, w), min(ys.max() + margin, h))


def human(n):
    return f"{n / 1e6:.1f} MB" if n >= 1e6 else f"{n / 1e3:.0f} KB"


groups = {}
for r in res:
    groups.setdefault((r["scene"], r["view"]), []).append(r)
summary = []
for (scene, view), rs in groups.items():
    orig = next(r for r in rs if r["variant"] == "original")
    a = load(orig["png"])
    imgs = [load(r["png"]) for r in rs]
    crop = None
    if view.startswith("far"):
        crop = object_box(imgs)
    tiles = []
    for r, b in zip(rs, imgs):
        m = metrics(a, b)
        if crop:
            x0, y0, x1, y1 = crop
            ca, cb = a[y0:y1, x0:x1], b[y0:y1, x0:x1]
            m = metrics(ca, cb)
        r.update(m)
        summary.append({k: r[k] for k in ("scene", "view", "variant", "psnr", "ssim", "changed", "drawn_tris",
                                          "file_tris", "draws", "geo_bytes", "tex_bytes", "lod_choice")})
        im = Image.open(r["png"]).convert("RGB")
        diff = np.clip(np.abs(load(r["png"]) - a).max(axis=2) * 4, 0, 255)
        heat = np.zeros(diff.shape + (3,), np.uint8)
        heat[..., 0] = diff
        heat[..., 1] = (diff * 0.35).astype(np.uint8)
        dimg = Image.fromarray(np.maximum(heat, (a * 0.25).astype(np.uint8)))
        if crop:
            im = im.crop(crop)
            dimg = dimg.crop(crop)
            k = max(1, min(4, 420 // max(im.height, 1)))
            if k > 1:
                im = im.resize((im.width * k, im.height * k), Image.NEAREST)
                dimg = dimg.resize((dimg.width * k, dimg.height * k), Image.NEAREST)
        else:
            s = 520 / im.width
            im = im.resize((520, int(im.height * s)), Image.LANCZOS)
            dimg = dimg.resize((520, int(dimg.height * s)), Image.LANCZOS)
        tiles.append((r, im, dimg))
    tw = max(t[1].width for t in tiles)
    th = max(t[1].height for t in tiles)
    head = 64
    cols = len(tiles)
    sheet = Image.new("RGB", (cols * (tw + 8) + 8, 2 * (th + head) + 40), (250, 250, 250))
    d = ImageDraw.Draw(sheet)
    d.text((10, 8), f"{scene} - {view}   (row 1: render, row 2: difference to original x4)", fill=(0, 0, 0), font=FONTB)
    for i, (r, im, dimg) in enumerate(tiles):
        x = 8 + i * (tw + 8)
        y = 36
        lod = ""
        if r["lod_choice"]:
            lod = "  LOD level " + ",".join(str(c[2][0]) if c[2] else "-" for c in r["lod_choice"])
        d.text((x, y), f"{r['variant']}{lod}", fill=(0, 0, 0), font=FONTB)
        d.text((x, y + 21), f"{r['drawn_tris']:,} tris drawn  {r['draws']} draws  geo {human(r['geo_bytes'])}",
               fill=(60, 60, 60), font=FONT)
        q = "identical" if r["psnr"] >= 99 else f"PSNR {r['psnr']:.1f} dB  SSIM {r['ssim']:.3f}  changed {r['changed']:.2%}"
        col = (20, 120, 40) if r["psnr"] >= 35 or r["ssim"] >= 0.985 else ((170, 110, 0) if r["ssim"] >= 0.95 else (180, 30, 30))
        d.text((x, y + 40), q, fill=col, font=FONT)
        sheet.paste(im, (x, y + head))
        sheet.paste(dimg, (x, y + head + th + 10))
    path = os.path.join(OUT, f"{scene}__{view}.jpg")
    sheet.save(path, quality=88)
    print(path)
json.dump(summary, open(os.path.join(OUT, "metrics.json"), "w"), indent=1)
print()
for s in summary:
    if s["variant"] != "original":
        print(f"{s['scene']:<15}{s['view']:<12}{s['variant']:<16} tris {s['drawn_tris']:>7,}  PSNR {s['psnr']:5.1f}  SSIM {s['ssim']:.4f}  changed {s['changed']:.2%}")
