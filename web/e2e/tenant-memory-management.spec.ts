import { canonicalFetch } from './support/tenant-auth';
import { expect, test, request as requests, type APIRequestContext } from '@playwright/test';
const login = 'tenant-chat-owner@example.test'; const password = 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const result = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(result.ok(), `${method} ${path}: ${result.status()} ${await result.text()}`).toBeTruthy(); return result.status() === 204 ? undefined as T : result.json();
}
for (const viewport of [{ width: 1400, height: 950 }, { width: 390, height: 844 }]) {
  test(`canonical User Profile and Memory management ${viewport.width}`, async ({ page }, testInfo) => {
    test.setTimeout(120000);
    await page.setViewportSize(viewport); const failures: string[] = []; const retired: string[] = [];
    page.on('pageerror', e => failures.push(e.message)); page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) failures.push(r.url()); });
    page.on('request', r => { if (r.headers()['x-agentcore-owner-capability'] || /^\/api\/v[12]\/(admin|owner-capability|sessions|agent-instances)(\/|$)/.test(new URL(r.url()).pathname)) retired.push(r.url()); });
    const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
    try {
      await call(api, '/api/v2/auth/login', { login, password });
      const nonce = crypto.randomUUID().slice(0, 8); const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Private Memory ${nonce}`, slug: `private-memory-${nonce}` })).tenantId;
      const prefix = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
      const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, prefix + '/agents', { templateId: 'general-assistant', version: 32, expectedRevision: await revision() });
      const role = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Memory executor', permissions: ['agents.chat', 'sessions.read', 'agent-runs.read', 'memory.read', 'memory.manage'], expectedRevision: await revision() });
      await call(api, prefix + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
      await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
      await page.goto(`/t/${tenant}/profile`); await expect(page.getByLabel('Preferred name', { exact: true })).toHaveValue('');
      await page.getByLabel('Preferred name', { exact: true }).fill('Organization-specific name'); await page.getByLabel('Language', { exact: true }).fill('en'); await page.getByLabel('Locale', { exact: true }).fill('en-GB'); await page.getByLabel('Time zone', { exact: true }).fill('Asia/Ho_Chi_Minh');
      await page.getByRole('button', { name: 'Save profile', exact: true }).click(); await expect(page.getByText('Profile saved', { exact: true })).toBeVisible(); await page.reload(); await expect(page.getByLabel('Preferred name', { exact: true })).toHaveValue('Organization-specific name');
      // Change the actual saved Profile after this page's read: the visible draft must survive a real CAS conflict.
      await page.getByLabel('Preferred name', { exact: true }).fill('Retained browser draft');
      const profile = await call<{ profile: { revision: number }; securityRevision: number }>(api, prefix + '/me/profile');
      await call(api, prefix + '/me/profile', { expectedRevision: profile.securityRevision, expectedProfileRevision: profile.profile.revision, values: { preferredName: 'Concurrent saved name' } }, 'PATCH');
      await page.getByRole('button', { name: 'Save profile', exact: true }).click(); await expect(page.getByText('Read current saved values before another save', { exact: true })).toBeVisible(); await expect(page.getByLabel('Preferred name', { exact: true })).toHaveValue('Retained browser draft');
      await page.getByRole('button', { name: 'Reload saved profile', exact: true }).click(); await expect(page.getByText('Saved: Concurrent saved name', { exact: true })).toBeVisible(); await expect(page.getByLabel('Preferred name', { exact: true })).toHaveValue('Retained browser draft');
      await page.getByRole('button', { name: 'Save profile', exact: true }).click(); await expect(page.getByText('Profile saved', { exact: true })).toBeVisible();
      await page.getByLabel('Language', { exact: true }).fill(''); await page.getByRole('button', { name: 'Save profile', exact: true }).click(); await expect(page.getByRole('button', { name: 'Save profile', exact: true })).toBeDisabled();
      await page.screenshot({ path: testInfo.outputPath('profile.png'), fullPage: true });
      await page.goto(`/t/${tenant}`); const box = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(box).toBeEnabled(); await box.fill('synthetic-memory-semantics: start-research'); await page.getByRole('button', { name: 'Send', exact: true }).click(); await expect(page.getByText('The research is pending review.', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0, { timeout: 30000 });
      const session = new URL(page.url()).pathname.split('/').pop()!; const memoryPath = `/t/${tenant}/admin/agents/${agent.instance.instanceId}/memory`;
      await page.goto(memoryPath); await expect(page.getByRole('cell', { name: 'Vietnam car prices research', exact: true })).toBeVisible();
      await page.getByRole('button', { name: 'Expand row', exact: true }).click(); await expect(page.getByText('Agent inferred', { exact: true })).toBeVisible();
      // A concurrent Profile save changes the shared security witness; this exact delete must refuse without replay.
      const latest = await call<typeof profile>(api, prefix + '/me/profile'); await call(api, prefix + '/me/profile', { expectedRevision: latest.securityRevision, expectedProfileRevision: latest.profile.revision, values: { preferredName: 'New saved witness' } }, 'PATCH');
      await page.getByRole('button', { name: 'Forget Vietnam car prices research', exact: true }).click(); await page.getByRole('button', { name: 'Forget item', exact: true }).click(); await expect(page.getByText('Memory needs a fresh read', { exact: true })).toBeVisible();
      await page.getByRole('button', { name: 'Reload Memory', exact: true }).click(); await expect(page.getByRole('cell', { name: 'Vietnam car prices research', exact: true })).toBeVisible();
      await page.getByRole('button', { name: 'Forget Vietnam car prices research', exact: true }).click(); await page.getByRole('button', { name: 'Keep Memory', exact: true }).click(); await expect(page.getByRole('cell', { name: 'Vietnam car prices research', exact: true })).toBeVisible();
      await page.getByRole('button', { name: 'Forget Vietnam car prices research', exact: true }).click(); await page.getByRole('button', { name: 'Forget item', exact: true }).click(); await expect(page.getByText('1 learned fact forgotten. Reload Memory to inspect current facts.', { exact: true })).toBeVisible();
      await page.getByRole('button', { name: 'Reload Memory', exact: true }).click(); await expect(page.getByRole('cell', { name: 'Forgotten fact', exact: true })).toBeVisible(); await page.reload(); await expect(page.getByRole('cell', { name: 'Forgotten fact', exact: true })).toBeVisible();
      await page.getByRole('combobox', { name: 'Memory scope', exact: true }).click(); await page.getByText('This Session', { exact: true }).click(); await page.getByRole('textbox', { name: 'Session ID', exact: true }).fill(session); await page.getByRole('button', { name: 'Load Session memory', exact: true }).click();
      await expect(page.getByRole('cell', { name: 'Vietnam car prices research', exact: true })).toBeVisible(); await page.getByRole('button', { name: 'Forget readable scope', exact: true }).click(); await page.getByRole('button', { name: 'Forget scope', exact: true }).click(); await expect(page.getByText('1 learned fact forgotten. Reload Memory to inspect current facts.', { exact: true })).toBeVisible(); await page.getByRole('button', { name: 'Reload Memory', exact: true }).click(); await expect(page.getByRole('cell', { name: 'Forgotten fact', exact: true })).toBeVisible();
      const history = await call<{ items: { text: string }[] }>(api, `${prefix}/sessions/${session}/messages?limit=100`); expect(history.items.some(i => i.text.includes('The research is pending review.'))).toBe(true);
      await page.screenshot({ path: testInfo.outputPath('memory.png'), fullPage: true });
      const other = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Other Memory ${nonce}`, slug: `other-memory-${nonce}` })).tenantId;
      await page.goto(`/t/${other}/profile`); await expect(page.getByLabel('Preferred name', { exact: true })).toHaveValue(''); await expect(page.getByLabel('Locale', { exact: true })).toHaveValue('');
      expect(failures).toEqual([]); expect(retired).toEqual([]);
    } finally { await api.dispose(); }
  });
}
