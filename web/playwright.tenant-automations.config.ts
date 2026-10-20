import { defineConfig } from '@playwright/test';
import base from './playwright.tenant-chat.config';
export default defineConfig({ ...base, testMatch: 'tenant-automations.spec.ts' });
