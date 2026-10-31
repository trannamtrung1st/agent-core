import { captureExperience } from './support/experience-navigation';
import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
import { signInTenant, canonicalFetch, prepareOrdinaryChat } from './support/tenant-auth';
import type { ModelAdmin, ModelPreset } from '../src/services/models';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';
const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method, data: body, headers: { 'X-AgentCore-CSRF': csrf.token } });
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBeTruthy();
  return response.status() === 204 ? undefined as T : response.json();
}
async function setup() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  expect((await (await api.get('/health')).json()).profile).toBe('Synthetic');
  await call(api, '/api/v2/auth/login', { login, password });
  const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Model UI ${Date.now()}`, slug: `model-ui-${Date.now()}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`;
  const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
  return { api, tenant, root, revision, path: (section = 'connections') => `/t/${tenant}/integrations/ai-models/${section}` };
}
async function select(page: Page, label: string, text: string) {
  await page.getByRole('combobox', { name: label, exact: true }).click();
  const escaped = text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.locator('.ant-select-dropdown:visible .ant-select-item-option-content').filter({ hasText: new RegExp(`^${escaped}$`) }).click();
}
async function prepareAgent(api: APIRequestContext, tenant: string, id: string, name?: string) {
  await prepareOrdinaryChat(api, tenant, id);
  if (!name) return;
  const root = `/api/v2/tenants/${tenant}/agents/${id}`;
  const current = await call<{ securityRevision: number; agent: { instance: { revision: number; personaRevision: number; persona: Record<string, unknown> } } }>(api, root + '/editor');
  await call(api, root + '/persona', { persona: { ...current.agent.instance.persona, name }, expectedRevision: current.securityRevision,
    expectedInstanceRevision: current.agent.instance.revision, expectedPersonaRevision: current.agent.instance.personaRevision }, 'PUT');
}

test('protected-key onboarding, explicit default, stale connection recovery and responsive configuration', async ({ page }) => {
  const f = await setup(); const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  page.on('websocket', socket => {
    if (!socket.url().includes('/hubs/session')) return;
    socket.on('framereceived', ({ payload }) => {
      if (typeof payload === 'string') return;
      try {
        const data = payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.byteLength) as ArrayBuffer;
        for (const message of new MessagePackHubProtocol().parseMessages(data, { log() {} })) {
          if (message.type !== 3 || !message.result?.error) continue;
          const error = message.result.error;
          const safe = JSON.stringify({ category: error.category, code: error.code });
          errors.push(`Hub refusal: ${safe}`); console.error(`Hub refusal: ${safe}`);
        }
      } catch { /* Handshake and non-MessagePack control frames contain no acknowledgement. */ }
    });
  });
  try {
    await signInTenant(page, login, password); await page.goto(f.path());
    await expect(page.getByText('No provider connections you can read.')).toBeVisible();
    await page.getByRole('button', { name: 'Connect a provider', exact: true }).click();
    await select(page, 'Provider', 'OpenAI');
    await page.getByLabel('Connection name', { exact: true }).fill('Research provider — long owned connection');
    await page.getByRole('button', { name: 'Create protected inference key' }).click();
    const credential = page.getByRole('dialog', { name: 'Add credential', exact: true });
    await credential.getByLabel('Display name', { exact: true }).fill('Protected native inference');
    await credential.getByLabel('Protected value', { exact: true }).fill('synthetic-e2e-private-key');
    await credential.getByRole('button', { name: 'Save credential', exact: true }).click();
    await expect(credential).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Save connection' })).toBeEnabled();
    await page.getByRole('button', { name: 'Save connection' }).click();
    await expect(page.getByRole('dialog', { name: 'Connect a provider' })).toHaveCount(0);
    const name = 'Research provider — long owned connection';
    await page.getByRole('button', { name: `Test metadata ${name}` }).click();
    await expect(page.getByText('Offline Synthetic metadata fixture', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: `Add model on ${name}` }).click();
    await select(page, 'Starter model', 'GPT-6 Luna');
    await page.getByRole('button', { name: 'Save model', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Add model', exact: true })).toHaveCount(0);
    await page.getByRole('tab', { name: 'Defaults', exact: true }).click();
    await select(page, 'Default model', `GPT-6 Luna · ${name}`);
    await select(page, 'Default reasoning effort', 'high');
    await page.getByRole('button', { name: 'Save organization default' }).click();
    await expect(page.getByRole('button', { name: 'Save organization default' })).toBeDisabled();
    let catalog = await call<ModelAdmin>(f.api, f.root + '/models');
    expect(catalog.settings.defaultOfferingId).toBe(catalog.offerings[0].offeringId); expect(catalog.settings.defaultReasoningEffort).toBe('high');
    expect(catalog.availability[0].available).toBe(true); expect(JSON.stringify(catalog)).not.toContain('synthetic-e2e-private-key');
    await captureExperience(page, 'model-defaults');
    await page.reload(); await expect(page.getByRole('combobox', { name: 'Default model', exact: true })).toBeVisible();
    await page.getByRole('tab', { name: 'Connections', exact: true }).click();
    await page.getByRole('button', { name: `Edit connection ${name}` }).click();
    await page.getByLabel('Connection name', { exact: true }).fill('Retained after real conflict');
    catalog = await call<ModelAdmin>(f.api, f.root + '/models'); const c = catalog.connections[0];
    await call(f.api, f.root + `/model-connections/${c.connectionId}`, { kind: c.kind, label: 'Concurrent edit', endpoint: null, credentialId: c.credentialId, noCredential: false, enabled: true, expectedRevision: catalog.securityRevision, expectedConnectionRevision: c.revision }, 'PATCH');
    await page.getByRole('button', { name: 'Save connection' }).click();
    await expect(page.getByText('Reload before saving again', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Save connection' })).toBeDisabled();
    await page.getByRole('button', { name: 'Reload current configuration' }).click();
    await expect(page.getByText(/Concurrent edit · Enabled · revision 2/)).toBeVisible();
    await expect(page.getByLabel('Connection name', { exact: true })).toHaveValue('Retained after real conflict');
    await page.getByRole('button', { name: 'Save connection' }).click();
    const confirmation = page.getByRole('dialog', { name: 'Apply model configuration change?' });
    await expect(confirmation.getByText('Current readable references')).toBeVisible();
    await confirmation.getByRole('button', { name: 'Apply configuration change' }).click();
    await expect(page.getByRole('dialog', { name: 'Edit provider connection' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Reload', exact: true })).toBeFocused();
    catalog = await call<ModelAdmin>(f.api, f.root + '/models'); expect(catalog.connections[0].revision).toBe(3);
    for (const width of [1440, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 }); await page.goto(f.path('models'));
      await expect(page.getByRole('tabpanel', { name: 'Models', exact: true })).toContainText('Available for admission');
      expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
      await page.getByRole('button', { name: /^Edit model GPT-6 Luna/ }).click();
      const drawer = page.getByRole('dialog', { name: 'Edit model offering', exact: true });
      await expect(drawer.getByLabel('Model name', { exact: true })).toHaveValue('GPT-6 Luna');
      await expect(drawer.getByLabel('Model enabled', { exact: true })).toBeDisabled();
      await expect(drawer.getByLabel('Retire this offering permanently', { exact: true })).toBeDisabled();
      expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
      await drawer.getByRole('button', { name: 'Cancel', exact: true }).click();
      await expect(page.getByRole('button', { name: /^Edit model GPT-6 Luna/ })).toBeFocused();
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    const agent = await call<{ principalId: string; instance: { instanceId: string } }>(f.api, f.root + '/agents', {
      templateId: 'examiner', version: 1, expectedRevision: await f.revision()
    });
    const contextRole = await call<{ roleId: string }>(f.api, f.root + '/roles', { name: 'Model fixture Space execution', permissions: ['spaces.read'], expectedRevision: await f.revision() });
    await call(f.api, f.root + '/role-assignments', { principalId: agent.principalId, roleId: contextRole.roleId, expectedRevision: await f.revision() });
    await prepareAgent(f.api, f.tenant, agent.instance.instanceId);
    await page.goto(`/t/${f.tenant}`);
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('GPT-6 Luna');
    const prompt = `synthetic authorization audit: agents.read ${agent.instance.instanceId}`;
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toBeEnabled();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill(prompt);
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    const reply = 'Synthetic authorization check completed. Its result is recorded in the organization audit.';
    await expect(page.getByText(reply, { exact: true })).toBeVisible();
    await expect(page).toHaveURL(new RegExp(`/t/${f.tenant}/c/`));
    const session = page.url().split('/').pop()!;
    const conversation = await call<{ model: { offeringId: string; reasoningEffort: string } }>(f.api, f.root + `/sessions/${session}/conversation`);
    expect(conversation.model.offeringId).toBe(catalog.settings.defaultOfferingId);
    expect(conversation.model.reasoningEffort).toBe('high');
    await page.reload();
    await expect(page.getByText(reply, { exact: true })).toBeVisible();
    await expect(page.getByText(prompt, { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Model', exact: true }).click();
    for (const width of [1440, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await expect.poll(() => page.locator('.model-picker-overlay').evaluate(element => {
        const box = element.getBoundingClientRect();
        const trigger = document.querySelector('.model-picker-model')!.getBoundingClientRect();
        const label = document.querySelector('.model-picker-model .model-picker-name')!.getBoundingClientRect();
        const chevron = document.querySelector('.model-picker-model .anticon-down')!.getBoundingClientRect();
        return box.width > 0 && box.left >= 0 && box.right <= innerWidth && document.documentElement.scrollWidth <= innerWidth
          && label.left >= trigger.left && chevron.right <= trigger.right;
      })).toBe(true);
    }
    await page.keyboard.press('Escape');
    await page.setViewportSize({ width: 1440, height: 900 });

    // Model choice and Space context have independent authority and must survive together.
    const shared = crypto.randomUUID();
    await call(f.api, f.root + '/spaces', { spaceId: shared, name: 'Model integration space', description: '', instructions: 'Keep the selected model while using this Space.', expectedRevision: await f.revision() });
    await call(f.api, f.root + `/spaces/${shared}/memberships/${agent.instance.instanceId}`, { responsibility: 'Examiner', instructions: '', disabledInheritedBindingKeys: [], addedBindings: [], expectedRevision: await f.revision(), expectedSpaceRevision: 1, expectedMembershipRevision: 0 }, 'PUT');
    await page.getByRole('button', { name: 'Session Space', exact: true }).click();
    const context = page.getByRole('dialog', { name: 'Session context', exact: true });
    await page.getByRole('combobox', { name: 'Space', exact: true }).click();
    await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden)').getByTitle(/^Model integration space · /).click();
    await context.getByRole('button', { name: 'Apply to next turn', exact: true }).click();
    await expect(context).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Session Space', exact: true })).toContainText('Model integration space');
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('GPT-6 Luna');
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill(prompt);
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText(reply, { exact: true })).toHaveCount(2);
    const combined = await call<{ model: { offeringId: string } }>(f.api, f.root + `/sessions/${session}/conversation`);
    expect(combined.model.offeringId).toBe(catalog.settings.defaultOfferingId);
    const spaceContext = await call<{ selection: { spaceId: string } }>(f.api, f.root + `/sessions/${session}/context`);
    expect(spaceContext.selection.spaceId).toBe(shared);
    await page.reload();
    await expect(page.getByRole('button', { name: 'Session Space', exact: true })).toContainText('Model integration space');
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('GPT-6 Luna');

    await page.goto(f.path('models'));
    await page.getByRole('button', { name: `Inspect references for model GPT-6 Luna · Retained after real conflict`, exact: true }).click();
    const references = page.getByRole('dialog', { name: 'Model references: GPT-6 Luna · Retained after real conflict', exact: true });
    await select(page, 'Model reference category', 'Runs');
    await expect(references.getByText('2 readable references', { exact: false })).toBeVisible();
    await expect(references.getByText('Admitted Run pin', { exact: true })).toHaveCount(2);
    await references.getByRole('button', { name: 'Close', exact: true }).last().click();
    await expect(page.getByRole('button', { name: 'Inspect references for model GPT-6 Luna · Retained after real conflict', exact: true })).toBeFocused();
    await page.getByRole('tab', { name: 'Defaults', exact: true }).click();
    await select(page, 'Default model', 'No default — refuse inherited model execution');
    await page.getByRole('button', { name: 'Save organization default' }).click();
    const defaultPreview = page.getByRole('dialog', { name: 'Apply model configuration change?', exact: true });
    await expect(defaultPreview.getByText('Instances: 2', { exact: true })).toBeVisible();
    await expect(defaultPreview.getByText('Runs: 2', { exact: true })).toBeVisible();
    await defaultPreview.getByRole('button', { name: 'Keep editing', exact: true }).click();
    expect((await call<ModelAdmin>(f.api, f.root + '/models')).settings.defaultOfferingId).toBe(catalog.settings.defaultOfferingId);
    expect(errors).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('shared preset edits retain tenant declarations and read-only members cannot mutate model configuration', async ({ page, browser }) => {
  const f = await setup();
  let originalPreset: ModelPreset | undefined;
  try {
    const credential = await call<{ credential: { credentialId: string } }>(f.api, f.root + '/credentials', { displayName: 'Fixture inference', kind: 'ApiKey', purpose: 'ModelInference', protectedValue: 'synthetic-read-only-key', expectedRevision: await f.revision() });
    const c = await call<{ connectionId: string }>(f.api, f.root + '/model-connections', { kind: 'OpenAI', label: 'Read-only connection', credentialId: credential.credential.credentialId, noCredential: false, enabled: true, expectedRevision: await f.revision(), expectedConnectionRevision: 0 });
    const initialPreset = (await call<ModelPreset[]>(f.api, '/api/v2/system/model-presets')).find(p => p.presetId === 'dca10000-0000-4000-8000-000000000001')!;
    originalPreset = initialPreset;
    const { source: _source, verifiedAt: _verifiedAt, ...capabilities } = initialPreset.capabilities;
    const adopted = await call<{ offeringId: string }>(f.api, f.root + '/models', { connectionId: c.connectionId, presetId: initialPreset.presetId, externalModelId: initialPreset.externalModelId, label: initialPreset.label, transport: initialPreset.transport, capabilities, enabled: true, retired: false, expectedRevision: await f.revision(), expectedOfferingRevision: 0 });
    await signInTenant(page, login, password); await page.goto('/system/model-presets');
    await expect(page.getByRole('heading', { name: 'Model catalog', exact: true })).toBeVisible();
    await page.getByRole('button', { name: `Edit preset ${initialPreset.label}`, exact: true }).click();
    await page.getByLabel('Preset name', { exact: true }).fill('GPT-6 Luna — reviewed metadata');
    await page.getByRole('button', { name: 'Save preset', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Edit preset GPT-6 Luna — reviewed metadata', exact: true })).toBeVisible();
    expect((await call<ModelPreset[]>(f.api, '/api/v2/system/model-presets')).find(p => p.presetId === initialPreset.presetId)?.revision).toBe(initialPreset.revision + 1);
    const retained = (await call<ModelAdmin>(f.api, f.root + '/models')).offerings.find(o => o.offeringId === adopted.offeringId)!;
    expect(retained.label).toBe(initialPreset.label); expect(retained.capabilities).toEqual(initialPreset.capabilities); expect(retained.revision).toBe(1);
    await call(f.api, f.root + '/models/default', { offeringId: adopted.offeringId, reasoningEffort: null, expectedRevision: await f.revision(), expectedSettingsRevision: 0 }, 'PUT');
    await call(f.api, f.root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await f.revision() });
    const ownedReferences = await call<{ total: number }>(f.api, f.root + `/models/${adopted.offeringId}/references?kind=instances`);
    expect(ownedReferences.total).toBe(2); // Managed General Assistant also inherits the organization default.
    const role = await call<{ roleId: string }>(f.api, f.root + '/roles', { name: 'Read models only', permissions: ['models.read', 'model-connections.read'], expectedRevision: await f.revision() });
    const memberLogin = `model-reader-${Date.now()}@example.test`, memberPassword = 'synthetic-model-reader-password';
    const invitation = await call<{ invitationId: string; token: string }>(f.api, f.root + '/members/invite', { login: memberLogin, roleId: role.roleId, expectedRevision: await f.revision() });
    const member = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
    const context = await browser.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
    try {
      await call(member, '/api/v2/auth/register', { login: memberLogin, displayName: 'Model reader', password: memberPassword, invitation: { tenantId: f.tenant, invitationId: invitation.invitationId, token: invitation.token } });
      const readerPage = await context.newPage(); await signInTenant(readerPage, memberLogin, memberPassword); await readerPage.goto(f.path());
      await expect(readerPage.getByText('Read-only connection', { exact: true })).toBeVisible();
      await expect(readerPage.getByRole('button', { name: 'Connect a provider', exact: true })).toHaveCount(0);
      await expect(readerPage.getByRole('button', { name: 'Edit connection Read-only connection' })).toHaveCount(0);
      await expect(readerPage.getByRole('button', { name: 'Test metadata Read-only connection' })).toHaveCount(0);
      await readerPage.getByRole('button', { name: 'Inspect references for connection Read-only connection', exact: true }).click();
      await select(readerPage, 'Model reference category', 'Instances');
      await expect(readerPage.getByText('0 readable references', { exact: false })).toBeVisible();
      await expect(readerPage.getByText('No readable references in this category.', { exact: true })).toBeVisible();
      await readerPage.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click();
      await readerPage.getByRole('tab', { name: 'Defaults', exact: true }).click();
      await expect(readerPage.getByText('Read-only default', { exact: true })).toBeVisible();
      await expect(readerPage.getByRole('button', { name: 'Save organization default' })).toBeDisabled();
      const catalog = await readerPage.evaluate(async root => (await (await fetch(root + '/models')).json()) as ModelAdmin, f.root);
      expect(catalog.connections[0].connectionId).toBe(c.connectionId); expect(catalog.access.manageConnectionIds).toEqual([]); expect(catalog.access.canManageDefault).toBe(false);
      expect(catalog.presets.every(p => p.offeringReferences == null)).toBe(true);
      expect((await readerPage.request.get('/api/v2/system/model-presets')).status()).toBe(403);
    } finally { await member.dispose(); await context.close(); }
  } finally {
    if (originalPreset) {
      const current = (await call<ModelPreset[]>(f.api, '/api/v2/system/model-presets')).find(p => p.presetId === originalPreset.presetId)!;
      const { source: _source, verifiedAt: _at, ...capabilities } = originalPreset.capabilities;
      await call(f.api, `/api/v2/system/model-presets/${current.presetId}`, { kind: originalPreset.kind, externalModelId: originalPreset.externalModelId, label: originalPreset.label, transport: originalPreset.transport, enabled: originalPreset.enabled, archived: originalPreset.archived, capabilities, expectedRevision: current.revision }, 'PATCH');
    }
    await f.api.dispose();
  }
});

test('unavailable explicit model refuses without a Run, retains the draft, and recovers after deliberate retry', async ({ page }) => {
  const f = await setup();
  try {
    const credential = await call<{ credential: { credentialId: string } }>(f.api, f.root + '/credentials', {
      displayName: 'Recovery fixture key', kind: 'ApiKey', purpose: 'ModelInference', protectedValue: 'synthetic-recovery-private', expectedRevision: await f.revision()
    });
    const connection = await call<{ connectionId: string }>(f.api, f.root + '/model-connections', {
      kind: 'OpenAI', label: 'Recovery', credentialId: credential.credential.credentialId, noCredential: false, enabled: true, expectedRevision: await f.revision(), expectedConnectionRevision: 0
    });
    const preset = (await call<ModelPreset[]>(f.api, '/api/v2/system/model-presets')).find(p => p.presetId === 'dca10000-0000-4000-8000-000000000001')!;
    const { source: _source, verifiedAt: _at, ...capabilities } = preset.capabilities;
    const offering = await call<{ offeringId: string }>(f.api, f.root + '/models', {
      connectionId: connection.connectionId, presetId: preset.presetId, externalModelId: preset.externalModelId, label: 'Native conversation', transport: preset.transport,
      capabilities, enabled: true, retired: false, expectedRevision: await f.revision(), expectedOfferingRevision: 0
    });
    const agentState = await call<{ principalId: string; instance: { instanceId: string } }>(f.api, f.root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await f.revision() });
    const agent = agentState.instance.instanceId;
    const contextRole = await call<{ roleId: string }>(f.api, f.root + '/roles', { name: 'Recovery fixture Space execution', permissions: ['spaces.read'], expectedRevision: await f.revision() });
    await call(f.api, f.root + '/role-assignments', { principalId: agentState.principalId, roleId: contextRole.roleId, expectedRevision: await f.revision() });
    await prepareAgent(f.api, f.tenant, agent);
    await signInTenant(page, login, password); await page.goto(`/t/${f.tenant}`);
    await page.getByRole('button', { name: 'Model', exact: true }).click();
    await page.getByTitle('Native conversation · Recovery', { exact: true }).click();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('First exact model request');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(1);
    const session = page.url().split('/').pop()!, runsPath = f.root + `/agents/${agent}/activity/sessions/${session}/runs`;
    expect((await call<{ total: number }>(f.api, runsPath)).total).toBe(1);
    async function enable(enabled: boolean) {
      const current = await call<ModelAdmin>(f.api, f.root + '/models'); const c = current.connections.find(c => c.connectionId === connection.connectionId)!;
      await call(f.api, f.root + `/model-connections/${c.connectionId}`, { kind: c.kind, label: c.label, endpoint: null, credentialId: c.credentialId, noCredential: false, enabled,
        expectedRevision: current.securityRevision, expectedConnectionRevision: c.revision }, 'PATCH');
    }
    await enable(false); await page.reload();
    await expect(page.getByText('Native conversation · Recovery · Unavailable', { exact: true })).toBeVisible();
    const text = 'Retain this exact message when the selected model is unavailable';
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill(text);
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('The selected model is unavailable.');
    await expect(page.getByRole('alert')).toContainText('Provider or model');
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveText(text);
    await expect(page.getByRole('region', { name: 'Conversation', exact: true })).not.toContainText(text);
    expect((await call<{ total: number }>(f.api, runsPath)).total).toBe(1);
    await enable(true);
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(2);
    expect((await call<{ total: number }>(f.api, runsPath)).total).toBe(2);
    const saved = await call<{ model: { offeringId: string; connectionId: string; selectionSource: string } }>(f.api, f.root + `/sessions/${session}/conversation`);
    expect(saved.model.offeringId).toBe(offering.offeringId); expect(saved.model.connectionId).toBe(connection.connectionId); expect(saved.model.selectionSource).toBe('host');
    await page.reload(); await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(2);
    await expect(page.getByText(text, { exact: true })).toHaveCount(1);
    await expect(page.getByText('Native conversation · Recovery', { exact: true })).toBeVisible();
    await enable(false); await page.reload();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(2);
    const modelButton = page.getByRole('button', { name: 'Model', exact: true });
    await expect(modelButton).toContainText('Native conversation · Recovery · Unavailable');
    const recoveryDraft = 'Keep this draft while explicitly changing the conversation model';
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill(recoveryDraft);
    await modelButton.click();
    await page.getByTitle('Scripted Alpha · Offline Synthetic', { exact: true }).click();
    await expect(modelButton).toContainText('Scripted Alpha');
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveText(recoveryDraft);
    expect(page.url().split('/').pop()).toBe(session);
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(3);
    expect((await call<{ total: number }>(f.api, runsPath)).total).toBe(3);
    const recovered = await call<{ model: { offeringId: string; selectionSource: string } }>(f.api, f.root + `/sessions/${session}/conversation`);
    expect(recovered.model).toMatchObject({ offeringId: 'dca30000-0000-4000-8000-000000000001', selectionSource: 'host' });
    await page.reload(); await expect(page.getByText('Hello from synthetic.', { exact: true })).toHaveCount(3);
    await expect(page.getByText(recoveryDraft, { exact: true })).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('Scripted Alpha');

  } finally { await f.api.dispose(); }
});

test('Agent defaults refresh with identity and an explicit same-offering pin stays distinct from inheritance', async ({ page }) => {
  const f = await setup(); const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  const alpha = 'dca30000-0000-4000-8000-000000000001', beta = 'dca30000-0000-4000-8000-000000000002';
  try {
    async function agent(offeringId: string, reasoningEffort: string | null) {
      const saved = await call<{ instance: { instanceId: string; revision: number } }>(f.api, f.root + '/agents', {
        templateId: 'examiner', version: 1, expectedRevision: await f.revision()
      });
      await call(f.api, f.root + `/agents/${saved.instance.instanceId}/settings/modelDefaults`, {
        expectedRevision: await f.revision(), expectedInstanceRevision: saved.instance.revision, set: { preference: { mode: 'fixed', offeringId, reasoning: { mode: reasoningEffort ? 'fixed' : 'modelDefault', effort: reasoningEffort } } }, clear: []
      }, 'PATCH');
      await prepareAgent(f.api, f.tenant, saved.instance.instanceId, offeringId === alpha ? 'Model Alpha' : 'Model Beta');
      return saved.instance.instanceId;
    }
    const first = await agent(alpha, 'high'), second = await agent(beta, null);
    await signInTenant(page, login, password); await page.goto(`/t/${f.tenant}`);
    await page.getByRole('combobox', { name: 'Agent', exact: true }).click();
    await page.locator('.ant-select-dropdown:visible .ant-select-item-option').filter({ hasText: /^Model Alpha —/ }).click();
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('Scripted Alpha');
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('Default');
    await page.getByRole('button', { name: 'Model', exact: true }).click();
    await expect(page.getByTitle('Use Agent default', { exact: true })).toHaveAttribute('aria-selected', 'true');
    await page.getByTitle('Scripted Alpha · Offline Synthetic', { exact: true }).click();
    await expect(page.getByRole('button', { name: 'Model', exact: true })).not.toContainText('Default');
    await page.getByRole('button', { name: 'Model', exact: true }).click();
    await select(page, 'Reasoning preference', 'Fixed effort');
    await page.getByRole('slider', { name: 'Reasoning effort', exact: true }).focus();
    await page.getByRole('slider', { name: 'Reasoning effort', exact: true }).press('End');
    await expect(page.getByRole('slider', { name: 'Reasoning effort', exact: true })).toHaveAttribute('aria-valuetext', 'High');
    await page.keyboard.press('Escape');
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Explicitly pin the same Agent default');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    const pinned = page.url().split('/').pop()!;
    const saved = await call<{ model: { offeringId: string; reasoningEffort: string; selectionSource: string } }>(f.api, f.root + `/sessions/${pinned}/conversation`);
    expect(saved.model).toMatchObject({ offeringId: alpha, reasoningEffort: 'high', selectionSource: 'host' });
    expect((await call<{ modelPreference: { mode: string; offeringId: string } }>(f.api, f.root + `/sessions/${pinned}/conversation`)).modelPreference).toMatchObject({ mode: 'fixed', offeringId: alpha });
    await page.reload(); await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    await page.goto(`/t/${f.tenant}`);
    await page.getByRole('combobox', { name: 'Agent', exact: true }).click();
    await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option').filter({ hasText: /^Model Beta —/ }).click();
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('Scripted Beta');
    await expect(page.getByRole('button', { name: 'Model', exact: true })).not.toContainText('High');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    for (const width of [1440, 768, 390, 320]) {
      await page.mouse.move(0, 0);
      await page.setViewportSize({ width, height: 900 });
      await page.getByRole('button', { name: 'Model', exact: true }).click();
      await expect(page.getByTitle('Use Agent default', { exact: true })).toHaveAttribute('aria-selected', 'true');
      await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
      await page.keyboard.press('Escape');
    }
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Inherit the selected Agent model');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    const inherited = page.url().split('/').pop()!;
    const model = await call<{ model: { offeringId: string; reasoningEffort: string | null; selectionSource: string } }>(f.api, f.root + `/sessions/${inherited}/conversation`);
    expect(model.model).toMatchObject({ offeringId: beta, reasoningEffort: null, selectionSource: 'host' });
    expect((await call<{ modelPreference: { mode: string } }>(f.api, f.root + `/sessions/${inherited}/conversation`)).modelPreference.mode).toBe('inherit');
    expect(first).not.toBe(second); expect(errors).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('configured native offering flows through unattended and Automation selectors, exact approval and completed work', async ({ page }) => {
  const f = await setup(); const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  try {
    const credential = (await call<{ credential: { credentialId: string } }>(f.api, f.root + '/credentials', {
      displayName: 'Gated Synthetic inference fixture', kind: 'ApiKey', purpose: 'ModelInference', protectedValue: 'gated-model-offline-only', expectedRevision: await f.revision()
    })).credential.credentialId;
    const connection = await call<{ connectionId: string }>(f.api, f.root + '/model-connections', {
      kind: 'OpenAI', label: 'Gated production fixture', endpoint: null, credentialId: credential, noCredential: false, enabled: true,
      expectedRevision: await f.revision(), expectedConnectionRevision: 0
    });
    const preset = (await call<ModelPreset[]>(f.api, '/api/v2/system/model-presets')).find(p => p.kind === 'OpenAI')!;
    const p = preset.capabilities;
    const offering = await call<{ offeringId: string }>(f.api, f.root + '/models', {
      connectionId: connection.connectionId, presetId: preset.presetId, externalModelId: preset.externalModelId, label: 'Gated native model', transport: preset.transport,
      capabilities: { tools: p.tools, vision: p.vision, hierarchy: p.hierarchy, semanticMode: p.semanticMode, forcedToolChoice: p.forcedToolChoice,
        structuredOutput: p.structuredOutput, reasoningEfforts: p.reasoningEfforts, defaultReasoningEffort: p.defaultReasoningEffort },
      enabled: true, retired: false, expectedRevision: await f.revision(), expectedOfferingRevision: 0
    });
    await call(f.api, f.root + '/models/default', { offeringId: offering.offeringId, reasoningEffort: 'medium', expectedRevision: await f.revision(), expectedSettingsRevision: 0 }, 'PUT');
    const agent = await call<{ principalId: string; instance: { instanceId: string } }>(f.api, f.root + '/agents', {
      templateId: 'general-assistant', version: 36, expectedRevision: await f.revision()
    });
    // Incumbent independent-work permissions only. Approval mode Always requires Review; it never auto-allows work.
    const role = await call<{ roleId: string }>(f.api, f.root + '/roles', { name: 'Gated model executor',
      permissions: ['automations.run', 'sessions.create', 'agent-runs.create', 'sessions.read', 'agent-runs.read', 'agents.chat'], expectedRevision: await f.revision() });
    await call(f.api, f.root + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await f.revision() });
    await prepareAgent(f.api, f.tenant, agent.instance.instanceId);
    await call(f.api, f.root + '/operation-policies/agent-runs.create', { mode: 'always', independentReview: false, expectedRevision: await f.revision(), expectedPolicyRevision: 0 }, 'PUT');
    const id = agent.instance.instanceId, label = 'Gated native model · Gated production fixture';
    await signInTenant(page, login, password); await page.goto(`/t/${f.tenant}/admin/agents/${id}/unattended-model`);
    await select(page, 'Unattended model', label); await select(page, 'Unattended reasoning effort', 'high');
    await page.getByRole('button', { name: 'Save Unattended model', exact: true }).click();
    await expect(page.getByText('Configuration saved. Reading current values.', { exact: true })).toBeVisible();
    await page.reload(); await expect(page.getByRole('combobox', { name: 'Unattended model', exact: true }).locator('..')).toContainText(label);
    await page.goto(`/t/${f.tenant}/admin/agents/${id}/automations`);
    await page.getByRole('button', { name: 'New Automation', exact: true }).click();
    const editor = page.getByRole('dialog', { name: 'New Automation', exact: true });
    await editor.getByRole('textbox', { name: 'Automation name', exact: true }).fill('Gated model selector qualification');
    await editor.getByRole('textbox', { name: 'Automation instructions', exact: true }).fill('synthetic-automation-attention: finish this model-qualified occurrence');
    await editor.getByRole('checkbox', { name: 'Enable automatic occurrences', exact: true }).check();
    await editor.getByRole('spinbutton', { name: 'Schedule maximum occurrences', exact: true }).fill('2');
    await editor.getByRole('button', { name: 'Execution options', exact: true }).click();
    await editor.getByRole('checkbox', { name: 'Task requires tools', exact: true }).check();
    await select(page, 'Automation execution model', label); await select(page, 'Automation reasoning effort', 'low');
    await editor.getByRole('button', { name: 'Save Automation', exact: true }).click(); await expect(editor).toHaveCount(0);
    const registration = (await call<{ items: { modelPreference: { mode: string; offeringId: string; reasoning: { mode: string; effort: string } } }[] }>(f.api, f.root + `/agents/${id}/automations`)).items[0];
    expect(registration).toMatchObject({ modelPreference: { mode: 'fixed', offeringId: offering.offeringId, reasoning: { mode: 'fixed', effort: 'low' } } });
    await page.getByRole('button', { name: 'Run now', exact: true }).click();
    await page.getByRole('dialog', { name: 'Run saved Automation now?', exact: true }).getByRole('button', { name: 'Run now', exact: true }).click();
    const link = page.getByRole('link', { name: 'Review the exact admission request', exact: true }); await expect(link).toBeVisible();
    const requestId = (await link.getAttribute('href'))!.split('/').pop()!;
    const pending = await call<{ runId: null; sessionId: null; review: { executionState: string } }>(f.api, f.root + `/approvals/${requestId}`);
    expect(pending.runId).toBeNull(); expect(pending.sessionId).toBeNull(); expect(pending.review.executionState).toBe('waitingDecision');
    const workPath = f.root + `/agents/${id}/activity/background-sessions`;
    expect((await call<{ total: number }>(f.api, workPath)).total).toBe(0);
    await link.click(); const review = page.getByRole('dialog', { name: 'Approval details', exact: true });
    await expect(review.getByRole('region', { name: 'Exact saved admission', exact: true })).toContainText(label + ' · low reasoning');
    await review.getByRole('button', { name: 'Approve', exact: true }).click();
    await page.getByRole('dialog', { name: 'Approve?', exact: true }).getByRole('button', { name: 'Approve', exact: true }).click();
    type Work = { total: number; items: { session: { sessionId: string }; initialRun: { status: string; modelOfferingId: string; outcomeText: string } }[] };
    await expect.poll(async () => (await call<Work>(f.api, workPath)).items[0]?.initialRun.status).toBe('completed');
    const work = await call<Work>(f.api, workPath); expect(work.total).toBe(1);
    expect(work.items[0].initialRun.modelOfferingId).toBe(offering.offeringId);
    expect(work.items[0].initialRun.outcomeText).toBe("An unresolved checkpoint needs the owner's attention.");
    const saved = await call<{ model: { offeringId: string; connectionId: string; reasoningEffort: string } }>(f.api, f.root + `/sessions/${work.items[0].session.sessionId}/conversation`);
    expect(saved.model).toMatchObject({ offeringId: offering.offeringId, connectionId: connection.connectionId, reasoningEffort: 'low' });
    expect(errors).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('Definition defaults publish into Chat and sparse Instance model changes clear incompatible inherited effort', async ({ page }) => {
  const f = await setup(); const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  try {
    const draft = await call<{ definitionId: string; draftId: string }>(f.api, f.root + '/agent-definitions', {
      name: 'Definition model inheritance', expectedRevision: await f.revision()
    });
    const route = `/t/${f.tenant}/admin/definitions/${draft.definitionId}/drafts/${draft.draftId}/models`;
    await signInTenant(page, login, password); await page.goto(route);
    await select(page, 'Definition model', 'Scripted Alpha · Offline Synthetic'); await select(page, 'Definition reasoning', 'high');
    await page.getByRole('button', { name: 'Save draft', exact: true }).click();
    await expect(page.getByText('Saved', { exact: true })).toBeVisible(); await page.reload();
    await expect(page.getByTitle('Scripted Alpha · Offline Synthetic', { exact: true })).toBeVisible();
    await expect(page.getByTitle('high', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Publish', exact: true }).click();
    await page.getByRole('dialog', { name: 'Publish this saved draft?', exact: true }).getByRole('button', { name: 'Publish definition', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/admin/definitions/${draft.definitionId}$`));
    const agent = await call<{ instance: { instanceId: string } }>(f.api, f.root + '/agents/from-publication', {
      definitionId: draft.definitionId, version: 1, expectedRevision: await f.revision()
    });
    const id = agent.instance.instanceId;
    await prepareAgent(f.api, f.tenant, id);
    await page.goto(`/t/${f.tenant}`);
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('Scripted Alpha');
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('Default');
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Use the published Definition model');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    const first = page.url().split('/').pop()!;
    const inherited = await call<{ model: { offeringId: string; reasoningEffort: string | null; selectionSource: string } }>(f.api, f.root + `/sessions/${first}/conversation`);
    expect(inherited.model).toMatchObject({ offeringId: 'dca30000-0000-4000-8000-000000000001', reasoningEffort: 'high', selectionSource: 'host' });
    await page.goto(`/t/${f.tenant}/admin/agents/${id}/settings`);
    await page.getByRole('button').filter({ hasText: 'Model defaults' }).click();
    await select(page, 'Instance model', 'Scripted Beta · Offline Synthetic');
    await expect(page.getByRole('combobox', { name: 'Instance reasoning', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Save Model defaults', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Save Model defaults', exact: true })).toBeDisabled();
    await expect(page.getByText('Unsaved changes', { exact: true })).toHaveCount(0);
    await page.reload(); await page.getByRole('button').filter({ hasText: 'Model defaults' }).click();
    await expect(page.getByTitle('Scripted Beta · Offline Synthetic', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Model preference: Instance override', exact: true })).toBeVisible();
    await page.goto(`/t/${f.tenant}`);
    await expect(page.getByRole('button', { name: 'Model', exact: true })).toContainText('Scripted Beta');
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Use the sparse Instance model override');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    const second = page.url().split('/').pop()!;
    const overridden = await call<{ model: { offeringId: string; reasoningEffort: string | null; selectionSource: string } }>(f.api, f.root + `/sessions/${second}/conversation`);
    expect(overridden.model).toMatchObject({ offeringId: 'dca30000-0000-4000-8000-000000000002', reasoningEffort: null, selectionSource: 'host' });
    expect(first).not.toBe(second); expect(errors).toEqual([]);
  } finally { await f.api.dispose(); }
});

