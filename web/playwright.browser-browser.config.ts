import { defineConfig } from '@playwright/test';
import chat from './playwright.tenant-chat.config';

export default defineConfig({
  ...chat, testMatch: 'browser-browser.spec.ts', timeout: 90_000,
  webServer: (Array.isArray(chat.webServer) ? chat.webServer : [chat.webServer!]).map((server, index) => index === 0 ? {
    ...server, env: { ...server.env, Providers__Speech__Recognition__Adapter: 'Browser', Providers__Speech__Synthesis__Adapter: 'Browser' },
  } : server),
});
