#!/usr/bin/env python3
"""Key-free watch/cursor restart evidence on an explicitly named disposable Compose project."""
import argparse
import json
import os
import pathlib
import sqlite3
import subprocess
import tempfile
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--project', required=True)
parser.add_argument('--port', type=int, required=True)
args = parser.parse_args()
if not args.project.startswith('email-monitor-'):
    raise SystemExit('Use an isolated email-monitor-* project; this check stops/recreates its container.')
env = dict(os.environ, COMPOSE_PROJECT_NAME=args.project, AGENTCORE_COMPOSE_PORT=str(args.port))
compose = ['docker', 'compose']
base = f'http://127.0.0.1:{args.port}'

def run(*command):
    return subprocess.check_output(command, env=env, text=True).strip()

def call(path, method='GET', value=None, token=None):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['X-AgentCore-Owner-Capability'] = token
    request = urllib.request.Request(base + path, method=method, data=None if value is None else json.dumps(value).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=10) as response:
        return json.load(response)

def ready():
    for _ in range(90):
        try:
            if call('/health')['profile'] == 'Synthetic':
                return
        except Exception:
            pass
        time.sleep(1)
    raise RuntimeError('Isolated Synthetic host unavailable')

def watch_state(account_id):
    cid = run(*compose, 'ps', '-aq', 'agent-core')
    with tempfile.TemporaryDirectory(prefix='email-watch-db-') as target:
        run('docker', 'cp', f'{cid}:/data/.', target)
        with sqlite3.connect(pathlib.Path(target) / 'agent-core.db') as db:
            state = json.loads(db.execute('SELECT StateJson FROM EmailWatches WHERE AccountId=?', (account_id,)).fetchone()[0])
            events = db.execute("SELECT COUNT(*) FROM CoreEvents WHERE PayloadJson LIKE '%email.message.received%'").fetchone()[0]
        return {name: state[name] for name in ['Enabled', 'ConfigurationRevision', 'Generation', 'Cursor', 'AuthorizationFingerprint']}, events

try:
    run(*compose, 'up', '--no-build', '-d'); ready()
    token = call('/api/v1/local/owner-capability', 'POST')['token']
    profile = {'preset': 'Custom', 'emailAddress': 'restart@example.test', 'imapUsername': 'restart@example.test', 'smtpUsername': 'restart@example.test',
        'imapEndpoint': {'host': 'mail.example.test', 'port': 993, 'tlsMode': 'ImplicitTls'}, 'smtpEndpoint': {'host': 'mail.example.test', 'port': 465, 'tlsMode': 'ImplicitTls'}}
    account = call('/api/v2/admin/email-accounts', 'POST', {'displayName': 'Disposable restart monitor', 'profile': profile, 'protectedValue': 'offline-fixture-password'}, token)
    path = f"/api/v2/admin/email-accounts/{account['credentialId']}/monitoring"
    saved = call(path, 'PUT', {'expectedRevision': 0, 'enabled': True, 'intervalSeconds': 300}, token)
    assert saved['configurationRevision'] == 1
    for _ in range(35):
        summary = call(path, token=token)
        if summary['lastSuccessAtUtc'] and summary['status'] == 'Monitoring':
            break
        time.sleep(1)
    else:
        raise RuntimeError('From-now baseline was not observed')
    assert summary['subscribedInstances'] == 0
    run(*compose, 'stop')
    before, event_count = watch_state(account['credentialId'])
    assert before['Cursor'] is not None
    run(*compose, 'up', '--no-build', '--force-recreate', '-d'); ready()
    token = call('/api/v1/local/owner-capability', 'POST')['token']
    summary = call(path, token=token)
    assert summary['enabled'] and summary['configurationRevision'] == 1 and summary['lastSuccessAtUtc']
    assert call(f"/api/v2/admin/credentials/{account['credentialId']}", token=token)['revision'] == account['revision']
    run(*compose, 'stop')
    after, after_count = watch_state(account['credentialId'])
    assert before == after, 'Cursor, generation or authorization unexpectedly changed across recreation'
    assert event_count == after_count, 'From-now baseline/recreation emitted mail events'
    print('Email watch settings, cursor, generation and authorization survived container recreation; no historical Events or Runs from the unsubscribed baseline.')
finally:
    run(*compose, 'down')
