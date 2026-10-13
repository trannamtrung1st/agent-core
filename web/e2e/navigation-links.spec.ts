import { expect, test, type Locator, type Page } from '@playwright/test';
import { navigateAdmin } from './support/admin-navigation';
test.use({ trace: 'retain-on-failure' });
async function openInNewTab(page: Page, link: Locator, content: 'Instances table' | 'Overview' | 'Automations', gesture: 'modifier' | 'middle' = 'modifier') {
  const source = page.url(); const href = await link.getAttribute('href'); expect(href).toBeTruthy();
  const popup = page.context().waitForEvent('page');
  await link.click(gesture === 'middle' ? { button: 'middle' } : { modifiers: ['ControlOrMeta'] });
  const opened = await popup; const destination = new URL(href!, source).href;
  // A modifier-click opens a background tab; select it before asserting its hydrated UI.
  await opened.bringToFront();
  const bootstrapStarted = Date.now();
  // Chromium can leave Playwright's popup navigation marker pending after the document
  // has loaded. Read the actual document instead of disabling navigation checks globally.
  await expect.poll(() => opened.evaluate(({ destination, content }) => {
    const matches = content === 'Instances table'
      ? [...document.querySelectorAll('table[aria-label="Instances table"]')]
      : [...document.querySelectorAll('h2, [role="heading"][aria-level="2"]')].filter(element => element.textContent?.trim() === content);
    return location.href === destination && document.readyState === 'complete' && matches.some(element => {
      const style = getComputedStyle(element); const rect = element.getBoundingClientRect();
      return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
    });
  }, { destination, content }), { timeout: 15_000, message: `Native popup displays ${content} at its canonical URL` }).toBe(true);
  console.log(`Native popup bootstrap: ${Date.now() - bootstrapStarted}ms`);
  await expect(page).toHaveURL(source); return opened;
}
for (const width of [1440, 390]) test(`native canonical links preserve source drafts at ${width}px`, async ({ page }) => {
  test.setTimeout(90_000); await page.setViewportSize({ width, height: 900 });
  await page.goto('/'); await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  const created = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'examiner', version: 1 } }); expect(created.ok()).toBe(true);
  const { instanceId } = await created.json(); const base = `/admin/instances/${instanceId}`;
  const admin = await openInNewTab(page, page.getByRole('link', { name: 'Open Admin', exact: true }), 'Instances table', 'middle');
  await admin.close();
  await page.getByRole('link', { name: 'Open Admin', exact: true }).click();
  await page.getByRole('textbox', { name: 'Search instances', exact: true }).fill(instanceId);
  const instance = page.locator(`a[href="${base}/overview"]`); const detail = await openInNewTab(page, instance, 'Overview');
  await detail.close(); await instance.click();
  await navigateAdmin(page, 'Profile & version'); await page.getByLabel('Persona name', { exact: true }).fill('Unsaved native navigation edit');
  const menu = page.getByRole('button', { name: 'Open Admin navigation', exact: true }); if (await menu.isVisible()) await menu.click();
  const nav = page.getByRole('navigation', { name: 'Admin navigation', exact: true });
  const source = page.url(); const automation = await openInNewTab(page, nav.getByRole('link', { name: 'Automations', exact: true }), 'Automations');
  await automation.close();
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
