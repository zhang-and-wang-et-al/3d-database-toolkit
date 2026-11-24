"""Build test scenes for the visual quality check (bpy for the meshes, scenekit writes the .osgt files).

    python-with-bpy tools/make_test_scenes.py <output folder>

characters.osgt  textured, UV-seamed, smooth + hard-edged meshes (63k / 37k triangles)
city.osgt        instancing, copies, 30 small drawables, an over-tessellated object, triangle soups, a 4k texture
terrain.osgt     180k triangle height field with a contour texture
"""
import math
import os
import sys

import bpy
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import scenekit  # noqa: E402
from scenekit import images, model as M  # noqa: E402


def bilinear(Z, x, y):
    """Sample grid Z (rows = y) at fractional (x, y)."""
    x0 = np.clip(np.floor(x).astype(int), 0, Z.shape[1] - 2)
    y0 = np.clip(np.floor(y).astype(int), 0, Z.shape[0] - 2)
    fx, fy = x - x0, y - y0
    return (Z[y0, x0] * (1 - fx) * (1 - fy) + Z[y0, x0 + 1] * fx * (1 - fy)
            + Z[y0 + 1, x0] * (1 - fx) * fy + Z[y0 + 1, x0 + 1] * fx * fy)


OUT = (sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])[0]
os.makedirs(OUT, exist_ok=True)
rng = np.random.default_rng(7)


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def save_png(name, rgb):
    """rgb: (h, w, 3) float 0..1, row 0 = bottom."""
    h, w, _ = rgb.shape
    img = bpy.data.images.new(name, w, h)
    px = np.ones((h, w, 4), np.float32)
    px[..., :3] = rgb
    img.pixels.foreach_set(px.ravel())
    path = os.path.join(OUT, name)
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    return name


