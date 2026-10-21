import { defineConfig } from '@playwright/test';
import base from './playwright.tenant-completion.config';
export default defineConfig({ ...base, timeout: 90_000 });
