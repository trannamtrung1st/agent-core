import { expect, test, request as requests, type APIRequestContext } from '@playwright/test';
import { canonicalFetch, prepareOrdinaryChat, signInTenant } from './support/tenant-auth';
const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body ? 'POST' : 'GET'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${path}: ${response.status()} ${await response.text()}`).toBeTruthy(); return response.json();
}
type Editor = { securityRevision: number; canUpdate: boolean; agent: { managedKind: string; instance: { revision: number; instanceId: string; lifecycle: string; persona: { name: string } } } };
test('owner setup retry refreshes the ordinary picker and starts a real conversation', async ({ page }) => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await call(api, '/api/v2/auth/login', { login, password });
    const nonce = crypto.randomUUID().slice(0, 8);
    const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Recovery ${nonce}`, slug: `recovery-${nonce}` })).tenantId;
    // The API failure-injection tests own missing-default recovery. This browser fixture
    // presents that pending read state, then exercises the real authorized retry endpoint.
    let pending = true; let writes = 0;
    await page.route(`**/api/v2/tenants/${tenant}/chat/agents?*`, async route => {
      const response = await route.fetch(); const body = await response.json();
      await route.fulfill({ response, json: pending ? { ...body, items: [], total: 0, emptyReason: 'setup_pending' } : body });
    });
    await page.route(`**/api/v2/tenants/${tenant}/general-assistant/reconcile`, async route => {
      writes++; const response = await route.fetch(); expect(response.ok()).toBe(true);
      expect((await response.json()).status).toBe('ready'); pending = false; await route.fulfill({ response });
    });
    await signInTenant(page, login, password); await page.goto(`/t/${tenant}`);
    const retry = page.getByRole('button', { name: 'Retry assistant setup' }); await expect(retry).toBeVisible();
    for (const width of [320, 1440]) {
      await page.setViewportSize({ width, height: 900 }); await expect(retry).toBeVisible();
      await expect.poll(async () => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }
    await retry.click(); await expect(page.getByTitle(/^General Assistant · System-managed —/)).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Agent' })).toBeFocused(); expect(writes).toBe(1);
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Hello after setup retry');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(1); expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
test('managed availability preserves history, denies new work, retains reset state and re-enables future work', async ({ page }) => {
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
    await expect(page.locator('.agent-picker .ant-select-content')).toContainText('General Assistant');
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Hello General Assistant');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(1);
    const conversation = page.url(), session = conversation.split('/c/')[1];
    await page.goto(`/t/${tenant}/admin/agents/${id}/lifecycle`);
    const disable = page.getByRole('button', { name: 'Disable agent', exact: true }); await expect(disable).toBeEnabled();
    await disable.click(); const confirmation = page.getByRole('dialog', { name: 'Disable agent?' });
    await expect(confirmation).toContainText('cancel active Runs'); await expect(confirmation).toContainText('may still complete');
    for (const width of [320, 390, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 }); await page.evaluate(async () => { await Promise.all(document.getAnimations().map(animation => animation.finished.catch(() => {}))); }); await expect.poll(async () => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: test.info().outputPath(`disable-confirmation-${width}.png`) });
    }
    await confirmation.getByRole('button', { name: 'Disable agent', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Enable agent', exact: true })).toBeEnabled();
    await expect.poll(async () => (await call<Editor>(api, resource + '/editor')).agent.instance.lifecycle).toBe('Disabled');
    for (const width of [320, 390, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 }); await page.evaluate(async () => { await Promise.all(document.getAnimations().map(animation => animation.finished.catch(() => {}))); }); await expect.poll(async () => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: test.info().outputPath(`disabled-detail-${width}.png`) });
    }
    await page.goto(`/t/${tenant}/admin/agents`);
    await expect(page.getByRole('combobox', { name: 'Status on this page' })).toBeVisible();
    for (const width of [320, 390, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 }); await page.evaluate(async () => { await Promise.all(document.getAnimations().map(animation => animation.finished.catch(() => {}))); }); await expect.poll(async () => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: test.info().outputPath(`disabled-inventory-${width}.png`) });
    }
    await page.goto(`/t/${tenant}/admin/agents/${id}/settings`);
    await expect(page.getByRole('switch', { name: 'Visible in Chat', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Reset to System Defaults', exact: true }).click();
    await page.getByRole('dialog', { name: 'Reset to System Defaults?' }).getByRole('button', { name: 'Reset to System Defaults', exact: true }).click();
    await expect.poll(async () => (await call<Editor>(api, resource + '/editor')).agent.instance.lifecycle).toBe('Disabled');
    await page.goto(`/t/${tenant}`); await expect(page.getByText(/No enabled agents available/)).toBeVisible();
    await page.goto(conversation); await expect(page.getByText('Hello General Assistant', { exact: true })).toBeVisible();
    await expect(page.getByText(/Agent disabled. History is kept/)).toBeVisible(); await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveCount(0);
    for (const width of [320, 390, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 }); await page.evaluate(async () => { await Promise.all(document.getAnimations().map(animation => animation.finished.catch(() => {}))); }); await expect.poll(async () => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: test.info().outputPath(`disabled-history-${width}.png`) });
    }
    const csrf = await (await api.get('/api/v2/auth/csrf')).json();
    const current = await call<{ revision: number }>(api, root + `/sessions/${session}`);
    const access = await call<{ revision: number }>(api, root + '/access/me');
    const denied = await canonicalFetch(api, root + `/sessions/${session}/messages`, { method: 'POST', headers: { 'X-AgentCore-CSRF': csrf.token }, data: { messageId: crypto.randomUUID(), text: 'Must be refused', expectedRevision: access.revision, expectedSessionRevision: current.revision } });
    expect(denied.status()).toBe(403);
    await page.goto(`/t/${tenant}/admin/agents/${id}/lifecycle`); await page.getByRole('button', { name: 'Enable agent', exact: true }).click();
    await page.getByRole('dialog', { name: 'Enable agent?' }).getByRole('button', { name: 'Enable agent', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Disable agent', exact: true })).toBeEnabled();
    await page.goto(conversation); await page.getByRole('textbox', { name: 'Message', exact: true }).fill('A fresh request after enabling');
    await page.getByRole('button', { name: 'Send', exact: true }).click(); await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(2);
    expect(errors).toEqual([]);
  } finally { await api.dispose(); }
});
test('long Run optimizes context without another response or duplicate file execution', async ({ page }) => {
  test.setTimeout(360000);
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    await call(api, '/api/v2/auth/login', { login, password }); const nonce = crypto.randomUUID().slice(0, 8);
    const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Compaction ${nonce}`, slug: `compaction-${nonce}` })).tenantId;
    const root = `/api/v2/tenants/${tenant}`;
    const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
    const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() });
    await prepareOrdinaryChat(api, tenant, agent.instance.instanceId);
    await call(api, root + `/agents/${agent.instance.instanceId}/execution-budgets`, {
      expectedRevision: await revision(), expectedInstanceRevision: 1,
      budgets: { standard: { maxSteps: 24, durationSeconds: 600 }, interactiveBrowser: { maxSteps: 48, durationSeconds: 600 }, unattendedBoundBrowser: { maxSteps: 32, durationSeconds: 600 } }
    }, 'PUT');

    const role = await call<{ roleId: string }>(api, root + '/roles', { name: 'Compaction source reader', permissions: ['sessions.read', 'attachments.read'], expectedRevision: await revision() });
    await call(api, root + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
    await signInTenant(page, login, password); await page.goto(`/t/${tenant}`);
    await page.getByRole('button', { name: 'Add content', exact: true }).click(); const chooser = page.waitForEvent('filechooser');
    await page.getByRole('menuitem', { name: /Attach file/ }).click();
    await (await chooser).setFiles({ name: 'context-proof.txt', mimeType: 'text/plain', buffer: Buffer.from('COMPACTION-FILE-PROOF ' + 'read evidence '.repeat(1600)) });
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('synthetic-in-run-compaction Read the supplied file twelve times and retain exact source proof.');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Completed twelve reads of COMPACTION-FILE-PROOF. Exact file receipts remain available.', { exact: true })).toHaveCount(1, { timeout: 300000 });
    const session = page.url().split('/c/')[1];
    const view = await call<{ agentInstanceId: string }>(api, root + `/sessions/${session}/conversation`);
    const runs = await call<{ items: { runId: string; responseId: string; executionDiagnostic: { recordedToolResults: number; compaction: { count: number } } }[] }>(api, root + `/agents/${view.agentInstanceId}/activity/sessions/${session}/runs`);
    expect(runs.items).toHaveLength(1); expect(runs.items[0].executionDiagnostic.recordedToolResults).toBeGreaterThanOrEqual(12);
    expect(runs.items[0].executionDiagnostic.recordedToolResults).toBeLessThanOrEqual(13);
    expect(runs.items[0].executionDiagnostic.compaction.count).toBeGreaterThan(0);
    await expect.poll(async () => (await call<{ items: { status: string }[] }>(api, root + `/agents/${view.agentInstanceId}/activity/sessions/${session}/runs`)).items[0]?.status,
      { timeout: 300000 }).toBe('completed');
    await page.reload(); await expect(page.getByText('Completed twelve reads of COMPACTION-FILE-PROOF. Exact file receipts remain available.', { exact: true })).toHaveCount(1);
    await page.screenshot({ path: test.info().outputPath('compacted-run-chat.png') });
    await page.goto(`/t/${tenant}/admin/agents/${view.agentInstanceId}/activity/runs?run=${runs.items[0].runId}`);
    const details = page.getByRole('dialog', { name: 'Run details', exact: true });
    await expect(details.getByRole('region', { name: 'Context optimization' })).toContainText('Exact execution receipts are retained');
    await expect(details).not.toContainText('read evidence read evidence');
    for (const width of [320, 390, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      await page.evaluate(async () => { await Promise.all(document.getAnimations().map(animation => animation.finished.catch(() => {}))); });
      await expect.poll(async () => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: test.info().outputPath(`compacted-run-details-${width}.png`) });
    }
  } finally { await api.dispose(); }
});
