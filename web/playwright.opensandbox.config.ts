import { defineConfig, devices } from '@playwright/test';

// The owned runner starts Core/Vite and performs real process recreation.
// This suite is separate from ordinary Synthetic tests and cannot silently skip.
if (process.env.AGENTCORE_OPENSANDBOX_TESTS !== '1' || !process.env.AGENTCORE_OSB_UI_STATE)
  throw new Error('Run through scripts/opensandbox-local.py accept --ui.');
export default defineConfig({
  testDir: './opensandbox-e2e', workers: 1, retries: 0, timeout: 180000,
  reporter: [['list'], ['html', { outputFolder: 'opensandbox-report', open: 'never' }]],
  outputDir: 'opensandbox-results',
  use: { ...devices['Desktop Chrome'], baseURL: process.env.AGENTCORE_OSB_UI_URL ?? 'http://127.0.0.1:6273',
    trace: 'retain-on-failure', permissions: ['clipboard-read', 'clipboard-write'] },
});
