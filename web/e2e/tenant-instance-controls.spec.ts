import { canonicalFetch, signInTenant } from './support/tenant-auth';
import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';

const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBeTruthy(); return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) { await signInTenant(page, email, secret); }
async function navigateTo(page: Page, name: 'Execution budgets' | 'Self-management' | 'Unattended model') {
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

test('320px budgets retain stale drafts; policy freeze preserves drafts; model reset uses current catalog', async ({ page }) => {
  const f = await fixture(); const errors: string[] = [], legacy: string[] = []; page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => { if (/^\/api\/v[12]\/(owner-capability|agent-instances|admin)(\/|$)/.test(new URL(r.url()).pathname)) legacy.push(r.url()); });
  const select = async (name: string, option: string) => {
    const input = page.getByRole('combobox', { name, exact: true }); await input.click();
    // Native popup hover may choose a different active row from the saved selection.
    // Follow the actual keyboard-active row, rather than assuming a fixed starting index.
    const listId = await input.getAttribute('aria-controls');
    const popup = page.locator('.ant-select-dropdown').filter({ has: page.locator(`[id="${listId}"]`) });
    await expect(popup).toBeVisible();
    await page.mouse.move(0, 0);
    await expect(popup).not.toHaveClass(/\bant-slide-up-(?:appear|enter)(?:-(?:prepare|start|active))?\b/);
    const active = popup.locator('.ant-select-item-option-active');
    for (let i = 0; i < 10; i++) {
      if (await active.getAttribute('title') === option) break;
      await input.press('ArrowDown');
    }
    await expect(active).toHaveAttribute('title', option); await input.press('Enter');
    await expect(input.locator('..')).toHaveAttribute('title', option); await expect(popup).toBeHidden();
  };
  const read = () => call<any>(f.api, f.resource + '/configuration-controls');
  const save = async (name: string) => {
    const refreshed = page.waitForResponse(response => response.request().method() === 'GET' && response.url().endsWith('/configuration-controls') && response.ok());
    await page.getByRole('button', { name, exact: true }).click(); await (await refreshed).finished();
    await expect(page.getByRole('button', { name: /^Reset to (Definition defaults|execution default)$/ })).toBeEnabled();
    await expect(page.getByRole('button', { name, exact: true })).toBeDisabled();
  };
  try {
    await page.setViewportSize({ width: 320, height: 740 }); await signIn(page); await page.goto(f.route + '/execution-budgets');
    await select('Standard inheritance', 'Override'); await select('Standard profile', 'Extended'); await f.patch('conversationPolicy', { language: 'de' });
    await page.getByRole('button', { name: 'Save Execution budgets', exact: true }).click(); await expect(page.getByText(/Access has changed since you opened/)).toBeVisible();
    await page.getByRole('button', { name: 'Reload controls', exact: true }).click(); await expect(page.getByRole('group', { name: 'Standard budget', exact: true })).toContainText('48 steps');
    await save('Save Execution budgets');
    expect((await read()).budgets.overrides.standard.maxSteps).toBe(48);
    await page.getByRole('button', { name: 'Reset to Definition defaults', exact: true }).click(); expect((await read()).budgets.overrides.standard.maxSteps).toBe(48);
    await save('Save Execution budgets'); expect((await read()).budgets.overrides.standard).toBeNull();
    await navigateTo(page, 'Self-management'); await select('Self-management mode', 'Assisted'); await page.getByRole('checkbox', { name: 'Settings', exact: true }).check();
    await save('Save Self-management');
    await select('Self-management mode', 'Off'); await page.getByRole('button', { name: 'Freeze self-management', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Freeze self-management?', exact: true }); await expect(dialog).toContainText('Unsaved policy edits are preserved'); await dialog.getByRole('button', { name: 'Freeze self-management', exact: true }).click();
    await expect(page.getByText('Self-management is frozen', { exact: true })).toBeVisible(); let state = await read(); expect(state.management.policy.effective).toMatchObject({ mode: 'Assisted', scopes: ['Settings'], frozen: true });
    await expect(page.getByRole('combobox', { name: 'Self-management mode', exact: true }).locator('xpath=ancestor::div[contains(@class,"ant-select")][1]')).toContainText('Off');
    await navigateTo(page, 'Unattended model'); await page.getByRole('dialog', { name: 'Discard unsaved changes?' }).getByRole('button', { name: 'Keep editing', exact: true }).click();
    await page.getByRole('button', { name: 'Discard Self-management changes', exact: true }).click(); await navigateTo(page, 'Unattended model');
    await select('Unattended model', 'Scripted Beta'); await save('Save Unattended model'); expect((await read()).agent.instance.unattendedOfferingId).toBe('dca30000-0000-4000-8000-000000000002');
    await page.getByRole('button', { name: 'Reset to execution default', exact: true }).click(); await save('Save Unattended model'); expect((await read()).agent.instance.unattendedOfferingId).toBeNull();
    await expect(page.getByRole('combobox', { name: 'Unattended reasoning effort', exact: true })).toBeVisible(); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(errors).toEqual([]); expect(legacy).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('exact scoped controls reader needs no IAM inspector and loses protected configuration after revocation', async ({ page }) => {
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
    await signIn(page, email, secret); await page.goto(f.route + '/execution-budgets'); await expect(page.getByText('Read-only configuration', { exact: true })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Standard inheritance', exact: true })).toBeDisabled();
    await navigateTo(page, 'Self-management'); await expect(page.getByRole('combobox', { name: 'Self-management mode', exact: true })).toBeDisabled();
    await call(f.api, `${f.root}/role-assignments/${assignment.assignmentId}?expectedRevision=${await f.revision()}`, undefined, 'DELETE');
    await page.getByRole('button', { name: 'Reload controls', exact: true }).click(); await expect(page.getByRole('combobox', { name: 'Self-management mode', exact: true })).toHaveCount(0); await expect(page.getByRole('alert')).toBeVisible();
  } finally { await f.api.dispose(); }
});
