import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';

const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBeTruthy(); return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) {
  await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(email); await page.getByLabel('Password', { exact: true }).fill(secret);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}
async function select(page: Page, name: string, label: string) { await page.getByRole('combobox', { name, exact: true }).click(); await page.getByTitle(label, { exact: true }).click(); }
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Agent Access ${nonce}`, slug: `agent-access-${nonce}` })).tenantId;
  const prefix = `/api/v2/tenants/${tenant}`, path = `/t/${tenant}/admin/agents`; const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
  const role = async (name: string, permissions: string[]) => call<{ roleId: string }>(api, prefix + '/roles', { name, permissions, expectedRevision: await revision() });
  const chat = await role('Agent-specific Chat', ['agents.chat']), execution = await role('Credential metadata reader', ['credentials.read']), membership = await role('Minimal organization member', ['sessions.read']);
  const memberLogin = `agent-access-${nonce}@example.test`, memberPassword = 'synthetic-agent-access-password';
  const invite = await call<{ invitationId: string; token: string }>(api, prefix + '/members/invite', { login: memberLogin, roleId: membership.roleId, expectedRevision: await revision() });
  const publicApi = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try { await call(publicApi, '/api/v2/auth/register', { login: memberLogin, displayName: 'Avery Nguyen — long member name for scoped access', password: memberPassword, invitation: { tenantId: tenant, invitationId: invite.invitationId, token: invite.token } }); } finally { await publicApi.dispose(); }
  const member = (await call<{ items: { principalId: string; displayName: string }[] }>(api, prefix + '/members')).items.find(item => item.displayName.startsWith('Avery'))!;
  return { api, tenant, prefix, path, revision, role, chat, execution, member, memberLogin, memberPassword };
}
test.beforeAll(async () => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try { expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState(); } finally { await api.dispose(); }
});

test('create, grant exact chat and independent execution, inspect permissions, recover stale revoke and lifecycle', async ({ page }) => {
  const f = await fixture(); const errors: string[] = [], legacy: string[] = []; page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (/^\/api\/v[12]\/(owner-capability|agent-instances|admin)(\/|$)/.test(new URL(request.url()).pathname)) legacy.push(request.url()); });
  try {
    await page.setViewportSize({ width: 320, height: 740 }); await signIn(page); await page.goto(f.path);
    await page.getByRole('button', { name: 'New instance', exact: true }).click(); await page.getByRole('dialog', { name: 'New agent instance' }).getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.getByRole('button', { name: 'New instance', exact: true })).toBeFocused(); await page.getByRole('button', { name: 'New instance', exact: true }).click();
    const drawer = page.getByRole('dialog', { name: 'New agent instance' }); await drawer.getByLabel('Agent template', { exact: true }).fill('Alex');
    await page.getByTitle('Alex · Speaking examiner · v1', { exact: true }).click(); await drawer.getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByRole('dialog', { name: 'Discard unsaved changes?' }).getByRole('button', { name: 'Keep editing', exact: true }).click();
    await drawer.getByRole('button', { name: 'Create instance', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`${f.path}/[0-9a-f-]+/access$`)); const id = page.url().split('/').at(-2)!;
    await select(page, 'Person or agent', 'Avery Nguyen — long member name for scoped access · Person'); await select(page, 'Chat role', 'Agent-specific Chat');
    await page.getByRole('button', { name: 'Grant chat access', exact: true }).click(); await page.getByRole('dialog').getByRole('button', { name: 'Grant chat access', exact: true }).click();
    const audience = page.getByRole('region', { name: 'Who can chat' }); await expect(audience.getByText('Agent-specific Chat', { exact: true })).toBeVisible();
    const check = (principalId: string, permission: string, resource = false) => call<{ allowed: boolean }>(f.api, f.prefix + '/access/check', { principalId, permission, ...(resource ? { resourceType: 'agent', resourceId: id } : {}) });
    expect((await check(f.member.principalId, 'agents.chat', true)).allowed).toBe(true);
    await select(page, 'Execution role', 'Credential metadata reader'); await page.getByRole('button', { name: 'Grant execution role', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Grant execution role', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Agent execution authority' }).getByText('Credential metadata reader', { exact: true })).toBeVisible();
    const saved = await call<{ principalId: string }>(f.api, f.prefix + `/agents/${id}`);
    expect((await check(saved.principalId, 'credentials.read')).allowed).toBe(true); expect((await check(f.member.principalId, 'credentials.read')).allowed).toBe(false);
    await page.getByRole('radio', { name: 'Organization', exact: true }).check(); const authority = page.getByRole('region', { name: 'Agent execution authority' });
    await authority.getByRole('button', { name: 'Credentials', exact: true }).click(); await expect(authority.getByText('Allowed', { exact: true })).toBeVisible();
    await f.role('Concurrent unrelated access change', ['agents.read']);
    await audience.getByRole('button', { name: /^Revoke Agent-specific Chat/ }).click(); await expect(page.getByRole('dialog')).toContainText('Agent: Alex.');
    await page.getByRole('dialog').getByRole('button', { name: 'Revoke role', exact: true }).click();
    await expect(page.getByText(/Access has changed since you opened/)).toBeVisible(); expect((await check(f.member.principalId, 'agents.chat', true)).allowed).toBe(true);
    await page.getByRole('button', { name: 'Reload access', exact: true }).click(); await audience.getByRole('button', { name: /^Revoke Agent-specific Chat/ }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Revoke role', exact: true }).click(); await expect(audience.getByText('Agent-specific Chat', { exact: true })).toHaveCount(0);
    expect((await check(f.member.principalId, 'agents.chat', true)).allowed).toBe(false);
    await page.getByRole('button', { name: 'Archive instance', exact: true }).click(); await page.getByRole('dialog').getByRole('button', { name: 'Archive instance', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Restore instance', exact: true })).toBeVisible(); await expect(audience.getByText('Can chat', { exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Restore instance', exact: true }).click(); await page.getByRole('dialog').getByRole('button', { name: 'Restore instance', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Archive instance', exact: true })).toBeVisible();
    await page.reload(); await expect(authority.getByText('Credential metadata reader', { exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(errors).toEqual([]); expect(legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('a resource-scoped reader can inspect only that agent and loses private content after revocation', async ({ page }) => {
  const f = await fixture();
  try {
    const agent = await call<{ instance: { instanceId: string } }>(f.api, f.prefix + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await f.revision() }); const id = agent.instance.instanceId;
    const role = await f.role('Scoped access reader', ['agents.read', 'iam.roles.read']);
    const assignment = await call<{ assignmentId: string }>(f.api, f.prefix + '/role-assignments', { principalId: f.member.principalId, roleId: role.roleId, resourceType: 'agent', resourceId: id, expectedRevision: await f.revision() });
    await signIn(page, f.memberLogin, f.memberPassword); await page.goto(`${f.path}/${id}/access`);
    await expect(page.getByRole('heading', { name: 'Who can chat?', exact: true })).toBeVisible();
    for (const name of ['Grant chat access', 'Grant execution role', 'Archive instance', 'New instance']) await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);
    await expect(page.getByRole('radio', { name: 'Organization', exact: true })).toHaveCount(0);
    await call(f.api, `${f.prefix}/role-assignments/${assignment.assignmentId}?expectedRevision=${await f.revision()}`, undefined, 'DELETE');
    await page.getByRole('button', { name: 'Reload access', exact: true }).click(); await expect(page.getByText(/You do not have permission/)).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Alex', exact: true })).toHaveCount(0); await expect(page.getByRole('region', { name: 'Who can chat' })).toHaveCount(0);
  } finally { await f.api.dispose(); }
});
