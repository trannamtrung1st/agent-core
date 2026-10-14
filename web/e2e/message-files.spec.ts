import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import {
  INSTANCE_DEFINITIONS,
  selectInstanceIdentity,
} from './support/instance-identity';

test('sent files share artifact cards, preserve originals, and recover downloads at narrow widths', async ({ page }, testInfo) => {
  test.setTimeout(120000);
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  const failedRequests: string[] = [];
  page.on('requestfailed', (request) => {
    if (request.failure()?.errorText !== 'net::ERR_ABORTED')
      failedRequests.push(request.url());
  });
  await page.goto('/');
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.customerSupport);
  const bytes = await readFile(
    new URL('fixtures/composer-upload.xlsx', import.meta.url),
  );
  const names = [
    'Tổng hợp các đơn vị cung cấp dịch vụ cưới.xlsx',
    'Đám cưới cần chuẩn bị những gì _.xlsx',
  ];
  await page.getByRole('button', { name: 'Add content', exact: true }).click();
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('menuitem', { name: /Attach file/ }).click();
  await (
    await chooser
  ).setFiles(
    names.map((name) => ({
      name,
      mimeType: 'application/octet-stream',
      buffer: bytes,
    })),
  );
  await expect(
    page.getByRole('button', { name: 'Send', exact: true }),
  ).toBeEnabled();
  await page
    .getByLabel('Message', { exact: true })
    .fill('Summarize these for order 91.');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  const files = page.locator('.entry-files');
  await expect(files.getByRole('group')).toHaveCount(2);
  await expect(
    page.getByRole('group', { name: 'case-note.md', exact: true }),
  ).toBeVisible({ timeout: 30000 });
  await page.reload();
  await expect(files.getByRole('group')).toHaveCount(2);
  for (const width of [1440, 768, 767, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await expect
      .poll(() =>
        page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
      )
      .toBe(true);
    await expect
      .poll(() =>
        page.locator('.artifact-card').evaluateAll((cards) =>
          cards.every((card) => {
            const rect = card.getBoundingClientRect();
            const actions = card
              .querySelector('.artifact-actions')!
              .getBoundingClientRect();
            return (
              rect.width > 180 &&
              actions.right <= rect.right &&
              card.scrollWidth <= card.clientWidth
            );
          }),
        ),
      )
      .toBe(true);
    if (width === 1440 || width === 390) await files.screenshot({ path: testInfo.outputPath(`message-files-${width}.png`) });
    const edges = await files
      .getByRole('group')
      .evaluateAll((cards) =>
        cards.map((card) => card.getBoundingClientRect().left),
      );
    expect(edges[0]).toBe(edges[1]);
    if (width < 768)
      for (const button of await files.getByRole('button').all()) {
        const box = await button.boundingBox();
        expect(box!.width).toBeGreaterThanOrEqual(40);
        expect(box!.height).toBeGreaterThanOrEqual(40);
      }
  }
  const card = files.getByRole('group', { name: names[0], exact: true });
  await card.getByText(names[0], { exact: true }).focus();
  await expect(page.getByRole('tooltip')).toContainText(names[0]);
  const opener = card.getByRole('button', {
    name: `Preview ${names[0]}`,
    exact: true,
  });
  await opener.focus();
  await page.keyboard.press('Enter');
  const modal = page.getByRole('dialog', { name: names[0], exact: true });
  await expect(modal).toContainText('Revenue');
  await page.keyboard.press('Escape');
  await expect(modal).toHaveCount(0);
  await expect(opener).toBeFocused();
  await page.route('**/attachments/*/content', (route) =>
    route.fulfill({ status: 503, body: '' }),
  );
  await card
    .getByRole('button', { name: `Download ${names[0]}`, exact: true })
    .click();
  await expect(card.getByRole('status')).toHaveText(
    'Download failed. Try again.',
  );
  await page.unroute('**/attachments/*/content');
  const pending = page.waitForEvent('download');
  await card
    .getByRole('button', { name: `Retry download ${names[0]}`, exact: true })
    .focus();
  await page.keyboard.press('Enter');
  const downloaded = await pending;
  expect(downloaded.suggestedFilename()).toBe(names[0]);
  expect(await readFile((await downloaded.path())!)).toEqual(bytes);
  await expect(card.getByRole('status')).toHaveCount(0);
  expect(errors).toEqual([]);
  expect(failedRequests).toEqual([]);
});
