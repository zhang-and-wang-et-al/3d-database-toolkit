"""Tests for the parts that do not need Blender: python -m unittest discover tests"""

import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from showcase import index_db, schema  # noqa: E402
from showcase.selection import Selection  # noqa: E402


def touch(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, "w").close()


class SelectionTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        for rel in ("a/one.fbx", "a/two.obj", "a/notes.txt", "b/three.glb", "b/deep/four.stl", "c/five.ply",
                    "d/sim.flt", "d/old.max", "d/tex.rgb"):
            touch(os.path.join(self.tmp, rel))
        touch(os.path.join(self.tmp, "c", ".noshowcase"))

    def names(self, sel):
        return sorted(os.path.basename(p) for _, p in sel.iter_models())

    def test_default_includes_models_and_honours_marker(self):
        sel = Selection([self.tmp])
        self.assertEqual(self.names(sel), ["four.stl", "old.max", "one.fbx", "sim.flt", "three.glb", "two.obj"])

    def test_exclude_folder_then_reinclude_file(self):
        sel = Selection([self.tmp])
        sel.set(os.path.join(self.tmp, "b"), False)
        sel.set(os.path.join(self.tmp, "d"), False)
        self.assertEqual(self.names(sel), ["one.fbx", "two.obj"])
        sel.set(os.path.join(self.tmp, "b", "deep", "four.stl"), True)
        self.assertEqual(self.names(sel), ["four.stl", "one.fbx", "two.obj"])
        self.assertEqual(sel.dir_state(os.path.join(self.tmp, "b")), "partial")
        # toggling the parent again clears the overrides below it
        sel.set(os.path.join(self.tmp, "b"), True)
        self.assertEqual(list(sel.overrides.values()), [False])  # only folder d stays excluded

    def test_marker_folder_can_be_forced_on(self):
        sel = Selection([self.tmp])
        sel.set(os.path.join(self.tmp, "c"), True)
        self.assertIn("five.ply", self.names(sel))


class SchemaTest(unittest.TestCase):
    def test_status_and_tiers(self):
        rec = {"issues": []}
        schema.add_issue(rec, "open_boundaries")
        self.assertEqual(schema.finalize(rec)["status"], "ok")
        schema.add_issue(rec, "missing_textures")
        self.assertEqual(schema.finalize(rec)["status"], "warning")
        self.assertEqual(schema.compute_status([schema.make_issue("unsupported_format")]), "unsupported")
        schema.add_issue(rec, "import_failed")
        self.assertEqual(schema.finalize(rec)["status"], "broken")
        self.assertEqual(rec["issues"][0]["code"], "import_failed")
        self.assertEqual(schema.poly_tier(500), "very_low")
        self.assertEqual(schema.poly_tier(50_000), "medium")
        self.assertEqual(schema.poly_tier(5_000_000), "very_high")

    def test_report_and_index_roundtrip(self):
        tmp = tempfile.mkdtemp()
        rec = schema.finalize({"issues": [], "file": {"path": os.path.join(tmp, "x.fbx"), "name": "x.fbx"},
                               "geometry": {"triangles": 12, "vertices": 8},
                               "render": {"sheet_image": "x.fbx.showcase.jpg"}})
        self.assertIn("x.fbx", schema.format_report(rec))
        idx = index_db.Index(os.path.join(tmp, "i.sqlite"))
        idx.upsert(rec, os.path.join(tmp, "x.fbx.info.json"))
        idx.upsert(rec, os.path.join(tmp, "x.fbx.info.json"))  # upsert, not duplicate
        self.assertEqual(idx.summary(), {"ok": 1})
        csv_path = idx.export_csv()
        idx.close()
        with open(csv_path, encoding="utf-8-sig") as fh:
            self.assertEqual(len(fh.read().strip().splitlines()), 2)


if __name__ == "__main__":
    unittest.main()
