import { expect, type Locator, type Page } from '@playwright/test';
async function visibleAdminNavigation(page: Page) {
  const nav = page.getByRole('navigation', { name: 'Admin navigation', exact: true });
  // Wait for AntD's responsive subscription after a viewport change before choosing a control.
  if (await page.evaluate(() => matchMedia('(min-width: 1024px)').matches)) {
    await expect(page.locator('.admin-sidebar')).toBeVisible();
  } else {
    const toggle = page.getByRole('button', { name: 'Open Admin navigation', exact: true });
    await expect(toggle).toBeVisible();
    if (!await page.locator('.admin-navigation-drawer.ant-drawer-open').isVisible()) {
      await expect(nav).toBeHidden();
      await toggle.click();
    }
    await expect.poll(() => page.locator('.admin-navigation-drawer .ant-drawer-content-wrapper').evaluate(element => Math.round(element.getBoundingClientRect().x))).toBe(0);
  }
  await expect(nav).toBeVisible();
  return nav;
}
/** Navigate through the visible canonical rail, opening the responsive Drawer when needed. */
export async function navigateAdmin(target: Page | Locator, oldLabel: string) {
  const page = 'goto' in target ? target : target.page();
  const local = target.getByRole('tab', { name: oldLabel, exact: true });
  // Immutable inspectors retain local read-only view controls; they are not workspace routes.
  // Their region mounts before the asynchronous version read creates its tabs.
  if (!('goto' in target) && (await target.getAttribute('role') === 'dialog' || await target.locator('xpath=ancestor::*[@role="dialog"]').count())) {
    await expect(local.first()).toBeVisible();
    await local.first().click();
    return;
  }
  if (await local.first().isVisible().catch(() => false)) { await local.first().click(); return; }
  const draft = /\/drafts\/[^/]+\//.test(new URL(page.url()).pathname);
  const names: Record<string, string> = { 'Identity & version': draft ? 'Identity & goals' : 'Profile', Profile: draft ? 'Identity & goals' : 'Profile', Settings: 'Instructions', 'Skills & resources': 'Skills', Continuity: 'Memory', Automation: 'Automations', Triggers: 'Automations', 'Policies & models': 'Unattended model', Activity: 'Sessions', Credentials: /\/instances\//.test(page.url()) ? 'Credential bindings' : 'Credentials', Connections: 'Credentials' };
  const label = names[oldLabel] ?? oldLabel;
  const nav = await visibleAdminNavigation(page);
  await nav.getByRole('link', { name: label, exact: true }).click();
  await expect(page.getByRole('heading', { name: label, exact: true, level: 2 })).toBeVisible();
  if (oldLabel === 'Policies & models') await page.locator('#execution-defaults').scrollIntoViewIfNeeded();
}
export async function expectAdminPage(page: Page, label: string) {
  await expect(page.getByRole('heading', { name: label, exact: true, level: 2 })).toBeVisible();
}
export async function expandAdminSection(target: Page | Locator, name: string) {
  const page = 'goto' in target ? target : target.page();
  const owners: Record<string, string> = { 'Agent instructions': 'Instructions', Conversation: 'Conversation', Behavior: 'Behavior', Initiative: 'Initiative', Voice: 'Voice', 'Model defaults': 'Models', 'Provider preferences': 'Providers', 'Memory policy': 'Memory policy', 'Trigger restrictions': 'Trigger restrictions' };
  if (owners[name] && !await target.getByRole('button', { name: new RegExp('^' + name) }).first().isVisible().catch(() => false)) await navigateAdmin(page, owners[name]);
  const header = target.getByRole('button', { name: new RegExp('^' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) });
  if (await header.getAttribute('aria-expanded') !== 'true') await header.click();
}
export async function backToInventory(page: Page) {
  const nav = await visibleAdminNavigation(page);
  await nav.getByRole('link', { name: /Back to (Instances|Definitions)/ }).click();
}
export async function backToDrafts(page: Page) {
  const nav = await visibleAdminNavigation(page);
  await nav.getByRole('link', { name: 'Back to drafts', exact: true }).click();
}
