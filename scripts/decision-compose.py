#!/usr/bin/env python3
"""Explicit native decision service lifecycle; never starts the application container."""
import argparse
import json
import os
from pathlib import Path
import runpy
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
policy = runpy.run_path(str(ROOT / 'scripts/dev-env.py'))

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('build', 'download', 'start', 'stop', 'status', 'preflight'))
    args = parser.parse_args()
    env = {**policy['read_env'](Path(os.environ.get('AGENTCORE_DEV_ENV_FILE', ROOT / '.env'))), **os.environ}
    if args.command in ('build', 'download', 'start', 'preflight'): policy['validate'](env)
    enabled = env.get('DecisionIntelligence__Enabled', 'false').lower() == 'true'
    if args.command in ('start', 'preflight'):
        if not enabled:
            print('Decision Intelligence is disabled; optional service was not started.')
            return 0
        if env.get('DecisionIntelligence__Backend', 'LocalKev') != 'LocalKev':
            raise ValueError('Native decision service requires the explicit LocalKev backend.')
        if env.get('DecisionIntelligence__Endpoint', 'http://127.0.0.1:8009').rstrip('/') != 'http://127.0.0.1:8009':
            raise ValueError('Native decision service requires the loopback port 8009 endpoint.')
    key = env.get('KEV_API_KEY') or env.get('DecisionIntelligence__ApiKey')
    if args.command in ('build', 'download', 'start', 'preflight') and (not key or not 16 <= len(key) <= 256 or any(ord(c) < 33 or ord(c) > 126 for c in key)):
        raise ValueError('Set the private KEV_API_KEY (16–256 printable non-space ASCII characters). Values are redacted.')
    if env.get('KEV_API_KEY') and env.get('DecisionIntelligence__ApiKey') and env['KEV_API_KEY'] != env['DecisionIntelligence__ApiKey']:
        raise ValueError('Decision service and host credentials differ. Values are redacted.')
    env['KEV_API_KEY'] = key or 'status-does-not-authenticate'
    base = ['docker', 'compose', '-p', 'agent-core-decision', '--env-file', os.devnull,
            # The sidecar overlay also configures agent-core. Include its base so
            # Compose validates the model; every command below still targets decision only.
            '-f', str(ROOT / 'docker-compose.yml'),
            '-f', str(ROOT / 'docker-compose.decision.yml'), '-f', str(ROOT / 'docker-compose.decision.native.yml')]
    if args.command in ('start', 'preflight'):
        info = subprocess.run(['docker', 'info', '--format', '{{json .Runtimes}}'], capture_output=True, text=True, check=True, env=env)
        if 'nvidia' not in json.loads(info.stdout):
            raise ValueError('The optional CUDA service requires Docker NVIDIA runtime and a suitable GPU (24 GB recommended). Native Apple Silicon serving is a separate operator setup. No download/start was attempted.')
        print('CUDA runtime detected. Pinned cache must already exist; cold model loading may take minutes and requests may time out until ready.')
        if args.command == 'preflight': return 0
    suffix = {
        'build': ['build', 'decision'], 'start': ['up', '-d', '--no-build', 'decision'],
        'stop': ['stop', 'decision'], 'status': ['ps', 'decision']
    }
    if args.command == 'download':
        base.extend(['-f', str(ROOT / 'docker-compose.decision.download.yml')])
        command = ['run', '--rm', '--no-deps', 'decision', 'download']
    else: command = suffix[args.command]
    return subprocess.run(base + command, cwd=ROOT, env=env, check=False).returncode

if __name__ == '__main__':
    try: sys.exit(main())
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(str(error) if isinstance(error, ValueError) else 'Decision service command failed. Check Docker/GPU availability; secrets were not printed.')
