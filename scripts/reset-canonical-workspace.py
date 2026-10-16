#!/usr/bin/env python3
"""Explicit offline backup/reset by selected-path rename; never called by startup."""
import argparse
import hashlib
import json
import os
from pathlib import Path


def plan(database, roots, destination):
    selected = [Path(database), *(Path(root) for root in roots)]
    backup = Path(destination)
    if not all(path.is_absolute() for path in [*selected, backup]):
        raise ValueError("Use exact absolute configured paths.")
    if backup.exists() or backup.is_symlink() or not backup.parent.is_dir():
        raise ValueError("Backup destination must be new, with an existing parent.")
    sources = []
    for path in [*selected, Path(str(database) + "-wal"), Path(str(database) + "-shm")]:
        if path.is_symlink() or path != path.resolve():
            raise ValueError("Selected paths and ancestors cannot be links.")
        if path in (Path('/'), Path.home(), Path.cwd()) or path in Path.cwd().parents:
            raise ValueError("Select data paths, never a system, home or repository root.")
        if not path.exists():
            continue
        if path in sources:
            raise ValueError("Duplicate selected path.")
        if backup == path or path in backup.parents or backup in path.parents:
            raise ValueError("Backup must be outside every selected path.")
        if any(path in other.parents or other in path.parents for other in sources):
            raise ValueError("Selected paths overlap; select each configured data root once.")
        if path.stat().st_dev != backup.parent.stat().st_dev:
            raise ValueError("Backup must share the source filesystem for atomic rename.")
        sources.append(path)
    if not sources:
        raise ValueError("No existing selected data; nothing to reset.")
    entries = []
    for index, source in enumerate(sources):
        stat = source.stat()
        entries.append(dict(source=str(source), destination=str(backup / f"{index:02d}-{source.name}"),
                            device=stat.st_dev, inode=stat.st_ino, modifiedNs=stat.st_mtime_ns))
    fingerprint = hashlib.sha256(json.dumps(entries, sort_keys=True).encode()).hexdigest()
    return entries, fingerprint


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', required=True)
    parser.add_argument('--root', action='append', required=True, help='Repeat for every configured data root, including workspace, attachments, artifacts, resources and keys.')
    parser.add_argument('--backup-directory', required=True)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--confirm-stopped', action='store_true', help='Operator confirms Core and all referenced workloads have stopped with termination confirmed.')
    parser.add_argument('--confirm-set', help='Exact fingerprint printed by the dry run.')
    args = parser.parse_args()
    entries, fingerprint = plan(args.database, args.root, args.backup_directory)
    if not args.apply:
        print(json.dumps(dict(paths=entries, confirmation=fingerprint, action='backup-by-rename; no files changed'), indent=2))
        return
    if not args.confirm_stopped or args.confirm_set != fingerprint:
        parser.error('Apply requires --confirm-stopped and the unchanged exact --confirm-set from a dry run.')
    backup = Path(args.backup_directory)
    backup.mkdir(mode=0o700)
    manifest = dict(confirmation=fingerprint, paths=entries, completed=[])
    def save():
        fd = os.open(backup / 'manifest.json', os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, 'w') as output:
            json.dump(manifest, output, indent=2)
            output.flush()
            os.fsync(output.fileno())
    save()
    for entry in entries:
        source = Path(entry['source'])
        stat = source.stat()
        if (stat.st_dev, stat.st_ino, stat.st_mtime_ns) != (entry['device'], entry['inode'], entry['modifiedNs']):
            raise RuntimeError('Selected data changed during backup. Stop; inspect manifest. Earlier renames are retained.')
        source.rename(entry['destination'])
        manifest['completed'].append(entry['source'])
        save()
    print(f"Backup retained at {backup}. Start the matching new build to initialize fresh selected data. No provider, container or server was restarted.")


if __name__ == '__main__':
    main()
