"""Disposable canonical cookie/CSRF clients and HTTPS Compose fixture; no live providers."""
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

REPO = Path(__file__).resolve().parents[1]


class Client:
    def __init__(self, base, context=None):
        self.base = base
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
            urllib.request.HTTPSHandler(context=context))

    def call(self, path, method='GET', value=None, expected=200, csrf=True, _read_retry=0,
             raw=None, content_type='application/json', binary=False):
        headers = {'Content-Type': content_type}
        if method not in ('GET', 'HEAD') and csrf:
            headers['X-AgentCore-CSRF'] = self.call('/api/v2/auth/csrf')['token']
        request = urllib.request.Request(self.base + path, method=method, headers=headers,
            data=raw if raw is not None else None if value is None else json.dumps(value).encode())
        try:
            response = self.opener.open(request, timeout=20)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body = response.read()
            # Never print response bodies: credentials, cookies and conversation content are private.
            if response.code != expected:
                reason = ''
                try:
                    reason = json.loads(body).get('reasonCode', '')
                except (ValueError, AttributeError):
                    pass
                if method == 'GET' and response.code == 409 and reason == 'stale_revision' and _read_retry < 2:
                    return self.call(path, expected=expected, _read_retry=_read_retry + 1, binary=binary)
                raise AssertionError(f'{method} {path}: expected {expected}, got {response.code} ({reason})')
            return body if binary else json.loads(body) if body and 'json' in response.headers.get('Content-Type', '') else None

    def login(self, login, password):
        return self.call('/api/v2/auth/login', 'POST', {'login': login, 'password': password})

    def revision(self, prefix):
        return self.call(prefix + '/access/me')['revision']


def eventually(read, predicate, label, seconds=60):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = read()
        if predicate(value):
            return value
        time.sleep(.5)
    raise AssertionError(f'Timed out waiting for {label}')


class ComposeFixture:
    def __init__(self, project, port, build=False):
        self.project, self.port, self.build = project, port, build
        self.login = 'smoke-' + uuid.uuid4().hex + '@example.test'
        self.password = secrets.token_urlsafe(32)
        self.env = dict(os.environ, COMPOSE_PROJECT_NAME=project, AGENTCORE_COMPOSE_PORT=str(port), AGENTCORE_DRIVE_ENABLED='true')
        self.compose = ['docker', 'compose', '--env-file', '/dev/null', '-f', str(REPO / 'docker-compose.yml'), '-f', str(REPO / 'docker-compose.https.yml')]
        self.started = False

    def run(self, *args):
        return subprocess.check_output(args, cwd=REPO, env=self.env, text=True, stderr=subprocess.STDOUT).strip()

    def command(self, *args):
        return self.run(*self.compose, *args)

    def __enter__(self):
        self.temp = tempfile.TemporaryDirectory(prefix='agent-core-compose-smoke-')
        try:
            root = Path(self.temp.name)
            cert, key, pfx = (root / name for name in ('cert.pem', 'key.pem', 'app.pfx'))
            tls_password = secrets.token_urlsafe(32)
            self.env.update(AGENTCORE_TLS_PASSWORD=tls_password, AGENTCORE_TLS_CERTIFICATE_PATH=str(pfx))
            self.run('openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-keyout', str(key), '-out', str(cert),
                '-subj', '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1')
            self.run('openssl', 'pkcs12', '-export', '-in', str(cert), '-inkey', str(key), '-out', str(pfx), '-passout', 'env:AGENTCORE_TLS_PASSWORD')
            # Private parent directory; the mounted file must be readable by the image's non-root UID.
            pfx.chmod(0o644)
            self.context = ssl.create_default_context(cafile=str(cert))
            self.base = f'https://127.0.0.1:{self.port}'
            self.client = Client(self.base, self.context)
            # Never reuse/reset a deployment or an earlier failed verification volume.
            volumes = self.run('docker', 'volume', 'ls', '-q', '--filter', f'label=com.docker.compose.project={self.project}')
            if volumes or self.command('ps', '-aq'):
                raise RuntimeError('Disposable Compose project already exists; select a fresh project name.')
            if self.build:
                self.command('build')
            for key, value in {'LOGIN': self.login, 'PASSWORD': self.password, 'DISPLAY_NAME': 'Smoke owner',
                    'TENANT_NAME': 'Smoke tenant', 'TENANT_SLUG': 'smoke-tenant'}.items():
                self.env['AGENTCORE_BOOTSTRAP_' + key] = value
            self.started = True
            bootstrap = ['run', '--rm', '--no-deps']
            for key in ('LOGIN', 'PASSWORD', 'DISPLAY_NAME', 'TENANT_NAME', 'TENANT_SLUG'):
                bootstrap += ['-e', 'AGENTCORE_BOOTSTRAP_' + key]
            self.command(*bootstrap, 'agent-core', 'bootstrap-identity')
            # Bootstrap secrets are not passed to the running app.
            for key in ('LOGIN', 'PASSWORD', 'DISPLAY_NAME', 'TENANT_NAME', 'TENANT_SLUG'):
                self.env.pop('AGENTCORE_BOOTSTRAP_' + key)
            self.command('up', '--no-build', '-d')
            self.ready()
            self.client.login(self.login, self.password)
            self.tenant = self.client.call('/api/v2/account/tenants')['items'][0]['tenantId']
            self.prefix = '/api/v2/tenants/' + self.tenant
            return self
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def ready(self):
        def healthy():
            try:
                return self.client.call('/health')
            except (OSError, AssertionError):
                return None
        eventually(healthy, lambda health: health and health['status'] == 'healthy' and health['profile'] == 'Synthetic', 'Synthetic HTTPS readiness', 120)

    def recreate(self):
        self.command('stop', '--timeout', '30')
        self.command('up', '--no-build', '--force-recreate', '-d')
        self.ready()
        # Persisted cookie keys and server sessions must both survive recreation.
        self.client.call('/api/v2/account/me')
        self.client.call(self.prefix + '/access/me')

    def watch_state(self, account):
        import sqlite3
        cid = self.command('ps', '-aq', 'agent-core')
        with tempfile.TemporaryDirectory(prefix='agent-core-watch-snapshot-') as target:
            self.run('docker', 'cp', f'{cid}:/data/.', target)
            with sqlite3.connect(Path(target) / 'agent-core.db') as db:
                state = json.loads(db.execute('SELECT StateJson FROM EmailWatches WHERE lower(TenantId)=lower(?) AND lower(AccountId)=lower(?)', (self.tenant, account)).fetchone()[0])
                events = db.execute('SELECT COUNT(*) FROM CoreEvents').fetchone()[0]
            return {key: state[key] for key in ('Enabled', 'ConfigurationRevision', 'Generation', 'Cursor', 'AuthorizationFingerprint')}, events

    def __exit__(self, *exc):
        try:
            if self.started:
                self.command('down', '--volumes', '--timeout', '30')
        finally:
            self.temp.cleanup()
