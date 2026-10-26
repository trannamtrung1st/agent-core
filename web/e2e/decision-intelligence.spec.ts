import { expect, test, request as requests } from '@playwright/test';
import { canonicalFetch, grantPrivateSpaceRead, signInTenant } from './support/tenant-auth';

test('Auto routing keeps saved intent, pins each Run, and repairs through a fixed choice', async ({ page }) => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  const call = async (path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST') => {
    const csrf = await (await api.get('/api/v2/auth/csrf')).json();
    const response = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
    expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy();
    return response.status() === 204 ? undefined : response.json();
  };
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic');
    await call('/api/v2/auth/login', { login: 'tenant-chat-owner@example.test', password: 'synthetic-tenant-chat-password' });
    const nonce = crypto.randomUUID().slice(0, 8);
    const tenant = (await call('/api/v2/system/tenants', { name: `Routing ${nonce}`, slug: `routing-${nonce}` })).tenantId;
    const root = `/api/v2/tenants/${tenant}`;
    const revision = async () => (await call(root + '/access/me')).revision;
    const agent = await call(root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() });
    await grantPrivateSpaceRead(api, tenant, agent.instance.instanceId);
    await signInTenant(page, 'tenant-chat-owner@example.test', 'synthetic-tenant-chat-password');
    await page.goto(`/t/${tenant}/integrations/ai-models/routing`);
    await expect(page.getByText('Decision service: ready', { exact: true })).toBeVisible();
    await page.getByRole('checkbox', { name: 'Allow Auto for new Runs', exact: true }).check();
    await page.getByRole('button', { name: 'Save routing', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Save routing', exact: true })).toBeDisabled();
    await page.goto(`/t/${tenant}`);
    await page.getByRole('button', { name: 'Model', exact: true }).click();
    await page.getByRole('option', { name: /Auto · choose per new Run/ }).click();
    await page.getByRole('button', { name: 'Model', exact: true }).click();
    await page.getByRole('combobox', { name: 'Auto reasoning intent', exact: true }).click();
    await page.locator('.model-picker-effort-popup').getByText('High', { exact: true }).click({ timeout: 15000 });
    await page.keyboard.press('Escape');
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Exercise Auto admission in Synthetic mode');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    const url = page.url(), session = url.split('/').pop();
    const saved = await call(root + `/sessions/${session}/conversation`);
    expect(saved.modelPreference.mode).toBe('auto');
    expect(saved.model.offeringId).toBeTruthy();
    expect(saved.modelPreference.reasoning).toMatchObject({ mode: 'fixed', effort: 'high' });
    expect(saved.model.reasoningEffort).toBe('high');
    await page.reload();
    await page.getByRole('button', { name: 'Model', exact: true }).click();
    await expect(page.getByRole('option', { name: /Auto · choose per new Run/ })).toHaveAttribute('aria-selected', 'true');
    await page.getByRole('combobox', { name: 'Auto reasoning intent', exact: true }).click();
    await page.locator('.model-picker-effort-popup').getByText('Model default', { exact: true }).click({ timeout: 15000 });
    await page.keyboard.press('Escape');
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('high reasoning');
    await page.locator('input.attach-input').setInputFiles({ name: 'routing-vision.png', mimeType: 'image/png',
      buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64') });
    await expect(page.locator('.attach-list')).toContainText('routing-vision.png');
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Inspect this image using an eligible Auto model');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(2);
    const vision = await call(root + `/sessions/${session}/conversation`);
    expect(vision.modelPreference.mode).toBe('auto');
    expect(vision.modelPreference.reasoning.mode).toBe('modelDefault');
    expect(vision.model.offeringId).toBe('dca30000-0000-4000-8000-000000000003');
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('Scripted Vision');
    const catalog = await call(root + '/models');
    await call(root + '/models/routing', { routing: { ...catalog.settings.routing, enabled: false }, expectedRevision: catalog.securityRevision, expectedSettingsRevision: catalog.settings.revision }, 'PUT');
    const message = page.getByRole('textbox', { name: 'Message', exact: true });
    await message.fill('Retain this refused Auto request');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText(/Model routing could not admit this Run/)).toBeVisible();
    await expect(message).toHaveText('Retain this refused Auto request');
    await page.getByRole('button', { name: 'Model', exact: true }).click();
    await page.getByRole('option', { name: /Scripted Beta/ }).click();
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(3);
    expect((await call(root + `/sessions/${session}/conversation`)).modelPreference.mode).toBe('fixed');
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0);
    const latest = await call(root + '/models');
    await call(root + '/models/routing', { routing: { ...latest.settings.routing, enabled: true }, expectedRevision: latest.securityRevision, expectedSettingsRevision: latest.settings.revision }, 'PUT');
    await call(root + `/agents/${agent.instance.instanceId}/settings/modelDefaults`, {
      expectedRevision: await revision(), expectedInstanceRevision: agent.instance.revision,
      set: { preference: { mode: 'auto', reasoning: { mode: 'modelDefault' } } }, clear: [] }, 'PATCH');
    await page.reload();
    await page.getByRole('button', { name: 'Model', exact: true }).click();
    await page.getByRole('option', { name: 'Use Agent default (Auto)', exact: true }).click();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Use the inherited Instance Auto preference');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(4);
    const inherited = await call(root + `/sessions/${session}/conversation`);
    expect(inherited.modelPreference.mode).toBe('inherit');
    expect(inherited.model.offeringId).toBe('dca30000-0000-4000-8000-000000000001');
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('Agent default · Auto');

  } finally { await api.dispose(); }
});
