import { defineConfig } from '@playwright/test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
const root = path.resolve(import.meta.dirname, '..');
const data = fs.mkdtempSync(path.join(os.tmpdir(), 'agentcore-access-e2e-'));
const api = `http://127.0.0.1:${process.env.ACCESS_API_PORT ?? '5298'}`;
const web = `http://127.0.0.1:${process.env.ACCESS_WEB_PORT ?? '5398'}`;
export default defineConfig({
  testDir: './e2e', testMatch: 'access-foundation.spec.ts', workers: 1, retries: 0,
  timeout: 60_000, use: { baseURL: web, trace: 'retain-on-failure' },
  webServer: [
    { command: `dotnet run --project src/AgentCore.Api --no-build --no-launch-profile --urls ${api}`, cwd: root, url: `${api}/health`, timeout: 60_000, reuseExistingServer: false,
      env: { AgentCore__Profile: 'Synthetic', Persistence__Provider: 'InMemory', Persistence__WorkspaceRoot: path.join(data, 'workspace'), Persistence__ArtifactRoot: path.join(data, 'artifacts'), Persistence__AttachmentRoot: path.join(data, 'attachments'), Persistence__CredentialProtectionKeyRoot: path.join(data, 'keys'), Security__Http__AllowInsecureLocalHttp: 'true', Security__EphemeralBootstrap__Login: 'access-owner@example.test', Security__EphemeralBootstrap__Password: 'synthetic-access-test-password', Security__EphemeralBootstrap__DisplayName: 'Access test owner', Security__EphemeralBootstrap__TenantName: 'Access test organization', Security__EphemeralBootstrap__TenantSlug: 'access-test', Browser__Enabled: 'false', OpenSandbox__Enabled: 'false' } },
    { command: 'pnpm dev --host 127.0.0.1 --strictPort', cwd: import.meta.dirname, url: web, timeout: 60_000, reuseExistingServer: false, env: { VITE_DEV_PORT: process.env.ACCESS_WEB_PORT ?? '5398', VITE_API_PROXY_TARGET: api } }
  ]
});
