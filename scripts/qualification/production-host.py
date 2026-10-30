#!/usr/bin/env python3
"""Disposable direct-HTTPS production preflight, IAM, ownership and restart proof. No AI calls."""
import argparse
import json
import os
from pathlib import Path
import secrets
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.request
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from canonical_smoke import Client

REPO = Path(__file__).resolve().parents[2]


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


class Host:
    def __init__(self, assembly, root):
        self.root = root
        self.data = root / 'installation'
        self.data.mkdir(mode=0o700)
        self.password = secrets.token_urlsafe(32)
        self.command = ['dotnet', str(assembly)]
        self.port = port()
        self.base = f'https://127.0.0.1:{self.port}'
        self.env = {k: v for k, v in os.environ.items() if k in ('PATH', 'HOME', 'DOTNET_ROOT', 'TMPDIR')}
        self.env.update(DOTNET_ENVIRONMENT='Production', AgentCore__Profile='Synthetic',
            AgentCore__AgentDirectory=str(REPO / 'agents'), Persistence__Provider='Sqlite',
            Persistence__ConnectionString='Data Source=' + str(self.data / 'app.db'),
            AllowedHosts='127.0.0.1;localhost', Browser__Enabled='false', OpenSandbox__Enabled='false',
            Email__Enabled='false', Email__SmtpEnabled='false', GoogleDrive__Enabled='false',
            Observability__OtlpEnabled='false', Observability__LogConversationContent='false')
        for key, name in {'WorkspaceRoot': 'workspaces', 'AttachmentRoot': 'attachments', 'ArtifactRoot': 'artifacts',
                'DefinitionResourceRoot': 'resources', 'CredentialProtectionKeyRoot': 'keys'}.items():
            self.env['Persistence__' + key] = str(self.data / name)
        tls_password = secrets.token_urlsafe(32)
        self.env.update(Kestrel__Certificates__Default__Path=str(root / 'app.pfx'), Kestrel__Certificates__Default__Password=tls_password)
        self.run('openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
            '-keyout', str(root / 'key.pem'), '-out', str(root / 'cert.pem'), '-subj', '/CN=127.0.0.1',
            '-addext', 'subjectAltName=IP:127.0.0.1')
        self.run('openssl', 'pkcs12', '-export', '-in', str(root / 'cert.pem'), '-inkey', str(root / 'key.pem'),
            '-out', str(root / 'app.pfx'), '-passout', 'env:Kestrel__Certificates__Default__Password')
        self.context = ssl.create_default_context(cafile=str(root / 'cert.pem'))
        self.client = Client(self.base, self.context)
        self.process = None

    def run(self, *command, env=None, expected=0):
        result = subprocess.run(command, cwd=REPO, env=env or self.env, capture_output=True, timeout=120)
        if result.returncode != expected:
            raise AssertionError('Disposable command failed; private output withheld.')
        return result

    def bootstrap(self, repeat=False):
        env = dict(self.env)
        for key, value in {'LOGIN': 'owner@example.test', 'PASSWORD': self.password, 'DISPLAY_NAME': 'Fixture',
                'TENANT_NAME': 'Fixture', 'TENANT_SLUG': 'fixture'}.items():
            env['AGENTCORE_BOOTSTRAP_' + key] = value
        if repeat:
            self.refuse(env=env, args=['bootstrap-identity'], reason=b'SecurityConflictException')
        else:
            self.run(*self.command, 'bootstrap-identity', env=env)

    def start(self):
        self.log = open(self.root / 'host.log', 'ab')
        self.process = subprocess.Popen([*self.command, '--urls', self.base], cwd=REPO, env=self.env,
            stdout=self.log, stderr=self.log)
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise AssertionError('Disposable HTTPS host exited; private log retained only in fixture.')
            try:
                with urllib.request.urlopen(self.base + '/health', context=self.context, timeout=1) as response:
                    assert json.load(response)['profile'] == 'Synthetic'
                    assert response.headers['X-Content-Type-Options'] == 'nosniff'
                    assert response.headers['Strict-Transport-Security'] == 'max-age=31536000'
                return
            except (OSError, TimeoutError):
                time.sleep(.1)
        raise AssertionError('Disposable HTTPS readiness timed out.')

    def stop(self):
        if self.process is not None:
            self.process.terminate()
            try:
                self.process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
                raise AssertionError('Controlled shutdown exceeded 30 seconds.')
            finally:
                self.log.close()
                self.process = None

    def refuse(self, env=None, reason=b'', args=None):
        result = subprocess.run([*self.command, *(args or ['--urls', self.base])], cwd=REPO, env=env or self.env,
            capture_output=True, timeout=30)
        assert result.returncode != 0 and reason in result.stdout + result.stderr
        assert b'private-canary' not in result.stdout + result.stderr


