import { openConversationTool } from './support/experience-navigation';
import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const r = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(r.ok(), `${method} ${path}: ${r.status()} ${await r.text()}`).toBeTruthy(); return r.status() === 204 ? undefined as T : r.json();
}
async function send(page: Page, value: string) { const box = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(box).toBeEnabled(); await box.fill(value); await page.getByRole('button', { name: 'Send', exact: true }).click(); }
async function reviewExperience(page: Page, title: string) {
  const panel = page.getByRole('region', { name: 'Conversation approvals', exact: true });
  await expect(panel.getByText(title, { exact: true }).first()).toBeVisible();
  await expect(panel.getByText('Waiting for review', { exact: true })).toBeVisible();
  await panel.getByRole('link', { name: 'Review details', exact: true }).first().click();
  const result = page.getByRole('dialog', { name: 'Approval details', exact: true });
  await expect(result.getByRole('button', { name: 'Approve', exact: true })).toBeEnabled(); return result;
}
async function approve(page: Page, review: ReturnType<Page['getByRole']>) {
  await review.getByRole('button', { name: 'Approve', exact: true }).click();
  await page.getByRole('dialog', { name: 'Approve?', exact: true }).getByRole('button', { name: 'Approve', exact: true }).click();
  await review.getByRole('button', { name: 'Close', exact: true }).click();
}
for (const viewport of [{ width: 1400, height: 950 }, { width: 390, height: 844 }]) test(`Experience exact source and record consent retained observation controls ${viewport.width}`, async ({ page }, info) => {
  test.setTimeout(150000); await page.setViewportSize(viewport); const errors: string[] = []; page.on('pageerror', e => errors.push(e.message)); page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) errors.push(r.url()); });
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password });
    const nonce = crypto.randomUUID().slice(0, 8); const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Experience ${nonce}`, slug: `experience-${nonce}` })).tenantId;
    const prefix = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
    const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, prefix + '/agents', { templateId: 'general-assistant', version: 36, expectedRevision: await revision() });
    const role = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Continuity executor', permissions: ['agents.chat', 'agents.read', 'sessions.read', 'agent-runs.read', 'memory.read', 'memory.manage'], expectedRevision: await revision() });
    await call(api, prefix + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    const isolation = prefix + '/agents/' + agent.instance.instanceId + '/state-isolation';
    const preview = await call<{ securityRevision: number; plan: { expected: unknown; draft: unknown; fingerprint: string } }>(api, isolation + '/preview', { settings: [], experienceEnabled: true, allowMaintenance: false });
    await call(api, isolation + '/apply', { expectedRevision: preview.securityRevision, ...preview.plan });
    await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible(); await page.goto(`/t/${tenant}`);
    await send(page, 'synthetic-inferred-frontend: one'); await expect(page.getByText('Observed a durable frontend preference.', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0, { timeout: 30000 });
    const sourceId = page.url().split('/').pop()!;
    await openConversationTool(page, 'End'); await page.getByRole('dialog', { name: 'End this conversation?', exact: true }).getByRole('button', { name: 'End', exact: true }).click(); await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveCount(0);
    for (const permission of ['sessions.read', 'memory.manage']) await call(api, prefix + '/operation-policies/' + permission, { mode: 'always', independentReview: false, expectedRevision: await revision(), expectedPolicyRevision: 0 }, 'PUT');
    await page.goto(`/t/${tenant}`); await send(page, 'synthetic-tenant-experience: record ' + sourceId);
    let review = await reviewExperience(page, 'Inspect exact Experience source');
    await expect(review).toContainText(sourceId); await approve(page, review);
    review = await reviewExperience(page, 'Record exact Experience'); await expect(review).toContainText('Frontend evidence grouping'); await approve(page, review);
    await expect(page.getByText('The exact Experience observation was recorded with source provenance.', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0, { timeout: 30000 });
    await page.reload(); await expect(page.getByText('The exact Experience observation was recorded with source provenance.', { exact: true })).toBeVisible();
    await page.goto(`/t/${tenant}/admin/agents/${agent.instance.instanceId}/memory`);
    const memory = page.getByRole('region', { name: 'Your learned Memory', exact: true });
    await memory.getByRole('button', { name: 'Expand row', exact: true }).click(); await expect(memory.getByText('Content', { exact: true })).toBeVisible();
    expect(await memory.locator('.admin-expanded-record').evaluate(element => element.getBoundingClientRect().width <= element.closest('.ant-table-wrapper')!.getBoundingClientRect().width)).toBeTruthy();
    await memory.getByRole('button', { name: 'Collapse row', exact: true }).click();
    const retained = page.getByRole('region', { name: 'Retained Experience', exact: true });
    await expect(retained.getByText('Frontend evidence grouping', { exact: true })).toBeVisible();
    await retained.getByRole('button', { name: 'Expand row', exact: true }).click(); await expect(retained).toContainText(sourceId);
    await expect(retained).toContainText('Inspect actual source provenance before recording Experience.');
    await page.evaluate(() => { document.querySelectorAll('*').forEach(element => { if (element instanceof HTMLElement && element.scrollTop) element.scrollTop = 0; }); scrollTo(0, 0); });
    await page.screenshot({ path: info.outputPath('continuity-top.png'), fullPage: true });
    await retained.getByText('Retained Experience', { exact: true }).evaluate(element => element.scrollIntoView({ block: 'start' }));
    await page.screenshot({ path: info.outputPath('retained-experience.png'), fullPage: true });
    await retained.getByText('Inspect actual source provenance before recording Experience.', { exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: info.outputPath('experience-source-detail.png'), fullPage: true });
    await retained.getByRole('button', { name: 'Suppress Frontend evidence grouping', exact: true }).click();
    await page.getByRole('dialog', { name: 'Suppress observation?', exact: true }).getByRole('button', { name: 'Suppress observation', exact: true }).click();
    await retained.getByRole('button', { name: 'Reload Experience', exact: true }).click(); await expect(retained.getByText('Suppressed', { exact: true })).toBeVisible();
    await retained.getByRole('button', { name: 'Restore Frontend evidence grouping', exact: true }).click();
    await page.getByRole('dialog', { name: 'Restore to recall?', exact: true }).getByRole('button', { name: 'Restore to recall', exact: true }).click();
    await retained.getByRole('button', { name: 'Reload Experience', exact: true }).click(); await expect(retained.getByText('Eligible', { exact: true })).toBeVisible();
    await retained.getByRole('button', { name: 'Delete Frontend evidence grouping', exact: true }).click();
    await page.getByRole('dialog', { name: 'Delete observation?', exact: true }).getByRole('button', { name: 'Delete observation', exact: true }).click();
    await retained.getByRole('button', { name: 'Reload Experience', exact: true }).click(); await expect(retained.getByText('Deleted', { exact: true })).toBeVisible();
    await page.reload(); await expect(retained.getByText('Deleted', { exact: true })).toBeVisible(); await expect(retained).not.toContainText('Inspect actual source provenance');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
    await retained.getByText('Retained Experience', { exact: true }).evaluate(element => element.scrollIntoView({ block: 'start' })); await page.screenshot({ path: info.outputPath('experience-tombstone-reload.png'), fullPage: true }); expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
