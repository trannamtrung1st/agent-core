import { expect, test } from '@playwright/test';

test('Recurring continuity review uses the shared Automation editor and Continuity retains data controls', async ({ page }) => {
  test.setTimeout(120_000);
  await page.goto('/admin');
  await page.getByRole('tab', { name: 'Instances', exact: true }).click();
  await page.getByRole('button', { name: 'New instance', exact: true }).click();
  for (const [label, text] of [['Definition', 'general-assistant'], ['Published version', 'v16']] as const) {
    const input = page.getByRole('combobox', { name: label, exact: true }); await input.click(); await input.fill(text);
    await page.locator('.ant-select-item-option').filter({ hasText: label === 'Definition' ? 'General Assistant' : 'v16 · Built-in · Published' }).click();
  }
  await page.getByRole('button', { name: 'Create instance', exact: true }).click();
  await page.getByRole('tab', { name: 'Continuity', exact: true }).click();
  await page.getByRole('tab', { name: 'Experience', exact: true }).click();
  await expect(page.getByRole('switch', { name: 'Enable experience', exact: true })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Automatic continuity review' })).toHaveCount(0);
  await page.getByRole('tab', { name: 'Automation', exact: true }).click();
  const region = page.getByRole('region', { name: 'Automations', exact: true });
  await region.getByRole('button', { name: 'New automation', exact: true }).click();
  await region.getByLabel('Automation name', { exact: true }).fill('Daily continuity review');
  await region.getByLabel('Automation instructions', { exact: true }).fill('Inspect retained Memory and Experience. Consolidate only safe redundant observations when permission allows. Do nothing otherwise.');
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(region.getByLabel('Schedule local time', { exact: true })).toBeVisible();
    await expect(region.getByRole('button', { name: 'Create automation', exact: true })).toBeEnabled();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  }
  await region.getByRole('button', { name: 'Create automation', exact: true }).click();
  const source = region.getByRole('button', { name: 'View automation: Daily continuity review', exact: true });
  await expect(source).toBeVisible();
  if (await source.getAttribute('aria-expanded') !== 'true') await source.click();
  await region.getByRole('button', { name: 'Run automation now', exact: true }).click();
  await expect(region.getByText(/Completed · No action/).first()).toBeVisible({ timeout: 30_000 });
  await page.reload();
  await expect(region.getByRole('button', { name: 'View automation: Daily continuity review', exact: true })).toBeVisible();
});
