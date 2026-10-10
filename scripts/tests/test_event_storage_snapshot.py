import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("event_storage_snapshot", Path(__file__).parents[1] / "event-storage-snapshot.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class EventStorageSnapshotTests(unittest.TestCase):
    def test_wal_counts_growth_without_exporting_or_modifying_evidence(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "fixture.db"
            with sqlite3.connect(path) as writer:
                writer.execute("PRAGMA journal_mode=WAL")
                for table, columns in module.TABLES.items():
                    fields = [f'"{column}" TEXT' for column in columns]
                    if table == "CoreEventBuckets":
                        fields.append("Flushed INTEGER")
                    writer.execute(f'CREATE TABLE "{table}" ({", ".join(fields)})')
                private = json.dumps({"private": "PAYLOAD-SECRET", "Sources": [{}, {}]})
                writer.execute("INSERT INTO CoreEventBuckets VALUES (?, ?, 0)", (private, '["SOURCE-SECRET"]'))
                writer.execute("INSERT INTO ExternalEvents VALUES (?)", (private,))
                writer.commit()
                before = writer.total_changes
                first = module.snapshot(path)
                self.assertEqual(first["tables"]["ExternalEvents"]["rows"], 1)
                self.assertEqual(first["tables"]["ExternalEvents"]["json_bytes"], len(private.encode()))
                self.assertEqual(first["bucket_source_references"], 2)
                self.assertEqual(first["reviewed_source_references"], 1)
                self.assertEqual(first["unreviewed_source_references"], 1)
                self.assertEqual(first["pending_buckets"], 1)
                self.assertNotIn("SECRET", json.dumps(first))
                self.assertEqual(writer.total_changes, before)
                self.assertEqual(writer.execute("SELECT * FROM CoreEventBuckets").fetchone(), (private, '["SOURCE-SECRET"]', 0))
                writer.execute("INSERT INTO ExternalEvents VALUES (?)", (private,))
                # Consistent read excludes another connection's uncommitted rows.
                self.assertEqual(module.snapshot(path)["tables"]["ExternalEvents"]["rows"], 1)
                writer.commit()
                self.assertEqual(module.snapshot(path)["tables"]["ExternalEvents"]["rows"], 2)

    def test_missing_database_is_not_created(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "missing.db"
            with self.assertRaises(FileNotFoundError):
                module.snapshot(path)
            self.assertFalse(path.exists())

    def test_incomplete_schema_does_not_report_misleading_zero_counts(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "incomplete.db"
            sqlite3.connect(path).close()
            with self.assertRaises(sqlite3.OperationalError):
                module.snapshot(path)


if __name__ == "__main__":
    unittest.main()
