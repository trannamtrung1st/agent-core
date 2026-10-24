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
    api.call(prefix + '/agents', 'POST', {}, expected=400, csrf=False)
    for retired in ('/api/v1/local/owner-capability', '/api/v2/admin/agent-instances', '/api/v1/sessions'):
        api.call(retired, 'POST', {}, expected=404)
    agent = api.call(prefix + '/agents', 'POST', {'templateId': 'examiner', 'version': 1, 'expectedRevision': api.revision(prefix)}, expected=201)['instance']
    session = api.call(prefix + '/sessions', 'POST', {'agentInstanceId': agent['instanceId'], 'expectedRevision': api.revision(prefix), 'expectedInstanceRevision': agent['revision']}, expected=201)
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
