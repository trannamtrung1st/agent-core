import { expect, type Locator } from '@playwright/test';
export async function selectSpreadsheetCell(dialog: Locator, address: string, value?: string) {
  await expect(dialog.getByRole('region', { name: 'Read-only spreadsheet', exact: true })).toBeVisible();
  await dialog.getByLabel('Selected cell', { exact: true }).fill(address);
  await dialog.getByLabel('Selected cell', { exact: true }).press('Enter');
  await expect(dialog.locator('.file-preview-cell-detail')).toContainText(address);
  if (value !== undefined) {
    await expect(dialog.locator('.file-preview-cell-detail')).toContainText(value);
    await dialog.page().context().grantPermissions(['clipboard-read', 'clipboard-write']);
    await dialog.getByRole('button', { name: /^(Copy selected range|Copied|Copy failed)$/ }).click();
    expect(await dialog.page().evaluate(() => navigator.clipboard.readText())).toContain(value);
  }
}
