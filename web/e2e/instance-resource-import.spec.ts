import { expect, test, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

async function call(page: Page, path: string, data?: unknown): Promise<any> {
  const csrf = await (await page.request.get('/api/v2/auth/csrf')).json();
  const response = await page.request.fetch(path, { method: data === undefined ? 'GET' : 'POST', data, headers: { 'X-AgentCore-CSRF': csrf.token } });
  expect(response.ok(), `${path}: ${response.status()}`).toBeTruthy();
  return response.json();
}
async function openResources(page: Page) {
  expect((await (await page.request.get('/health')).json()).profile).toBe('Synthetic');
  await call(page, '/api/v2/auth/login', { login: 'tenant-chat-owner@example.test', password: 'synthetic-tenant-chat-password' });
  const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = await call(page, '/api/v2/system/tenants', { name: `Resource imports ${nonce}`, slug: `imports-${nonce}` });
  const tenantRoot = `/api/v2/tenants/${tenant.tenantId}`;
  const access = await call(page, tenantRoot + '/access/me');
  const owner = await call(page, tenantRoot + '/agents', { templateId: 'general-assistant', version: 36, expectedRevision: access.revision });
  const root = `${tenantRoot}/agents/${owner.instance.instanceId}`;
  await page.goto(`/t/${tenant.tenantId}/admin/agents/${owner.instance.instanceId}/resources`);
  await expect(page.getByRole('region', { name: 'Instance resources', exact: true })).toBeVisible();
  return root;
}

test('Instance imports retry remaining files, preserve folder paths and edit exact content', async ({ page }, testInfo) => {
  test.setTimeout(90_000);
  const root = await openResources(page);
  const openImport = page.getByRole('button', { name: 'Add Instance resources', exact: true });
  await openImport.click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('button', { name: 'Add resources', exact: true })).toBeDisabled();
  await dialog.locator('input[type=file]:not([webkitdirectory])').setInputFiles([
    { name: 'one.md', mimeType: 'text/markdown', buffer: Buffer.from('first exact bytes') },
    { name: 'two.txt', mimeType: 'text/plain', buffer: Buffer.from('second exact bytes') }
  ]);
  await page.getByLabel('Imported resource path 1').fill('knowledge/one.md');
  await page.getByLabel('Imported resource path 2').fill('references/two.txt');
  let posts = 0;
  await page.route(`**${root}/resources`, route => {
    if (route.request().method() === 'POST' && ++posts === 2) {
      return route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Synthetic upload failure.' }) });
    }
    return route.continue();
  });
  await dialog.getByRole('button', { name: 'Add resources', exact: true }).click();
  await expect(dialog.getByText('1 resource saved', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Imported resource path 1')).toHaveValue('references/two.txt');
  await expect(page.getByLabel('Imported resource path 2')).toHaveCount(0);
  await page.unroute(`**${root}/resources`);
  await dialog.getByRole('button', { name: 'Reload latest resources', exact: true }).click();
  await dialog.getByRole('button', { name: 'Add resources', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  const catalog = async () => (await call(page, root + '/resources')).catalog;
  expect((await catalog()).resources.filter((r: { origin: string }) => r.origin === "Instance")).toHaveLength(2);

  const packageRoot = testInfo.outputPath('package-root');
  await mkdir(path.join(packageRoot, 'knowledge', 'nested'), { recursive: true });
  await mkdir(path.join(packageRoot, 'references'), { recursive: true });
  await writeFile(path.join(packageRoot, 'knowledge', 'nested', 'guide.md'), 'folder knowledge bytes');
  const binary = Buffer.from('%PDF-1.4\nBinary reference\x00\xff', 'latin1');
  await writeFile(path.join(packageRoot, 'references', 'guide.pdf'), binary);
  await openImport.click();
  await dialog.locator('input[webkitdirectory]').setInputFiles(packageRoot);
  await expect(page.getByLabel('Imported resource path 1')).toHaveValue('knowledge/nested/guide.md');
  await expect(page.getByLabel('Imported resource path 2')).toHaveValue('references/guide.pdf');
  await expect(dialog.locator('.admin-resource-preview-row').nth(0)).toContainText('Knowledge');
  await expect(dialog.locator('.admin-resource-preview-row').nth(1)).toContainText('Reference');
  await dialog.getByRole('button', { name: 'Add resources', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  const pdf = (await catalog()).resources.find((resource: { logicalPath: string }) => resource.logicalPath === 'references/guide.pdf');
  const downloaded = await page.request.get(root + '/resources/' + encodeURIComponent(pdf.key) + '/content');
  expect(await downloaded.body()).toEqual(binary);

  await openImport.click();
  await dialog.locator('input[type=file]:not([webkitdirectory])').setInputFiles({ name: 'duplicate.md', mimeType: 'text/markdown', buffer: Buffer.from('duplicate') });
  await page.getByLabel('Imported resource path 1').fill('knowledge/one.md');
  await expect(page.getByLabel('Imported resource path 1')).toHaveAttribute('aria-invalid', 'false');
  await page.getByLabel('Imported resource path 1').blur();
  await expect(dialog.getByText('Path is already bound on this Instance.', { exact: true })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Add resources', exact: true })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Cancel editing', exact: true }).click();
  await page.getByRole('dialog', { name: 'Discard unsaved changes?' }).getByRole('button', { name: 'Discard changes', exact: true }).click();
  await expect(openImport).toBeFocused();

  await page.getByRole('row').filter({ hasText: 'references/two.txt' }).getByRole('button', { name: 'Edit resource', exact: true }).click();
  await page.getByLabel('Resource logical path').fill('references/renamed.txt');
  await dialog.getByRole('button', { name: 'Save resource', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  let renamed = (await catalog()).resources.find((resource: { logicalPath: string }) => resource.logicalPath === 'references/renamed.txt');
  expect(await (await page.request.get(root + '/resources/' + encodeURIComponent(renamed.key) + '/content')).text()).toBe('second exact bytes');
  await page.getByRole('row').filter({ hasText: 'references/renamed.txt' }).getByRole('button', { name: 'Edit resource', exact: true }).click();
  const replacement = { name: 'replacement.md', mimeType: 'text/markdown', buffer: Buffer.from('replacement exact bytes') };
  await dialog.locator('input[type=file]').setInputFiles(replacement);
  await dialog.getByRole('button', { name: 'Keep current file', exact: true }).click();
  await expect(dialog.getByText('Current content is kept unless you choose a replacement.', { exact: true })).toBeVisible();
  await dialog.locator('input[type=file]').setInputFiles(replacement);
  await dialog.getByRole('button', { name: 'Save resource', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  renamed = (await catalog()).resources.find((resource: { logicalPath: string }) => resource.logicalPath === 'references/renamed.txt');
  expect(await (await page.request.get(root + '/resources/' + encodeURIComponent(renamed.key) + '/content')).text()).toBe('replacement exact bytes');
});

test('inherited built-in knowledge can be inspected and disabled while Instance kinds exclude evaluation fixtures', async ({ page }) => {
  await openResources(page);
  const inherited = page.getByRole('region', { name: 'Instance resources', exact: true });
  const row = inherited.getByRole('row').filter({ hasText: 'knowledge/support-order-policy' });
  await expect(inherited.getByText('knowledge/compliance-retention', { exact: true })).toBeVisible();
  await row.getByRole('button', { name: 'Inspect resource', exact: true }).click();
  const details = page.getByRole('dialog', { name: 'Resource inspection' });
  await details.getByRole('button', { name: /^Preview / }).click();
  const preview = page.locator('.file-preview-modal');
  await expect(preview).toContainText('Simulated order policy');
  await preview.getByRole('button', { name: 'Close', exact: true }).click();
  await details.getByRole('button', { name: 'Close inspection', exact: true }).click();
  await row.getByRole('switch').click();
  await page.getByRole('dialog', { name: 'Disable resource?' }).getByRole('button', { name: 'Disable resource', exact: true }).click();
  await expect(row.getByRole('switch')).not.toBeChecked();
  await expect(row).toContainText('Instance choice');
  await row.getByRole('button', { name: 'Reset choice', exact: true }).click();
  await page.getByRole('dialog', { name: 'Reset choice?' }).getByRole('button', { name: 'Reset choice', exact: true }).click();
  await expect(row.getByRole('switch')).toBeChecked();
  await expect(row).toContainText('Inherited choice');
  await page.getByRole('button', { name: 'Add Instance resources', exact: true }).click();
  const add = page.getByRole('dialog', { name: 'Add Instance resources' });
  await add.locator('input[type=file]:not([webkitdirectory])').setInputFiles({ name: 'case.json', mimeType: 'application/json', buffer: Buffer.from('{}') });
  await page.getByLabel('Imported resource path 1').fill('eval/case.json');
  await expect(add.getByRole('button', { name: 'Add resources', exact: true })).toBeDisabled();
  await page.getByLabel('Imported resource kind 1').click();
  const options = page.locator('.ant-select-dropdown .ant-select-item-option-content');
  await expect(options.filter({ hasText: /^Evaluation fixture$/ })).toHaveCount(0);
  for (const name of ['Knowledge', 'Reference', 'Template', 'Static asset']) await expect(options.filter({ hasText: new RegExp(`^${name}$`) })).toBeVisible();
  await options.filter({ hasText: /^Reference$/ }).click();
  await add.getByRole('button', { name: 'Cancel editing', exact: true }).click();
  await page.getByRole('dialog', { name: 'Discard unsaved changes?' }).getByRole('button', { name: 'Discard changes', exact: true }).click();
});
