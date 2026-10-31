import { defineConfig } from '@playwright/test';
import canonical from './playwright.canonical.config';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// Extend the canonical disposable-host guard. This fixture never starts or reuses
// an unknown installation, and never obtains credentials from a private .env.
const results = process.env.EXPERIENCE_RUN_ROOT ??= fs.mkdtempSync(path.join(os.tmpdir(), 'agent-core-experience-results-'));
process.env.TENANT_CHAT_TEST_BASE_URL = process.env.CANONICAL_SYNTHETIC_URL;
export default defineConfig({
  ...canonical,
  testMatch: ['access-foundation.spec.ts', 'product-experience.spec.ts', 'managed-general-assistant.spec.ts', 'tenant-chat.spec.ts', 'tenant-model-management.spec.ts', 'tenant-approval-independence.spec.ts', 'tenant-definitions.spec.ts', 'task-delegation.spec.ts', 'session-goals.spec.ts', 'session-goals-stabilization.spec.ts', 'tenant-voice.spec.ts',
    'tenant-automations.spec.ts', 'tenant-agent-access.spec.ts', 'tenant-runtime-state.spec.ts', 'system-browser-privacy.spec.ts', 'tenant-instance-version.spec.ts', 'tenant-events.spec.ts', 'tenant-credentials.spec.ts', 'tenant-isolation.spec.ts', 'tenant-instance-resources.spec.ts', 'tenant-continuity.spec.ts', 'tenant-instance-skills.spec.ts', 'tenant-instance-controls.spec.ts', 'tenant-browser-profile.spec.ts', 'system-administration.spec.ts', 'system-operators.spec.ts'],
  timeout: 120_000,
  expect: { timeout: 15_000 },
  outputDir: results,
  use: { ...canonical.use, launchOptions: { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--autoplay-policy=no-user-gesture-required'] }, permissions: ['microphone'], actionTimeout: 15_000, viewport: { width: 1440, height: 900 }, screenshot: 'only-on-failure' },
});
