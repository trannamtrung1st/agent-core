import { defineConfig } from '@playwright/test';

const web = process.env.SELFHOST_WEB_URL;
if (process.env.AGENTCORE_OPENSANDBOX_TESTS !== '1' || !web || !process.env.SELFHOST_WORKBENCH_IMAGE)
  throw new Error('Select a disposable Synthetic host, installed Workbench image and AGENTCORE_OPENSANDBOX_TESTS=1 explicitly.');
if (!/^sha256:[a-f0-9]{64}$/.test(process.env.SELFHOST_WORKBENCH_IMAGE))
  throw new Error('Select the complete immutable ID of an installed Workbench image.');
const origin = new URL(web);
if (origin.protocol !== 'http:' || !['localhost', '127.0.0.1'].includes(origin.hostname) || origin.username || origin.password)
  throw new Error('Native qualification requires an explicit disposable loopback app.');

export default defineConfig({
  testDir: './opensandbox-e2e', testMatch: 'tenant-workbench.spec.ts', workers: 1, retries: 0,
  timeout: 240_000, expect: { timeout: 30_000 }, reporter: 'list',
  outputDir: process.env.SELFHOST_RESULTS ?? 'test-results/selfhost',
  use: { baseURL: web, trace: 'retain-on-failure', actionTimeout: 30_000 },
});
