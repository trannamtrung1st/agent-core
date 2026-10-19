import { defineConfig } from '@playwright/test';
import base from './playwright.tenant-chat.config';
export default defineConfig({ ...base, testMatch: /tenant-agent-lifecycle\.spec\.ts|tenant-agent-access\.spec\.ts/,
  webServer: process.env.TENANT_UI_EXTERNAL === '1' ? undefined : base.webServer });
