#!/usr/bin/env python3
"""Fresh identity, tenant operations, authentication boundaries and SQLite restart in the packaged app."""
import argparse
import json
import os
from pathlib import Path
import runpy
import uuid
from canonical_smoke import Client, ComposeFixture, eventually

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--profile', choices=['Synthetic', 'Real'], default='Synthetic')
parser.add_argument('--hub', action='store_true', help='Also run the existing real JavaScript MessagePack Hub checks (Synthetic only; requires tests/realtime-js dependencies).')
parser.add_argument('--live-openrouter-env', type=Path,
    help='Explicitly opt into provider metadata and inference using OPENROUTER_API_KEY from this private dotenv file; requires Real.')
args = parser.parse_args()
if args.hub and args.profile != 'Synthetic':
    parser.error('The deterministic Hub proof requires Synthetic.')
if args.live_openrouter_env and args.profile != 'Real':
    parser.error('Live provider verification requires --profile Real.')
live_key = None
if args.live_openrouter_env:
    policy = runpy.run_path(str(Path(__file__).with_name('dev-env.py')))
    try:
        live_key = policy['read_env'](args.live_openrouter_env).get('OPENROUTER_API_KEY')
        if not live_key:
            parser.error('The selected private file has no nonblank OPENROUTER_API_KEY.')
    except (OSError, ValueError):
        parser.error('Cannot read private provider inputs; values withheld.')

