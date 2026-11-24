"""Low level mesh helpers shared by the analysis and the optimizer (numpy only)."""

import numpy as np

from . import model as M


def vertex_matrix(g, include=None):
    """All per-vertex attributes side by side -> (N, k) float64 matrix and the column layout."""
    cols = []
    layout = []
    n = g.num_vertices
    for name, a in g.arrays():
        if include is not None and name not in include:
            continue
        if a.binding != "vertex" or len(a.data) != n:
            continue
        d = np.asarray(a.data, dtype=np.float64).reshape(n, -1)
        layout.append((name, d.shape[1]))
        cols.append(d)
    if not cols:
        return np.zeros((n, 0)), layout
    return np.concatenate(cols, axis=1), layout


def unique_rows(mat, pos_tol=0.0, attr_tol=1e-6, pos_cols=3):
    """-> (first index of every unique row, inverse mapping). Tolerances quantize before comparing."""
    if len(mat) == 0:
        return np.zeros(0, np.int64), np.zeros(0, np.int64)
    q = np.array(mat, dtype=np.float64, copy=True)
    if pos_tol > 0:
        q[:, :pos_cols] = np.round(q[:, :pos_cols] / pos_tol)
    if attr_tol > 0 and q.shape[1] > pos_cols:
        q[:, pos_cols:] = np.round(q[:, pos_cols:] / attr_tol)
    q[q == 0] = 0.0  # -0.0 == 0.0
    q = np.ascontiguousarray(q)
    v = q.view(np.dtype((np.void, q.dtype.itemsize * q.shape[1]))).reshape(-1)
    _, first, inv = np.unique(v, return_index=True, return_inverse=True)
    return first.astype(np.int64), inv.reshape(-1).astype(np.int64)


def triangle_areas(pos, tris):
    if not len(tris):
        return np.zeros(0)
    p = np.asarray(pos, dtype=np.float64)
    a, b, c = p[tris[:, 0]], p[tris[:, 1]], p[tris[:, 2]]
    return 0.5 * np.linalg.norm(np.cross(b - a, c - a), axis=1)


def degenerate_mask(pos, tris, area_eps=0.0):
    """Triangles that repeat a vertex index or have (near) zero area."""
    if not len(tris):
        return np.zeros(0, bool)
    bad = (tris[:, 0] == tris[:, 1]) | (tris[:, 1] == tris[:, 2]) | (tris[:, 0] == tris[:, 2])
    if pos is not None and len(pos):
        p = np.asarray(pos, dtype=np.float64)[:, :3]
        a = p[tris[:, 0]]
        u = p[tris[:, 1]] - a
        v = p[tris[:, 2]] - a
        # |u x v|^2 written out (np.cross / norm have a large per-call overhead for small meshes)
        cx = u[:, 1] * v[:, 2] - u[:, 2] * v[:, 1]
        cy = u[:, 2] * v[:, 0] - u[:, 0] * v[:, 2]
        cz = u[:, 0] * v[:, 1] - u[:, 1] * v[:, 0]
        cr2 = cx * cx + cy * cy + cz * cz
        if area_eps <= 0:
            ext = float((p.max(axis=0) - p.min(axis=0)).max())
            area_eps = (ext * 1e-7) ** 2
        bad |= cr2 <= area_eps * area_eps
    return bad


