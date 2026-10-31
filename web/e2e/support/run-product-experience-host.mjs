import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';

// Explicit disposable storage; never select the developer's ordinary database.
const root = process.env.EXPERIENCE_DATA_ROOT;
if (!root || !path.isAbsolute(root)) throw new Error('Set EXPERIENCE_DATA_ROOT to an absolute disposable directory.');
if (fs.existsSync(path.join(root, 'store.db'))) throw new Error('Use a fresh disposable directory; this launcher never opens an existing database.');
fs.mkdirSync(root, { recursive: true });
const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', AgentCore__Profile: 'Synthetic',
  Persistence__Provider: 'Sqlite', Persistence__ConnectionString: `Data Source=${path.join(root, 'store.db')}`,
  Browser__Enabled: 'false', Browser__FixtureEnabled: 'false', OpenSandbox__Enabled: 'false',
  Browser__AllowUnmaskedCaptures: 'true', Browser__UnmaskedCaptureOrigins__0: 'https://example.test',
  Browser__TrustedVisualCaptureOrigins__0: 'https://graphics.test',
  Providers__Speech__Recognition__Adapter: 'Synthetic', Providers__Speech__Synthesis__Adapter: 'Synthetic',
  Security__Http__AllowInsecureLocalHttp: 'true', Security__Http__AllowedOrigins__0: 'http://127.0.0.1:5179',
  AGENTCORE_BOOTSTRAP_LOGIN: 'tenant-chat-owner@example.test', AGENTCORE_BOOTSTRAP_PASSWORD: 'synthetic-tenant-chat-password',
  AGENTCORE_BOOTSTRAP_DISPLAY_NAME: 'Chat test owner', AGENTCORE_BOOTSTRAP_TENANT_NAME: 'Experience studio',
  AGENTCORE_BOOTSTRAP_TENANT_SLUG: 'experience-studio', TENANT_ACCEPTANCE_API: 'http://127.0.0.1:5088', AGENTCORE_LIVE_PROVIDER_TESTS: '0' };
for (const [key, folder] of Object.entries({ WorkspaceRoot: 'workspaces', AttachmentRoot: 'attachments', ArtifactRoot: 'artifacts',
  DefinitionResourceRoot: 'resources', CredentialProtectionKeyRoot: 'keys' })) env[`Persistence__${key}`] = path.join(root, folder);
env.Browser__ProfileRoot = path.join(root, 'browser');
const child = spawn(process.execPath, [new URL('./run-tenant-acceptance-host.mjs', import.meta.url).pathname], { env, stdio: 'inherit' });
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
child.once('error', error => { console.error(error.message); process.exitCode = 1; });
child.once('exit', code => { process.exitCode = code ?? 1; });
