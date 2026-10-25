import { canonicalFetch, grantPrivateSpaceRead, signInTenant } from './support/tenant-auth';
import { expect, test, request as requests, type APIRequestContext, type Page, type WebSocketRoute } from '@playwright/test';

// Public HTTP is fixture setup; all conversation commands below use visible product controls.
const login = 'tenant-chat-owner@example.test';
const password = 'synthetic-tenant-chat-password';
let fixtureIdentity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBeTruthy();
  return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) { await signInTenant(page, email, secret); }
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: fixtureIdentity });
  const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Chat ${nonce}`, slug: `chat-${nonce}` });
  const prefix = `/api/v2/tenants/${tenant.tenantId}`;
  const revision = async () => (await call<{ revision: number }>(api, `${prefix}/access/me`)).revision;
  const agent = await call<{ instance: { instanceId: string } }>(api, `${prefix}/agents`, { templateId: 'examiner', version: 1, expectedRevision: await revision() });
  await grantPrivateSpaceRead(api, tenant.tenantId, agent.instance.instanceId);
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
    if (r.headers()["x-agentcore-owner-capability"]) legacy.push("retired owner header");
    if (/^\/api\/v[12]\/(owner-capability|sessions|agent-instances)(\/|$)/.test(path)) legacy.push(path);
  });
  return { errors, legacy };
}
async function reply(page: Page, count = 1) { await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(count); }
async function steerWithFreshRetry(page: Page, action: () => Promise<void>, replies: number, text: string, queued: boolean) {
  await action();
  const refusal = page.getByText(/^(State changed\. Reload before trying again\.|That response changed\. Check the current response before steering again\.)$/);
  await expect.poll(async () => await page.getByText('Hello from synthetic.', { exact: true }).count() === replies
    ? 'accepted' : await refusal.count() > 0 ? 'refused' : 'pending').not.toBe('pending');
  if (await refusal.count() > 0) {
    // A definitive CAS refusal is safe. Verify retained input, then act as a human
    // making one deliberate fresh attempt; the product must not replay it itself.
    if (queued) await expect(page.getByRole('region', { name: 'Queued messages', exact: true })).toContainText(text);
    else await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveText(text);
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toBeEnabled();
    await action();
  }
  await reply(page, replies);
}

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
    await page.addInitScript(() => localStorage.setItem("agent-core.owner-capability", "retired-token-must-be-ignored"));
    await signIn(page); await page.goto(f.path);
    await expect(page.getByRole('button', { name: 'Voice', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add content', exact: true })).toBeVisible();
    expect(await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'))).toBeNull();
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
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveText('');
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

test('tenant attachment-only input retains the original file after reload', async ({ page }) => {
  const f = await fixture(); const observed = watch(page);
  try {
    await signIn(page); await page.goto(f.path);
    const name = 'attachment-only-tenant-proof.txt';
    await page.getByRole('button', { name: 'Add content', exact: true }).click();
    const chooser = page.waitForEvent('filechooser');
    await page.getByRole('menuitem', { name: /Attach file/ }).click();
    await (await chooser).setFiles({ name, mimeType: 'text/plain', buffer: Buffer.from('Owned attachment without a text task.') });
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveText('');
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeEnabled();
    await page.getByRole('button', { name: 'Send', exact: true }).click(); await reply(page);
    const original = page.locator('.entry-files').getByRole('group', { name, exact: true });
    await expect(original).toBeVisible(); await page.reload(); await expect(original).toBeVisible();
    await original.getByRole('button', { name: `Preview ${name}`, exact: true }).click();
    await expect(page.getByRole('dialog', { name, exact: true })).toContainText('Owned attachment without a text task.');
    await page.keyboard.press('Escape');
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
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
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveText('My preserved draft');
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
    await call(f.api, `${f.prefix}/agents/${f.agent}/self-management`, { expectedRevision: await f.revision(), expectedInstanceRevision: 1, overrides: { mode: { value: 'Assisted' }, scopes: { value: ['Settings'] } } }, 'PUT');
    const nonce = crypto.randomUUID().slice(0, 8), email = `chat-member-${nonce}@example.test`, secret = 'synthetic-chat-member-password';
    const role = await call<{ roleId: string }>(f.api, `${f.prefix}/roles`, { name: `Chat member ${nonce}`, permissions: ['sessions.read', 'sessions.create'], expectedRevision: await f.revision() });
    const invitation = await call<{ invitationId: string; token: string }>(f.api, `${f.prefix}/members/invite`, { login: email, roleId: role.roleId, expectedRevision: await f.revision() });
    await call(page.request, '/api/v2/auth/register', { login: email, displayName: 'Chat member', password: secret, invitation: { tenantId: f.tenant, invitationId: invitation.invitationId, token: invitation.token } });
    const members = await call<{ items: { principalId: string; login: string }[] }>(f.api, `${f.prefix}/members`);
    const principalId = members.items.find(m => m.login.toLowerCase() === email)!.principalId;
    await grantPrivateSpaceRead(f.api, f.tenant, f.agent, principalId);
    const chat = await call<{ roleId: string }>(f.api, `${f.prefix}/roles`, { name: `Exact agent Chat ${nonce}`, permissions: ['agents.chat'], expectedRevision: await f.revision() });
    const grant = await call<{ assignmentId: string }>(f.api, `${f.prefix}/role-assignments`, { principalId, roleId: chat.roleId, resourceType: 'agent', resourceId: f.agent, expectedRevision: await f.revision() });
    const observed = watch(page); await signIn(page, email, secret); await page.goto(f.path);
    await send(page, 'Protected member transcript'); await reply(page);
    await send(page, 'synthetic approval review');
    const minimal = page.getByRole('region', { name: 'Conversation approvals', exact: true });
    await expect(minimal.getByText('Waiting for review', { exact: true })).toBeVisible();
    await expect(minimal.getByRole('link', { name: 'Review details', exact: true })).toHaveCount(0);
    await expect(minimal.getByText('vi', { exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'End', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^Actions for / })).toHaveCount(0);
    await call(f.api, `${f.prefix}/role-assignments/${grant.assignmentId}?expectedRevision=${await f.revision()}`, undefined, 'DELETE');
    await send(page, 'Keep my rejected draft');
    await expect(page.locator('body')).not.toContainText('Protected member transcript');
    await expect(page.getByRole('button', { name: 'Retry', exact: true })).toBeVisible();
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

for (const decision of ['approve', 'reject'] as const) test(`original Chat recovers a durable ${decision} decision and remains usable while waiting`, async ({ page }) => {
  const f = await fixture(); const observed = watch(page);
  try {
    await call(f.api, `${f.prefix}/agents/${f.agent}/self-management`, { expectedRevision: await f.revision(), expectedInstanceRevision: 1, overrides: { mode: { value: 'Assisted' }, scopes: { value: ['Settings'] } } }, 'PUT');
    await signIn(page); await page.goto(f.path); await send(page, 'synthetic approval review');
    let panel = page.getByRole('region', { name: 'Conversation approvals', exact: true });
    await expect(panel.getByText('Waiting for review', { exact: true })).toBeVisible();
    if (decision === 'approve') {
      await page.route('**/sessions/*/approvals?*', route => route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ reasonCode: 'unavailable' }) }), { times: 1 });
      await panel.getByRole('button', { name: 'Refresh approvals', exact: true }).click();
      await expect(panel.getByRole('alert')).toBeVisible(); await expect(panel.getByText('Waiting for review', { exact: true })).toBeVisible();
      await panel.getByRole('button', { name: 'Refresh approvals', exact: true }).click(); await expect(panel.getByRole('alert')).toHaveCount(0);
    }
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0);
    const url = page.url(), id = url.split('/').pop()!;
    await page.reload(); await expect(panel.getByText('Waiting for review', { exact: true })).toBeVisible();
    await send(page, 'Independent work remains usable'); await reply(page);
    await panel.getByRole('link', { name: 'Review details', exact: true }).click();
    let detail = page.getByRole('dialog', { name: 'Approval details', exact: true });
    await expect(detail.getByRole('region', { name: 'Exact saved changes', exact: true })).toContainText('vi');
    expect(page.url()).toBe(url);
    if (decision === 'approve') {
      // A distinct, authenticated fixture session represents the authorized second device.
      const feed = await call<{ items: { requestId: string }[] }>(f.api, `${f.prefix}/sessions/${id}/approvals`);
      const saved = await call<{ preparedAction: { actionHash: string }; requestRevision: number }>(f.api, `${f.prefix}/approvals/${feed.items[0].requestId}`);
      await call(f.api, `${f.prefix}/approvals/${feed.items[0].requestId}/decision`, { decision, actionHash: saved.preparedAction.actionHash, expectedRevision: await f.revision(), expectedRequestRevision: saved.requestRevision });
      await detail.getByRole('button', { name: 'Refresh details', exact: true }).click();
    } else {
      await detail.getByRole('button', { name: 'Reject', exact: true }).click();
      await page.getByRole('dialog', { name: 'Reject?', exact: true }).getByRole('button', { name: 'Reject', exact: true }).click();
    }
    await detail.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(panel.getByRole('link', { name: 'Review details', exact: true })).toBeFocused();
    await expect(panel.getByText(decision === 'approve' ? 'Approved' : 'Rejected', { exact: true })).toBeVisible();
    if (decision === 'approve') await expect(panel.getByText('Applied', { exact: true })).toBeVisible();
    else await expect(panel.getByText('Applied', { exact: true })).toHaveCount(0);
    await page.reload(); await expect(panel.getByText(decision === 'approve' ? 'Applied' : 'Rejected', { exact: true })).toBeVisible();
    await expect(page.getByText('Independent work remains usable', { exact: true })).toHaveCount(1);
    if (decision === 'approve') {
      await page.getByRole('link', { name: 'Start a new chat', exact: true }).click();
      await send(page, 'New Chat after the approved configuration change'); await reply(page);
    }
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('tenant Steer atomically interrupts the current Run with row and keyboard controls', async ({ page }, testInfo) => {
  const f = await fixture(); const observed = watch(page);
  try {
    await page.setViewportSize({ width: 1440, height: 1000 }); await signIn(page); await page.goto(f.path);
    await send(page, '[test:durable-stream-long] original steer objective');
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
    const session = page.url().split('/').pop()!;
    const before = await call<{ items: { runId: string; status: string }[] }>(f.api, `${f.prefix}/agents/${f.agent}/activity/runs?page=0&pageSize=20`);
    const original = before.items.find(item => item.status === 'running')!; expect(original).toBeTruthy();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Steered exact human objective');
    await page.getByRole('button', { name: 'Queue', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Steer queued message 1' })).toBeEnabled();
    await page.screenshot({ path: testInfo.outputPath('steer-wide.png'), fullPage: true });
    await steerWithFreshRetry(page, () => page.getByRole('button', { name: 'Steer queued message 1' }).click(), 1, 'Steered exact human objective', true);
    await expect(page.getByRole('region', { name: 'Queued messages', exact: true })).toHaveCount(0);
    await expect.poll(async () => (await call<{ status: string }>(f.api, `${f.prefix}/agent-runs/${original.runId}`)).status).toBe('cancelled');
    const history = await call<{ items: { text: string; status: string; interruptReason?: string }[] }>(f.api, `${f.prefix}/sessions/${session}/messages?limit=100`);
    expect(history.items.filter(item => item.text === 'Steered exact human objective')).toHaveLength(1);
    expect(history.items.filter(item => item.status === 'interrupted')).toHaveLength(1);
    await page.setViewportSize({ width: 390, height: 844 });
    await send(page, '[test:durable-stream-long] narrow explicit interruption');
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Retain this unrelated queued objective');
    await page.getByRole('button', { name: 'Queue', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Steer queued message 1' })).toBeEnabled();
    await page.screenshot({ path: testInfo.outputPath('steer-narrow.png'), fullPage: true });
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Keyboard steered objective');
    await steerWithFreshRetry(page, () => page.getByRole('textbox', { name: 'Message', exact: true }).press('Control+Enter'), 3, 'Keyboard steered objective', false);
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0);
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.reload(); await reply(page, 3);
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('Chat permission permits Queue while Steer requires independent Run cancellation authority', async ({ page }) => {
  const f = await fixture(); const nonce = crypto.randomUUID().slice(0, 8), email = `steer-reader-${nonce}@example.test`, secret = 'synthetic-steer-reader-password';
  try {
    const role = await call<{ roleId: string }>(f.api, `${f.prefix}/roles`, { name: `Chat without cancellation ${nonce}`, permissions: ['sessions.read', 'sessions.create', 'agents.chat'], expectedRevision: await f.revision() });
    const invitation = await call<{ invitationId: string; token: string }>(f.api, `${f.prefix}/members/invite`, { login: email, roleId: role.roleId, expectedRevision: await f.revision() });
    await call(page.request, '/api/v2/auth/register', { login: email, displayName: 'Chat reader', password: secret, invitation: { tenantId: f.tenant, invitationId: invitation.invitationId, token: invitation.token } });
    const members = await call<{ items: { principalId: string; login: string }[] }>(f.api, `${f.prefix}/members`);
    await grantPrivateSpaceRead(f.api, f.tenant, f.agent, members.items.find(member => member.login.toLowerCase() === email)!.principalId);
    const observed = watch(page); await signIn(page, email, secret); await page.goto(f.path);
    await send(page, '[test:durable-stream-long] Chat-only live objective');
    // Queue is available before admission finishes. Wait for the held response's
    // visible prefix before collecting witnesses for the permission refusal.
    await expect(page.locator('[data-role="assistant"]').first()).toContainText('Hello');
    await expect(page.getByRole('button', { name: 'Queue', exact: true })).toBeVisible();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Retain my unprivileged queued objective');
    await page.getByRole('button', { name: 'Queue', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Steer queued message 1' })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0);
    const session = page.url().split('/').pop()!;
    let active: { runId: string; status: string; revision: number } | undefined;
    await expect.poll(async () => {
      active = (await call<{ items: { runId: string; status: string; revision: number }[] }>(f.api, `${f.prefix}/agents/${f.agent}/activity/runs?page=0&pageSize=20`)).items.find(item => item.status === 'running');
      return !!active;
    }).toBe(true);
    const meta = await call<{ revision: number }>(page.request, `${f.prefix}/sessions/${session}`), access = await call<{ revision: number }>(page.request, `${f.prefix}/access/me`);
    const csrf = await call<{ token: string }>(page.request, '/api/v2/auth/csrf');
    const denied = await page.request.post(`${f.prefix}/sessions/${session}/messages`, { headers: { 'X-AgentCore-CSRF': csrf.token }, data: {
      messageId: crypto.randomUUID(), text: 'Forbidden interruption', behavior: 'interrupt', interruptedRunId: active!.runId, expectedRunRevision: active!.revision, expectedRevision: access.revision, expectedSessionRevision: meta.revision
    } });
    expect(denied.status()).toBe(403);
    expect((await call<{ status: string }>(f.api, `${f.prefix}/agent-runs/${active!.runId}`)).status).toBe('running');
    await expect(page.getByRole('region', { name: 'Queued messages', exact: true })).toContainText('Retain my unprivileged queued objective');
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]); // The independent Run-read metadata request is expected to return 403.
  } finally { await f.api.dispose(); }
});

for (const width of [1440, 390]) test(`authenticated entry and retired browser routes at ${width}px`, async ({ page }) => {
  const f = await fixture(); const observed = watch(page); const ownerRequests: string[] = [];
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (path.includes('owner-capability') || /^\/api\/v[12]\/(admin|sessions|agent-instances)(\/|$)/.test(path)) ownerRequests.push(path);
  });
  await page.setViewportSize({ width, height: 900 });
  await page.addInitScript(() => localStorage.setItem('agent-core.owner-capability', 'obsolete-browser-entry-test'));
  try {
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
    await expect(page).toHaveURL(/\/sign-in\?notice=expired&returnTo=%2Faccount%2Forganizations$/);
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveCount(0);
    await signIn(page);
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
    for (const path of ['/admin', `/admin/instances/${f.agent}/activity/sessions`, `/c/${crypto.randomUUID()}`, '/unknown']) {
      await page.goto(path);
      await expect(page.getByRole('heading', { name: 'Page not found', exact: true })).toBeVisible();
      await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'New chat', exact: true })).toHaveCount(0);
    }
    await page.goto(`${f.path}/admin/agents`);
    await expect(page.getByRole('heading', { name: 'Instances', exact: true })).toBeVisible();
    if (width < 1024) await page.getByRole('button', { name: 'Open Admin navigation', exact: true }).click();
    await page.getByRole('navigation', { name: 'Admin navigation', exact: true }).getByRole('link', { name: 'Roles & permissions', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Roles & permissions', exact: true })).toBeVisible();
    if (width < 1024) await page.getByRole('button', { name: 'Open Admin navigation', exact: true }).click();
    await page.getByRole('navigation', { name: 'Admin navigation', exact: true }).getByRole('link', { name: 'Instances', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Instances', exact: true })).toBeVisible();
    await page.getByRole('link', { name: 'Chat', exact: true }).click();
    await expect(page).toHaveURL(f.path);
    await send(page, 'Canonical entry remains usable'); await reply(page);
    expect(observed.errors).toEqual([]); expect(ownerRequests).toEqual([]);
  } finally { await f.api.dispose(); }
});
