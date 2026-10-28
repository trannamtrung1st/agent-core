import { expect, test, request as requests, type APIRequestContext } from '@playwright/test';
import { canonicalFetch, signInTenant } from './support/tenant-auth';
const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body ? 'POST' : 'GET'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${path}: ${response.status()} ${await response.text()}`).toBeTruthy(); return response.json();
}
type Editor = { securityRevision: number; canUpdate: boolean; agent: { managedKind: string; instance: { revision: number; instanceId: string; showInChat: boolean; persona: { name: string } } } };
test('managed default ordinary Chat, hide, retained conversation, non-destructive reset and responsive management', async ({ page }) => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await call(api, '/api/v2/auth/login', { login, password });
    const nonce = crypto.randomUUID().slice(0, 8);
    const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Assistant ${nonce}`, slug: `assistant-${nonce}` })).tenantId;
    const root = `/api/v2/tenants/${tenant}`;
    const candidates = await call<{ items: { instanceId: string; systemManaged: boolean }[]; total: number }>(api, root + '/chat/agents');
    expect(candidates.total).toBe(1); expect(candidates.items[0].systemManaged).toBe(true);
    const id = candidates.items[0].instanceId, resource = root + `/agents/${id}`;
    await signInTenant(page, login, password); await page.goto(`/t/${tenant}`);
    await expect(page.getByText(/General Assistant · System-managed/)).toBeVisible();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Hello General Assistant'); await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(1);
    const conversation = page.url(); expect(conversation).toContain('/c/');
    await page.goto(`/t/${tenant}/admin/agents/${id}/settings`);
    const visibility = page.getByRole('switch', { name: 'Visible in Chat', exact: true }); await expect(visibility).toBeChecked();
    await visibility.click(); await expect(visibility).not.toBeChecked(); await expect(visibility).toBeFocused();
    await expect.poll(async () => (await call<Editor>(api, resource + '/editor')).agent.instance.showInChat).toBe(false);
    for (const width of [320, 390, 768, 1024, 1440]) {
      await page.setViewportSize({ width, height: 900 }); await expect(visibility).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }
    await page.getByRole('button', { name: 'Reset to System Defaults', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Reset to System Defaults?' }); await expect(dialog).toContainText('local Skills/resources');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click(); await expect(dialog).toHaveCount(0);
    await page.getByRole('button', { name: 'Reset to System Defaults', exact: true }).click();
    await page.getByRole('dialog', { name: 'Reset to System Defaults?' }).getByRole('button', { name: 'Reset to System Defaults', exact: true }).click();
    await expect(visibility).not.toBeChecked();
    await page.goto(`/t/${tenant}`); await expect(page.getByText(/No visible agents/)).toBeVisible();
    await page.goto(conversation); await expect(page.getByText('Hello General Assistant', { exact: true })).toBeVisible();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Continue our hidden assistant conversation'); await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(2);
    expect((await call<Editor>(api, resource + '/editor')).agent.instance.instanceId).toBe(id);
    await page.goto(`/t/${tenant}/admin/agents/${id}/lifecycle`); await expect(page.getByText('System-managed instance', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Archive instance', exact: true })).toHaveCount(0);
    expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
