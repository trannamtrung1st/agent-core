import { prepareOrdinaryChat } from './tenant-auth';
import { expect, request as requests, type APIRequestContext, type Page } from '@playwright/test';

let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, route: string, data?: unknown): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await api.fetch(route, { method: data === undefined ? 'GET' : 'POST', data, headers: { 'X-AgentCore-CSRF': csrf.token } });
  expect(response.ok(), `${route}: ${response.status()}`).toBeTruthy();
  return response.json();
}
export async function prepareTenantVoiceIdentity() {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic');
    await call(api, '/api/v2/auth/login', { login: 'tenant-chat-owner@example.test', password: 'synthetic-tenant-chat-password' });
    identity = await api.storageState();
  } finally { await api.dispose(); }
}
export async function openTenantConversation(page: Page) {
  await page.context().addCookies(identity.cookies);
  const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = await call<{ tenantId: string }>(page.request, '/api/v2/system/tenants', { name: `Speech ${nonce}`, slug: `speech-${nonce}` });
  const root = `/api/v2/tenants/${tenant.tenantId}`;
  const state = await call<{ revision: number }>(page.request, root + '/access/me');
  const agent = await call<{ instance: { instanceId: string } }>(page.request, root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: state.revision });
  await prepareOrdinaryChat(page.request, tenant.tenantId, agent.instance.instanceId);
  await page.goto(`/t/${tenant.tenantId}`);
}
