import { expect, test } from '@playwright/test';
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from './support/instance-identity';
import { backgroundFixture, mockBackgroundSessions } from './support/background-fixtures';

test('long run outcomes scroll independently and remain usable across drawer widths', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  const item = backgroundFixture(1, 'Review pending orders, customer requests and outstanding follow-ups before preparing the weekly operations report', { progress: 'Processing/' + 'unbroken-path'.repeat(35), modelCatalogKey: 'long-model-'.repeat(11), outcome: { kind: 'NeedsAttention',
    summary: 'Sample hello!\n' + 'This is the owner-facing scheduled greeting.\n'.repeat(70), outcomeEntryId: 'result', attentionRequired: true } });
  item.origin.automationId = '90000000-0000-4000-8000-999999999998';
  await mockBackgroundSessions(page, [item]);
  await page.goto('/'); await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await page.getByLabel('Message', { exact: true }).fill('hello'); await page.getByRole('button', { name: 'Send', exact: true }).click();
  await page.getByRole('button', { name: 'Background work, 1 need attention', exact: true }).click();
  const list = page.getByRole('dialog', { name: 'Background work', exact: true });
  await expect(list.getByRole('heading', { name: item.session.title, exact: true })).toBeVisible();
  await expect(list.getByRole('button', { name: item.session.title, exact: true })).toHaveCount(0);
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect.poll(async () => Math.round((await list.boundingBox())!.width)).toBe(width >= 768 ? 640 : width);
    const heading = list.getByRole('heading', { name: item.session.title, exact: true });
    await expect(heading).toBeVisible();
    expect(await heading.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    const history = (await list.getByRole('button', { name: 'View original result', exact: true }).boundingBox())!;
    const continueChat = (await list.getByRole('button', { name: 'Continue in chat', exact: true }).boundingBox())!;
    const automation = (await list.getByRole('link', { name: 'View Automation', exact: true }).boundingBox())!;
    expect(Math.abs(history.y + history.height / 2 - continueChat.y - continueChat.height / 2)).toBeLessThan(1);
    if (width >= 768) expect(Math.abs(history.y + history.height / 2 - automation.y - automation.height / 2)).toBeLessThan(1);
    // The drawer slides as viewport changes; wait for its geometry, not only its width.
    await expect.poll(async () => {
      const bounds = (await list.boundingBox())!;
      const action = (await list.getByRole('link', { name: 'View Automation', exact: true }).boundingBox())!;
      return action.x + action.width <= bounds.x + bounds.width;
    }).toBe(true);
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
  await list.getByRole('button', { name: 'View original result', exact: true }).click();
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
  item.initialRun = { ...item.initialRun!, revision: 4, outcome: { ...item.initialRun!.outcome!, summary: 'Sample hello! This is a one-time reminder.' } };
  await expect(result).toHaveText(item.initialRun.outcome!.summary, { timeout: 10000 });
  expect(await result.evaluate(e => e.scrollHeight <= e.clientHeight)).toBe(true);
  await page.keyboard.press('Escape'); await expect(drawer).toBeHidden(); expect(errors).toEqual([]);
});
