import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';

const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy(); return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) {
  await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(email); await page.getByLabel('Password', { exact: true }).fill(secret);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Schedule ${nonce}`, slug: `schedule-${nonce}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
  const agent = await call<{ principalId: string; instance: { instanceId: string } }>(api, root + '/agents', { templateId: 'general-assistant', version: 32, expectedRevision: await revision() });
  const id = agent.instance.instanceId; const route = `/t/${tenant}/admin/agents/${id}/automations`, resource = root + `/agents/${id}/automations`;
  const role = await call<{ roleId: string }>(api, root + '/roles', { name: 'Independent scheduled executor', permissions: ['automations.run', 'sessions.create', 'agent-runs.create'], expectedRevision: await revision() });
  await call(api, root + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
  return { api, tenant, root, revision, id, route, resource };
}
function watch(page: Page) { const errors: string[] = [], legacy: string[] = []; page.on('pageerror', error => errors.push(error.message)); page.on('request', request => { if (/^\/api\/v[12]\/(owner-capability|agent-instances|admin)(\/|$)/.test(new URL(request.url()).pathname)) legacy.push(request.url()); }); return { errors, legacy }; }
test.beforeAll(async () => { const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); try { expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState(); } finally { await api.dispose(); } });

test('Schedule management preserves stale drafts, admits saved work and cancels registration at desktop and narrow sizes', async ({ page }, info) => {
  const f = await fixture(); const observed = watch(page);
  try {
    await page.setViewportSize({ width: 1440, height: 1000 }); await signIn(page); await page.goto(f.route);
    await page.getByRole('button', { name: 'New Automation', exact: true }).click(); const editor = page.getByRole('dialog', { name: 'New Automation', exact: true });
    await editor.getByRole('textbox', { name: 'Automation name', exact: true }).fill('Weekly research — planned and independently authorized');
    await editor.getByRole('textbox', { name: 'Automation instructions', exact: true }).fill('synthetic-automation-attention: check the requested progress and report the outcome');
    await editor.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(editor).toHaveCount(0);
    await expect(page.getByText('disabled', { exact: true })).toBeVisible(); await page.screenshot({ path: info.outputPath('automation-wide.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 }); await page.getByRole('button', { name: 'Edit Automation', exact: true }).click();
    const saved = page.getByRole('dialog', { name: 'Saved Automation', exact: true }); await saved.getByRole('textbox', { name: 'Automation name', exact: true }).fill('Retained exact schedule after concurrent change');
    const item = (await call<{ items: { automationId: string; revision: number }[] }>(f.api, f.resource)).items[0];
    await call(f.api, f.resource + `/${item.automationId}`, { enabled: false, name: 'Concurrent saved name', instructions: 'Concurrent saved instructions', schedule: { kind: 'daily', interval: 1, timeZone: 'UTC', localTime: '09:00' }, expectedRevision: await f.revision(), expectedInstanceRevision: 1, expectedAutomationRevision: item.revision }, 'PUT');
    await saved.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(saved.getByText(/Access has changed since/)).toBeVisible();
    await expect(saved.getByRole('button', { name: 'Save Automation', exact: true })).toBeDisabled();
    await saved.getByRole('button', { name: 'Reload current Automation', exact: true }).click(); await expect(saved.getByRole('textbox', { name: 'Automation name', exact: true })).toHaveValue('Retained exact schedule after concurrent change');
    await saved.getByRole('checkbox', { name: 'Enable scheduled occurrences', exact: true }).check();
    await saved.getByRole('spinbutton', { name: 'Schedule maximum occurrences', exact: true }).fill('10');
    await page.screenshot({ path: info.outputPath('automation-narrow-editor.png'), fullPage: true });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await saved.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(saved).toHaveCount(0); await expect(page.getByText('active', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Run now', exact: true }).click(); const confirmation = page.getByRole('dialog', { name: 'Run saved Automation now?', exact: true });
    await expect(confirmation).toContainText('Retained exact schedule'); await confirmation.getByRole('button', { name: 'Run now', exact: true }).click();
    const activity = page.getByRole('link', { name: 'Open the admitted Run in Activity', exact: true }); await expect(activity).toBeVisible();
    const run = new URL((await activity.getAttribute('href'))!, 'http://localhost').searchParams.get('run')!;
    await expect.poll(async () => (await call<{ status: string }>(f.api, f.root + `/agent-runs/${run}`)).status).toBe('completed');
    await page.getByRole('button', { name: 'Reload Automations', exact: true }).click();
    await page.getByRole('button', { name: 'Cancel Automation', exact: true }).click(); const cancel = page.getByRole('dialog', { name: 'Cancel this Automation?', exact: true });
    await expect(cancel).toContainText('Already admitted work keeps its own Run'); await cancel.getByRole('button', { name: 'Cancel Automation', exact: true }).click();
    await expect(page.getByText('cancelled', { exact: true })).toBeVisible(); await expect(page.getByRole('table').first()).toContainText('Next: Not scheduled'); expect((await call<{ status: string }>(f.api, f.root + `/agent-runs/${run}`)).status).toBe('completed');
    await activity.click(); await expect(page.getByRole('dialog', { name: 'Run details', exact: true })).toBeVisible();
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('scoped Automation reader has no mutation controls and read revocation retires the open detail', async ({ page }) => {
  const f = await fixture(); const observed = watch(page);
  try {
    const created = await call<{ automationId: string }>(f.api, f.resource, { enabled: false, name: 'Only this readable schedule', instructions: 'Protected saved objective', schedule: { kind: 'daily', interval: 1, timeZone: 'UTC', localTime: '09:00' }, expectedRevision: await f.revision(), expectedInstanceRevision: 1 });
    const nonce = crypto.randomUUID().slice(0, 8), email = `schedule-reader-${nonce}@example.test`, secret = 'synthetic-schedule-reader-password';
    const membership = await call<{ roleId: string }>(f.api, f.root + '/roles', { name: 'Membership only', permissions: ['sessions.read'], expectedRevision: await f.revision() });
    const invitation = await call<{ invitationId: string; token: string }>(f.api, f.root + '/members/invite', { login: email, roleId: membership.roleId, expectedRevision: await f.revision() });
    await call(page.request, '/api/v2/auth/register', { login: email, displayName: 'Schedule reader', password: secret, invitation: { tenantId: f.tenant, invitationId: invitation.invitationId, token: invitation.token } });
    const member = (await call<{ items: { login: string; principalId: string }[] }>(f.api, f.root + '/members')).items.find(m => m.login.toLowerCase() === email)!;
    const grant = async (name: string, permissions: string[], resourceType: string, resourceId: string) => { const role = await call<{ roleId: string }>(f.api, f.root + '/roles', { name, permissions, expectedRevision: await f.revision() }); return call<{ assignmentId: string }>(f.api, f.root + '/role-assignments', { principalId: member.principalId, roleId: role.roleId, resourceType, resourceId, expectedRevision: await f.revision() }); };
    await grant('Read exact agent', ['agents.read'], 'agent', f.id); const access = await grant('Read exact schedule', ['automations.read'], 'automation', created.automationId);
    await signIn(page, email, secret); await page.goto(f.route); await expect(page.getByRole('button', { name: 'New Automation', exact: true })).toBeDisabled();
    expect(await page.getByRole('button', { name: 'Run now', exact: true }).count()).toBe(0); expect(await page.getByRole('button', { name: 'Cancel Automation', exact: true }).count()).toBe(0);
    await page.getByRole('button', { name: 'Inspect Automation', exact: true }).click(); const detail = page.getByRole('dialog', { name: 'Saved Automation', exact: true });
    await expect(detail.getByRole('textbox', { name: 'Automation instructions', exact: true })).toBeDisabled(); await expect(detail.getByRole('button', { name: 'Save Automation', exact: true })).toBeDisabled();
    await call(f.api, f.root + `/role-assignments/${access.assignmentId}?expectedRevision=${await f.revision()}`, undefined, 'DELETE');
    await detail.getByRole('button', { name: 'Reload current Automation', exact: true }).click(); await expect(detail).toHaveCount(0); await expect(page.getByText('Protected saved objective', { exact: true })).toHaveCount(0);
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});
