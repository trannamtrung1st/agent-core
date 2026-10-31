import { expect, type Page } from '@playwright/test';
import { tenantPages, instancePages } from '../../src/features/access/experienceNavigation';

/** Follow the real contextual navigation, including its narrow-screen disclosure. */
export async function openDestination(page: Page, label: string) {
  await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);
  const workspace = page.getByRole('button', { name: 'Workspace menu', exact: true });
  if (await workspace.isVisible()) {
    await workspace.click();
    const entry = page.getByRole('menuitem', { name: label, exact: true });
    if (await entry.count()) { await entry.click(); return; }
    await page.getByRole('menuitem', { name: 'Organization administration', exact: true }).click();
  }
  const narrow = page.getByRole('button', { name: 'Open Admin navigation', exact: true });
  if ((page.viewportSize()?.width ?? 1440) < 1024) {
    await expect(narrow).toBeVisible();
    const drawer = page.getByRole('dialog', { name: 'Admin navigation', exact: true });
    // A prior navigation can still be closing its native Drawer. Finish that
    // dismissal before opening this destination's menu, rather than reusing a
    // visible outgoing tree that is about to be retired.
    if (await drawer.isVisible()) await page.keyboard.press('Escape');
    await expect(drawer).toBeHidden();
    await narrow.click();
  }
  else await expect(narrow).toHaveCount(0);
  const nav = page.getByRole('navigation', { name: 'Admin navigation', exact: true });
  await expect(nav).toBeVisible();
  // Scope permissions arrive after the shell. Don't mistake its loading menu
  // for a definitive absence and open an unrelated scope's destinations.
  await expect(page.locator('[aria-label="Loading access"]')).toHaveCount(0);
  const pathname = new URL(page.url()).pathname;
  const agentContext = /\/admin\/agents\/[^/]+\//.test(pathname);
  const draftContext = pathname.includes('/drafts/');
  const expectedInRail = pathname.startsWith('/t/') && (agentContext
    ? instancePages.some(item => item.label === label)
    : !draftContext && tenantPages.some(item => item.label === label));
  if (expectedInRail) await expect(nav.locator(`a[aria-label=${JSON.stringify(label)}]`)).toHaveCount(1);
  let link = nav.getByRole('link', { name: label, exact: true });
  if (!await link.count()) {
    // Closed native details intentionally remove descendant accessible names.
    // Find the rendered anchor's text, then open its owning disclosure visibly.
    const hidden = nav.locator(`a[aria-label=${JSON.stringify(label)}]`);
    if (await hidden.count()) {
      await hidden.locator('xpath=ancestor::details').locator('summary').click();
      link = nav.getByRole('link', { name: label, exact: true });
    } else {
      const organization = nav.getByRole('button', { name: /Organization navigation/ });
      if ((agentContext || draftContext) && tenantPages.some(item => item.label === label)) await expect(organization).toHaveCount(1);
      if (await organization.count()) {
        await organization.click(); await expect(page.getByRole('menu')).toBeVisible();
        const destination = page.getByRole('menuitem', { name: label, exact: true });
        if (await destination.count()) { await destination.click(); return; }
        await page.keyboard.press('Escape');
      }
      // Account/platform are deliberately separate scopes.
      if (await page.getByRole('dialog', { name: 'Admin navigation', exact: true }).isVisible()) await page.keyboard.press('Escape');
      await page.getByRole('button', { name: 'Account and platform navigation', exact: true }).click();
      await page.getByRole('menuitem', { name: label, exact: true }).click(); return;
    }
  }
  await expect(link).toBeVisible(); await link.click();
}
export async function openConversationTool(page: Page, name: string | RegExp) {
  await page.getByRole('button', { name: /^Conversation tools/ }).click();
  await page.getByRole('button', { name, exact: typeof name === 'string' }).click();
}
export async function signOutFromChat(page: Page) {
  await page.getByRole('button', { name: 'Workspace menu', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Sign out', exact: true }).click();
}

/** Optional qualification evidence; ordinary feature regressions need no artifacts. */
export async function captureExperience(page: Page, name: string, widths = [1440, 390]) {
  if (!process.env.EXPERIENCE_CAPTURE_ROOT) return;
  const fs = await import('node:fs'); const path = await import('node:path');
  fs.mkdirSync(process.env.EXPERIENCE_CAPTURE_ROOT, { recursive: true });
  const prior = page.viewportSize();
  for (const width of widths) {
    await page.setViewportSize({ width, height: width < 768 ? 844 : 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: path.join(process.env.EXPERIENCE_CAPTURE_ROOT, `${name}-${width}.png`), animations: 'disabled' });
  }
  if (prior) await page.setViewportSize(prior);
}
