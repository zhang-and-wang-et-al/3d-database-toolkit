"""Network-folder behaviour, simulated (no real share needed): python -m unittest discover tests"""

import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from showcase import config, index_db, netpaths, runner, schema  # noqa: E402


class NetPathTest(unittest.TestCase):
    def test_resolve_plain_and_file_url(self):
        d = tempfile.mkdtemp()
        self.assertEqual(netpaths.resolve_user_path(f'  "{d}" '), os.path.abspath(d))
        self.assertEqual(netpaths.resolve_user_path("file://" + d), os.path.abspath(d))
        with self.assertRaises(ValueError):
            netpaths.resolve_user_path(os.path.join(d, "does_not_exist"))

    def test_unmounted_smb_url_gives_helpful_error(self):
        with self.assertRaises(ValueError) as cm:
            netpaths.resolve_user_path("smb://nas-that-does-not-exist/share/models")
        self.assertIn("mount", str(cm.exception).lower())

    def test_root_key_for_unc_and_posix(self):
        self.assertEqual(runner.root_key("/mnt/nas/models"), "mnt_nas_models")
        with mock.patch("os.path.abspath", lambda p: p), mock.patch("os.path.normpath", lambda p: p):
            self.assertEqual(runner.root_key(r"\\server\share\models"), "server_share_models")

    def test_retry_recovers_from_transient_errors(self):
        calls = []

        def flaky():
            calls.append(1)
            if len(calls) < 3:
                raise OSError("network hiccup")
            return "ok"
        self.assertEqual(netpaths.retry(flaky, delay=0.01), "ok")
        self.assertEqual(len(calls), 3)


class ReadOnlyAndIndexTest(unittest.TestCase):
    def setUp(self):
        self.lib = tempfile.mkdtemp()
        self.model = os.path.join(self.lib, "sub", "chair.obj")
        os.makedirs(os.path.dirname(self.model))
        with open(self.model, "w") as fh:
            fh.write("v 0 0 0\n")
        self.fallback = tempfile.mkdtemp()
        self.settings = config.Settings(fallback_output_dir=self.fallback)

    def test_read_only_folder_uses_fallback(self):
        with mock.patch.object(netpaths, "is_writable_dir", return_value=False):
            base = runner.output_base(self.lib, self.model, self.settings, check_writable=True)
            self.assertTrue(base.startswith(self.fallback))
            self.assertTrue(base.endswith(os.path.join("sub", "chair.obj")))
            self.assertTrue(runner.index_path_for(self.lib, self.settings).startswith(self.fallback))
        # results written to the fallback are found again (GUI / skip logic)
        os.makedirs(os.path.dirname(base), exist_ok=True)
        netpaths.write_text_atomic(base + schema.SUFFIX_JSON, schema.dumps({"status": "ok", "issues": []}))
        rec, found = runner.find_record(self.lib, self.model, self.settings)
        self.assertEqual((rec["status"], found), ("ok", base))

    def test_long_windows_paths_get_short_output_names(self):
        deep = os.path.join(self.lib, *(["very_long_folder_name_" * 2] * 6), "model_with_a_long_name.fbx")
        self.assertTrue(runner.too_long(deep, system="Windows"))
        self.assertFalse(runner.too_long(deep, system="Linux"))
        with mock.patch.object(runner.platform, "system", return_value="Windows"):
            base = runner.output_base(self.lib, deep, self.settings, check_writable=True)
            self.assertIn("_long_paths", base)
            self.assertLess(len(base) + runner.LONGEST_SUFFIX, runner.WIN_MAX_PATH + len(self.fallback))
            self.assertIn(base, runner.candidate_bases(self.lib, deep, self.settings))

    def test_up_to_date_tolerates_coarse_network_timestamps(self):
        info = runner.file_info(self.lib, self.model, do_hash=False)
        st = os.stat(self.model)
        os.utime(self.model, (st.st_atime, st.st_mtime + 1.5))  # SMB/FAT round to 2 s
        rec = schema.finalize({"issues": [], "file": info, "render": {}})
        rec["status"] = "broken"  # broken records need no images
        j = self.model + schema.SUFFIX_JSON
        netpaths.write_text_atomic(j, schema.dumps(rec))
        self.assertTrue(runner.is_up_to_date(j, self.model, self.settings)[0])

    def test_index_on_network_share_uses_local_working_copy(self):
        target = os.path.join(self.lib, index_db.INDEX_NAME)
        cache = tempfile.mkdtemp()
        with mock.patch.object(netpaths, "is_network_path", return_value=True), \
                mock.patch.object(config, "user_cache_dir", return_value=cache):
            idx = index_db.Index(target)
            self.assertNotEqual(idx.work_path, idx.path)
            self.assertTrue(idx.work_path.startswith(cache))
            idx.upsert(schema.finalize({"issues": [], "file": {"path": self.model, "name": "chair.obj"}}),
                       self.model + schema.SUFFIX_JSON)
            self.assertFalse(os.path.exists(target))  # nothing written to the share yet
            idx.close()
            self.assertTrue(os.path.exists(target))   # published on close
            idx2 = index_db.Index(target)              # re-opened: copied back from the share
            self.assertEqual(idx2.summary(), {"ok": 1})
            idx2.close()


if __name__ == "__main__":
    unittest.main()
