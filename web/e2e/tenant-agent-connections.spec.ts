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
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Connections ${nonce}`, slug: `connections-${nonce}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
  const agent = await call<{ principalId: string; instance: { instanceId: string } }>(api, root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() });
  const id = agent.instance.instanceId, path = `/t/${tenant}/admin/agents/${id}/connections`, bindings = root + `/agents/${id}/credential-bindings`;
  const credential = async (displayName: string) => (await call<{ credential: { credentialId: string } }>(api, root + '/credentials', { displayName, kind: 'Password', purpose: 'General', protectedValue: 'synthetic-binding-private-value', expectedRevision: await revision() })).credential.credentialId;
  const role = (name: string, permissions: string[]) => revision().then(expectedRevision => call<{ roleId: string }>(api, root + '/roles', { name, permissions, expectedRevision }));
  const grant = (principalId: string, roleId: string, resourceType: string, resourceId: string) => revision().then(expectedRevision => call<{ assignmentId: string }>(api, root + '/role-assignments', { principalId, roleId, resourceType, resourceId, expectedRevision }));
  const revoke = (assignmentId: string) => revision().then(expectedRevision => call(api, `${root}/role-assignments/${assignmentId}?expectedRevision=${expectedRevision}`, undefined, 'DELETE'));
  return { api, tenant, root, id, path, bindings, agent, revision, credential, role, grant, revoke };
}
test.beforeAll(async () => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try { expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState(); } finally { await api.dispose(); }
});