project = 'agent-core-iam-smoke-' + uuid.uuid4().hex[:12]
with ComposeFixture(project, int(os.environ.get('AGENTCORE_COMPOSE_PORT', '5080')), build=True, profile=args.profile) as f:
    api, prefix = f.client, f.prefix
    container = f.command('ps', '-q', 'agent-core')
    print(json.dumps({'profile': args.profile, 'health': api.call('/health'),
        'image': f.run('docker', 'inspect', '--format', '{{.Image}}', container)}))
    Client(f.base, f.context).call(prefix + '/agents', expected=401)
    api.call('/api/v2/tenants/' + str(uuid.uuid4()) + '/agents', expected=403)
    missing_csrf = api.call(prefix + '/agents', 'POST',
        {'templateId': 'examiner', 'version': 1, 'expectedRevision': api.revision(prefix)}, expected=400, csrf=False)
    assert missing_csrf['reasonCode'] == 'invalid_csrf'
    # Valid authentication, CSRF and revision must reach actual input validation.
    invalid_input = api.call(prefix + '/agents', 'POST',
        {'templateId': '', 'version': 0, 'expectedRevision': api.revision(prefix)}, expected=400)
    assert invalid_input['reasonCode'] == 'invalid_request'
    defaults = eventually(lambda: api.call(prefix + '/chat/agents'),
        lambda catalog: any(item['systemManaged'] for item in catalog['items']), 'managed default provisioning')
    assert defaults['total'] == 1 and defaults['items'][0]['systemManaged']
    if args.profile == 'Real':
        models = api.call(prefix + '/models')
        assert not models['connections'] and not models['offerings'] and models['settings']['defaultOfferingId'] is None
        managed = defaults['items'][0]['instanceId']
        selectable = api.call(prefix + '/chat/models?instanceId=' + managed)
        assert not selectable['models'] and selectable['defaultOfferingId'] is None
        if live_key:
            credential = api.call(prefix + '/credentials', 'POST', {'displayName': 'Disposable inference key', 'kind': 'ApiKey',
                'purpose': 'ModelInference', 'protectedValue': live_key, 'expectedRevision': api.revision(prefix)}, expected=201)['credential']['credentialId']
            connection = api.call(prefix + '/model-connections', 'POST', {'kind': 'OpenRouter', 'label': 'Disposable OpenRouter',
                'credentialId': credential, 'noCredential': False, 'enabled': True, 'expectedRevision': api.revision(prefix),
                'expectedConnectionRevision': 0}, expected=201)
            tested = api.call(prefix + '/model-connections/' + connection['connectionId'] + '/test', 'POST',
                {'expectedRevision': api.revision(prefix), 'expectedConnectionRevision': connection['revision']})
            assert tested['metadataAvailable'], 'Real provider metadata unavailable; private details withheld.'
            preset = next(p for p in models['presets'] if p['kind'] == 'OpenRouter' and p['enabled'] and not p['archived'])
            capabilities = {k: v for k, v in preset['capabilities'].items() if k not in ('source', 'verifiedAt')}
            offering = api.call(prefix + '/models', 'POST', {'connectionId': connection['connectionId'], 'presetId': preset['presetId'],
                'externalModelId': preset['externalModelId'], 'label': 'Disposable Real model', 'transport': preset['transport'],
                'capabilities': capabilities, 'enabled': True, 'retired': False, 'expectedRevision': api.revision(prefix),
                'expectedOfferingRevision': 0}, expected=201)
            api.call(prefix + '/models/default', 'PUT', {'offeringId': offering['offeringId'], 'reasoningEffort': None,
                'expectedRevision': api.revision(prefix), 'expectedSettingsRevision': models['settings']['revision']})
        else:
            f.recreate()
            assert api.call(prefix + '/models')['settings']['defaultOfferingId'] is None
            api.call('/api/v2/auth/logout', 'POST', expected=204)
            api.call(prefix + '/models', expected=401)
            print('PASS: packaged Real HTTPS bootstrap/login, explicit unconfigured model catalog, retained identity/settings after recreation and logout revocation. Inference NOT RUN (explicit live opt-in required).')
            raise SystemExit(0)
    for retired in ('/api/v1/local/owner-capability', '/api/v2/admin/agent-instances', '/api/v1/sessions'):
        api.call(retired, 'POST', {}, expected=404)
    created_agent = api.call(prefix + '/agents', 'POST', {'templateId': 'examiner', 'version': 1, 'expectedRevision': api.revision(prefix)}, expected=201)
    agent = created_agent['instance']
    private_space = api.call(prefix + '/agents/' + agent['instanceId'] + '/private-space')
    role = api.call(prefix + '/roles', 'POST', {'name': 'Read exact smoke context', 'permissions': ['spaces.read'],
        'expectedRevision': api.revision(prefix)}, expected=201)
    api.call(prefix + '/role-assignments', 'POST', {'principalId': created_agent['principalId'], 'roleId': role['roleId'],
        'resourceType': 'space', 'resourceId': private_space['spaceId'], 'expectedRevision': api.revision(prefix)}, expected=201)
    session = api.call(prefix + '/sessions', 'POST', {'retentionKind': 'saved', 'agentInstanceId': agent['instanceId'], 'expectedRevision': api.revision(prefix), 'expectedInstanceRevision': agent['revision']}, expected=201)
    session_id = session.get('sessionId') or session['owner']['sessionId']
    path = prefix + '/sessions/' + session_id
    view = api.call(path + '/conversation')
    original = b'Disposable tenant attachment survives the packaged restart.\n'
    boundary = 'smoke-' + uuid.uuid4().hex
    multipart = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="restart.txt"\r\nContent-Type: text/plain\r\n\r\n'.encode()
        + original + f'\r\n--{boundary}--\r\n'.encode())
    attachment = api.call(path + f'/attachments?uploadId={uuid.uuid4()}&expectedRevision={api.revision(prefix)}&expectedSessionRevision={view["revision"]}',
        'POST', expected=201, raw=multipart, content_type='multipart/form-data; boundary=' + boundary)['attachmentId']
    view = api.call(path + '/conversation')
    api.call(path + '/messages', 'POST', {'messageId': str(uuid.uuid4()), 'text': 'Reply with one short sentence confirming receipt. Do not use tools.', 'attachmentIds': [attachment],
        'expectedRevision': api.revision(prefix), 'expectedSessionRevision': view['revision']}, expected=202)
    history = eventually(lambda: api.call(path + '/messages'), lambda h: any(m['role'] == 'assistant' and m['text'] and m['status'] == 'completed' for m in h['items']), 'completed ' + args.profile + ' reply', 120)
    replies = [m for m in history['items'] if m['role'] == 'assistant']
    assert len(replies) == 1
    assert (replies[0]['text'] == 'Hello from synthetic.') == (args.profile == 'Synthetic')
    if args.hub:
        f.env.update(BASE_URL=f.base, NODE_EXTRA_CA_CERTS=str(f.ca_file), OLD_OWNER_TOKEN='retired-disposable-token', HUB_PROTOCOL='messagepack',
            TENANT_UI_LOGIN=f.login, TENANT_UI_PASSWORD=f.password)
        try:
            print(f.run('node', 'tests/realtime-js/tenant-hub-cutover.mjs'))
        finally:
            for key in ('BASE_URL', 'NODE_EXTRA_CA_CERTS', 'OLD_OWNER_TOKEN', 'HUB_PROTOCOL', 'TENANT_UI_LOGIN', 'TENANT_UI_PASSWORD'):
                f.env.pop(key)
    f.recreate()
    restored = api.call(path + '/messages')
    assert [(m['role'], m['text']) for m in restored['items']] == [(m['role'], m['text']) for m in history['items']]
    assert api.call(path + '/attachments/' + attachment + '/content', binary=True) == original
    Client(f.base, f.context).call(path + '/attachments/' + attachment + '/content', expected=401)
    Client(f.base, f.context).call(path + '/messages', expected=401)
    fresh = Client(f.base, f.context)
    fresh.login(f.login, f.password)
    fresh.call(path + '/conversation')
    api.call('/api/v2/auth/logout', 'POST', expected=204)
    api.call(path + '/messages', expected=401)
    fresh.call(path + '/messages')
    print('PASS (' + args.profile + '): fresh explicit identity, HTTPS cookie/CSRF, tenant isolation, exactly one completed reply, persistent sessions/history/attachment bytes, independent login and logout revocation across container recreation.')
