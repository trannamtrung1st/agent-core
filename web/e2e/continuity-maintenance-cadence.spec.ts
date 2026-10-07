import { expect, test, type Page } from '@playwright/test';

async function select(page: Page, label: string, text: string) {
  const input = page.getByRole('combobox', { name: label, exact: true });
  await input.click(); await input.fill(text);
  await page.locator('.ant-select-item-option').filter({ hasText: text }).last().click();
}

test('Automatic continuity review separates draft and effective cadence and stays usable at Admin breakpoints', async ({ page }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');
  await page.getByRole('button', { name: 'Open Admin' }).click();
  await page.getByRole('tab', { name: 'Instances', exact: true }).click();
  await page.getByRole('button', { name: 'New instance', exact: true }).click();
  await select(page, 'Definition', 'General Assistant · general-assistant');
  await select(page, 'Published version', 'v16 · Built-in · Published');
  await page.getByRole('dialog', { name: 'New instance', exact: true }).getByRole('button', { name: 'Create instance', exact: true }).click();
  await page.getByRole('tab', { name: 'Continuity', exact: true }).click();
  await page.getByRole('tab', { name: 'Experience', exact: true }).click();
  const review = page.getByRole('region', { name: 'Automatic continuity review', exact: true });
  const input = review.getByRole('spinbutton', { name: 'Continuity review interval' });
  const useDefault = review.getByRole('switch', { name: 'Use system default for continuity review' });
  const save = review.getByRole('button', { name: 'Save review interval' });
  await expect(review.getByText('Effective interval: 5 minutes · System default')).toBeVisible();
  await expect(input).toBeDisabled();
  await useDefault.click(); await input.fill('15');
  await expect(review.getByText('Effective interval: 5 minutes · System default')).toBeVisible();
  const response = page.waitForResponse(r => r.url().endsWith('/continuity-maintenance') && r.request().method() === 'PUT');
  await save.click(); const saved = await response;
  expect(saved.ok()).toBeTruthy(); expect((await saved.json()).configuredIntervalSeconds).toBe(900);
  await expect(review.getByText('Effective interval: 15 minutes · Custom interval')).toBeVisible();
  await page.reload();
  await expect(review.getByText('Effective interval: 15 minutes · Custom interval')).toBeVisible();
  await expect(input).toHaveValue('15');
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    await input.fill('1441'); await input.blur();
    await expect(input).toHaveValue('1441');
    await expect(review.getByText('Enter an interval between 1 and 1440 minutes.')).toBeVisible();
    await expect(save).toBeDisabled();
    await input.fill('30');
    await expect(save).toBeEnabled();
    await expect(useDefault).toBeVisible(); await expect(input).toBeVisible();
    const action = await save.boundingBox(); const field = await input.boundingBox();
    expect(action!.width).toBeGreaterThan(40); expect(field!.width).toBeLessThanOrEqual(192);
    if (width === 390) expect(action!.height).toBeGreaterThanOrEqual(40);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
  }
  await useDefault.click();
  const reset = page.waitForResponse(r => r.url().endsWith('/continuity-maintenance') && r.request().method() === 'PUT');
  await save.click(); expect((await reset).ok()).toBeTruthy();
  await expect(review.getByText('Effective interval: 5 minutes · System default')).toBeVisible();
  await expect(page.getByRole('switch', { name: 'Enable experience', exact: true })).not.toBeChecked();
  expect(errors).toEqual([]);
});
