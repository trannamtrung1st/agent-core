import { test, expect } from '@playwright/test';
import { selectInstanceIdentity } from './support/instance-identity';
import { readFile } from 'node:fs/promises';
test.use({ actionTimeout: 15000 });
test('connect explicit Drive binding, generate Office deliverables, verify remote previews and epoch recovery', async ({
  page,
}) => {
  test.setTimeout(180000);
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.stack ?? error.message));
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
  // A transient destination failure must be recoverable without closing the form.
  await page.route(
    '**/drive-accounts',
    (route) =>
      route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ error: 'unavailable' }),
      }),
    { times: 1 },
  );
  await page
    .getByRole('button', { name: 'Save q3-report.docx to Drive' })
    .click();
  const save = page.getByRole('dialog', { name: 'Save as new file in Drive' });
  await save
    .getByRole('button', { name: 'Retry destinations', exact: true })
    .click();
  await expect(
    save.getByRole('combobox', { name: 'Drive account' }),
  ).toBeEnabled();
  await save.getByLabel('Drive file name').fill('review-copy.docx');
  const failedIds: string[] = [];
  const rejectedUpload = async (route: import('@playwright/test').Route) => {
    failedIds.push(route.request().postDataJSON().operationId);
    if (failedIds.length === 1)
      await route.fulfill({
        status: 409,
        json: {
          title: 'drive_operation_failed',
          detail:
            'The operation did not execute. Check destination permission.',
        },
      });
    else await route.continue();
  };
  await page.route('**/drive-upload', rejectedUpload);
  await save
    .getByRole('button', { name: 'Save new file', exact: true })
    .click();
  await expect(
    save.getByRole('button', { name: 'Start new save' }),
  ).toBeVisible();
  await expect(save.getByLabel('Drive file name')).toBeDisabled();
  expect(failedIds).toHaveLength(1);
  await save.getByRole('button', { name: 'Start new save' }).click();
  await save
    .getByRole('button', { name: 'Save new file', exact: true })
    .click();
  await expect(save).toContainText('Verified in Synthetic Drive');
  expect(failedIds).toHaveLength(2);
  expect(failedIds[0]).not.toBe(failedIds[1]);
  await page.unroute('**/drive-upload', rejectedUpload);
  await expect(
    save.getByRole('combobox', { name: 'Drive folder' }),
  ).toBeDisabled();
  await save.getByRole('button', { name: 'Close', exact: true }).click();
  // UI fault fixture; adapter tests independently verify Google's actual 308 protocol.
  const continuedBodies: unknown[] = [];
  const partialUpload = async (route: import('@playwright/test').Route) => {
    const body = route.request().postDataJSON();
    continuedBodies.push(body);
    if (continuedBodies.length === 1)
      await route.fulfill({
        status: 409,
        json: {
          error: 'drive_effect_indeterminate',
          operationId: body.operationId,
          fileId: 'partial-fixture',
          retryAllowed: false,
          resumeAllowed: true,
        },
      });
    else await route.continue();
  };
  await page.route('**/drive-upload', partialUpload);
  await page.setViewportSize({ width: 390, height: 640 });
  await page
    .getByRole('button', { name: 'Save q3-report.docx to Drive', exact: true })
    .click();
  await save.getByLabel('Drive file name').fill('continued-copy.docx');
  await save
    .getByRole('button', { name: 'Save new file', exact: true })
    .click();
  const resume = save.getByRole('button', {
    name: 'Continue upload',
    exact: true,
  });
  await expect(resume).toBeVisible();
  await expect(save.getByLabel('Drive file name')).toBeDisabled();
  await expect(
    save.getByRole('combobox', { name: 'Drive folder' }),
  ).toBeDisabled();
  await expect(
    save.getByRole('button', { name: 'Save new file', exact: true }),
  ).toHaveCount(0);
  await resume.focus();
  await expect(resume).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(save).toContainText('Verified in Synthetic Drive');
  expect(continuedBodies).toHaveLength(2);
  expect(continuedBodies[0]).toEqual(continuedBodies[1]);
  await save.getByRole('button', { name: 'Close', exact: true }).click();
  await page.unroute('**/drive-upload', partialUpload);
  await page.setViewportSize({ width: 1280, height: 720 });
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
  const search = files.getByRole('searchbox', {
    name: 'Search app-authorized Drive files',
  });
  await search.fill('q3-sales');
  await search.press('Enter');
  await expect(files.getByText('q3-sales.xlsx', { exact: true })).toBeVisible();
  await expect(
    files.getByText('review-copy.docx', { exact: true }),
  ).toHaveCount(0);
  await files.getByRole('button', { name: 'Clear Drive search' }).click();
  await expect(
    files.getByText('review-copy.docx', { exact: true }),
  ).toBeVisible();
  await files.getByRole('button', { name: 'Preview q3-sales.xlsx' }).click();
  const workbook = page.getByRole('dialog', {
    name: 'q3-sales.xlsx',
    exact: true,
  });
  await workbook.getByLabel('Worksheet range').fill('A1:C4');
  await workbook
    .getByRole('button', { name: 'Load range', exact: true })
    .click();
  await workbook
    .getByRole('button', { name: 'B4 empty formula', exact: true })
    .click();
  await expect(workbook).toContainText('B4 · =SUM(B2:B3)');
  const rangeReads: string[] = [];
  const trackRange = (request: import('@playwright/test').Request) => {
    if (request.url().includes('/office-preview'))
      rangeReads.push(request.url());
  };
  page.on('request', trackRange);
  for (const width of [1440, 1024, 768, 390, 320]) {
    await page.setViewportSize({ width, height: 700 });
    await expect(
      workbook.getByRole('button', { name: 'A1 Region', exact: true }),
    ).toBeVisible();
    await expect(workbook).toContainText('B4 · =SUM(B2:B3)');
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
  }
  expect(rangeReads).toEqual([]);
  page.off('request', trackRange);
  await page.keyboard.press('Escape');
  await expect(
    files.getByRole('button', { name: 'Preview q3-sales.xlsx' }),
  ).toBeFocused();
  await page.setViewportSize({ width: 1440, height: 900 });
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
