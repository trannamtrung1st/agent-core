import { expect, test } from '@playwright/test';
import { signInTenant } from './support/tenant-auth';

test('a rate-limited sign-in recovers through a fresh form after the real limiter window', async ({ page }) => {
  test.setTimeout(150_000);
  expect((await (await page.request.get('/health')).json()).profile).toBe('Synthetic');
  const csrf = (await (await page.request.get('/api/v2/auth/csrf')).json()).token;
  let rejected = false;
  for (let attempt = 0; attempt < 11; attempt++) {
    const result = await page.request.post('/api/v2/auth/login', {
      headers: { 'X-AgentCore-CSRF': csrf },
      data: { login: 'absent-disposable-account@example.test', password: 'invalid-disposable-password' },
    });
    if (result.status() === 429) { rejected = true; break; }
    expect(result.status()).toBe(401);
  }
  expect(rejected).toBe(true);
  let refusals = 0;
  page.on('response', response => {
    if (new URL(response.url()).pathname === '/api/v2/auth/login' && response.status() === 429) refusals++;
  });
  await signInTenant(page, 'tenant-chat-owner@example.test', 'synthetic-tenant-chat-password');
  expect(refusals).toBe(1);
  await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
});
