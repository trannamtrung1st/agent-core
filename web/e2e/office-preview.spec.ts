import { test, expect } from '@playwright/test';
import { selectInstanceIdentity } from './support/instance-identity';
import { readFile } from 'node:fs/promises';
test('agent creates real Office files, previews sheets/formulas and isolated Word pages, then downloads originals', async ({
  page,
}) => {
  test.setTimeout(120000);
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  const external: string[] = [];
  page.on('request', (request) => {
    if (
      !request.url().startsWith('http://127.0.0.1') &&
      !request.url().startsWith('data:') &&
      !request.url().startsWith('blob:')
    )
      external.push(request.url());
  });
  await page.goto('/');
  await selectInstanceIdentity(page, { id: 'general-assistant', version: 26 });
  await page.getByLabel('Message').fill('synthetic-office: q3-sales');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  const sheetOpen = page.getByRole('button', { name: 'Preview q3-sales.xlsx' });
  await expect(sheetOpen).toBeVisible({ timeout: 45000 });
  await sheetOpen.click();
  const sheet = page.getByRole('dialog', { name: 'q3-sales.xlsx' });
  await expect(
    sheet.getByRole('tab', { name: 'Sales', exact: true }),
  ).toBeVisible();
  await sheet.getByRole('button', { name: /B4.*formula/ }).click();
  await expect(sheet).toContainText('SUM(B2:B3)');
  await expect(sheet).toContainText('cached value unavailable');
  await sheet.getByRole('tab', { name: 'Assumptions', exact: true }).click();
  await expect(sheet.getByRole('button', { name: /B2 0001/ })).toBeVisible();
  await sheet.getByLabel('Worksheet range').fill('A1:B201');
  await sheet.getByRole('button', { name: 'Load range', exact: true }).click();
  await expect(sheet).toContainText('unsupported, unsafe or corrupt');
  await sheet.getByLabel('Worksheet range').fill('A1:B2');
  await sheet.getByRole('button', { name: 'Load range', exact: true }).click();
  await expect(sheet.getByRole('button', { name: /B2 0001/ })).toBeVisible();
  const pending = page.waitForEvent('download');
  await sheet.getByRole('button', { name: 'Download q3-sales.xlsx' }).click();
  const download = await pending;
  expect((await readFile((await download.path())!)).subarray(0, 4)).toEqual(
    Buffer.from([80, 75, 3, 4]),
  );
  await sheet.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(sheet).toHaveCount(0);
  await expect(sheetOpen).toBeFocused();
  const wordOpen = page.getByRole('button', { name: 'Preview q3-report.docx' });
  await wordOpen.click();
  const word = page.getByRole('dialog', { name: 'q3-report.docx' });
  const frame = page.frameLocator('iframe[title="Word document preview"]');
  await expect(frame.getByText('Q3 sales report', { exact: true })).toBeVisible(
    { timeout: 20000 },
  );
  await expect
    .poll(() =>
      frame
        .locator('section.docx')
        .first()
        .evaluate((el) => getComputedStyle(el).backgroundColor),
    )
    .toBe('rgb(255, 255, 255)');
  await expect(
    frame.getByText('Recommendations', { exact: true }),
  ).toBeVisible();
  await expect(word.locator('iframe')).toHaveAttribute(
    'sandbox',
    'allow-same-origin',
  );
  expect(await frame.locator('script,form,object,embed').count()).toBe(0);
  for (const width of [1440, 1024, 768, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await expect
      .poll(() =>
        page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
      )
      .toBe(true);
    await expect(
      word.getByRole('button', { name: 'Download q3-report.docx' }),
    ).toBeVisible();
  }
  await frame.getByText('Q3 sales report', { exact: true }).click();
  await page.keyboard.press('Escape');
  await expect(word).toHaveCount(0);
  await expect(wordOpen).toBeFocused();
  expect(errors).toEqual([]);
  expect(external).toEqual([]);
});
