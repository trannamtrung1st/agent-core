import { defineConfig } from '@playwright/test';
import base from './playwright.tenant-chat.config';

export default defineConfig({ ...base, testMatch: ['decision-intelligence.spec.ts'], timeout: 90_000,
  webServer: (base.webServer as NonNullable<typeof base.webServer> & Array<unknown>).map(server => ({
    ...server, env: { ...server.env, DecisionIntelligence__Enabled: 'true', DecisionIntelligence__Backend: 'Synthetic', DecisionIntelligence__ShadowMode: 'false' }
  }))
});
