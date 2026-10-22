import { expect, test, request as requests, type APIRequestContext } from '@playwright/test';
const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const r = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(r.ok(), `${method} ${path}: ${r.status()} ${await r.text()}`).toBeTruthy(); return r.status() === 204 ? undefined as T : r.json();
}
for (const width of [1440, 390]) for (const decision of ['approve', 'reject']) test(`exact Automation read ${decision} ${width}`, async ({ page }, info) => {
  test.setTimeout(120000); await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 }); const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message)); page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) errors.push(r.url()); });
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password });
    const nonce = crypto.randomUUID().slice(0, 8); const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Automation reads ${nonce}`, slug: `automation-reads-${nonce}` })).tenantId;
    const prefix = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
    const draft = await call<{ draftId: string; definitionId: string; candidate: Record<string, unknown> }>(api, prefix + '/agent-definitions', { name: 'Metadata reader', expectedRevision: await revision() });
    draft.candidate.environment = { harness: [], knowledgeSources: [], toolAllowlist: [], capabilities: { mode: 'Selected', resolvedCapabilities: ['automation.inspect', 'automation.list'] }, projection: { alwaysCapabilities: ['automation.inspect', 'automation.list'] } };
    draft.candidate.triggerPolicy = { enabled: true, allowUserScheduling: true, allowOneShot: true, allowDaily: false, allowWeekly: false, allowIndefiniteRecurrence: false, maxActiveRegistrations: 20, oneShotHorizonDays: 30, minRecurrenceDays: 1, allowedSourceKinds: ['schedule'] };
    const changed = await call<{ revision: number }>(api, prefix + `/definition-drafts/${draft.draftId}`, { candidate: draft.candidate, expectedRevision: await revision(), expectedDraftRevision: 1 }, 'PUT');
    await call(api, prefix + `/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: changed.revision });
    const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, prefix + '/agents/from-publication', { definitionId: draft.definitionId, version: 1, expectedRevision: await revision() });
    const role = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Metadata reader', permissions: ['agents.chat', 'sessions.read', 'automations.read'], expectedRevision: await revision() });
    await call(api, prefix + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    const route = prefix + `/agents/${agent.instance.instanceId}/automations`;
    const automation = await call<{ automationId: string; revision: number }>(api, route, { enabled: false, name: 'Saved owned task', instructions: 'Literal <script>configuration</script> retained only as evidence.', requiresTools: false,
      schedule: { kind: 'oneShot', timeZone: 'UTC', atUtc: new Date(Date.now() + 3600000).toISOString() }, expectedRevision: await revision(), expectedInstanceRevision: 1 });
    await call(api, prefix + '/operation-policies/automations.read', { mode: 'always', independentReview: false, expectedRevision: await revision(), expectedPolicyRevision: 0 }, 'PUT');
    await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible(); await page.goto(`/t/${tenant}`);
    const input = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(input).toBeEnabled(); await input.fill('synthetic-tenant-automation-read: ' + automation.automationId); await page.getByRole('button', { name: 'Send', exact: true }).click();
    const approvals = page.getByRole('region', { name: 'Conversation approvals', exact: true }); await expect(approvals.getByText('Inspect exact Automation metadata', { exact: true })).toBeVisible();
    await approvals.getByRole('link', { name: 'Review details', exact: true }).click(); const detail = page.getByRole('dialog', { name: 'Approval details', exact: true });
    const read = detail.getByRole('region', { name: 'Exact saved read', exact: true }); const instructions = read.getByText('Literal <script>configuration</script> retained only as evidence.', { exact: true });
    await instructions.scrollIntoViewIfNeeded(); await expect(instructions).toBeVisible(); expect(await read.locator('script').count()).toBe(0);
    await expect(detail.getByRole('button', { name: 'Approve', exact: true })).toBeInViewport();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); await page.screenshot({ path: info.outputPath(`automation-read-${width}.png`), fullPage: true, animations: 'disabled' });
    const action = decision === 'approve' ? 'Approve' : 'Reject'; await detail.getByRole('button', { name: action, exact: true }).click(); const confirm = page.getByRole('dialog', { name: action + '?', exact: true });
    if (decision === 'approve') await expect(confirm).toContainText('no Automation mutation, execution or future read authority'); await confirm.getByRole('button', { name: action, exact: true }).click(); await detail.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.getByText(decision === 'approve' ? 'The exact Automation metadata was inspected. No Automation was changed.' : 'Automation inspection was not authorized. No Automation was changed.', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0); if (decision === 'approve') await expect(approvals.getByText('Read authorized', { exact: true })).toBeVisible();
    expect((await call<{ revision: number }>(api, route + '/' + automation.automationId)).revision).toBe(automation.revision); expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
