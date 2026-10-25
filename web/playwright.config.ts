import { defineConfig } from '@playwright/test';
import chat from './playwright.tenant-chat.config';

// Current canonical journeys only. Dedicated tenant configs retain the wider
// feature matrix; pre-IAM specs are historical evidence, not runnable contracts.
export default defineConfig({
  ...chat,
  reporter: process.env.CI ? [['github'], ['line'], ['html', { open: 'never' }]] : 'list',
  projects: [
    { name: 'synthetic', testMatch: [
      'text-conversation.spec.ts', 'tenant-approval-independence.spec.ts',
      'tenant-auth-recovery.spec.ts',
      'tenant-management-reads.spec.ts', 'tenant-management-mutations.spec.ts', 'tenant-management-inspections.spec.ts',
      'tenant-memory-management.spec.ts', 'memory-semantics.spec.ts',
      'tenant-automations.spec.ts', 'tenant-email-watches.spec.ts',
      'tenant-instance-editor.spec.ts', 'tenant-instance-controls.spec.ts',
      'admin-lifecycle.spec.ts',
    ] },
    { name: 'tenant-voice', testMatch: 'tenant-voice.spec.ts', timeout: 90_000,
      use: { permissions: ['microphone'], launchOptions: { args: [
        '--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream',
        '--autoplay-policy=no-user-gesture-required',
      ] } } },
  ],
});
