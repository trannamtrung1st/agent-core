#!/usr/bin/env python3
"""Read-only count inventory before the offline Decision Intelligence cutover."""
import argparse
import json
from pathlib import Path
import sqlite3
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('database', type=Path)
parser.add_argument('--backup', type=Path, required=True, help='Existing verified stopped-writer SQLite backup; never overwritten.')
args = parser.parse_args()
if not args.database.is_file() or not args.backup.is_file() or args.database.resolve() == args.backup.resolve():
    sys.exit('Supply the stopped database and a distinct existing backup. No data was changed.')
queries = {
    'sessions': 'SELECT count(*) FROM Sessions',
    'instances': 'SELECT count(*) FROM AgentInstances',
    'automations': 'SELECT count(*) FROM Automations',
    'retained_runs': 'SELECT count(*) FROM AgentRuns',
    'nonterminal_runs': 'SELECT count(*) FROM AgentRuns WHERE Status NOT IN (4,5,6)',
    'legacy_immutable_publications': "SELECT count(*) FROM AgentDefinitionPublications WHERE json_type(PayloadJson,'$.modelDefaults')='object' AND json_extract(PayloadJson,'$.modelDefaults.mode') IS NULL",
    'legacy_definition_drafts': "SELECT count(*) FROM AgentDefinitionDrafts WHERE json_type(CandidateJson,'$.modelDefaults')='object' AND json_extract(CandidateJson,'$.modelDefaults.mode') IS NULL",
    'legacy_session_definitions': "SELECT count(*) FROM Sessions WHERE json_type(DefinitionJson,'$.modelDefaults')='object' AND json_extract(DefinitionJson,'$.modelDefaults.mode') IS NULL",
    'legacy_immutable_run_configuration': "SELECT count(*) FROM AgentRuns WHERE json_type(PayloadJson,'$.admission.configuration.definition.modelDefaults')='object' AND json_extract(PayloadJson,'$.admission.configuration.definition.modelDefaults.mode') IS NULL",
    'legacy_prepared_approvals': "SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.AutomationMutation')='object' OR json_type(PayloadJson,'$.Operation.ManagementMutation')='object' OR json_type(PayloadJson,'$.Operation.AutomationAdmission')='object'",
    'legacy_orphan_unattended_efforts': 'SELECT count(*) FROM AgentInstances WHERE UnattendedOfferingId IS NULL AND UnattendedReasoningEffort IS NOT NULL',
    'legacy_orphan_automation_efforts': 'SELECT count(*) FROM Automations WHERE ModelOverrideOfferingId IS NULL AND ModelOverrideReasoningEffort IS NOT NULL',
}
try:
    with sqlite3.connect(args.database.resolve().as_uri() + '?mode=ro', uri=True) as db:
        result = {key: db.execute(sql).fetchone()[0] for key, sql in queries.items()}
        result['foreign_key_errors'] = len(db.execute('PRAGMA foreign_key_check').fetchall())
        result['database_integrity'] = db.execute('PRAGMA integrity_check').fetchone()[0]
        result['schema'] = db.execute('SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1').fetchone()[0]
    with sqlite3.connect(args.backup.resolve().as_uri() + '?mode=ro', uri=True) as backup:
        result['backup_integrity'] = backup.execute('PRAGMA integrity_check').fetchone()[0]
        result['backup_inventory_matches'] = all(backup.execute(sql).fetchone()[0] == result[key] for key, sql in queries.items())
        result['backup_schema_matches'] = backup.execute('SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1').fetchone()[0] == result['schema']
    print(json.dumps(result, indent=2))
    refused = any(result[key] for key in result if key.startswith('legacy_')) or result['foreign_key_errors'] or result['backup_integrity'] != 'ok' or result['database_integrity'] != 'ok' or not result['backup_inventory_matches'] or not result['backup_schema_matches']
    if refused:
        sys.exit('Refused: immutable legacy evidence or integrity errors require explicit operator reconciliation/fresh-state approval. No data was changed.')
except sqlite3.Error:
    sys.exit('Unsupported or unreadable database schema. No data or credentials were printed or changed.')
