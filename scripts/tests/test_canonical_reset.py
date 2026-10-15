import importlib.util
from pathlib import Path
import tempfile
import unittest
import json
import subprocess
import sys

spec = importlib.util.spec_from_file_location('canonical_reset', Path(__file__).resolve().parents[1] / 'reset-canonical-workspace.py')
reset = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reset)


class CanonicalResetTests(unittest.TestCase):
    def test_explicit_apply_preserves_all_selected_bytes_and_records_each_rename(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            db = root / 'core.db'; db.write_bytes(b'database')
            wal = root / 'core.db-wal'; wal.write_bytes(b'wal')
            workspace = root / 'workspace'; workspace.mkdir()
            (workspace / '.env').write_bytes(b'ordinary-file')
            backup = root / 'backup'
            command = [sys.executable, str(Path(reset.__file__)), '--database', str(db), '--root', str(workspace), '--backup-directory', str(backup)]
            preview = subprocess.run(command, check=True, text=True, capture_output=True)
            planned = json.loads(preview.stdout)
            subprocess.run([*command, '--apply', '--confirm-stopped', '--confirm-set', planned['confirmation']], check=True, capture_output=True)
            manifest = json.loads((backup / 'manifest.json').read_text())
            self.assertEqual(len(manifest['completed']), 3)
            expected = {str(db): b'database', str(wal): b'wal'}
            for entry in manifest['paths']:
                target = Path(entry['destination'])
                self.assertFalse(Path(entry['source']).exists())
                if entry['source'] == str(workspace):
                    self.assertEqual((target / '.env').read_bytes(), b'ordinary-file')
                else:
                    self.assertEqual(target.read_bytes(), expected[entry['source']])
            self.assertEqual(backup.stat().st_mode & 0o777, 0o700)

    def test_dry_plan_preserves_bytes_and_changes_confirmation_after_source_change(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            db = root / 'core.db'; db.write_bytes(b'database')
            workspace = root / 'workspace'; workspace.mkdir()
            (workspace / '.env').write_text('retained')
            entries, fingerprint = reset.plan(db, [workspace], root / 'backup')
            self.assertEqual(len(entries), 2)
            self.assertEqual(db.read_bytes(), b'database')
            self.assertEqual((workspace / '.env').read_text(), 'retained')
            db.write_bytes(b'changed')
            self.assertNotEqual(fingerprint, reset.plan(db, [workspace], root / 'backup')[1])
            self.assertFalse((root / 'backup').exists())

    def test_link_overlap_and_existing_backup_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            db = root / 'core.db'; db.write_text('retained')
            workspace = root / 'workspace'; workspace.mkdir()
            link = root / 'link'; link.symlink_to(workspace)
            with self.assertRaisesRegex(ValueError, 'links'):
                reset.plan(db, [link], root / 'backup')
            with self.assertRaisesRegex(ValueError, 'overlap'):
                reset.plan(db, [root], root.parent / (root.name + '-backup'))
            (root / 'backup').mkdir()
            with self.assertRaisesRegex(ValueError, 'new'):
                reset.plan(db, [workspace], root / 'backup')
            self.assertEqual(db.read_text(), 'retained')
