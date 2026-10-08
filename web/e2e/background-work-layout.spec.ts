import { expect, test } from '@playwright/test';
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from './support/instance-identity';
import { backgroundFixture, mockBackgroundSessions } from './support/background-fixtures';

test('long run outcomes scroll independently and remain usable across drawer widths', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  const item = backgroundFixture(1, 'Sample hello in 1 minute', { outcome: { kind: 'NeedsAttention',
    summary: 'Sample hello!\n' + 'This is the owner-facing scheduled greeting.\n'.repeat(70), outcomeEntryId: 'result', attentionRequired: true } });
  await mockBackgroundSessions(page, [item]);
  await page.goto('/'); await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await page.getByLabel('Message', { exact: true }).fill('hello'); await page.getByRole('button', { name: 'Send', exact: true }).click();
  await page.getByRole('button', { name: 'Background work, 1 need attention', exact: true }).click();
  await page.getByRole('dialog', { name: 'Background work', exact: true }).getByRole('button', { name: 'View history', exact: true }).click();
  const drawer = page.getByRole('dialog', { name: item.session.title, exact: true });
  const result = drawer.getByRole('region', { name: 'Needs attention', exact: true });
  await expect(result).toContainText('Sample hello!');
  for (const [width, height] of [[1440, 900], [768, 900], [390, 844]]) {
    await page.setViewportSize({ width, height });
    await expect.poll(async () => Math.round((await drawer.boundingBox())!.width)).toBe(width >= 768 ? 640 : width);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await result.focus(); const before = await drawer.locator('.ant-drawer-body').evaluate(e => e.scrollTop);
    await page.keyboard.press('End'); await expect.poll(() => result.evaluate(e => e.scrollTop)).toBeGreaterThan(0);
    expect(await result.evaluate(e => e.clientHeight)).toBeLessThanOrEqual(256);
    expect(await drawer.locator('.ant-drawer-body').evaluate(e => e.scrollTop)).toBe(before);
    await result.evaluate(e => { e.scrollTop = 0; });
  }
  item.latestRun = { ...item.latestRun!, revision: 4, outcome: { ...item.latestRun!.outcome!, summary: 'Sample hello! This is a one-time reminder.' } };
  await expect(result).toHaveText(item.latestRun.outcome!.summary, { timeout: 10000 });
  expect(await result.evaluate(e => e.scrollHeight <= e.clientHeight)).toBe(true);
  await page.keyboard.press('Escape'); await expect(drawer).toBeHidden(); expect(errors).toEqual([]);
});
