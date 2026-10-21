import { defineConfig } from '@playwright/test';
import base from './playwright.tenant-chat.config';
export default defineConfig({ ...base, testMatch: 'tenant-file-reads.spec.ts', timeout: 120_000 });
