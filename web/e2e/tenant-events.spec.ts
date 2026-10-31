import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
import { canonicalFetch, grantPrivateSpaceRead, signInTenant } from './support/tenant-auth';

const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const response = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy(); return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) { await signInTenant(page, email, secret); }
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Events ${nonce}`, slug: `events-${nonce}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
  return { api, tenant, root, revision, route: `/t/${tenant}/integrations/events`, resource: root + '/webhooks' };
}
function watch(page: Page) { const errors: string[] = [], legacy: string[] = []; page.on('pageerror', error => errors.push(error.message)); page.on('request', request => { if (/^\/api\/v[12]\/(owner-capability|admin)(\/|$)/.test(new URL(request.url()).pathname)) legacy.push(request.url()); }); return { errors, legacy }; }
test.beforeAll(async () => { const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); try { expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState(); } finally { await api.dispose(); } });

test('webhook management preserves exact source drafts and distinguishes receipt acceptance from execution across native widths', async ({ page }, info) => {
  const f = await fixture(); const observed = watch(page); const sender = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    await page.setViewportSize({ width: 1440, height: 1000 }); await signIn(page); await page.goto(f.route);
    await page.getByRole('button', { name: 'New webhook', exact: true }).click(); const editor = page.getByRole('dialog', { name: 'New webhook', exact: true });
    await editor.getByRole('textbox', { name: 'Webhook name', exact: true }).fill('Customer orders — nguồn dữ liệu bên ngoài'); await editor.getByRole('textbox', { name: 'Webhook event key', exact: true }).fill('customer.orders');
    await editor.getByRole('button', { name: 'Create webhook', exact: true }).click(); await expect(editor).toHaveCount(0);
    const tokenInput = page.getByLabel('New webhook token', { exact: true }); await expect(tokenInput).toHaveAttribute('type', 'password'); const initialToken = await tokenInput.inputValue();
    expect(await page.evaluate(secret => Object.values(localStorage).some(value => String(value).includes(secret)), initialToken)).toBe(false);
    const source = (await call<{ items: { resourceId: string; ingressPath: string; revision: number }[] }>(f.api, f.resource)).items[0];
    const send = (token: string, id: string, value = 'Đơn hàng 123') => sender.post(source.ingressPath, { headers: { Authorization: `Bearer ${token}` }, data: { eventId: id, data: { orderReference: value, note: '<script>literal external data</script>' } } });
    const accepted = await send(initialToken, 'external-1'); expect(accepted.status()).toBe(202); const fact = await accepted.json();
    expect((await send(initialToken, 'external-1')).status()).toBe(200); expect((await send(initialToken, 'external-1', 'changed')).status()).toBe(409);
    await page.getByRole('button', { name: 'Dismiss token', exact: true }).click(); await page.getByRole('button', { name: 'Inspect webhook', exact: true }).click(); const detail = page.getByRole('dialog', { name: 'Webhook details', exact: true });
    await expect(detail.getByRole('textbox', { name: 'Webhook event key', exact: true })).toBeDisabled(); await detail.getByRole('button', { name: 'Inspect receipt data', exact: true }).click();
    expect(await detail.getByRole('textbox', { name: 'Untrusted webhook receipt data', exact: true }).inputValue()).toContain('<script>literal external data</script>');
    expect(await detail.locator('script').count()).toBe(0);
    for (const width of [1440, 1024, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 1000 }); await expect.poll(async () => { const r = await detail.boundingBox(); return !!r && r.x >= -1 && r.x + r.width <= width + 1; }).toBe(true);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); await page.screenshot({ path: info.outputPath(`webhook-detail-${width}.png`), fullPage: true });
    }
    await page.setViewportSize({ width: 390, height: 844 }); await detail.getByRole('textbox', { name: 'Webhook name', exact: true }).fill('Retained rename after a concurrent source change');
    await call(f.api, f.resource + `/${source.resourceId}`, { displayName: 'Concurrent saved source name', expectedRevision: await f.revision(), expectedSourceRevision: 1 }, 'PUT');
    await detail.getByRole('button', { name: /Save name$/ }).click(); await expect(detail.getByText(/Access has changed since/)).toBeVisible(); await expect(detail.getByRole('button', { name: /Save name$/ })).toBeDisabled();
    await detail.getByRole('button', { name: 'Reload webhook', exact: true }).click(); await expect(detail.getByRole('textbox', { name: 'Webhook name', exact: true })).toHaveValue('Retained rename after a concurrent source change');
    await detail.getByRole('button', { name: /Save name$/ }).click(); await expect(detail).toHaveCount(0);
    await page.getByRole('button', { name: 'Inspect webhook', exact: true }).click(); const current = page.getByRole('dialog', { name: 'Webhook details', exact: true });
    await current.getByRole('button', { name: 'Rotate token', exact: true }).click(); const rotate = page.getByRole('dialog', { name: 'Rotate this webhook token?', exact: true }); await expect(rotate).toContainText('Reconfigure the external sender'); await rotate.getByRole('button', { name: 'Rotate token', exact: true }).click();
    await expect(tokenInput).toHaveAttribute('type', 'password'); const rotated = await tokenInput.inputValue(); expect((await send(initialToken, 'external-2')).status()).toBe(401);
    expect((await send(rotated, 'external-1')).status()).toBe(200); expect((await send(rotated, 'external-2')).status()).toBe(202);
    await page.getByRole('button', { name: 'Dismiss token', exact: true }).click(); await page.getByRole('button', { name: 'Inspect webhook', exact: true }).click();
    await current.getByRole('button', { name: 'Revoke webhook', exact: true }).click(); const revoke = page.getByRole('dialog', { name: 'Revoke this webhook?', exact: true }); await expect(revoke).toContainText('does not cancel already admitted work'); await revoke.getByRole('button', { name: 'Revoke webhook', exact: true }).click();
    await expect(page.getByText('revoked', { exact: true })).toBeVisible(); expect((await send(rotated, 'external-3')).status()).toBe(401);
    await page.getByRole('button', { name: 'Inspect webhook', exact: true }).click(); await current.getByRole('button', { name: 'Issue new token and activate', exact: true }).click();
    const activate = page.getByRole('dialog', { name: 'Activate this webhook with a new token?', exact: true }); await expect(activate).toContainText('Reactivate incoming receipts'); await activate.getByRole('button', { name: 'Activate source', exact: true }).click();
    await expect(tokenInput).toBeVisible(); const restored = await tokenInput.inputValue(); expect((await send(rotated, 'external-3')).status()).toBe(401); expect((await send(restored, 'external-3')).status()).toBe(202);
    const facts = await call<{ items: { eventId: string }[]; total: number }>(f.api, f.resource + `/${source.resourceId}/receipts`); expect(facts.total).toBe(3); expect(facts.items.some(item => item.eventId === fact.eventId)).toBe(true);
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await sender.dispose(); await f.api.dispose(); }
});