def exercise(host):
    host.bootstrap()
    host.start()
    client = host.client
    Client(host.base, host.context).call('/api/v2/account/me', expected=401)
    client.login('owner@example.test', host.password)
    account = client.call('/api/v2/account/me')['account']['accountId']
    tenant = client.call('/api/v2/account/tenants')['items'][0]['tenantId']
    prefix = '/api/v2/tenants/' + tenant
    client.call('/api/v2/tenants/' + str(uuid.uuid4()) + '/agents', expected=403)
    client.call(prefix + '/agents', 'POST', {}, expected=400, csrf=False)
    created = client.call(prefix + '/agents', 'POST', {'templateId': 'examiner', 'version': 1,
        'expectedRevision': client.revision(prefix)}, expected=201)
    instance = created['instance']['instanceId']
    host.refuse(reason=b'SQLite installation ownership unavailable')
    host.refuse(env=dict(host.env, Security__Http__AllowInsecureLocalHttp='true'), reason=b'local_http_exception')
    host.refuse(env=dict(host.env, AllowedHosts='*'), reason=b'exact_hosts_required')
    host.refuse(env=dict(host.env, Kestrel__Certificates__Default__Password='private-canary'), reason=b'usable_certificate_required')
    host.refuse(reason=b'https_listener_required', args=['--urls', 'http://127.0.0.1:' + str(port())])
    request = urllib.request.Request(host.base + '/api/v2/account/me', headers={'Host': 'forged.example.test'})
    try:
        urllib.request.urlopen(request, context=host.context, timeout=5)
        raise AssertionError('Forged Host was accepted.')
    except urllib.error.HTTPError as error:
        assert error.code == 400
    host.stop()
    host.start()
    assert client.call('/api/v2/account/me')['account']['accountId'] == account
    assert any(a['instance']['instanceId'] == instance for a in client.call(prefix + '/agents')['items'])
    client.call('/api/v2/auth/logout', 'POST', expected=204)
    client.call('/api/v2/account/me', expected=401)
    host.stop()
    host.bootstrap(repeat=True)
    # Account-only state must force key parsing/usability; a filename is not proof.
    account_keys = list((host.data / 'keys' / 'account-sessions').glob('key-*.xml'))
    assert account_keys
    for file in account_keys:
        file.write_text('<invalid />')
    host.refuse(args=['verify-protected-state', str(host.data / 'app.db'), str(host.data / 'keys')],
        reason=b'Protected state verification refused')
    host.refuse(reason=b'protected_state_or_schema_mismatch')
    assert all(file.read_text() == '<invalid />' for file in account_keys)
    return {'httpsLoginCsrfMutationRestartLogout': True, 'secondOwnerRefused': True,
        'unsafeConfigAndHostRefused': True, 'rebootstrapRefused': True, 'malformedAccountKeysRefusedWithoutRepair': True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assembly', type=Path, default=REPO / 'src/AgentCore.Api/bin/Debug/net10.0/AgentCore.Api.dll')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='agent-core-ph-') as temporary:
        host = Host(args.assembly.resolve(), Path(temporary).resolve())
        try:
            print(json.dumps(exercise(host)))
        finally:
            host.stop()


if __name__ == '__main__':
    main()
