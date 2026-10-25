import { expect, test, request as requests, type APIRequestContext } from '@playwright/test';
const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const r = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(r.ok(), `${method} ${path}: ${r.status()} ${await r.text()}`).toBeTruthy(); return r.status() === 204 ? undefined as T : r.json();
}
for (const width of [1440, 390]) for (const decision of ['approve', 'reject']) test(`management read consent ${decision} ${width}`, async ({ page }, info) => {
  test.setTimeout(120000); await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 }); const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message)); page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) errors.push(r.url()); });
  page.on('response', r => { if (r.status() >= 500) errors.push(`${r.status()} ${r.url()}`); });
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password });
    const nonce = crypto.randomUUID().slice(0, 8); const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Management read ${nonce}`, slug: `management-read-${nonce}` })).tenantId;
    const prefix = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
    const draft = await call<{ draftId: string; definitionId: string; candidate: Record<string, unknown> }>(api, prefix + '/agent-definitions', { name: 'Configuration reader', expectedRevision: await revision() });
    draft.candidate.executionPolicy = { harness: [], knowledgeSources: [], toolAllowlist: [], capabilities: { mode: 'Selected', resolvedCapabilities: ['management.inspect', 'settings.list', 'settings.inspect', 'automation.preferences.inspect'] }, projection: { alwaysCapabilities: [] } };
    draft.candidate.selfManagement = { mode: 'Managed', scopes: ['Settings', 'AutomationPreferences'], sources: [], eligibleTools: [] };
    const instructionsText = 'Literal <script>saved setting</script> retained as evidence.'; draft.candidate.agentInstructions = instructionsText;
    const changed = await call<{ revision: number }>(api, prefix + `/definition-drafts/${draft.draftId}`, { candidate: draft.candidate, expectedRevision: await revision(), expectedDraftRevision: 1 }, 'PUT');
    await call(api, prefix + `/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: changed.revision });
    const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, prefix + '/agents/from-publication', { definitionId: draft.definitionId, version: 1, expectedRevision: await revision() });
    const role = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Configuration reader', permissions: ['agents.chat', 'sessions.read', 'agents.read'], expectedRevision: await revision() });
    await call(api, prefix + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    await call(api, prefix + '/operation-policies/agents.read', { mode: 'always', independentReview: false, expectedRevision: await revision(), expectedPolicyRevision: 0 }, 'PUT');
    await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible(); await page.goto(`/t/${tenant}`);
    const input = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(input).toBeEnabled(); await input.fill('synthetic-tenant-management-read: instructions'); await page.getByRole('button', { name: 'Send', exact: true }).click();
    const approvals = page.getByRole('region', { name: 'Conversation approvals', exact: true }); await expect(approvals.getByText('Inspect exact Agent configuration', { exact: true })).toBeVisible();
    await approvals.getByRole('link', { name: 'Review details', exact: true }).click(); const detail = page.getByRole('dialog', { name: 'Approval details', exact: true });
    const read = detail.getByRole('region', { name: 'Exact saved read', exact: true }); const instructions = read.getByText(instructionsText, { exact: false }).first();
    await instructions.scrollIntoViewIfNeeded(); await expect(instructions).toBeVisible(); expect(await read.locator('script').count()).toBe(0); await expect(read.getByText('Saved field', { exact: true })).toHaveCount(0);
    await expect(detail.getByRole('button', { name: 'Approve', exact: true })).toBeInViewport(); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: info.outputPath(`management-read-${width}-${decision}.png`), fullPage: true, animations: 'disabled' });
    const action = decision === 'approve' ? 'Approve' : 'Reject'; await detail.getByRole('button', { name: action, exact: true }).click(); const confirm = page.getByRole('dialog', { name: action + '?', exact: true });
    if (decision === 'approve') await expect(confirm).toContainText('This grants no write, raw permission or future read'); await confirm.getByRole('button', { name: action, exact: true }).click(); await detail.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.getByText(decision === 'approve' ? 'The exact saved Agent configuration was inspected. No settings were changed.' : 'The Agent configuration read was not authorized. No settings were changed.', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0);
    const saved = await call<{ instance: { revision: number } }>(api, prefix + `/agents/${agent.instance.instanceId}`); expect(saved.instance.revision).toBe(1); expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
