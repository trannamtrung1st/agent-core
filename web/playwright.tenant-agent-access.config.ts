import { defineConfig } from '@playwright/test';
import tenantChat from './playwright.tenant-chat.config';

export default defineConfig({ ...tenantChat, testMatch: 'tenant-agent-access.spec.ts' });
