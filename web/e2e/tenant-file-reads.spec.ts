import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
import { readFile } from 'node:fs/promises';
const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await api.fetch(path, { method: body === undefined ? 'GET' : 'POST', headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${path}: ${response.status()}`).toBeTruthy(); return response.json();
}
async function signIn(page: Page) {
  await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}
test.beforeAll(async () => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try { expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password }); identity = await api.storageState(); }
  finally { await api.dispose(); }
});
for (const width of [1440, 390]) test(`tenant original, working copy and Artifact reads recover without owner authority at ${width}px`, async ({ page }) => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL, storageState: identity }); const nonce = crypto.randomUUID().slice(0, 8);
  const errors: string[] = [], obsolete: string[] = [], unexpected: string[] = [], consoleErrors: string[] = [];
  page.on('pageerror', error => errors.push(error.message)); page.on('console', entry => { if (entry.type() === 'error') consoleErrors.push(entry.text()); });
  page.on('request', request => { const path = new URL(request.url()).pathname; if (path.includes('owner-capability') || /^\/api\/v[12]\/(sessions|agent-instances|admin|file-preview)(\/|$)/.test(path) || request.headers()['x-agentcore-owner-capability']) obsolete.push(path); });
  let expectedDeniedPath = '';
  page.on('response', response => { const path = new URL(response.url()).pathname; if (response.status() >= 400 && !(response.status() === 403 && path === expectedDeniedPath)) unexpected.push(`${response.status()} ${path}`); });
  try {
    const tenant = await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `File proof ${nonce}`, slug: `file-proof-${nonce}` }); const prefix = `/api/v2/tenants/${tenant.tenantId}`;
    const access = await call<{ revision: number }>(api, prefix + '/access/me');
    await call(api, prefix + '/agents', { templateId: 'examiner', version: 1, expectedRevision: access.revision });
    await page.setViewportSize({ width, height: 900 }); await page.addInitScript(() => localStorage.setItem('agent-core.owner-capability', 'obsolete-file-read-token'));
    await signIn(page); await page.goto(`/t/${tenant.tenantId}`);
    const name = `original-proof-${nonce}.md`, bytes = Buffer.from('# Original file\n\n```ts\nconst scope = "tenant";\n```\n');
    await page.getByRole('button', { name: 'Add content', exact: true }).click(); const chooser = page.waitForEvent('filechooser');
    await page.getByRole('menuitem', { name: /Attach file/ }).click(); await (await chooser).setFiles({ name, mimeType: 'text/markdown', buffer: bytes });
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Keep this original file.'); await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    const original = page.locator('.entry-files').getByRole('group', { name, exact: true }); await expect(original).toBeVisible();
    await original.getByRole('button', { name: `Preview ${name}`, exact: true }).click(); let preview = page.getByRole('dialog', { name, exact: true });
    await expect(preview).toContainText('const scope = "tenant";'); await page.keyboard.press('Escape'); await expect(preview).toBeHidden();
    const originalDownload = page.waitForEvent('download'); await original.getByRole('button', { name: `Download ${name}`, exact: true }).click();
    expect(await readFile((await (await originalDownload).path())!)).toEqual(bytes);
    await original.getByRole('button', { name: `Copy ${name} to working files`, exact: true }).click();
    let files = page.getByRole('dialog', { name: 'Session files', exact: true }); await files.getByRole('button', { name: 'Create copies', exact: true }).click();
    const copied = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname.endsWith('/materialize'));
    await page.getByRole('dialog', { name: 'Create working and Artifact copies?', exact: true }).getByRole('button', { name: 'Create copies', exact: true }).click();
    const copy = await (await copied).json() as { workspaceLogicalPath: string };
    await expect(files.getByText('Copies created', { exact: true })).toBeVisible();
    await files.getByRole('button', { name: `Preview ${copy.workspaceLogicalPath.split('/').at(-1)}`, exact: true }).click();
    preview = page.getByRole('dialog', { name: copy.workspaceLogicalPath.split('/').at(-1)!, exact: true });
    await expect(preview).toContainText('const scope = "tenant";'); await page.keyboard.press('Escape'); await expect(preview).toBeHidden();
    const workingDownload = page.waitForEvent('download'); await files.getByRole('button', { name: `Download ${copy.workspaceLogicalPath}`, exact: true }).click();
    expect(await readFile((await (await workingDownload).path())!)).toEqual(bytes);
    await files.getByRole('tab', { name: 'Artifacts', exact: true }).click(); const artifact = files.getByRole('group', { name, exact: true }); await expect(artifact).toBeVisible();
    await artifact.getByRole('button', { name: `Preview ${name}`, exact: true }).click(); preview = page.getByRole('dialog', { name, exact: true });
    await expect(preview).toContainText('const scope = "tenant";'); await page.keyboard.press('Escape'); await expect(preview).toBeHidden();
    let denied = false;
    await page.route(`**${prefix}/sessions/*/artifacts/*/content`, async route => {
      if (!denied) { denied = true; expectedDeniedPath = new URL(route.request().url()).pathname; await route.fulfill({ status: 403, contentType: 'application/problem+json', body: '{"title":"permission_denied"}' }); }
      else await route.continue();
    });
    await artifact.getByRole('button', { name: `Download ${name}`, exact: true }).click(); await expect(files.getByText('File unavailable', { exact: true })).toBeVisible();
    await expect(files.getByRole('button', { name: `Download ${name}`, exact: true })).toHaveCount(0);
    await files.getByRole('button', { name: 'Retry file', exact: true }).click(); await expect(artifact).toBeVisible();
    const restored = page.waitForEvent('download'); await artifact.getByRole('button', { name: `Download ${name}`, exact: true }).click();
    const download = await restored; expect(download.suggestedFilename()).toBe(name); expect(await readFile((await download.path())!)).toEqual(bytes);
    expect(denied).toBe(true); expect(errors).toEqual([]); expect(obsolete).toEqual([]); expect(unexpected).toEqual([]);
    expect(consoleErrors).toHaveLength(1); expect(consoleErrors[0]).toContain('403');
  } finally { await api.dispose(); }
});
