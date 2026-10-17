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
async function navigateTo(page: Page, name: 'Profile' | 'Settings') {
  const toggle = page.getByRole('button', { name: 'Open Admin navigation', exact: true });
  if (await toggle.isVisible()) await toggle.click();
  await page.getByRole('link', { name, exact: true }).click();
}
type Editor = { securityRevision: number; agent: { instance: { revision: number; personaRevision: number; persona: { name: string; role: string; description: string; tone: string } } }; settings: { section: string; overrides: Record<string, unknown>; effective: Record<string, unknown>; sources: Record<string, string> }[] };
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Editor ${nonce}`, slug: `editor-${nonce}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
  const agent = await call<{ instance: { instanceId: string } }>(api, root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() });
  const id = agent.instance.instanceId, route = `/t/${tenant}/admin/agents/${id}`, resource = root + `/agents/${id}`;
  const read = () => call<Editor>(api, resource + '/editor');
  const patch = async (section: string, set: Record<string, unknown>, clear: string[] = []) => { const current = await read(); return call(api, resource + `/settings/${section}`, { expectedRevision: current.securityRevision, expectedInstanceRevision: current.agent.instance.revision, set, clear }, 'PATCH'); };
  return { api, tenant, root, route, resource, id, revision, read, patch };
}
test.beforeAll(async () => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try { expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState(); } finally { await api.dispose(); }
});

test('320px Profile form/JSON, guarded navigation, stale save and independent settings save/reset use exact tenant authority', async ({ page }) => {
  const f = await fixture(); const errors: string[] = [], legacy: string[] = []; page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => { if (/^\/api\/v[12]\/(owner-capability|agent-instances|admin)(\/|$)/.test(new URL(r.url()).pathname)) legacy.push(r.url()); });
  try {
    await page.setViewportSize({ width: 320, height: 740 }); await signIn(page); await page.goto(f.route + '/profile');
    await page.getByRole('textbox', { name: 'Persona name', exact: true }).fill('Regional operations and customer support');
    await navigateTo(page, 'Settings');
    await page.getByRole('dialog', { name: 'Discard unsaved changes?' }).getByRole('button', { name: 'Keep editing', exact: true }).click();
    await page.getByRole('tab', { name: 'JSON', exact: true }).click(); const json = page.getByRole('textbox', { name: 'Persona JSON', exact: true });
    expect(JSON.parse(await json.inputValue()).name).toBe('Regional operations and customer support');
    const value = await json.inputValue(); await json.fill('null'); await page.getByRole('button', { name: 'Save persona', exact: true }).click(); await expect(page.getByText(/Persona JSON must be an object/)).toBeVisible(); await json.fill(value);
    await f.patch('conversationPolicy', { language: 'de' });
    await page.getByRole('button', { name: 'Save persona', exact: true }).click(); await expect(page.getByText(/Access has changed since you opened/)).toBeVisible();
    await page.getByRole('button', { name: 'Reload profile', exact: true }).click(); await expect(json).toHaveValue(value);
    await page.getByRole('button', { name: 'Save persona', exact: true }).click(); await expect(page.getByRole('button', { name: 'Save persona', exact: true })).toBeDisabled();
    expect((await f.read()).agent.instance.persona.name).toBe('Regional operations and customer support');
    await navigateTo(page, 'Settings');
    await page.getByRole('button', { name: /^Conversation(?: Conversation: Instance override)?$/ }).click(); await page.getByRole('textbox', { name: 'Language', exact: true }).fill('fr-FR');
    await page.getByRole('button', { name: 'Behavior', exact: true }).click(); await page.getByRole('switch', { name: 'Acknowledge interruption', exact: true }).click();
    await page.getByRole('button', { name: 'Save Conversation', exact: true }).click(); await expect(page.getByRole('button', { name: 'Save Conversation', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Save Behavior', exact: true })).toBeEnabled();
    let state = await f.read(); expect(state.settings.find(s => s.section === 'conversationPolicy')?.effective.language).toBe('fr-FR'); expect(state.settings.find(s => s.section === 'behaviorPolicy')?.effective.acknowledgeInterruption).toBe(true);
    await page.getByRole('button', { name: 'Reset Conversation to Definition', exact: true }).click();
    const reset = page.getByRole('dialog', { name: 'Reset Conversation?' }); await expect(reset).toContainText('Regional operations and customer support'); await reset.getByRole('button', { name: 'Reset section', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Reset Conversation to Definition', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Save Behavior', exact: true })).toBeEnabled(); state = await f.read();
    expect(state.settings.find(s => s.section === 'conversationPolicy')?.sources.language).toBe('definition'); expect(state.settings.find(s => s.section === 'conversationPolicy')?.overrides).not.toHaveProperty('language');
    await page.getByRole('button', { name: 'Discard Behavior changes', exact: true }).click();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(errors).toEqual([]); expect(legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('exact scoped reader needs no IAM inspection and loses protected Profile after revocation', async ({ page }) => {
  const f = await fixture();
  try {
    const role = async (name: string, permissions: string[]) => call<{ roleId: string }>(f.api, f.root + '/roles', { name, permissions, expectedRevision: await f.revision() });
    const minimal = await role('Minimal membership', ['sessions.read']), reader = await role('Exact configuration reader', ['agents.read']);
    const email = `editor-reader-${crypto.randomUUID().slice(0, 8)}@example.test`, secret = 'synthetic-editor-reader-password';
    const invitation = await call<{ invitationId: string; token: string }>(f.api, f.root + '/members/invite', { login: email, roleId: minimal.roleId, expectedRevision: await f.revision() });
    const registration = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
    try { await call(registration, '/api/v2/auth/register', { login: email, displayName: 'Exact editor reader', password: secret, invitation: { tenantId: f.tenant, invitationId: invitation.invitationId, token: invitation.token } }); } finally { await registration.dispose(); }
    const member = (await call<{ items: { principalId: string; displayName: string }[] }>(f.api, f.root + '/members')).items.find(item => item.displayName === 'Exact editor reader')!;
    const assignment = await call<{ assignmentId: string }>(f.api, f.root + '/role-assignments', { principalId: member.principalId, roleId: reader.roleId, resourceType: 'agent', resourceId: f.id, expectedRevision: await f.revision() });
    await signIn(page, email, secret); await page.goto(f.route + '/settings'); await expect(page.getByText('Read-only configuration', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Conversation', exact: true }).click(); await expect(page.getByRole('textbox', { name: 'Language', exact: true })).toBeDisabled();
    await page.getByRole('link', { name: 'Profile', exact: true }).click(); await expect(page.getByRole('textbox', { name: 'Persona name', exact: true })).toBeDisabled(); await expect(page.getByRole('button', { name: 'Save persona', exact: true })).toHaveCount(0);
    await call(f.api, `${f.root}/role-assignments/${assignment.assignmentId}?expectedRevision=${await f.revision()}`, undefined, 'DELETE');
    await page.getByRole('button', { name: 'Reload profile', exact: true }).click(); await expect(page.getByRole('textbox', { name: 'Persona name', exact: true })).toHaveCount(0); await expect(page.getByRole('alert')).toBeVisible();
  } finally { await f.api.dispose(); }
});
