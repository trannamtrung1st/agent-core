import { expect, test, type Locator, type Page } from '@playwright/test';
import { navigateAdmin } from './support/admin-navigation';
async function openInNewTab(page: Page, link: Locator, gesture: 'modifier' | 'middle' = 'modifier') {
  const source = page.url(); const href = await link.getAttribute('href'); expect(href).toBeTruthy();
  const popup = page.context().waitForEvent('page');
  await link.click(gesture === 'middle' ? { button: 'middle' } : { modifiers: ['ControlOrMeta'] });
  const opened = await popup; await opened.waitForURL(new URL(href!, source).href);
  // A modifier-click opens a background tab; select it before asserting its hydrated UI.
  await opened.bringToFront();
  await opened.waitForLoadState('domcontentloaded');
  await expect(opened.locator('.admin-layout')).toBeVisible();
  await expect(page).toHaveURL(source); return opened;
}
for (const width of [1440, 390]) test(`native canonical links preserve source drafts at ${width}px`, async ({ page }) => {
  test.setTimeout(90_000); await page.setViewportSize({ width, height: 900 });
  await page.goto('/'); await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  const created = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'examiner', version: 1 } }); expect(created.ok()).toBe(true);
  const { instanceId } = await created.json(); const base = `/admin/instances/${instanceId}`;
  const admin = await openInNewTab(page, page.getByRole('link', { name: 'Open Admin', exact: true }), 'middle');
  await expect(admin.getByRole('table', { name: 'Instances table', exact: true })).toBeVisible(); await admin.close();
  await page.getByRole('link', { name: 'Open Admin', exact: true }).click();
  await page.getByRole('textbox', { name: 'Search instances', exact: true }).fill(instanceId);
  const instance = page.locator(`a[href="${base}/overview"]`); const detail = await openInNewTab(page, instance);
  await expect(detail.getByRole('heading', { name: 'Overview', exact: true, level: 2 })).toBeVisible(); await detail.close(); await instance.click();
  await navigateAdmin(page, 'Profile & version'); await page.getByLabel('Persona name', { exact: true }).fill('Unsaved native navigation edit');
  const menu = page.getByRole('button', { name: 'Open Admin navigation', exact: true }); if (await menu.isVisible()) await menu.click();
  const nav = page.getByRole('navigation', { name: 'Admin navigation', exact: true });
  const source = page.url(); const automation = await openInNewTab(page, nav.getByRole('link', { name: 'Automations', exact: true }));
  await expect(automation.getByRole('heading', { name: 'Automations', exact: true, level: 2 })).toBeVisible(); await automation.close();
  if (width < 1024) { await page.bringToFront(); await page.keyboard.press('Escape'); await expect(page.getByRole('dialog', { name: 'Admin navigation', exact: true })).toBeHidden(); }
  await expect(page).toHaveURL(source); await expect(page.getByLabel('Persona name', { exact: true })).toHaveValue('Unsaved native navigation edit');
  await page.getByRole('link', { name: 'Chat', exact: true }).click(); await page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true }).getByRole('button', { name: 'Stay', exact: true }).click(); await expect(page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true })).toBeHidden();
  await expect(page.getByLabel('Persona name', { exact: true })).toHaveValue('Unsaved native navigation edit');
  // Back traverses to this same Instance Overview, preserving the draft; leaving the owner asks.
  await page.goBack(); await expect(page).toHaveURL(new URL(`${base}/overview`, page.url()).href);
  await page.goBack(); await page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true }).getByRole('button', { name: 'Stay', exact: true }).click(); await expect(page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true })).toBeHidden();
  await expect(page).toHaveURL(new URL(`${base}/overview`, page.url()).href);
  await page.goBack(); await page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true }).getByRole('button', { name: 'Discard and leave', exact: true }).click(); await expect(page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true })).toBeHidden();
  await expect(page).toHaveURL(/\/admin\/instances$/);
});
