import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

function authBackoff() {
  // Combined suites share the production IP limiter. Extend only the current
  // test/hook on an explicit rejection; do not disable or weaken that limiter.
  test.setTimeout(test.info().timeout + 65_000);
  return new Promise(resolve => setTimeout(resolve, 60_000));
}
export async function canonicalFetch(api: APIRequestContext, path: string, options: Parameters<APIRequestContext['fetch']>[1]) {
  let response = await api.fetch(path, options);
  if (response.status() === 429 && options?.method === 'POST' && ['/api/v2/auth/login', '/api/v2/auth/register'].includes(path)) {
    await authBackoff(); response = await api.fetch(path, options);
  }
  // Current workers may commit between a projection's two authority checks.
  // Obtain a new snapshot only after the explicit read conflict. Never replay writes.
  for (let retry = 0; (options?.method ?? 'GET') === 'GET' && response.status() === 409 && retry < 2; retry++) {
    if ((await response.json()).reasonCode !== 'stale_revision') break;
    response = await api.fetch(path, options);
  }
  return response;
}

// Space membership supplies context, never IAM. Conversational fixtures explicitly
// grant their executor read access to its exact protected Private Space.
export async function grantPrivateSpaceRead(api: APIRequestContext, tenant: string, instance: string, principalId?: string) {
  const root = `/api/v2/tenants/${tenant}`;
  async function call(path: string, data?: unknown) {
    const csrf = await (await api.get('/api/v2/auth/csrf')).json();
    const response = await canonicalFetch(api, path, { method: data === undefined ? 'GET' : 'POST', headers: { 'X-AgentCore-CSRF': csrf.token }, data });
    expect(response.ok(), `${path}: ${response.status()}`).toBeTruthy(); return response.json();
  }
  const agent = await call(root + `/agents/${instance}`), space = await call(root + `/agents/${instance}/private-space`);
  const revision = async () => (await call(root + '/access/me')).revision;
  const principal = principalId ?? agent.principalId;
  const role = await call(root + '/roles', { name: `Read exact context ${principal}`, permissions: ['spaces.read'], expectedRevision: await revision() });
  await call(root + '/role-assignments', { principalId: principal, roleId: role.roleId, resourceType: 'space', resourceId: space.spaceId, expectedRevision: await revision() });
}
export async function signInTenant(page: Page, login: string, password: string) {
  for (let attempt = 0; attempt < 2; attempt++) {
    // A deliberate fresh form/CSRF after an explicit limiter refusal matches the
    // existing acceptance journey and releases the previous submit lifecycle.
    await page.goto('/sign-in');
    await page.getByLabel('Email', { exact: true }).fill(login);
    await page.getByLabel('Password', { exact: true }).fill(password);
    const pending = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v2/auth/login' && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    if ((await pending).status() !== 429 || attempt === 1) break;
    await expect(page.getByRole('alert')).toContainText('Too many attempts');
    await authBackoff();
  }
  await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}
