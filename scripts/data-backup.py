#!/usr/bin/env python3
"""Cold coordinated single-host backup. Plan/verify are read-only; all paths and manifests are private."""
import argparse
from contextlib import closing, contextmanager
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import sqlite3
import stat
import subprocess
import tempfile

VERSION = 1
ROLES = {'workspaces', 'attachments', 'artifacts', 'resources', 'keys'}
PROFILE = 'controlled-synthetic'


class Refused(ValueError):
    pass


def require(value, code):
    if not value:
        raise Refused(code)


def physical(path, exists=True):
    path = Path(path)
    require(path.is_absolute() and path == path.resolve(), 'physical_absolute_path_required')
    require(not path.is_symlink() and (not exists or path.exists()), 'path_unavailable')
    return path


def private(path):
    mode = path.stat()
    require(stat.S_ISDIR(mode.st_mode) and mode.st_uid == os.getuid()
        and not mode.st_mode & 0o077, 'private_owned_directory_required')


def relative(value):
    require(isinstance(value, str) and value and '\\' not in value, 'invalid_relative_path')
    path = PurePosixPath(value)
    require(not path.is_absolute() and str(path) == value and '..' not in path.parts, 'invalid_relative_path')
    return value


def layout(value):
    require(isinstance(value, dict) and set(value) == {'version', 'profile', 'source', 'database', 'roots'}, 'invalid_layout')
    require(value['version'] == VERSION and value['profile'] == PROFILE, 'unsupported_profile')
    require(isinstance(value['source'], str) and re.fullmatch('[0-9a-f]{40,64}', value['source']), 'source_identity_required')
    database = relative(value['database'])
    require('/' not in database and database.endswith('.db'), 'database_must_be_direct_child')
    roots = value['roots']
    require(isinstance(roots, dict) and set(roots) == ROLES, 'complete_roots_required')
    paths = [relative(v) for v in roots.values()]
    require(len(set(paths)) == len(paths) and all('/' not in p and p not in ('.', database) for p in paths), 'disjoint_roots_required')
    return value


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def inventory(root, db):
    entries = []
    excluded = {db + suffix for suffix in ('-wal', '-shm', '.owner.lock')}
    for path in sorted(root.rglob('*')):
        name = path.relative_to(root).as_posix()
        require(not path.is_symlink(), 'linked_file_refused')
        info = path.lstat()
        require(info.st_uid == root.stat().st_uid, 'mixed_file_ownership')
        if name in excluded:
            require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1, 'unsafe_database_sidecar')
            continue
        if stat.S_ISDIR(info.st_mode):
            entries.append({'path': name, 'type': 'directory', 'mode': stat.S_IMODE(info.st_mode)})
        else:
            require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1, 'special_or_linked_file_refused')
            require(not info.st_mode & 0o7000, 'privileged_file_mode_refused')
            entries.append({'path': name, 'type': 'file', 'bytes': info.st_size, 'sha256': digest(path),
                'mode': stat.S_IMODE(info.st_mode), 'modifiedNs': info.st_mtime_ns})
    return entries


def source_inventory(root, spec):
    private(root)
    require((root / spec['database']).is_file(), 'database_required')
    for directory in spec['roots'].values():
        require((root / directory).is_dir(), 'required_root_missing')
    entries = inventory(root, spec['database'])
    allowed = set(spec['roots'].values()) | {spec['database']}
    require(all(e['path'].split('/')[0] in allowed for e in entries), 'undeclared_root_or_file')
    return entries


@contextmanager
def stopped(root, database):
    # Matches .NET FileShare.None's Unix flock on the persistent database-specific inode.
    import fcntl
    lock = root / (database + '.owner.lock')
    require(lock.is_file() and not lock.is_symlink() and lock.stat().st_nlink == 1, 'qualified_owner_lock_required')
    descriptor = os.open(lock, os.O_RDONLY | os.O_NOFOLLOW)
    try:
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise Refused('active_installation_owner') from None
        yield
    finally:
        os.close(descriptor)


def destination(path, source):
    path = physical(path, exists=False)
    require(not path.exists() and path.parent.is_dir(), 'fresh_destination_required')
    private(path.parent)
    require(path != source and source not in path.parents and path not in source.parents, 'overlapping_destination')
    return path


def build_identity(assembly):
    assembly = physical(assembly)
    paths = sorted(assembly.parent.glob('AgentCore.*.dll'))
    require(len(paths) == 5 and assembly in paths, 'matching_five_assembly_build_required')
    return {path.name: digest(path) for path in paths}


