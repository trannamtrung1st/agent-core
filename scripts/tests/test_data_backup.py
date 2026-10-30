"""Cold-copy path/transaction refusal tests; production-host recovery separately qualifies real protected state."""
from contextlib import closing
import fcntl
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

module = importlib.util.spec_from_file_location('data_backup', Path(__file__).resolve().parents[1] / 'data-backup.py')
backup = importlib.util.module_from_spec(module)
module.loader.exec_module(backup)


class ColdBackupTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.base = Path(self.temporary.name).resolve()
        self.root = self.base / 'installation'
        self.root.mkdir(mode=0o700)
        self.spec = {'version': 1, 'profile': backup.PROFILE, 'source': 'a' * 40, 'database': 'app.db',
            'roots': {name: name for name in backup.ROLES}}
        for name in backup.ROLES:
            (self.root / name).mkdir(mode=0o700)
        (self.root / 'workspaces' / 'script.sh').write_bytes(b'#!/bin/sh\nexit 0\n')
        (self.root / 'workspaces' / 'script.sh').chmod(0o700)
        (self.root / 'app.db.owner.lock').touch(mode=0o600)
        with closing(sqlite3.connect(self.root / 'app.db')) as db, db:
            for table in ('__EFMigrationsHistory', '__FormalWorkflowManagementMigrations', '__FormalWorkflowRuntimeMigrations'):
                db.execute('CREATE TABLE "' + table + '"(MigrationId TEXT PRIMARY KEY)')
                db.execute('INSERT INTO "' + table + '" VALUES (?)', ('fixture-schema',))
            db.execute('CREATE TABLE Retained(id INTEGER PRIMARY KEY, value TEXT)')
            db.execute('INSERT INTO Retained VALUES (1, ?)', ('retained state',))
            db.execute('CREATE TABLE Attachments(BlobKey TEXT,ByteSize INTEGER,Sha256Hex TEXT,ContentDeletedAtUtc INTEGER)')
            db.execute('CREATE TABLE Artifacts(BlobKey TEXT,ByteSize INTEGER,Sha256Hex TEXT,SessionId TEXT)')
            db.execute('CREATE TABLE Sessions(SessionId TEXT,DurablyDeletedAtUtc INTEGER)')
            for table in ('AgentDefinitionDraftResources', 'AgentDefinitionPublicationResources', 'SpaceFileResources'):
                db.execute('CREATE TABLE "' + table + '"(ContentSha256 TEXT,ByteLength INTEGER)')
            db.execute('CREATE TABLE AgentInstanceResources(PayloadJson TEXT)')
        self.archive = self.base / 'snapshot'
        self.target = self.base / 'restored'
        self.build_patch = patch.object(backup, 'build_identity', return_value={'fixture': 'digest'})
        self.protected_patch = patch.object(backup, 'protected_check', return_value=0)
        self.build_patch.start()
        self.protected_patch.start()

    def tearDown(self):
        self.build_patch.stop()
        self.protected_patch.stop()
        self.temporary.cleanup()

    def capture(self):
        return backup.backup(self.root, self.spec, self.archive, Path('/unused'), True, str(self.root))

    def test_snapshot_restore_preserves_sqlite_bytes_file_mode_and_timestamp(self):
        self.capture()
        before = backup.inventory(self.archive / 'payload', 'app.db')
        self.assertTrue(backup.restore(self.archive, self.target, Path('/unused'))['planOnly'])
        self.assertFalse(self.target.exists())
        backup.restore(self.archive, self.target, Path('/unused'), execute=True)
        self.assertEqual(backup.inventory(self.target, 'app.db'), before)
        with closing(sqlite3.connect(self.target / 'app.db')) as db:
            self.assertEqual(db.execute('SELECT value FROM Retained').fetchall(), [('retained state',)])
        self.assertEqual(self.archive.stat().st_mode & 0o777, 0o700)
        self.assertEqual((self.target / 'workspaces' / 'script.sh').stat().st_mode & 0o777, 0o700)

    def test_live_wal_commits_are_in_normalized_snapshot(self):
        with closing(sqlite3.connect(self.root / 'app.db')) as db, db:
            db.execute('PRAGMA journal_mode=WAL')
            db.execute('PRAGMA wal_autocheckpoint=0')
            db.execute('INSERT INTO Retained VALUES (2, ?)', ('committed WAL',))
            db.commit()
            self.assertGreater((self.root / 'app.db-wal').stat().st_size, 0)
            self.capture()
        with closing(sqlite3.connect(self.archive / 'payload' / 'app.db')) as db:
            self.assertEqual(db.execute('SELECT count(*) FROM Retained').fetchone()[0], 2)

    def test_active_owner_refuses_before_copy(self):
        with (self.root / 'app.db.owner.lock').open('rb') as stream:
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaisesRegex(backup.Refused, 'active_installation_owner'):
                self.capture()
        self.assertFalse(self.archive.exists())

    def test_explicit_stop_and_exact_confirmation_required(self):
        for stopped, confirmation in ((False, str(self.root)), (True, 'wrong')):
            with self.assertRaisesRegex(backup.Refused, 'confirmation_required'):
                backup.backup(self.root, self.spec, self.archive, Path('/unused'), stopped, confirmation)
        self.assertFalse(self.archive.exists())

    def test_omitted_overlapping_and_external_roots_refuse(self):
        for roots in ({'keys': 'keys'}, {name: 'same' for name in backup.ROLES},
                dict(self.spec['roots'], keys='../keys')):
            with self.assertRaises(backup.Refused):
                backup.layout(dict(self.spec, roots=roots))
        shutil.rmtree(self.root / 'keys')
        with self.assertRaisesRegex(backup.Refused, 'required_root_missing'):
            self.capture()

    def test_symlink_and_hardlink_refuse_without_following(self):
        outside = self.base / 'outside-private'
        outside.write_bytes(b'outside private data')
        target = self.root / 'workspaces' / 'link'
        target.symlink_to(outside)
        with self.assertRaisesRegex(backup.Refused, 'linked_file_refused'):
            self.capture()
        target.unlink()
        os.link(outside, target)
        with self.assertRaisesRegex(backup.Refused, 'linked_file_refused'):
            self.capture()
        self.assertFalse(self.archive.exists())

    def test_corrupt_or_omitted_payload_refuses_before_target_mutation(self):
        self.capture()
        file = self.archive / 'payload' / 'workspaces' / 'script.sh'
        file.write_bytes(b'corrupted')
        with self.assertRaisesRegex(backup.Refused, 'digest_or_inventory_mismatch'):
            backup.restore(self.archive, self.target, Path('/unused'), execute=True)
        self.assertFalse(self.target.exists())

    def test_occupied_destination_is_unchanged(self):
        self.capture()
        self.target.mkdir(mode=0o700)
        marker = self.target / 'original'
        marker.write_bytes(b'keep original')
        with self.assertRaisesRegex(backup.Refused, 'fresh_destination_required'):
            backup.restore(self.archive, self.target, Path('/unused'), execute=True)
        self.assertEqual(marker.read_bytes(), b'keep original')

    def test_wrong_build_and_protection_refuse_before_copy(self):
        self.capture()
        with patch.object(backup, 'build_identity', return_value={'other': 'build'}):
            with self.assertRaisesRegex(backup.Refused, 'matching_build_required'):
                backup.restore(self.archive, self.target, Path('/unused'), execute=True)
        with patch.object(backup, 'protected_check', side_effect=backup.Refused('protected_state_or_schema_mismatch')):
            with self.assertRaisesRegex(backup.Refused, 'protected_state_or_schema_mismatch'):
                backup.restore(self.archive, self.target, Path('/unused'), execute=True)
        self.assertFalse(self.target.exists())

    def test_interrupted_copy_never_publishes_complete_backup(self):
        with patch.object(backup, 'copy_tree', side_effect=OSError('failure injection')):
            with self.assertRaises(OSError):
                self.capture()
        self.assertFalse(self.archive.exists())
        self.assertEqual(len(list(self.base.glob('.partial-backup-*'))), 1)

    def test_manifest_path_traversal_and_unlisted_file_refuse(self):
        self.capture()
        path = self.archive / 'manifest.json'
        original = json.loads(path.read_text())
        changed = dict(original, entries=[{'path': '../outside', 'type': 'file'}])
        path.write_text(json.dumps(changed))
        with self.assertRaises(backup.Refused):
            backup.restore(self.archive, self.target, Path('/unused'), execute=True)
        path.write_text(json.dumps(original))
        (self.archive / 'payload' / 'keys' / 'extra').write_bytes(b'extra')
        with self.assertRaises(backup.Refused):
            backup.restore(self.archive, self.target, Path('/unused'), execute=True)
        self.assertFalse(self.target.exists())

    def test_unsealed_sqlite_sidecars_refuse(self):
        self.capture()
        (self.archive / 'payload' / 'app.db-wal').write_bytes(b'unsealed state')
        with self.assertRaisesRegex(backup.Refused, 'unsealed_database_sidecar'):
            backup.restore(self.archive, self.target, Path('/unused'), execute=True)
        self.assertFalse(self.target.exists())

    def test_empty_or_swapped_retained_attachment_root_refuses_before_sealing(self):
        with closing(sqlite3.connect(self.root / 'app.db')) as db, db:
            db.execute('INSERT INTO Attachments VALUES (?,?,?,NULL)', ('owned-blob', 4, 'a' * 64))
        with self.assertRaises((backup.Refused, OSError)):
            self.capture()
        (self.root / 'attachments' / 'owned-blob').write_bytes(b'wrong')
        with self.assertRaisesRegex(backup.Refused, 'referenced_content_missing_or_changed'):
            self.capture()
        self.assertFalse(self.archive.exists())

    def test_retained_artifact_and_each_resource_catalog_require_matching_bytes(self):
        for catalog in ('Artifacts', 'AgentDefinitionDraftResources', 'AgentDefinitionPublicationResources', 'SpaceFileResources', 'AgentInstanceResources'):
            with self.subTest(catalog=catalog):
                with closing(sqlite3.connect(self.root / 'app.db')) as db, db:
                    if catalog == 'Artifacts':
                        db.execute('INSERT INTO Sessions VALUES (?,NULL)', ('active-session',))
                        db.execute('INSERT INTO Artifacts VALUES (?,?,?,?)', ('missing-artifact', 4, 'a' * 64, 'active-session'))
                    elif catalog == 'AgentInstanceResources':
                        db.execute('INSERT INTO AgentInstanceResources VALUES (?)', (json.dumps({'contentSha256': 'a' * 64, 'byteLength': 4}),))
                    else:
                        db.execute('INSERT INTO "' + catalog + '" VALUES (?,?)', ('a' * 64, 4))
                with self.assertRaises(backup.Refused):
                    backup.content_check(self.root, self.spec)
                with closing(sqlite3.connect(self.root / 'app.db')) as db, db:
                    db.execute('DELETE FROM "' + catalog + '"')


if __name__ == '__main__':
    unittest.main()
