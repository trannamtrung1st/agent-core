import { selectSpreadsheetCell } from './support/spreadsheet';
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
  await selectSpreadsheetCell(sheet, 'B4');
  await expect(sheet).toContainText('SUM(B2:B3)');
  await expect(sheet).toContainText('cached value unavailable');
  await sheet.getByRole('tab', { name: 'Assumptions', exact: true }).click();
  await selectSpreadsheetCell(sheet, 'B2', '0001');
  await sheet.getByLabel('Worksheet range').fill('A1:B201');
  await sheet.getByRole('button', { name: 'Load range', exact: true }).click();
  await expect(sheet).toContainText('exceeds the configured preview window');
  await sheet.getByLabel('Worksheet range').fill('A1:B2');
  await sheet.getByRole('button', { name: 'Load range', exact: true }).click();
  await selectSpreadsheetCell(sheet, 'B2', '0001');
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

test('shared spreadsheet blocks edits, clipboard HTML, context-menu mutation and keeps modal focus', async ({ page }) => {
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  const errors: string[] = [], external: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (!/^(http:\/\/127\.0\.0\.1|blob:|data:)/.test(request.url())) external.push(request.url()); });
  await page.goto('/');
  await selectInstanceIdentity(page, { id: 'general-assistant', version: 26 });
  await page.getByLabel('Message').fill('synthetic-office: q3-sales');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  const opener = page.getByRole('button', { name: 'Preview q3-sales.xlsx', exact: true });
  await expect(opener).toBeVisible({ timeout: 45000 }); await opener.click();
  const dialog = page.getByRole('dialog', { name: 'q3-sales.xlsx', exact: true });
  await selectSpreadsheetCell(dialog, 'B2');
  const rangeOverlay = dialog.locator('.fortune-sheet-overlay'); await rangeOverlay.focus();
  await page.keyboard.press('Shift+ArrowDown');
  await dialog.getByRole('button', { name: 'Copy selected range', exact: true }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe('$1,250.00\n$3,250.00');
  const zoom = dialog.getByRole('slider', { name: 'Spreadsheet zoom', exact: true });
  await zoom.focus(); await page.keyboard.press('ArrowRight');
  await expect(zoom).toHaveAttribute('aria-valuenow', '110');
  await dialog.getByRole('button', { name: 'Copied', exact: true }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe('$1,250.00\n$3,250.00');
  await selectSpreadsheetCell(dialog, 'B2');
  await dialog.getByRole('button', { name: 'Copied', exact: true }).click();
  const before = await page.evaluate(() => navigator.clipboard.readText());
  expect(before).toContain('1,250');
  const overlay = dialog.locator('.fortune-sheet-overlay'); await overlay.focus();
  await page.keyboard.type('mutated');
  for (const key of ['Delete', 'Backspace', 'Control+b', 'Control+v', 'Control+z', 'Control+y', 'Control+d']) await page.keyboard.press(key);
  await overlay.evaluate(el => { const data = new DataTransfer(); data.setData('text/html', '<table><tr><td><img src="https://invalid.test/clipboard">mutated</td></tr></table>'); el.dispatchEvent(new ClipboardEvent('paste', { bubbles: true, cancelable: true, clipboardData: data })); });
  await overlay.click({ button: 'right', position: { x: 150, y: 45 } });
  await expect(dialog.getByText('Paste', { exact: true })).toHaveCount(0);
  await expect(dialog.getByText('Insert row', { exact: true })).toHaveCount(0);
  await overlay.click({ position: { x: 150, y: 45 } });
  await selectSpreadsheetCell(dialog, 'B2');
  await dialog.getByRole('button', { name: /Copy selected range|Copied/ }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(before);
  await overlay.focus(); await page.keyboard.press('Tab');
  await expect(overlay).not.toBeFocused();
  await overlay.focus(); await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0); await expect(opener).toBeFocused();
  expect(errors).toEqual([]); expect(external).toEqual([]);
});
