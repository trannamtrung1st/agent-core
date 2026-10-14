import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import {
  INSTANCE_DEFINITIONS,
  selectInstanceIdentity,
} from './support/instance-identity';
const source =
  '\uFEFFid,note,empty\r\n001,"a,b\r\n""quoted""",\r\n002,=HYPERLINK("evil"),end\r\n';
test('protected attachment preview preserves CSV source, download, keyboard and responsive containment', async ({
  page,
}) => {
  test.setTimeout(120000);
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.stack ?? error.message));
  const requests: string[] = [];
  page.on('request', (request) => {
    if (/attachments\/[^/]+\/content$/.test(request.url())) {
      requests.push(request.url());
      expect(request.headers()['x-agentcore-owner-capability']).toBeTruthy();
    }
  });
  await page.goto('/');
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.customerSupport);
  await page.getByRole('button', { name: 'Add content' }).click();
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('menuitem', { name: /Attach file/ }).click();
  await (
    await chooser
  ).setFiles({
    name: 'quoted.csv',
    mimeType: 'text/csv',
    buffer: Buffer.from(source),
  });
  // With an empty composer, Send becomes enabled only after the attachment is ready.
  await expect(
    page.getByRole('button', { name: 'Send', exact: true }),
  ).toBeEnabled();
  await page.getByLabel('Message').fill('Read the attached CSV for order 91.');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(page.getByTestId('connection')).toHaveText('Ready');
  await expect(page.locator('.chat-message-assistant').last()).toContainText(
    'Order 91',
    { timeout: 30000 },
  );
  const opener = page.getByRole('button', { name: 'Preview quoted.csv' });
  const previewNavigations: string[] = [];
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame()) previewNavigations.push(frame.url());
  });
  await opener.click();
  const dialog = page.getByRole('dialog', { name: 'quoted.csv' });
  await expect(dialog.getByText('001', { exact: true })).toBeVisible();
  await expect(
    dialog.getByText('a,b\r\n"quoted"', { exact: true }),
  ).toBeVisible();
  await dialog.getByText('Text', { exact: true }).click();
  await expect(dialog.locator('pre')).toContainText('001,"a,b');
  await dialog.getByText('Grid', { exact: true }).click();
  await expect(dialog.getByText('001', { exact: true })).toBeVisible();
  await dialog
    .getByRole('checkbox', { name: 'First row is headers' })
    .uncheck();
  await expect(
    dialog.getByRole('columnheader', { name: 'Column 1' }),
  ).toBeVisible();
  const pending = page.waitForEvent('download');
  await dialog.getByRole('button', { name: 'Download quoted.csv' }).click();
  const downloaded = await pending;
  expect(await readFile((await downloaded.path())!)).toEqual(
    Buffer.from(source),
  );
  for (const width of [1440, 1024, 768, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await expect
      .poll(() =>
        page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
      )
      .toBe(true);
    await expect(
      dialog.getByRole('button', { name: 'Download quoted.csv' }),
    ).toBeVisible();
  }
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(opener).toBeFocused();
  await page.route('**/attachments/*/content', (route) =>
    route.fulfill({ status: 403, body: '' }),
  );
  await opener.click();
  await expect(page.getByRole('dialog')).toContainText(
    'no longer have permission',
  );
  await page.unroute('**/attachments/*/content');
  await page
    .getByRole('dialog')
    .getByRole('button', { name: 'Retry', exact: true })
    .click();
  await expect(
    page.getByRole('dialog').getByText('001', { exact: true }),
  ).toBeVisible();
  expect(errors).toEqual([]);
  expect(previewNavigations).toEqual([]);
  expect(requests.length).toBeGreaterThan(1);
});
