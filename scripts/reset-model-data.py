#!/usr/bin/env python3
"""Explicit v1 disposable-data cutover. Plan by default; preserve the entire old root."""
import argparse
import os
from pathlib import Path
import stat

VERSION = 'model-cutover-v1'


def validate(data_root, backup_path, repository):
    root = Path(data_root).absolute()
    backup = Path(backup_path).absolute()
    repository = Path(repository).resolve()
    if root != root.resolve() or backup != backup.resolve():
        raise ValueError('Use physical absolute paths without symlink components.')
    if root == Path(root.anchor) or root == Path.home().resolve() or root == repository or root in repository.parents:
        raise ValueError('The data root must be a dedicated disposable application directory.')
    if not root.is_dir():
        raise ValueError('The selected data root must already be a directory.')
    if backup.exists() or backup.is_symlink():
        raise ValueError('The backup destination must not exist.')
    if backup == root or root in backup.parents or backup in root.parents:
        raise ValueError('The backup must be outside the selected data root.')
    if not backup.parent.is_dir() or backup.parent.stat().st_dev != root.stat().st_dev:
        raise ValueError('The backup parent must exist on the same filesystem for an atomic move.')
    if any(path.is_symlink() for path in root.rglob('*')):
        raise ValueError('The selected tree contains symlinks; review and back it up manually.')
    return root, backup


def reset(data_root, backup_path, repository, *, execute=False, services_stopped=False, confirmation=None):
    root, backup = validate(data_root, backup_path, repository)
    if not execute:
        return f'{VERSION}: plan only. Preserve {root} at {backup}, then create an empty owner-only root. No files changed.'
    if not services_stopped or confirmation != str(root):
        raise ValueError('Execution requires --services-stopped and --confirm-disposable-data-root with the exact physical data-root path.')
    # Rename preserves the complete database/WAL/key/blob tree together. No SQL,
    # credentials or unrelated roots are read, converted, erased or imported.
    os.rename(root, backup)
    try:
        root.mkdir(mode=stat.S_IRWXU)
    except BaseException:
        os.rename(backup, root)
        raise
    return f'{VERSION}: preserved the old data at {backup}; created empty {root}. Bootstrap IAM explicitly before configuring model connections.'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data-root', required=True, type=Path)
    parser.add_argument('--backup-path', required=True, type=Path)
    parser.add_argument('--execute', action='store_true')
    parser.add_argument('--services-stopped', action='store_true')
    parser.add_argument('--confirm-disposable-data-root')
    args = parser.parse_args()
    try:
        print(reset(args.data_root, args.backup_path, Path(__file__).resolve().parent.parent,
                    execute=args.execute, services_stopped=args.services_stopped,
                    confirmation=args.confirm_disposable_data_root))
    except (ValueError, OSError) as error:
        parser.exit(2, f'{VERSION}: {error}\n')


if __name__ == '__main__':
    main()
