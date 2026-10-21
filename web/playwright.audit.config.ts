import { defineConfig } from '@playwright/test';
import base from './playwright.tenant-chat.config';
const servers = Array.isArray(base.webServer) ? base.webServer : base.webServer ? [base.webServer] : [];
export default defineConfig({ ...base, testMatch: 'authorization-audit.spec.ts', timeout: 120_000,
  webServer: process.env.TENANT_UI_EXTERNAL === '1' ? undefined : servers.map((server, index) => index === 0
    ? { ...server, command: server.command.replace('--no-restore', '--no-build --no-restore') } : server) });
