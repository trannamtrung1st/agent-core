import { defineConfig } from '@playwright/test';
import base from './playwright.tenant-chat.config';
const servers = Array.isArray(base.webServer) ? base.webServer : base.webServer ? [base.webServer] : [];
export default defineConfig({ ...base, testMatch: 'system-browser-privacy.spec.ts',
  webServer: process.env.TENANT_UI_EXTERNAL === '1' ? undefined : servers.map((server, index) => index === 0 ? { ...server,
    env: { ...server.env, Browser__AllowUnmaskedCaptures: 'true', Browser__UnmaskedCaptureOrigins__0: 'https://example.test', Browser__TrustedVisualCaptureOrigins__0: 'https://graphics.test' } } : server) });
