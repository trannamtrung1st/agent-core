import { expect, type APIRequestContext, type Page } from '@playwright/test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';

export const ownerLogin = 'acceptance-owner@example.test';
export const ownerPassword = 'synthetic-acceptance-owner-password';
export async function call<T>(api: APIRequestContext, route: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  for (let attempt = 0; ; attempt++) {
    const csrf = await (await api.get('/api/v2/auth/csrf')).json();
    const response = await api.fetch(route, { method, data, headers: { 'X-AgentCore-CSRF': csrf.token } });
    if (response.status() === 429 && attempt === 0 && ['/api/v2/auth/login', '/api/v2/auth/register'].includes(route)) {
      await new Promise(resolve => setTimeout(resolve, 60_000)); continue;
    }
    if (method === 'GET' && response.status() === 409 && attempt < 2 && (await response.json()).reasonCode === 'stale_revision') continue;
    expect(response.ok(), `${method} ${route}: ${response.status()} ${await response.text()}`).toBeTruthy();
    return response.status() === 204 ? undefined as T : response.json();
  }
}
export async function signIn(page: Page, login = ownerLogin, password = ownerPassword) {
  for (let attempt = 0; attempt < 2; attempt++) {
    await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password);
    const result = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v2/auth/login' && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    if ((await result).status() !== 429 || attempt === 1) break;
    await expect(page.getByRole('alert')).toContainText('Too many attempts'); await new Promise(resolve => setTimeout(resolve, 60_000));
  }
  await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}
export async function tenant(api: APIRequestContext, label: string) {
  const nonce = crypto.randomUUID().slice(0, 8), name = `${label} ${nonce}`;
  const created = await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name, slug: `acceptance-${nonce}` });
  const prefix = `/api/v2/tenants/${created.tenantId}`;
  return { id: created.tenantId, name, prefix, revision: async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision };
}
export function expireFixture(kind: 'invitation' | 'approval', tenantId: string, id: string, remainingMs = -1000) {
  // Fixture-only expiry preparation; never accept a database/path from a user or API.
  const root = process.env.TENANT_ACCEPTANCE_ROOT;
  if (!root || !path.basename(root).startsWith('agent-core-tenant-acceptance-') || !fs.realpathSync(root).startsWith(fs.realpathSync(os.tmpdir()) + path.sep)) throw new Error('A private acceptance fixture is required.');
  const marker = JSON.parse(fs.readFileSync(path.join(root, 'fixture.json'), 'utf8'));
  if (marker.kind !== 'tenant-acceptance' || marker.web !== process.env.TENANT_CHAT_TEST_BASE_URL) throw new Error('Fixture marker mismatch.');
  const database = path.join(root, 'store.db');
  if (fs.lstatSync(database).isSymbolicLink()) throw new Error('Fixture database must not be a link.');
  const db = new DatabaseSync(database); const expiry = Date.now() + remainingMs;
  try {
    db.exec('PRAGMA busy_timeout=5000; BEGIN IMMEDIATE');
    if (kind === 'invitation') {
      const result = db.prepare('UPDATE TenantInvitations SET ExpiresAt=? WHERE lower(TenantId)=lower(?) AND lower(InvitationId)=lower(?) AND AcceptedAt IS NULL AND Revoked=0').run(expiry, tenantId, id);
      expect(result.changes).toBe(1);
    } else {
      const result = db.prepare("UPDATE TenantApprovalRequests SET ExpiresAt=?, PayloadJson=json_set(PayloadJson,'$.ExpiresAt',?) WHERE lower(TenantId)=lower(?) AND lower(RequestId)=lower(?) AND Status=0").run(expiry, new Date(expiry).toISOString(), tenantId, id);
      expect(result.changes).toBe(1);
    }
    db.exec('COMMIT');
  } catch (error) { db.exec('ROLLBACK'); throw error; }
  finally { db.close(); }
}
