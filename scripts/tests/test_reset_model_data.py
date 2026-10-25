import importlib.util
from pathlib import Path
import tempfile
import unittest

MODULE_PATH = Path(__file__).resolve().parents[1] / 'reset-model-data.py'
spec = importlib.util.spec_from_file_location('reset_model_data', MODULE_PATH)
cutover = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cutover)


class DisposableModelResetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='model-cutover-test-')
        self.parent = Path(self.temp.name).resolve()
        self.root = self.parent / 'disposable'
        self.root.mkdir()
        self.backup = self.parent / 'preserved'
        self.repo = self.parent / 'repository'
        self.repo.mkdir()
        self.files = {'core.db': b'opaque-db\0bytes', 'core.db-wal': b'wal',
                      'credential-protection-keys/key.xml': b'fake-protected-key',
                      'workspaces/file.bin': b'\x00\xffretained', 'artifacts/file.bin': b'artifact'}
        for name, content in self.files.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)

    def tearDown(self):
        self.temp.cleanup()

    def test_default_plan_changes_nothing(self):
        self.assertIn('plan only', cutover.reset(self.root, self.backup, self.repo))
        self.assertFalse(self.backup.exists())
        for name, content in self.files.items():
            self.assertEqual(content, (self.root / name).read_bytes())

    def test_execution_preserves_all_bytes_and_recreates_an_empty_private_root(self):
        unrelated = self.parent / 'unrelated.db'
        unrelated.write_bytes(b'unrelated')
        self.assertIn('preserved the old data', cutover.reset(self.root, self.backup, self.repo,
                      execute=True, services_stopped=True, confirmation=str(self.root)))
        self.assertEqual([], list(self.root.iterdir()))
        self.assertEqual(0o700, self.root.stat().st_mode & 0o777)
        self.assertEqual(b'unrelated', unrelated.read_bytes())
        for name, content in self.files.items():
            self.assertEqual(content, (self.backup / name).read_bytes())

    def test_actual_sqlite_backup_remains_readable_without_importing_old_identity(self):
        import sqlite3
        with sqlite3.connect(self.root / 'store.db') as database:
            database.execute('CREATE TABLE RetainedIdentity (Id TEXT PRIMARY KEY)')
            database.execute("INSERT INTO RetainedIdentity VALUES ('disposable-old-identity')")
        cutover.reset(self.root, self.backup, self.repo, execute=True,
                      services_stopped=True, confirmation=str(self.root))
        with sqlite3.connect(f'file:{self.backup}/store.db?mode=ro', uri=True) as preserved:
            self.assertEqual('ok', preserved.execute('PRAGMA quick_check').fetchone()[0])
            self.assertEqual('disposable-old-identity', preserved.execute('SELECT Id FROM RetainedIdentity').fetchone()[0])
        self.assertFalse((self.root / 'store.db').exists())

    def test_incomplete_confirmation_refuses_before_moving(self):
        for stopped, confirmation in [(False, str(self.root)), (True, 'yes'), (True, None)]:
            with self.subTest(stopped=stopped, confirmation=confirmation), self.assertRaises(ValueError):
                cutover.reset(self.root, self.backup, self.repo, execute=True,
                              services_stopped=stopped, confirmation=confirmation)
        self.assertFalse(self.backup.exists())
        self.assertTrue((self.root / 'core.db').exists())

    def test_unsafe_or_existing_destination_refuses(self):
        for destination in [self.root / 'backup', self.root, self.parent, self.repo]:
            with self.subTest(destination=destination), self.assertRaises(ValueError):
                cutover.reset(self.root, destination, self.repo)

    def test_symlink_tree_refuses_without_touching_the_target(self):
        target = self.parent / 'outside'
        target.write_bytes(b'outside')
        (self.root / 'linked').symlink_to(target)
        with self.assertRaises(ValueError):
            cutover.reset(self.root, self.backup, self.repo)
        self.assertEqual(b'outside', target.read_bytes())

    def test_repository_and_ancestors_cannot_be_data_roots(self):
        for root in [self.repo, self.parent, Path('/')]:
            with self.subTest(root=root), self.assertRaises(ValueError):
                cutover.reset(root, self.backup, self.repo)


if __name__ == '__main__':
    unittest.main()
