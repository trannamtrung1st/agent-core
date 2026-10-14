"""Export installed Workbench components and retained license-file hashes as SPDX 2.3."""
import datetime
import hashlib
import importlib.metadata
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import urllib.parse

packages = []
for line in subprocess.check_output(['dpkg-query', '-W', '-f=${Package}\t${Version}\n'], text=True).splitlines():
    name, version = line.split('\t', 1)
    packages.append(('deb', name, version, 'NOASSERTION'))
for distribution in importlib.metadata.distributions():
    packages.append(('pypi', distribution.metadata['Name'], distribution.version,
                     distribution.metadata.get('License-Expression') or 'NOASSERTION'))
for manifest in Path('/usr/local/lib/node_modules').rglob('package.json'):
    data = json.loads(manifest.read_text())
    if data.get('name') and data.get('version'):
        license_value = data.get('license')
        packages.append(('npm', data['name'], data['version'], license_value if isinstance(license_value, str) else 'NOASSERTION'))
packages.append(('runtime', 'node', subprocess.check_output(['node', '--version'], text=True).strip(), 'MIT'))
files = []
notices = list(Path('/usr/share/doc').glob('*/copyright'))
notices += list(Path('/usr/local/lib').glob('python*/LICENSE.txt'))
notices += [Path('/usr/share/agent-core/node-LICENSE')]
for path in sorted(notices):
    if path.is_file():
        files.append({'SPDXID': f'SPDXRef-File-{len(files)}', 'fileName': str(path),
            'checksums': [{'algorithm': 'SHA256', 'checksumValue': hashlib.sha256(path.read_bytes()).hexdigest()}],
            'licenseConcluded': 'NOASSERTION', 'licenseInfoInFiles': ['NOASSERTION'], 'copyrightText': 'NOASSERTION'})
if '--licenses' in sys.argv:
    candidates = set(notices)
    for base in [Path('/usr/local/lib/node_modules'), *Path('/usr/local/lib').glob('python*/site-packages')]:
        candidates.update(p for p in base.rglob('*') if p.is_file() and any(part in p.name.lower() for part in ['license', 'copying', 'notice', 'copyright']))
    with tarfile.open(fileobj=sys.stdout.buffer, mode='w|gz') as archive:
        for path in sorted(candidates):
            if path.is_file():
                archive.add(path, arcname=str(path).lstrip('/'), recursive=False)
    raise SystemExit(0)
known_licenses = {'MIT', 'Apache-2.0', 'BSD-3-Clause', 'BSD-2-Clause', 'ISC', 'PSF-2.0', 'MPL-2.0', 'Unlicense', 'CC0-1.0', 'NOASSERTION'}
document = {'spdxVersion': 'SPDX-2.3', 'dataLicense': 'CC0-1.0', 'SPDXID': 'SPDXRef-DOCUMENT',
    'name': 'Agent Core General Workbench installed inventory',
    'documentNamespace': 'https://agent-core.invalid/spdx/workbench-' + hashlib.sha256(repr(sorted(packages)).encode()).hexdigest(),
    'creationInfo': {'created': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
                     'creators': ['Tool: agent-core-workbench-inventory']},
    'packages': [{'SPDXID': f'SPDXRef-Package-{i}', 'name': name, 'versionInfo': version,
        'downloadLocation': 'NOASSERTION', 'filesAnalyzed': False, 'licenseConcluded': 'NOASSERTION',
        'licenseDeclared': license_value if license_value in known_licenses else 'NOASSERTION',
        'licenseComments': 'Upstream metadata declaration: ' + license_value, 'copyrightText': 'NOASSERTION',
        'externalRefs': [{'referenceCategory': 'PACKAGE-MANAGER', 'referenceType': 'purl',
            'referenceLocator': f'pkg:{"generic" if kind == "runtime" else kind}/{urllib.parse.quote(name, safe="/")}@{urllib.parse.quote(version, safe="")}'}]} for i, (kind, name, version, license_value) in enumerate(sorted(set(packages)))],
    'files': files,
    'relationships': [{'spdxElementId': 'SPDXRef-DOCUMENT', 'relationshipType': 'DESCRIBES',
                       'relatedSpdxElement': f'SPDXRef-Package-{i}'} for i in range(len(set(packages)))]}
print(json.dumps(document, indent=2))
