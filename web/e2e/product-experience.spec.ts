import fs from 'node:fs';
import path from 'node:path';
import { test, expect, request, type APIRequestContext, type Page } from '@playwright/test';
import { canonicalFetch, signInTenant } from './support/tenant-auth';
import { openConversationTool, openDestination } from './support/experience-navigation';
import { selectSpreadsheetCell } from './support/spreadsheet';

const base = process.env.CANONICAL_SYNTHETIC_URL!;
const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let organizationSlug: string;
let api: APIRequestContext, tenant: string, agent: string, readerState: Awaited<ReturnType<APIRequestContext['storageState']>>;
let ownerState: Awaited<ReturnType<APIRequestContext['storageState']>>;
const captureRoot = process.env.EXPERIENCE_CAPTURE_ROOT ?? '/tmp/agent-core-final-ux';
async function call<T>(client: APIRequestContext, url: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await client.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(client, url, { method, data, headers: { 'X-AgentCore-CSRF': csrf.token } });
  expect(response.ok(), `${method} ${url}: ${response.status()} ${await response.text()}`).toBeTruthy();
  return response.status() === 204 ? undefined as T : response.json();
}
const root = () => `/api/v2/tenants/${tenant}`;
const route = (suffix = '') => `/t/${tenant}${suffix}`;
async function settled(page: Page) { await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1); await expect(page.locator('[aria-label="Loading access"]')).toHaveCount(0); await expect(page.locator('.ant-spin-spinning')).toHaveCount(0); }
async function capture(page: Page, name: string, widths = [1440, 768, 390]) {
  fs.mkdirSync(captureRoot, { recursive: true });
  for (const width of widths) {
    await page.setViewportSize({ width, height: width === 768 ? 1024 : width < 768 ? 844 : 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect(page.locator('.ant-spin-spinning')).toHaveCount(0);
    if (width <= 390) for (const label of await page.locator('.agent-setting-description').all()) {
      if (await label.isVisible() && (await label.innerText()).length > 20) await expect.poll(async () => (await label.boundingBox())?.width ?? 0, { message: 'setting description must retain readable measure' }).toBeGreaterThan(80);
    }
    await page.screenshot({ path: path.join(captureRoot, `${name}-${width}.png`), animations: 'disabled' });
  }
  await page.setViewportSize({ width: 1440, height: 900 });
}

test.beforeAll(async () => {
  api = await request.newContext({ baseURL: base });
  expect((await (await api.get('/health')).json()).profile).toBe('Synthetic');
  await call(api, '/api/v2/auth/login', { login, password }); ownerState = await api.storageState();
  organizationSlug = `experience-${crypto.randomUUID().slice(0, 8)}`;
  tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: 'Northstar studio', slug: organizationSlug })).tenantId;
  agent = (await call<{ items: { instanceId: string }[] }>(api, root() + '/chat/agents')).items[0].instanceId;
  const access = await call<{ revision: number }>(api, root() + '/access/me');
  const roles = await call<{ items: { name: string; roleId: string }[] }>(api, root() + '/roles');
  const reader = roles.items.find(r => r.name === 'Reader')!;
  const readerLogin = `experience-reader-${crypto.randomUUID().slice(0, 8)}@example.test`;
  const invite = await call<{ invitationId: string; token: string }>(api, root() + '/members/invite', { login: readerLogin, roleId: reader.roleId, expectedRevision: access.revision });
  const guest = await request.newContext({ baseURL: base });
  try {
    await call(guest, '/api/v2/auth/register', { login: readerLogin, displayName: 'Review colleague', password: 'synthetic-experience-reader-password', invitation: { tenantId: tenant, invitationId: invite.invitationId, token: invite.token } });
    await call(guest, '/api/v2/auth/login', { login: readerLogin, password: 'synthetic-experience-reader-password' }); readerState = await guest.storageState();
  } finally { await guest.dispose(); }
  fs.mkdirSync(captureRoot, { recursive: true });
  fs.writeFileSync(path.join(captureRoot, 'experience-fixture.json'), JSON.stringify({ tenant, agent, profile: 'Synthetic', base }));
});
test.afterAll(async () => { await api?.dispose(); });

