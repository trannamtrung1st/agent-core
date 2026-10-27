import { defineConfig } from '@playwright/test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const webDir = path.dirname(fileURLToPath(import.meta.url));
const root = process.env.TASK_DELEGATION_ROOT ?? fs.mkdtempSync(path.join(os.tmpdir(), 'agent-core-task-delegation-'));
process.env.TASK_DELEGATION_ROOT = root;
const api = process.env.TASK_DELEGATION_API ?? 'http://127.0.0.1:5097';
const web = process.env.TASK_DELEGATION_WEB ?? 'http://127.0.0.1:5193';
export default defineConfig({ testDir: './e2e', testMatch: 'task-delegation.spec.ts', fullyParallel: false, workers: 1, retries: 0,
  timeout: 120_000, expect: { timeout: 20_000 }, reporter: 'list', outputDir: path.join(root, 'results'),
  use: { baseURL: web, trace: 'retain-on-failure' },
  webServer: process.env.TASK_DELEGATION_EXTERNAL === '1' ? undefined : [{
    command: 'node e2e/support/run-tenant-acceptance-host.mjs', cwd: webDir, url: `${api}/health`, reuseExistingServer: false, timeout: 120_000,
    env: { ASPNETCORE_ENVIRONMENT: 'Development', AgentCore__Profile: 'Synthetic', Persistence__Provider: 'Sqlite',
      Persistence__ConnectionString: `Data Source=${path.join(root, 'store.db')}`, Persistence__WorkspaceRoot: path.join(root, 'workspaces'),
      Persistence__AttachmentRoot: path.join(root, 'attachments'), Persistence__ArtifactRoot: path.join(root, 'artifacts'),
      Persistence__DefinitionResourceRoot: path.join(root, 'resources'), Persistence__CredentialProtectionKeyRoot: path.join(root, 'keys'),
      Browser__Enabled: 'false', OpenSandbox__Enabled: 'false', Providers__Speech__Recognition__Adapter: 'Synthetic', Providers__Speech__Synthesis__Adapter: 'Synthetic',
      Security__Http__AllowInsecureLocalHttp: 'true', Security__Http__AllowedOrigins__0: web,
      AGENTCORE_BOOTSTRAP_LOGIN: 'task-owner@example.test', AGENTCORE_BOOTSTRAP_PASSWORD: 'synthetic-task-password',
      AGENTCORE_BOOTSTRAP_DISPLAY_NAME: 'Task test owner', AGENTCORE_BOOTSTRAP_TENANT_NAME: 'Task test foundation', AGENTCORE_BOOTSTRAP_TENANT_SLUG: 'task-test-foundation',
      TENANT_ACCEPTANCE_API: api, AGENTCORE_LIVE_PROVIDER_TESTS: '0' }
  }, {
    command: `pnpm exec vite --host 127.0.0.1 --port ${new URL(web).port} --strictPort`, cwd: webDir, url: web, reuseExistingServer: false, timeout: 120_000,
    env: { VITE_API_PROXY_TARGET: api, VITE_DEV_PORT: new URL(web).port, VITE_CACHE_DIR: path.join(root, 'vite'), VITE_AGENTCORE_REALTIME_PROTOCOL: 'messagepack' }
  }] });
