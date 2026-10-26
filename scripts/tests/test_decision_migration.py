import hashlib
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class DecisionMigrationPreflightTests(unittest.TestCase):
    def test_read_only_inventory_requires_matching_backup_and_refuses_immutable_legacy(self):
        with tempfile.TemporaryDirectory() as root:
            database, backup = Path(root) / 'store.db', Path(root) / 'backup.db'
            with sqlite3.connect(database) as db:
                db.executescript('''
                    CREATE TABLE Sessions(DefinitionJson TEXT);
                    CREATE TABLE AgentInstances(UnattendedOfferingId TEXT,UnattendedReasoningEffort TEXT);
                    CREATE TABLE Automations(ModelOverrideOfferingId TEXT,ModelOverrideReasoningEffort TEXT);
                    CREATE TABLE AgentRuns(Status INTEGER,PayloadJson TEXT);
                    CREATE TABLE AgentDefinitionPublications(PayloadJson TEXT);
                    CREATE TABLE AgentDefinitionDrafts(CandidateJson TEXT);
                    CREATE TABLE TenantApprovalRequests(PayloadJson TEXT);
                    CREATE TABLE __EFMigrationsHistory(MigrationId TEXT);
                    INSERT INTO __EFMigrationsHistory VALUES ('20261026090000_DeepSeekFlash1017');
                ''')
            def snapshot():
                with sqlite3.connect(database) as source, sqlite3.connect(backup) as target:
                    source.backup(target)
            def run():
                return subprocess.run([sys.executable, str(ROOT / 'scripts/decision-migration-preflight.py'), str(database), '--backup', str(backup)], capture_output=True, text=True)
            snapshot()
            before = hashlib.sha256(database.read_bytes()).digest()
            self.assertEqual(0, run().returncode)
            self.assertEqual(before, hashlib.sha256(database.read_bytes()).digest())
            with sqlite3.connect(database) as db:
                db.execute('INSERT INTO Sessions VALUES (?)', ('{}',))
            self.assertNotEqual(0, run().returncode)  # Backup inventory no longer matches.
            with sqlite3.connect(database) as db:
                db.execute('INSERT INTO AgentDefinitionPublications VALUES (?)', ('{"modelDefaults":{"offeringId":"private-fixture-sentinel"}}',))
            snapshot()
            result = run()
            self.assertNotEqual(0, result.returncode)
            self.assertIn('legacy_immutable_publications', result.stdout)
            self.assertNotIn('private-fixture-sentinel', result.stdout + result.stderr)
