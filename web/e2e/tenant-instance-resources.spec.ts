import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { selectSpreadsheetCell } from './support/spreadsheet';
const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T = any>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const response = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBeTruthy(); return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) {
  await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(email); await page.getByLabel('Password', { exact: true }).fill(secret); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}
async function fixture() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call(api, '/api/v2/system/tenants', { name: `Regional Resources ${nonce}`, slug: `resources-${nonce}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`, revision = async () => (await call(api, root + '/access/me')).revision;
  const draft = await call(api, root + '/agent-definitions', { name: 'Regional file standards', expectedRevision: await revision() });
  const workbook = await readFile(new URL('./fixtures/file-preview/presentation.xlsx', import.meta.url));
  const bound = await call(api, root + `/definition-drafts/${draft.draftId}/resources/batch`, { expectedRevision: await revision(), expectedDraftRevision: draft.revision,
    items: Array.from({ length: 13 }, (_, i) => ({ logicalPath: i ? `references/regional-${String(i + 1).padStart(2, '0')}.txt` : 'references/regional-01-operations-and-customer-escalation-review-across-service-locations.xlsx', kind: 2,
      mediaType: i ? 'text/plain' : 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', content: (i ? Buffer.from(`Regional evidence ${i + 1}.`) : workbook).toString('base64') })) });
  await call(api, root + `/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: bound.revision });
  const id = (await call(api, root + '/agents/from-publication', { definitionId: draft.definitionId, version: 1, expectedRevision: await revision(), persona: { name: 'Alex — Regional operations and customer care', role: 'Guide', description: 'Synthetic resource review.', tone: 'Calm' } })).instance.instanceId;
  const resource = root + `/agents/${id}`, route = `/t/${tenant}/admin/agents/${id}/resources`;
  const catalog = () => call(api, resource + '/resources/editor?pageSize=100');
  return { api, tenant, root, id, resource, route, revision, catalog };
}
async function search(page: Page, value: string) { await page.getByRole('searchbox', { name: 'Search readable resources', exact: true }).fill(value); await page.getByRole('button', { name: 'Search', exact: true }).click(); }
async function stage(page: Page, names: string[]) {
  await page.getByRole('button', { name: 'Add Instance resources', exact: true }).click(); await page.locator('input[type=file]').first().setInputFiles(names.map(name => ({ name, mimeType: 'text/plain', buffer: Buffer.from(`Exact bytes for ${name}.`) })));
  for (const [index, name] of names.entries()) await page.getByLabel(`Imported resource path ${index + 1}`, { exact: true }).fill(`references/${name}`);
}
test.beforeAll(async () => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); try { expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState(); } finally { await api.dispose(); }
});

test('320px owner imports, previews, downloads, edits, copies and deletes with exact stale recovery', async ({ page }) => {
  test.setTimeout(120000);
  const f = await fixture(); const errors: string[] = [], unexpected: string[] = []; page.on('pageerror', error => errors.push(error.message));
  page.on('request', r => { if (/^\/api\/v[12]\/(local\/owner-capability|owner-capability|agent-instances|admin|file-preview)(\/|$)/.test(new URL(r.url()).pathname)) unexpected.push(r.url()); });
  try {
    await page.setViewportSize({ width: 320, height: 740 }); await signIn(page); await page.goto(f.route); await page.getByTitle('Next Page').click(); await expect(page.getByTitle('Previous Page')).toHaveAttribute('aria-disabled', 'false');
    await stage(page, ['local-review.txt']); const before = (await f.catalog()).total; expect(before).toBe(13); await page.getByRole('button', { name: 'Add resources', exact: true }).click(); await expect(page.getByRole('dialog')).toHaveCount(0);
    await search(page, 'local-review'); const row = page.getByRole('row').filter({ has: page.getByText('references/local-review.txt', { exact: true }) });
    const inspect = row.getByRole('button', { name: 'Inspect resource', exact: true }); await inspect.click(); await page.getByRole('button', { name: 'Preview local-review.txt', exact: true }).click(); await expect(page.getByText('Exact bytes for local-review.txt.', { exact: true })).toBeVisible(); await page.keyboard.press('Escape'); await expect(page.getByRole('dialog')).toHaveCount(1);
    const download = page.waitForEvent('download'); await page.getByRole('dialog').getByRole('button', { name: 'Download', exact: true }).click(); expect(await readFile((await (await download).path())!)).toEqual(Buffer.from('Exact bytes for local-review.txt.'));
    await page.keyboard.press('Escape'); await expect(inspect).toBeFocused(); await row.getByRole('button', { name: 'Edit resource', exact: true }).click(); await page.getByLabel('Resource logical path', { exact: true }).fill('references/local-review-renamed.txt'); await page.getByRole('button', { name: 'Cancel editing', exact: true }).click(); await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
    const editor = await call(f.api, f.resource + '/editor'); await call(f.api, f.resource + '/settings/conversationPolicy', { expectedRevision: editor.securityRevision, expectedInstanceRevision: editor.agent.instance.revision, set: { language: 'de' }, clear: [] }, 'PATCH');
    await page.getByRole('button', { name: 'Save resource', exact: true }).click(); await expect(page.getByText(/Access has changed since/)).toBeVisible(); await expect(page.getByLabel('Resource logical path', { exact: true })).toHaveValue('references/local-review-renamed.txt'); await page.getByRole('button', { name: 'Reload latest resources', exact: true }).click(); await page.getByRole('button', { name: 'Save resource', exact: true }).click(); await expect(page.getByRole('dialog')).toHaveCount(0);
    const local = (await f.catalog()).items.find((item: any) => item.resource.logicalPath === 'references/local-review-renamed.txt').resource; expect(local.enabled).toBe(true); expect((await f.api.get(f.resource + `/resources/${encodeURIComponent(local.key)}/content`)).ok()).toBe(true);
    await page.getByRole('button', { name: 'Delete', exact: true }).click(); await page.getByRole('button', { name: 'Delete resource', exact: true }).click(); await expect(page.getByText('No matching readable resources.', { exact: true })).toBeVisible();
    await search(page, 'regional-01'); const inherited = page.getByRole('row').filter({ has: page.getByText(/references\/regional-01-operations/, { exact: false }) }); await inherited.getByRole('switch').click(); await page.getByRole('button', { name: 'Disable resource', exact: true }).click(); await expect(inherited.getByRole('switch')).not.toBeChecked(); await inherited.getByRole('button', { name: 'Reset choice', exact: true }).click(); await page.getByRole('button', { name: 'Reset choice', exact: true }).last().click(); await expect(inherited.getByRole('switch')).toBeChecked();
    await inherited.getByRole('switch').click(); await page.getByRole('button', { name: 'Disable resource', exact: true }).click(); await expect(inherited.getByRole('switch')).not.toBeChecked(); await inherited.getByRole('button', { name: 'Copy to Instance', exact: true }).click(); await page.getByLabel('Copy resource logical path').fill('references/independent-workbook.xlsx'); await page.getByRole('button', { name: 'Review copy', exact: true }).click(); await page.keyboard.press('Escape'); await expect(page.getByRole('button', { name: 'Review copy', exact: true })).toBeFocused(); await page.getByRole('button', { name: 'Review copy', exact: true }).click(); await page.getByRole('button', { name: 'Copy resource', exact: true }).click(); await expect(page.getByRole('dialog')).toHaveCount(0);
    const copied = (await f.catalog()).items.find((item: any) => item.resource.logicalPath === 'references/independent-workbook.xlsx').resource; expect(copied.sourceDefinitionId).toBeTruthy(); expect(copied.enabled).toBe(true); expect((await f.catalog()).items.find((item: any) => item.resource.key === 'definition:' + copied.sourceDefinitionResourceId).resource.enabled).toBe(false);
    await search(page, 'independent-workbook'); await page.getByRole('button', { name: 'Inspect resource', exact: true }).click(); await page.getByRole('button', { name: 'Preview independent-workbook.xlsx', exact: true }).click(); await selectSpreadsheetCell(page.getByRole('dialog', { name: 'independent-workbook.xlsx', exact: true }), 'B4', '$1,234.50'); await page.keyboard.press('Escape'); await page.keyboard.press('Escape');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(errors).toEqual([]); expect(unexpected).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('partial and acknowledged-malformed imports retire saved files and resume only remaining staged bytes', async ({ page }) => {
  test.setTimeout(120000);
  const f = await fixture(); const paths: string[] = []; let failed = false, malformed = false;
  try {
    await signIn(page); await page.goto(f.route);
    await page.route('**/agents/*/resources', async route => {
      if (route.request().method() !== 'POST') { await route.continue(); return; }
      const path = route.request().postDataJSON().input.logicalPath; paths.push(path);
      if (path.endsWith('second.txt') && !failed) { failed = true; await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ reasonCode: 'unavailable' }) }); }
      else if (path.endsWith('second.txt') && !malformed) { malformed = true; const response = await route.fetch(); expect(response.ok()).toBe(true); await route.fulfill({ response, body: '{' }); }
      else await route.continue();
    });
    await stage(page, ['first.txt', 'second.txt', 'third.txt']); await page.getByRole('button', { name: 'Add resources', exact: true }).click(); await expect(page.getByText('1 resource saved', { exact: true })).toBeVisible(); await expect(page.getByLabel('Imported resource path 1', { exact: true })).toHaveValue('references/second.txt'); await expect(page.getByRole('button', { name: 'Add resources', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Reload latest resources', exact: true }).click(); await page.getByRole('button', { name: 'Add resources', exact: true }).click(); await expect(page.getByText('2 resources saved', { exact: true })).toBeVisible(); await expect(page.getByText(/This resource was saved, but its new revision/)).toBeVisible(); await expect(page.getByLabel('Imported resource path 1', { exact: true })).toHaveValue('references/third.txt');
    await page.getByRole('button', { name: 'Reload latest resources', exact: true }).click(); await page.getByRole('button', { name: 'Add resources', exact: true }).click(); await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(paths).toEqual(['references/first.txt', 'references/second.txt', 'references/second.txt', 'references/third.txt']); const items = (await f.catalog()).items.filter((item: any) => item.resource.origin === 'Instance'); expect(items).toHaveLength(3);
    for (const item of items) expect(await (await f.api.get(f.resource + `/resources/${encodeURIComponent(item.resource.key)}/content`)).text()).toBe(`Exact bytes for ${item.resource.logicalPath.split('/').at(-1)}.`);
  } finally { await f.api.dispose(); }
});

test('exact resource reader can preview without IAM tools and revocation retires nested protected content', async ({ page }) => {
  test.setTimeout(120000);
  const f = await fixture(); const unexpected: string[] = []; page.on('request', r => { const path = new URL(r.url()).pathname; if (/^\/api\/v[12]\/(local\/owner-capability|owner-capability|agent-instances|admin|file-preview)(\/|$)/.test(path) || /\/(members|roles|role-assignments|access\/resources|access\/check)(\/|$)/.test(path)) unexpected.push(path); });
  try {
    const role = async (name: string, permissions: string[]) => (await call(f.api, f.root + '/roles', { name, permissions, expectedRevision: await f.revision() })).roleId;
    const minimal = await role('Minimal resource membership', ['sessions.read']), parent = await role('Exact parent read', ['agents.read']), reader = await role('Exact resource read', ['resources.read']);
    const email = `resource-reader-${crypto.randomUUID().slice(0, 8)}@example.test`, secret = 'synthetic-resource-reader-password'; const invite = await call(f.api, f.root + '/members/invite', { login: email, roleId: minimal, expectedRevision: await f.revision() });
    const registration = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); try { await call(registration, '/api/v2/auth/register', { login: email, displayName: 'Exact Resource reader', password: secret, invitation: { tenantId: f.tenant, invitationId: invite.invitationId, token: invite.token } }); } finally { await registration.dispose(); }
    const member = (await call(f.api, f.root + '/members')).items.find((m: any) => m.displayName === 'Exact Resource reader'), resource = (await f.catalog()).items.find((item: any) => item.resource.logicalPath.endsWith('.xlsx')).resource;
    const grant = async (roleId: string, resourceType: string, resourceId: string) => call(f.api, f.root + '/role-assignments', { principalId: member.principalId, roleId, resourceType, resourceId, expectedRevision: await f.revision() });
    await grant(parent, 'agent', f.id); const assignment = await grant(reader, 'resource', `${f.id}:${resource.key}`);
    await page.setViewportSize({ width: 320, height: 740 }); await signIn(page, email, secret); await page.goto(f.route); await expect(page.getByRole('button', { name: 'Add Instance resources', exact: true })).toBeDisabled(); await expect(page.getByRole('switch')).toBeDisabled(); await expect(page.getByText('references/regional-02.txt', { exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Inspect resource', exact: true }).click(); await page.getByRole('button', { name: `Preview ${resource.logicalPath.split('/').at(-1)}`, exact: true }).click(); const dialog = page.getByRole('dialog', { name: resource.logicalPath.split('/').at(-1), exact: true }); await selectSpreadsheetCell(dialog, 'B4', '$1,234.50');
    await call(f.api, `${f.root}/role-assignments/${assignment.assignmentId}?expectedRevision=${await f.revision()}`, undefined, 'DELETE'); await dialog.getByLabel('Worksheet range').fill('A1:A1'); await dialog.getByRole('button', { name: 'Load range', exact: true }).click(); await expect(page.getByRole('dialog')).toHaveCount(0); await expect(page.getByText(resource.logicalPath, { exact: true })).toHaveCount(0); await expect(page.getByRole('button', { name: 'Inspect resource', exact: true })).toHaveCount(0); await expect(page.getByRole('button', { name: 'Reload resources', exact: true })).toBeFocused(); expect(unexpected).toEqual([]);
  } finally { await f.api.dispose(); }
});
