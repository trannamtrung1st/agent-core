import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
import { canonicalFetch, signInTenant } from './support/tenant-auth';

const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, body?: unknown): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method: body === undefined ? 'GET' : 'POST', data: body, headers: { 'X-AgentCore-CSRF': csrf.token } });
  expect(response.ok(), `${path}: ${response.status()} ${await response.text()}`).toBeTruthy(); return response.json();
}
async function fixture(page: Page) {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  await call(api, '/api/v2/auth/login', { login, password });
  const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Retention ${nonce}`, slug: `retention-${nonce}` });
  const root = `/api/v2/tenants/${tenant.tenantId}`;
  const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
  const agent = await call<{ principalId: string; instance: { instanceId: string } }>(api, root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() });
  const space = await call<{ spaceId: string }>(api, root + `/agents/${agent.instance.instanceId}/private-space`);
  const role = await call<{ roleId: string }>(api, root + '/roles', { name: 'Exact Private Space read', permissions: ['spaces.read'], expectedRevision: await revision() });
  await call(api, root + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, resourceType: 'space', resourceId: space.spaceId, expectedRevision: await revision() });
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await signInTenant(page, login, password); await page.goto(`/t/${tenant.tenantId}`); return { api, root, errors };
}
async function send(page: Page, text: string) {
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill(text);
  await page.getByRole('button', { name: 'Send', exact: true }).click();
}
test('Saved completion publishes the final reply and read-only agent outcome before and after reload', async ({ page }) => {
  const f = await fixture(page);
  try {
    await expect(page.getByRole('radio', { name: 'Saved', exact: true })).toBeChecked();
    await send(page, 'Saved Synthetic verification'); await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    await page.reload(); await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    const policy = page.getByRole('switch', { name: 'Allow agent to complete this chat' }); await expect(policy).not.toBeChecked();
    await policy.click(); await expect(policy).toBeChecked();
    await send(page, '[test:session-complete] Finish this conversation.');
    await expect(page.getByText('The requested conversation is complete. Goodbye.', { exact: true })).toBeVisible();
    await expect(page.getByText(/Conversation completed by agent/)).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveCount(0);
    await page.reload(); await expect(page.getByText('The requested conversation is complete. Goodbye.', { exact: true })).toBeVisible();
    await expect(page.getByText(/Conversation completed by agent/)).toBeVisible(); expect(f.errors).toEqual([]);
  } finally { await f.api.dispose(); }
});
test('Temporary long text survives failed upload, Retry, exact byte admission and direct reload', async ({ page }) => {
  const f = await fixture(page);
  try {
    await page.getByText('Temporary', { exact: true }).click(); await expect(page.getByText(/Expires 24 hours after creation/)).toBeVisible();
    await send(page, 'Temporary private verification'); await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    const session = page.url().split('/').at(-1)!;
    const original = '  Literal @data /data 😀\r\n```text\n' + '字'.repeat(8_001) + '\n```  \r\n';
    const editor = page.getByRole('textbox', { name: 'Message', exact: true }); await editor.focus();
    await editor.evaluate((element, text) => { const data = new DataTransfer(); data.setData('text/plain', text); element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true })); }, original);
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled();
    await page.route('**/sessions/*/attachments?*', route => route.fulfill({ status: 503, json: { title: 'Synthetic upload unavailable' } }), { times: 1 });
    await page.getByRole('button', { name: 'Attach long text', exact: true }).click();
    const retry = page.getByRole('button', { name: /^Retry message-/ }); await expect(retry).toBeVisible(); expect(await editor.textContent()).toBe(original);
    await retry.click(); await expect(editor).toHaveText('Please work with the attached text.');
    await expect(page.getByRole('button', { name: 'Restore text', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Send', exact: true }).click(); await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(2);
    const history = await call<{ items: { attachments?: { attachmentId: string }[] }[] }>(f.api, f.root + `/sessions/${session}/messages`);
    const file = history.items.flatMap(entry => entry.attachments ?? [])[0]; expect(file).toBeTruthy();
    const bytes = await f.api.get(f.root + `/sessions/${session}/attachments/${file.attachmentId}/content`);
    expect(bytes.ok()).toBeTruthy(); expect((await bytes.body()).equals(Buffer.from(original, 'utf8'))).toBe(true);
    const catalog = await call<{ items: { sessionId: string }[] }>(f.api, f.root + '/sessions'); expect(catalog.items.some(item => item.sessionId === session)).toBe(false);
    await page.reload(); await expect(page.getByText('Temporary private verification', { exact: true })).toBeVisible();
    await expect(page.getByText(/Temporary · expires/)).toBeVisible();
    await page.getByRole('button', { name: 'Session files', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Session files' })).toBeVisible();
    // Advance the projected deadline to exercise the browser's authoritative expiry reaction.
    await page.evaluate(async () => {
      const source = '/src/state/sessionStore.ts';
      const { useSessionStore } = await import(/* @vite-ignore */ source);
      useSessionStore.setState({ retentionDueAtUtc: new Date(Date.now() - 1).toISOString() });
    });
    await expect(page.getByText('This conversation has expired. Start a new chat to continue.')).toBeVisible();
    await expect(page.getByRole('dialog', { name: 'Session files' })).toHaveCount(0);
    await expect(page.getByText('Temporary private verification', { exact: true })).toHaveCount(0);
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveCount(0);
    expect(f.errors).toEqual([]);
  } finally { await f.api.dispose(); }
});