test('J01–J05: first use, actual answer, retained draft, labeled tools and protected file preview', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto('/sign-in');
  await capture(page, 'sign-in', [1440, 390, 320]);
  await expect(page).toHaveScreenshot('sign-in-1440.png', { animations: 'disabled' });
  await signInTenant(page, login, password);
  await page.getByRole('textbox', { name: 'Search organizations', exact: true }).fill(organizationSlug);
  await page.getByRole('button', { name: 'Open Northstar studio', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/t/${tenant}$`));
  await expect(page.getByRole('combobox', { name: 'Agent', exact: true })).toBeEnabled();
  await expect(page.locator('.agent-picker .ant-select-content')).toContainText('General Assistant');
  await expect(page.locator('.agent-picker .ant-select-content')).not.toContainText('System-managed');
  await expect(page.getByRole('button', { name: 'Model', exact: true })).toBeEnabled();
  await capture(page, 'chat-empty');
  await expect(page).toHaveScreenshot('chat-empty-1440.png', { animations: 'disabled' });
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Help me plan the next release.');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(1);
  const chat = page.url();
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Retain my draft while I inspect this agent');
  await openDestination(page, 'Agents'); await settled(page);
  await page.getByRole('banner').getByRole('link', { name: 'Chat', exact: true }).click();
  await expect(page).toHaveURL(chat);
  await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveText('Retain my draft while I inspect this agent');
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('');
  await page.getByRole('button', { name: 'Add content', exact: true }).click();
  const chooser = page.waitForEvent('filechooser'); await page.getByRole('menuitem', { name: /Attach file/ }).click();
  await (await chooser).setFiles({ name: 'release-notes.md', mimeType: 'text/markdown', buffer: Buffer.from('# Release plan\n\nA controlled Synthetic document.') });
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(2);
  const file = page.locator('.entry-files').getByRole('group', { name: 'release-notes.md', exact: true });
  await file.getByRole('button', { name: 'Preview release-notes.md', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'release-notes.md', exact: true })).toContainText('A controlled Synthetic document.');
  await capture(page, 'file-preview', [1440, 390]); await page.keyboard.press('Escape');
  await capture(page, 'chat-populated', [1440, 768, 390, 320]);
  await openConversationTool(page, 'Session files'); await expect(page.getByRole('dialog', { name: 'Session files', exact: true })).toBeVisible(); await expect(page.getByRole('tab', { name: /Working files$/ })).toBeFocused(); await page.keyboard.press('Escape');
  await expect(page.getByRole('button', { name: /^Conversation tools/ })).toBeFocused();
  await openConversationTool(page, 'Tasks'); await expect(page.getByRole('dialog')).toContainText(/Task/); await page.keyboard.press('Escape');
  await openConversationTool(page, 'Session goals'); await expect(page.getByRole('dialog')).toContainText(/goal/i); await page.keyboard.press('Escape');
  expect(errors).toEqual([]);
});

test('J06/J13/J14/J15: contextual navigation, settings categories, dirty guard and authoritative read-only scope', async ({ page, browser }) => {
  await page.context().addCookies(ownerState.cookies);
  await page.goto(route(`/admin/agents/${agent}/settings`));
  await expect(page.getByRole('combobox', { name: 'Settings category' })).toBeVisible();
  const navigation = page.getByRole('navigation', { name: 'Admin navigation' });
  await expect(navigation.getByRole('link', { name: 'Accounts', includeHidden: true, exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Conversation', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Language', exact: true })).toBeVisible();
  await capture(page, 'agent-settings', [1440, 768, 390, 320]);
  await page.getByRole('heading', { level: 1 }).click();
  await expect(page).toHaveScreenshot('agent-settings-1440.png', { animations: 'disabled' });
  const language = page.getByRole('textbox', { name: 'Language', exact: true });
  const inheritedLanguage = await language.inputValue();
  await language.fill('vi');
  for (const category of ['Behavior', 'Conversation']) {
    await page.getByRole('combobox', { name: 'Settings category', exact: true }).click();
    await page.locator('.ant-select-dropdown:visible .ant-select-item-option-content').filter({ hasText: new RegExp(`^${category}(?: · Unsaved)?$`) }).click();
    await expect(page.locator('.ant-select-dropdown:visible')).toHaveCount(0);
  }
  await expect(language).toHaveValue('vi');
  await page.getByRole('button', { name: 'Save Conversation', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Language: Instance override', exact: true })).toBeVisible();
  await page.reload(); await page.getByRole('button', { name: /^Conversation(?: |$)/ }).click();
  await expect(language).toHaveValue('vi');
  await page.getByRole('button', { name: 'Reset Language', exact: true }).click();
  await page.getByRole('button', { name: 'Save Conversation', exact: true }).click();
  await expect.poll(async () => {
    const current = await call<{ settings: { section: string; effective: { language?: string } }[] }>(api, root() + `/agents/${agent}/editor`);
    return current.settings.find(section => section.section === 'conversationPolicy')?.effective.language;
  }).toBe(inheritedLanguage);
  await expect(language).toHaveValue(inheritedLanguage);
  await page.goto(route(`/admin/agents/${agent}/profile`)); await settled(page);
  await page.getByRole('textbox', { name: 'Persona name', exact: true }).fill('Unsubmitted agent name');
  await navigation.getByRole('link', { name: 'Back to Agents', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true })).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: /Keep editing/ }).click();
  await expect(page.getByRole('textbox', { name: 'Persona name', exact: true })).toHaveValue('Unsubmitted agent name');
  await page.getByRole('button', { name: 'Discard persona changes', exact: true }).click();
  await openDestination(page, 'Agents'); await settled(page); await capture(page, 'agents');
  await page.getByRole('button', { name: 'Account and platform navigation', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Accounts', exact: true }).click(); await settled(page); await capture(page, 'system-accounts');
  const reader = await browser.newContext({ storageState: readerState, viewport: { width: 390, height: 844 } });
  try {
    const view = await reader.newPage(); await view.goto(base + route(`/admin/agents/${agent}/profile`)); await settled(view);
    await expect(view.getByText('Read-only configuration', { exact: true })).toBeVisible();
    await expect(view.getByRole('textbox', { name: 'Persona name', exact: true })).toBeDisabled();
    await capture(view, 'reader-profile', [390, 320]);
    await view.goto(base + '/system/accounts'); await settled(view); await expect(view.getByText(/This page is unavailable/)).toBeVisible();
    await capture(view, 'permission-denied', [390]);
    const csrf = await (await view.request.get(base + '/api/v2/auth/csrf')).json();
    const denied = await view.request.put(base + root() + `/agents/${agent}/persona`, { headers: { 'X-AgentCore-CSRF': csrf.token }, data: { persona: { name: 'Forbidden', role: 'Guide', description: 'Denied', tone: 'Calm' }, expectedRevision: 1, expectedInstanceRevision: 1, expectedPersonaRevision: 1 } });
    expect([403, 404]).toContain(denied.status());
  } finally { await reader.close(); }
});

test('J10: author, validate, publish, start and independently sign off an actual Workflow', async ({ page }) => {
  await page.context().addCookies(ownerState.cookies); await page.goto(route('/admin/workflows'));
  await page.getByRole('button', { name: 'New Workflow', exact: true }).click();
  await page.getByRole('textbox', { name: 'Workflow name', exact: true }).fill('Release review');
  await page.getByRole('textbox', { name: 'Description', exact: true }).fill('A controlled review with attributable process sign-off.');
  await capture(page, 'workflow-editor', [1440, 768, 390, 320]);
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Validate saved draft', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Validate saved draft', exact: true }).click();
  await expect(page.getByText('Saved revision validated', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Publish', exact: true }).click();
  await page.getByRole('dialog', { name: 'Publish version 1?', exact: true }).getByRole('button', { name: 'Publish immutable version', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Run published v1', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Run published v1', exact: true }).click();
  await page.getByRole('button', { name: 'Start version 1', exact: true }).click();
  const execution = page.getByRole('dialog', { name: 'Workflow execution', exact: true }); await expect(execution).toBeVisible();
  await expect(async () => { if (!await page.getByLabel('Process decision', { exact: true }).isVisible()) await execution.getByRole('button', { name: 'Refresh status', exact: true }).click(); await expect(page.getByLabel('Process decision', { exact: true })).toBeVisible(); }).toPass();
  await capture(page, 'workflow-sign-off', [1440, 390]);
  await page.getByLabel('Process decision', { exact: true }).click();
  await page.getByText('Approve this process step', { exact: true }).click();
  await page.getByRole('textbox', { name: 'Decision rationale (optional)', exact: true }).fill('Synthetic process evidence reviewed.');
  await page.getByRole('button', { name: 'Record sign-off', exact: true }).click();
  await expect(async () => { await execution.getByRole('button', { name: 'Refresh status', exact: true }).click(); await expect(execution.getByText('Completed', { exact: true }).first()).toBeVisible(); }).toPass();
  await expect(execution).toContainText('1/1 applicable required steps proven'); await capture(page, 'workflow-completed', [1440, 390]);
});

test('complete route families retain contextual access across organization, agent and system scopes', async ({ page }) => {
  test.setTimeout(240_000);
  await page.context().addCookies(ownerState.cookies);
  const { tenantPages, instancePages, systemPages } = await import('../src/features/access/experienceNavigation');
  const { tenantAgentPath } = await import('../src/features/access/accessRoute');
  const seen: { id: string; width: number; path: string; heading: string }[] = [];
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(route('/admin/agents'));
  for (const item of tenantPages) {
    await openDestination(page, item.label); await settled(page);
    for (const width of [1440, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), item.label + ' page overflow').toBe(true);
      seen.push({ id: `tenant.${item.id}`, width, path: new URL(page.url()).pathname, heading: await page.getByRole('heading', { level: 1 }).innerText() });
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await capture(page, `route-tenant-${item.id}`, [1440, 390]);
  }
  await page.goto(tenantAgentPath(tenant, agent, 'profile'));
  for (const item of instancePages) {
    await openDestination(page, item.label); await settled(page);
    for (const width of [1440, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), item.label + ' agent-page overflow').toBe(true);
      seen.push({ id: `agent.${item.id.slice('instance-'.length)}`, width, path: new URL(page.url()).pathname, heading: await page.getByRole('heading', { level: 1 }).innerText() });
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await capture(page, `route-agent-${item.id.slice('instance-'.length)}`, [1440, 390]);
  }
  for (const item of systemPages) {
    await openDestination(page, item.label); await settled(page);
    for (const width of [1440, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), item.label + ' system-page overflow').toBe(true);
      seen.push({ id: `system.${item.id}`, width, path: new URL(page.url()).pathname, heading: await page.getByRole('heading', { level: 1 }).innerText() });
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await capture(page, `route-system-${item.id}`, [1440, 390]);
  }
  fs.writeFileSync(path.join(captureRoot, 'route-visits.json'), JSON.stringify(seen, null, 2));
  expect(errors).toEqual([]);
});

test('J07/J15: Space and Environment authoring retain exact identity and recover stale edits', async ({ page }) => {
  await page.context().addCookies(ownerState.cookies); await page.goto(route('/admin/spaces'));
  await page.getByRole('button', { name: 'Create Shared Space', exact: true }).click();
  const editor = page.getByRole('dialog', { name: 'Create Shared Space', exact: true });
  await editor.getByLabel('Name', { exact: true }).fill('Release team');
  await editor.getByLabel('Description', { exact: true }).fill('Shared release context for this Synthetic workspace.');
  await editor.getByRole('button', { name: 'Save Space', exact: true }).click(); await expect(editor).toHaveCount(0);
  await page.getByRole('button', { name: 'Edit Space', exact: true }).waitFor(); await settled(page);
  const spaceUrl = page.url(), spaceId = new URL(spaceUrl).pathname.split('/').at(-2)!;
  await page.getByRole('button', { name: 'Edit Space', exact: true }).click();
  const edit = page.getByRole('dialog', { name: 'Edit Space', exact: true });
  await edit.getByLabel('Description', { exact: true }).fill('Keep this draft after concurrent change.');
  const space = await call<{ revision: number; name: string; instructions: string; environmentsEnabled: boolean }>(api, root() + `/spaces/${spaceId}`);
  const access = await call<{ revision: number }>(api, root() + '/access/me');
  await call(api, root() + `/spaces/${spaceId}`, { name: space.name, description: 'A concurrent editor changed this description.', instructions: space.instructions, environmentsEnabled: space.environmentsEnabled, expectedRevision: access.revision, expectedSpaceRevision: space.revision }, 'PATCH');
  await edit.getByRole('button', { name: 'Save Space', exact: true }).click();
  await expect(edit.getByRole('alert')).toBeVisible();
  await expect(edit.getByRole('button', { name: 'Reload current Space · keep draft', exact: true })).toBeEnabled();
  await expect(edit.getByLabel('Description', { exact: true })).toHaveValue('Keep this draft after concurrent change.');
  await capture(page, 'space-stale', [1440, 390]);
  await edit.getByRole('button', { name: 'Reload current Space · keep draft', exact: true }).click();
  await edit.getByRole('button', { name: /Save Space$/ }).click(); await expect(edit).toHaveCount(0);
  await page.getByRole('tab', { name: 'Environments', exact: true }).click();
  await page.getByRole('button', { name: 'Create Environment', exact: true }).click();
  const environment = page.getByRole('dialog', { name: 'Create Environment', exact: true });
  await environment.getByLabel('Name', { exact: true }).fill('Release checks');
  await environment.getByRole('button', { name: 'Save Environment', exact: true }).click(); await expect(environment).toHaveCount(0);
  await page.getByRole('button', { name: 'Add variable', exact: true }).click();
  const variable = page.getByRole('dialog', { name: 'Add variable', exact: true });
  await variable.getByLabel('Name', { exact: true }).fill('RELEASE_CHANNEL');
  await variable.getByLabel('Plain value', { exact: true }).fill('preview');
  await variable.getByRole('button', { name: 'Save variable', exact: true }).click(); await expect(variable).toHaveCount(0);
  await expect(page.getByRole('row').filter({ hasText: 'RELEASE_CHANNEL' })).toBeVisible();
  await expect(page.getByText('Environments are disabled in this Space', { exact: true })).toBeVisible();
  await capture(page, 'space-environment', [1440, 768, 390, 320]);
  await page.reload(); await page.getByRole('button', { name: 'Release checks', exact: true }).click();
  await expect(page.getByRole('row').filter({ hasText: 'RELEASE_CHANNEL' })).toBeVisible();
});


test('final refinement: Cooperation dependency, compact rows, semantic navigation and collection actions', async ({ page }) => {
  await page.context().addCookies(ownerState.cookies);
  await page.goto(route(`/admin/agents/${agent}/settings`)); await settled(page);
  const panel = page.getByRole('region', { name: 'Cooperation', exact: true });
  const messages = panel.getByRole('checkbox', { name: 'Accept direct Agent messages', exact: true });
  const wake = panel.getByRole('checkbox', { name: 'Allow direct-message wake', exact: true });
  await expect(wake).toBeDisabled(); await messages.check(); await wake.check(); await messages.uncheck();
  await expect(wake).not.toBeChecked(); await expect(wake).toBeDisabled();
  await panel.getByRole('spinbutton', { name: 'Maximum concurrent tasks' }).fill('3');
  await panel.getByRole('button', { name: 'Save cooperation', exact: true }).click();
  await expect(panel.getByRole('button', { name: 'Save cooperation', exact: true })).toBeDisabled();
  await expect(panel.getByRole('button', { name: 'Save cooperation', exact: true })).not.toHaveClass(/ant-btn-loading/);
  await page.reload(); await settled(page);
  await expect(panel.getByRole('spinbutton', { name: 'Maximum concurrent tasks' })).toHaveValue('3');
  await expect(wake).not.toBeChecked(); await expect(wake).toBeDisabled();
  for (const width of [1440, 1024, 768, 390, 320]) {
    await page.setViewportSize({ width, height: 900 }); await panel.scrollIntoViewIfNeeded();
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const row = panel.locator('.agent-setting-row').first();
    const label = await row.locator('.agent-setting-description').boundingBox();
    const control = await row.locator('.agent-setting-control').boundingBox();
    if (width >= 1024) expect(control!.x).toBeGreaterThan(label!.x + label!.width);
    else if (width <= 390) expect(control!.x).toBeGreaterThan(label!.x + label!.width);
    await page.screenshot({ path: path.join(captureRoot, `cooperation-${width}.png`), animations: 'disabled' });
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(route('/admin/agents')); await settled(page);
  for (const title of ['Role', 'Lifecycle', 'Managed', 'Chat visibility', 'Actions']) await expect(page.getByRole('columnheader', { name: title, exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'More actions for General Assistant', exact: true }).click();
  await page.getByRole('link', { name: 'Connections for General Assistant', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Connections', level: 1, exact: true })).toBeVisible();
  await capture(page, 'connections-from-more', [1440, 390]);
});

test('final refinement: nested Definition, model and account pages have rendered dispositions', async ({ page }) => {
  test.setTimeout(180_000);
  await page.context().addCookies(ownerState.cookies);
  const revision = (await call<{ revision: number }>(api, root() + '/access/me')).revision;
  const draft = await call<{ definitionId: string; draftId: string }>(api, root() + '/agent-definitions', { name: 'Navy configuration proof', expectedRevision: revision });
  const { draftPages } = await import('../src/features/admin/adminNavigation');
  const visits: { id: string; route: string; widths: number[] }[] = [];
  for (const item of draftPages) {
    await page.goto(route(`/admin/definitions/${draft.definitionId}/drafts/${draft.draftId}/${item.path}`)); await settled(page);
    await capture(page, `route-draft-${item.id}`, [1440, 390]);
    visits.push({ id: `draft.${item.id}`, route: new URL(page.url()).pathname, widths: [1440, 390] });
  }
  for (const item of ['models', 'defaults', 'routing']) {
    await page.goto(route(`/integrations/ai-models/${item}`)); await settled(page);
    await capture(page, `route-models-${item}`, [1440, 1024, 768, 390, 320]);
    visits.push({ id: `models.${item}`, route: new URL(page.url()).pathname, widths: [1440, 1024, 768, 390, 320] });
  }
  for (const item of ['profile', 'organizations']) {
    await page.goto(`/account/${item}`); await settled(page);
    await capture(page, `route-account-${item}`, [1440, 390, 320]);
    visits.push({ id: `account.${item}`, route: new URL(page.url()).pathname, widths: [1440, 390, 320] });
  }
  await page.goto(route('/admin/spaces')); await settled(page);
  await page.getByRole('button', { name: 'Create Shared Space', exact: true }).click();
  const spaceEditor = page.getByRole('dialog', { name: 'Create Shared Space', exact: true });
  await spaceEditor.getByLabel('Name', { exact: true }).fill('Navy Space proof');
  await spaceEditor.getByRole('button', { name: 'Save Space', exact: true }).click(); await expect(spaceEditor).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Edit Space', exact: true })).toBeVisible();
  const segments = new URL(page.url()).pathname.split('/'); const spaceId = segments[segments.indexOf('spaces') + 1];
  for (const item of ['overview', 'members', 'workspace', 'environments', 'access']) {
    await page.goto(route(`/admin/spaces/${spaceId}/${item}`)); await settled(page);
    await capture(page, `route-space-${item}`, [1440, 390]);
    visits.push({ id: `space.${item}`, route: new URL(page.url()).pathname, widths: [1440, 390] });
  }
  fs.writeFileSync(path.join(captureRoot, 'nested-route-visits.json'), JSON.stringify(visits, null, 2));
});


test('final refinement: every inherited settings section retains readable rows and native controls', async ({ page }) => {
  await page.context().addCookies(ownerState.cookies);
  await page.goto(route(`/admin/agents/${agent}/settings`)); await settled(page);
  await page.getByRole('combobox', { name: 'Settings category', exact: true }).click();
  await page.locator('.ant-select-dropdown:visible .ant-select-item-option-content').filter({ hasText: /^All settings$/ }).click();
  const headers = page.locator('.ant-collapse-header');
  const current = await call<{ settings: unknown[] }>(api, root() + `/agents/${agent}/editor`);
  await expect(headers).toHaveCount(current.settings.length);
  const count = current.settings.length;
  const sections: { id: string; title: string; widths: number[] }[] = [];
  for (let index = 0; index < count; index++) {
    const header = headers.nth(index); const title = (await header.innerText()).replace(/\n/g, ' ');
    if (await header.getAttribute('aria-expanded') !== 'true') await header.click();
    const id = await header.locator('.admin-settings-anchor').getAttribute('id');
    await header.scrollIntoViewIfNeeded();
    if (id === 'capabilities') {
      const wideRow = header.locator('..').locator('.agent-setting-row-wide').first();
      await expect(wideRow.locator('.instance-settings-control').first()).toBeVisible();
      const content = await wideRow.locator('.agent-setting-control').boundingBox();
      const selector = await wideRow.locator('.instance-settings-control').first().boundingBox();
      expect(selector!.width, 'capability lists use the available full-width row').toBeGreaterThan(content!.width * 0.9);
    }
    await capture(page, `settings-${id}`, [1440, 390, 320]);
    sections.push({ id: `settings.${id === 'restrictions' ? 'triggerPolicy' : id}`, title, widths: [1440, 390, 320] });
    await header.click();
  }
  fs.writeFileSync(path.join(captureRoot, 'settings-visits.json'), JSON.stringify(sections, null, 2));
});

test('final refinement: protected spreadsheet and Word canvases retain native presentation in the navy frame', async ({ page }) => {
  await page.context().addCookies(ownerState.cookies); await page.goto(route());
  await expect(page.getByRole('combobox', { name: 'Agent', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Add content', exact: true }).click();
  const chooser = page.waitForEvent('filechooser'); await page.getByRole('menuitem', { name: /Attach file/ }).click();
  await (await chooser).setFiles([
    { name: 'navy.csv', mimeType: 'text/csv', buffer: Buffer.from('Name,Value\nNavy,42\n') },
    { name: 'presentation.xlsx', mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: fs.readFileSync(path.resolve('e2e/fixtures/file-preview/presentation.xlsx')) },
    { name: 'with-image.docx', mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', buffer: fs.readFileSync(path.resolve('e2e/fixtures/file-preview/with-image.docx')) },
  ]);
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Keep these files available for preview.');
  await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(1);
  for (const name of ['navy.csv', 'presentation.xlsx', 'with-image.docx']) {
    await page.getByRole('button', { name: `Preview ${name}`, exact: true }).click();
    const dialog = page.getByRole('dialog', { name, exact: true }); await expect(dialog).toBeVisible();
    if (name === 'with-image.docx') await expect(page.frameLocator('iframe[title="Word document preview"]').getByText('Local document header', { exact: true })).toBeVisible();
    else {
      await expect(dialog.locator('.file-preview-spreadsheet')).toBeVisible();
      await selectSpreadsheetCell(dialog, name === 'navy.csv' ? 'A2' : 'B4', name === 'navy.csv' ? 'Navy' : '$1,234.50');
    }
    await capture(page, `office-${name.replaceAll('.', '-')}`, [1440, 390, 320]);
    if (name === 'navy.csv') { await dialog.getByText('Text', { exact: true }).click(); await expect(dialog.locator('pre')).toContainText('Navy,42'); }
    await page.keyboard.press('Escape'); await expect(dialog).toHaveCount(0);
  }
});
