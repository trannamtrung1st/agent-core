import { selectSpreadsheetCell } from './support/spreadsheet';
import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { selectInstanceIdentity } from './support/instance-identity';
const fixture = new URL('./fixtures/file-preview/', import.meta.url);
test('owned home images, inert code/Markdown, native media and unsupported files use the shared shell', async ({
  page,
}) => {
  test.setTimeout(120000);
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.stack ?? error.message));
  await page.goto('/');
  const owner = await selectInstanceIdentity(page, {
    id: 'general-assistant',
    version: 26,
  });
  const token = await page.evaluate(() =>
    localStorage.getItem('agent-core.owner-capability'),
  );
  const headers = { 'X-AgentCore-Owner-Capability': token! };
  const response = await page.request.post('/api/v2/sessions', {
    headers,
    data: { agentInstanceId: owner, mode: 'text' },
  });
  expect(response.ok()).toBe(true);
  const session = (await response.json()).sessionId;
  const samples = [
    ['image.png', 'image/png', await readFile(new URL('image.png', fixture))],
    ['tone.mp3', 'audio/mpeg', await readFile(new URL('tone.mp3', fixture))],
    ['clip.mp4', 'video/mp4', await readFile(new URL('clip.mp4', fixture))],
    [
      'with-image.docx',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      await readFile(new URL('with-image.docx', fixture)),
    ],
    [
      'presentation.xlsx',
      'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      await readFile(new URL('presentation.xlsx', fixture)),
    ],
    [
      'rows.tsv',
      'text/tab-separated-values',
      Buffer.from('id\tnote\n0001\ta,b\n'),
    ],
    [
      'wide.csv',
      'text/csv',
      Buffer.from(
        [
          Array.from({ length: 129 }, (_, i) => `column${i}`).join(','),
          Array.from({ length: 129 }, (_, i) => `row1-col${i}`).join(','),
        ].join('\n'),
      ),
    ],
    [
      'inert.xml',
      'application/xml',
      Buffer.from('<script>alert("preview must remain inert")</script>'),
    ],
    [
      'readme.md',
      'text/markdown',
      Buffer.from(
        '# Reading sample\n\n**Safe heading**\n\n![blocked](https://evil.example.test/pixel)\n\n<script>window.previewEscaped=true</script>',
      ),
    ],
    [
      'report.pdf',
      'application/pdf',
      Buffer.from('%PDF-1.4\nUnsupported PDF fixture'),
    ],
  ] as const;
  for (const [name, mime, bytes] of samples) {
    const saved = await page.request.put(
      `/api/v2/sessions/${session}/workspace/content?path=${encodeURIComponent('/home/' + name)}`,
      { headers: { ...headers, 'Content-Type': mime }, data: bytes },
    );
    expect(saved.ok(), await saved.text()).toBe(true);
  }
  await page.goto(`/admin/instances/${owner}/workspace`);
  const previewNavigations: string[] = [];
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame()) previewNavigations.push(frame.url());
  });
  const open = async (name: string) => {
    await page
      .getByRole('button', { name: `Preview ${name}`, exact: true })
      .click();
    return page.getByRole('dialog', { name, exact: true });
  };
  const image = await open('image.png');
  await expect(
    image.getByRole('img', { name: 'image.png', exact: true }),
  ).toBeVisible();
  await image.getByRole('button', { name: 'Actual size' }).click();
  await expect(image).toContainText('100%');
  await image.getByRole('button', { name: 'Fit image' }).click();
  await expect(
    image.getByRole('region', { name: 'Image canvas. Scroll or drag to pan.' }),
  ).toBeVisible();
  await page.keyboard.press('Escape');
  const styled = await open('presentation.xlsx');
  await selectSpreadsheetCell(styled, 'B4', '$1,234.50');
  await selectSpreadsheetCell(styled, 'D4', '2026');
  await styled.getByLabel('Worksheet range').fill('C2:D5');
  await styled.getByRole('button', { name: 'Load range', exact: true }).click();
  await expect(styled).toContainText('merged range crosses');
  await page.keyboard.press('Escape');
  const tsv = await open('rows.tsv');
  await selectSpreadsheetCell(tsv, 'A2', '0001');
  await selectSpreadsheetCell(tsv, 'B2', 'a,b');
  await tsv.getByText('Text', { exact: true }).click();
  await expect(tsv.locator('pre')).toContainText('0001\ta,b');
  await tsv.getByText('Grid', { exact: true }).click();
  await selectSpreadsheetCell(tsv, 'A2', '0001');
  await page.keyboard.press('Escape');
  const wide = await open('wide.csv');
  await wide.getByRole('button', { name: 'Next columns', exact: true }).click();
  await wide.getByRole('button', { name: 'Next columns', exact: true }).click();
  await selectSpreadsheetCell(wide, 'DY2', 'row1-col128');
  await page.setViewportSize({ width: 320, height: 900 });
  await expect
    .poll(() =>
      page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
    )
    .toBe(true);
  await expect(
    wide.getByRole('button', { name: 'Download wide.csv' }),
  ).toBeVisible();
  await page.keyboard.press('Escape');
  await page.setViewportSize({ width: 1440, height: 900 });
  const xml = await open('inert.xml');
  await expect(xml.locator('pre')).toContainText('<script>alert');
  expect(
    await page.evaluate(() => Reflect.get(window, 'previewEscaped')),
  ).toBeUndefined();
  await page.keyboard.press('Escape');
  const requests: string[] = [];
  page.on('request', (request) => {
    if (request.url().startsWith('https://evil.example.test'))
      requests.push(request.url());
  });
  const md = await open('readme.md');
  await expect(
    md.getByRole('heading', { name: 'Reading sample' }),
  ).toBeVisible();
  await md.getByText('Source', { exact: true }).click();
  await expect(md.locator('pre')).toContainText('<script>');
  await page.keyboard.press('Escape');
  expect(requests).toEqual([]);
  for (const [name, kind] of [
    ['tone.mp3', 'audio'],
    ['clip.mp4', 'video'],
  ] as const) {
    const media = await open(name);
    await expect(media.locator(kind)).toBeVisible();
    const playback = await media.locator(kind).evaluate(async (element) => {
      const player = element as HTMLMediaElement;
      await player.play();
      return {
        url: player.currentSrc,
        paused: player.paused,
        duration: player.duration,
      };
    });
    expect(playback.url).toMatch(/^blob:/);
    expect(playback.paused).toBe(false);
    expect(playback.duration).toBeGreaterThan(0);
    await media
      .locator(kind)
      .evaluate((element) => (element as HTMLMediaElement).pause());
    await page.keyboard.press('Escape');
  }
  const word = await open('with-image.docx');
  const frame = page.frameLocator('iframe[title="Word document preview"]');
  await expect(
    frame.getByText('Embedded image proof', { exact: true }),
  ).toBeVisible();
  await expect(
    frame.getByText('Local document header', { exact: true }),
  ).toBeVisible();
  await expect(
    frame.getByText('Local document footer', { exact: true }),
  ).toBeVisible();
  await expect(frame.locator('img')).toBeVisible();
  await expect(frame.locator('img')).toHaveAttribute(
    'src',
    /^data:image\/png;base64,/,
  );
  await expect
    .poll(() =>
      frame
        .locator('section.docx')
        .first()
        .evaluate((el) => getComputedStyle(el).backgroundColor),
    )
    .toBe('rgb(255, 255, 255)');
  await page.keyboard.press('Escape');
  const pdf = await open('report.pdf');
  await expect(pdf).toContainText('Preview is unavailable for this format');
  await expect(
    pdf.getByRole('button', { name: 'Download report.pdf' }),
  ).toBeEnabled();
  expect(errors).toEqual([]);
  expect(previewNavigations).toEqual([]);
});

