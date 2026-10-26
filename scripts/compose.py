#!/usr/bin/env python3
"""Validate operator inputs before handing off to Docker Compose (values stay private)."""
import os
from pathlib import Path
import runpy
import sys

root = Path(__file__).resolve().parents[1]
policy = runpy.run_path(str(root / 'scripts/dev-env.py'))
args = sys.argv[1:]
try:
    # Resolve against the caller's cwd before changing to the project root. Pass
    # those exact paths to Compose, retaining override order across both forms.
    files = []
    index = 0
    while index < len(args):
        arg = args[index]
        if arg == '--env-file':
            if index + 1 == len(args):
                raise ValueError('Missing path for --env-file.')
            index += 1
            file = Path(args[index]).resolve()
            args[index] = str(file)
            files.append(file)
        elif arg.startswith('--env-file='):
            file = Path(arg.split('=', 1)[1]).resolve()
            args[index] = '--env-file=' + str(file)
            files.append(file)
        index += 1
    if not files:
        files = [root / '.env']
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
