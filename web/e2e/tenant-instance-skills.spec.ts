import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';

const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T = any>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const response = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBeTruthy(); return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) {
  await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(email); await page.getByLabel('Password', { exact: true }).fill(secret); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call(api, '/api/v2/system/tenants', { name: `Regional Skills ${nonce}`, slug: `skills-${nonce}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`, revision = async () => (await call(api, root + '/access/me')).revision;
  let draft = await call(api, root + '/agent-definitions', { name: 'Regional operating standards', expectedRevision: await revision() });
  draft.candidate.skills = Array.from({ length: 13 }, (_, i) => ({ id: `regional-${String(i + 1).padStart(2, '0')}`, name: i ? `Regional review ${i + 1}` : 'Regional operations and customer escalation review across service locations', description: 'Review regional evidence.', procedure: 'Compare the supplied totals with supporting evidence.', projection: 'OnDemand', defaultEnabled: true, requiredCapabilities: ['chat.respond'], resourcePaths: [] }));
  draft = await call(api, root + `/definition-drafts/${draft.draftId}`, { candidate: draft.candidate, expectedRevision: await revision(), expectedDraftRevision: draft.revision }, 'PUT');
  await call(api, root + `/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: draft.revision });
  const id = (await call(api, root + '/agents/from-publication', { definitionId: draft.definitionId, version: 1, expectedRevision: await revision(), persona: { name: 'Alex — Regional operations and customer care', role: 'Guide', description: 'Synthetic Skills review.', tone: 'Calm' } })).instance.instanceId;
  const resource = root + `/agents/${id}`, route = `/t/${tenant}/admin/agents/${id}/skills`;
  const detail = (key: string) => call(api, resource + `/skills/${encodeURIComponent(key)}/editor`);
  return { api, tenant, root, id, resource, route, revision, detail };
}
async function search(page: Page, value: string) { await page.getByRole('searchbox', { name: 'Search readable Skills', exact: true }).fill(value); await page.getByRole('button', { name: 'Search', exact: true }).click(); }
test.beforeAll(async () => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); try { expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState(); } finally { await api.dispose(); }
});

test('320px owner authors and reviews Skills with exact stale recovery and inherited choices', async ({ page }) => {
  const f = await fixture(); const errors: string[] = [], unexpected: string[] = []; page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => { if (/^\/api\/v[12]\/(local\/owner-capability|owner-capability|agent-instances|admin)(\/|$)/.test(new URL(r.url()).pathname)) unexpected.push(r.url()); });
  try {
    await page.setViewportSize({ width: 320, height: 740 }); await signIn(page); await page.goto(f.route);
    await page.getByTitle('Next Page').click(); await expect(page.getByTitle('Previous Page')).toHaveAttribute('aria-disabled', 'false');
    await search(page, 'regional-01'); const row = page.getByRole('row').filter({ has: page.getByText('Regional operations and customer escalation review across service locations', { exact: true }) });
    const inspect = row.getByRole('button', { name: 'Inspect Skill', exact: true }); await inspect.click(); await expect(page.getByText('Compare the supplied totals with supporting evidence.', { exact: true })).toBeVisible(); await page.keyboard.press('Escape'); await expect(inspect).toBeFocused();
    await row.getByRole('switch').click(); await row.getByRole('button', { name: 'Reset choice', exact: true }).click(); await page.getByRole('button', { name: 'Reset choice', exact: true }).last().click(); await expect(row.getByRole('switch')).toBeChecked();
    await row.getByRole('button', { name: 'Customize', exact: true }).click(); await page.keyboard.press('Escape'); await expect(row.getByRole('button', { name: 'Customize', exact: true })).toBeFocused();
    await row.getByRole('button', { name: 'Customize', exact: true }).click(); await page.getByRole('button', { name: 'Customize Skill', exact: true }).click(); await expect.poll(async () => (await f.detail('definition:regional-01')).item.skill.enabled).toBe(false);
    await page.getByRole('button', { name: 'New Instance Skill', exact: true }).click(); await page.getByLabel('Skill ID', { exact: true }).fill('local-review'); await page.getByLabel('Skill name', { exact: true }).fill('Local regional review'); await page.getByLabel('Description', { exact: true }).fill('Review local totals.'); await page.getByLabel('Procedure', { exact: true }).fill('Original local procedure.'); await page.getByLabel('Required capabilities', { exact: true }).fill('chat.respond'); await page.getByRole('button', { name: 'Save Skill', exact: true }).click(); await expect(page.getByRole('dialog')).toHaveCount(0);
    await search(page, 'local-review'); await page.getByRole('button', { name: 'Edit Skill', exact: true }).click(); await page.getByLabel('Procedure', { exact: true }).fill('Retained reviewed procedure.'); await page.getByRole('button', { name: 'Cancel editing', exact: true }).click(); await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
    const editor = await call(f.api, f.resource + '/editor'); await call(f.api, f.resource + '/settings/conversationPolicy', { expectedRevision: editor.securityRevision, expectedInstanceRevision: editor.agent.instance.revision, set: { language: 'de' }, clear: [] }, 'PATCH');
    await page.getByRole('button', { name: 'Save Skill', exact: true }).click(); await expect(page.getByText(/Access has changed since/)).toBeVisible(); await expect(page.getByLabel('Procedure', { exact: true })).toHaveValue('Retained reviewed procedure.');
    await page.getByRole('button', { name: 'Reload latest Skill', exact: true }).click(); await expect(page.getByRole('button', { name: 'Save Skill', exact: true })).toBeEnabled(); await page.getByRole('button', { name: 'Save Skill', exact: true }).click(); await expect(page.getByRole('dialog')).toHaveCount(0); expect((await f.detail('instance:local-review')).item.skill.procedure).toBe('Retained reviewed procedure.');
    await page.getByRole('button', { name: 'Delete', exact: true }).click(); await page.getByRole('button', { name: 'Delete Skill', exact: true }).click(); await expect(page.getByText('No matching readable Skills.', { exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(errors).toEqual([]); expect(unexpected).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('exact item reader can inspect without IAM tools and revocation retires open protected content', async ({ page }) => {
  const f = await fixture(); const unexpected: string[] = [];
  page.on('request', r => { const path = new URL(r.url()).pathname; if (/^\/api\/v[12]\/(local\/owner-capability|owner-capability|agent-instances|admin)(\/|$)/.test(path) || /\/(members|roles|role-assignments|access\/resources|access\/check)(\/|$)/.test(path)) unexpected.push(path); });
  try {
    const role = async (name: string, permissions: string[]) => (await call(f.api, f.root + '/roles', { name, permissions, expectedRevision: await f.revision() })).roleId;
    const minimal = await role('Minimal membership', ['sessions.read']), parent = await role('Exact parent read', ['agents.read']), reader = await role('Exact Skill read', ['skills.read']);
    const email = `skill-reader-${crypto.randomUUID().slice(0, 8)}@example.test`, secret = 'synthetic-skill-reader-password'; const invite = await call(f.api, f.root + '/members/invite', { login: email, roleId: minimal, expectedRevision: await f.revision() });
    const registration = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); try { await call(registration, '/api/v2/auth/register', { login: email, displayName: 'Exact Skill reader', password: secret, invitation: { tenantId: f.tenant, invitationId: invite.invitationId, token: invite.token } }); } finally { await registration.dispose(); }
    const member = (await call(f.api, f.root + '/members')).items.find((m: any) => m.displayName === 'Exact Skill reader');
    const grant = async (roleId: string, resourceType: string, resourceId: string) => call(f.api, f.root + '/role-assignments', { principalId: member.principalId, roleId, resourceType, resourceId, expectedRevision: await f.revision() });
    await grant(parent, 'agent', f.id); const assignment = await grant(reader, 'skill', `${f.id}:definition:regional-01`);
    await signIn(page, email, secret); await page.goto(f.route); await expect(page.getByRole('button', { name: 'New Instance Skill', exact: true })).toBeDisabled(); await expect(page.getByRole('switch')).toBeDisabled(); await expect(page.getByText('Regional review 2', { exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Inspect Skill', exact: true }).click(); await expect(page.getByText('Compare the supplied totals with supporting evidence.', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Save Skill', exact: true })).toHaveCount(0);
    await call(f.api, `${f.root}/role-assignments/${assignment.assignmentId}?expectedRevision=${await f.revision()}`, undefined, 'DELETE'); await page.getByRole('button', { name: 'Reload latest Skill', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0); await expect(page.getByText('Compare the supplied totals with supporting evidence.', { exact: true })).toHaveCount(0); await expect(page.getByRole('button', { name: 'Inspect Skill', exact: true })).toHaveCount(0); expect(unexpected).toEqual([]);
  } finally { await f.api.dispose(); }
});
