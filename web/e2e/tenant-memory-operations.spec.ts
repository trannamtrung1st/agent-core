import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const r = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(r.ok(), `${method} ${path}: ${r.status()} ${await r.text()}`).toBeTruthy(); return r.status() === 204 ? undefined as T : r.json();
}
async function send(page: Page, value: string) { const box = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(box).toBeEnabled(); await box.fill(value); await page.getByRole('button', { name: 'Send', exact: true }).click(); }
async function approve(page: Page, title: string, grant = false) {
  const panel = page.getByRole('region', { name: 'Conversation approvals', exact: true });
  await expect(panel.getByText('Waiting for review', { exact: true })).toBeVisible();
  await panel.getByRole('link', { name: 'Review details', exact: true }).first().click(); const detail = page.getByRole('dialog', { name: 'Approval details', exact: true });
  await expect(detail.getByRole('region', { name: title === 'Search learned Memory' ? 'Exact saved read' : 'Exact saved changes', exact: true })).toContainText(title);
  const action = grant ? 'Authorize once' : 'Approve'; await detail.getByRole('button', { name: action, exact: true }).click();
  await page.getByRole('dialog', { name: action + '?', exact: true }).getByRole('button', { name: action, exact: true }).click();
  await detail.getByRole('button', { name: 'Close', exact: true }).click();
}
for (const viewport of [{ width: 1400, height: 950 }, { width: 390, height: 844 }]) test(`protected Memory read consolidate lineage and exact forget ${viewport.width}`, async ({ page }, info) => {
  test.setTimeout(150000); await page.setViewportSize(viewport); const errors: string[] = []; page.on('pageerror', e => errors.push(e.message)); page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) errors.push(r.url()); });
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password });
    const nonce = crypto.randomUUID().slice(0, 8); const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Memory consent ${nonce}`, slug: `memory-consent-${nonce}` })).tenantId;
    const prefix = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
    const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, prefix + '/agents', { templateId: 'general-assistant', version: 32, expectedRevision: await revision() });
    const rights = ['agents.chat', 'agents.read', 'sessions.read', 'agent-runs.read', 'memory.read', 'memory.manage'];
    const role = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Memory consent executor', permissions: rights, expectedRevision: await revision() });
    await call(api, prefix + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible(); await page.goto(`/t/${tenant}`);
    for (const subject of ['one', 'two']) { await send(page, 'synthetic-inferred-frontend: ' + subject); await expect(page.getByText('Observed a durable frontend preference.', { exact: true })).toHaveCount(subject === 'one' ? 1 : 2); await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0, { timeout: 30000 }); }
    const chat = page.url(); await call(api, prefix + '/operation-policies/memory.read', { mode: 'always', independentReview: false, expectedRevision: await revision(), expectedPolicyRevision: 0 }, 'PUT');
    await send(page, 'synthetic-tenant-memory:consolidate'); await approve(page, 'Search learned Memory'); await approve(page, 'Consolidate learned Memory');
    await expect(page.getByText('The exact learned Memories were consolidated with source lineage.', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0, { timeout: 30000 });
    const memoryPath = `/t/${tenant}/admin/agents/${agent.instance.instanceId}/memory`; await page.goto(memoryPath); await expect(page.getByRole('cell', { name: 'Frontend examples', exact: true })).toBeVisible();
    const result = page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Frontend examples', exact: true }) }); await result.getByRole('button', { name: 'Expand row', exact: true }).click(); await page.getByRole('button', { name: 'View sources', exact: true }).click();
    await expect(page.getByText('Source status', { exact: true })).toHaveCount(2); await expect(page.getByText('Prefer TypeScript for frontend examples.', { exact: true })).toHaveCount(3); await page.screenshot({ path: info.outputPath('memory-lineage.png'), fullPage: true });
    await call(api, prefix + '/roles/' + role.roleId, { name: 'Memory consent executor', permissions: rights.filter(p => p !== 'memory.manage'), expectedRevision: await revision() }, 'PUT');
    await page.goto(chat); await send(page, 'synthetic-tenant-memory:forget'); await approve(page, 'Search learned Memory'); await approve(page, 'Forget learned Memory', true);
    await expect(page.getByText('The exact learned Memory was forgotten. Other retained data remains.', { exact: true })).toBeVisible(); await page.goto(memoryPath); await expect(page.getByRole('cell', { name: 'Forgotten fact', exact: true })).toBeVisible();
    const forgotten = page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Forgotten fact', exact: true }) }); await forgotten.getByRole('button', { name: 'Expand row', exact: true }).click(); await page.getByRole('button', { name: 'View sources', exact: true }).click(); await expect(page.getByText('Source status', { exact: true })).toHaveCount(2); await page.screenshot({ path: info.outputPath('forgotten-lineage.png'), fullPage: true }); expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
