#!/usr/bin/env python3
"""Disposable HTTPS -> cold snapshot -> independent restore -> continued Workflow -> matching rollback."""
import argparse
from contextlib import closing
import importlib.util
import json
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile
import uuid


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


HERE = Path(__file__).resolve().parent
host_module = module('production_host', HERE / 'production-host.py')
cold = module('cold_backup', HERE.parent / 'data-backup.py')
from canonical_smoke import Client, eventually


def use_data(host, path):
    host.data = path
    host.env['Persistence__ConnectionString'] = 'Data Source=' + str(path / 'app.db')
    for key, name in {'WorkspaceRoot': 'workspaces', 'AttachmentRoot': 'attachments', 'ArtifactRoot': 'artifacts',
            'DefinitionResourceRoot': 'resources', 'CredentialProtectionKeyRoot': 'keys'}.items():
        host.env['Persistence__' + key] = str(path / name)


def exercise(host, assembly):
    host.bootstrap()
    host.start()
    api = host.client
    api.login('owner@example.test', host.password)
    account = api.call('/api/v2/account/me')['account']['accountId']
    tenant = api.call('/api/v2/account/tenants')['items'][0]['tenantId']
    prefix = '/api/v2/tenants/' + tenant
    revision = lambda: api.revision(prefix)
    agent = api.call(prefix + '/agents', 'POST', {'templateId': 'examiner', 'version': 1, 'expectedRevision': revision()}, expected=201)
    instance = agent['instance']['instanceId']
    space = api.call(prefix + '/agents/' + instance + '/private-space')['spaceId']
    role = api.call(prefix + '/roles', 'POST', {'name': 'Read recovery context', 'permissions': ['spaces.read'], 'expectedRevision': revision()}, expected=201)
    api.call(prefix + '/role-assignments', 'POST', {'principalId': agent['principalId'], 'roleId': role['roleId'],
        'resourceType': 'space', 'resourceId': space, 'expectedRevision': revision()}, expected=201)
    session = api.call(prefix + '/sessions', 'POST', {'retentionKind': 'saved', 'agentInstanceId': instance,
        'expectedRevision': revision(), 'expectedInstanceRevision': 1}, expected=201)['sessionId']
    path = prefix + '/sessions/' + session
    text = b'Private attachment retained across coordinated restore.\n'
    boundary = 'recovery-' + uuid.uuid4().hex
    multipart = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="proof.txt"\r\nContent-Type: text/plain\r\n\r\n'.encode()
        + text + f'\r\n--{boundary}--\r\n'.encode())
    view = api.call(path + '/conversation')
    attachment = api.call(path + f'/attachments?uploadId={uuid.uuid4()}&expectedRevision={revision()}&expectedSessionRevision={view["revision"]}',
        'POST', expected=201, raw=multipart, content_type='multipart/form-data; boundary=' + boundary)['attachmentId']
    view = api.call(path + '/conversation')
    api.call(path + '/messages', 'POST', {'messageId': str(uuid.uuid4()), 'text': 'Preserve this Synthetic conversation', 'attachmentIds': [attachment],
        'expectedRevision': revision(), 'expectedSessionRevision': view['revision']}, expected=202)
    history = eventually(lambda: api.call(path + '/messages'),
        lambda h: any(m['role'] == 'assistant' and m['status'] == 'completed' for m in h['items']), 'Synthetic reply')
    credential = api.call(prefix + '/credentials', 'POST', {'displayName': 'Recovery protected token', 'kind': 'Token',
        'purpose': 'General', 'protectedValue': 'private-recovery-canary-' + uuid.uuid4().hex, 'expectedRevision': revision()}, expected=201)['credential']['credentialId']
    human = api.call(prefix + '/access/me')['principalId']
    workflow = str(uuid.uuid4())
    graph = {'schemaVersion': 1, 'nodes': [{'id': 'start', 'name': 'Start', 'kind': 0},
        {'id': 'review', 'name': 'Owner sign-off', 'kind': 3, 'requirement': 1,
            'decision': {'reviewerPrincipalId': human, 'prompt': 'Confirm restored process?'}},
        {'id': 'done', 'name': 'Done', 'kind': 5}, {'id': 'failed', 'name': 'Rejected', 'kind': 6}],
        'edges': [{'id': 'a', 'from': 'start', 'to': 'review'}, {'id': 'b', 'from': 'review', 'to': 'done', 'outcome': 'approve'},
            {'id': 'c', 'from': 'review', 'to': 'failed', 'outcome': 'reject'}], 'inputs': [], 'allowedOrigins': [0], 'coordinatorInstanceIds': []}
    api.call(prefix + '/workflows/' + workflow + '/draft', 'PUT', {'name': 'Recovery sign-off', 'description': 'Disposable recovery proof',
        'graph': graph, 'retired': False, 'expectedRevision': 0, 'expectedSecurityRevision': revision(), 'requestKey': 'recovery-draft'})
    api.call(prefix + '/workflows/' + workflow + '/publish', 'POST', {'expectedRevision': 1, 'expectedSecurityRevision': revision(), 'requestKey': 'recovery-publish'})
    receipt = api.call(prefix + '/workflows/' + workflow + '/executions', 'POST', {'version': 1, 'inputs': {}, 'requestKey': 'recovery-start', 'expectedSecurityRevision': revision()})
    execution_path = prefix + '/workflow-executions/' + receipt['executionId']
    eventually(lambda: api.call(execution_path), lambda v: v['status'] == 'WaitingForHuman', 'durable Workflow bookmark')
    original = host.data
    spec = {'version': 1, 'profile': cold.PROFILE, 'source': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=host_module.REPO, text=True).strip(),
        'database': 'app.db', 'roots': {name: name for name in cold.ROLES}}
    archive = host.root / 'snapshot'
    try:
        cold.backup(original, spec, archive, assembly, True, str(original))
        raise AssertionError('Running application backup was accepted.')
    except cold.Refused as error:
        assert str(error) == 'active_installation_owner'
    host.stop()
    # Ordinary private bytes in every durable root exercise complete root handling, including executable mode.
    for name in ('workspaces', 'artifacts', 'resources'):
        file = original / name / 'offline-proof'
        file.write_bytes((name + '-matching-bytes').encode())
        file.chmod(0o700)
    cold.backup(original, spec, archive, assembly, True, str(original))
    manifest = cold.verify(archive, assembly)
    assert manifest['protectedRecords'] >= 1
    restored = host.root / 'restored'
    cold.restore(archive, restored, assembly, execute=True)
    assert cold.inventory(restored, 'app.db') == manifest['entries']
    # A consistently rehashed but mismatched key set must still fail cryptographic validation.
    bad = host.root / 'wrong-keys'
    shutil.copytree(archive, bad)
    for file in (bad / 'payload' / 'keys').glob('key-*.xml'):
        file.unlink()
    bad_manifest = json.loads((bad / 'manifest.json').read_text())
    bad_manifest['entries'] = cold.inventory(bad / 'payload', 'app.db')
    (bad / 'manifest.json').write_text(json.dumps(bad_manifest))
    rejected_target = host.root / 'must-not-exist'
    try:
        cold.restore(bad, rejected_target, assembly, execute=True)
        raise AssertionError('Wrong protection keys were accepted.')
    except cold.Refused as error:
        assert str(error) == 'protected_state_or_schema_mismatch'
    assert not rejected_target.exists()
    missing_blob = host.root / 'missing-attachment'
    shutil.copytree(archive, missing_blob)
    shutil.rmtree(missing_blob / 'payload' / 'attachments')
    (missing_blob / 'payload' / 'attachments').mkdir(mode=0o700)
    changed_blob = json.loads((missing_blob / 'manifest.json').read_text())
    changed_blob['entries'] = cold.inventory(missing_blob / 'payload', 'app.db')
    (missing_blob / 'manifest.json').write_text(json.dumps(changed_blob))
    try:
        cold.restore(missing_blob, rejected_target, assembly, execute=True)
        raise AssertionError('Missing retained attachment root content was accepted.')
    except cold.Refused as error:
        assert str(error) in ('path_unavailable', 'referenced_content_missing_or_changed')
    assert not rejected_target.exists()
    bad_schema = host.root / 'wrong-elsa'
    shutil.copytree(archive, bad_schema)
    with closing(sqlite3.connect(bad_schema / 'payload' / 'app.db')) as db, db:
        db.execute('UPDATE "__FormalWorkflowRuntimeMigrations" SET MigrationId=MigrationId||\'-invalid\'')
    changed = json.loads((bad_schema / 'manifest.json').read_text())
    changed['schema'] = cold.sqlite_check(bad_schema / 'payload' / 'app.db')
    changed['entries'] = cold.inventory(bad_schema / 'payload', 'app.db')
    (bad_schema / 'manifest.json').write_text(json.dumps(changed))
    try:
        cold.restore(bad_schema, rejected_target, assembly, execute=True)
        raise AssertionError('Wrong Elsa schema was accepted.')
    except cold.Refused as error:
        assert str(error) == 'protected_state_or_schema_mismatch'
    assert not rejected_target.exists()
    use_data(host, restored)
    host.start()
    assert api.call('/api/v2/account/me')['account']['accountId'] == account  # same pre-backup cookie
    assert api.call(path + '/attachments/' + attachment + '/content', binary=True) == text
    assert [(m['role'], m['text']) for m in api.call(path + '/messages')['items']] == [(m['role'], m['text']) for m in history['items']]
    assert api.call(prefix + '/credentials/' + credential)['credential']['credentialId'] == credential
    current = api.call(execution_path)
    assert current['status'] == 'WaitingForHuman'
    step = next(s for s in current['steps'] if s['nodeId'] == 'review')
    api.call(execution_path + '/nodes/review/decision', 'POST', {'approved': True, 'rationale': 'Restored state inspected',
        'requestKey': 'recovery-decision', 'expectedRevision': current['revision'], 'expectedStepRevision': step['revision'], 'expectedSecurityRevision': revision()})
    eventually(lambda: api.call(execution_path), lambda v: v['status'] == 'Completed', 'restored Workflow completion')
    fresh = Client(host.base, host.context)
    fresh.login('owner@example.test', host.password)
    assert fresh.call('/api/v2/account/me')['account']['accountId'] == account
    host.stop()
    # Rollback is a fresh restore of the complete matching pre-change snapshot, never a down migration.
    rollback = host.root / 'rollback'
    cold.restore(archive, rollback, assembly, execute=True)
    use_data(host, rollback)
    host.start()
    assert api.call(execution_path)['status'] == 'WaitingForHuman'
    assert api.call(path + '/attachments/' + attachment + '/content', binary=True) == text
    host.stop()
    return {'coldRestoreBytesKeysCookieHistory': True, 'workflowBookmarkContinued': True,
        'wrongKeyPreflightRefused': True, 'wrongElsaSchemaRefused': True, 'missingAttachmentRefused': True,
        'activeOwnerRefused': True, 'matchingFullSnapshotRollback': True,
        'files': sum(e['type'] == 'file' for e in manifest['entries']), 'protectedRecords': manifest['protectedRecords']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assembly', type=Path, default=host_module.REPO / 'src/AgentCore.Api/bin/Debug/net10.0/AgentCore.Api.dll')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='agent-core-ph-recovery-') as temporary:
        host = host_module.Host(args.assembly.resolve(), Path(temporary).resolve())
        try:
            print(json.dumps(exercise(host, args.assembly.resolve())))
        finally:
            host.stop()


if __name__ == '__main__':
    main()
