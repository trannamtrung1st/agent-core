import { test, expect, request as requests, type APIRequestContext } from '@playwright/test';

async function call<T>(api: APIRequestContext, path: string, body?: unknown) {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const result = await api.fetch(path, { method: body === undefined ? 'GET' : 'POST', headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(result.ok(), `${path}: ${result.status()} ${await result.text()}`).toBeTruthy(); return result.json() as Promise<T>;
}
test('request isolation completes the live Chat, retains history, and New chat admits a fresh independent Session', async ({ page }) => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); const errors: string[] = [], obsolete: string[] = [], consoleErrors: string[] = [], failures: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
  page.on('requestfailed', request => { if (!(request.method() === 'GET' && request.failure()?.errorText === 'net::ERR_ABORTED')) failures.push(request.url()); });
  page.on('request', request => { if (request.headers()['x-agentcore-owner-capability'] || /^\/api\/v[12]\/(sessions|admin|agent-instances|owner-capability)(\/|$)/.test(new URL(request.url()).pathname)) obsolete.push(request.url()); });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic');
    await call(api, '/api/v2/auth/login', { login: 'tenant-chat-owner@example.test', password: 'synthetic-tenant-chat-password' });
    const nonce = crypto.randomUUID().slice(0, 8), tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Isolated ${nonce}`, slug: `isolated-${nonce}` })).tenantId;
    const root = `/api/v2/tenants/${tenant}`, revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
    const agent = await call<{ principalId: string; instance: { instanceId: string } }>(api, root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() });
    const id = agent.instance.instanceId, isolation = root + `/agents/${id}/state-isolation`;
    const preview = await call<{ securityRevision: number; plan: { expected: unknown; draft: unknown; fingerprint: string } }>(api, isolation + '/presets/Stateless', {});
    await call(api, isolation + '/apply', { expectedRevision: preview.securityRevision, expected: preview.plan.expected, draft: preview.plan.draft, fingerprint: preview.plan.fingerprint });
    const role = await call<{ roleId: string }>(api, root + '/roles', { name: 'Isolated Chat executor', permissions: ['agents.chat'], expectedRevision: await revision() });
    await call(api, root + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    const first = await call<{ sessionId: string }>(api, root + '/sessions', { agentInstanceId: id, expectedInstanceRevision: 2, expectedRevision: await revision() });
    await page.setViewportSize({ width: 1440, height: 1000 }); await page.goto('/sign-in');
    await page.getByLabel('Email', { exact: true }).fill('tenant-chat-owner@example.test'); await page.getByLabel('Password', { exact: true }).fill('synthetic-tenant-chat-password');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
    await page.goto(`/t/${tenant}/c/${first.sessionId}`);
    const composer = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(composer).toBeEnabled(); await composer.fill('First isolated customer request'); await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(1); await expect(composer).toHaveCount(0);
    await expect.poll(async () => (await call<{ lifecycleStatus: string }>(api, root + `/sessions/${first.sessionId}`)).lifecycleStatus).toBe('completed');
    await page.reload(); await expect(page.getByText('First isolated customer request', { exact: true })).toHaveCount(1); await expect(composer).toHaveCount(0);
    await page.setViewportSize({ width: 390, height: 844 }); await page.getByRole('button', { name: 'Start another chat', exact: true }).click();
    await expect(composer).toBeEnabled(); await composer.fill('Second isolated customer request'); await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(1); await expect(composer).toHaveCount(0);
    await expect(page).not.toHaveURL(new RegExp(first.sessionId)); const second = page.url().split('/').pop()!;
    expect(second).toMatch(/^[0-9a-f-]{36}$/); await expect.poll(async () => (await call<{ lifecycleStatus: string }>(api, root + `/sessions/${second}`)).lifecycleStatus).toBe('completed');
    await expect(page.getByText('First isolated customer request', { exact: true })).toHaveCount(0);
    const history = await call<{ items: { role: string; text: string }[] }>(api, root + `/sessions/${first.sessionId}/messages`);
    expect(history.items.filter(item => item.role === 'user').map(item => item.text)).toEqual(['First isolated customer request']);
    expect(errors).toEqual([]); expect(obsolete).toEqual([]); expect(consoleErrors).toEqual([]); expect(failures).toEqual([]);
  } finally { await api.dispose(); }
});
