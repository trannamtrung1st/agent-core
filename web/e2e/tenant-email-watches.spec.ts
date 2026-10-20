import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

test('tenant account monitoring saves, queues a scan, guards edits and recovers a stale witness at five widths', async ({ page }) => {
  const failures: string[] = []; const legacy: string[] = [];
  page.on('pageerror', e => failures.push(e.message)); page.on('console', m => { if (m.type() === 'error' && !m.text().includes('409')) failures.push(m.text()); });
  page.on('request', r => { if (new URL(r.url()).pathname.startsWith('/api/v2/admin/')) legacy.push(r.url()); });
  page.on('requestfailed', r => { if (r.failure()?.errorText !== 'net::ERR_ABORTED') failures.push(r.url()); });
  await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill('tenant-chat-owner@example.test'); await page.getByLabel('Password', { exact: true }).fill('synthetic-tenant-chat-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
  const tenant = (await (await page.request.get('/api/v2/account/tenants')).json()).items[0].tenantId;
  const api = `/api/v2/tenants/${tenant}`; const csrf = (await (await page.request.get('/api/v2/auth/csrf')).json()).token;
  const revision = (await (await page.request.get(api + '/access/me')).json()).revision;
  const created = await page.request.post(api + '/credentials', { headers: { 'X-AgentCore-CSRF': csrf }, data: { displayName: 'Inbox monitoring qualification', kind: 'Password', purpose: 'EmailAccount', protectedValue: 'synthetic-fixture-only', expectedRevision: revision,
    emailAccount: { preset: 'Custom', emailAddress: 'fixture@example.test', imapUsername: 'fixture@example.test', smtpUsername: 'fixture@example.test', imapEndpoint: { host: 'mail.example.test', port: 993, tlsMode: 'ImplicitTls' }, smtpEndpoint: { host: 'mail.example.test', port: 465, tlsMode: 'ImplicitTls' } } } });
  expect(created.status()).toBe(201); const id = (await created.json()).credential.credentialId;
  await page.goto(`/t/${tenant}/integrations/credentials?credentialId=${id}`);
  const details = page.getByRole('dialog', { name: 'Credential details', exact: true }); const monitor = page.getByRole('region', { name: 'Email monitoring', exact: true });
  await expect(monitor.getByText('Monitoring Off', { exact: true })).toBeVisible();
  await monitor.getByRole('switch', { name: 'New inbox messages' }).click();
  await details.getByRole('button', { name: 'Close details', exact: true }).click();
  const discard = page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true }); await expect(discard).toBeVisible(); await discard.getByRole('button', { name: 'Keep editing', exact: true }).click(); await expect(discard).toBeHidden();
  await monitor.getByRole('button', { name: 'Save monitoring', exact: true }).click(); await expect(monitor.getByText('Monitoring settings saved.', { exact: true })).toBeVisible();
  await expect(monitor.getByRole('button', { name: 'Check now', exact: true })).toBeEnabled({ timeout: 20_000 }); await monitor.getByRole('button', { name: 'Check now', exact: true }).click();
  await expect(monitor.getByText('Check queued. It may trigger subscribed Automations.', { exact: true })).toBeVisible();
  await page.route(`**/credentials/${id}/monitoring`, async route => {
    if (route.request().method() === 'PUT') { await route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ reasonCode: 'stale_revision' }) }); await page.unroute(`**/credentials/${id}/monitoring`); }
    else await route.continue();
  });
  await monitor.getByRole('switch').click(); await monitor.getByRole('button', { name: 'Save monitoring', exact: true }).click();
  await expect(monitor.getByText('Reload monitoring before trying again', { exact: true })).toBeVisible(); await expect(monitor.getByRole('switch')).not.toBeChecked(); await expect(monitor.getByRole('button', { name: 'Save monitoring', exact: true })).toBeDisabled();
  await monitor.getByRole('button', { name: 'Reload monitoring', exact: true }).click(); await expect(monitor.getByRole('switch')).toBeChecked(); await expect(monitor.getByText('Reload monitoring before trying again', { exact: true })).toHaveCount(0);
  const shots = path.resolve('../.impeccable/review/tenant-email-watches'); fs.mkdirSync(shots, { recursive: true });
  for (const width of [1440, 1024, 768, 390, 320]) {
    await page.setViewportSize({ width, height: 900 }); await monitor.scrollIntoViewIfNeeded();
    await expect.poll(async () => { const b = await monitor.boundingBox(); return !!b && b.x >= 0 && b.x + b.width <= width + 1; }).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await monitor.screenshot({ path: path.join(shots, `monitor-${width}.png`), animations: 'disabled' });
  }
  expect(failures).toEqual([]); expect(legacy).toEqual([]);
});
