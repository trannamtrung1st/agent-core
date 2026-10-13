import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { selectInstanceIdentity } from './support/instance-identity';
const fixture = new URL('./fixtures/file-preview/', import.meta.url);
test('owned home images, inert code/Markdown, native media and unsupported files use the shared shell', async ({
  page,
}) => {
  test.setTimeout(120000);
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
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
      'rows.tsv',
      'text/tab-separated-values',
      Buffer.from('id\tnote\n0001\ta,b\n'),
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
  const tsv = await open('rows.tsv');
  await expect(tsv.getByText('0001', { exact: true })).toBeVisible();
  await expect(tsv.getByText('a,b', { exact: true })).toBeVisible();
  await tsv.getByText('Text', { exact: true }).click();
  await expect(tsv.locator('pre')).toContainText('0001\ta,b');
  await tsv.getByText('Grid', { exact: true }).click();
  await expect(tsv.getByText('0001', { exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
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
});