test('large CSV windows source rows and preserves exact Text copy and download', async ({ page }) => {
  test.setTimeout(120000);
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/');
  const owner = await selectInstanceIdentity(page, { id: 'general-assistant', version: 26 });
  const token = await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': token! };
  const session = await page.request.post('/api/v2/sessions', { headers, data: { agentInstanceId: owner, mode: 'text' } });
  const id = (await session.json()).sessionId;
  const long = 'Unicode Việt 🙂\n=WEBSERVICE("https://invalid.test/never") ' + 'x'.repeat(8000);
  const source = '\uFEFFid,note\r\n' + Array.from({ length: 50010 }, (_, i) =>
    `${String(i + 1).padStart(5, '0')},${i === 199 ? '"' + long.replaceAll('"', '""') + '"' : '+literal'}`,
  ).join('\r\n');
  const saved = await page.request.put(`/api/v2/sessions/${id}/workspace/content?path=%2Fhome%2Ftall.csv`, {
    headers: { ...headers, 'Content-Type': 'text/csv' }, data: Buffer.from(source),
  });
  expect(saved.ok()).toBe(true);
  await page.goto(`/admin/instances/${owner}/workspace`);
  await page.getByRole('button', { name: 'Preview tall.csv', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'tall.csv', exact: true });
  await selectSpreadsheetCell(dialog, 'A2', '00001');
  await expect(dialog).toContainText('parsing limit reached');
  await dialog.locator('.ant-pagination-next').click();
  await selectSpreadsheetCell(dialog, 'A201', '00200');
  await expect(dialog).toContainText('long values shortened');
  await selectSpreadsheetCell(dialog, 'B201', 'Unicode Việt');
  const policyResponse = await page.request.get('/api/v2/file-preview/options', { headers });
  expect(policyResponse.ok()).toBe(true);
  const policy = await policyResponse.json();
  const details = await dialog.locator('.file-preview-cell-detail').textContent();
  expect(details!.length).toBeLessThanOrEqual(policy.spreadsheet.maxCellCharacters + 64);
  expect(details).toContain('…');
  await dialog.getByText('Text', { exact: true }).click();
  await expect(dialog).toContainText('configured text line limit');
  await dialog.getByRole('button', { name: 'Copy', exact: true }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(source);
  const download = page.waitForEvent('download');
  await dialog.getByRole('button', { name: 'Download tall.csv', exact: true }).click();
  expect(await readFile((await (await download).path())!)).toEqual(Buffer.from(source));
  expect(errors).toEqual([]);
});
