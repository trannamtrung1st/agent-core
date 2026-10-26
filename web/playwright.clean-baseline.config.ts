import { defineConfig } from '@playwright/test';
import base from './playwright.tenant-chat.config';

const external = process.env.AGENTCORE_BASELINE_BROWSER_URL;
if (external) process.env.TENANT_CHAT_TEST_BASE_URL = external;
export default defineConfig({
  ...base,
  testMatch: ['clean-baseline-files.spec.ts', 'chat-retention.spec.ts'],
  timeout: 120_000,
  ...(external ? { webServer: undefined, use: { ...base.use, baseURL: external } } : {}),
});
