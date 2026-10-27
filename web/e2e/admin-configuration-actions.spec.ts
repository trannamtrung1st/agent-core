import { expect, test, type Page } from '@playwright/test';
import { navigateAdmin } from './support/admin-navigation';
async function fixture(page: Page) {
  await page.goto('/'); await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  const created = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'general-assistant', version: 35 } });
  expect(created.ok()).toBe(true); const { instanceId } = await created.json(); await page.goto(`/admin/instances/${instanceId}/profile`);
  return { instanceId, headers, api: `/api/v2/admin/agent-instances/${instanceId}` };
}
async function select(page: Page, name: string, label: string) {
  await page.getByRole('combobox', { name, exact: true }).click(); await page.locator('.ant-select-item-option:visible').filter({ hasText: new RegExp('^' + label + '$') }).click();
}
test('archived unattended navigation recovers to Lifecycle and clears its warning after unarchive', async ({ page }) => {
  const { instanceId } = await fixture(page);
  await navigateAdmin(page, 'Lifecycle');
  await page.getByRole('button', { name: 'Archive instance', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Archive', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Unarchive instance', exact: true })).toBeVisible();
  await page.goto(`/admin/instances/${instanceId}/unattended`);
  await expect(page).toHaveURL(new RegExp(`/lifecycle\\?notice=archived-automations$`));
  const warning = page.getByRole('alert').filter({ hasText: 'Automations are unavailable for this archived Instance' });
  await expect(warning).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save configuration', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Unarchive instance', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Archive instance', exact: true })).toBeVisible();
  await expect(warning).toHaveCount(0);
  await navigateAdmin(page, 'Unattended model');
  await expect(page.getByRole('heading', { name: 'Unattended model', level: 2, exact: true })).toBeVisible();
});
test('one save persists persona, sparse settings, budgets, policy and unattended edits with current revisions', async ({ page }) => {
  const { instanceId, api, headers } = await fixture(page);
  await page.getByLabel('Persona name', { exact: true }).fill('Shared configuration save');
  await navigateAdmin(page, 'Instructions');
  await expect(page.getByRole('button', { name: /Customize Agent instructions/ })).toHaveCount(0);
  await expect(page.getByLabel('Agent instructions', { exact: true })).toBeEnabled();
  await page.getByLabel('Agent instructions', { exact: true }).fill('Sparse retained instructions');
  await navigateAdmin(page, 'Conversation'); await page.getByLabel('Language', { exact: true }).fill('fr-FR');
  await navigateAdmin(page, 'Execution budgets'); await select(page, 'Standard inheritance', 'Override');
  await navigateAdmin(page, 'Self-management'); await select(page, 'Self-management mode', 'Off');
  await navigateAdmin(page, 'Unattended model'); await select(page, 'Unattended model', 'Scripted Beta');
  await page.getByRole('button', { name: 'Save configuration', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Instance configuration status' })).toContainText('Configuration saved.');
  const effectiveResponse = await page.request.get(`/api/v2/admin/instances/${instanceId}/effective-config`, { headers }); expect(effectiveResponse.ok()).toBe(true); const effective = await effectiveResponse.json();
  expect(effective.persona.name).toBe('Shared configuration save'); expect(effective.unattendedOfferingId).toBe('dca30000-0000-4000-8000-000000000002');
  const settings = await (await page.request.get(api + '/settings', { headers })).json();
  expect(settings.find((s: { section: string }) => s.section === 'instructions').overrides).toEqual({ agentInstructions: 'Sparse retained instructions' });
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
  const { api } = await fixture(page); await navigateAdmin(page, 'Instructions'); await page.getByLabel('Agent instructions', { exact: true }).fill('First committed section');
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
