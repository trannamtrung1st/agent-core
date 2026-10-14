import { test, expect, type Page, type FileChooser } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from './support/instance-identity';

async function chooseFiles(page: Page, files: Parameters<FileChooser['setFiles']>[0]) {
  await page.getByRole('button', { name: 'Add content', exact: true }).click();
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('menuitem', { name: /Attach file/ }).click();
  await (await chooser).setFiles(files);
}

test('compact tiles upload Office and binary files, remove a target and send originals', async ({ page }, testInfo) => {
  test.setTimeout(120000);
  const errors: string[] = [];
  const failedRequests: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('requestfailed', request => {
    // Chromium reports bodyless 204s and deliberate navigation cancellation as ERR_ABORTED.
    if (request.failure()?.errorText !== 'net::ERR_ABORTED') failedRequests.push(request.url());
  });
  await page.goto('/');
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.customerSupport);
  const document = await readFile(new URL('fixtures/file-preview/with-image.docx', import.meta.url));
  const workbook = await readFile(new URL('fixtures/composer-upload.xlsx', import.meta.url));
  const longName = 'quarterly-financial-forecast-with-a-long-filename.xlsx';
  await chooseFiles(page, [
    { name: 'report.docx', mimeType: 'application/octet-stream', buffer: document },
    { name: longName, mimeType: 'application/octet-stream', buffer: workbook },
    { name: 'original.bin', mimeType: 'application/octet-stream', buffer: Buffer.from([0, 1, 2, 255]) },
    ...Array.from({ length: 5 }, (_, index) => ({ name: `export-${index}.csv`, mimeType: 'text/csv', buffer: Buffer.from('id,value\n1,evidence\n') })),
  ]);
  const tiles = page.getByRole('list', { name: 'Pending attachments' });
  await expect(tiles.getByRole('listitem')).toHaveCount(8);
  await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeEnabled();
  await expect(tiles.locator('img')).toHaveCount(0);
  for (const width of [1440, 768, 767, 390, 320]) {
    await page.setViewportSize({ width, height: 800 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(await tiles.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    for (const row of await tiles.getByRole('listitem').all()) expect(await row.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    await expect(page.getByLabel('Message', { exact: true })).toBeInViewport();
    if ([1440, 390].includes(width)) await page.screenshot({ path: testInfo.outputPath(`composer-files-${width}.png`) });
  }
  const filename = tiles.getByText(longName, { exact: true });
  await filename.scrollIntoViewIfNeeded();
  await filename.focus();
  await expect(page.getByRole('tooltip')).toContainText(longName);
  await page.getByLabel('Message', { exact: true }).focus();
  const remove = tiles.getByRole('button', { name: 'Remove export-4.csv', exact: true });
  await remove.scrollIntoViewIfNeeded();
  await remove.focus();
  await page.keyboard.press('Enter');
  await expect(tiles.getByRole('listitem')).toHaveCount(7);
  await page.getByLabel('Message', { exact: true }).fill('Read the attached files for order 91.');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(tiles).toHaveCount(0);
  await expect(page.locator('.chat-message-assistant').last()).toContainText('Order 91', { timeout: 30000 });
  await expect(page.getByRole('button', { name: 'Preview report.docx', exact: true })).toBeVisible();
  const token = await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'));
  const sessionId = new URL(page.url()).pathname.split('/').pop();
  const headers = { 'X-AgentCore-Owner-Capability': token! };
  const response = await page.request.get(`/api/v2/sessions/${sessionId}/attachments`, { headers });
  expect(response.ok()).toBe(true);
  const files = await response.json();
  expect(files.filter((file: { state: string }) => file.state === 'bound')).toHaveLength(7);
  for (const [name, bytes] of [['report.docx', document], [longName, workbook]] as const) {
    const file = files.find((file: { displayName: string }) => file.displayName === name);
    const content = await page.request.get(`/api/v2/sessions/${sessionId}/attachments/${file.attachmentId}/content`, { headers });
    expect(await content.body()).toEqual(bytes);
  }
  await page.reload();
  await expect(page.getByRole('button', { name: `Preview ${longName}`, exact: true })).toBeVisible();
  await page.getByRole('button', { name: `Preview ${longName}`, exact: true }).click();
  await expect(page.getByRole('dialog', { name: longName, exact: true })).toContainText('Revenue');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: longName, exact: true })).toHaveCount(0);
  expect(errors).toEqual([]);
  expect(failedRequests).toEqual([]);
});

test('failed binary upload retains its filename, retries and remains removable', async ({ page }) => {
  await page.goto('/');
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.customerSupport);
  let failed = false;
  await page.route('**/api/v2/sessions/*/attachments', async route => {
    if (route.request().method() === 'POST' && !failed) {
      failed = true;
      await route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Temporary upload failure.' }) });
    } else await route.continue();
  });
  await chooseFiles(page, { name: 'retry.bmp', mimeType: 'image/bmp', buffer: Buffer.from([0, 1, 2, 255]) });
  const tiles = page.getByRole('list', { name: 'Pending attachments' });
  await expect(tiles.getByRole('status')).toHaveText('Temporary upload failure.');
  await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled();
  await tiles.getByRole('button', { name: 'Retry retry.bmp', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeEnabled();
  await expect(tiles.getByRole('status')).toHaveCount(0);
  await tiles.getByRole('button', { name: 'Remove retry.bmp', exact: true }).click();
  await expect(tiles).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled();
});