test('paged binding draft, focus/discard, normalized save, stale removal recovery and archived inspection at 320px', async ({ page }) => {
  const f = await fixture(); const errors: string[] = [], legacy: string[] = []; page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (/^\/api\/v[12]\/(owner-capability|agent-instances|admin)(\/|$)/.test(new URL(request.url()).pathname)) legacy.push(request.url()); });
  try {
    for (let index = 0; index < 10; index++) await f.credential(`A account ${index}`);
    const credential = await f.credential('Z Operations account — long regional coordination account');
    await page.setViewportSize({ width: 320, height: 740 }); await signIn(page); await page.goto(f.path);
    await page.getByRole('button', { name: 'Add binding', exact: true }).click();
    await page.getByRole('dialog', { name: 'Add credential binding', exact: true }).getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Add binding', exact: true })).toBeFocused();
    await page.getByRole('button', { name: 'Add binding', exact: true }).click();
    let drawer = page.getByRole('dialog', { name: 'Add credential binding', exact: true });
    await drawer.getByLabel('Alias', { exact: true }).fill(' Primary-Operations ');
    await drawer.getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByRole('dialog', { name: 'Discard unsaved changes?' }).getByRole('button', { name: 'Keep editing', exact: true }).click();
    await drawer.getByTitle('2', { exact: true }).click();
    await drawer.getByLabel('Credential', { exact: true }).click(); await page.getByTitle('Z Operations account — long regional coordination account · General credential', { exact: true }).click();
    await drawer.getByRole('button', { name: 'Save binding', exact: true }).click();
    await expect(drawer).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Saved credential bindings' }).getByText('primary-operations', { exact: true })).toBeVisible();
    expect((await call<{ allowed: boolean }>(f.api, f.root + '/access/check', { principalId: f.agent.principalId, permission: 'credentials.use', resourceType: 'credential', resourceId: credential })).allowed).toBe(false);
    await page.getByRole('heading', { name: 'Connections', exact: true }).hover();
    await f.role('Concurrent security change', ['agents.read']);
    await page.getByRole('button', { name: 'Remove binding primary-operations', exact: true }).click();
    let confirmation = page.getByRole('dialog', { name: 'Remove credential binding?', exact: true });
    for (const text of ['Agent: Alex', 'Alias: primary-operations', 'Z Operations account', `Organization: Connections`]) await expect(confirmation).toContainText(text);
    await confirmation.getByRole('button', { name: 'Remove binding', exact: true }).click();
    await expect(page.getByText(/Access has changed since you opened/)).toBeVisible();
    expect((await call<{ total: number }>(f.api, f.bindings)).total).toBe(1);
    await page.getByRole('button', { name: 'Reload connections', exact: true }).click();
    await page.getByRole('button', { name: 'Remove binding primary-operations', exact: true }).click();
    await page.getByRole('dialog', { name: 'Remove credential binding?', exact: true }).getByRole('button', { name: 'Remove binding', exact: true }).click();
    await expect(page.getByText('No credential bindings you can read.', { exact: true })).toBeVisible();
    expect((await f.api.get(f.root + `/credentials/${credential}`)).ok()).toBe(true);
    const current = await call<{ instance: { revision: number } }>(f.api, f.root + `/agents/${f.id}`);
    await call(f.api, f.root + `/agents/${f.id}/lifecycle`, { lifecycle: 1, expectedRevision: await f.revision(), expectedInstanceRevision: current.instance.revision }, 'PUT');
    await page.getByRole('button', { name: 'Reload connections', exact: true }).click(); await expect(page.getByText('Archived instance', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Add binding', exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(errors).toEqual([]); expect(legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('exact scoped configuration manager needs no IAM inspector, retains stale draft and loses protected view after revocation', async ({ page }) => {
  const f = await fixture();
  try {
    const visible = await f.credential('Scoped shared account'), readOnly = await f.credential('Readable account without binding authority'); await f.credential('Hidden unrelated account');
    const memberRole = await f.role('Minimal member', ['sessions.read']), parent = await f.role('Exact agent configuration', ['agents.read', 'agents.update']);
    const bind = await f.role('Exact credential binding', ['credentials.read', 'credentials.bind']), read = await f.role('Exact credential reader', ['credentials.read']);
    const memberLogin = `binding-manager-${crypto.randomUUID().slice(0, 8)}@example.test`, memberPassword = 'synthetic-binding-manager-password';
    const invitation = await call<{ invitationId: string; token: string }>(f.api, f.root + '/members/invite', { login: memberLogin, roleId: memberRole.roleId, expectedRevision: await f.revision() });
    const registration = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
    try { await call(registration, '/api/v2/auth/register', { login: memberLogin, displayName: 'Scoped connection manager', password: memberPassword, invitation: { tenantId: f.tenant, invitationId: invitation.invitationId, token: invitation.token } }); } finally { await registration.dispose(); }
    const member = (await call<{ items: { principalId: string; displayName: string }[] }>(f.api, f.root + '/members')).items.find(item => item.displayName === 'Scoped connection manager')!;
    const parentGrant = await f.grant(member.principalId, parent.roleId, 'agent', f.id); await f.grant(member.principalId, bind.roleId, 'credential', visible); await f.grant(member.principalId, read.roleId, 'credential', readOnly);
    await signIn(page, memberLogin, memberPassword); await page.goto(f.path);
    await expect(page.getByText('Only bindings to credentials you can read are shown.', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Add binding', exact: true }).click(); const drawer = page.getByRole('dialog', { name: 'Add credential binding', exact: true });
    await drawer.getByLabel('Credential', { exact: true }).click(); await expect(page.getByTitle('Hidden unrelated account · General credential', { exact: true })).toHaveCount(0);
    await page.getByTitle('Readable account without binding authority · General credential', { exact: true }).click();
    await expect(drawer.getByText('Bind access is required', { exact: true })).toBeVisible(); await expect(drawer.getByRole('button', { name: 'Save binding', exact: true })).toBeDisabled();
    await drawer.getByLabel('Credential', { exact: true }).click(); await page.getByTitle('Scoped shared account · General credential', { exact: true }).click(); await drawer.getByLabel('Alias', { exact: true }).fill('retained-shared');
    await f.role('Concurrent separate change', ['agents.read']);
    await drawer.getByRole('button', { name: 'Save binding', exact: true }).click(); await expect(drawer.getByText(/Access has changed since you opened/)).toBeVisible();
    expect((await call<{ total: number }>(f.api, f.bindings)).total).toBe(0); await expect(drawer.getByLabel('Alias', { exact: true })).toHaveValue('retained-shared');
    await drawer.getByRole('button', { name: 'Reload latest', exact: true }).click(); await expect(drawer.getByLabel('Alias', { exact: true })).toHaveValue('retained-shared');
    await drawer.getByRole('button', { name: 'Save binding', exact: true }).click(); await expect(drawer).toHaveCount(0);
    await expect(page.getByText('retained-shared', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Remove binding retained-shared', exact: true })).toBeVisible();
    await page.getByRole('heading', { name: 'Connections', exact: true }).hover();
    await page.getByRole('navigation', { name: 'Admin navigation', exact: true }).getByRole('link', { name: 'Access', exact: true }).click();
    await expect(page.getByText(/You do not have permission/)).toBeVisible();
    await page.getByRole('navigation', { name: 'Admin navigation', exact: true }).getByRole('link', { name: 'Connections', exact: true }).click();
    await expect(page.getByText('retained-shared', { exact: true })).toBeVisible();
    await f.revoke(parentGrant.assignmentId); await page.getByRole('button', { name: 'Reload connections', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Browser state', exact: true }).getByText(/You do not have permission/)).toBeVisible(); await expect(page.getByText('retained-shared', { exact: true })).toHaveCount(0); await expect(page.getByRole('heading', { name: 'Alex', exact: true })).toHaveCount(0);
  } finally { await f.api.dispose(); }
});
