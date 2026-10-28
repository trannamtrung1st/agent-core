import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const result = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(result.ok(), `${method} ${path}: ${result.status()} ${await result.text()}`).toBeTruthy(); return result.status() === 204 ? undefined as T : result.json();
}
async function select(page: Page, name: string, label: string) {
  await page.getByRole('combobox', { name, exact: true }).click();
  await page.locator('.ant-select-item-option-content').filter({ hasText: new RegExp(`^${label}$`) }).click();
  await expect(page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden)')).toHaveCount(0);
}
for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) test(`tenant preset disabled draft provenance uncertain acknowledgement and customized execution ${viewport.width}`, async ({ page }, info) => {
  test.setTimeout(120000); await page.setViewportSize(viewport); const errors: string[] = [], legacy: string[] = [];
  page.on('pageerror', e => errors.push(e.message)); page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) errors.push(r.url()); });
  page.on('request', r => { if (/^\/api\/v[12]\/(owner-capability|agent-instances|admin)(\/|$)/.test(new URL(r.url()).pathname)) legacy.push(r.url()); });
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password });
    const nonce = crypto.randomUUID().slice(0, 8); const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Preset ${nonce}`, slug: `preset-${nonce}` })).tenantId;
    const root = `/api/v2/tenants/${tenant}`; const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
    const agent = await call<{ principalId: string; instance: { instanceId: string } }>(api, root + '/agents', { templateId: 'general-assistant', version: 36, expectedRevision: await revision() });
    const role = await call<{ roleId: string }>(api, root + '/roles', { name: 'Preset executor', permissions: ['automations.run', 'sessions.create', 'agent-runs.create', 'sessions.read', 'agent-runs.read', 'agents.chat'], expectedRevision: await revision() });
    await call(api, root + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    const resource = root + `/agents/${agent.instance.instanceId}/automations`, route = `/t/${tenant}/admin/agents/${agent.instance.instanceId}/automations`;
    await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible(); await page.goto(route);
    await page.getByRole('button', { name: 'New Automation', exact: true }).click();
    let editor = page.getByRole('dialog', { name: 'New Automation', exact: true }); await select(page, 'Automation starting recipe', 'Consolidate continuity');
    await expect(editor.getByRole('textbox', { name: 'Automation name', exact: true })).toHaveValue('Consolidate continuity');
    await expect(editor.getByRole('checkbox', { name: 'Enable automatic occurrences', exact: true })).not.toBeChecked();
    await expect(editor).toContainText('Enable Allow agent consolidation'); await page.screenshot({ path: info.outputPath('preset-draft.png'), fullPage: true, animations: 'disabled' });
    await editor.getByRole('textbox', { name: 'Automation name', exact: true }).fill(`Owned preset ${nonce}`);
    await editor.getByRole('textbox', { name: 'Automation instructions', exact: true }).fill('synthetic-automation-attention: finish the customized bounded task and retain its factual outcome');
    await editor.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(editor).toHaveCount(0);
    await page.mouse.move(4, 4);
    await expect(page.getByText('Automation saved. Reading current registrations.', { exact: true })).toHaveCount(0);
    let item = (await call<{ items: { automationId: string; revision: number; presetId: string; presetVersion: number; status: string }[] }>(api, resource)).items[0];
    expect(item.status).toBe('disabled'); expect(item.presetId).toBe('consolidate-continuity'); expect(item.presetVersion).toBe(1);
    await page.getByRole('button', { name: 'Edit Automation', exact: true }).click(); editor = page.getByRole('dialog', { name: 'Saved Automation', exact: true });
    await expect(editor).toContainText('Consolidate continuity (version 1)'); await expect(editor.getByRole('combobox', { name: 'Automation starting recipe', exact: true })).toHaveCount(0);
    let intercepted = false, writes = 0;
    const exact = resource + '/' + item.automationId;
    await page.route('**' + exact, async request => {
      if (request.request().method() !== 'PUT' || intercepted) return request.continue();
      intercepted = true; writes++; const response = await request.fetch(); expect(response.ok()).toBeTruthy();
      return request.fulfill({ response, json: { ...(await response.json()), presetId: 'improve-harness' } });
    });
    await editor.getByRole('textbox', { name: 'Automation name', exact: true }).fill(`Recovered preset ${nonce}`);
    await editor.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(editor.getByText('Outcome unknown', { exact: true })).toBeVisible();
    await expect(editor).not.toContainText('The change was saved'); await expect(page.getByText('Automation saved. Reading current registrations.', { exact: true })).toHaveCount(0);
    await expect(editor.getByRole('button', { name: 'Save Automation', exact: true })).toBeDisabled(); await page.screenshot({ path: info.outputPath('preset-uncertain.png'), fullPage: true, animations: 'disabled' });
    expect(writes).toBe(1); await page.unroute('**' + exact); await page.goto(route); await expect(page.getByText(`Recovered preset ${nonce}`, { exact: true })).toBeVisible();
    item = (await call<{ items: typeof item[] }>(api, resource)).items[0]; expect(item.presetId).toBe('consolidate-continuity'); expect(item.revision).toBe(2);
    await page.getByRole('button', { name: 'Edit Automation', exact: true }).click(); editor = page.getByRole('dialog', { name: 'Saved Automation', exact: true });
    await expect(editor).toContainText('Consolidate continuity (version 1)'); await page.screenshot({ path: info.outputPath('preset-saved-origin.png'), fullPage: true, animations: 'disabled' });
    await select(page, 'Schedule timing', 'Daily'); await editor.getByRole('checkbox', { name: 'Enable automatic occurrences', exact: true }).check();
    await editor.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(editor).toHaveCount(0);
    await page.getByRole('button', { name: 'Run now', exact: true }).click(); await page.getByRole('dialog', { name: 'Run saved Automation now?', exact: true }).getByRole('button', { name: 'Run now', exact: true }).click();
    const activity = page.getByRole('link', { name: 'Open the admitted Run in Activity', exact: true }); await expect(activity).toBeVisible(); const href = (await activity.getAttribute('href'))!;
    const id = new URL(href, 'http://localhost').searchParams.get('run')!;
    await expect.poll(async () => (await call<{ status: string }>(api, root + `/agent-runs/${id}`)).status, { timeout: 30000 }).toBe('completed');
    await activity.click(); const result = page.getByRole('dialog', { name: 'Run details', exact: true }); await expect(result).toContainText('Completed');
    await page.mouse.move(4, 4);
    await expect(page.locator('.ant-message-notice')).toHaveCount(0);
    await page.screenshot({ path: info.outputPath('preset-result.png'), fullPage: true, animations: 'disabled' });
    await page.goto(route);
    await page.route('**' + root + '/events/builtin', request => request.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ reasonCode: 'catalog_unavailable' }) }));
    await page.getByRole('button', { name: 'New Automation', exact: true }).click();
    editor = page.getByRole('dialog', { name: 'New Automation', exact: true }); await select(page, 'Automation starting recipe', 'Review recent work');
    const sourceWarning = editor.getByText('You can save a structurally valid disabled draft.', { exact: false });
    await expect(sourceWarning).toBeVisible(); await sourceWarning.scrollIntoViewIfNeeded();
    await expect(editor.getByRole('button', { name: 'Save Automation', exact: true })).toBeEnabled();
    await page.screenshot({ path: info.outputPath('preset-source-unavailable.png'), fullPage: true, animations: 'disabled' });
    await editor.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(editor).toHaveCount(0);
    const recipes = (await call<{ items: { presetId: string; status: string }[] }>(api, resource)).items;
    expect(recipes.some(row => row.presetId === 'review-recent-work' && row.status === 'disabled')).toBeTruthy();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy(); expect(errors).toEqual([]); expect(legacy).toEqual([]);
  } finally { await api.dispose(); }
});
