#!/usr/bin/env python3
"""Names and source locations only; never reads private .env or effective values."""
import json
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parents[2]
files = subprocess.check_output(['git', 'ls-files', 'src/**/*.cs', 'src/**/appsettings*.json', 'src/**/launchSettings.json', 'scripts/*.py', 'scripts/*.sh', 'docker-compose*.yml', 'Dockerfile', 'web/vite.config.ts'], cwd=root, text=True).splitlines()
# New boundary files are included before their first commit as well.
files += ['scripts/compose.py', 'src/AgentCore.Infrastructure/Configuration/ConfigurationCutover.cs']
entries = []
for name in sorted(set(files)):
    path = root / name
    if not path.is_file(): continue
    for number, line in enumerate(path.read_text().splitlines(), 1):
        patterns = [('section', r'GetSection\("([A-Za-z][A-Za-z0-9:_-]*)"'),
                    ('lookup', r'(?:configuration|Configuration|config)\["([A-Za-z][A-Za-z0-9:_-]*)"\]'),
                    ('typed lookup', r'GetValue(?:<[^>]+>)?\("([A-Za-z][A-Za-z0-9:_-]*)"'),
                    ('process env', r'GetEnvironmentVariable\("([A-Za-z][A-Za-z0-9:_-]*)"'),
                    ('orchestration', r'\$\{([A-Z][A-Z0-9_]*)')]
        for kind, pattern in patterns:
            for key in re.findall(pattern, line): entries.append({'name': key, 'consumer': name, 'line': number, 'kind': kind})
def classification(key):
    return 'secret' if re.search(r'(secret|password|api[_:]?key|token|private[_:]?key)', key, re.I) else 'non-secret'


settings = {}
def add(key, default, source, owner):
    item = settings.setdefault(key.replace('__', ':').lower(), {
        'name': key, 'owner': owner, 'secretClassification': classification(key), 'sources': [],
        'default': 'option-owner default when absent',
        'precedence': 'JSON < inherited environment < native dotenv < launcher-derived; Compose environment > dotenv before explicit service forwarding',
        'invalidOrContradictory': 'boundary preflight and option-owner validation; retired/dual inputs rejected with key names only',
    })
    item['sources'].append(source)
    if default is not None:
        item['default'] = '[redacted]' if item['secretClassification'] == 'secret' else default


def flatten(value, prefix, source):
    if isinstance(value, dict):
        for key, child in value.items(): flatten(child, prefix + [key], source)
    elif isinstance(value, list):
        for index, child in enumerate(value): flatten(child, prefix + [str(index)], source)
    else: add('__'.join(prefix), value, source, '.NET binder; native or explicitly forwarded Compose')


for name in files:
    if '/appsettings' in name and name.endswith('.json'):
        # Current appsettings are JSON without comments. No private configuration is read.
        flatten(json.loads((root / name).read_text()), [], name)
for name in ['.env.example', 'docs/host-configuration-reference.md']:
    for line in (root / name).read_text().splitlines():
        match = re.match(r'^\s*#?\s*([A-Za-z][A-Za-z0-9_]*)=(.*)$', line)
        if match:
            key, value = match.groups()
            owner = '.NET binder; native or explicitly forwarded Compose' if '__' in key else (
                'browser build/tooling' if key.startswith(('VITE_', 'NODE_')) else 'launcher/Compose/platform/external integration')
            add(key, value, name, owner)
for entry in entries:
    add(entry['name'].replace(':', '__'), None, entry['consumer'] + ':' + str(entry['line']),
        '.NET section/lookup' if entry['kind'] != 'orchestration' else 'launcher/Compose')
print(json.dumps({
    'scope': 'Public source audit: effective static readers, JSON defaults, startup preset and advanced option shapes. Section/map/array entries remain shapes, not invented finite keys. Private process/env/database values are never read.',
    'translations': {'GOOGLE_OAUTH_CLIENT_ID': 'GoogleOAuth__ClientId', 'GOOGLE_OAUTH_CLIENT_SECRET': 'GoogleOAuth__ClientSecret', 'GOOGLE_OAUTH_PUBLIC_APP_URL': 'GoogleOAuth__PublicAppUrl', 'AGENTCORE_DRIVE_ENABLED': 'GoogleDrive__Enabled'},
    'settings': sorted(settings.values(), key=lambda item: item['name'].lower()), 'entries': entries,
}, indent=2))
