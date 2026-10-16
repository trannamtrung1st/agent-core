import { expect, test, type Page } from '@playwright/test';

async function login(page: Page, login = 'access-owner@example.test', password = 'synthetic-access-test-password') {
  await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}
async function request<T>(page: Page, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  return page.evaluate(async ({ path, body, method }) => {
    const csrf = await (await fetch('/api/v2/auth/csrf')).json();
    const response = await fetch(path, { method, credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-AgentCore-CSRF': csrf.token }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
    if (!response.ok) throw new Error(`Fixture request failed: ${response.status} ${path}`);
    return response.status === 204 ? undefined : await response.json();
  }, { path, body, method }) as Promise<T>;
}
async function scope(page: Page) {
  const tenant = (await request<{ items: { tenantId: string }[] }>(page, '/api/v2/account/tenants')).items[0].tenantId;
  return { tenant, api: `/api/v2/tenants/${tenant}`, path: `/t/${tenant}/integrations/credentials` };
}
async function purpose(page: Page, label: string) {
  await page.getByRole('dialog', { name: 'Add credential', exact: true }).getByRole('combobox', { name: 'Credential purpose', exact: true }).click();
  await page.locator('.ant-select-item-option').filter({ hasText: new RegExp(`^${label}$`) }).click();
}

test('canonical Credential purposes, protected replacement and separate Google consent', async ({ page }) => {
  const failures: string[] = []; const legacy: string[] = [];
  page.on('pageerror', error => failures.push(error.message));
  page.on('console', entry => { if (entry.type() === 'error') failures.push(entry.text()); });
  page.on('response', response => { if (response.status() >= 400) failures.push(`HTTP ${response.status()} ${new URL(response.url()).pathname}`); });
  page.on('requestfailed', r => { if (r.failure()?.errorText !== 'net::ERR_ABORTED') failures.push(`Network failure ${new URL(r.url()).pathname}`); });
  page.on('request', r => { if (new URL(r.url()).pathname.startsWith('/api/v2/admin/')) legacy.push('unscoped request'); });
  await login(page); const { path } = await scope(page); const suffix = crypto.randomUUID().slice(0, 8);
  await page.goto(path); await page.getByRole('button', { name: 'Add credential', exact: true }).click();
  let dialog = page.getByRole('dialog', { name: 'Add credential', exact: true });
  await dialog.getByLabel('Display name', { exact: true }).fill(`Browser secret ${suffix}`);
  await dialog.getByLabel('Protected value', { exact: true }).fill('browser-protected-sentinel');
  await purpose(page, 'Gmail account'); await purpose(page, 'General credential');
  await expect(dialog.getByLabel('Protected value', { exact: true })).toHaveValue('');
  await dialog.getByLabel('Protected value', { exact: true }).fill('browser-protected-sentinel');
  await dialog.getByRole('button', { name: 'Save credential', exact: true }).click();
  let details = page.getByRole('dialog', { name: 'Credential details', exact: true });
  await expect(details.getByRole('heading', { name: `Browser secret ${suffix}`, exact: true })).toBeVisible();
  const generalId = new URL(page.url()).searchParams.get('credentialId')!;
  await expect(details).not.toContainText('browser-protected-sentinel');
  await details.getByRole('button', { name: 'Replace protected value', exact: true }).click();
  const replace = page.getByRole('dialog', { name: 'Replace protected value', exact: true });
  await replace.getByLabel('Protected value', { exact: true }).fill('replacement-protected-sentinel');
  await replace.getByRole('button', { name: 'Replace value', exact: true }).click(); await expect(replace).toBeHidden();
  await expect(details.getByRole('button', { name: 'Replace protected value', exact: true })).toBeFocused();
  await details.getByRole('button', { name: 'Close details', exact: true }).click();
  const trigger = page.getByRole('link', { name: `Browser secret ${suffix}`, exact: true });
  await expect(await trigger.count() ? trigger : page.getByRole('button', { name: 'Add credential', exact: true })).toBeFocused();
  for (const [choice, name] of [['Gmail account', 'Gmail'], ['Google Drive account', 'Drive']] as const) {
    await page.getByRole('button', { name: 'Add credential', exact: true }).click(); await purpose(page, choice);
    dialog = page.getByRole('dialog', { name: 'Add credential', exact: true });
    await expect(dialog.getByLabel('Protected value', { exact: true })).toHaveCount(0);
    await dialog.getByLabel('Display name', { exact: true }).fill(`Browser ${name} ${suffix}`);
    await dialog.getByRole('button', { name: 'Save credential', exact: true }).click();
    details = page.getByRole('dialog', { name: 'Credential details', exact: true });
    await details.getByRole('button', { name: 'Connect with Google', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Connect Synthetic account', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.getByText('Connection cancelled', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Connect with Google', exact: true }).click();
    await page.getByRole('button', { name: 'Connect Synthetic account', exact: true }).click();
    await expect(page.getByText('Google account connected', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Reconnect with Google', exact: true })).toBeEnabled();
    details = page.getByRole('dialog', { name: 'Credential details', exact: true });
    await expect(details.getByText('Credential status', { exact: true })).toBeVisible();
    await expect(details.getByText('Active', { exact: true })).toBeVisible();
    await expect(details.getByText('Connection status', { exact: true })).toBeVisible();
    for (const status of ['Disabled', 'Active']) {
      await details.getByRole('button', { name: 'Edit credential', exact: true }).click();
      const edit = page.getByRole('dialog', { name: 'Edit credential', exact: true });
      await edit.getByRole('combobox', { name: 'Status', exact: true }).click(); await page.locator('.ant-select-item-option').filter({ hasText: new RegExp(`^${status}$`) }).click();
      await edit.getByRole('button', { name: 'Save credential', exact: true }).click(); await expect(edit).toBeHidden();
      await expect(details.getByText(status, { exact: true })).toBeVisible(); await expect(details.getByText('Synthetic', { exact: true })).toBeVisible();
      if (status === 'Disabled') await expect(details.getByRole('button', { name: 'Reconnect with Google', exact: true })).toBeDisabled();
    }
    expect(new URL(page.url()).searchParams.has('state')).toBe(false);
    await page.getByRole('button', { name: 'Disconnect account', exact: true }).click();
    await page.getByRole('dialog', { name: 'Disconnect this Google account?', exact: true }).getByRole('button', { name: 'Disconnect account', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Connect with Google', exact: true })).toBeEnabled();
    await expect(page.getByText('Google account connected', { exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Close details', exact: true }).click();
  }
  await page.getByRole('button', { name: 'Add credential', exact: true }).click(); await purpose(page, 'Custom email account');
  dialog = page.getByRole('dialog', { name: 'Add credential', exact: true });
  await dialog.getByLabel('Display name', { exact: true }).fill(`Custom mail ${suffix}`);
  await dialog.getByLabel('Sender email address', { exact: true }).fill('fixture@example.test');
  for (const protocol of ['IMAP', 'SMTP']) { await dialog.getByLabel(`${protocol} username`, { exact: true }).fill('fixture@example.test'); await dialog.getByLabel(`${protocol} host`, { exact: true }).fill('mail.example.test'); }
  await dialog.getByLabel('Password', { exact: true }).fill('custom-protected-sentinel'); await dialog.getByRole('button', { name: 'Save credential', exact: true }).click();
  details = page.getByRole('dialog', { name: 'Credential details', exact: true });
  await expect(details.getByText('Required implicit TLS', { exact: false }).first()).toBeVisible();
  await expect(details.getByRole('button', { name: 'Connect with Google', exact: true })).toHaveCount(0);
  await details.getByRole('button', { name: 'Close details', exact: true }).click();
  await page.getByRole('button', { name: 'Add credential', exact: true }).click(); await purpose(page, 'Container registry');
  dialog = page.getByRole('dialog', { name: 'Add credential', exact: true });
  await dialog.getByLabel('Display name', { exact: true }).fill(`Registry ${suffix}`); await dialog.getByLabel('Registry host', { exact: true }).fill('ghcr.io');
  await dialog.getByLabel('Registry username', { exact: true }).fill('fixture'); await dialog.getByLabel('Protected value', { exact: true }).fill('registry-protected-sentinel');
  await dialog.getByRole('button', { name: 'Save credential', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Credential details', exact: true }).getByText('ghcr.io', { exact: true })).toBeVisible();
  await expect(page.locator('body')).not.toContainText('registry-protected-sentinel');
  await page.goto(`${path}?credentialId=${generalId}`);
  await page.getByRole('button', { name: 'Delete credential', exact: true }).click();
  await page.getByRole('dialog', { name: `Delete Browser secret ${suffix}?`, exact: true }).getByRole('button', { name: 'Delete credential', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Credential details', exact: true })).toBeHidden();
  await expect(page.getByRole('link', { name: `Browser secret ${suffix}`, exact: true })).toHaveCount(0);
  expect(legacy).toEqual([]); expect(failures).toEqual([]);
});

test('exact Credential grants, read-only inspection, revoked-write and stale-draft recovery', async ({ page, browser }) => {
  await login(page); const { tenant, api, path } = await scope(page); const nonce = crypto.randomUUID().slice(0, 8);
  const revision = async () => (await request<{ revision: number }>(page, `${api}/access/me`)).revision;
  const owned = await request<{ credential: { credentialId: string; revision: number } }>(page, `${api}/credentials`, { displayName: `Scoped credential ${nonce}`, kind: 'Password', purpose: 'General', protectedValue: 'scoped-protected-sentinel', expectedRevision: await revision() });
  const id = owned.credential.credentialId;
  const chat = await request<{ roleId: string }>(page, `${api}/roles`, { name: `Credential visitor ${nonce}`, permissions: ['agents.chat'], expectedRevision: await revision() });
  const invitation = await request<{ invitationId: string; token: string }>(page, `${api}/members/invite`, { login: `credential-reader-${nonce}@example.test`, roleId: chat.roleId, expectedRevision: await revision() });
  const readerContext = await browser.newContext(); const reader = await readerContext.newPage(); await reader.goto('/sign-in');
  await request(reader, '/api/v2/auth/register', { login: `credential-reader-${nonce}@example.test`, displayName: 'Credential visitor', password: 'synthetic-credential-reader-password', invitation: { tenantId: tenant, invitationId: invitation.invitationId, token: invitation.token } });
  await login(reader, `credential-reader-${nonce}@example.test`, 'synthetic-credential-reader-password');
  const members = await request<{ items: { principalId: string; login: string }[] }>(page, `${api}/members`);
  const principal = members.items.find(m => m.login === `CREDENTIAL-READER-${nonce.toUpperCase()}@EXAMPLE.TEST`)!.principalId;
  const readRole = await request<{ roleId: string }>(page, `${api}/roles`, { name: `Read one credential ${nonce}`, permissions: ['credentials.read'], expectedRevision: await revision() });
  await request(page, `${api}/role-assignments`, { principalId: principal, roleId: readRole.roleId, resourceType: 'credential', resourceId: id, expectedRevision: await revision() });
  await reader.goto(path); await expect(reader.getByRole('link', { name: `Scoped credential ${nonce}`, exact: true })).toBeVisible();
  await expect(reader.locator('tbody tr.ant-table-row')).toHaveCount(1);
  await expect(reader.getByRole('button', { name: 'Add credential', exact: true })).toHaveCount(0);
  await reader.getByRole('link', { name: `Scoped credential ${nonce}`, exact: true }).click();
  await expect(reader.getByText('Read-only credential', { exact: true })).toBeVisible(); await expect(reader.getByRole('button', { name: 'Edit credential', exact: true })).toHaveCount(0);
  const manage = await request<{ roleId: string }>(page, `${api}/roles`, { name: `Manage one credential ${nonce}`, permissions: ['credentials.read', 'credentials.manage'], expectedRevision: await revision() });
  const grant = await request<{ assignmentId: string }>(page, `${api}/role-assignments`, { principalId: principal, roleId: manage.roleId, resourceType: 'credential', resourceId: id, expectedRevision: await revision() });
  await reader.getByRole('button', { name: 'Reload details', exact: true }).click(); await reader.getByRole('button', { name: 'Edit credential', exact: true }).click();
  const edit = reader.getByRole('dialog', { name: 'Edit credential', exact: true });
  await edit.getByLabel('Display name', { exact: true }).fill('Retained safe unsaved name');
  await request(page, `${api}/role-assignments/${grant.assignmentId}?expectedRevision=${await revision()}`, undefined, 'DELETE');
  await edit.getByRole('button', { name: 'Save credential', exact: true }).click();
  await expect(edit.getByRole('alert')).toBeVisible(); await expect(edit.getByLabel('Display name', { exact: true })).toHaveValue('Retained safe unsaved name');
  await edit.getByRole('button', { name: 'Cancel', exact: true }).click(); await reader.getByRole('button', { name: 'Discard changes', exact: true }).click();
  await reader.getByRole('button', { name: 'Reload details', exact: true }).click(); await expect(reader.getByText('Read-only credential', { exact: true })).toBeVisible();
  await readerContext.close();
  await page.goto(`${path}?credentialId=${id}`); await page.getByRole('button', { name: 'Edit credential', exact: true }).click();
  const ownerEdit = page.getByRole('dialog', { name: 'Edit credential', exact: true }); await ownerEdit.getByLabel('Display name', { exact: true }).fill('Keep after conflict');
  await request(page, `${api}/credentials/${id}`, { displayName: `Server changed ${nonce}`, status: 'Active', expectedRevision: await revision(), expectedCredentialRevision: 1 }, 'PATCH');
  await ownerEdit.getByRole('button', { name: 'Save credential', exact: true }).click(); await expect(ownerEdit.getByRole('alert')).toBeVisible();
  await expect(ownerEdit.getByLabel('Display name', { exact: true })).toHaveValue('Keep after conflict'); await expect(ownerEdit.getByText('Your draft is retained.', { exact: false })).toBeVisible();
  await ownerEdit.getByRole('button', { name: 'Cancel', exact: true }).click(); await page.getByRole('button', { name: 'Discard changes', exact: true }).click();
  const beta = await request<{ tenantId: string }>(page, '/api/v2/system/tenants', { name: `Another credential organization ${nonce}`, slug: `credential-beta-${nonce}` });
  await page.goto(`/t/${beta.tenantId}/integrations/credentials?credentialId=${id}`);
  await expect(page.getByRole('dialog', { name: 'Credential details', exact: true }).getByRole('alert')).toBeVisible();
  await expect(page.getByText(`Server changed ${nonce}`, { exact: true })).toHaveCount(0);
});

test('protected failure clears only the secret and acknowledged creation never offers duplicate retry', async ({ page }) => {
  await login(page); const { path } = await scope(page); await page.goto(path);
  const name = `Retained after failed save ${crypto.randomUUID().slice(0, 8)}`;
  await page.getByRole('button', { name: 'Add credential', exact: true }).click(); const dialog = page.getByRole('dialog', { name: 'Add credential', exact: true });
  await dialog.getByLabel('Display name', { exact: true }).fill(name); await dialog.getByLabel('Protected value', { exact: true }).fill('do-not-retain-this-secret');
  await page.route('**/api/v2/tenants/*/credentials', async route => { if (route.request().method() === 'POST') await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ reasonCode: 'service_unavailable' }) }); else await route.continue(); }, { times: 1 });
  await dialog.getByRole('button', { name: 'Save credential', exact: true }).click(); await expect(dialog.getByRole('alert')).toBeVisible();
  await expect(dialog.getByLabel('Display name', { exact: true })).toHaveValue(name); await expect(dialog.getByLabel('Protected value', { exact: true })).toHaveValue('');
  await dialog.getByLabel('Protected value', { exact: true }).fill('retry-protected-sentinel');
  let writes = 0;
  await page.route('**/api/v2/tenants/*/credentials', async route => { if (route.request().method() !== 'POST') { await route.continue(); return; } writes++; const response = await route.fetch(); await route.fulfill({ response, body: '{' }); });
  await dialog.getByRole('button', { name: 'Save credential', exact: true }).click(); await expect(dialog.getByRole('button', { name: 'Reload page', exact: true })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Save credential', exact: true })).toBeDisabled(); expect(writes).toBe(1);
  await page.unroute('**/api/v2/tenants/*/credentials');
  await dialog.getByRole('button', { name: 'Reload page', exact: true }).click();
  await expect(page.getByRole('link', { name, exact: true })).toBeVisible();
});
