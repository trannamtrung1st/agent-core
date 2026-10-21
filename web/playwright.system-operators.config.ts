import { defineConfig } from '@playwright/test';
import base from './playwright.audit.config';
export default defineConfig({ ...base, testMatch: 'system-operators.spec.ts', timeout: 180_000 });
