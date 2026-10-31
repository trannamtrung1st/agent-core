import { canonicalFetch, signInTenant } from './support/tenant-auth';
import { openDestination } from './support/experience-navigation';
import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';

const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBeTruthy(); return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) { await signInTenant(page, email, secret); }
type Draft = { definitionId: string; draftId: string; revision: number; candidate: Record<string, unknown> };
async function fixture(count = 2) {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Version review ${nonce}`, slug: `version-${nonce}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`, revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
  let draft = await call<Draft>(api, root + '/agent-definitions', { name: 'Regional support working standards', expectedRevision: await revision() }); const definitionId = draft.definitionId;
  await call(api, root + `/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: draft.revision });
  const id = (await call<{ instance: { instanceId: string } }>(api, root + '/agents/from-publication', { definitionId, version: 1, expectedRevision: await revision(), persona: { name: 'Alex — Regional operations and customer care', role: 'Guide', description: 'Synthetic version review.', tone: 'Calm' } })).instance.instanceId;
  for (let version = 2; version <= count; version++) {
    draft = await call<Draft>(api, root + `/agent-definitions/${definitionId}/drafts`, { version: version - 1, expectedRevision: await revision() });
    draft.candidate.agentInstructions = `Use regional working standards revision ${version}.`;
    draft = await call<Draft>(api, root + `/definition-drafts/${draft.draftId}`, { candidate: draft.candidate, expectedRevision: await revision(), expectedDraftRevision: draft.revision }, 'PUT');
    await call(api, root + `/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: draft.revision });
  }
  const resource = root + `/agents/${id}`, route = `/t/${tenant}/admin/agents/${id}/version`;
  const read = () => call<any>(api, resource + '/editor');
  const patch = async () => { const v = await read(); return call(api, resource + '/settings/conversationPolicy', { expectedRevision: v.securityRevision, expectedInstanceRevision: v.agent.instance.revision, set: { language: 'de' }, clear: [] }, 'PATCH'); };
  return { api, tenant, root, id, definitionId, resource, route, revision, read, patch };
}
async function choose(page: Page, label: string) {
  const input = page.getByRole('combobox', { name: 'Target Definition version', exact: true }); await input.click();
  const listId = await input.getAttribute('aria-controls'); const popup = page.locator('.ant-select-dropdown').filter({ has: page.locator(`[id="${listId}"]`) });
  const active = popup.locator('.ant-select-item-option-active');
  for (let i = 0; i < 15; i++) { if (await active.getAttribute('title') === label) break; await input.press('ArrowDown'); }
  await expect(active).toHaveAttribute('title', label); await input.press('Enter'); await expect(popup).toBeHidden();
}
test.beforeAll(async () => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try { expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState(); } finally { await api.dispose(); }
});


test('320px version paging, guarded review, focus, stale recovery and explicit adoption preserve configuration', async ({ page }) => {
  const f = await fixture(12); const errors: string[] = [], legacy: string[] = [];
  page.on('pageerror', e => errors.push(e.message)); page.on('request', r => { if (/^\/api\/v[12]\/(local\/owner-capability|owner-capability|agent-instances|admin)(\/|$)/.test(new URL(r.url()).pathname)) legacy.push(r.url()); });
  try {
    await page.setViewportSize({ width: 320, height: 740 }); await signIn(page); await page.goto(f.route);
    await page.getByTitle('Next Page').click(); await choose(page, 'v2 · Active');
    await openDestination(page, 'Profile');
    await page.getByRole('dialog', { name: 'Discard unsaved changes?' }).getByRole('button', { name: 'Keep editing', exact: true }).click();
    await page.getByRole('button', { name: 'Preview changes', exact: true }).click(); const adopt = page.getByRole('button', { name: 'Adopt Definition v2', exact: true }); await expect(adopt).toBeEnabled(); expect((await f.read()).agent.instance.activeVersion).toBe(1);
    await adopt.click(); await page.keyboard.press('Escape'); await expect(page.getByRole('dialog', { name: 'Adopt Definition v2?', exact: true })).toHaveCount(0); await expect(adopt).toBeFocused();
    await f.patch(); await adopt.click(); await page.getByRole('dialog', { name: 'Adopt Definition v2?', exact: true }).getByRole('button', { name: 'Adopt version', exact: true }).click();
    await expect(page.getByText(/Access has changed since you opened/)).toBeVisible(); await expect(adopt).toHaveCount(0); await expect(page.getByRole('button', { name: 'Reload versions', exact: true })).toBeFocused();
    await page.getByRole('button', { name: 'Reload versions', exact: true }).click(); await page.getByRole('button', { name: 'Preview changes', exact: true }).click(); await expect(adopt).toBeEnabled();
    await adopt.click(); const dialog = page.getByRole('dialog', { name: 'Adopt Definition v2?', exact: true }); await expect(dialog).toContainText('Alex — Regional operations and customer care'); await dialog.getByRole('button', { name: 'Adopt version', exact: true }).click();
    await expect(page.getByText('Definition v2', { exact: true })).toBeVisible(); const saved = await f.read(); expect(saved.agent.instance.activeVersion).toBe(2); expect(saved.settings.find((s: any) => s.section === 'conversationPolicy').effective.language).toBe('de');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(errors).toEqual([]); expect(legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('exact reader needs Definition source permission, can preview without update/IAM inspection and loses protected content on revocation', async ({ page }) => {
  const f = await test.step('Prepare isolated version fixture', () => fixture()); const unexpected: string[] = [];
  page.on('request', request => { const path = new URL(request.url()).pathname; if (/^\/api\/v[12]\/(local\/owner-capability|owner-capability|agent-instances|admin)(\/|$)/.test(path) || /\/(members|roles|role-assignments|access\/resources|access\/check)(\/|$)/.test(path)) unexpected.push(path); });
  try {
    const role = async (name: string, permissions: string[]) => call<{ roleId: string }>(f.api, f.root + '/roles', { name, permissions, expectedRevision: await f.revision() });
    const minimal = await role('Minimal membership', ['sessions.read']), reader = await role('Exact version reader', ['agents.read']), source = await role('Exact source reader', ['agent-definitions.read']);
    const email = `version-reader-${crypto.randomUUID().slice(0, 8)}@example.test`, secret = 'synthetic-version-reader-password';
    const invitation = await call<{ invitationId: string; token: string }>(f.api, f.root + '/members/invite', { login: email, roleId: minimal.roleId, expectedRevision: await f.revision() });
    const registration = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
    try { await call(registration, '/api/v2/auth/register', { login: email, displayName: 'Exact version reader', password: secret, invitation: { tenantId: f.tenant, invitationId: invitation.invitationId, token: invitation.token } }); } finally { await registration.dispose(); }
    const member = (await call<{ items: { principalId: string; displayName: string }[] }>(f.api, f.root + '/members')).items.find(item => item.displayName === 'Exact version reader')!;
    await call(f.api, f.root + '/role-assignments', { principalId: member.principalId, roleId: reader.roleId, resourceType: 'agent', resourceId: f.id, expectedRevision: await f.revision() });
    await signIn(page, email, secret); await page.goto(f.route); await expect(page.getByRole('alert')).toBeVisible(); await expect(page.getByRole('combobox', { name: 'Target Definition version', exact: true })).toHaveCount(0);
    const assignment = await call<{ assignmentId: string }>(f.api, f.root + '/role-assignments', { principalId: member.principalId, roleId: source.roleId, resourceType: 'definition', resourceId: f.definitionId, expectedRevision: await f.revision() });
    await page.getByRole('button', { name: 'Reload versions', exact: true }).click(); await expect(page.getByText('Read-only configuration', { exact: true })).toBeVisible();
    await choose(page, 'v2 · Active'); await page.getByRole('button', { name: 'Preview changes', exact: true }).click(); await expect(page.getByRole('button', { name: 'Adopt Definition v2', exact: true })).toBeDisabled(); await expect(page.getByText('Child details follow your read access', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: /Preview changes$/ })).not.toHaveClass(/ant-btn-loading/);
    await test.step('Revoke exact Definition source read', async () => { await call(f.api, `${f.root}/role-assignments/${assignment.assignmentId}?expectedRevision=${await f.revision()}`, undefined, 'DELETE'); });
    await test.step('Read again and retire revoked content', async () => { await page.getByRole('button', { name: 'Preview changes', exact: true }).click(); await expect(page.getByRole('combobox', { name: 'Target Definition version', exact: true })).toHaveCount(0); await expect(page.getByRole('button', { name: 'Adopt Definition v2', exact: true })).toHaveCount(0); });
    expect(unexpected).toEqual([]);
  } finally { await f.api.dispose(); }
});
