"""Tests for the scene inspector core (scenekit): python -m unittest discover tests

Needs numpy. Tests that compare with OpenSceneGraph itself run only when osgconv is on PATH.
"""

import os
import shutil
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

try:
    import numpy as np
except ImportError:  # pragma: no cover
    np = None

if np is not None:
    import scenekit
    from scenekit import analyze as A
    from scenekit import model as M
    from scenekit import osgtext

OSGT_HEAD = "#Ascii Scene \n#Version 161 \n#Generator OpenSceneGraph 3.6.5 \n\n"


def osgt_geometry(uid, verts, prim, extra=""):
    vs = "\n".join(" ".join(str(x) for x in v) for v in verts)
    return f"""osg::Geometry {{
  UniqueID {uid}
  {extra}
  PrimitiveSetList 1 {{
    {prim}
  }}
  VertexArray TRUE {{
    osg::Vec3Array {{
      UniqueID {uid + 1000}
      Binding BIND_PER_VERTEX
      vector {len(verts)} {{
{vs}
      }}
    }}
  }}
}}"""


def soup_quads(n, offset=(0, 0, 0)):
    """n unit quads as an unindexed triangle soup (6 vertices per quad)."""
    out = []
    for i in range(n):
        x = i + offset[0]
        a, b, c, d = (x, offset[1], offset[2]), (x + 1, offset[1], offset[2]), (x + 1, offset[1] + 1, offset[2]), (x, offset[1] + 1, offset[2])
        out += [a, b, c, a, c, d]
    return out


def sphere(nu=40, nv=20, r=1.0):
    th = np.linspace(0, np.pi, nv + 1)
    ph = np.linspace(0, 2 * np.pi, nu + 1)
    TH, PH = np.meshgrid(th, ph, indexing="ij")
    V = np.stack([np.sin(TH) * np.cos(PH), np.sin(TH) * np.sin(PH), np.cos(TH)], -1).reshape(-1, 3) * r
    idx = np.arange((nv + 1) * (nu + 1)).reshape(nv + 1, nu + 1)
    a, b, c, d = idx[:-1, :-1].ravel(), idx[:-1, 1:].ravel(), idx[1:, :-1].ravel(), idx[1:, 1:].ravel()
    return V, np.concatenate([np.stack([a, c, d], 1), np.stack([a, d, b], 1)])


def sphere_osgt(uid, nu=40, nv=20, r=1.0):
    V, T = sphere(nu, nv, r)
    idx = " ".join(str(i) for i in T.reshape(-1))
    prim = f"osg::DrawElementsUInt {{ \n UniqueID {uid + 2000}\n Mode TRIANGLES\n vector {T.size} {{\n{idx}\n }}\n }}"
    return osgt_geometry(uid, [tuple(round(float(x), 6) for x in v) for v in V], prim)


def flt_bytes():
    """A minimal OpenFlight file: header, vertex palette, group > LOD > object > 2 faces (a quad split in two)."""
    recs = []

    def rec(op, body):
        recs.append(struct.pack(">HH", op, 4 + len(body)) + body)

    hdr = bytearray(320)
    hdr[0:8] = b"db\0\0\0\0\0\0"
    struct.pack_into(">i", hdr, 8, 1600)  # revision at record offset 12
    hdr[58] = 0  # units at record offset 62: meters
    rec(1, bytes(hdr))
    rec(67, struct.pack(">i", 8 + 4 * 40))
    pal_start = sum(len(r) for r in recs) - 8
    offsets = []
    for p in ((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)):
        offsets.append(sum(len(r) for r in recs) - pal_start)
        rec(68, struct.pack(">hH3d", 0, 0, *p) + struct.pack(">Ii", 0, -1))
    rec(10, b"")
    rec(2, b"group1\0\0" + bytes(36))
    rec(10, b"")
    lod = bytearray(76)
    lod[0:4] = b"lod\0"
    struct.pack_into(">dd", lod, 12, 500.0, 0.0)  # switch in / out at record offsets 16 / 24
    rec(73, bytes(lod))
    rec(10, b"")
    rec(4, b"obj1\0\0\0\0" + bytes(20))
    rec(10, b"")
    for tri in ((0, 1, 2), (0, 2, 3)):
        face = bytearray(76)
        face[0:4] = b"f\0\0\0"
        struct.pack_into(">h", face, 24, -1)       # texture pattern (record offset 28)
        struct.pack_into(">I", face, 40, 0x40000000)  # flags (record offset 44): no colour
        rec(5, bytes(face))
        rec(10, b"")
        rec(72, struct.pack(">3i", *(offsets[i] for i in tri)))
        rec(11, b"")
    for _ in range(4):
        rec(11, b"")
    return b"".join(recs)


