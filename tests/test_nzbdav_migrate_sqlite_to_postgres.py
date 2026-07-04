import importlib.util
import json
import pathlib
import sys
import tempfile
import unittest


SCRIPT_PATH = pathlib.Path(__file__).resolve().parents[1] / "scripts" / "nzbdav_migrate_sqlite_to_postgres.py"
SPEC = importlib.util.spec_from_file_location("nzbdav_migrate_sqlite_to_postgres", SCRIPT_PATH)
nzbdav_migrate_sqlite_to_postgres = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = nzbdav_migrate_sqlite_to_postgres
SPEC.loader.exec_module(nzbdav_migrate_sqlite_to_postgres)


class PostgresMigrationHelperTests(unittest.TestCase):
    def test_snapshot_total_rows_uses_total_rows_when_present(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "snapshot.json"
            path.write_text(json.dumps({"TotalRows": 12, "Items": [{}]}), encoding="utf-8")

            self.assertEqual(nzbdav_migrate_sqlite_to_postgres.snapshot_total_rows(path), 12)

    def test_snapshot_total_rows_sums_tables_when_total_missing(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "snapshot.json"
            path.write_text(json.dumps({"Items": [{}, {}], "QueueItems": [{}], "Version": 1}), encoding="utf-8")

            self.assertEqual(nzbdav_migrate_sqlite_to_postgres.snapshot_total_rows(path), 3)

    def test_copy_blobs_copies_only_blob_tree_and_not_cache(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            source = root / "sqlite"
            target = root / "postgres"
            (source / "blobs" / "aa").mkdir(parents=True)
            (source / "cache" / "segments").mkdir(parents=True)
            (source / "blobs" / "aa" / "blob").write_text("blob", encoding="utf-8")
            (source / "cache" / "segments" / "cache-file").write_text("cache", encoding="utf-8")

            summary = nzbdav_migrate_sqlite_to_postgres.copy_blobs(source, target, replace=False, dry_run=False)

            self.assertTrue(summary["copied"])
            self.assertTrue(summary["cache_excluded"])
            self.assertTrue((target / "blobs" / "aa" / "blob").exists())
            self.assertFalse((target / "cache").exists())

    def test_copy_blobs_replaces_empty_target_blob_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            source = root / "sqlite"
            target = root / "postgres"
            (source / "blobs").mkdir(parents=True)
            (target / "blobs").mkdir(parents=True)
            (source / "blobs" / "blob").write_text("blob", encoding="utf-8")

            summary = nzbdav_migrate_sqlite_to_postgres.copy_blobs(source, target, replace=False, dry_run=False)

            self.assertTrue(summary["copied"])
            self.assertTrue((target / "blobs" / "blob").exists())

    def test_redact_command_hides_postgres_connection_string(self):
        command = [
            "docker",
            "run",
            "-e",
            "NZBDAV_DATABASE_CONNECTION_STRING=Host=db;Password=secret",
        ]

        self.assertEqual(
            nzbdav_migrate_sqlite_to_postgres.redact_command(command)[3],
            "NZBDAV_DATABASE_CONNECTION_STRING=***REDACTED***",
        )


if __name__ == "__main__":
    unittest.main()
