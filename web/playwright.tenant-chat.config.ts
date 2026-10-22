import { defineConfig } from '@playwright/test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const webDir = path.dirname(fileURLToPath(import.meta.url));
const repo = path.dirname(webDir);
const root = process.env.TENANT_CHAT_TEST_RUN_ROOT ?? fs.mkdtempSync(path.join(os.tmpdir(), 'agent-core-tenant-chat-e2e-'));
process.env.TENANT_CHAT_TEST_RUN_ROOT = root;
const api = `http://127.0.0.1:${process.env.TENANT_CHAT_API_PORT ?? '5088'}`;
const web = `http://127.0.0.1:${process.env.TENANT_CHAT_WEB_PORT ?? '5179'}`;
process.env.TENANT_CHAT_TEST_BASE_URL = web;

export default defineConfig({
  testDir: './e2e', testMatch: ['tenant-chat.spec.ts', 'memory-semantics.spec.ts', 'tenant-memory-management.spec.ts', 'tenant-management-mutations.spec.ts', 'tenant-management-inspections.spec.ts'], fullyParallel: false, workers: 1, retries: 0,
  timeout: 60_000, expect: { timeout: 15_000 }, reporter: 'list',
  outputDir: path.join(root, 'results'), use: { baseURL: web, trace: 'retain-on-failure' },
  webServer: [{
    command: `dotnet run --project src/AgentCore.Api --no-restore --no-launch-profile --urls ${api}`, cwd: repo,
    url: `${api}/health`, reuseExistingServer: false, timeout: 120_000,
    env: { ASPNETCORE_ENVIRONMENT: 'Development', AgentCore__Profile: 'Synthetic', Persistence__Provider: 'InMemory',
      Persistence__WorkspaceRoot: path.join(root, 'workspaces'), Persistence__AttachmentRoot: path.join(root, 'attachments'),
      Persistence__ArtifactRoot: path.join(root, 'artifacts'), Persistence__DefinitionResourceRoot: path.join(root, 'resources'),
      Persistence__CredentialProtectionKeyRoot: path.join(root, 'keys'), Browser__ProfileRoot: path.join(root, 'browser'),
      Browser__Enabled: 'false', Browser__FixtureEnabled: 'false', OpenSandbox__Enabled: 'false',
      Providers__Speech__Recognition__Adapter: 'Synthetic', Providers__Speech__Synthesis__Adapter: 'Synthetic',
      Security__Http__AllowInsecureLocalHttp: 'true', Security__Http__AllowedOrigins__0: web,
      Security__EphemeralBootstrap__Login: 'tenant-chat-owner@example.test', Security__EphemeralBootstrap__Password: 'synthetic-tenant-chat-password',
      Security__EphemeralBootstrap__DisplayName: 'Chat test owner', Security__EphemeralBootstrap__TenantName: 'Chat test foundation', Security__EphemeralBootstrap__TenantSlug: 'chat-test-foundation',
      AGENTCORE_LIVE_PROVIDER_TESTS: '0' }
  }, {
    command: `pnpm exec vite --host 127.0.0.1 --port ${new URL(web).port} --strictPort`, cwd: webDir,
    url: web, reuseExistingServer: false, timeout: 120_000,
    env: { VITE_API_PROXY_TARGET: api, VITE_DEV_PORT: new URL(web).port, VITE_CACHE_DIR: path.join(root, 'vite') }
  }]
});
