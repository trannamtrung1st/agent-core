import { defineConfig, devices } from '@playwright/test';

// Run against an explicitly selected disposable Synthetic host, never a user's app.
if (!process.env.CANONICAL_SYNTHETIC_URL || !process.env.CANONICAL_SYNTHETIC_API_URL)
  throw new Error('Set CANONICAL_SYNTHETIC_URL and CANONICAL_SYNTHETIC_API_URL to the isolated Synthetic hosts.');
export default defineConfig({
  globalSetup: './e2e/support/canonical-host.ts',
  testDir: './e2e',
  testMatch: /(?:agent-workspace-v2|agent-workspace|workspace-filesystem|unified-workspace-project)\.spec\.ts/,
  workers: 1, retries: 0, reporter: 'list',
  use: { ...devices['Desktop Chrome'], baseURL: process.env.CANONICAL_SYNTHETIC_URL, trace: 'retain-on-failure' },
});
