"""Read-only, aggregate event-history growth snapshot; never compacts or exports payloads."""

import argparse
from contextlib import closing
from datetime import datetime, timezone
import json
from pathlib import Path
import sqlite3
import shutil


TABLES = {
    "CoreEvents": ("PayloadJson",),
    "CoreEventDeliveries": ("SnapshotJson", "DecisionJson"),
    "CoreEventBuckets": ("PayloadJson", "CoverageJson"),
    "ExternalEvents": ("EvidenceJson",),
    "ExternalEventDeliveries": ("SnapshotJson", "DecisionJson"),
}


def work_snapshot(db, now_ms):
    catalogs = {
        'runs': ('AgentRuns', 'Status', 'UpdatedAtUtc', ['queued', 'running', 'approval', 'retry', 'completed', 'failed', 'cancelled', 'signal']),
        'tasks': ('TenantDelegatedTasks', 'State', 'UpdatedAt', ['dependencies', 'queued', 'running', 'waiting', 'blocked', 'succeeded', 'failed', 'cancelled', 'needs_attention']),
        'workflows': ('TenantWorkflowExecutions', 'Status', 'CreatedAt', ['pending', 'running', 'agent', 'verification', 'human', 'approval', 'blocked', 'needs_attention', 'completed', 'failed', 'cancelled', 'expired']),
    }
    result = {}
    for name, (table, status, timestamp, labels) in catalogs.items():
        rows = db.execute(f'SELECT "{status}",count(*),min("{timestamp}") FROM "{table}" GROUP BY "{status}"').fetchall()
        result[name] = {labels[state] if 0 <= state < len(labels) else 'unknown': {
            'rows': count, 'oldest_creation_age_ms' if name == 'workflows' else 'oldest_update_age_ms': max(0, now_ms - oldest)
        } for state, count, oldest in rows}
    result['runs_indeterminate_effects'] = db.execute("SELECT count(*) FROM AgentRuns WHERE json_extract(PayloadJson,'$.sideEffect.disposition')=5").fetchone()[0]
    result['runs_expired_claims'] = db.execute('SELECT count(*) FROM AgentRuns WHERE Status=1 AND LeaseExpiresAtUtc<=?', (now_ms,)).fetchone()[0]
    return result


def snapshot(path: Path, include_work=False) -> dict:
    path = path.resolve(strict=True)
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=5)) as db:
        db.execute("PRAGMA query_only=ON")
        db.execute("BEGIN")
        tables = {}
        for table, columns in TABLES.items():
            size = " + ".join(f'COALESCE(length(CAST("{column}" AS BLOB)), 0)' for column in columns)
            count, payload_bytes = db.execute(
                f'SELECT COUNT(*), COALESCE(SUM({size}), 0) FROM "{table}"'
            ).fetchone()
            tables[table] = {"rows": count, "json_bytes": payload_bytes}
        sources, reviewed, unreviewed, pending = db.execute("""
            SELECT COALESCE(SUM(json_array_length(PayloadJson, '$.Sources')), 0),
                   COALESCE(SUM(json_array_length(CoverageJson)), 0),
                   COALESCE(SUM(MAX(json_array_length(PayloadJson, '$.Sources') - json_array_length(CoverageJson), 0)), 0),
                   COALESCE(SUM(CASE WHEN Flushed = 0 THEN 1 ELSE 0 END), 0)
            FROM CoreEventBuckets
        """).fetchone()
        page_size = db.execute("PRAGMA page_size").fetchone()[0]
        page_count = db.execute("PRAGMA page_count").fetchone()[0]
        free_pages = db.execute("PRAGMA freelist_count").fetchone()[0]
        work = work_snapshot(db, int(datetime.now(timezone.utc).timestamp() * 1000)) if include_work else None
    sizes = {}
    for label, suffix in (("database", ""), ("wal", "-wal"), ("shm", "-shm")):
        try:
            sizes[label] = Path(str(path) + suffix).stat().st_size
        except FileNotFoundError:
            sizes[label] = 0
    result = {
        "observed_at_utc": datetime.now(timezone.utc).isoformat(),
        "tables": tables,
        "bucket_source_references": sources,
        "reviewed_source_references": reviewed,
        "unreviewed_source_references": unreviewed,
        "pending_buckets": pending,
        "database_page_bytes": page_size * page_count,
        "database_free_page_bytes": page_size * free_pages,
        "file_bytes": sizes,
        "filesystem_free_bytes": shutil.disk_usage(path.parent).free,
    }
    if include_work:
        result['work'] = work
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", type=Path)
    parser.add_argument('--work', action='store_true', help='Include current Run/Task/Workflow state aggregates; requires the matching full schema.')
    args = parser.parse_args()
    try:
        print(json.dumps(snapshot(args.database, args.work), sort_keys=True))
    except (OSError, sqlite3.Error):
        parser.exit(1, "Unable to read the existing event database/schema; no snapshot produced.\n")
