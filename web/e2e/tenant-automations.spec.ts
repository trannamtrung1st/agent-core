import { canonicalFetch, grantPrivateSpaceRead, signInTenant } from './support/tenant-auth';
import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';

const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy(); return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) { await signInTenant(page, email, secret); }
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Schedule ${nonce}`, slug: `schedule-${nonce}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
  const agent = await call<{ principalId: string; instance: { instanceId: string } }>(api, root + '/agents', { templateId: 'general-assistant', version: 36, expectedRevision: await revision() });
  await grantPrivateSpaceRead(api, tenant, agent.instance.instanceId);
  const id = agent.instance.instanceId; const route = `/t/${tenant}/admin/agents/${id}/automations`, resource = root + `/agents/${id}/automations`;
  const role = await call<{ roleId: string }>(api, root + '/roles', { name: 'Independent scheduled executor', permissions: ['automations.run', 'sessions.create', 'agent-runs.create', 'sessions.read', 'agent-runs.read', 'agents.chat'], expectedRevision: await revision() });
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
    await saved.getByRole('checkbox', { name: 'Enable automatic occurrences', exact: true }).check();
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

test('exact Automation admission is reviewed before any work exists and settles truthfully at narrow width', async ({ page }, info) => {
  const f = await fixture(); const observed = watch(page);
  try {
    const target = await call<{ sessionId: string }>(f.api, f.root + '/sessions', { retentionKind: "saved", agentInstanceId: f.id, mode: 'text', expectedRevision: await f.revision(), expectedInstanceRevision: 1 });
    await call(f.api, f.root + '/operation-policies/agent-runs.create', { mode: 'always', independentReview: false, expectedRevision: await f.revision(), expectedPolicyRevision: 0 }, 'PUT');
    await call(f.api, f.resource, { enabled: true, name: 'Approval before work exists', instructions: 'synthetic-automation-attention: finish this exact approved occurrence', schedule: { kind: 'daily', interval: 1, timeZone: 'UTC', localTime: '09:00' }, expectedRevision: await f.revision(), expectedInstanceRevision: 1, requiresTools: true, reportToSessionId: target.sessionId });
    await page.setViewportSize({ width: 390, height: 844 }); await signIn(page); await page.goto(f.route);
    await page.getByRole('button', { name: 'Run now', exact: true }).click(); const confirmation = page.getByRole('dialog', { name: 'Run saved Automation now?', exact: true });
    await confirmation.getByRole('button', { name: 'Run now', exact: true }).click();
    const link = page.getByRole('link', { name: 'Review the exact admission request', exact: true }); await expect(link).toBeVisible();
    expect(await page.getByRole('link', { name: 'Open the admitted Run in Activity', exact: true }).count()).toBe(0);
    const requestId = (await link.getAttribute('href'))!.split('/').at(-1)!;
    const pending = await call<{ runId: null; sessionId: null; review: { executionState: string } }>(f.api, f.root + `/approvals/${requestId}`);
    expect(pending.runId).toBeNull(); expect(pending.sessionId).toBeNull(); expect(pending.review.executionState).toBe('waitingDecision');
    await link.click(); const review = page.getByRole('dialog', { name: 'Approval details', exact: true });
    await expect(review).toBeVisible(); await expect(review.getByText('synthetic-automation-attention: finish this exact approved occurrence', { exact: true })).toBeVisible();
    await expect(review).toContainText(target.sessionId);
    expect(await review.getByRole('link', { name: 'Open original conversation', exact: true }).count()).toBe(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await expect.poll(async () => Math.round(await review.evaluate(element => element.getBoundingClientRect().x))).toBe(0);
    await expect(review).toContainText('UTC · saved registration revision');
    await page.screenshot({ path: info.outputPath('admission-narrow-review.png'), fullPage: true });
    await review.getByRole('button', { name: 'Approve', exact: true }).click(); const decide = page.getByRole('dialog', { name: 'Approve?', exact: true });
    await expect(decide).toContainText('Later tools need their own authority'); await decide.getByRole('button', { name: 'Approve', exact: true }).click();
    await expect.poll(async () => (await call<{ review: { executionState: string } }>(f.api, f.root + `/approvals/${requestId}`)).review.executionState).toBe('admitted');
    await review.getByRole('button', { name: 'Refresh details', exact: true }).click(); await expect(review.getByText(/One Background Session and Run were admitted/)).toBeVisible();
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('configured completion uses the shared tenant picker, retains a stale destination and delivers the exact report once', async ({ page }, info) => {
  const f = await fixture(); const observed = watch(page);
  try {
    const target = await call<{ sessionId: string; revision: number }>(f.api, f.root + '/sessions', { retentionKind: "saved", agentInstanceId: f.id, mode: 'text', expectedRevision: await f.revision(), expectedInstanceRevision: 1 });
    await call(f.api, f.root + `/sessions/${target.sessionId}`, { title: 'Quarterly research — a deliberately long conversation title for completion reports', expectedRevision: await f.revision(), expectedSessionRevision: target.revision }, 'PATCH');
    await page.setViewportSize({ width: 1440, height: 1000 }); await signIn(page); await page.goto(f.route);
    await page.getByRole('button', { name: 'New Automation', exact: true }).click();
    const editor = page.getByRole('dialog', { name: 'New Automation', exact: true });
    await editor.getByRole('textbox', { name: 'Automation name', exact: true }).fill('Independent work with an exact conversation report');
    await editor.getByRole('textbox', { name: 'Automation instructions', exact: true }).fill('synthetic-automation-attention: finish the bounded task and retain its factual result');
    await editor.getByRole('checkbox', { name: 'Enable automatic occurrences', exact: true }).check();
    await editor.getByRole('spinbutton', { name: 'Schedule maximum occurrences', exact: true }).fill('10');
    await editor.getByRole('combobox', { name: 'Automation completion report', exact: true }).click();
    await page.locator('.ant-select-item-option-content').filter({ hasText: /^Selected conversation$/ }).click();
    await expect(editor.getByRole('button', { name: 'Save Automation', exact: true })).toBeDisabled();
    await editor.getByRole('combobox', { name: 'Report destination', exact: true }).click();
    await page.locator('.ant-select-item-option-content').filter({ hasText: /Quarterly research/ }).click();
    await expect(editor.getByRole('button', { name: 'Save Automation', exact: true })).toBeEnabled();
    await editor.getByRole('heading', { name: 'Completion', exact: true }).click();
    await expect(page.locator('.ant-select-dropdown:visible, .ant-tooltip:visible')).toHaveCount(0);
    await editor.getByRole('region', { name: 'Completion', exact: true }).scrollIntoViewIfNeeded();
    for (const width of [1440, 1024, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 1000 });
      await expect.poll(async () => Math.round(await editor.evaluate(element => element.getBoundingClientRect().width))).toBe(width < 768 ? width : 640);
      await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await editor.getByRole('region', { name: 'Completion', exact: true }).scrollIntoViewIfNeeded();
      await expect(editor.getByRole('combobox', { name: 'Report destination', exact: true })).toBeInViewport();
      await page.screenshot({ path: info.outputPath(`configured-report-${width}.png`), fullPage: true });
    }
    // A real concurrent security mutation makes the prepared save stale.
    await call(f.api, f.root + '/roles', { name: 'Concurrent access update', permissions: ['agents.read'], expectedRevision: await f.revision() });
    await editor.getByRole('button', { name: 'Save Automation', exact: true }).click();
    await expect(editor.getByText(/Access has changed since/)).toBeVisible();
    await expect(editor.getByRole('button', { name: 'Save Automation', exact: true })).toBeDisabled();
    await editor.getByRole('button', { name: 'Reload current Automation', exact: true }).click();
    await expect(editor.getByRole('textbox', { name: 'Automation name', exact: true })).toHaveValue('Independent work with an exact conversation report');
    await expect(editor.getByRole('button', { name: 'Save Automation', exact: true })).toBeEnabled();
    await editor.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(editor).toHaveCount(0);
    const saved = (await call<{ items: { automationId: string; reportToSessionId: string; completionDelivery: string }[] }>(f.api, f.resource)).items[0];
    expect(saved.reportToSessionId).toBe(target.sessionId); expect(saved.completionDelivery).toBe('toSession');
    await page.getByRole('button', { name: 'Run now', exact: true }).click();
    const confirmation = page.getByRole('dialog', { name: 'Run saved Automation now?', exact: true });
    await confirmation.getByRole('button', { name: 'Run now', exact: true }).click();
    const activity = page.getByRole('link', { name: 'Open the admitted Run in Activity', exact: true }); await expect(activity).toBeVisible();
    const runId = new URL((await activity.getAttribute('href'))!, 'http://localhost').searchParams.get('run')!;
    await expect.poll(async () => (await call<{ status: string }>(f.api, f.root + `/agent-runs/${runId}`)).status).toBe('completed');
    await expect.poll(async () => (await call<{ items: { text: string | null; role: string }[] }>(f.api, f.root + `/sessions/${target.sessionId}/messages`)).items.filter(item => item.role.toLowerCase() === 'assistant').length).toBe(1);
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('selected-conversation execution preserves its destination through stale recovery and exact admission Review', async ({ page }, info) => {
  const f = await fixture(); const observed = watch(page);
  try {
    const target = await call<{ sessionId: string; revision: number }>(f.api, f.root + '/sessions', { retentionKind: "saved", agentInstanceId: f.id, mode: 'text', expectedRevision: await f.revision(), expectedInstanceRevision: 1 });
    await call(f.api, f.root + `/sessions/${target.sessionId}`, { title: 'Quarterly analysis — a long existing conversation name retained for exact scheduled execution', expectedRevision: await f.revision(), expectedSessionRevision: target.revision }, 'PATCH');
    await page.setViewportSize({ width: 1440, height: 1000 }); await signIn(page); await page.goto(f.route);
    await page.getByRole('button', { name: 'New Automation', exact: true }).click(); const editor = page.getByRole('dialog', { name: 'New Automation', exact: true });
    await editor.getByRole('textbox', { name: 'Automation name', exact: true }).fill('Reply in the exact selected conversation');
    await editor.getByRole('textbox', { name: 'Automation instructions', exact: true }).fill('Return one bounded factual response in the selected conversation.');
    await editor.getByRole('checkbox', { name: 'Enable automatic occurrences', exact: true }).check();
    await editor.getByRole('spinbutton', { name: 'Schedule maximum occurrences', exact: true }).fill('10');
    await editor.getByRole('combobox', { name: 'Automation execution destination', exact: true }).click();
    await page.locator('.ant-select-item-option-content').filter({ hasText: /^Selected conversation$/ }).click();
    await expect(editor.getByRole('combobox', { name: 'Automation completion report', exact: true })).toHaveCount(0);
    await expect(editor.getByRole('button', { name: 'Save Automation', exact: true })).toBeDisabled();
    await editor.getByRole('combobox', { name: 'Execution conversation', exact: true }).click();
    await page.locator('.ant-select-item-option-content').filter({ hasText: /Quarterly analysis/ }).click();
    await editor.getByRole('heading', { name: 'Execution', exact: true }).click();
    await expect(page.locator('.ant-select-dropdown:visible, .ant-tooltip:visible')).toHaveCount(0);
    for (const width of [1440, 1024, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 1000 });
      await expect.poll(async () => Math.round(await editor.evaluate(element => element.getBoundingClientRect().width))).toBe(width < 768 ? width : 640);
      await editor.getByRole('region', { name: 'Execution', exact: true }).scrollIntoViewIfNeeded();
      await expect(editor.getByRole('combobox', { name: 'Execution conversation', exact: true })).toBeInViewport();
      await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: info.outputPath(`existing-execution-${width}.png`), fullPage: true });
    }
    await call(f.api, f.root + '/roles', { name: 'Concurrent exact-target access update', permissions: ['agents.read'], expectedRevision: await f.revision() });
    await editor.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(editor.getByText(/Access has changed since/)).toBeVisible();
    await expect(editor.getByRole('button', { name: 'Save Automation', exact: true })).toBeDisabled();
    await editor.getByRole('button', { name: 'Reload current Automation', exact: true }).click();
    await expect(editor.getByRole('textbox', { name: 'Automation name', exact: true })).toHaveValue('Reply in the exact selected conversation');
    await expect(editor.getByRole('button', { name: 'Save Automation', exact: true })).toBeEnabled();
    await editor.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(editor).toHaveCount(0);
    const saved = (await call<{ items: { automationId: string; executionTarget: string; targetSessionId: string; completionDelivery: string }[] }>(f.api, f.resource)).items[0];
    expect(saved).toMatchObject({ executionTarget: 'existingSession', targetSessionId: target.sessionId, completionDelivery: 'none' });
    await page.getByRole('button', { name: 'Run now', exact: true }).click(); const run = page.getByRole('dialog', { name: 'Run saved Automation now?', exact: true });
    await expect(run).toContainText('selected conversation'); await run.getByRole('button', { name: 'Run now', exact: true }).click();
    await expect(page.getByRole('link', { name: 'Open the admitted Run in Activity', exact: true })).toBeVisible();
    const history = async () => (await call<{ items: { role: string; text: string | null; status: string }[] }>(f.api, f.root + `/sessions/${target.sessionId}/messages`)).items;
    await expect.poll(async () => (await history()).filter(item => item.role.toLowerCase() === 'assistant' && item.status === 'completed' && !!item.text).length).toBe(1);
    expect((await history()).filter(item => item.role.toLowerCase() === 'user')).toHaveLength(0);
    expect((await call<{ items: unknown[] }>(f.api, f.root + '/sessions')).items).toHaveLength(1);
    await call(f.api, f.root + '/operation-policies/agents.chat', { mode: 'always', independentReview: false, expectedRevision: await f.revision(), expectedPolicyRevision: 0 }, 'PUT');
    await page.getByRole('button', { name: 'Reload Automations', exact: true }).click();
    await page.getByRole('button', { name: 'Run now', exact: true }).click(); await page.getByRole('dialog', { name: 'Run saved Automation now?', exact: true }).getByRole('button', { name: 'Run now', exact: true }).click();
    const approval = page.getByRole('link', { name: 'Review the exact admission request', exact: true }); await expect(approval).toBeVisible();
    const requestId = (await approval.getAttribute('href'))!.split('/').at(-1)!; await approval.click();
    const review = page.getByRole('dialog', { name: 'Approval details', exact: true }); await expect(review).toContainText(target.sessionId);
    await expect(review).toContainText('Conversation revision'); await expect(review).toContainText('No Run has been admitted for this occurrence');
    await review.getByRole('button', { name: 'Approve', exact: true }).click();
    await page.getByRole('dialog', { name: 'Approve?', exact: true }).getByRole('button', { name: 'Approve', exact: true }).click();
    await expect.poll(async () => (await call<{ review: { executionState: string } }>(f.api, f.root + `/approvals/${requestId}`)).review.executionState).toBe('admitted');
    await review.getByRole('button', { name: 'Refresh details', exact: true }).click(); await expect(review).toContainText('A Run was admitted in the selected conversation');
    await expect.poll(async () => (await history()).filter(item => item.role.toLowerCase() === 'assistant' && item.status === 'completed' && !!item.text).length).toBe(2);
    expect((await call<{ items: unknown[] }>(f.api, f.root + '/sessions')).items).toHaveLength(1);
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});


test('tenant Activity inspects original background facts and continues the same Session through canonical Chat', async ({ page }) => {
  const f = await fixture(); const observed = watch(page);
  try {
    await page.setViewportSize({width: 390, height: 844}); await signIn(page); await page.goto(f.route);
    await page.getByRole('button', {name: 'New Automation', exact: true}).click();
    const editor = page.getByRole('dialog', {name: 'New Automation', exact: true});
    await editor.getByRole('textbox', {name: 'Automation name', exact: true}).fill('Activity continuation check');
    await editor.getByRole('textbox', {name: 'Automation instructions', exact: true}).fill('synthetic-automation-attention: check requested progress');
    await editor.getByRole('checkbox', {name: 'Enable automatic occurrences', exact: true}).check();
    await editor.getByRole('spinbutton', {name: 'Schedule maximum occurrences', exact: true}).fill('10');
    await editor.getByRole('button', {name: 'Save Automation', exact: true}).click(); await expect(editor).toHaveCount(0);
    await page.getByRole('button', {name: 'Run now', exact: true}).click();
    await page.getByRole('dialog', {name: 'Run saved Automation now?', exact: true}).getByRole('button', {name: 'Run now', exact: true}).click();
    const runLink = page.getByRole('link', {name: 'Open the admitted Run in Activity', exact: true});
    await expect(runLink).toBeVisible();
    const run = new URL((await runLink.getAttribute('href'))!, 'http://localhost').searchParams.get('run')!;
    await expect.poll(async () => (await call<{status:string}>(f.api, f.root + `/agent-runs/${run}`)).status).toBe('completed');
    await runLink.click(); const runDetail = page.getByRole('dialog', {name:'Run details', exact:true}); await expect(runDetail).toBeVisible();
    await expect(runDetail.getByText('Completed', {exact:true}).first()).toBeVisible();
    await page.goto(f.route.replace('/automations', '/activity/sessions'));
    const table = page.getByRole('table', {name:'Activity sessions table'});
    await table.getByRole('link').first().click();
    const details = page.getByRole('dialog'); await expect(details.getByText('Conversation run history', {exact:true})).toBeVisible();
    const originalSession = new URL(page.url()).searchParams.get('session')!;
    await details.getByRole('button', {name:'Continue in chat', exact:true}).click();
    await expect(page).toHaveURL(new RegExp(`/t/${f.tenant}/c/`));
    const session = new URL(page.url()).pathname.split('/').pop()!;
    expect(session).toBe(originalSession);
    const before = (await call<{items:{role:string}[]}>(f.api, f.root + `/sessions/${session}/messages`)).items.filter(x=>x.role === 'assistant').length;
    const message = page.getByRole('textbox', {name:'Message', exact:true}); await expect(message).toBeEnabled();
    await message.fill('Follow up in this same Session'); await page.getByRole('button', {name:'Send', exact:true}).click();
    await expect(page.getByText('Follow up in this same Session', {exact:true})).toHaveCount(1);
    await expect.poll(async () => (await call<{items:{role:string}[]}>(f.api, f.root + `/sessions/${session}/messages`)).items.filter(x=>x.role === 'assistant').length).toBeGreaterThan(before);
    expect(observed.errors).toEqual([]); expect(observed.legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});
