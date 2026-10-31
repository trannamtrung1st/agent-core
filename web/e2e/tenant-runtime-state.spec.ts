import { test, expect, request as requests, type APIRequestContext, type Page } from '@playwright/test';
import fs from 'node:fs';
import { grantPrivateSpaceRead, canonicalFetch, signInTenant } from './support/tenant-auth';
import path from 'node:path';
const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, route: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const response = await canonicalFetch(api, route, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${method} ${route}: ${response.status()} ${await response.text()}`).toBeTruthy(); return response.status() === 204 ? undefined as T : response.json();
}
async function signIn(page: Page, email = login, secret = password) { await signInTenant(page, email, secret); }
test('Runtime State refresh, expiry, exact reader scope and revocation work on native Synthetic at desktop and narrow widths', async ({ page }) => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); const errors: string[] = [], obsolete: string[] = [], failures: string[] = [], consoleErrors: string[] = [];
  page.on('pageerror', e => errors.push(e.message)); page.on('request', request => { if (/^\/api\/v[12]\/(owner-capability|agent-instances|admin)(\/|$)/.test(new URL(request.url()).pathname)) obsolete.push(request.url()); });
  page.on('requestfailed', request => { if (!(request.method() === 'GET' && request.failure()?.errorText === 'net::ERR_ABORTED')) failures.push(`${new URL(request.url()).pathname}: ${request.failure()?.errorText}`); });
  page.on('console', message => { if (message.type() === 'error' && !/Failed to load resource: the server responded with a status of 403/.test(message.text())) consoleErrors.push(message.text()); });
  const captures = process.env.TENANT_RUNTIME_STATE_CAPTURE_ROOT ?? path.resolve('../.impeccable/review/tenant-runtime-state'); fs.mkdirSync(captures, { recursive: true });
  try {
    await call(api, '/api/v2/auth/login', { login, password }); const nonce = crypto.randomUUID().slice(0, 8);
    const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `State ${nonce}`, slug: `state-${nonce}` })).tenantId, root = `/api/v2/tenants/${tenant}`;
    const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
    const agent = await call<{ principalId: string; instance: { instanceId: string } }>(api, root + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() }); const id = agent.instance.instanceId;
    await grantPrivateSpaceRead(api, tenant, id);
    const name = 'Runtime observation — readable agent';
    await call(api, root + `/agents/${id}/persona`, { persona: { name, role: 'Guide', description: 'Observe only readable runtime facts', tone: 'Clear' }, expectedRevision: await revision(), expectedInstanceRevision: 1, expectedPersonaRevision: 1 }, 'PUT');
    const executor = await call<{ roleId: string }>(api, root + '/roles', { name: 'Runtime text executor', permissions: ['agents.chat'], expectedRevision: await revision() });
    await call(api, root + '/role-assignments', { principalId: agent.principalId, roleId: executor.roleId, expectedRevision: await revision() });
    const a = await call<{ sessionId: string }>(api, root + '/sessions', { retentionKind: "saved", agentInstanceId: id, expectedInstanceRevision: 2, expectedRevision: await revision() });
    await call(api, root + '/sessions', { retentionKind: "saved", agentInstanceId: id, expectedInstanceRevision: 2, expectedRevision: await revision() });
    const admitted = await call<{ runId: string }>(api, root + `/sessions/${a.sessionId}/messages`, { messageId: crypto.randomUUID(), text: 'Hello from the State observation fixture', expectedSessionRevision: 1, expectedRevision: await revision() });
    await expect.poll(async () => (await call<{ status: string }>(api, root + `/agent-runs/${admitted.runId}`)).status).toBe('completed');
    const route = `/t/${tenant}/admin/agents/${id}/state`;
    await page.setViewportSize({ width: 1440, height: 1100 }); await signIn(page); await page.goto(route);
    const sessions = page.getByRole('region', { name: 'Visible Session state', exact: true }); const runs = page.getByRole('region', { name: 'Visible Run state', exact: true });
    await expect(sessions).toContainText(/Open Sessions\s*2/); await expect(runs).toContainText(/Terminal\s*1/);
    await expect(page.getByRole('region', { name: 'Readable Browser state', exact: true }).getByText('Unknown', { exact: true })).toHaveCount(2);
    const before = await revision(); await page.getByRole('button', { name: 'Refresh state', exact: true }).click(); await expect(sessions).toBeVisible(); expect(await revision()).toBe(before);
    await page.screenshot({ path: path.join(captures, 'desktop.png'), fullPage: true, animations: 'disabled' });
    await page.clock.install(); await page.clock.fastForward(16000); await expect(page.getByText('This observation expired', { exact: true })).toBeVisible(); await expect(sessions).toHaveCount(0);
    await page.clock.setSystemTime(new Date()); await page.getByRole('button', { name: 'Refresh state', exact: true }).click(); await expect(sessions).toBeVisible();
    const email = `state-reader-${nonce}@example.test`, secret = 'synthetic-state-reader-password';
    const membership = await call<{ roleId: string }>(api, root + '/roles', { name: 'State membership only', permissions: ['agents.chat'], expectedRevision: await revision() });
    const invitation = await call<{ invitationId: string; token: string }>(api, root + '/members/invite', { login: email, roleId: membership.roleId, expectedRevision: await revision() });
    await call(page.request, '/api/v2/auth/register', { login: email, displayName: 'State reader', password: secret, invitation: { tenantId: tenant, invitationId: invitation.invitationId, token: invitation.token } });
    const principal = (await call<{ items: { login: string; principalId: string }[] }>(api, root + '/members')).items.find(item => item.login.toLowerCase() === email)!.principalId;
    const grant = async (label: string, permissions: string[], resourceType: string, resourceId: string) => { const role = await call<{ roleId: string }>(api, root + '/roles', { name: label, permissions, expectedRevision: await revision() }); return call<{ assignmentId: string }>(api, root + '/role-assignments', { principalId: principal, roleId: role.roleId, resourceType, resourceId, expectedRevision: await revision() }); };
    const access = await grant('Read exact State agent', ['agents.read', 'browser.read'], 'agent', id); await grant('Read exact State Session', ['sessions.read'], 'session', a.sessionId);
    await page.setViewportSize({ width: 320, height: 844 }); await signIn(page, email, secret); await page.goto(route);
    await expect(sessions).toContainText(/Open Sessions\s*1/); await expect(runs.getByText('Unknown', { exact: true })).toHaveCount(6);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); await page.screenshot({ path: path.join(captures, 'mobile.png'), fullPage: true, animations: 'disabled' });
    await runs.scrollIntoViewIfNeeded(); await page.screenshot({ path: path.join(captures, 'restricted-runs-320.png'), fullPage: true, animations: 'disabled' });
    await call(api, root + `/role-assignments/${access.assignmentId}?expectedRevision=${await revision()}`, undefined, 'DELETE');
    await page.setViewportSize({ width: 390, height: 844 }); await page.getByRole('button', { name: 'Refresh state', exact: true }).click(); await expect(page.getByRole('alert')).toBeVisible(); await expect(page.getByRole('heading', { name, exact: true })).toHaveCount(0); await expect(sessions).toHaveCount(0);
    await page.screenshot({ path: path.join(captures, 'revoked-390.png'), fullPage: true, animations: 'disabled' });
    expect(errors).toEqual([]); expect(obsolete).toEqual([]); expect(failures).toEqual([]); expect(consoleErrors).toEqual([]);
  } finally { await api.dispose(); }
});