def checker(name, n=1024, cells=16):
    y, x = np.mgrid[0:n, 0:n]
    c = ((x // (n // cells)) + (y // (n // cells))) % 2
    rgb = np.zeros((n, n, 3), np.float32)
    rgb[...] = np.where(c[..., None] == 1, [0.92, 0.85, 0.2], [0.15, 0.35, 0.8])
    # thin grid lines + gradient make UV distortion easy to see
    lines = ((x % (n // cells)) < 3) | ((y % (n // cells)) < 3)
    rgb[lines] = [0.05, 0.05, 0.05]
    rgb *= (0.6 + 0.4 * x / n)[..., None]
    return save_png(name, rgb)


def mesh_from_object(obj, flat=False):
    """-> GeomData (welded per corner: position, normal, uv)."""
    dg = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(dg)
    me = ev.to_mesh()
    me.calc_loop_triangles()
    nl = len(me.loops)
    co = np.zeros(len(me.vertices) * 3)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    vi = np.zeros(nl, np.int64)
    me.loops.foreach_get("vertex_index", vi)
    nrm = np.zeros(nl * 3)
    me.corner_normals.foreach_get("vector", nrm)
    nrm = nrm.reshape(-1, 3)
    uv = None
    if me.uv_layers.active is not None:
        uv = np.zeros(nl * 2)
        me.uv_layers.active.data.foreach_get("uv", uv)
        uv = uv.reshape(-1, 2)
    tri = np.zeros(len(me.loop_triangles) * 3, np.int64)
    me.loop_triangles.foreach_get("loops", tri)
    W = np.array(obj.matrix_world)
    pos = co[vi] @ W[:3, :3].T + W[:3, 3]
    cols = [pos, nrm]
    if uv is not None:
        cols.append(uv)
    mat = np.round(np.concatenate(cols, 1), 6)
    uniq, first, inv = np.unique(mat, axis=0, return_index=True, return_inverse=True)
    inv = inv.reshape(-1)
    g = M.GeomData()
    g.vertices = M.Array(pos[first], "Vec3Array")
    g.normals = M.Array(nrm[first], "Vec3Array")
    if uv is not None:
        g.texcoords[0] = M.Array(uv[first], "Vec2Array")
    t = inv[tri]
    g.prims.append(M.Primitive("TRIANGLES", "elements", indices=t, index_type="DrawElementsUInt"))
    ev.to_mesh_clear()
    return g


def stateset(tex=None, diffuse=(0.8, 0.8, 0.8, 1.0), name=""):
    ss = M.StateSet()
    ss.diffuse = diffuse
    ss.material_name = name
    if tex:
        t = M.Texture(tex)
        images.fill_texture(t, OUT)
        ss.textures.append(t)
    return ss


def geode(name, g, ss=None):
    n = M.Node(M.GEOMETRY, "osg::Geometry", "")
    n.geom = g
    gd = M.Node(M.GEODE, "osg::Geode", name)
    gd.children = [n]
    gd.stateset = ss
    return gd


def xform(child, t=(0, 0, 0), rz=0.0, s=1.0, name=""):
    x = M.Node(M.TRANSFORM, "osg::MatrixTransform", name)
    c, sn = math.cos(rz), math.sin(rz)
    m = np.eye(4)
    m[:3, :3] = np.array([[c, -sn, 0], [sn, c, 0], [0, 0, 1]]) * s
    m[:3, 3] = t
    x.matrix = m
    x.children = [child]
    return x


def write(name, root):
    sc = M.Scene(os.path.join(OUT, name), "osgt")
    sc.root = root
    scenekit.save_scene(sc, sc.path)
    print("wrote", name)


def soup(g):
    """Unindexed copy (DrawArrays triangle soup), like many exporters write."""
    t = g.prims[0].indices
    out = M.GeomData()
    out.vertices = M.Array(g.vertices.data[t], "Vec3Array")
    out.normals = M.Array(g.normals.data[t], "Vec3Array")
    if 0 in g.texcoords:
        out.texcoords[0] = M.Array(g.texcoords[0].data[t], "Vec2Array")
    out.prims.append(M.Primitive("TRIANGLES", "arrays", 0, len(t)))
    return out


# ------------------------------------------------------------------ scene 1: textured characters
clear()
checker("checker.png")
bpy.ops.mesh.primitive_monkey_add(size=2, location=(0, 0, 1.2))
monkey = bpy.context.active_object
monkey.rotation_euler = (0, 0, math.radians(-20))
mod = monkey.modifiers.new("sub", "SUBSURF")
mod.levels = 3
bpy.ops.object.shade_smooth()
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.uv.smart_project(angle_limit=math.radians(66))
bpy.ops.object.mode_set(mode="OBJECT")
g_monkey = mesh_from_object(monkey)
# hard-edged gear: flat shading + bevel
bpy.ops.mesh.primitive_cylinder_add(vertices=48, radius=1, depth=0.5, location=(2.8, 0.5, 0.25))
gear = bpy.context.active_object
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="DESELECT")
bpy.ops.object.mode_set(mode="OBJECT")
for i, p in enumerate(gear.data.polygons):
    p.select = len(p.vertices) == 4 and i % 2 == 0
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.extrude_region_shrink_fatten(TRANSFORM_OT_shrink_fatten={"value": 0.25})
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.mesh.bevel(offset=0.03, segments=3)
bpy.ops.uv.smart_project()
bpy.ops.object.mode_set(mode="OBJECT")
bpy.ops.object.shade_flat()
g_gear = mesh_from_object(gear)
bpy.ops.mesh.primitive_torus_add(major_segments=192, minor_segments=96, location=(-2.6, 0.8, 0.6),
                                 major_radius=0.9, minor_radius=0.35)
torus = bpy.context.active_object
bpy.ops.object.shade_smooth()
g_torus = mesh_from_object(torus)
bpy.ops.mesh.primitive_plane_add(size=9, location=(0, 0, 0))
g_ground = mesh_from_object(bpy.context.active_object)
tex_ss = stateset("checker.png", name="checker")
root = M.Node(M.GROUP, "osg::Group", "characters")
root.children = [geode("monkey", g_monkey, tex_ss), geode("gear", g_gear, tex_ss),
                 geode("torus", g_torus, stateset(None, (0.85, 0.25, 0.2, 1), "red")),
                 geode("ground", g_ground, stateset(None, (0.7, 0.7, 0.68, 1), "floor"))]
write("characters.osgt", root)

# ------------------------------------------------------------------ scene 2: city block
clear()
# windows texture
n = 512
y, x = np.mgrid[0:n, 0:n]
win = ((x % 64) > 14) & ((x % 64) < 50) & ((y % 64) > 18) & ((y % 64) < 52)
rgb = np.where(win[..., None], [0.25, 0.35, 0.5], [0.75, 0.68, 0.6]).astype(np.float32)
save_png("facade.png", rgb)
# huge ground texture (triggers the downscale pass)
n = 4096
y, x = np.mgrid[0:n, 0:n]
ground = 0.35 + 0.08 * np.sin(x / 37.0) * np.cos(y / 53.0)
road = ((x % 1024) < 160) | ((y % 1024) < 160)
rgb = np.stack([ground, ground * 1.05, ground * 0.9], -1).astype(np.float32)
rgb[road] = [0.12, 0.12, 0.13]
stripe = road & (((x % 1024) // 8 == 10) | ((y % 1024) // 8 == 10)) & (((x + y) // 40) % 2 == 0)
rgb[stripe] = [0.9, 0.9, 0.85]
save_png("ground_4k.png", rgb)
del x, y, rgb, ground, road, stripe

bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, 0.5))
b = bpy.context.active_object
b.scale = (2, 2, 6)
b.location = (0, 0, 3)
bpy.ops.object.transform_apply(scale=True, location=True)
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.uv.cube_project(cube_size=6)
bpy.ops.object.mode_set(mode="OBJECT")
g_building = mesh_from_object(b)
bpy.ops.mesh.primitive_cylinder_add(vertices=24, radius=0.08, depth=4, location=(0, 0, 2))
pole = bpy.context.active_object
bpy.ops.mesh.primitive_uv_sphere_add(segments=24, ring_count=12, radius=0.3, location=(0, 0, 4.1))
lamp_head = bpy.context.active_object
bpy.ops.object.select_all(action="DESELECT")
pole.select_set(True)
lamp_head.select_set(True)
bpy.context.view_layer.objects.active = pole
bpy.ops.object.join()
bpy.ops.object.shade_smooth()
g_lamp = mesh_from_object(pole)
bpy.ops.mesh.primitive_uv_sphere_add(segments=256, ring_count=128, radius=0.35)   # over-tessellated hydrant
bpy.ops.object.shade_smooth()
g_hydrant = mesh_from_object(bpy.context.active_object)
bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=4, radius=1.6, location=(0, 0, 3.2))  # tree crown
crown = bpy.context.active_object
for v in crown.data.vertices:
    v.co *= 1 + 0.12 * math.sin(v.co.x * 5) * math.cos(v.co.y * 4)
bpy.ops.object.shade_flat()
g_crown = mesh_from_object(crown)
bpy.ops.mesh.primitive_plane_add(size=60)
pl = bpy.context.active_object
g_ground = mesh_from_object(pl)

facade = stateset("facade.png", name="facade")
lamp_ss = stateset(None, (0.25, 0.25, 0.28, 1), "metal")
city = M.Node(M.GROUP, "osg::Group", "city")
shared_building = geode("building", g_building, facade)
blocks = M.Node(M.GROUP, "osg::Group", "instanced blocks")
for i in range(5):
    for j in range(3):
        blocks.children.append(xform(shared_building, (-20 + i * 9, -18 + j * 7, 0), rz=0.0,
                                     s=1.0, name=f"b{i}{j}"))
copies = M.Node(M.GROUP, "osg::Group", "copied blocks")
for i in range(5):
    gb = M.GeomData()
    gb.vertices = M.Array(g_building.vertices.data + [-20 + i * 9, 14, 0], "Vec3Array")
    gb.normals = g_building.normals
    gb.texcoords[0] = g_building.texcoords[0]
    gb.prims = g_building.prims
    copies.children.append(geode(f"copy{i}", gb, stateset("facade.png", name="facade")))
lamps = M.Node(M.GROUP, "osg::Group", "street lamps")
for i in range(30):
    gl = M.GeomData()
    gl.vertices = M.Array(g_lamp.vertices.data + [-24 + (i % 15) * 3.4, -6 if i < 15 else 6, 0], "Vec3Array")
    gl.normals = g_lamp.normals
    gl.prims = g_lamp.prims
    lamps.children.append(geode(f"lamp{i}", gl, lamp_ss))
hyd = geode("hydrant", M.GeomData(), stateset(None, (0.9, 0.15, 0.1, 1), "hydrant"))
hyd.children[0].geom = g_hydrant
trees = M.Node(M.GROUP, "osg::Group", "trees")
for i in range(6):
    tg = soup(g_crown)
    tg.vertices = M.Array(tg.vertices.data + [-21 + i * 8, 0, 0], "Vec3Array")
    trees.children.append(geode(f"tree{i}", tg, stateset(None, (0.2, 0.55, 0.2, 1), "leaves")))
city.children = [geode("ground", g_ground, stateset("ground_4k.png", name="ground")), blocks, copies, lamps,
                 xform(hyd, (1.5, -4, 0.35), name="hydrant pos"), trees]
for gd in [city.children[0]]:
    gd.children[0].geom.texcoords[0].data = gd.children[0].geom.texcoords[0].data * 2
write("city.osgt", city)

# ------------------------------------------------------------------ scene 3: terrain
clear()
N = 300
xs = np.linspace(-50, 50, N)
X, Y = np.meshgrid(xs, xs)
Z = (6 * np.sin(X / 9) * np.cos(Y / 11) + 3 * np.sin(X / 3.1 + Y / 4.7) + 0.6 * rng.standard_normal(X.shape).cumsum(0) * 0.05
     + 12 * np.exp(-((X - 10) ** 2 + (Y + 8) ** 2) / 300))
gy, gx = np.gradient(Z, xs, xs)
nrm = np.stack([-gx, -gy, np.ones_like(Z)], -1)
nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True)
idx = np.arange(N * N).reshape(N, N)
a, b2, c, d = idx[:-1, :-1].ravel(), idx[:-1, 1:].ravel(), idx[1:, :-1].ravel(), idx[1:, 1:].ravel()
tris = np.concatenate([np.stack([a, b2, d], 1), np.stack([a, d, c], 1)])
g = M.GeomData()
g.vertices = M.Array(np.stack([X, Y, Z], -1).reshape(-1, 3), "Vec3Array")
g.normals = M.Array(nrm.reshape(-1, 3), "Vec3Array")
g.texcoords[0] = M.Array(np.stack([(X + 50) / 100, (Y + 50) / 100], -1).reshape(-1, 2), "Vec2Array")
g.prims.append(M.Primitive("TRIANGLES", "elements", indices=tris.reshape(-1), index_type="DrawElementsUInt"))
# height colour texture with contour lines
n = 1024
ty, tx = np.mgrid[0:n, 0:n]
h = bilinear(Z, tx / (n - 1) * (N - 1), ty / (n - 1) * (N - 1))
hn = (h - h.min()) / (h.max() - h.min())
rgb = np.stack([0.25 + 0.6 * hn, 0.45 + 0.3 * hn, 0.2 + 0.2 * hn], -1)
rgb[(np.abs((h % 2.0) - 1.0) < 0.06)] *= 0.55
save_png("terrain_color.png", rgb.astype(np.float32))
write("terrain.osgt", geode("terrain", g, stateset("terrain_color.png", name="terrain")))
