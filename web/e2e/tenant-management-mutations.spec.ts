import { canonicalFetch, signInTenant } from './support/tenant-auth';
import { expect, test, request as requests, type APIRequestContext } from '@playwright/test';
const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const r = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(r.ok(), `${method} ${path}: ${r.status()} ${await r.text()}`).toBeTruthy(); return r.status() === 204 ? undefined as T : r.json();
}
let authenticatedState: Awaited<ReturnType<APIRequestContext['storageState']>>;
test.beforeAll(async ({ browser }) => {
  const context = await browser.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); const page = await context.newPage();
  try {
    expect((await (await context.request.get('/health')).json()).profile).toBe('Synthetic');
    await signInTenant(page, login, password);
    authenticatedState = await context.storageState();
  } finally { await context.close(); }
});
for (const tool of ['settings.patch', 'management.policy.update', 'management.freeze', 'automation.preferences.patch'])
  for (const width of [1440, 390]) for (const decision of ['approve', 'reject']) test(`management command ${tool} ${decision} ${width}`, async ({ page, context }, info) => {
  test.setTimeout(120000); await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 }); const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message)); page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) errors.push(r.url()); });
  page.on('response', r => { if (r.status() >= 500) errors.push(`${r.status()} ${r.url()}`); });
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: authenticatedState });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await context.addCookies(authenticatedState.cookies);
    const nonce = crypto.randomUUID().slice(0, 8); const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Management command ${nonce}`, slug: `management-command-${nonce}` })).tenantId;
    const prefix = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
    const draft = await call<{ draftId: string; definitionId: string; candidate: Record<string, unknown> }>(api, prefix + '/agent-definitions', { name: 'Configuration editor', expectedRevision: await revision() });
    draft.candidate.environment = { harness: [], knowledgeSources: [], toolAllowlist: [], capabilities: { mode: 'Selected', resolvedCapabilities: ['management.inspect', 'settings.inspect', ...['settings.patch', 'management.policy.update', 'management.freeze', 'automation.preferences.patch']] }, projection: { alwaysCapabilities: [] } };
    draft.candidate.selfManagement = { mode: 'Managed', scopes: ['Settings', 'ManagementPolicy', 'AutomationPreferences'], sources: [], eligibleTools: [] };
    const oldInstructions = 'Saved literal <script>old configuration</script>.'; const nextInstructions = 'New literal <script>future configuration</script>.'; draft.candidate.agentInstructions = oldInstructions;
    const options = await call<{ authoring: { models: { key: string }[] } }>(api, prefix + `/definition-authoring-options?definitionId=${draft.definitionId}`); const key = options.authoring.models[0].key;
    const changed = await call<{ revision: number }>(api, prefix + `/definition-drafts/${draft.draftId}`, { candidate: draft.candidate, expectedRevision: await revision(), expectedDraftRevision: 1 }, 'PUT');
    await call(api, prefix + `/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: changed.revision });
    const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, prefix + '/agents/from-publication', { definitionId: draft.definitionId, version: 1, expectedRevision: await revision() });
    const role = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Configuration editor', permissions: ['agents.chat', 'sessions.read', 'agents.read', 'agents.update'], expectedRevision: await revision() });
    await call(api, prefix + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    const arguments_ = tool === 'settings.patch' ? { expectedInstanceRevision: 1, section: 'instructions', set: { agentInstructions: nextInstructions }, clear: [] }
      : tool === 'management.policy.update' ? { expectedInstanceRevision: 1, mode: 'Assisted', scopes: ['Settings'], managedAutoApply: [] }
      : tool === 'management.freeze' ? { expectedInstanceRevision: 1 } : { expectedInstanceRevision: 1, catalogKey: key, reasoningEffort: null };
    await page.goto(`/t/${tenant}`);
    const input = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(input).toBeEnabled(); await input.fill('synthetic-tenant-management-mutation: ' + JSON.stringify({ tool, arguments: arguments_ })); await page.getByRole('button', { name: 'Send', exact: true }).click();
    const approvals = page.getByRole('region', { name: 'Conversation approvals', exact: true }); await expect(approvals.getByText('Change exact Agent configuration', { exact: true })).toBeVisible();
    await approvals.getByRole('link', { name: 'Review details', exact: true }).click(); const detail = page.getByRole('dialog', { name: 'Approval details', exact: true });
    const changes = detail.getByRole('region', { name: 'Exact saved changes', exact: true });
    await expect(changes.getByText(tool, { exact: true })).toBeAttached(); expect(await changes.locator('script').count()).toBe(0); await expect(changes.getByText('Saved field', { exact: true })).toHaveCount(0);
    if (tool === 'settings.patch') { await changes.getByText(nextInstructions, { exact: true }).scrollIntoViewIfNeeded(); await expect(changes.getByText(oldInstructions, { exact: true })).toBeAttached(); }
    await expect(detail.getByRole('button', { name: 'Approve', exact: true })).toBeInViewport(); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: info.outputPath(`management-command-${width}-${decision}.png`), fullPage: true, animations: 'disabled' });
    const action = decision === 'approve' ? 'Approve' : 'Reject'; await detail.getByRole('button', { name: action, exact: true }).click(); const confirm = page.getByRole('dialog', { name: action + '?', exact: true });
    if (decision === 'approve') await expect(confirm).toContainText('only this exact saved Agent configuration change once'); await confirm.getByRole('button', { name: action, exact: true }).click(); await detail.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.getByText(decision === 'approve' ? 'The exact Agent configuration change was recorded. Current Run settings stayed pinned.' : 'The Agent configuration change was withheld. No settings were changed by this request.', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0); if (decision === 'approve') await expect(approvals.getByText('Configuration updated', { exact: true })).toBeVisible();
    const path = prefix + `/agents/${agent.instance.instanceId}`;
    const saved = await call<{ instance: { revision: number; unattendedModelCatalogKey: string | null } }>(api, path);
    expect(saved.instance.revision).toBe(decision === 'approve' ? 2 : 1);
    if (decision === 'approve') {
      if (tool === 'settings.patch') {
        const settings = await call<{ items: { section: string; effective: { agentInstructions?: string }; sources: { agentInstructions?: string } }[] }>(api, path + '/settings');
        const instructions = settings.items.find(item => item.section === 'instructions')!; expect(instructions.effective.agentInstructions).toBe(nextInstructions); expect(instructions.sources.agentInstructions).toBe('instance');
      }
      if (tool === 'management.freeze' || tool === 'management.policy.update') {
        const management = await call<{ policy: { effective: { frozen: boolean; mode: string } } }>(api, path + '/self-management');
        if (tool === 'management.freeze') expect(management.policy.effective.frozen).toBe(true); else expect(management.policy.effective.mode).toBe('Assisted');
      }
      if (tool === 'automation.preferences.patch') expect(saved.instance.unattendedModelCatalogKey).toBe(key);
    }
    expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