@unittest.skipIf(np is None, "numpy not installed")
class ScenekitTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write(self, name, text, binary=False):
        p = os.path.join(self.tmp, name)
        with open(p, "wb" if binary else "w") as fh:
            fh.write(text)
        return p

    def build_osgt(self):
        """root > [xform > geodeA(shared), geodeA again, geode with 12 small soups, geode with a sphere]"""
        quad = osgt_geometry(10, soup_quads(1), "osg::DrawArrays {\n UniqueID 11\n Mode TRIANGLES\n Count 6\n }")
        smalls = "\n".join(osgt_geometry(100 + i * 3, soup_quads(2, (0, i * 2, 0)),
                                         f"osg::DrawArrays {{\n UniqueID {101 + i * 3}\n Mode TRIANGLES\n Count 12\n }}")
                           for i in range(12))
        text = OSGT_HEAD + f"""osg::Group {{
  UniqueID 1
  Name "root"
  Children 4 {{
    osg::MatrixTransform {{
      UniqueID 2
      Name "xform"
      Children 1 {{
        osg::Geode {{
          UniqueID 3
          Name "shared"
          Drawables 1 {{
{quad}
          }}
        }}
      }}
      Matrix {{
        1 0 0 0
        0 1 0 0
        0 0 1 0
        5 0 0 1
      }}
    }}
    osg::Geode {{
      UniqueID 3
    }}
    osg::Geode {{
      UniqueID 4
      Name "smalls"
      Drawables 12 {{
{smalls}
      }}
    }}
    osg::Geode {{
      UniqueID 5
      Name "ball"
      Drawables 1 {{
{sphere_osgt(500)}
      }}
    }}
  }}
}}
"""
        return self.write("scene.osgt", text)

    # ------------------------------------------------------------ parsing
    def test_osgt_parse_and_sharing(self):
        sc = scenekit.load_scene(self.build_osgt())
        root = sc.root
        self.assertEqual(root.name, "root")
        self.assertEqual(len(root.children), 4)
        xform, again = root.children[0], root.children[1]
        self.assertEqual(xform.kind, M.TRANSFORM)
        self.assertTrue(np.allclose(xform.matrix[:3, 3], [5, 0, 0]))
        self.assertIs(xform.children[0], again)  # instancing survives
        rep = A.analyze(sc)
        self.assertEqual(rep.instances[id(again)], 2)
        geo = again.children[0]
        self.assertEqual(geo.geom.counts()[0], 2)
        self.assertEqual(rep.total.tris, 2 * 2 + 12 * 4 + 40 * 20 * 2)

    def test_legacy_osg_parse(self):
        text = """Group {
  name "root"
  num_children 2
  MatrixTransform {
    UniqueID MT_1
    Matrix {
      1 0 0 0
      0 1 0 0
      0 0 1 0
      0 0 7 1
    }
    num_children 1
    Geode {
      UniqueID Geode_1
      num_drawables 1
      Geometry {
        PrimitiveSets 1
        {
          DrawArrays TRIANGLE_STRIP 0 4
        }
        VertexArray Vec3Array 4
        {
          0 0 0
          1 0 0
          0 1 0
          1 1 0
        }
        ColorBinding OVERALL
        ColorArray Vec4Array 1
        {
          1 0 0 1
        }
      }
    }
  }
  Use Geode_1
}
"""
        sc = scenekit.load_scene(self.write("legacy.osg", text))
        self.assertEqual(sc.fmt, "osg")
        mt = sc.root.children[0]
        self.assertTrue(np.allclose(mt.matrix[:3, 3], [0, 0, 7]))
        self.assertIs(mt.children[0], sc.root.children[1])
        g = mt.children[0].children[0].geom
        self.assertEqual(g.counts()[0], 2)
        self.assertEqual(g.colors.binding, "overall")

    def test_tokenizer_strings_and_numbers(self):
        doc = osgtext.parse(OSGT_HEAD + 'osg::Group {\n UniqueID 1\n Name "a \\"quoted\\" name"\n Data 1 {\n "x\ny" }\n}\n')
        obj = doc.root_objects()[0]
        self.assertEqual(obj.value("Name"), 'a "quoted" name')
        self.assertEqual(doc.version, 161)

    def test_flt_reader(self):
        sc = scenekit.load_scene(self.write("db.flt", flt_bytes(), binary=True))
        self.assertEqual(sc.fmt, "flt")
        rep = A.analyze(sc)
        self.assertEqual(rep.total.tris, 2)
        lods = [n for n in sc.iter_nodes() if n.kind == M.LOD]
        self.assertEqual(len(lods), 1)
        self.assertEqual(lods[0].ranges, [(0.0, 500.0)])

    # ------------------------------------------------------------ analysis
    def test_findings(self):
        rep = A.analyze(scenekit.load_scene(self.build_osgt()))
        codes = {f.code for f in rep.findings}
        self.assertIn("many_small_drawables", codes)
        self.assertIn("duplicate_vertices", codes)   # the triangle soups
        self.assertNotIn("duplicate_geometry", codes)  # the shared geode is instanced, not duplicated

    # ------------------------------------------------------------ optimisation
    def test_flt_saved_as_osgt(self):
        sc = scenekit.load_scene(self.write("db.flt", flt_bytes(), binary=True))
        out = os.path.join(self.tmp, "db.osgt")
        scenekit.save_scene(sc, out)
        self.assertEqual(A.analyze(scenekit.load_scene(out)).total.tris, 2)

    def test_flt_face_matrix(self):
        data = bytearray(flt_bytes())
        # insert a matrix record (translate x by 10) right after the first face record
        recs = []
        i = 0
        while i < len(data):
            op, ln = struct.unpack_from(">HH", data, i)
            recs.append(bytes(data[i:i + ln]))
            if op == 5 and not any(struct.unpack_from(">H", r, 0)[0] == 49 for r in recs):
                m = np.eye(4)
                m[3, 0] = 10.0  # row-vector convention: translation in the last row
                recs.append(struct.pack(">HH", 49, 68) + struct.pack(">16f", *m.ravel()))
            i += ln
        sc = scenekit.load_scene(self.write("mat.flt", b"".join(recs), binary=True))
        bb = A.analyze(sc).total.bbox
        self.assertAlmostEqual(float(bb[1][0]), 11.0)

if __name__ == "__main__":
    unittest.main()
