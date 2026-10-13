import { expect, test, type Page } from '@playwright/test';
import { navigateAdmin } from './support/admin-navigation';
async function fixture(page: Page) {
  await page.goto('/'); await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  const created = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'general-assistant', version: 24 } });
  expect(created.ok()).toBe(true); const { instanceId } = await created.json(); await page.goto(`/admin/instances/${instanceId}/profile`);
  return { instanceId, headers, api: `/api/v2/admin/agent-instances/${instanceId}` };
}
async function select(page: Page, name: string, label: string) {
  await page.getByRole('combobox', { name, exact: true }).click(); await page.locator('.ant-select-item-option:visible').filter({ hasText: new RegExp('^' + label + '$') }).click();
}
test('one save persists persona, sparse settings, budgets, policy and unattended edits with current revisions', async ({ page }) => {
  const { instanceId, api, headers } = await fixture(page);
  await page.getByLabel('Persona name', { exact: true }).fill('Shared configuration save');
  await navigateAdmin(page, 'Instructions');
  await expect(page.getByRole('button', { name: /Customize Operating instructions/ })).toHaveCount(0);
  await expect(page.getByLabel('Operating instructions', { exact: true })).toBeEnabled();
  await page.getByLabel('Operating instructions', { exact: true }).fill('Sparse retained instructions');
  await navigateAdmin(page, 'Conversation'); await page.getByLabel('Language', { exact: true }).fill('fr-FR');
  await navigateAdmin(page, 'Execution budgets'); await select(page, 'Standard inheritance', 'Override');
  await navigateAdmin(page, 'Self-management'); await select(page, 'Self-management mode', 'Off');
  await navigateAdmin(page, 'Unattended model'); await select(page, 'Unattended model', 'Scripted Beta');
  await page.getByRole('button', { name: 'Save configuration', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Instance configuration status' })).toContainText('Configuration saved.');
  const effectiveResponse = await page.request.get(`/api/v2/admin/instances/${instanceId}/effective-config`, { headers }); expect(effectiveResponse.ok()).toBe(true); const effective = await effectiveResponse.json();
  expect(effective.persona.name).toBe('Shared configuration save'); expect(effective.unattendedModelCatalogKey).toBe('scripted-beta');
  const settings = await (await page.request.get(api + '/settings', { headers })).json();
  expect(settings.find((s: { section: string }) => s.section === 'instructions').overrides).toEqual({ systemInstructions: 'Sparse retained instructions' });
  expect(settings.find((s: { section: string }) => s.section === 'conversationPolicy').overrides).toEqual({ language: 'fr-FR' });
  expect((await (await page.request.get(api + '/execution-budgets', { headers })).json()).executionBudgets.standard.preset).toBe(0);
  expect((await (await page.request.get(api + '/self-management', { headers })).json()).effective.mode).toBe('Disabled');
  await navigateAdmin(page, 'Conversation'); await page.getByRole('button', { name: 'Reset Language', exact: true }).click();
  await page.getByRole('button', { name: 'Save configuration', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Instance configuration status' })).toContainText('Configuration saved.');
  const reset = await (await page.request.get(api + '/settings', { headers })).json();
  expect(reset.find((s: { section: string }) => s.section === 'conversationPolicy').overrides).toEqual({});
});
test('partial failure preserves unsaved fields, refresh repairs revisions and markers keep inputs aligned', async ({ page }) => {
  const { api } = await fixture(page); await navigateAdmin(page, 'Instructions'); await page.getByLabel('Operating instructions', { exact: true }).fill('First committed section');
  await navigateAdmin(page, 'Conversation'); const language = page.getByLabel('Language', { exact: true });
  const before = await language.evaluate(el => el.getBoundingClientRect().top - el.closest('form')!.getBoundingClientRect().top);
  await language.fill('fr-FR'); expect(await language.evaluate(el => el.getBoundingClientRect().top - el.closest('form')!.getBoundingClientRect().top)).toBe(before);
  let fail = true; await page.route(`**${api}/settings/conversationPolicy`, route => fail ? route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Temporary settings write failure', status: 503 }) }) : route.continue());
  await page.getByRole('button', { name: 'Save configuration', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Instance configuration actions' }).getByRole('alert')).toContainText('1 settings section(s) were saved.');
  await expect(language).toHaveValue('fr-FR'); fail = false;
  await page.getByRole('button', { name: 'Refresh configuration', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save configuration', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Save configuration', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Instance configuration status' })).toContainText('Configuration saved.'); await expect(language).toHaveValue('fr-FR');
  await language.fill('de-DE'); await page.getByRole('button', { name: 'Discard configuration changes', exact: true }).click(); await expect(language).toHaveValue('fr-FR');
  for (const width of [1440, 1024, 768, 767, 390, 320]) {
    await page.setViewportSize({ width, height: 900 }); await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
    const brand = page.locator('.admin-brand'); const actions = page.locator('.admin-header-actions');
    await expect.poll(async () => { const b = (await brand.boundingBox())!; const a = (await actions.boundingBox())!; return b.x + b.width <= a.x; }).toBe(true);
    expect(await page.locator('.admin-brand-area').evaluate(el => el.getBoundingClientRect().right <= el.closest('.admin-brand')!.getBoundingClientRect().right)).toBe(true);
  }
});
