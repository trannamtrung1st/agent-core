#!/usr/bin/env python3
"""Fresh identity, tenant operations, authentication boundaries and SQLite restart in the packaged app."""
import os
import uuid
from canonical_smoke import Client, ComposeFixture, eventually

project = 'agent-core-iam-smoke-' + uuid.uuid4().hex[:12]
with ComposeFixture(project, int(os.environ.get('AGENTCORE_COMPOSE_PORT', '5080')), build=True) as f:
    api, prefix = f.client, f.prefix
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
    api.call(path + '/messages', 'POST', {'messageId': str(uuid.uuid4()), 'text': 'Disposable canonical restart smoke', 'attachmentIds': [attachment],
        'expectedRevision': api.revision(prefix), 'expectedSessionRevision': view['revision']}, expected=202)
    history = eventually(lambda: api.call(path + '/messages'), lambda h: any(m['role'] == 'assistant' and m['text'] == 'Hello from synthetic.' and m['status'] == 'completed' for m in h['items']), 'completed Synthetic reply')
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
    print('PASS: fresh explicit identity, HTTPS cookie/CSRF, tenant isolation, completed text, persistent sessions/history/attachment bytes, independent login and logout revocation across container recreation.')
