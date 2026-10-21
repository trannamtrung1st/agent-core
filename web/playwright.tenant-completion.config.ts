import { defineConfig } from '@playwright/test';
import base from './playwright.tenant-chat.config';
export default defineConfig({ ...base, testMatch: 'tenant-automations.spec.ts',
  webServer: Array.isArray(base.webServer) ? base.webServer.map((server, index) => index === 0
    ? { ...server, command: server.command.replace('--no-restore', '--no-build --no-restore') } : server) : base.webServer,
});
