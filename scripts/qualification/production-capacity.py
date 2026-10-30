#!/usr/bin/env python3
"""Measured disposable SQLite/Synthetic envelope: three tenants, two active Runtimes, bounded offered overload."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import signal
import subprocess
import tempfile
import threading
import time
import uuid


HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('production_host', HERE / 'production-host.py')
ph = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ph)
from canonical_smoke import Client, eventually


def percentiles(values):
    ordered = sorted(values)
    return {f'p{p}_ms': round(ordered[max(0, math.ceil(len(ordered) * p / 100) - 1)], 2) for p in (50, 95, 99)}


def counters_summary(path):
    data = json.loads(path.read_text())
    events = data.get('Events', data.get('events', []))
    result = {}
    for event in events:
        name = event.get('CounterName') or event.get('counterName') or event.get('name')
        tags = event.get('Tags') or event.get('tags')
        if name and tags:
            name += '[' + (tags if isinstance(tags, str) else json.dumps(tags, sort_keys=True)) + ']'
        value = event.get('Value', event.get('value'))
        if isinstance(name, str) and isinstance(value, (int, float)) and math.isfinite(value):
            row = result.setdefault(name, {'samples': 0, 'min': value, 'max': value, 'last': value})
            row.update(samples=row['samples'] + 1, min=min(row['min'], value), max=max(row['max'], value), last=value)
    return result


def exercise(host):
    host.env['AgentCore__MaxActiveSessions'] = '2'
    host.bootstrap()
    host.start()
    api = host.client
    api.login('owner@example.test', host.password)
    tenants = [api.call('/api/v2/account/tenants')['items'][0]['tenantId']]
    for index in range(2):
        tenants.append(api.call('/api/v2/system/tenants', 'POST', {'name': 'Capacity fixture ' + str(index), 'slug': 'capacity-' + str(index)}, expected=201)['tenantId'])
    cases = []
    tenant_writes = {}
    for tenant in tenants:
        tenant_writes[tenant] = threading.Lock()
        prefix = '/api/v2/tenants/' + tenant
        eventually(lambda: api.call(prefix + '/chat/agents'), lambda catalog: len(catalog['items']) >= 1, 'default provisioning')
        created = api.call(prefix + '/agents', 'POST', {'templateId': 'examiner', 'version': 1, 'expectedRevision': api.revision(prefix)}, expected=201)
        instance = created['instance']['instanceId']
        space = api.call(prefix + '/agents/' + instance + '/private-space')['spaceId']
        role = api.call(prefix + '/roles', 'POST', {'name': 'Read load context', 'permissions': ['spaces.read'], 'expectedRevision': api.revision(prefix)}, expected=201)
        api.call(prefix + '/role-assignments', 'POST', {'principalId': created['principalId'], 'roleId': role['roleId'], 'resourceType': 'space',
            'resourceId': space, 'expectedRevision': api.revision(prefix)}, expected=201)
        for _ in range(2):
            session = api.call(prefix + '/sessions', 'POST', {'retentionKind': 'saved', 'agentInstanceId': instance,
                'expectedRevision': api.revision(prefix), 'expectedInstanceRevision': 1}, expected=201)['sessionId']
            client = Client(host.base, host.context)
            client.login('owner@example.test', host.password)
            cases.append((client, prefix, prefix + '/sessions/' + session, tenant_writes[tenant]))
    output = host.root / 'runtime-counters.json'
    counter_log = (host.root / 'counter-tool.log').open('wb')
    counter = subprocess.Popen(['dotnet-counters', 'collect', '-p', str(host.process.pid), '--refresh-interval', '1',
        '--counters', 'System.Runtime,AgentCore.Runtime', '--format', 'json', '--output', str(output)], stdout=counter_log, stderr=counter_log)
    original_db = (host.data / 'app.db').stat().st_size
    admitted, completed, conflicts = [], [], []

    def turn(case, text):
        client, prefix, path, write_gate = case
        before = client.call(path + '/messages')
        count = sum(m['role'] == 'assistant' and m['status'] == 'completed' for m in before['items'])
        start = time.monotonic()
        message = str(uuid.uuid4())
        stale_conflicts = 0
        with write_gate:
            for attempt in range(4):
                view = client.call(path + '/conversation')
                try:
                    client.call(path + '/messages', 'POST', {'messageId': message, 'text': text,
                        'expectedRevision': client.revision(prefix), 'expectedSessionRevision': view['revision']}, expected=202)
                    break
                except AssertionError as error:
                    # Only an explicit pre-commit stale-revision refusal permits a refreshed read.
                    # Same logical message ID is retained; uncertain HTTP failures are never retried.
                    if 'got 409 (stale_revision)' not in str(error) or attempt == 3:
                        raise
                    stale_conflicts += 1
        acceptance = (time.monotonic() - start) * 1000
        eventually(lambda: client.call(path + '/messages'),
            lambda h: sum(m['role'] == 'assistant' and m['status'] == 'completed' for m in h['items']) == count + 1, 'load turn', seconds=90)
        return acceptance, (time.monotonic() - start) * 1000, stale_conflicts

    started = time.monotonic()
    try:
        with ThreadPoolExecutor(max_workers=6) as pool:
            for _ in range(2):
                for acceptance, completion, stale in pool.map(lambda case: turn(case, 'Representative key-free concurrent work'), cases):
                    admitted.append(acceptance)
                    completed.append(completion)
                    conflicts.append(stale)
            normal_seconds = time.monotonic() - started
            overload_start = time.monotonic()
            overloaded = list(pool.map(lambda case: turn(case, '[test:durable-stream]'), cases))
            overload_seconds = time.monotonic() - overload_start
        # An abrupt process death after settled work must reopen the same history, without added replies.
        histories = [case[0].call(case[2] + '/messages') for case in cases]
        host.process.kill()
        host.process.wait(timeout=15)
        host.log.close()
        host.process = None
        host.start()
        for case, before in zip(cases, histories):
            after = case[0].call(case[2] + '/messages')
            assert [(m['role'], m['text']) for m in after['items']] == [(m['role'], m['text']) for m in before['items']]
        api.call('/api/v2/account/me')
        host.stop()
    finally:
        if counter.poll() is None:
            counter.send_signal(signal.SIGINT)
            try:
                counter.wait(timeout=15)
            except subprocess.TimeoutExpired:
                counter.kill()
                counter.wait()
        counter_log.close()
    runtime = counters_summary(output)
    assert runtime, 'Runtime counter capture produced no numeric samples.'
    # dotnet-counters names vary by servicing version; retain the actual names and values, not invented units.
    active = [v['max'] for k, v in runtime.items() if 'active_runtimes' in k]
    assert active and max(active) <= 2
    capacity = [v['max'] for k, v in runtime.items() if 'runtime_admissions' in k and 'capacity' in k]
    assert capacity and max(capacity) > 0
    return {'profile': 'controlled-synthetic', 'tenants': 3, 'sessions': 6, 'maxActiveRuntimes': 2,
        'normal': {'completedTurns': len(completed), 'seconds': round(normal_seconds, 2), 'turnsPerSecond': round(len(completed) / normal_seconds, 3),
            'admission': percentiles(admitted), 'completion': percentiles(completed), 'explicitStaleRevisionRefusals': sum(conflicts)},
        'overload': {'offeredTurns': 6, 'completedTurns': len(overloaded), 'seconds': round(overload_seconds, 2),
            'admission': percentiles([x[0] for x in overloaded]), 'completion': percentiles([x[1] for x in overloaded]),
            'httpAccepted': 6, 'semantics': 'Durable input accepted; excess Runtime dispatch deferred at configured capacity.'},
        'crashReopenPreservedSixHistories': True, 'duplicateReplies': 0, 'databaseBytesBefore': original_db,
        'databaseBytesAfter': (host.data / 'app.db').stat().st_size,
        'fileBytesAfter': sum(p.stat().st_size for p in host.data.rglob('*') if p.is_file()),
        'runtimeCounters': runtime, 'machine': {'platform': platform.platform(), 'architecture': platform.machine(), 'logicalCpus': os.cpu_count()},
        'limits': 'Small local Synthetic text/read workload; no provider, physical audio, hostile native, customer or sustained production SLA claim.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assembly', type=Path, default=ph.REPO / 'src/AgentCore.Api/bin/Debug/net10.0/AgentCore.Api.dll')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='agent-core-ph-capacity-') as temporary:
        host = ph.Host(args.assembly.resolve(), Path(temporary).resolve())
        try:
            result = exercise(host)
            text = json.dumps(result, indent=2, sort_keys=True)
            if args.output:
                with args.output.open('x') as stream:
                    stream.write(text + '\n')
            print(text)
        finally:
            host.stop()


if __name__ == '__main__':
    main()
