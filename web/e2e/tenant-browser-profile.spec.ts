import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';

const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const reply = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(reply.ok(), `${method} ${path}: ${reply.status()}`).toBeTruthy(); return reply.status() === 204 ? undefined as T : reply.json();
}
async function signIn(page: Page) { await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible(); }
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Browser state for regional operations ${nonce}`, slug: `browser-reset-${nonce}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
  const agent = await call<{ instance: { instanceId: string } }>(api, root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() });
  const id = agent.instance.instanceId; return { api, tenant, root, id, revision, profile: root + `/agents/${id}/browser/profile`, path: `/t/${tenant}/admin/agents/${id}/connections` };
}
test.beforeAll(async () => { const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); try { expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState(); } finally { await api.dispose(); } });

test('named confirmation, real reset, lost reply readback and no automatic physical replay at desktop and mobile', async ({ page }) => {
  const f = await fixture(); const errors: string[] = [], legacy: string[] = [], operations: string[] = [];
  page.on('pageerror', error => errors.push(error.message)); page.on('request', request => {
    const path = new URL(request.url()).pathname; if (/^\/api\/v[12]\/(owner-capability|agent-instances|admin)(\/|$)/.test(path)) legacy.push(path);
    if (path === f.profile + '/reset') operations.push(request.postDataJSON().operationId);
  });
  try {
    await signIn(page); await page.setViewportSize({ width: 1440, height: 1000 }); await page.goto(f.path);
    const panel = page.getByRole('region', { name: 'Browser state', exact: true }); await expect(panel.getByText('No saved Browser profile', { exact: true })).toBeVisible();
    await panel.getByRole('button', { name: 'Reset Browser profile', exact: true }).click(); let confirm = page.getByRole('dialog', { name: 'Reset this Browser profile?', exact: true });
    await expect(confirm).toContainText('Agent: Alex'); await expect(confirm).toContainText(`Organization: Browser state for regional operations`); await expect(confirm).toContainText('all persistent Browser views');
    expect(operations).toHaveLength(0); await page.screenshot({ path: test.info().outputPath('desktop-confirmation.png') });
    await confirm.getByRole('button', { name: 'Cancel', exact: true }).click(); await expect(panel.getByRole('button', { name: 'Reset Browser profile', exact: true })).toBeFocused();
    await panel.getByRole('button', { name: 'Reset Browser profile', exact: true }).click(); confirm = page.getByRole('dialog', { name: 'Reset this Browser profile?', exact: true });
    await confirm.getByRole('button', { name: 'Reset Browser profile', exact: true }).click(); await expect(panel.getByText('Profile reset confirmed', { exact: true })).toBeVisible();
    await expect(panel.getByRole('button', { name: 'Reload Browser state', exact: true })).toBeFocused(); expect(operations).toHaveLength(1);
    await panel.getByRole('button', { name: 'Reload Browser state', exact: true }).click(); await expect(panel.getByText('Last reset confirmed', { exact: true })).toBeVisible(); expect(operations).toHaveLength(1);
    let replies = 0; await page.route('**' + f.profile + '/reset', async route => { const actual = await route.fetch(); expect(actual.ok()).toBeTruthy(); replies++; await route.fulfill({ response: actual, body: 'unreadable reply' }); });
    await page.setViewportSize({ width: 390, height: 844 }); await panel.getByRole('button', { name: 'Reset Browser profile', exact: true }).click();
    await page.getByRole('dialog', { name: 'Reset this Browser profile?', exact: true }).getByRole('button', { name: 'Reset Browser profile', exact: true }).click();
    await expect(panel.getByText('Reset outcome not confirmed', { exact: true })).toBeVisible(); expect(operations).toHaveLength(2); expect(replies).toBe(1);
    await page.screenshot({ path: test.info().outputPath('mobile-lost-reply.png'), fullPage: true });
    await panel.getByRole('button', { name: 'Reload Browser state', exact: true }).click(); await expect(panel.getByText('Last reset confirmed', { exact: true })).toBeVisible(); expect(operations).toHaveLength(2); expect(replies).toBe(1);
    const saved = await call<{ lastReset: { operationId: string; status: string } }>(f.api, f.profile); expect(saved.lastReset.operationId).toBe(operations[1]); expect(saved.lastReset.status).toBe('reset');
    expect(operations[0]).not.toBe(operations[1]); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(errors).toEqual([]); expect(legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('stale preflight requires fresh observation and archived profile stays readable without reset at 320px', async ({ page }) => {
  const f = await fixture(); let posts = 0; page.on('request', r => { if (new URL(r.url()).pathname === f.profile + '/reset') posts++; });
  try {
    await signIn(page); await page.setViewportSize({ width: 320, height: 740 }); await page.goto(f.path); const panel = page.getByRole('region', { name: 'Browser state', exact: true });
    await panel.getByRole('button', { name: 'Reset Browser profile', exact: true }).click();
    await call(f.api, f.root + '/roles', { name: 'Concurrent Browser permission change', permissions: ['browser.read'], expectedRevision: await f.revision() });
    await page.getByRole('dialog', { name: 'Reset this Browser profile?', exact: true }).getByRole('button', { name: 'Reset Browser profile', exact: true }).click();
    await expect(panel.getByText('Reset was refused', { exact: true })).toBeVisible(); expect(posts).toBe(1);
    expect((await call<{ lastReset: unknown }>(f.api, f.profile)).lastReset).toBeNull();
    await panel.getByRole('button', { name: 'Reload Browser state', exact: true }).click(); await expect(panel.getByRole('button', { name: 'Reset Browser profile', exact: true })).toBeEnabled(); expect(posts).toBe(1);
    await panel.getByRole('button', { name: 'Reset Browser profile', exact: true }).click(); const dialog = page.getByRole('dialog', { name: 'Reset this Browser profile?', exact: true });
    await page.screenshot({ path: test.info().outputPath('narrow-confirmation.png') }); await dialog.getByRole('button', { name: 'Reset Browser profile', exact: true }).click(); await expect(panel.getByText('Profile reset confirmed', { exact: true })).toBeVisible();
    const saved = await call<{ instanceRevision: number }>(f.api, f.profile); await call(f.api, f.root + `/agents/${f.id}/lifecycle`, { lifecycle: 1, expectedRevision: await f.revision(), expectedInstanceRevision: saved.instanceRevision }, 'PUT');
    await panel.getByRole('button', { name: 'Reload Browser state', exact: true }).click(); await expect(panel.getByText('Restore this instance before resetting its Browser profile.', { exact: true })).toBeVisible();
    await expect(panel.getByRole('button', { name: 'Reset Browser profile', exact: true })).toBeDisabled(); expect(posts).toBe(2);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  } finally { await f.api.dispose(); }
});
