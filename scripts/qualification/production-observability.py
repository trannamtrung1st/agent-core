#!/usr/bin/env python3
"""Real OTLP HTTP/protobuf collection, private-content canaries, worker store fault and collector outage."""
import argparse
from contextlib import closing
import http.server
import json
from pathlib import Path
import re
import sqlite3
import tempfile
import threading
import time
import urllib.request
import uuid

from importlib.util import module_from_spec, spec_from_file_location


def load(name, path):
    spec = spec_from_file_location(name, path)
    module = module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


HERE = Path(__file__).resolve().parent
ph = load('production_host', HERE / 'production-host.py')
storage = load('event_storage', HERE.parent / 'event-storage-snapshot.py')
from canonical_smoke import eventually


class Collector:
    def __init__(self):
        self.requests = []
        self.gate = threading.Lock()
        self.failing = False
        owner = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_POST(self):
                length = int(self.headers.get('Content-Length', '0'))
                if length > 2 * 1024 * 1024:
                    self.send_error(413)
                    return
                body = self.rfile.read(length)
                with owner.gate:
                    assert len(owner.requests) < 512
                    owner.requests.append((self.path, body))
                self.send_response(503 if owner.failing else 200)
                self.send_header('Content-Type', 'application/x-protobuf')
                self.send_header('Content-Length', '0')
                self.end_headers()

        self.server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def payload(self, signal):
        with self.gate:
            return b''.join(body for path, body in self.requests if path == '/v1/' + signal)

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)


def health(host):
    with urllib.request.urlopen(host.base + '/health', context=host.context, timeout=5) as response:
        return json.load(response)