def batch_stats(geoms, chunk=4000):
    """For many small geometries at once (one numpy pass per few thousand instead of one per geometry):
    [{'dup_vertices', 'degenerate', 'unused', 'duplicate_tris', 'degenerate_lines'} or None per geometry].
    The numbers equal unique_rows() / degenerate_mask() / referenced() and normalized()'s triangle
    de-duplication on each geometry alone. None = not batchable (no 3D positions, broken indices)."""
    out = [None] * len(geoms)
    for c0 in range(0, len(geoms), chunk):        # a few thousand at a time: bounded memory
        groups = {}
        for i in range(c0, min(c0 + chunk, len(geoms))):
            geom = geoms[i]
            if geom is None or geom.vertices is None or not geom.num_vertices:
                continue
            pos = np.asarray(geom.vertices.data)
            if pos.ndim != 2 or pos.shape[1] < 3:
                continue
            nv = geom.num_vertices
            tris = geom.triangles()
            if len(tris) and (tris.min() < 0 or tris.max() >= nv):
                continue
            mat, layout = vertex_matrix(geom)
            groups.setdefault(tuple(layout), []).append((i, geom, mat, pos[:, :3], tris))
        for items in groups.values():
            k = len(items)
            sizes = np.array([len(it[2]) for it in items], dtype=np.int64)
            offs = np.concatenate([[0], np.cumsum(sizes)[:-1]])
            gid = np.repeat(np.arange(k), sizes)
            # duplicate vertices: unique rows with the geometry number as an extra column
            mat = np.concatenate([np.concatenate([it[2] for it in items]), gid[:, None].astype(np.float64)], axis=1)
            first, _ = unique_rows(mat)
            dup = sizes - np.bincount(gid[first], minlength=k)
            del mat
            # degenerate triangles (per-geometry area threshold as in degenerate_mask)
            P = np.concatenate([it[3] for it in items]).astype(np.float64)
            ext = (np.maximum.reduceat(P, offs) - np.minimum.reduceat(P, offs)).max(axis=1)
            eps = (ext * 1e-7) ** 2
            degenerate = np.zeros(k, np.int64)
            dup_tris = np.zeros(k, np.int64)
            tl = [it[4] + o for it, o in zip(items, offs) if len(it[4])]
            if tl:
                T = np.concatenate(tl)
                tgid = np.concatenate([np.full(len(it[4]), j) for j, it in enumerate(items) if len(it[4])])
                bad = (T[:, 0] == T[:, 1]) | (T[:, 1] == T[:, 2]) | (T[:, 0] == T[:, 2])
                a = P[T[:, 0]]
                u = P[T[:, 1]] - a
                v = P[T[:, 2]] - a
                cx = u[:, 1] * v[:, 2] - u[:, 2] * v[:, 1]
                cy = u[:, 2] * v[:, 0] - u[:, 0] * v[:, 2]
                cz = u[:, 0] * v[:, 1] - u[:, 1] * v[:, 0]
                e = eps[tgid]
                bad |= (cx * cx + cy * cy + cz * cz) <= e * e
                degenerate = np.bincount(tgid[bad], minlength=k)
                # duplicated triangles among the good ones (same canonical rotation as normalized())
                G = T[~bad]
                if len(G):
                    rot = np.argmin(G, axis=1)
                    canon = np.stack([G[np.arange(len(G)), (rot + r) % 3] for r in range(3)], axis=1)
                    _, first_t = np.unique(canon, axis=0, return_index=True)
                    good_gid = tgid[~bad]
                    dup_tris = np.bincount(good_gid, minlength=k) - np.bincount(good_gid[first_t], minlength=k)
            # unused vertices
            used = np.zeros(len(gid), bool)
            bad_lines = np.zeros(k, np.int64)
            for j, (it, o, n) in enumerate(zip(items, offs, sizes)):
                geom = it[1]
                for p in geom.prims:
                    idx = p.vertex_indices()
                    idx = idx[(idx >= 0) & (idx < n)]
                    used[idx + o] = True
                ln = geom.lines()
                if len(ln):
                    bad_lines[j] = int(np.count_nonzero(ln[:, 0] == ln[:, 1]))
            unused = np.bincount(gid[~used], minlength=k)
            for j, it in enumerate(items):
                out[it[0]] = {"dup_vertices": int(dup[j]), "degenerate": int(degenerate[j]),
                              "unused": int(unused[j]), "duplicate_tris": int(dup_tris[j]),
                              "degenerate_lines": int(bad_lines[j])}
    return out


def referenced(g):
    """Bool mask of vertices used by any primitive."""
    used = np.zeros(g.num_vertices, bool)
    for p in g.prims:
        idx = p.vertex_indices()
        idx = idx[(idx >= 0) & (idx < len(used))]
        used[idx] = True
    return used


def smallest_index_type(nverts):
    # 8-bit indices are legal but slow on many GPUs/drivers, so 16 bit is the smallest we pick
    if nverts <= 65536:
        return "DrawElementsUShort"
    return "DrawElementsUInt"


def rebuild(g, keep, tris=None, lines=None, points=None):
    """New GeomData with the vertices ``keep`` (index array) and the given primitive index lists
    (already in the new numbering). Non per-vertex arrays are copied unchanged."""
    out = M.GeomData()
    out.notes = list(g.notes)
    nv = g.num_vertices
    for name, a in g.arrays():
        if a.binding == "vertex" and len(a.data) == nv:
            na = a.copy_with(np.asarray(a.data)[keep])
        else:
            na = a.copy_with(np.asarray(a.data).copy())
        if name == "vertices":
            out.vertices = na
        elif name == "normals":
            out.normals = na
        elif name == "colors":
            out.colors = na
        elif name.startswith("texcoord"):
            out.texcoords[int(name[8:])] = na
        else:
            out.extra[name] = na
    n = len(keep)
    it = smallest_index_type(n)
    if tris is not None and len(tris):
        out.prims.append(M.Primitive("TRIANGLES", "elements", indices=np.asarray(tris, np.int64).reshape(-1), index_type=it))
    if lines is not None and len(lines):
        out.prims.append(M.Primitive("LINES", "elements", indices=np.asarray(lines, np.int64).reshape(-1), index_type=it))
    if points is not None and len(points):
        out.prims.append(M.Primitive("POINTS", "elements", indices=np.asarray(points, np.int64).reshape(-1), index_type=it))
    return out


def instanced(g):
    return any(p.instances for p in g.prims)


def acmr(tris, cache=32):
    """Average cache miss ratio of a FIFO post-transform cache (lower is better; 0.5-0.7 is good)."""
    if not len(tris):
        return 0.0
    from collections import deque
    fifo = deque(maxlen=cache)
    inset = set()
    misses = 0
    flat = np.asarray(tris).reshape(-1).tolist()
    for v in flat:
        if v not in inset:
            misses += 1
            if len(fifo) == cache:
                inset.discard(fifo[0])
            fifo.append(v)
            inset.add(v)
    return misses / len(tris)
