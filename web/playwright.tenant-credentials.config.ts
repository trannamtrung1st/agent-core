import { defineConfig } from '@playwright/test';
import access from './playwright.access.config';
export default defineConfig({ ...access, testMatch: 'tenant-credentials.spec.ts', timeout: 120_000, outputDir: '../local/test-results/tenant-credentials',
  webServer: process.env.TENANT_UI_EXTERNAL === '1' ? undefined : access.webServer,
  use: { ...access.use, baseURL: process.env.TENANT_UI_URL ?? access.use?.baseURL },
});
