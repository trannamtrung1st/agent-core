import { defineConfig } from '@playwright/test';
import chat from './playwright.tenant-chat.config';

export default defineConfig({
  ...chat,
  testMatch: 'tenant-voice.spec.ts',
  timeout: 90_000,
  use: { ...chat.use, launchOptions: { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--autoplay-policy=no-user-gesture-required'] }, permissions: ['microphone'] },
  webServer: (Array.isArray(chat.webServer) ? chat.webServer : [chat.webServer!]).map(server => ({
    ...server, command: server.command.replace('--no-restore', '--no-build --no-restore')
  }))
});
