import { test, expect } from '@playwright/test';
import { selectInstanceIdentity } from './support/instance-identity';
import { readFile } from 'node:fs/promises';
test.use({ actionTimeout: 15000 });
test('connect explicit Drive binding, generate Office deliverables, verify remote previews and epoch recovery', async ({
  page,
}) => {
  test.setTimeout(180000);
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/');
  const owner = await selectInstanceIdentity(page, {
    id: 'general-assistant',
    version: 27,
  });
  const name = `Office Drive ${owner.slice(-8)}`;
  await page.goto('/admin/connections/credentials');
  await page
    .getByRole('button', { name: 'Add Google Drive Account', exact: true })
    .click();
  const create = page.getByRole('dialog', { name: 'Add Google Drive Account' });
  await create.getByRole('textbox').fill(name);
  await create
    .getByRole('button', { name: 'Create account', exact: true })
    .click();
  await create.waitFor({ state: 'hidden' });
  const row = page.getByRole('row').filter({ hasText: name });
  await row.getByRole('button', { name: 'Expand row' }).click();
  await page
    .getByRole('region', { name: `Details for ${name}` })
    .getByRole('button', { name: 'Connect with Google', exact: true })
    .click();
  await page
    .getByRole('button', { name: 'Authorize Synthetic Drive', exact: true })
    .click();
  await page.waitForURL(/\/admin\/connections\/credentials(?:\?|$)/);
  const detail = page.getByRole('region', { name: `Details for ${name}` });
  await expect(detail).toContainText('Connected · Synthetic');
  await detail
    .getByRole('button', { name: 'Check connection', exact: true })
    .click();
  await expect(detail).toContainText('Last checked');
  await page.goto(`/admin/instances/${owner}/credentials`);
  await page
    .getByRole('button', { name: 'Bind credential', exact: true })
    .click();
  const bind = page.getByRole('dialog', { name: 'Bind credential' });
  await bind.getByRole('combobox').click();
  await page.getByText(`${name} · Token · Active`, { exact: true }).click();
  await bind.getByLabel('Reference', { exact: true }).fill('drive-work');
  await bind
    .getByRole('button', { name: 'Bind credential', exact: true })
    .click();
  await bind.waitFor({ state: 'hidden' });
  await expect(
    page.getByRole('table', { name: 'Credential bindings table' }),
  ).toContainText('drive-work');
  await page.goto('/');
  const identity = page.getByRole('combobox', { name: 'Identity' });
  await expect(identity).toBeEnabled();
  await identity.click();
  await identity.fill(owner.replace(/-/g, '').slice(-8));
  await page
    .locator('.ant-select-item-option')
    .filter({ hasText: owner.replace(/-/g, '').slice(-8) })
    .click();
  await page.getByLabel('Message').fill('synthetic-office: q3-sales-drive');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Preview q3-report.docx' }),
  ).toBeVisible({ timeout: 60000 });
  await expect(page.getByText(/Verified report ID:/)).toBeVisible();
  const session = page.url();
  await page
    .getByRole('button', { name: 'Save q3-report.docx to Drive' })
    .click();
  const save = page.getByRole('dialog', { name: 'Save as new file in Drive' });
  await save.getByLabel('Drive file name').fill('review-copy.docx');
  await save
    .getByRole('button', { name: 'Save new file', exact: true })
    .click();
  await expect(save).toContainText('Verified in Synthetic Drive');
  await save.getByRole('button', { name: 'Close', exact: true }).click();
  await page.goto(`/admin/instances/${owner}/credentials`);
  await page
    .getByRole('row')
    .filter({ hasText: 'drive-work' })
    .getByRole('button', { name: 'Expand row' })
    .click();
  await page
    .getByRole('button', { name: 'Browse Drive files', exact: true })
    .click();
  const files = page.getByRole('dialog', {
    name: 'Files available to Agent Core',
  });
  await expect(files).toContainText('App-authorized files only');
  await files.getByRole('button', { name: 'Preview review-copy.docx' }).click();
  const preview = page.getByRole('dialog', { name: 'review-copy.docx' });
  await expect(
    page
      .frameLocator('iframe[title="Word document preview"]')
      .getByText('Q3 sales report', { exact: true }),
  ).toBeVisible();
  const downloadEvent = page.waitForEvent('download');
  await preview
    .getByRole('button', { name: 'Download review-copy.docx' })
    .click();
  const downloaded = await downloadEvent;
  expect((await readFile((await downloaded.path())!)).subarray(0, 4)).toEqual(
    Buffer.from([80, 75, 3, 4]),
  );
  await page.keyboard.press('Escape');
  await expect(
    files.getByRole('button', { name: 'Preview review-copy.docx' }),
  ).toBeFocused();
  await page.keyboard.press('Escape');
  const connection = page.getByRole('region', { name: `Details for ${name}` });
  await connection
    .getByRole('button', { name: 'Disconnect', exact: true })
    .click();
  await page
    .getByRole('dialog', { name: 'Disconnect Google Drive?' })
    .getByRole('button', { name: 'Disconnect', exact: true })
    .click();
  await expect(connection).toContainText('Disconnected · Synthetic');
  await expect(
    page.getByRole('button', { name: 'Browse Drive files' }),
  ).toBeDisabled();
  await connection
    .getByRole('button', { name: 'Reconnect with Google', exact: true })
    .click();
  await page
    .getByRole('button', { name: 'Authorize Synthetic Drive', exact: true })
    .click();
  await page.waitForURL(/\/admin\/connections\/credentials(?:\?|$)/);
  await expect(
    page.getByRole('region', { name: `Details for ${name}` }),
  ).toContainText('Connected · Synthetic');
  await page.goto(session);
  await page.getByRole('button', { name: 'Preview q3-sales.xlsx' }).click();
  await expect(
    page.getByRole('tab', { name: 'Assumptions', exact: true }),
  ).toBeVisible();
  expect(errors).toEqual([]);
});
