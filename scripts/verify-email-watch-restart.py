#!/usr/bin/env python3
"""Key-free canonical tenant watch/cursor restart on an explicitly disposable Compose project."""
import argparse
from canonical_smoke import Client, ComposeFixture, eventually

parser = argparse.ArgumentParser()
parser.add_argument('--project', required=True)
parser.add_argument('--port', type=int, required=True)
args = parser.parse_args()
if not args.project.startswith('email-monitor-'):
    raise SystemExit('Use a fresh isolated email-monitor-* project; this check recreates and removes only its disposable volume.')

with ComposeFixture(args.project, args.port) as f:
    api, prefix = f.client, f.prefix
    account = api.call(prefix + '/credentials', 'POST', {'displayName': 'Disposable restart monitor', 'kind': 'Password', 'purpose': 'EmailAccount',
        'protectedValue': 'offline-fixture-password', 'expectedRevision': api.revision(prefix),
        'emailAccount': {'preset': 'Custom', 'emailAddress': 'restart@example.test', 'imapUsername': 'restart@example.test', 'smtpUsername': 'restart@example.test',
            'imapEndpoint': {'host': 'mail.example.test', 'port': 993, 'tlsMode': 'ImplicitTls'}, 'smtpEndpoint': {'host': 'mail.example.test', 'port': 465, 'tlsMode': 'ImplicitTls'}}}, expected=201)['credential']
    path = prefix + '/credentials/' + account['credentialId'] + '/monitoring'
    view = api.call(path)
    api.call(path, 'PUT', {'expectedRevision': api.revision(prefix), 'expectedCredentialRevision': account['revision'], 'expectedWatchRevision': view['configurationRevision'], 'enabled': True, 'intervalSeconds': 300})
    summary = eventually(lambda: api.call(path), lambda s: s['lastSuccessAtUtc'] and s['status'] == 'Monitoring', 'from-now mailbox baseline')
    f.command('stop', '--timeout', '30')
    before, event_count = f.watch_state(account['credentialId'])
    assert before['Cursor'] is not None
    f.recreate()
    summary = api.call(path)
    assert summary['enabled'] and summary['configurationRevision'] == 1 and summary['lastSuccessAtUtc']
    assert api.call(prefix + '/credentials/' + account['credentialId'])['credential']['revision'] == account['revision']
    Client(f.base, f.context).call(path, expected=401)
    f.command('stop', '--timeout', '30')
    after, after_count = f.watch_state(account['credentialId'])
    assert before == after, 'Cursor, generation or authorization changed across recreation'
    assert event_count == after_count, 'Baseline/recreation emitted historical events'
    print('PASS: canonical tenant watch settings, cursor, generation and authorization survive recreation; no historical events from the unsubscribed baseline; anonymous read denied.')
