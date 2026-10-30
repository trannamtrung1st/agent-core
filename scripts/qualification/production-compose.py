#!/usr/bin/env python3
"""Actual non-root HTTPS image: cold volume snapshot, independent restored volume and matching-volume rollback."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import uuid
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
from canonical_smoke import ComposeFixture, eventually

spec = importlib.util.spec_from_file_location('cold', HERE.parent / 'data-backup.py')
cold = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cold)


def exercise(f, root):
    api, prefix = f.client, f.prefix
    agent = api.call(prefix + '/agents', 'POST', {'templateId': 'examiner', 'version': 1,
        'expectedRevision': api.revision(prefix)}, expected=201)
    instance = agent['instance']['instanceId']
    space = api.call(prefix + '/agents/' + instance + '/private-space')['spaceId']
    role = api.call(prefix + '/roles', 'POST', {'name': 'Read restored context', 'permissions': ['spaces.read'], 'expectedRevision': api.revision(prefix)}, expected=201)
    api.call(prefix + '/role-assignments', 'POST', {'principalId': agent['principalId'], 'roleId': role['roleId'],
        'resourceType': 'space', 'resourceId': space, 'expectedRevision': api.revision(prefix)}, expected=201)
    session = api.call(prefix + '/sessions', 'POST', {'retentionKind': 'saved', 'agentInstanceId': instance,
        'expectedRevision': api.revision(prefix), 'expectedInstanceRevision': 1}, expected=201)['sessionId']
    path = prefix + '/sessions/' + session

    def send(text):
        current = api.call(path + '/conversation')
        return api.call(path + '/messages', 'POST', {'messageId': str(uuid.uuid4()), 'text': text,
            'expectedRevision': api.revision(prefix), 'expectedSessionRevision': current['revision']}, expected=202)

    send('Packaged snapshot proof')
    before = eventually(lambda: api.call(path + '/messages'), lambda h: any(m['role'] == 'assistant' and m['status'] == 'completed' for m in h['items']), 'packaged reply')
    credential = api.call(prefix + '/credentials', 'POST', {'displayName': 'Packaged snapshot token', 'kind': 'Token',
        'purpose': 'General', 'protectedValue': 'disposable-' + uuid.uuid4().hex, 'expectedRevision': api.revision(prefix)}, expected=201)['credential']['credentialId']
    account = api.call('/api/v2/account/me')['account']['accountId']
    container = f.command('ps', '-q', 'agent-core')
    image = f.run('docker', 'inspect', '--format', '{{.Image}}', container)
    assert f.command('exec', '-T', 'agent-core', 'id', '-u') == '1654'
    f.command('exec', '-T', 'agent-core', 'sh', '-c', 'test ! -e /app/.playwright')
    runtimes = {line.split()[0]: line.split()[1] for line in f.command('exec', '-T', 'agent-core', 'dotnet', '--list-runtimes').splitlines()}
    assert all(tuple(map(int, runtimes[name].split('.'))) >= (10, 0, 13) for name in ('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'))
    # Stop every selected writer before copying its complete volume. No live files/database consistency claim.
    f.command('stop', '--timeout', '30')
    installation, published = root / 'installation', root / 'published'
    installation.mkdir(mode=0o700)
    published.mkdir(mode=0o700)
    f.run('docker', 'cp', container + ':/data/.', str(installation))
    f.run('docker', 'cp', container + ':/app/.', str(published))
    installation.chmod(0o700)
    layout = {'version': 1, 'profile': cold.PROFILE, 'source': '0' * 40, 'database': 'agent-core.db',
        'roots': {'workspaces': 'workspaces', 'attachments': 'attachments', 'artifacts': 'artifacts',
            'resources': 'definition-resources', 'keys': 'credential-protection-keys'}}
    # The matching five published assemblies are hashed; the exact image label is recorded separately below.
    revision = f.run('docker', 'image', 'inspect', '--format', '{{index .Config.Labels "org.opencontainers.image.revision"}}', image)
    layout['source'] = revision.removeprefix('worktree-') if revision.startswith('worktree-') else revision
    snapshot = root / 'snapshot'
    cold.backup(installation, layout, snapshot, published / 'AgentCore.Api.dll', True, str(installation))
    restored = root / 'restored'
    cold.restore(snapshot, restored, published / 'AgentCore.Api.dll', execute=True)
    volume = f.project + '-restored'
    helper = f.project + '-restore-loader'
    extra_overlay = root / 'restore.yml'
    f.run('docker', 'volume', 'create', '--label', 'agent-core.fixture=' + f.project, volume)
    try:
        # Explicit operator provisioning of one task-owned volume; application execution remains non-root.
        f.run('docker', 'create', '--name', helper, '--user', '0:0', '--entrypoint', 'sh',
            '--mount', 'type=volume,source=' + volume + ',target=/data', image,
            '-c', 'chown -R 1654:1654 /data && chmod 700 /data')
        try:
            f.run('docker', 'cp', str(restored) + '/.', helper + ':/data/')
            f.run('docker', 'start', '-a', helper)
        finally:
            f.run('docker', 'rm', '-f', helper)
        extra_overlay.write_text(json.dumps({'volumes': {'agent-core-data': {'name': volume}}}))
        f.compose += ['-f', str(extra_overlay)]
        f.command('up', '--no-build', '--force-recreate', '-d')
        f.ready()
        assert api.call('/api/v2/account/me')['account']['accountId'] == account
        assert api.call(prefix + '/credentials/' + credential)['credential']['credentialId'] == credential
        assert [(m['role'], m['text']) for m in api.call(path + '/messages')['items']] == [(m['role'], m['text']) for m in before['items']]
        assert f.command('exec', '-T', 'agent-core', 'id', '-u') == '1654'
        audit = json.loads(f.command('exec', '-T', 'agent-core', 'dotnet', 'AgentCore.Api.dll', 'verify-protected-state',
            '/data/agent-core.db', '/data/credential-protection-keys'))
        assert audit['valid'] and audit['protectedRecords'] >= 1
        send('Continue on independently restored packaged volume')
        eventually(lambda: api.call(path + '/messages'), lambda h: sum(m['role'] == 'assistant' and m['status'] == 'completed' for m in h['items']) == 2, 'restored packaged reply')
        f.command('stop', '--timeout', '30')
        f.compose = f.compose[:-2]
        f.command('up', '--no-build', '--force-recreate', '-d')
        f.ready()
        assert [(m['role'], m['text']) for m in api.call(path + '/messages')['items']] == [(m['role'], m['text']) for m in before['items']]
        return {'image': image, 'sourceFingerprint': revision, 'nonRootUid': 1654, 'sharedRuntime': runtimes['Microsoft.NETCore.App'],
            'actualImageIndependentVolumeRestore': True, 'sameCookieIdentityAndProtectedCredential': True, 'runtimes': runtimes,
            'safeChatAfterRestore': True, 'matchingImageAndQuiescedOriginalVolumeRollback': True}
    finally:
        # Ensure the fixture's ordinary cleanup again owns the original declaration.
        if f.compose[-2:] == ['-f', str(extra_overlay)]:
            f.command('stop', '--timeout', '30')
            f.compose = f.compose[:-2]
        f.command('down', '--timeout', '30')
        # A recreated original container has no reference to this explicitly owned restore volume.
        f.run('docker', 'volume', 'rm', volume)


def main():
    project = 'agent-core-ph-' + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix='ph-compose-restore-') as temporary:
        with ComposeFixture(project, int(os.environ.get('AGENTCORE_COMPOSE_PORT', '15880')), build=False) as fixture:
            print(json.dumps(exercise(fixture, Path(temporary).resolve())))


if __name__ == '__main__':
    main()
