#!/usr/bin/env python3
"""Validate operator inputs before handing off to Docker Compose (values stay private)."""
import os
from pathlib import Path
import runpy
import sys

root = Path(__file__).resolve().parents[1]
policy = runpy.run_path(str(root / 'scripts/dev-env.py'))
args = sys.argv[1:]
# Resolve the same explicit files passed to Compose. Without one, use root .env.
files = [Path(args[n + 1]) for n, arg in enumerate(args[:-1]) if arg == '--env-file']
if any(arg.startswith('--env-file=') for arg in args):
    files += [Path(arg.split('=', 1)[1]) for arg in args if arg.startswith('--env-file=')]
if not files:
    files = [root / '.env']
try:
    values = {}
    for file in files:
        values.update(policy['read_env'](file))
    # Compose's inherited environment overrides dotenv interpolation.
    policy['validate']({**values, **os.environ}, compose=True)
except ValueError as error:
    sys.exit(str(error))
except OSError:
    sys.exit('Cannot read Compose env file. Values were not printed.')
os.chdir(root)
os.execvp('docker', ['docker', 'compose', *args])