test('omitted manual default requires explicit recovery and fresh confirmation after conflict', async ({ page }) => {
  const f = await setup(); const errors: string[] = []; const failedRequests: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('requestfailed', request => { if (!request.failure()?.errorText.includes('ERR_ABORTED')) failedRequests.push(request.url()); });
  try {
    const key = await call<{ credential: { credentialId: string } }>(f.api, f.root + '/credentials', {
      displayName: 'Recovery key', kind: 'ApiKey', purpose: 'ModelInference', protectedValue: 'synthetic-recovery-key', expectedRevision: await f.revision() });
    const c = await call<{ connectionId: string }>(f.api, f.root + '/model-connections', {
      kind: 'OpenAI', label: 'Recovery provider', credentialId: key.credential.credentialId, noCredential: false, enabled: true, expectedRevision: await f.revision(), expectedConnectionRevision: 0 });
    const o = await call<{ offeringId: string }>(f.api, f.root + '/models', {
      connectionId: c.connectionId, presetId: null, externalModelId: 'manual-unlisted-model', label: 'Manual unlisted model', transport: 'Responses',
      capabilities: { tools: true, vision: false, hierarchy: 'NativeDeveloper', semanticMode: 'NativeStructuredOutput', structuredOutput: true, forcedToolChoice: true, reasoningEfforts: [], defaultReasoningEffort: null },
      enabled: true, retired: false, expectedRevision: await f.revision(), expectedOfferingRevision: 0 });
    await call(f.api, f.root + '/models/default', { offeringId: o.offeringId, reasoningEffort: null, expectedRevision: await f.revision(), expectedSettingsRevision: 0 }, 'PUT');
    await signInTenant(page, login, password); await page.goto(f.path());
    await page.getByRole('button', { name: 'Discover models Recovery provider' }).click();
    await expect(page.getByText('Offline Synthetic metadata fixture', { exact: true })).toBeVisible();
    await page.getByRole('tab', { name: 'Models', exact: true }).click();
    const modelRow = page.getByRole('row').filter({ hasText: 'Manual unlisted model' });
    await expect(modelRow).toContainText('Not returned by discovery');
    await page.getByRole('button', { name: /^Edit model Manual unlisted model/ }).click();
    const drawer = page.getByRole('dialog', { name: 'Edit model offering', exact: true });
    const restore = drawer.getByRole('checkbox', { name: 'Restore availability for this exact model' });
    await expect(restore).not.toBeChecked();
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 900 });
      await expect(drawer.getByText('Model missing from provider listing', { exact: true })).toBeVisible();
      expect(await drawer.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
      await expect(drawer.getByRole('button', { name: 'Save model', exact: true })).toBeInViewport();
    }
    await restore.focus(); await page.keyboard.press('Space'); await expect(restore).toBeChecked();
    await drawer.getByLabel('Model name', { exact: true }).fill('Retained recovery draft');
    // Concurrent discovery changes the security witness, never the draft or historical model identity.
    await call(f.api, f.root + `/model-connections/${c.connectionId}/discover`, { expectedRevision: await f.revision(), expectedConnectionRevision: 1 });
    await drawer.getByRole('button', { name: 'Save model', exact: true }).click();
    await expect(drawer.getByText('Reload before saving again', { exact: true })).toBeVisible();
    await drawer.getByRole('button', { name: 'Reload current configuration' }).click();
    await expect(drawer.getByRole('button', { name: 'Save model', exact: true })).toBeEnabled();
    await expect(restore).not.toBeChecked();
    await expect(drawer.getByLabel('Model name', { exact: true })).toHaveValue('Retained recovery draft');
    await restore.check(); await drawer.getByRole('button', { name: 'Save model', exact: true }).click();
    const confirmation = page.getByRole('dialog', { name: 'Apply model configuration change?' });
    await expect(confirmation.getByText('Restore availability despite missing provider metadata; later discovery can block this model again.', { exact: true })).toBeVisible();
    await confirmation.getByRole('button', { name: 'Apply configuration change' }).click();
    await expect(drawer).toHaveCount(0);
    await expect(page.getByRole('row').filter({ hasText: 'Retained recovery draft' })).toContainText('Available for admission');
    let catalog = await call<ModelAdmin>(f.api, f.root + '/models');
    expect(catalog.settings.defaultOfferingId).toBe(o.offeringId);
    expect(catalog.offerings.find(item => item.offeringId === o.offeringId)).toMatchObject({ externalModelId: 'manual-unlisted-model', observation: 'Unknown', revision: 2 });
    await page.getByRole('tab', { name: 'Connections', exact: true }).click();
    const [rediscovery] = await Promise.all([
      page.waitForResponse(response => response.url().endsWith(`/model-connections/${c.connectionId}/discover`) && response.request().method() === 'POST'),
      page.getByRole('button', { name: 'Discover models Recovery provider' }).click()
    ]);
    expect(rediscovery.ok()).toBe(true);
    catalog = await call<ModelAdmin>(f.api, f.root + '/models');
    expect(catalog.availability.find(item => item.offeringId === o.offeringId)?.reason).toBe('not_returned');
    expect(errors).toEqual([]); expect(failedRequests).toEqual([]);
  } finally { await f.api.dispose(); }
});
