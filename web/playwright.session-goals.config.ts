import { defineConfig } from '@playwright/test';
export default defineConfig({ testDir: './e2e', testMatch: 'session-goals.spec.ts', fullyParallel: false, workers: 1, retries: 0,
  timeout: 90_000, expect: { timeout: 15_000 }, reporter: 'list', outputDir: '/tmp/agent-core-session-goals-browser-results',
  use: { baseURL: process.env.SESSION_GOALS_WEB_URL ?? 'http://127.0.0.1:5186', trace: 'retain-on-failure' } });
