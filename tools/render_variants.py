"""Render scene variants with identical cameras (Blender Workbench, textured, studio light).

Runs inside Blender (or a Python with the bpy wheel); Workbench needs a display (use Xvfb on servers):
    blender -b --python tools/render_variants.py -- jobs.json
    python-with-bpy tools/render_variants.py jobs.json
Then build the comparison sheets with tools/compare_renders.py.
jobs.json: [{"name": .., "ref": file, "variants": {label: file}, "views": [{"id":.., "dist": k, "w":.., "h":..}]}]
LOD nodes pick their child by the real camera distance (like OSG's DISTANCE_FROM_EYE_POINT).
"""
import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "blender"))
import scenekit  # noqa: E402
from scenekit import analyze as A, model as M  # noqa: E402
from sc_importers import osgfmt  # noqa: E402


class Ctx:
    def log(self, m):
        pass

    def add_issue(self, c, m=None):
        pass


class LodBuilder(osgfmt.Builder):
    """Walk that chooses LOD children by camera distance."""

    def __init__(self, ctx, base, rep, cam):
        super().__init__(ctx, base)
        self.rep = rep
        self.cam = np.asarray(cam)
        self.chosen = []

    def walk(self, root):
        stack = [(root, np.eye(4), None)]
        while stack:
            n, W, ss = stack.pop()
            if not n.node_mask:
                continue
            if n.matrix is not None:
                W = W @ n.matrix
            ss = n.stateset or ss
            if n.kind == M.GEOMETRY and n.geom is not None:
                self.add_geometry(n.geom, W, n.stateset or ss)
                continue
            kids = n.all_children()
            if n.kind in (M.LOD, M.PAGED_LOD) and n.ranges and n.children:
                bb = self.rep.stat(n).bbox
                c = (bb[0] + bb[1]) / 2 if bb is not None else np.zeros(3)
                if n.center is not None and len(n.center) >= 3 and (len(n.center) < 4 or n.center[3] >= 0):
                    c = np.asarray(n.center[:3])
                wc = W[:3, :3] @ c + W[:3, 3]
                d = float(np.linalg.norm(self.cam - wc))
                kids = [ch for ch, (a, b) in zip(n.children, n.ranges) if a <= d < b]
                self.chosen.append((n.name, d, [n.children.index(k) for k in kids]))
            if n.kind == M.SWITCH and n.switch_values is not None:
                kids = [c for i, c in enumerate(n.children) if i >= len(n.switch_values) or n.switch_values[i]]
            for c in reversed(kids):
                stack.append((c, W, ss))


def setup_scene(w, h):
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_WORKBENCH"
    sc.render.resolution_x, sc.render.resolution_y = w, h
    sc.render.resolution_percentage = 100
    sc.display.render_aa = "8"
    sh = sc.display.shading
    sh.light = "STUDIO"
    sh.color_type = "TEXTURE"
    sh.show_shadows = True
    sh.shadow_intensity = 0.4
    sh.show_cavity = False
    sh.show_specular_highlight = True
    sc.display.shading.background_type = "VIEWPORT" if hasattr(sh, "background_type") else None
    sc.render.film_transparent = False
    world = bpy.data.worlds.new("w")
    world.color = (0.82, 0.84, 0.88)
    sc.world = world
    sc.view_settings.view_transform = "Standard"
    sc.render.image_settings.file_format = "PNG"


def look(cam, target, az, el, dist):
    d = Vector((math.cos(el) * math.cos(az), math.cos(el) * math.sin(az), math.sin(el)))
    cam.location = Vector(target) + d * dist
    cam.rotation_euler = (Vector(target) - cam.location).to_track_quat("-Z", "Y").to_euler()


def build(path, cam_pos, rep_cache):
    for o in list(bpy.data.objects):
        if o.type == "MESH":
            bpy.data.objects.remove(o)
    for me in list(bpy.data.meshes):
        bpy.data.meshes.remove(me)
    sc = rep_cache.get(path)
    if sc is None:
        sc = scenekit.load_scene(path, scenekit.LoadOptions(osgconv=""))
        rep_cache[path] = (sc, A.analyze(sc, deep_checks=False))
        sc = rep_cache[path]
    scene, rep = sc
    b = LodBuilder(Ctx(), os.path.dirname(path), rep, cam_pos)
    b.walk(scene.root)
    obj = b.acc.build("model")
    b.apply_normals(obj)
    return obj, b.chosen, rep.summary()


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    jobs = json.load(open(args[0]))
    out_dir = os.path.dirname(os.path.abspath(args[0]))
    results = []
    for job in jobs:
        rep_cache = {}
        ref = scenekit.load_scene(job["ref"], scenekit.LoadOptions(osgconv=""))
        bb = A.analyze(ref, deep_checks=False).total.bbox
        center = (bb[0] + bb[1]) / 2
        radius = float(np.linalg.norm(bb[1] - bb[0])) / 2
        az, el = math.radians(job.get("az", -55)), math.radians(job.get("el", 28))
        for view in job["views"]:
            w, h = view.get("w", 900), view.get("h", 640)
            fov = math.radians(view.get("fov", 40))
            dist = radius / math.sin(fov / 2) * view.get("dist", 1.05)
            target = center + np.array(view.get("offset", (0, 0, 0))) * radius
            for label, path in job["variants"].items():
                bpy.ops.wm.read_factory_settings(use_empty=True)
                setup_scene(w, h)
                camd = bpy.data.cameras.new("cam")
                camd.angle = fov
                camd.clip_start = max(radius * 1e-3, 1e-3)
                camd.clip_end = dist * 10 + radius * 4
                cam = bpy.data.objects.new("cam", camd)
                bpy.context.scene.collection.objects.link(cam)
                bpy.context.scene.camera = cam
                look(cam, target, az, el, dist)
                obj, chosen, summ = build(os.path.abspath(path), np.array(cam.location), rep_cache)
                out = os.path.join(out_dir, f"{job['name']}__{view['id']}__{label}.png")
                bpy.context.scene.render.filepath = out
                bpy.ops.render.render(write_still=True)
                tris = sum(len(p.vertices) - 2 for p in obj.data.polygons) if obj else 0
                results.append({"scene": job["name"], "view": view["id"], "variant": label, "png": out,
                                "drawn_tris": tris, "lod_choice": chosen, "file_tris": summ["triangles"],
                                "draws": summ["draw_calls"], "geo_bytes": summ["geometry_bytes"],
                                "tex_bytes": summ["texture_bytes"], "cam_dist": dist, "radius": radius})
                print("rendered", os.path.basename(out), tris, chosen[:4], flush=True)
    json.dump(results, open(os.path.join(out_dir, "results.json"), "w"), indent=1)


main()
