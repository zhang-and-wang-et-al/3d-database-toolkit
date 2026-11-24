"""SQLite + CSV index built from the per-model ``.info.json`` records.

Tables
------
models  one row per model file (flat columns, see ``schema.FLAT_COLUMNS``)
        plus ``record_json`` holding the complete record.
issues  one row per issue (model_path, code, severity, message).
View ``duplicate_files`` lists files with identical SHA-256.

Example queries::

    SELECT path, triangles FROM models WHERE status = 'broken';
    SELECT poly_tier, COUNT(*) FROM models GROUP BY poly_tier;
    SELECT * FROM duplicate_files;
"""

import csv
import hashlib
import json
import os
import shutil
import sqlite3
import threading

from . import config, netpaths, schema

INDEX_NAME = "showcase_index.sqlite"
CSV_NAME = "showcase_index.csv"


class Index:
    """SQLite index. On network shares SQLite's file locking is unreliable, so
    the database is worked on in a local cache copy and copied to the share
    (``path``) when the index is closed or ``publish()`` is called."""

    def __init__(self, path):
        self.path = os.path.abspath(path)
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        self.lock = threading.Lock()
        self.work_path = self.path
        if netpaths.is_network_path(self.path):
            key = hashlib.sha1(self.path.encode("utf-8")).hexdigest()[:16]
            self.work_path = os.path.join(config.user_cache_dir(), f"index_{key}.sqlite")
            os.makedirs(os.path.dirname(self.work_path), exist_ok=True)
            if os.path.exists(self.path):
                netpaths.retry(shutil.copy2, self.path, self.work_path)
            elif os.path.exists(self.work_path):
                os.remove(self.work_path)
        self.db = sqlite3.connect(self.work_path, check_same_thread=False)
        cols = ", ".join(f'"{c}" {t}' for c, t in schema.FLAT_COLUMNS if c != "path")
        with self.db:
            self.db.execute(f'CREATE TABLE IF NOT EXISTS models (path TEXT PRIMARY KEY, {cols}, record_json TEXT)')
            existing = {r[1] for r in self.db.execute("PRAGMA table_info(models)")}
            for c, t in schema.FLAT_COLUMNS:  # forward-compatible: add new columns
                if c not in existing:
                    self.db.execute(f'ALTER TABLE models ADD COLUMN "{c}" {t}')
            self.db.execute("CREATE TABLE IF NOT EXISTS issues (model_path TEXT, code TEXT, severity TEXT, message TEXT)")
            for col in ("status", "poly_tier", "sha256", "format", "extension"):
                self.db.execute(f'CREATE INDEX IF NOT EXISTS idx_models_{col} ON models("{col}")')
            self.db.execute("CREATE INDEX IF NOT EXISTS idx_issues_path ON issues(model_path)")
            self.db.execute("CREATE INDEX IF NOT EXISTS idx_issues_code ON issues(code)")
            self.db.execute("""CREATE VIEW IF NOT EXISTS duplicate_files AS
                SELECT sha256, COUNT(*) AS copies, GROUP_CONCAT(path, ' | ') AS paths
                FROM models WHERE sha256 IS NOT NULL AND sha256 != ''
                GROUP BY sha256 HAVING COUNT(*) > 1""")

    def upsert(self, record, json_path):
        row = schema.flatten(record)
        folder = os.path.dirname(os.path.abspath(json_path))
        row["info_json"] = os.path.abspath(json_path)
        for key in ("preview_image", "sheet_image"):
            if row.get(key):
                row[key] = os.path.join(folder, row[key])
        row["record_json"] = json.dumps(record, ensure_ascii=False)
        cols = list(row)
        sql = (f'INSERT OR REPLACE INTO models ({", ".join(chr(34) + c + chr(34) for c in cols)}) '
               f'VALUES ({", ".join("?" for _ in cols)})')
        with self.lock, self.db:
            self.db.execute(sql, [row[c] for c in cols])
            self.db.execute("DELETE FROM issues WHERE model_path = ?", (row["path"],))
            self.db.executemany(
                "INSERT INTO issues (model_path, code, severity, message) VALUES (?, ?, ?, ?)",
                [(row["path"], i.get("code"), i.get("severity"), i.get("message")) for i in record.get("issues", [])])

    def export_csv(self, csv_path=None):
        csv_path = csv_path or os.path.join(os.path.dirname(os.path.abspath(self.path)), CSV_NAME)
        cols = [c for c, _ in schema.FLAT_COLUMNS]
        with self.lock:
            rows = self.db.execute(f'SELECT {", ".join(chr(34) + c + chr(34) for c in cols)} FROM models ORDER BY path').fetchall()
        tmp = f"{csv_path}.{os.getpid()}.tmp"

        def write():
            with open(tmp, "w", newline="", encoding="utf-8-sig") as fh:
                w = csv.writer(fh)
                w.writerow(cols)
                w.writerows(rows)
            os.replace(tmp, csv_path)
        netpaths.retry(write)
        return csv_path

    def summary(self):
        with self.lock:
            return dict(self.db.execute("SELECT status, COUNT(*) FROM models GROUP BY status").fetchall())

    def publish(self):
        """Copy the local working database to its real location (network share)."""
        if self.work_path == self.path:
            return
        with self.lock:
            self.db.commit()
            tmp = f"{self.path}.{os.getpid()}.tmp"

            def copy():
                shutil.copy2(self.work_path, tmp)
                os.replace(tmp, self.path)
            netpaths.retry(copy)

    def close(self):
        self.publish()
        with self.lock:
            self.db.close()


def iter_records(folder):
    for dirpath, _, files in os.walk(folder):
        for f in files:
            if f.endswith(schema.SUFFIX_JSON):
                p = os.path.join(dirpath, f)
                try:
                    with open(p, encoding="utf-8") as fh:
                        yield p, json.load(fh)
                except (OSError, ValueError):
                    continue


def rebuild(folder, index_path=None, log=print):
    """(Re)build the index from all ``.info.json`` files below ``folder``."""
    index_path = index_path or os.path.join(folder, INDEX_NAME)
    idx = Index(index_path)
    n = 0
    for p, rec in iter_records(folder):
        idx.upsert(rec, p)
        n += 1
    csv_path = idx.export_csv()
    idx.close()
    log(f"Indexed {n} record(s) -> {index_path} and {csv_path}")
    return n
