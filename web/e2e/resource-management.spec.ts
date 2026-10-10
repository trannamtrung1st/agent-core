import { expect, test, type Page } from '@playwright/test';
import { isCanceledDraftEvidenceRead } from './admin-definition-gate-helpers';

const browserEvidence = new WeakMap<Page, { errors: string[]; failed: string[] }>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  const failed: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('requestfailed', request => { if (!isCanceledDraftEvidenceRead(request)) failed.push(request.url()); });
  browserEvidence.set(page, { errors, failed });
});
test.afterEach(async ({ page }) => {
  const evidence = browserEvidence.get(page)!;
  expect(evidence.errors).toEqual([]);
  expect(evidence.failed).toEqual([]);
});

async function capability(page: import('@playwright/test').Page) {
  await page.goto('/admin');
  await expect(page.getByRole('heading', { name: 'Agent inventory' })).toBeVisible();
  await expect.poll(() => page.evaluate(() => localStorage.getItem('agent-core.owner-capability'))).toEqual(expect.any(String));
  return { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
}

test('Definition CSV upload and selected deletion confirm, cancel and preserve unselected files', async ({ page }) => {
  await capability(page);
  await page.getByRole('button', { name: 'New definition', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('textbox').fill(`resource-files-${Date.now()}`);
  await dialog.getByRole('button', { name: 'Create draft' }).click();
  const resources = page.locator('section[aria-label="Draft resources"]');
  await page.getByRole("tab", { name: "Skills & resources", exact: true }).click();
  await page.getByRole('tab', { name: 'Resources', exact: true }).click();
  await page.getByLabel('Resource logical path', { exact: true }).fill('knowledge/office.docx');
  await resources.locator('section[aria-label="Add draft resource"] input[type="file"]').setInputFiles({ name: 'office.docx', mimeType: '', buffer: Buffer.from('office bytes') });
  await page.getByLabel('Resource kind', { exact: true }).click();
  await page.getByLabel('Resource kind', { exact: true }).press('ArrowUp');
  await page.getByLabel('Resource kind', { exact: true }).press('Enter');
  await expect(resources.getByRole('alert').filter({ hasText: 'Knowledge requires text.' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Upload and bind', exact: true })).toBeDisabled();
  await page.getByLabel('Resource kind', { exact: true }).click();
  await page.getByLabel('Resource kind', { exact: true }).press('ArrowDown');
  await page.getByLabel('Resource kind', { exact: true }).press('Enter');
  for (const name of ['table.csv', 'notes.yaml', 'keep.txt']) {
    await page.getByLabel('Resource logical path', { exact: true }).fill(`references/${name}`);
    await resources.locator('section[aria-label="Add draft resource"] input[type="file"]').setInputFiles({ name, mimeType: '', buffer: Buffer.from('name,value\nA,1') });
    await page.getByRole('button', { name: 'Upload and bind', exact: true }).click();
    await expect(page.getByRole('checkbox', { name: `Select resource references/${name}`, exact: true })).toBeVisible();
  }
  await page.getByRole('checkbox', { name: 'Select resource references/table.csv', exact: true }).check();
  await page.getByRole('checkbox', { name: 'Select resource references/notes.yaml', exact: true }).check();
  await expect(resources.getByRole('status')).toHaveText('2 selected');
  await resources.getByRole('button', { name: 'Delete selected', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(resources.getByRole('checkbox', { name: 'Select resource references/table.csv', exact: true })).toBeChecked();
  await resources.getByRole('button', { name: 'Delete selected', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete resources', exact: true }).click();
  await expect(resources.getByRole('checkbox', { name: 'Select resource references/table.csv', exact: true })).toHaveCount(0);
  await expect(resources.getByRole('checkbox', { name: 'Select resource references/notes.yaml', exact: true })).toHaveCount(0);
  await expect(resources.getByRole('checkbox', { name: 'Select resource references/keep.txt', exact: true })).toBeVisible();
});

test('Definition deletion locks stale revisions and recovers after repeated refresh failures', async ({ page }) => {
  const headers = await capability(page);
  const created = await page.request.post('/api/v2/admin/definition-drafts/new', { headers, data: { definitionId: `resource-refresh-${Date.now()}` } });
  expect(created.ok()).toBe(true);
  const draft = await created.json();
  const root = `/api/v2/admin/definition-drafts/${draft.draftId}`;
  const upload = await page.request.post(root + '/resources/content', { headers: { ...headers, 'Content-Type': 'text/csv' }, data: 'name,value\nA,1' });
  expect(upload.ok()).toBe(true);
  const stored = await upload.json();
  const bound = await page.request.put(root + '/resources/batch', { headers, data: { expectedRevision: draft.revision, items: ['one.csv', 'two.csv'].map(logicalPath => ({ logicalPath, kind: 'Knowledge', ...stored })) } });
  expect(bound.ok()).toBe(true);
  await page.goto(`/admin/definitions/${draft.definitionId}`);
  await page.getByRole("tab", { name: "Skills & resources", exact: true }).click();
  await page.getByRole('tab', { name: 'Resources', exact: true }).click();
  const resources = page.locator('section[aria-label="Draft resources"]');
  await resources.getByRole('checkbox', { name: 'Select resource one.csv', exact: true }).check();
  await page.route(`**${root}`, route => route.request().method() === 'GET'
    ? route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Draft refresh unavailable. Try again.' }) })
    : route.continue());
  await resources.getByRole('button', { name: 'Delete selected', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete resources', exact: true }).click();
  await expect(resources.getByRole('alert').filter({ hasText: '1 of 1 resources deleted.' })).toBeVisible();
  const retry = resources.getByRole('button', { name: 'Reload resources', exact: true });
  await expect(retry).toBeEnabled();
  await expect(resources.getByRole('checkbox', { name: 'Select resource two.csv', exact: true })).toBeDisabled();
  await retry.click();
  await expect(resources.getByRole('alert')).toHaveText(/Draft refresh unavailable. Try again./);
  await expect(retry).toBeEnabled();
  await expect(resources.getByRole('checkbox', { name: 'Select resource two.csv', exact: true })).toBeDisabled();
  await page.unroute(`**${root}`);
  await retry.click();
  await expect(resources.getByRole('alert')).toHaveCount(0);
  await resources.getByRole('checkbox', { name: 'Select resource two.csv', exact: true }).check();
  await resources.getByRole('button', { name: 'Delete selected', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete resources', exact: true }).click();
  await expect(resources.getByRole('checkbox', { name: 'Select resource two.csv', exact: true })).toHaveCount(0);
});

test('Instance bulk deletion reports partial conflicts and reload restores remaining selection', async ({ page }) => {
  const headers = await capability(page);
  const response = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'general-assistant', version: 21 } });
  expect(response.ok()).toBe(true);
  const owner = await response.json();
  const root = `/api/v2/admin/agent-instances/${owner.instanceId}`;
  await page.goto(`/admin/instances/${owner.instanceId}/skills/resources`);
  await page.getByRole('button', { name: 'New Instance resource', exact: true }).click();
  const drawer = page.getByRole('dialog');
  await drawer.locator('input[type=file]').first().setInputFiles([
    { name: 'table.csv', mimeType: 'application/vnd.ms-excel', buffer: Buffer.from('name,value\nA,1') },
    { name: 'keep.yaml', mimeType: '', buffer: Buffer.from('name: resource') }
  ]);
  await drawer.getByLabel('Imported resource path 1', { exact: true }).fill('knowledge/table.csv');
  await drawer.getByLabel('Imported resource path 2', { exact: true }).fill('references/keep.yaml');
  await drawer.getByRole('button', { name: 'Add 2 resources', exact: true }).click();
  const resources = page.getByRole('region', { name: 'Instance resources', exact: true });
  await expect(resources.getByRole('checkbox', { name: 'Select resource knowledge/table.csv', exact: true })).toBeVisible();
  await resources.getByRole('checkbox', { name: 'Select resource knowledge/table.csv', exact: true }).check();
  await resources.getByRole('checkbox', { name: 'Select resource references/keep.yaml', exact: true }).check();
  let deletes = 0;
  await page.route(`**${root}/resources/*?**`, async route => {
    if (route.request().method() !== 'DELETE') return route.continue();
    deletes++;
    if (deletes === 2) return route.fulfill({ status: 409, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Instance revision is stale. Reload resources.' }) });
    return route.continue();
  });
  await resources.getByRole('button', { name: 'Delete selected', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete resources', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: '1 of 2 resources deleted.' })).toBeVisible();
  expect(deletes).toBe(2);
  await expect(page.getByRole('button', { name: 'New Instance resource', exact: true })).toBeDisabled();
  await page.unroute(`**${root}/resources/*?**`);
  await page.getByRole('button', { name: 'Retry resources', exact: true }).click();
  await expect(resources.getByRole('checkbox', { name: 'Select resource knowledge/table.csv', exact: true })).toHaveCount(0);
  await expect(resources.getByRole('checkbox', { name: 'Select resource references/keep.yaml', exact: true })).toBeVisible();
  await resources.getByRole('checkbox', { name: 'Select resource references/keep.yaml', exact: true }).check();
  await resources.getByRole('button', { name: 'Delete selected', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete resources', exact: true }).click();
  await expect(resources.getByRole('checkbox', { name: 'Select resource references/keep.yaml', exact: true })).toHaveCount(0);
});

test('Definition deletion retains a partial failure notice across the draft refresh', async ({ page }) => {
  const headers = await capability(page);
  const created = await page.request.post('/api/v2/admin/definition-drafts/new', { headers, data: { definitionId: `resource-conflict-${Date.now()}` } });
  expect(created.ok()).toBe(true);
  const draft = await created.json();
  const root = `/api/v2/admin/definition-drafts/${draft.draftId}`;
  const upload = await page.request.post(root + '/resources/content', { headers: { ...headers, 'Content-Type': 'text/csv' }, data: 'name,value\nA,1' });
  expect(upload.ok()).toBe(true);
  const stored = await upload.json();
  const bound = await page.request.put(root + '/resources/batch', { headers, data: { expectedRevision: draft.revision, items: ['one.csv', 'two.csv'].map(logicalPath => ({ logicalPath, kind: 'Knowledge', ...stored })) } });
  expect(bound.ok()).toBe(true);
  await page.goto(`/admin/definitions/${draft.definitionId}`);
  await page.getByRole("tab", { name: "Skills & resources", exact: true }).click();
  await page.getByRole('tab', { name: 'Resources', exact: true }).click();
  const resources = page.locator('section[aria-label="Draft resources"]');
  await resources.getByRole('checkbox', { name: 'Select resource one.csv', exact: true }).check();
  await resources.getByRole('checkbox', { name: 'Select resource two.csv', exact: true }).check();
  let deletes = 0;
  await page.route(`**${root}/resources/*?**`, route => {
    if (route.request().method() !== 'DELETE') return route.continue();
    deletes++;
    if (deletes === 2) return route.fulfill({ status: 409, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Draft revision is stale.' }) });
    return route.continue();
  });
  await resources.getByRole('button', { name: 'Delete selected', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete resources', exact: true }).click();
  await expect(resources.getByRole('alert').filter({ hasText: '1 of 2 resources deleted.' })).toBeVisible();
  await expect(resources.getByRole('checkbox', { name: 'Select resource two.csv', exact: true })).toBeEnabled();
  await expect(resources.getByRole('alert').filter({ hasText: '1 of 2 resources deleted.' })).toBeVisible();
  expect(deletes).toBe(2);
  await page.unroute(`**${root}/resources/*?**`);
});
