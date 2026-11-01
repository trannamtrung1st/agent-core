import fs from 'node:fs';
import path from 'node:path';
import { expect, request, test, type APIRequestContext, type Locator, type Page } from '@playwright/test';
import { canonicalFetch } from './support/tenant-auth';
import { waitForResponseSettled } from './support/response-settled';

const captureRoot = process.env.EXPERIENCE_CAPTURE_ROOT;
const organization = 'Layout review organization with a deliberately long name';
let tenant: string, agent: string;
let ownerState: Awaited<ReturnType<APIRequestContext['storageState']>>;
const route = (suffix = '') => `/t/${tenant}${suffix}`;
async function capture(page: Page, name: string) {
  if (!captureRoot) return;
  fs.mkdirSync(captureRoot, { recursive: true });
  await page.screenshot({ path: path.join(captureRoot, `${name}-${page.viewportSize()!.width}.png`), animations: 'disabled' });
}
async function contained(page: Page, locator: Locator) {
  await expect.poll(async () => { const box = await locator.boundingBox(); return !!box && box.x >= 0 && box.x + box.width <= page.viewportSize()!.width + 1; }).toBe(true);
}
test.beforeAll(async () => {
  const api = await request.newContext({ baseURL: process.env.CANONICAL_SYNTHETIC_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic');
    async function call(url: string, data: unknown) {
      const csrf = await (await api.get('/api/v2/auth/csrf')).json();
      const response = await canonicalFetch(api, url, { method: 'POST', data, headers: { 'X-AgentCore-CSRF': csrf.token } });
      expect(response.ok(), await response.text()).toBe(true); return response.json();
    }
    await call('/api/v2/auth/login', { login: 'tenant-chat-owner@example.test', password: 'synthetic-tenant-chat-password' });
    tenant = (await call('/api/v2/system/tenants', { name: organization, slug: `layout-${crypto.randomUUID().slice(0, 8)}` })).tenantId;
    agent = (await (await api.get(`/api/v2/tenants/${tenant}/chat/agents`)).json()).items[0].instanceId;
    ownerState = await api.storageState();
  } finally { await api.dispose(); }
});
test.beforeEach(async ({ page }) => { await page.context().addCookies(ownerState.cookies); });

test('Chat keeps distinct aligned toolbar hits and readable organization context at narrow widths', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(route());
  const message = page.getByRole('textbox', { name: 'Message', exact: true });
  await expect(message).toBeEnabled(); await message.fill('Review the toolbar around this actual Synthetic response.');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible(); await waitForResponseSettled(page);
  const tools = page.getByRole('button', { name: 'Conversation tools', exact: true });
  const switcher = page.getByRole('button', { name: `Switch organization. Current: ${organization}`, exact: true });
  const menu = page.getByRole('button', { name: 'Workspace menu', exact: true });
  for (const width of [1440, 1024, 768, 481, 480, 390, 320]) {
    await page.setViewportSize({ width, height: 900 }); await contained(page, menu);
    await expect.poll(async () => (await switcher.locator('.ant-btn-icon').boundingBox())!.width).toBeGreaterThanOrEqual(14);
    if (width <= 480) {
      await expect.poll(async () => (await switcher.boundingBox())!.width).toBeGreaterThanOrEqual(140);
      const boxes = await Promise.all([tools, switcher, page.getByRole('link', { name: 'Approvals', exact: true }), menu].map(control => control.boundingBox()));
      for (let index = 1; index < boxes.length; index++) {
        expect(boxes[index]!.y).toBeCloseTo(boxes[0]!.y, 0);
        expect(boxes[index]!.x - boxes[index - 1]!.x - boxes[index - 1]!.width).toBeCloseTo(8, 0);
      }
      for (const box of boxes) expect(box!.height).toBeGreaterThanOrEqual(40);
    }
    await capture(page, 'chat-toolbar');
  }
  await tools.click(); await expect(page.getByRole('button', { name: 'Session files', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Session files', exact: true }).click();
  const files = page.getByRole('dialog', { name: 'Session files', exact: true });
  await expect(files.getByRole('tab', { name: /Working files$/ })).toBeFocused(); await capture(page, 'chat-files-empty');
  await page.keyboard.press('Escape'); await expect(files).not.toBeVisible(); await expect(tools).toBeFocused();
  await switcher.click(); await expect(page.getByRole('dialog', { name: 'Switch organization', exact: true })).toBeVisible(); await capture(page, 'organization-switcher');
  await page.keyboard.press('Escape'); await expect(switcher).toBeFocused();
  await menu.click(); await expect(page.getByRole('menuitem', { name: 'Agents', exact: true })).toBeVisible(); await page.keyboard.press('Escape');
  expect(errors).toEqual([]);
});

test('native editors keep consistent mobile heights and invitation validation and saving work', async ({ page }) => {
  for (const [suffix, action, name] of [
    ['/integrations/ai-models/connections', 'Connect a provider', 'connection'], ['/integrations/credentials', 'Add credential', 'credential'],
    ['/integrations/secrets', 'Create Secret', 'secret'], ['/integrations/events', 'New webhook', 'event'], ['/integrations/sandboxes', 'Add prebuilt image', 'sandbox'],
    ['/admin/spaces', 'Create Shared Space', 'space'], ['/admin/members', 'Invite member', 'member'], ['/admin/roles', 'Create role', 'role'], ['/admin/evaluations', 'New Suite', 'suite'],
    ['/admin/definitions', 'New definition', 'definition'], ['/admin/agents', 'New agent', 'agent'],
    [`/admin/agents/${agent}/memory`, 'Review completed work', 'experience'], ['/account/organizations', 'Create organization', 'organization'],
  ]) {
    await page.goto(suffix.startsWith('/account/') ? suffix : route(suffix)); await page.getByRole('button', { name: action, exact: true }).click();
    const dialog = page.getByRole('dialog').last(); await expect(dialog).toBeVisible();
    for (const width of [1440, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 }); await contained(page, dialog);
      if (width <= 390) for (const control of await dialog.locator('.ant-select-single, .ant-input-affix-wrapper, input.ant-input:not([type="hidden"]):not(.ant-input-affix-wrapper input)').all()) {
        if (await control.isVisible()) await expect.poll(async () => (await control.boundingBox())!.height).toBeGreaterThanOrEqual(40);
      }
      await capture(page, `editor-${name}`);
    }
    await dialog.getByRole('button', { name: name === 'event' ? 'Close details' : name === 'sandbox' ? 'Cancel editing' : name === 'experience' ? 'Close' : 'Cancel', exact: true }).last().click(); await expect(dialog).not.toBeVisible();
  }
  await page.goto(route('/admin/members')); await page.getByRole('button', { name: 'Invite member', exact: true }).click();
  const invitation = page.getByRole('dialog', { name: 'Invite member', exact: true });
  await invitation.getByRole('button', { name: 'Create invitation', exact: true }).click();
  await expect(invitation.getByText('Enter the invited person’s email.', { exact: true })).toBeVisible();
  await expect(invitation.getByText('Choose an initial role.', { exact: true })).toBeVisible(); await capture(page, 'invitation-validation');
  const email = `layout-invite-${crypto.randomUUID().slice(0, 8)}@example.test`;
  await invitation.getByLabel('Email', { exact: true }).fill(email);
  await invitation.getByRole('combobox', { name: 'Initial role', exact: true }).click();
  await invitation.getByRole('combobox', { name: 'Initial role', exact: true }).fill('Reader');
  await page.locator('.ant-select-dropdown:visible .ant-select-item-option-content').filter({ hasText: /^Reader$/ }).click();
  await invitation.getByRole('button', { name: 'Create invitation', exact: true }).click();
  const success = page.getByRole('dialog', { name: 'Invitation created', exact: true });
  await expect(success.getByRole('textbox', { name: 'Invitation link', exact: true })).toHaveValue(/\/invitations\//); await capture(page, 'invitation-success');
  await success.getByRole('button', { name: 'Done', exact: true }).click();
  await page.getByRole('tab', { name: 'Invitations', exact: true }).click(); await expect(page.getByText(email, { exact: true })).toBeVisible();
});

test('setup and failed-read recovery alerts keep readable columns and usable actions', async ({ page }) => {
  await page.goto(route('/integrations/ai-models/models'));
  const setup = page.locator('.ant-alert').filter({ hasText: 'Connect a provider first' }); await expect(setup).toBeVisible();
  for (const width of [1440, 768, 481, 480, 390, 320]) {
    await page.setViewportSize({ width, height: 900 }); await contained(page, setup);
    if (width <= 480) {
      const section = setup.locator('.ant-alert-section'); await expect.poll(async () => (await section.boundingBox())!.width).toBeGreaterThan(190);
      const action = await setup.getByRole('button', { name: 'Connections', exact: true }).boundingBox(); const text = await section.boundingBox();
      expect(action!.y).toBeGreaterThanOrEqual(text!.y + text!.height);
    }
    await capture(page, 'models-setup');
  }
  await setup.getByRole('button', { name: 'Connections', exact: true }).click(); await expect(page.getByRole('button', { name: 'Connect a provider', exact: true })).toBeVisible();
  let release!: () => void; const waiting = new Promise<void>(resolve => { release = resolve; });
  await page.route(`**/api/v2/tenants/${tenant}/models`, async handler => { await waiting; await handler.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ reasonCode: 'service_unavailable' }) }); }, { times: 1 });
  await page.getByRole('button', { name: 'Reload', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Loading access', exact: true })).toBeVisible(); await capture(page, 'models-loading'); release();
  const error = page.getByRole('alert'); await expect(error).toBeVisible(); await contained(page, error); await capture(page, 'models-read-error');
  await error.getByRole('button', { name: 'Reload', exact: true }).click(); await expect(error).not.toBeVisible();
  await expect(page.getByRole('button', { name: 'Connect a provider', exact: true })).toBeVisible();
});

test('a full organization picker page scrolls inside the dialog and retains paging and search', async ({ page }) => {
  // Deterministic long catalog: reproduce a real content boundary without creating 20 organizations.
  const items = Array.from({ length: 20 }, (_, index) => ({ tenantId: crypto.randomUUID(), name: `Review organization ${String(index).padStart(2, '0')} with a long descriptive name`, slug: `review-${index}`, userId: 'review-user', principalId: 'review-principal' }));
  await page.route('**/api/v2/account/tenants?*', async handler => {
    const params = new URL(handler.request().url()).searchParams;
    const current = Number(params.get('page') ?? 0), query = params.get('q') ?? '';
    const filtered = items.filter(item => item.name.toLowerCase().includes(query.toLowerCase()));
    await handler.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: filtered.slice(current * 10, current * 10 + 10), page: current, pageSize: 10, total: filtered.length }) });
  });
  await page.goto(route('/admin/agents'));
  const opener = page.getByRole('button', { name: `Switch organization. Current: ${organization}`, exact: true });
  await opener.click(); const dialog = page.getByRole('dialog', { name: 'Switch organization', exact: true });
  await expect(dialog.getByText(items[0].name, { exact: true })).toBeVisible();
  for (const viewport of [{ width: 1440, height: 900 }, { width: 768, height: 1024 }, { width: 390, height: 844 }, { width: 320, height: 568 }]) {
    await page.setViewportSize(viewport); await contained(page, dialog);
    await expect.poll(async () => { const box = await dialog.locator('.ant-modal-container').boundingBox(); return !!box && box.y >= 0 && box.y + box.height <= viewport.height; }).toBe(true);
    await expect(dialog.locator('.ant-modal-body')).toHaveCSS('overflow-y', 'auto');
    if (viewport.width <= 390) await expect.poll(() => dialog.locator('.ant-modal-body').evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
    await capture(page, 'organization-picker-bounded');
  }
  await dialog.locator('.ant-pagination-next').click();
  await expect(dialog.getByText(items[19].name, { exact: true })).toBeVisible();
  await dialog.getByRole('textbox', { name: 'Search organizations', exact: true }).fill('organization 03');
  await expect(dialog.getByText(items[3].name, { exact: true })).toBeVisible();
  await expect(dialog.getByText('1 organization', { exact: true })).toBeVisible();
  await capture(page, 'organization-picker-search');
  await page.keyboard.press('Escape'); await expect(dialog).not.toBeVisible(); await expect(opener).toBeFocused();
});
