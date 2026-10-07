#!/usr/bin/env python3
"""Explicitly reset a stopped, disposable SQLite demo database; no directory cleanup."""
import argparse
from pathlib import Path
import sqlite3

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--demo-data', required=True, type=Path, metavar='DATABASE', help='absolute path to a disposable demo/test SQLite database')
args = parser.parse_args()
path = args.demo_data
if not path.is_absolute() or path.suffix not in ('.db', '.sqlite', '.sqlite3'):
    parser.error('Provide an absolute SQLite database path with .db/.sqlite/.sqlite3 suffix.')
if path.is_symlink() or not path.is_file():
    parser.error('The selected database must be an existing regular file, not a symlink.')
# Refuse an actively writing host before removing the explicitly selected file.
connection = sqlite3.connect(str(path), timeout=0)
try:
    connection.execute('BEGIN EXCLUSIVE')
    connection.rollback()
finally:
    connection.close()
for suffix in ('-wal', '-shm', ''):
    target = Path(str(path) + suffix)
    if target.is_symlink():
        parser.error('Refusing a symlink sidecar.')
for suffix in ('-wal', '-shm', ''):
    Path(str(path) + suffix).unlink(missing_ok=True)
print('Disposable demo database reset. Restart the Synthetic host to migrate and reseed.')