test('exact webhook read scope filters records and revocation retires protected receipt details', async ({ page }) => {
  const f = await fixture(); const observed = watch(page);
  try {
    const created = await call<{ source: { resourceId: string } }>(f.api, f.resource, { displayName: 'Only this readable source', eventKey: 'readable.source', expectedRevision: await f.revision() });
    await call(f.api, f.resource, { displayName: 'Unreadable source', eventKey: 'private.source', expectedRevision: await f.revision() });
    const nonce = crypto.randomUUID().slice(0, 8), email = `event-reader-${nonce}@example.test`, secret = 'synthetic-event-reader-password';
    const membership = await call<{ roleId: string }>(f.api, f.root + '/roles', { name: 'Membership only', permissions: ['sessions.read'], expectedRevision: await f.revision() });
    const invite = await call<{ invitationId: string; token: string }>(f.api, f.root + '/members/invite', { login: email, roleId: membership.roleId, expectedRevision: await f.revision() });
    await call(page.request, '/api/v2/auth/register', { login: email, displayName: 'Exact event reader', password: secret, invitation: { tenantId: f.tenant, invitationId: invite.invitationId, token: invite.token } });
    const member = (await call<{ items: { login: string; principalId: string }[] }>(f.api, f.root + '/members')).items.find(m => m.login.toLowerCase() === email)!;
    const role = await call<{ roleId: string }>(f.api, f.root + '/roles', { name: 'Exact source reads', permissions: ['webhooks.read'], expectedRevision: await f.revision() });
    const grant = await call<{ assignmentId: string }>(f.api, f.root + '/role-assignments', { principalId: member.principalId, roleId: role.roleId, resourceType: 'webhook', resourceId: created.source.resourceId, expectedRevision: await f.revision() });
    await signIn(page, email, secret); await page.goto(f.route); await expect(page.getByText('Only this readable source', { exact: true })).toBeVisible(); expect(await page.getByText('Unreadable source', { exact: true }).count()).toBe(0);
    await expect(page.getByRole('button', { name: 'New webhook', exact: true })).toBeDisabled(); await page.getByRole('button', { name: 'Inspect webhook', exact: true }).click(); const detail = page.getByRole('dialog', { name: 'Webhook details', exact: true });
    await expect(detail.getByRole('textbox', { name: 'Webhook name', exact: true })).toBeDisabled(); expect(await detail.getByRole('button', { name: 'Rotate token', exact: true }).count()).toBe(0);
    await call(f.api, f.root + `/role-assignments/${grant.assignmentId}?expectedRevision=${await f.revision()}`, undefined, 'DELETE');
    await detail.getByRole('button', { name: 'Reload webhook', exact: true }).click(); await expect(detail).toHaveCount(0); await expect(page.getByText('Only this readable source', { exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Reload Events', exact: true })).toBeFocused(); expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('typed subscriptions test filters, preserve child identities, and separate receipt approval from Run admission', async ({ page }) => {
  const f = await fixture(); const observed = watch(page);
  try {
    const agent = await call<{ principalId: string; instance: { instanceId: string; revision: number } }>(f.api, f.root + '/agents', { templateId: 'general-assistant', version: 36, expectedRevision: await f.revision() });
    await grantPrivateSpaceRead(f.api, f.tenant, agent.instance.instanceId);
    const instance = agent.instance.instanceId; const resource = f.root + `/agents/${instance}/automations`;
    const role = await call<{ roleId: string }>(f.api, f.root + '/roles', { name: 'Independent event executor', permissions: ['automations.run', 'sessions.create', 'agent-runs.create', 'webhooks.read', 'core-events.read'], expectedRevision: await f.revision() });
    await call(f.api, f.root + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await f.revision() });
    const source = await call<{ source: { resourceId: string; ingressPath: string }; token: string }>(f.api, f.resource, { displayName: 'Typed order source', eventKey: 'typed.orders', expectedRevision: await f.revision() });
    await call(f.api, f.root + '/operation-policies/agent-runs.create', { mode: 'always', independentReview: false, expectedRevision: await f.revision(), expectedPolicyRevision: 0 }, 'PUT');
    await signIn(page); await page.goto(`/t/${f.tenant}/admin/agents/${instance}/automations`);
    await page.getByRole('button', { name: 'New Automation', exact: true }).click(); const d = page.getByRole('dialog', { name: 'New Automation', exact: true });
    await d.getByRole('textbox', { name: 'Automation name', exact: true }).fill('Typed order and configuration events');
    await d.getByRole('textbox', { name: 'Automation instructions', exact: true }).fill('Inspect only the captured event facts.');
    const choose = async (name: string, option: string) => { await d.getByRole('combobox', { name, exact: true }).click(); await page.locator('.ant-select-dropdown:visible .ant-select-item-option-content').getByText(option, { exact: true }).click(); };
    await choose('Automation trigger type', 'Events'); await choose('Subscription 1 source family', 'Webhook connection'); await choose('Subscription 1 event source', 'Typed order source · typed.orders');
    const first = d.getByRole('group', { name: 'Event subscription 1', exact: true });
    await first.getByRole('textbox', { name: 'Event filter expression', exact: true }).fill("event.data.status === 'ready'");
    await first.getByRole('textbox', { name: 'Sample event JSON', exact: true }).fill('{"schemaVersion":1,"data":{"status":"ready"}}'); await first.getByRole('button', { name: 'Test filter', exact: true }).click(); await expect(first.getByText('Matched', { exact: true })).toBeVisible();
    await first.getByRole('textbox', { name: 'Event filter expression', exact: true }).fill("event.data.status = 'ready'"); await first.getByRole('button', { name: 'Test filter', exact: true }).click(); await expect(first.getByText('Filter error', { exact: true })).toBeVisible();
    await first.getByRole('textbox', { name: 'Event filter expression', exact: true }).fill("event.data.status === 'ready'");
    await d.getByRole('button', { name: 'Add event subscription', exact: true }).click(); await choose('Subscription 2 event source', 'instance.config_changed');
    await d.getByRole('checkbox', { name: 'Enable automatic occurrences', exact: true }).check(); await d.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(d).toHaveCount(0);
    const saved = (await call<{ items: { automation: { automationId: string; triggers: { triggerId: string; revision: number; source: { kind: string }; filterExpression: string | null }[] } }[] }>(f.api, resource + '/editor')).items[0].automation;
    expect(saved.triggers).toHaveLength(2); const child = saved.triggers.find(t => t.source.kind === 'webhook')!; expect(child.filterExpression).toBe("event.data.status === 'ready'");
    const sent = await f.api.post(source.source.ingressPath, { headers: { Authorization: `Bearer ${source.token}` }, data: { eventId: 'typed-e2e', data: { status: 'ready', note: '<script>literal</script>' } } }); expect(sent.status()).toBe(202);
    await expect.poll(async () => (await call<{ items: { status: string }[] }>(f.api, resource + `/${saved.automationId}/events`)).items[0]?.status).toBe('awaitingAdmission');
    await page.getByRole('button', { name: 'Edit Automation', exact: true }).click(); const current = page.getByRole('dialog', { name: 'Saved Automation', exact: true }); await current.getByRole('tab', { name: 'Event Activity', exact: true }).click();
    await expect(current.getByText('Waiting for admission', { exact: true })).toBeVisible(); await expect(current.getByRole('link', { name: 'Open Run in Activity', exact: true })).toHaveCount(0);
    await current.getByRole('button', { name: 'Inspect received data', exact: true }).click(); expect(await current.getByRole('textbox', { name: 'Received data', exact: true }).inputValue()).toContain('<script>literal</script>'); expect(await current.locator('script').count()).toBe(0);
    await current.getByRole('link', { name: 'Review admission', exact: true }).click(); const approval = page.getByRole('dialog', { name: 'Approval details', exact: true }); await expect(approval.getByText('Captured filter', { exact: true })).toBeVisible();
    await approval.getByRole('button', { name: 'Approve', exact: true }).click(); await page.getByRole('dialog', { name: 'Approve?', exact: true }).getByRole('button', { name: 'Approve', exact: true }).click();
    await expect.poll(async () => (await call<{ items: { status: string; runId: string | null }[] }>(f.api, resource + `/${saved.automationId}/events`)).items[0]?.status).toBe('admitted');
    await page.goto(`/t/${f.tenant}/admin/agents/${instance}/automations`); await page.getByRole('button', { name: 'Edit Automation', exact: true }).click(); await current.getByRole('tab', { name: 'Configuration', exact: true }).click();
    await current.getByRole('textbox', { name: 'Automation instructions', exact: true }).fill('Updated parent intent for future admission.'); await current.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(current).toHaveCount(0);
    const updated = (await call<{ items: { automation: { triggers: { triggerId: string; revision: number }[] } }[] }>(f.api, resource + '/editor')).items[0].automation;
    expect(updated.triggers.map(t => t.triggerId)).toEqual(saved.triggers.map(t => t.triggerId)); expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});
