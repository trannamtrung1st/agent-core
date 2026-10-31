import { canonicalFetch, prepareOrdinaryChat, signInTenant } from './support/tenant-auth';
import { openConversationTool } from './support/experience-navigation';
import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const r = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(r.ok(), `${method} ${path}: ${r.status()} ${await r.text()}`).toBeTruthy(); return r.status() === 204 ? undefined as T : r.json();
}
async function send(page: Page, value: string) { const box = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(box).toBeEnabled(); await box.fill(value); await page.getByRole('button', { name: 'Send', exact: true }).click(); }
async function detail(page: Page) {
  const panel = page.getByRole('region', { name: 'Conversation approvals', exact: true });
  await expect(panel.getByText('Waiting for review', { exact: true })).toBeVisible();
  await panel.getByRole('link', { name: 'Review details', exact: true }).first().click();
  const result = page.getByRole('dialog', { name: 'Approval details', exact: true });
  await expect(result.getByRole('region', { name: 'Exact saved read', exact: true })).toContainText('Search historical continuity'); return result;
}
for (const viewport of [{ width: 1400, height: 950 }, { width: 390, height: 844 }]) test(`historical source change refusal fresh exact consent and reload ${viewport.width}`, async ({ page }, info) => {
  test.setTimeout(150000); await page.setViewportSize(viewport); const errors: string[] = []; page.on('pageerror', e => errors.push(e.message)); page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) errors.push(r.url()); });
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password });
    const nonce = crypto.randomUUID().slice(0, 8); const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Continuity ${nonce}`, slug: `continuity-${nonce}` })).tenantId;
    const prefix = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
    const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, prefix + '/agents', { templateId: 'general-assistant', version: 36, expectedRevision: await revision() });
    const role = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Continuity executor', permissions: ['agents.chat', 'agents.read', 'sessions.read', 'agent-runs.read', 'memory.read', 'memory.manage'], expectedRevision: await revision() });
    await call(api, prefix + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    await prepareOrdinaryChat(api, tenant, agent.instance.instanceId);
    await signInTenant(page, login, password); await page.goto(`/t/${tenant}`);
    await send(page, 'synthetic-inferred-frontend: one'); await expect(page.getByText('Observed a durable frontend preference.', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0, { timeout: 30000 });
    const sourceId = page.url().split('/').pop()!;
    await openConversationTool(page, 'End'); await page.getByRole('dialog', { name: 'End this conversation?', exact: true }).getByRole('button', { name: 'End', exact: true }).click(); await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveCount(0);
    await call(api, prefix + '/operation-policies/sessions.read', { mode: 'always', independentReview: false, expectedRevision: await revision(), expectedPolicyRevision: 0 }, 'PUT');
    await page.goto(`/t/${tenant}`); await send(page, 'synthetic-tenant-continuity:inspect'); let review = await detail(page);
    await expect(review).toContainText('Current historical excerpts'); await expect(review.getByRole('button', { name: 'Approve', exact: true })).toBeEnabled();
    const source = await call<{ revision: number }>(api, prefix + '/sessions/' + sourceId);
    await call(api, prefix + '/sessions/' + sourceId, { title: 'Changed Frontend historical source', expectedRevision: await revision(), expectedSessionRevision: source.revision }, 'PATCH');
    await review.getByRole('button', { name: 'Refresh details', exact: true }).click(); await expect(review.getByRole('button', { name: 'Approve', exact: true })).toBeDisabled();
    await expect(review).toContainText('The saved source changed. No current bytes are substituted'); await expect(review.getByText('Current historical excerpts', { exact: true })).toHaveCount(0);
    await page.screenshot({ path: info.outputPath('changed-source.png'), fullPage: true });
    await review.getByRole('button', { name: 'Cancel request', exact: true }).click(); await page.getByRole('dialog', { name: 'Cancel request?', exact: true }).getByRole('button', { name: 'Cancel request', exact: true }).click(); await review.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.getByText('Historical read was not authorized.', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0, { timeout: 30000 });
    await send(page, 'synthetic-tenant-continuity:inspect'); review = await detail(page); await expect(review).toContainText('Changed Frontend historical source');
    await review.getByRole('button', { name: 'Approve', exact: true }).click(); const confirmation = page.getByRole('dialog', { name: 'Approve?', exact: true });
    await expect(confirmation).toContainText('Current raw source reads and reviewer access remain required'); await confirmation.getByRole('button', { name: 'Approve', exact: true }).click(); await review.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.getByText('Historical source inspected under current access.', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0, { timeout: 30000 });
    await page.reload(); await expect(page.getByText('Historical source inspected under current access.', { exact: true })).toBeVisible(); await page.screenshot({ path: info.outputPath('approved-history-reload.png'), fullPage: true }); expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
