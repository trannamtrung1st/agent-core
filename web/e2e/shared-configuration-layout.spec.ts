import { expect, test } from '@playwright/test';

test('Shared Admin panels keep aligned insets and usable actions across viewport boundaries', async ({ page }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/admin/instances');
  await page.getByRole('button', { name: 'New instance', exact: true }).click();
  await page.getByRole('combobox', { name: 'Definition', exact: true }).click();
  await page.locator('.ant-select-item-option').filter({ hasText: 'General Assistant · general-assistant' }).click();
  await page.getByRole('button', { name: 'Create instance', exact: true }).click();
  await expect(page.getByLabel('Persona name', { exact: true })).toBeVisible();
  async function selectTab(name: string) {
    // Ant Design's tablet overflow can place a tab outside the visible strip.
    // Focusing also adds a positional accessible label; send Enter through the
    // keyboard rather than resolving the old exact name a second time.
    await page.getByRole('tab', { name, exact: true }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('tab', { name: new RegExp(`${name}$`) })).toHaveAttribute('aria-selected', 'true');
  }

  for (const width of [1440, 768, 767, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const surfaces = [
      { tab: 'Identity & version', section: 'Workspace', labels: ['Agent Workspace'] },
      { tab: 'Skills & resources', section: 'Skills', labels: ['Definition Skills', 'Instance Skills'] },
      { tab: 'Skills & resources', section: 'Resources', labels: ['Definition resources', 'Instance resources'] },
      { tab: 'Continuity', section: 'Memory', labels: ['Identity maintenance', 'Learned memory administration'] },
      { tab: 'Continuity', section: 'Experience', labels: ['Experience'] },
      { tab: 'Automation', section: 'Triggers', labels: ['Automations'] },
      { tab: 'Automation', section: 'Policies & models', labels: ['Harness management', 'Execution defaults'] },
      { tab: 'Credentials', labels: ['Credential bindings', 'Browser state'] },
      { tab: 'Effective configuration', labels: ['Effective configuration'] }
    ];
    for (const surface of surfaces) {
      await selectTab(surface.tab);
      if (surface.section) await selectTab(surface.section);
      for (const label of surface.labels) {
        const panel = page.getByRole('region', { name: label, exact: true });
        await expect(panel).toBeVisible();
        const heading = panel.locator(':scope > .admin-definition-panel-heading');
        const body = panel.locator(':scope > .admin-definition-panel-body');
        await expect(heading).toHaveCSS('gap', '8px');
        await expect(heading).toHaveCSS('padding', '16px');
        await expect(body).toHaveCSS('padding', '16px');
        const headingBox = (await heading.boundingBox())!;
        const bodyBox = (await body.boundingBox())!;
        expect(Math.abs(headingBox.x - bodyBox.x)).toBeLessThanOrEqual(1);
        expect(Math.abs(headingBox.width - bodyBox.width)).toBeLessThanOrEqual(1);
      }
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
    }
    await selectTab('Identity & version');
    await selectTab('Workspace');
    await page.getByRole('button', { name: 'Reload workspace', exact: true }).click();
    await expect(page.getByText('No workspace items yet', { exact: true })).toBeVisible();
  }
  expect(errors).toEqual([]);
});
