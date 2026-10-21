import { expect, test, request as requests, type APIRequestContext } from '@playwright/test';

const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy();
  return response.status() === 204 ? undefined as T : response.json();
}
test.beforeAll(async () => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic');
    await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState();
  } finally { await api.dispose(); }
});

for (const viewport of [{ width: 1400, height: 950 }, { width: 390, height: 844 }]) {
  test(`tenant memory closes research, retains its report and requires exact forgetting approval at ${viewport.width}px`, async ({ page }) => {
    test.setTimeout(120_000); await page.setViewportSize(viewport);
    const failures: string[] = [], retired: string[] = [];
    page.on('pageerror', error => failures.push(error.message));
    page.on('requestfailed', request => { if (!request.failure()?.errorText.includes('ERR_ABORTED')) failures.push(request.url()); });
    page.on('request', request => {
      if (request.headers()['x-agentcore-owner-capability'] || /^\/api\/v[12]\/(owner-capability|admin|sessions|agent-instances)(\/|$)/.test(new URL(request.url()).pathname)) retired.push(request.url());
    });
    const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity });
    try {
      const nonce = crypto.randomUUID().slice(0, 8);
      const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Memory ${nonce}`, slug: `memory-${nonce}` })).tenantId;
      const prefix = `/api/v2/tenants/${tenant}`;
      const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
      const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, prefix + '/agents', { templateId: 'general-assistant', version: 32, expectedRevision: await revision() });
      const role = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Memory executor', permissions: ['agents.chat', 'sessions.read', 'agent-runs.read', 'memory.read', 'memory.manage'], expectedRevision: await revision() });
      await call(api, prefix + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
      const report = '# Vietnam car prices\nResearch retained after closing work.\n';
      const reportUrl = `${prefix}/agents/${agent.instance.instanceId}/workspace/content?path=%2Fhome%2Fcar-prices.md`;
      const csrf = await (await api.get('/api/v2/auth/csrf')).json();
      expect((await api.put(reportUrl + `&expectedRevision=${await revision()}`, { headers: { 'X-AgentCore-CSRF': csrf.token, 'Content-Type': 'text/plain' }, data: report })).ok()).toBe(true);
      await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password);
      await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
      await page.goto(`/t/${tenant}`);
      async function send(text: string, expected: string) {
        const assistants = page.locator('.chat-message-assistant'); const count = await assistants.count();
        const box = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(box).toBeEnabled(); await box.fill(text);
        await page.getByRole('button', { name: 'Send', exact: true }).click();
        await expect(assistants).toHaveCount(count + 1); await expect(assistants.last()).toContainText(expected);
        await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0);
      }
      await send('synthetic-memory-semantics: start-research', 'The research is pending review.');
      await send('synthetic-memory-semantics: propose-delete', 'Not forgotten: Vietnam car prices research. Forgetting requires approval.');
      await send('synthetic-memory-semantics: explain-receipt', 'Core rejected a delete attempt; no memory was forgotten.');
      await send('Close the Vietnam car prices research work; keep the report.', '✓ Closed');
      const session = new URL(page.url()).pathname.split('/').pop()!;
      const history = await call<{ items: { role: string; memoryReceipts?: { outcome: string; scopes?: string[] }[] }[] }>(api, `${prefix}/sessions/${session}/messages?limit=100`);
      const closed = history.items.flatMap(i => i.memoryReceipts ?? []).filter(r => r.outcome === 'resolved'); expect(closed).toHaveLength(1);
      expect(closed[0].scopes).toEqual(['session', 'identityUser']);
      expect(await (await api.get(reportUrl)).text()).toBe(report);
      await page.reload(); await expect(page.getByText('✓ Closed', { exact: true })).toBeVisible();
      await send('Close the Vietnam car prices research work; keep the report.', '✓ Closed');
      const again = await call<typeof history>(api, `${prefix}/sessions/${session}/messages?limit=100`);
      expect(again.items.flatMap(i => i.memoryReceipts ?? []).filter(r => r.outcome === 'alreadyResolved')).toHaveLength(1);
      await send('synthetic-memory-semantics: close-missing', 'No matching open loop found: Missing research.');
      expect(await (await api.get(reportUrl)).text()).toBe(report);
      expect(failures).toEqual([]); expect(retired).toEqual([]);
    } finally { await api.dispose(); }
  });
}