def sqlite_check(path):
    with closing(sqlite3.connect(path.as_uri() + '?mode=ro', uri=True, timeout=5)) as connection:
        require(connection.execute('PRAGMA integrity_check').fetchall() == [('ok',)], 'database_integrity_failed')
        require(not connection.execute('PRAGMA foreign_key_check').fetchall(), 'database_ownership_failed')
        schemas = {}
        for table in ('__EFMigrationsHistory', '__FormalWorkflowManagementMigrations', '__FormalWorkflowRuntimeMigrations'):
            rows = connection.execute('SELECT MigrationId FROM "' + table + '" ORDER BY MigrationId').fetchall()
            require(rows, 'required_schema_missing')
            schemas[table] = [row[0] for row in rows]
        return schemas


def protected_check(root, spec, assembly):
    result = subprocess.run(['dotnet', str(assembly), 'verify-protected-state', str(root / spec['database']),
        str(root / spec['roots']['keys'])], capture_output=True, timeout=120,
        env={k: v for k, v in os.environ.items() if k in ('PATH', 'HOME', 'DOTNET_ROOT', 'TMPDIR')})
    require(result.returncode == 0, 'protected_state_or_schema_mismatch')
    result = json.loads(result.stdout)
    require(result.get('valid') is True, 'protected_state_or_schema_mismatch')
    return result['protectedRecords']


def content_check(root, spec):
    """Immutable retained references must match the supplied roots, not just the archive's own hashes."""
    def check(role, name, size, sha):
        relative(name)
        path = physical(root / spec['roots'][role] / name)
        require(path.is_file() and path.stat().st_size == size and digest(path) == sha.lower(), 'referenced_content_missing_or_changed')
    with closing(sqlite3.connect((root / spec['database']).as_uri() + '?mode=ro', uri=True, timeout=5)) as db:
        for row in db.execute('SELECT BlobKey,ByteSize,Sha256Hex FROM Attachments WHERE ContentDeletedAtUtc IS NULL'):
            check('attachments', *row)
        for row in db.execute('SELECT a.BlobKey,a.ByteSize,a.Sha256Hex FROM Artifacts a JOIN Sessions s ON s.SessionId=a.SessionId WHERE s.DurablyDeletedAtUtc IS NULL'):
            check('artifacts', *row)
        for table in ('AgentDefinitionDraftResources', 'AgentDefinitionPublicationResources', 'SpaceFileResources'):
            for sha, size in db.execute('SELECT ContentSha256,ByteLength FROM "' + table + '"'):
                check('resources', sha, size, sha)
        for payload, in db.execute('SELECT PayloadJson FROM AgentInstanceResources'):
            resource = json.loads(payload)
            check('resources', resource['contentSha256'], resource['byteLength'], resource['contentSha256'])


def copy_tree(source, target, entries, database):
    for entry in entries:
        path = target / entry['path']
        if entry['type'] == 'directory':
            path.mkdir(mode=0o700)
        elif entry['path'] != database:
            descriptor = os.open(source / entry['path'], os.O_RDONLY | os.O_NOFOLLOW)
            with os.fdopen(descriptor, 'rb') as reader, path.open('xb') as writer:
                os.chmod(path, 0o600)
                shutil.copyfileobj(reader, writer, length=1024 * 1024)
            os.chmod(path, entry['mode'])
            os.utime(path, ns=(entry['modifiedNs'], entry['modifiedNs']))
    for entry in reversed(entries):
        if entry['type'] == 'directory':
            os.chmod(target / entry['path'], entry['mode'])


def seal_sync(root):
    # Persist payload and manifest before the completion rename; no complete artifact on partial failure.
    for path in root.rglob('*'):
        if path.is_file():
            with path.open('rb') as stream:
                os.fsync(stream.fileno())
    for path in [*sorted((p for p in root.rglob('*') if p.is_dir()), reverse=True), root]:
        descriptor = os.open(path, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)


def sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def backup(root, spec, target, assembly, services_stopped=False, confirmation=None):
    root = physical(root)
    spec = layout(spec)
    target = destination(target, root)
    build = build_identity(assembly)
    require(services_stopped and confirmation == str(root), 'explicit_stopped_writer_confirmation_required')
    with stopped(root, spec['database']):
        original = source_inventory(root, spec)
        protected_check(root, spec, assembly)
        content_check(root, spec)
        stage = Path(tempfile.mkdtemp(prefix='.partial-backup-', dir=target.parent))
        payload = stage / 'payload'
        payload.mkdir(mode=0o700)
        try:
            copy_tree(root, payload, original, spec['database'])
            with closing(sqlite3.connect((root / spec['database']).as_uri() + '?mode=ro', uri=True, timeout=5)) as source:
                with closing(sqlite3.connect(payload / spec['database'])) as snapshot:
                    source.backup(snapshot, pages=256, sleep=.05)
                    snapshot.execute('PRAGMA journal_mode=DELETE')
            (payload / spec['database']).chmod(0o600)
            require(source_inventory(root, spec) == original, 'source_changed_during_backup')
            schema = sqlite_check(payload / spec['database'])
            count = protected_check(payload, spec, assembly)
            content_check(payload, spec)
            manifest = {'version': VERSION, 'createdUtc': datetime.now(timezone.utc).isoformat(), 'layout': spec,
                'build': build, 'schema': schema, 'protectedRecords': count, 'entries': inventory(payload, spec['database'])}
            manifest_path = stage / 'manifest.json'
            with manifest_path.open('x') as stream:
                os.chmod(manifest_path, 0o600)
                json.dump(manifest, stream, sort_keys=True, indent=2)
            verify(stage, assembly)
            seal_sync(stage)
            require(not target.exists() and not target.is_symlink(), 'fresh_destination_required')
            stage.rename(target)
            sync_directory(target.parent)
            return {'complete': True, 'files': sum(e['type'] == 'file' for e in manifest['entries']), 'protectedRecords': count}
        except BaseException:
            # A private .partial-backup directory remains for diagnosis; never a completed destination.
            raise


