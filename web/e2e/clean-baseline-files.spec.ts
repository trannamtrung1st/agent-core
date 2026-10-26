import { expect, test, request as requests, type APIRequestContext } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { canonicalFetch, grantPrivateSpaceRead, signInTenant } from './support/tenant-auth';

async function call<T>(api: APIRequestContext, path: string, body?: unknown): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method: body === undefined ? 'GET' : 'POST', data: body, headers: { 'X-AgentCore-CSRF': csrf.token } });
  expect(response.ok(), `${path}: ${response.status()}`).toBeTruthy(); return response.json();
}

test('PNG original and immutable Artifact preview/download retain precise refusal feedback and keyboard access', async ({ page }, info) => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic');
    await call(api, '/api/v2/auth/login', { login: 'tenant-chat-owner@example.test', password: 'synthetic-tenant-chat-password' });
    const nonce = crypto.randomUUID().slice(0, 8);
    const tenant = await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `PNG files ${nonce}`, slug: `png-files-${nonce}` });
    const root = `/api/v2/tenants/${tenant.tenantId}`;
    const agent = await call<{ instance: { instanceId: string } }>(api, root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: (await call<{ revision: number }>(api, root + '/access/me')).revision });
    await grantPrivateSpaceRead(api, tenant.tenantId, agent.instance.instanceId);
    await signInTenant(page, 'tenant-chat-owner@example.test', 'synthetic-tenant-chat-password'); await page.goto(`/t/${tenant.tenantId}`);
    const bytes = await readFile('e2e/fixtures/file-preview/image.png'), name = `PNG publication ${nonce}.png`;
    await page.getByRole('button', { name: 'Add content', exact: true }).click();
    const chooser = page.waitForEvent('filechooser'); await page.getByRole('menuitem', { name: /Attach file/ }).click();
    await (await chooser).setFiles({ name, mimeType: 'image/png', buffer: bytes });
    const editor = page.getByRole('textbox', { name: 'Message', exact: true });
    await editor.fill('Keep the PNG file and this draft.');
    await expect(page.getByText('This model cannot read images. Choose a vision-capable model to send this attachment.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled(); await expect(editor).toHaveText('Keep the PNG file and this draft.');
    await expect(page.getByText('Ready', { exact: true })).toBeVisible();
    const chooseVision = async () => {
      await page.getByRole('button', { name: 'Model', exact: true }).click();
      const reply = page.waitForResponse(response => response.request().method() === 'PUT' && new URL(response.url()).pathname.endsWith('/model'));
      await page.getByRole('option', { name: 'Scripted Vision · Offline Synthetic Vision Tools', exact: true }).click();
      return reply;
    };
    const selected = await chooseVision();
    if (selected.status() === 409) {
      // Explicit readback/reselection after a refused stale command; no automatic write replay.
      await expect(editor).toHaveText('Keep the PNG file and this draft.');
      await expect(page.getByText('Access has changed since you opened this page. Reload the latest access before trying again.')).toBeVisible();
      expect((await chooseVision()).ok()).toBeTruthy();
    } else expect(selected.ok()).toBeTruthy();
    await page.getByRole('button', { name: 'Send', exact: true }).click(); await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    const original = page.locator('.entry-files').getByRole('group', { name, exact: true });
    const opener = original.getByRole('button', { name: `Preview ${name}`, exact: true }); await opener.click();
    const dialog = page.getByRole('dialog', { name, exact: true }); await expect(dialog.getByRole('button', { name: 'Fit image', exact: true })).toBeEnabled();
    await dialog.getByRole('button', { name: 'Actual size', exact: true }).click();
    await dialog.getByRole('slider', { name: 'Image zoom' }).focus(); await page.keyboard.press('End');
    await expect(dialog).toContainText('200%');
    for (const width of [1440, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await expect.poll(() => page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth }))).toEqual({ width, scrollWidth: width });
      await expect(dialog.getByRole('button', { name: `Download ${name}`, exact: true })).toBeVisible();
      await page.screenshot({ path: info.outputPath(`png-preview-${width}.png`) });
    }
    await dialog.getByRole('button', { name: 'Fit image', exact: true }).click();
    const pending = page.waitForEvent('download'); await dialog.getByRole('button', { name: `Download ${name}`, exact: true }).click();
    expect(await readFile((await (await pending).path())!)).toEqual(bytes);
    await page.keyboard.press('Escape'); await expect(dialog).toBeHidden(); await expect(opener).toBeFocused();
    await page.route('**/attachments/*/content', route => route.fulfill({ status: 403, json: { title: 'permission_denied' } }), { times: 1 });
    await original.getByRole('button', { name: `Download ${name}`, exact: true }).click(); await expect(original).toContainText('You no longer have permission');
    await expect(page.getByRole('link', { name: `Open ${name}`, exact: true })).toHaveCount(0);
    const retry = page.waitForEvent('download'); await original.getByRole('button', { name: `Retry download ${name}`, exact: true }).click(); expect(await readFile((await (await retry).path())!)).toEqual(bytes);
    await original.getByRole('button', { name: `Copy ${name} to working files`, exact: true }).click();
    const files = page.getByRole('dialog', { name: 'Session files', exact: true }); await files.getByRole('button', { name: 'Create copies', exact: true }).click();
    const confirmCopy = async () => {
      const reply = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname.endsWith('/materialize'));
      await page.getByRole('dialog', { name: 'Create working and Artifact copies?', exact: true }).getByRole('button', { name: 'Create copies', exact: true }).click();
      return reply;
    };
    const copied = await confirmCopy();
    if (copied.status() === 409) {
      await expect(files.getByText('The copy was refused. Reload current file access before trying again.', { exact: true })).toBeVisible();
      await files.getByRole('button', { name: 'Reload files', exact: true }).click();
      await files.getByRole('button', { name: 'Create copies', exact: true }).click();
      expect((await confirmCopy()).ok()).toBeTruthy();
    } else expect(copied.ok()).toBeTruthy();
    await expect(files.getByText('Copies created', { exact: true })).toBeVisible(); await files.getByRole('tab', { name: 'Artifacts', exact: true }).click();
    const artifactName = name.replaceAll(' ', '_');
    const artifact = files.getByRole('group', { name: artifactName, exact: true }); await artifact.getByRole('button', { name: `Preview ${artifactName}`, exact: true }).click();
    const artifactDialog = page.getByRole('dialog', { name: artifactName, exact: true });
    await expect(artifactDialog.getByRole('img', { name: artifactName, exact: true })).toBeVisible(); await page.keyboard.press('Escape'); await expect(artifactDialog).toBeHidden();
    const exported = page.waitForEvent('download'); await artifact.getByRole('button', { name: `Download ${artifactName}`, exact: true }).click(); expect(await readFile((await (await exported).path())!)).toEqual(bytes);
    expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
