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
    version: 28,
  });
  const name = `Office Drive ${owner.slice(-8)}`;
  await page.goto('/admin/connections/credentials');
  await page
    .getByRole('button', { name: 'Add credential', exact: true })
    .click();
  const create = page.getByRole('dialog', { name: 'Add credential' });
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
  await page
    .getByLabel('Message')
    .fill('synthetic-office: q3-sales-drive-trash-fixtures');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Preview q3-report.docx' }),
  ).toBeVisible({ timeout: 60000 });
  await expect(page.getByText(/Verified report ID:/)).toBeVisible();
  const session = page.url();
  await expect(
    page.getByRole('button', { name: /Save .* to Drive/ }),
  ).toHaveCount(0);
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
  await expect(files.getByText('q3-report.docx', { exact: true })).toHaveCount(
    0,
  );
  await files.getByRole('button', { name: 'Clear Drive search' }).click();
  await expect(
    files.getByText('q3-report.docx', { exact: true }),
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
  await files.getByRole('button', { name: 'Preview q3-report.docx' }).click();
  const preview = page.getByRole('dialog', { name: 'q3-report.docx' });
  await expect(
    page
      .frameLocator('iframe[title="Word document preview"]')
      .getByText('Q3 sales report', { exact: true }),
  ).toBeVisible();
  const downloadEvent = page.waitForEvent('download');
  await preview
    .getByRole('button', { name: 'Download q3-report.docx' })
    .click();
  const downloaded = await downloadEvent;
  expect((await readFile((await downloaded.path())!)).subarray(0, 4)).toEqual(
    Buffer.from([80, 75, 3, 4]),
  );
  await page.keyboard.press('Escape');
  await expect(
    files.getByRole('button', { name: 'Preview q3-report.docx' }),
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
  await page.keyboard.press('Escape');
  for (const width of [320, 390, 768, 1024, 1440]) {
    await page.setViewportSize({ width, height: 800 });
    await expect(
      page.getByRole('button', { name: 'Preview q3-report.docx' }),
    ).toBeVisible();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    expect(
      await page
        .locator('.artifact-actions')
        .first()
        .getByRole('button')
        .allTextContents(),
    ).toEqual(['', '']);
    if (width < 768) {
      const action = await page
        .getByRole('button', { name: 'Download q3-report.docx' })
        .boundingBox();
      expect(action!.height).toBeGreaterThanOrEqual(40);
      expect(action!.width).toBeGreaterThanOrEqual(40);
    }
  }
  const token = await page.evaluate(() =>
    localStorage.getItem('agent-core.owner-capability'),
  );
  const remaining = async () => {
    const response = await page.request.get(
      `/api/v2/agent-instances/${owner}/drive/accounts/drive-work/files?query=AgentCoreUat&limit=20`,
      { headers: { 'X-AgentCore-Owner-Capability': token! } },
    );
    expect(response.ok()).toBe(true);
    return (await response.json()).items;
  };
  expect(await remaining()).toHaveLength(8);
  const requestBatch = async () => {
    await page
      .getByLabel('Message', { exact: true })
      .fill('synthetic-drive-trash: batch');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    const approval = page.getByRole('dialog', {
      name: 'Move 7 listed Drive files to Trash?',
    });
    await expect(approval).toBeVisible({ timeout: 30000 });
    await expect(approval).toContainText('7 listed app-authorized Drive files');
    await expect(approval).toContainText(name);
    await expect(approval).toContainText('Can be restored');
    await expect(
      approval
        .getByRole('list', { name: 'Drive files in this request' })
        .getByRole('listitem'),
    ).toHaveCount(7);
    // Filename and metadata stay grouped; a flex-item line break previously
    // inserted an empty line and two extra sibling gaps between them.
    const metadataGap = await approval
      .getByRole('listitem')
      .first()
      .evaluate(
        (row) =>
          row.lastElementChild!.getBoundingClientRect().top -
          row.firstElementChild!.getBoundingClientRect().bottom,
      );
    expect(metadataGap).toBeGreaterThanOrEqual(0);
    expect(metadataGap).toBeLessThanOrEqual(8);
    return approval;
  };
  const rejected = await requestBatch();
  await page.keyboard.press('Escape');
  await expect(rejected).toBeHidden();
  await expect(
    page.getByText('Drive fixture stopped after rejection or a tool failure.', {
      exact: false,
    }),
  ).toBeVisible();
  expect(await remaining()).toHaveLength(8);
  const approved = await requestBatch();
  await page.setViewportSize({ width: 320, height: 800 });
  await expect
    .poll(() =>
      page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
    )
    .toBe(true);
  await approved
    .getByRole('button', { name: 'Move 7 files to Trash', exact: true })
    .focus();
  await page.keyboard.press('Enter');
  await expect(approved).toBeHidden();
  await expect(
    page.getByText(
      'Synthetic app-authorized batch: requested 7; trashed 7; failed 0; uncertain 0; not attempted 0.',
      { exact: false },
    ),
  ).toBeVisible({ timeout: 30000 });
  expect(await remaining()).toHaveLength(1);
  expect(errors).toEqual([]);
});
