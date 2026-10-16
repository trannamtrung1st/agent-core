import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { selectInstanceIdentity } from './support/instance-identity';

test('DOCX hyperlinks stay readable and inert; unsupported preview recovers with unchanged downloads', async ({ page }) => {
  test.setTimeout(90000);
  const errors: string[] = [], external: string[] = [], navigations: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (!/^(http:\/\/127\.0\.0\.1[:/]|blob:|data:)/.test(request.url())) external.push(request.url()); });
  // Abort any accidental public request while still recording it as a test failure.
  await page.route(/https?:\/\/hyperlink\.example\.test\//, route => route.abort());
  await page.goto('/');
  const owner = await selectInstanceIdentity(page, { id: 'general-assistant', version: 26 });
  const token = await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': token! };
  const created = await page.request.post('/api/v2/sessions', { headers, data: { agentInstanceId: owner, mode: 'text' } });
  expect(created.ok()).toBe(true);
  const session = (await created.json()).sessionId;
  const bytes = await readFile(new URL('./fixtures/file-preview/hyperlinks.docx', import.meta.url));
  const saved = await page.request.put(`/api/v2/sessions/${session}/workspace/content?path=/home/hyperlinks.docx`, { headers: { ...headers, 'Content-Type': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' }, data: bytes });
  expect(saved.ok(), await saved.text()).toBe(true);
  await page.goto(`/admin/instances/${owner}/workspace`);
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations.push(frame.url()); });
  await page.getByRole('button', { name: 'Preview hyperlinks.docx', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'hyperlinks.docx', exact: true });
  const frame = page.frameLocator('iframe[title="Word document preview"]');
  for (const label of ['Inert hyperlinks report', 'HTTP example', 'HTTPS example', 'Mail example']) await expect(frame.getByText(label, { exact: true })).toBeVisible();
  await expect(dialog.locator('iframe')).toHaveAttribute('sandbox', 'allow-same-origin');
  expect(await frame.locator('[href], [target], [action], [formaction], [srcset], script, form, iframe').count()).toBe(0);
  for (const label of ['HTTP example', 'HTTPS example', 'Mail example']) await frame.getByText(label, { exact: true }).click();
  async function originalDownload() {
    const pending = page.waitForEvent('download');
    await dialog.getByRole('button', { name: 'Download hyperlinks.docx', exact: true }).click();
    expect(await readFile((await (await pending).path())!)).toEqual(bytes);
  }
  await originalDownload();
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  const previewEndpoint = /\/api\/v2\/agent-instances\/[^/]+\/workspace\/office-preview\?path=/;
  let injectedFailures = 0;
  await page.route(previewEndpoint, route => {
    expect(new URL(route.request().url()).searchParams.get('path')).toBe('/home/hyperlinks.docx');
    injectedFailures++;
    return route.fulfill({ status: 400, contentType: 'application/problem+json', body: JSON.stringify({ title: 'office_feature_unsupported', detail: 'UNTRUSTED exception private path' }) });
  });
  await page.getByRole('button', { name: 'Preview hyperlinks.docx', exact: true }).click();
  await expect.poll(() => injectedFailures).toBe(1);
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(dialog).toContainText('The original file is still available to download.');
    await expect(dialog).not.toContainText('UNTRUSTED');
    await expect(dialog).not.toContainText('corrupt');
    await expect(dialog.getByRole('button', { name: 'Download hyperlinks.docx', exact: true })).toBeEnabled();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
  await originalDownload();
  await page.unroute(previewEndpoint);
  await dialog.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(frame.getByText('Inert hyperlinks report', { exact: true })).toBeVisible();
  expect(injectedFailures).toBe(1);
  await originalDownload();
  expect(errors).toEqual([]); expect(external).toEqual([]); expect(navigations).toEqual([]);
});
