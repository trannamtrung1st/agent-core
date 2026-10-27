import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const r = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(r.ok(), `${method} ${path}: ${r.status()} ${await r.text()}`).toBeTruthy(); return r.status() === 204 ? undefined as T : r.json();
}
async function send(page: Page, value: string) { const box = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(box).toBeEnabled(); await box.fill(value); await page.getByRole('button', { name: 'Send', exact: true }).click(); }
async function approveInbox(page: Page, title: string) {
  await expect.poll(async () => { await page.getByRole('button', { name: 'Refresh inbox', exact: true }).click(); return page.getByRole('link', { name: title, exact: true }).first().isVisible(); }, { timeout: 20000 }).toBe(true);
  await page.getByRole('link', { name: title, exact: true }).first().click();
  const detail = page.getByRole('dialog', { name: 'Approval details', exact: true });
  await expect(detail.getByRole('button', { name: 'Approve', exact: true })).toBeEnabled();
  await detail.getByRole('button', { name: 'Approve', exact: true }).click();
  await page.getByRole('dialog', { name: 'Approve?', exact: true }).getByRole('button', { name: 'Approve', exact: true }).click();
  await detail.getByRole('button', { name: 'Close', exact: true }).click();
}
for (const viewport of [{ width: 1400, height: 950 }, { width: 390, height: 844 }]) test(`manual Experience review exact source confirmation background consent and result ${viewport.width}`, async ({ page }, info) => {
  test.setTimeout(150000); await page.setViewportSize(viewport); const errors: string[] = []; page.on('pageerror', e => errors.push(e.message)); page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) errors.push(r.url()); });
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password });
    const nonce = crypto.randomUUID().slice(0, 8); const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Experience ${nonce}`, slug: `experience-${nonce}` })).tenantId;
    const prefix = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
    const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, prefix + '/agents', { templateId: 'general-assistant', version: 35, expectedRevision: await revision() });
    const role = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Continuity executor', permissions: ['agents.chat', 'agents.read', 'sessions.read', 'agent-runs.read', 'memory.read', 'memory.manage'], expectedRevision: await revision() });
    await call(api, prefix + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    const isolation = prefix + '/agents/' + agent.instance.instanceId + '/state-isolation';
    const preview = await call<{ securityRevision: number; plan: { expected: unknown; draft: unknown; fingerprint: string } }>(api, isolation + '/preview', { settings: [], experienceEnabled: true, allowMaintenance: false });
    await call(api, isolation + '/apply', { expectedRevision: preview.securityRevision, ...preview.plan });
    await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible(); await page.goto(`/t/${tenant}`);
    await send(page, 'synthetic-inferred-frontend: one'); await expect(page.getByText('Observed a durable frontend preference.', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0, { timeout: 30000 });
    const sourceId = page.url().split('/').pop()!;
    await page.getByRole('button', { name: 'End', exact: true }).click(); await page.getByRole('dialog', { name: 'End this conversation?', exact: true }).getByRole('button', { name: 'End', exact: true }).click(); await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveCount(0);
    for (const permission of ['sessions.read', 'memory.manage']) await call(api, prefix + '/operation-policies/' + permission, { mode: 'always', independentReview: false, expectedRevision: await revision(), expectedPolicyRevision: 0 }, 'PUT');
    await page.goto(`/t/${tenant}/admin/agents/${agent.instance.instanceId}/memory`);
    await page.getByRole('button', { name: 'Review completed work', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Review completed work', exact: true });
    await dialog.getByRole('combobox', { name: 'Review source conversation', exact: true }).click();
    await expect(page.getByRole('option').first()).toHaveText(sourceId);
    await dialog.getByRole('combobox', { name: 'Review source conversation', exact: true }).press('ArrowDown');
    await dialog.getByRole('combobox', { name: 'Review source conversation', exact: true }).press('Enter'); await expect(dialog).toContainText(sourceId);
    await dialog.getByRole('button', { name: 'Inspect selected source', exact: true }).click();
    await expect(dialog.getByRole('button', { name: 'Queue review', exact: true })).toBeEnabled();
    await expect(page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden)')).toHaveCount(0);
    await page.screenshot({ path: info.outputPath('review-source-preview.png'), fullPage: true, animations: 'disabled' });
    await dialog.getByRole('button', { name: 'Queue review', exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: info.outputPath('review-source-actions.png'), fullPage: true, animations: 'disabled' });
    await dialog.getByRole('button', { name: 'Queue review', exact: true }).click();
    const confirm = page.getByRole('dialog', { name: 'Queue this review?', exact: true }); await expect(confirm).toContainText('grants no source-read consent or recording permission');
    await confirm.getByRole('button', { name: 'Queue review', exact: true }).click();
    await expect(dialog.getByText('Review queued', { exact: true })).toBeVisible(); await expect(confirm).toHaveCount(0);
    const runPath = await dialog.getByRole('link', { name: 'Inspect review work', exact: true }).getAttribute('href'); expect(runPath).toContain('?session=');
    await page.screenshot({ path: info.outputPath('review-admitted.png'), fullPage: true, animations: 'disabled' });
    await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
    await page.goto(`/t/${tenant}/admin/approvals`);
    try { await approveInbox(page, 'Inspect exact Experience source'); await approveInbox(page, 'Record exact Experience'); }
    catch (error) {
      const runs = await call<{ items: { runId: string; status: string; failureCode?: string; failureSummary?: string; outcomeText?: string }[] }>(api, prefix + '/agents/' + agent.instance.instanceId + '/activity/runs');
      const approvals = await call<{ items: { summary: string; status: string; operationResult?: unknown }[] }>(api, prefix + '/approvals?pageSize=10');
      throw new Error(String(error) + '\nManual review diagnostics: ' + JSON.stringify({ runs: runs.items.map(({ runId, status, failureCode, failureSummary, outcomeText }) => ({ runId, status, failureCode, failureSummary, outcomeText })), approvals: approvals.items.map(({ summary, status, operationResult }) => ({ summary, status, operationResult })) }));
    }
    await page.goto(runPath!); const run = page.getByRole('dialog', { name: 'Review completed work', exact: true });
    await expect(run).toContainText('The exact Experience observation was recorded with source provenance.', { timeout: 30000 });
    await expect(run).not.toContainText('selectedSource'); await expect(run).toContainText('Retain at most one');
    await page.screenshot({ path: info.outputPath('manual-review-result.png'), fullPage: true, animations: 'disabled' });
    await page.goto(`/t/${tenant}/admin/agents/${agent.instance.instanceId}/memory`);
    const retained = page.getByRole('region', { name: 'Retained Experience', exact: true }); await expect(retained.getByRole('cell', { name: 'Frontend evidence grouping', exact: true })).toBeVisible();
    await expect(retained).toContainText('You and this agent'); await retained.getByRole('button', { name: 'Expand row', exact: true }).click(); await expect(retained).toContainText(sourceId);
    await page.reload(); await expect(retained.getByRole('cell', { name: 'Frontend evidence grouping', exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy(); expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
