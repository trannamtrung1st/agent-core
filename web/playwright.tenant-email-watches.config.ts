import { defineConfig } from '@playwright/test';
import base from './playwright.tenant-chat.config';
const servers = Array.isArray(base.webServer) ? base.webServer : [];
export default defineConfig({ ...base, testMatch: 'tenant-email-watches.spec.ts',
  webServer: servers.map((server, index) => index === 0 ? { ...server,
    command: `dotnet src/AgentCore.Api/bin/Debug/net10.0/AgentCore.Api.dll --urls http://127.0.0.1:${process.env.TENANT_CHAT_API_PORT ?? '5088'}` } : server) });
