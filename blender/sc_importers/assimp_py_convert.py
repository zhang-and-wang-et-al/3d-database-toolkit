"""Convert a 3D file to OBJ + MTL with the ``assimp_py`` pip package.

Runs in the launcher's Python (not Blender's), so the ~40 formats Assimp
reads work without installing the Assimp command line tool:

    python assimp_py_convert.py <input> <output.obj>
"""

import os
import sys

import assimp_py
import numpy as np


def main(src, dst):
    flags = (assimp_py.Process_Triangulate | assimp_py.Process_PreTransformVertices
             | assimp_py.Process_JoinIdenticalVertices | assimp_py.Process_SortByPType)
    scene = assimp_py.import_file(src, flags)
    if scene is None or not scene.num_meshes:
        raise SystemExit("assimp_py: no meshes in file")
    base = os.path.dirname(os.path.abspath(src))
    mtl_path = os.path.splitext(dst)[0] + ".mtl"
    with open(mtl_path, "w", encoding="utf-8") as mtl:
        for i, m in enumerate(scene.materials):
            mtl.write(f"newmtl mat{i}\n")
            kd = m.get("COLOR_DIFFUSE") or [0.8, 0.8, 0.8]
            mtl.write("Kd {:.4f} {:.4f} {:.4f}\n".format(*kd[:3]))
            tex = (m.get("TEXTURES") or {}).get(assimp_py.TextureType_DIFFUSE) or []
            if tex and not tex[0].startswith("*"):  # "*0" = embedded texture
                t = tex[0].replace("\\", "/")
                cand = [t, os.path.join(base, t), os.path.join(base, os.path.basename(t))]
                found = next((c for c in cand if os.path.isfile(c)), None)
                if found:
                    mtl.write(f"map_Kd {os.path.abspath(found)}\n")
    voff = toff = 0
    with open(dst, "w", encoding="utf-8") as out:
        out.write(f"mtllib {os.path.basename(mtl_path)}\n")
        for mi, mesh in enumerate(scene.meshes):
            v = np.asarray(mesh.vertices, dtype=np.float64).reshape(-1, 3)
            idx = np.asarray(mesh.indices, dtype=np.int64).reshape(-1)
            if not len(v) or len(idx) < 3:
                continue
            faces = idx[: len(idx) // 3 * 3].reshape(-1, 3)
            out.write(f"o {mesh.name or f'mesh{mi}'}\n")
            np.savetxt(out, v, fmt="v %.6f %.6f %.6f")
            uv = None
            if mesh.texcoords:
                uv = np.asarray(mesh.texcoords[0], dtype=np.float64).reshape(len(v), -1)[:, :2]
                np.savetxt(out, uv, fmt="vt %.6f %.6f")
            out.write(f"usemtl mat{mesh.material_index}\n")
            f = faces + 1 + voff
            if uv is not None:
                ft = faces + 1 + toff
                np.savetxt(out, np.stack([f[:, 0], ft[:, 0], f[:, 1], ft[:, 1], f[:, 2], ft[:, 2]], axis=1), fmt="f %d/%d %d/%d %d/%d")
                toff += len(uv)
            else:
                np.savetxt(out, f, fmt="f %d %d %d")
            voff += len(v)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
