#!/usr/bin/env python3
"""Content identity for executable/test/build inputs, including uncommitted qualified work. No private files printed."""
import hashlib
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
PREFIXES = ('src/', 'web/', 'tests/', 'agents/', 'scripts/')
SPECIAL = {'Dockerfile', '.dockerignore', 'Directory.Build.props', 'global.json', 'AgentCore.sln'}


def fingerprint():
    listed = subprocess.check_output(['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard'], cwd=ROOT).decode().split('\0')
    paths = sorted({name for name in listed if name and (name.startswith(PREFIXES) or name in SPECIAL or name.startswith('docker-compose.'))})
    digest = hashlib.sha256()
    for name in paths:
        path = ROOT / name
        digest.update(name.encode() + b'\0')
        if path.is_symlink():
            digest.update(b'link\0' + os.readlink(path).encode())
        elif path.is_file():
            digest.update(str(path.stat().st_mode & 0o777).encode() + b'\0')
            with path.open('rb') as stream:
                digest.update(hashlib.file_digest(stream, 'sha256').digest())
        else:
            digest.update(b'deleted')
    return {'sourceFingerprint': 'worktree-' + digest.hexdigest(), 'files': len(paths),
        'baseCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()}


if __name__ == '__main__':
    print(json.dumps(fingerprint()))