def exercise(host, collector):
    host.env.update(Observability__OtlpEnabled='false', Observability__OtlpEndpoint='http://127.0.0.1:' + str(collector.server.server_port),
        OTEL_RESOURCE_ATTRIBUTES='private_resource=private-resource-canary')
    host.bootstrap()
    host.start()
    api = host.client
    api.login('owner@example.test', host.password)
    until = time.monotonic() + 2.1
    while time.monotonic() < until:
        api.call('/api/v2/account/me')
        time.sleep(.1)
    assert not collector.requests, 'Default-off telemetry performed network exports.'
    host.stop()
    host.env['Observability__OtlpEnabled'] = 'true'
    host.start()
    tenant = api.call('/api/v2/account/tenants')['items'][0]['tenantId']
    prefix = '/api/v2/tenants/' + tenant
    agent = api.call(prefix + '/agents', 'POST', {'templateId': 'examiner', 'version': 1, 'expectedRevision': api.revision(prefix)}, expected=201)
    instance = agent['instance']['instanceId']
    space = api.call(prefix + '/agents/' + instance + '/private-space')['spaceId']
    role = api.call(prefix + '/roles', 'POST', {'name': 'Read observation context', 'permissions': ['spaces.read'], 'expectedRevision': api.revision(prefix)}, expected=201)
    api.call(prefix + '/role-assignments', 'POST', {'principalId': agent['principalId'], 'roleId': role['roleId'],
        'resourceType': 'space', 'resourceId': space, 'expectedRevision': api.revision(prefix)}, expected=201)
    session = api.call(prefix + '/sessions', 'POST', {'retentionKind': 'saved', 'agentInstanceId': instance,
        'expectedRevision': api.revision(prefix), 'expectedInstanceRevision': 1}, expected=201)['sessionId']
    path = prefix + '/sessions/' + session
    canary = 'private-prompt-canary-' + uuid.uuid4().hex
    api.call('/api/v2/account/me?private=' + canary)

    def send(message):
        current = api.call(path + '/conversation')
        api.call(path + '/messages', 'POST', {'messageId': str(uuid.uuid4()), 'text': message,
            'expectedRevision': api.revision(prefix), 'expectedSessionRevision': current['revision']}, expected=202)

    send(canary)
    eventually(lambda: api.call(path + '/messages'), lambda h: sum(m['role'] == 'assistant' and m['status'] == 'completed' for m in h['items']) == 1, 'observed Synthetic reply')
    eventually(lambda: collector.payload('traces'), lambda p: b'AgentCore.Runtime' in p and b'model' in p, 'OTLP trace export')
    eventually(lambda: collector.payload('metrics'), lambda p: b'background_worker_passes' in p and b'runtime_admissions' in p, 'OTLP metric export')
    eventually(lambda: health(host), lambda h: all(v['status'] == 'ready' for v in h['workers'].values()) and h['scheduler']['status'] == 'ready', 'worker readiness')
    # A reversible missing-store fault on this disposable installation produces an actual worker diagnostic.
    with closing(sqlite3.connect(host.data / 'app.db', timeout=10)) as db, db:
        db.execute('ALTER TABLE AgentRuns RENAME TO PH_Injected_AgentRuns')
    try:
        eventually(lambda: health(host), lambda h: h['workers']['runs']['status'] == 'degraded', 'degraded Run worker')
        api.call('/api/v2/account/me')  # unrelated valid traffic still works
        eventually(lambda: (host.root / 'host.log').read_text(),
            lambda text: 'AgentRun pass failed' in text and 'DiagnosticId=' in text and 'TraceId=' in text, 'correlated diagnostic')
    finally:
        with closing(sqlite3.connect(host.data / 'app.db', timeout=10)) as db, db:
            db.execute('ALTER TABLE PH_Injected_AgentRuns RENAME TO AgentRuns')
    eventually(lambda: health(host), lambda h: h['workers']['runs']['status'] == 'ready', 'worker recovery')
    snapshot = storage.snapshot(host.data / 'app.db', include_work=True)
    with closing(sqlite3.connect(host.data / 'app.db')) as db:
        offering, connection = db.execute("SELECT json_extract(PayloadJson,'$.pinnedModel.offeringId'),json_extract(PayloadJson,'$.pinnedModel.connectionId') FROM AgentRuns LIMIT 1").fetchone()
    assert offering
    assert snapshot['work']['runs']['completed']['rows'] == 1
    assert snapshot['work']['runs_indeterminate_effects'] == 0
    assert snapshot['filesystem_free_bytes'] > 0
    collector.failing = True
    send('Safe independent work while telemetry returns 503')
    eventually(lambda: api.call(path + '/messages'), lambda h: sum(m['role'] == 'assistant' and m['status'] == 'completed' for m in h['items']) == 2, 'reply despite collector failure')
    host.stop()
    all_export = collector.payload('traces') + collector.payload('metrics')
    assert canary.encode() not in all_export and b'private-resource-canary' not in all_export
    assert canary not in (host.root / 'host.log').read_text()
    metrics = collector.payload('metrics')
    for identity in (tenant, session, instance, offering, connection):
        if not identity:
            continue
        assert identity.encode() not in metrics and identity.replace('-', '').encode() not in metrics
    logs = (host.root / 'host.log').read_text()
    trace_ids = re.findall(r'TraceId=([0-9a-f]{32})', logs)
    assert any(bytes.fromhex(trace_id) in collector.payload('traces') for trace_id in trace_ids)
    return {'defaultOffMadeNoExports': True, 'tracesAndMetricsReceived': True, 'workerFailureCorrelatedAndRecovered': True, 'collector503DidNotFailOrReplayChat': True,
        'privateCanariesAbsent': True, 'noTenantSessionInstanceOfferingConnectionMetricDimensions': True, 'readOnlyBacklogAndDiskSnapshot': True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assembly', type=Path, default=ph.REPO / 'src/AgentCore.Api/bin/Debug/net10.0/AgentCore.Api.dll')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='agent-core-ph-observe-') as temporary:
        collector = Collector()
        host = ph.Host(args.assembly.resolve(), Path(temporary).resolve())
        try:
            print(json.dumps(exercise(host, collector)))
        finally:
            host.stop()
            collector.close()


if __name__ == '__main__':
    main()
