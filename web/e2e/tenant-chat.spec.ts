import { expect, test, request as requests, type APIRequestContext, type Page, type WebSocketRoute } from '@playwright/test';

// Public HTTP is fixture setup; all conversation commands below use visible product controls.
const login = 'tenant-chat-owner@example.test';
const password = 'synthetic-tenant-chat-password';
let fixtureIdentity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBeTruthy();
  return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) {
  await page.goto('/sign-in');
  await page.getByLabel('Email', { exact: true }).fill(email);
  await page.getByLabel('Password', { exact: true }).fill(secret);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: fixtureIdentity });
  const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Chat ${nonce}`, slug: `chat-${nonce}` });
  const prefix = `/api/v2/tenants/${tenant.tenantId}`;
  const revision = async () => (await call<{ revision: number }>(api, `${prefix}/access/me`)).revision;
  const agent = await call<{ instance: { instanceId: string } }>(api, `${prefix}/agents`, { templateId: 'examiner', version: 1, expectedRevision: await revision() });
  return { api, tenant: tenant.tenantId, name: `Chat ${nonce}`, prefix, revision, agent: agent.instance.instanceId, path: `/t/${tenant.tenantId}` };
}
async function send(page: Page, text: string) {
  await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toBeEnabled();
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill(text);
  await page.getByRole('button', { name: 'Send', exact: true }).click();
}
function watch(page: Page) {
  const errors: string[] = [], legacy: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => {
    const path = new URL(r.url()).pathname;
    if (/^\/api\/v[12]\/(owner-capability|sessions|agent-instances)(\/|$)/.test(path)) legacy.push(path);
  });
  return { errors, legacy };
}
async function reply(page: Page, count = 1) { await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(count); }

test.beforeAll(async () => {
  // One private setup identity avoids spending the production login limiter on fixture calls.
  // Browser sign-ins still create independent sessions; UI logout cannot revoke this identity.
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic');
    await call(api, '/api/v2/auth/login', { login, password }); fixtureIdentity = await api.storageState();
  } finally { await api.dispose(); }
});

test('tenant text, rendered receipt, reload, rename, archive and read-only completion', async ({ page }) => {
  const f = await fixture(); const observed = watch(page);
  try {
    await signIn(page); await page.goto(f.path);
    await expect(page.getByRole('button', { name: 'Voice', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add content', exact: true })).toHaveCount(0);
    await send(page, 'First private tenant message'); await reply(page);
    await expect(page).toHaveURL(new RegExp(`${f.path}/c/`));
    const savedUrl = page.url(), id = savedUrl.split('/').pop()!;
    await expect.poll(async () => {
      const h = await call<{ items: { role: string; receivedTextEndExclusive: number; heardTextEndExclusive: number }[] }>(f.api, `${f.prefix}/sessions/${id}/messages`);
      return h.items.filter(e => e.role === 'assistant').map(e => [e.receivedTextEndExclusive, e.heardTextEndExclusive]);
    }).toEqual([[21, 0]]);
    await page.reload(); await reply(page);
    await expect(page.getByText('First private tenant message', { exact: true })).toHaveCount(1);
    await page.getByRole('button', { name: /^Actions for / }).click();
    await page.getByRole('menuitem', { name: 'Rename', exact: true }).click();
    const title = page.getByRole('textbox', { name: 'Session title', exact: true });
    await title.fill('Renamed private conversation'); await title.press('Enter');
    await expect(page.getByRole('link', { name: 'Renamed private conversation', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Actions for Renamed private conversation', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Archive', exact: true }).click();
    await expect(page).toHaveURL(f.path);
    await expect(page.getByRole('link', { name: 'Renamed private conversation', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Chat list options', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Show archived', exact: true }).click();
    await page.getByRole('button', { name: 'Actions for Renamed private conversation', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Unarchive', exact: true }).click();
    await page.getByRole('link', { name: 'Renamed private conversation', exact: true }).click();
    await page.getByRole('button', { name: 'End', exact: true }).click();
    await page.getByRole('dialog', { name: 'End this conversation?', exact: true }).getByRole('button', { name: 'End', exact: true }).click();
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveCount(0);
    await expect.poll(async () => (await call<{ lifecycleStatus: string }>(f.api, `${f.prefix}/sessions/${id}`)).lifecycleStatus).toBe('completed');
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('organization switch and logout retire prior history, draft and transport', async ({ page }) => {
  const a = await fixture(), b = await fixture(); const observed = watch(page);
  try {
    await signIn(page); await page.goto(a.path); await send(page, 'Alpha private transcript'); await reply(page);
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Alpha unsent draft');
    await page.getByRole('button', { name: `Switch organization. Current: ${a.name}`, exact: true }).click();
    await page.getByRole('dialog', { name: 'Switch organization', exact: true }).getByRole('textbox', { name: /Search/ }).fill(b.name);
    await page.getByRole('button', { name: `Open ${b.name}`, exact: true }).click();
    await expect(page).toHaveURL(b.path);
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveValue('');
    await expect(page.locator('body')).not.toContainText('Alpha private transcript');
    await send(page, 'Beta private transcript'); await reply(page);
    const beta = page.url();
    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
    await page.goto(beta); await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
    await expect(page.locator('body')).not.toContainText('Beta private transcript');
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await a.api.dispose(); await b.api.dispose(); }
});

test('real websocket reconnect preserves unsent draft and does not duplicate admitted text', async ({ page }) => {
  const f = await fixture(); let socket: WebSocketRoute | undefined, connections = 0;
  await page.routeWebSocket('**/hubs/session*', route => { connections++; socket = route; route.connectToServer(); });
  try {
    await signIn(page); await page.goto(f.path); await send(page, 'Admitted before reconnect'); await reply(page);
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('My preserved draft');
    const initial = connections; await socket!.close({ code: 1012, reason: 'Synthetic reconnect verification' });
    await expect.poll(() => connections).toBeGreaterThan(initial);
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toBeEnabled();
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveValue('My preserved draft');
    await expect(page.getByText('Admitted before reconnect', { exact: true })).toHaveCount(1);
    await page.getByRole('button', { name: 'Send', exact: true }).click(); await reply(page, 2);
  } finally { await f.api.dispose(); }
});

test('local queue drains once and Stop cancels the delivered canonical Run', async ({ page }) => {
  const f = await fixture();
  let runPath = '';
  page.on('request', r => { const path = new URL(r.url()).pathname; if (r.method() === 'GET' && /\/agent-runs\/[0-9a-f-]+$/.test(path)) runPath = path; });
  try {
    await signIn(page); await page.goto(f.path);
    await send(page, '[test:durable-stream] queued first');
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Queued second');
    await page.getByRole('button', { name: 'Queue', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Queued messages', exact: true })).toBeVisible();
    await reply(page, 2);
    await expect(page.getByRole('region', { name: 'Queued messages', exact: true })).toHaveCount(0);
    await send(page, '[test:durable-stream-long] stop now');
    await page.getByRole('button', { name: 'Stop', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0);
    expect(runPath).not.toBe('');
    await expect.poll(async () => (await call<{ status: string }>(f.api, runPath)).status).toBe('cancelled');
    await send(page, 'After Stop'); await reply(page, 3);
  } finally { await f.api.dispose(); }
});

test('Chat-only member has no management controls and revoked Chat clears protected content', async ({ page }) => {
  const f = await fixture();
  try {
    const nonce = crypto.randomUUID().slice(0, 8), email = `chat-member-${nonce}@example.test`, secret = 'synthetic-chat-member-password';
    const role = await call<{ roleId: string }>(f.api, `${f.prefix}/roles`, { name: `Chat member ${nonce}`, permissions: ['sessions.read', 'sessions.create'], expectedRevision: await f.revision() });
    const invitation = await call<{ invitationId: string; token: string }>(f.api, `${f.prefix}/members/invite`, { login: email, roleId: role.roleId, expectedRevision: await f.revision() });
    await call(page.request, '/api/v2/auth/register', { login: email, displayName: 'Chat member', password: secret, invitation: { tenantId: f.tenant, invitationId: invitation.invitationId, token: invitation.token } });
    const members = await call<{ items: { principalId: string; login: string }[] }>(f.api, `${f.prefix}/members`);
    const principalId = members.items.find(m => m.login.toLowerCase() === email)!.principalId;
    const chat = await call<{ roleId: string }>(f.api, `${f.prefix}/roles`, { name: `Exact agent Chat ${nonce}`, permissions: ['agents.chat'], expectedRevision: await f.revision() });
    const grant = await call<{ assignmentId: string }>(f.api, `${f.prefix}/role-assignments`, { principalId, roleId: chat.roleId, resourceType: 'agent', resourceId: f.agent, expectedRevision: await f.revision() });
    const observed = watch(page); await signIn(page, email, secret); await page.goto(f.path);
    await send(page, 'Protected member transcript'); await reply(page);
    await expect(page.getByRole('button', { name: 'End', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^Actions for / })).toHaveCount(0);
    await call(f.api, `${f.prefix}/role-assignments/${grant.assignmentId}?expectedRevision=${await f.revision()}`, undefined, 'DELETE');
    await send(page, 'Keep my rejected draft');
    await expect(page.locator('body')).not.toContainText('Protected member transcript');
    await expect(page.getByRole('button', { name: 'Retry', exact: true })).toBeVisible();
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});