def verify(archive, assembly):
    archive = physical(archive)
    private(archive)
    require({p.name for p in archive.iterdir()} == {'payload', 'manifest.json'}, 'incomplete_backup')
    require(not (archive / 'manifest.json').is_symlink() and (archive / 'manifest.json').stat().st_size <= 64 * 1024 * 1024, 'invalid_manifest')
    manifest = json.loads((archive / 'manifest.json').read_text())
    require(isinstance(manifest, dict) and manifest.get('version') == VERSION, 'unsupported_manifest')
    spec = layout(manifest['layout'])
    require(manifest['build'] == build_identity(assembly), 'matching_build_required')
    payload = physical(archive / 'payload')
    require(not any((payload / (spec['database'] + suffix)).exists()
        for suffix in ('-wal', '-shm', '.owner.lock')), 'unsealed_database_sidecar')
    actual = source_inventory(payload, spec)
    require(actual == manifest['entries'], 'payload_digest_or_inventory_mismatch')
    require(sqlite_check(payload / spec['database']) == manifest['schema'], 'schema_mismatch')
    require(protected_check(payload, spec, assembly) == manifest['protectedRecords'], 'protected_state_mismatch')
    content_check(payload, spec)
    return manifest


def restore(archive, target, assembly, execute=False):
    manifest = verify(archive, assembly)
    target = destination(target, physical(archive))
    if not execute:
        return {'valid': True, 'planOnly': True}
    stage = Path(tempfile.mkdtemp(prefix='.partial-restore-', dir=target.parent))
    spec = manifest['layout']
    copy_tree(Path(archive) / 'payload', stage, manifest['entries'], '')
    require(inventory(stage, spec['database']) == manifest['entries'], 'restored_digest_mismatch')
    require(sqlite_check(stage / spec['database']) == manifest['schema'], 'restored_schema_mismatch')
    protected_check(stage, spec, assembly)
    content_check(stage, spec)
    seal_sync(stage)
    require(not target.exists() and not target.is_symlink(), 'fresh_destination_required')
    stage.rename(target)
    sync_directory(target.parent)
    return {'restored': True, 'protectedRecords': manifest['protectedRecords']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['plan', 'backup', 'verify', 'restore-plan', 'restore'])
    parser.add_argument('--data-root', type=Path)
    parser.add_argument('--layout', type=Path)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--destination', type=Path)
    parser.add_argument('--assembly', type=Path, required=True)
    parser.add_argument('--services-stopped', action='store_true')
    parser.add_argument('--confirm-data-root')
    args = parser.parse_args()
    os.umask(0o077)
    try:
        if args.command in ('plan', 'backup'):
            require(args.data_root and args.layout, 'data_root_and_layout_required')
            root = physical(args.data_root)
            spec = layout(json.loads(args.layout.read_text()))
            if args.command == 'plan':
                destination(args.archive, root)
                entries = source_inventory(root, spec)
                build_identity(args.assembly)
                result = {'planOnly': True, 'entries': len(entries), 'requiresAllWritersStopped': True}
            else:
                result = backup(root, spec, args.archive, args.assembly, args.services_stopped, args.confirm_data_root)
        elif args.command == 'verify':
            manifest = verify(args.archive, args.assembly)
            result = {'valid': True, 'protectedRecords': manifest['protectedRecords']}
        else:
            require(args.destination is not None, 'fresh_destination_required')
            result = restore(args.archive, args.destination, args.assembly, execute=args.command == 'restore')
        print(json.dumps(result))
    except (Refused, OSError, ValueError, KeyError, TypeError, sqlite3.Error, subprocess.SubprocessError) as error:
        code = str(error) if isinstance(error, Refused) else 'operation_refused'
        parser.exit(2, 'Cold data operation refused: ' + code + '. No private values printed. See the recovery runbook.\n')


if __name__ == '__main__':
    main()
