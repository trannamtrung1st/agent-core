import { expect, test, request as requests, type APIRequestContext } from '@playwright/test';
const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const r = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(r.ok(), `${method} ${path}: ${r.status()} ${await r.text()}`).toBeTruthy(); return r.status() === 204 ? undefined as T : r.json();
}
let authenticatedState: Awaited<ReturnType<APIRequestContext['storageState']>>;
test.beforeAll(async ({ browser }) => {
  const context = await browser.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); const page = await context.newPage();
  try {
    expect((await (await context.request.get('/health')).json()).profile).toBe('Synthetic');
    await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
    authenticatedState = await context.storageState();
  } finally { await context.close(); }
});
for (const tool of ['harness', 'configuration', 'skills.list', 'skills.inspect', 'resources.list', 'resources.inspect']) for (const width of [1440, 390]) for (const decision of ['approve', 'reject'])
  test(`compound management inspection ${tool} ${decision} ${width}`, async ({ page, context }, info) => {
  test.setTimeout(120000); await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 }); const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message)); page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) errors.push(r.url()); });
  page.on('response', r => { if (r.status() >= 500) errors.push(`${r.status()} ${r.url()}`); });
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: authenticatedState });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await context.addCookies(authenticatedState.cookies);
    const nonce = crypto.randomUUID().slice(0, 8); const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Compound inspection ${nonce}`, slug: `compound-inspection-${nonce}` })).tenantId;
    const root = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
    const draft = await call<{ draftId: string; definitionId: string; candidate: Record<string, unknown> }>(api, root + '/agent-definitions', { name: 'Metadata reader', expectedRevision: await revision() });
    draft.candidate.environment = { harness: [], knowledgeSources: [], toolAllowlist: [], capabilities: { mode: 'Selected', resolvedCapabilities: ['configuration.inspect', 'skills.list', 'skills.inspect', 'resources.list', 'resources.inspect'] }, projection: { alwaysCapabilities: [] } };
    draft.candidate.selfManagement = { mode: 'Managed', scopes: ['Instructions', 'KnowledgeResources', 'ToolSelection', 'Skills', 'Settings', 'Resources', 'Connections'], sources: [], eligibleTools: [] };
    const instruction = 'Literal <script>saved inspection instruction</script>.'; const skillName = 'Review <script>literal Skill metadata</script>';
    draft.candidate.agentInstructions = instruction;
    draft.candidate.skills = [{ id: 'metadata-review', name: skillName, description: 'A bounded metadata observation', procedure: 'PRIVATE-INSPECTION-PROCEDURE-SENTINEL', projection: 'OnDemand', defaultEnabled: true, requiredCapabilities: ['chat.respond'], resourcePaths: [] }];
    const edited = await call<{ revision: number }>(api, root + `/definition-drafts/${draft.draftId}`, { candidate: draft.candidate, expectedRevision: await revision(), expectedDraftRevision: 1 }, 'PUT');
    await call(api, root + `/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: edited.revision });
    const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, root + '/agents/from-publication', { definitionId: draft.definitionId, version: 1, expectedRevision: await revision() });
    const path = root + `/agents/${agent.instance.instanceId}`;
    const savedResource = await call<{ resource: { key: string } }>(api, path + '/resources', { expectedRevision: await revision(), expectedInstanceRevision: 1, input: { logicalPath: 'private/metadata.txt', kind: 1, mediaType: 'text/plain', content: Buffer.from('PRIVATE-INSPECTION-RESOURCE-BYTES-SENTINEL').toString('base64') } });
    const credential = await call<{ credential: { credentialId: string } }>(api, root + '/credentials', { displayName: 'Readable credential <literal>', kind: 'Token', purpose: 'General', protectedValue: 'PRIVATE-INSPECTION-SECRET-SENTINEL', expectedRevision: await revision() });
    await call(api, path + '/credential-bindings', { credentialId: credential.credential.credentialId, reference: 'inspection-account', expectedRevision: await revision(), expectedInstanceRevision: 2, expectedCredentialRevision: 1 });
    const role = await call<{ roleId: string }>(api, root + '/roles', { name: 'Compound metadata reader', permissions: ['agents.chat', 'sessions.read', 'agents.read', 'skills.read', 'resources.read', 'credentials.read'], expectedRevision: await revision() });
    await call(api, root + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    await call(api, root + '/operation-policies/' + (tool.startsWith('resources.') ? 'resources.read' : 'skills.read'), { mode: 'always', independentReview: false, expectedRevision: await revision(), expectedPolicyRevision: 0 }, 'PUT');
    await page.goto(`/t/${tenant}`); const input = page.getByRole('textbox', { name: 'Message', exact: true }); await expect(input).toBeEnabled();
    const selected = tool === 'skills.inspect' ? tool + ' definition:metadata-review' : tool === 'resources.inspect' ? tool + ' ' + savedResource.resource.key : tool;
    await input.fill('synthetic-tenant-management-read: ' + selected); await page.getByRole('button', { name: 'Send', exact: true }).click();
    const approvals = page.getByRole('region', { name: 'Conversation approvals', exact: true }); await expect(approvals.getByText('Inspect exact Agent configuration', { exact: true })).toBeVisible();
    await approvals.getByRole('link', { name: 'Review details', exact: true }).click(); const detail = page.getByRole('dialog', { name: 'Approval details', exact: true });
    const read = detail.getByRole('region', { name: 'Exact saved read', exact: true });
    if (tool === 'harness') await expect(read.getByText(instruction, { exact: false }).first()).toBeAttached();
    if (!tool.startsWith('resources.')) { const skill = read.getByText(skillName, { exact: false }).first(); await skill.scrollIntoViewIfNeeded(); await expect(skill).toBeVisible(); }
    else await expect(read.getByText('private/metadata.txt', { exact: false }).first()).toBeAttached();
    const text = await read.innerText(); for (const secret of ['PRIVATE-INSPECTION-PROCEDURE-SENTINEL', 'PRIVATE-INSPECTION-RESOURCE-BYTES-SENTINEL', 'PRIVATE-INSPECTION-SECRET-SENTINEL']) if (secret === 'PRIVATE-INSPECTION-PROCEDURE-SENTINEL' && tool === 'skills.inspect' || secret === 'PRIVATE-INSPECTION-RESOURCE-BYTES-SENTINEL' && tool === 'resources.inspect') expect(text).toContain(secret); else expect(text).not.toContain(secret);
    expect(await read.locator('script').count()).toBe(0); await expect(read.getByText('Saved field', { exact: true })).toHaveCount(0);
    await expect(detail.getByRole('button', { name: 'Approve', exact: true })).toBeInViewport(); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: info.outputPath(`inspection-${tool}-${width}-${decision}.png`), fullPage: true, animations: 'disabled' });
    const action = decision === 'approve' ? 'Approve' : 'Reject'; await detail.getByRole('button', { name: action, exact: true }).click(); const confirm = page.getByRole('dialog', { name: action + '?', exact: true });
    if (decision === 'approve') await expect(confirm).toContainText('This grants no write, raw permission or future read');
    await confirm.getByRole('button', { name: action, exact: true }).click(); await detail.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.getByText(decision === 'approve' ? 'The exact saved Agent configuration was inspected. No settings were changed.' : 'The Agent configuration read was not authorized. No settings were changed.', { exact: true })).toBeVisible();
    if (decision === 'approve') await expect(approvals.getByText('Read authorized', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0); expect((await call<{ instance: { revision: number } }>(api, path)).instance.revision).toBe(3); expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
