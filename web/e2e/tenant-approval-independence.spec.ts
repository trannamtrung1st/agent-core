import { captureExperience, signOutFromChat } from './support/experience-navigation';
import { prepareOrdinaryChat } from './support/tenant-auth';
import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';

const login = process.env.TENANT_MEMORY_LOGIN ?? 'tenant-chat-owner@example.test';
const password = process.env.TENANT_MEMORY_PASSWORD ?? 'synthetic-tenant-chat-password';

async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST', rateRetry = true): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await api.fetch(path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  // Combined browser runs share the real IP-scoped, one-minute login limiter.
  // Retry only an explicit auth rejection; resource commands are never replayed.
  if (response.status() === 429 && rateRetry && ['/api/v2/auth/login', '/api/v2/auth/register'].includes(path)) {
    await new Promise(resolve => setTimeout(resolve, 60000));
    return call<T>(api, path, data, method, false);
  }
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy();
  return response.status() === 204 ? undefined as T : response.json();
}

async function signIn(page: Page, email: string, secret: string) {
  for (let attempt = 0; attempt < 2; attempt++) {
    await page.goto('/sign-in');
    await page.getByLabel('Email', { exact: true }).fill(email);
    await page.getByLabel('Password', { exact: true }).fill(secret);
    const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v2/auth/login' && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    if ((await response).status() !== 429 || attempt === 1) break;
    await expect(page.getByRole('alert')).toContainText('Too many attempts');
    await new Promise(resolve => setTimeout(resolve, 60000));
  }
  await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}

for (const exceptional of [false, true]) for (const decision of ['approve', 'reject'] as const)
  test(`a different person decides ${exceptional ? 'Authorize once' : 'Review'} after requester logout: ${decision}`, async ({ page, browser }, info) => {
    test.setTimeout(240000);
    const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
    const reviewerContext = await browser.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
    const reviewer = await reviewerContext.newPage();
    const errors: string[] = [], obsolete: string[] = [];
    for (const target of [page, reviewer]) {
      target.on('pageerror', error => errors.push(error.message));
      target.on('request', request => {
        const path = new URL(request.url()).pathname;
        if (/^\/api\/v[12]\/(owner-capability|agent-instances|admin)(\/|$)/.test(path)) obsolete.push(path);
      });
    }
    try {
      expect((await (await api.get('/health')).json()).profile).toBe('Synthetic');
      await call(api, '/api/v2/auth/login', { login, password });
      const nonce = crypto.randomUUID().slice(0, 8);
      const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Independent review ${nonce}`, slug: `independent-review-${nonce}` })).tenantId;
      const prefix = `/api/v2/tenants/${tenant}`;
      const revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
      const draft = await call<{ draftId: string; definitionId: string; candidate: Record<string, unknown> }>(api, prefix + '/agent-definitions', { name: 'Independent review assistant', expectedRevision: await revision() });
      const before = 'Original saved instructions.';
      const after = 'Reviewed literal <script>configuration</script> with a long exact target description.';
      draft.candidate.agentInstructions = before;
      draft.candidate.executionPolicy = { harness: [], knowledgeSources: [], toolAllowlist: [], capabilities: { mode: 'Selected', resolvedCapabilities: ['management.inspect', 'settings.inspect', 'settings.patch'] }, projection: { alwaysCapabilities: [] } };
      draft.candidate.selfManagement = { mode: 'Managed', scopes: ['Settings'], sources: [], eligibleTools: [] };
      const changed = await call<{ revision: number }>(api, prefix + `/definition-drafts/${draft.draftId}`, { candidate: draft.candidate, expectedRevision: await revision(), expectedDraftRevision: 1 }, 'PUT');
      await call(api, prefix + `/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: changed.revision });
      const agent = await call<{ instance: { instanceId: string }; principalId: string }>(api, prefix + '/agents/from-publication', { definitionId: draft.definitionId, version: 1, expectedRevision: await revision() });
      await prepareOrdinaryChat(api, tenant, agent.instance.instanceId);
      const executorRole = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Actual executor', permissions: ['agents.chat', 'sessions.read', 'agents.read', ...(exceptional ? [] : ['agents.update'])], expectedRevision: await revision() });
      await call(api, prefix + '/role-assignments', { principalId: agent.principalId, roleId: executorRole.roleId, expectedRevision: await revision() });
      const reviewerRole = await call<{ roleId: string }>(api, prefix + '/roles', { name: 'Independent qualified reviewer', permissions: ['agents.read', 'agents.update', 'sessions.read', 'approvals.read', 'approvals.approve', 'approvals.grant_exception'], expectedRevision: await revision() });
      const reviewerLogin = `reviewer-${nonce}@example.test`, reviewerPassword = 'synthetic-independent-review-password';
      const invitation = await call<{ invitationId: string; token: string }>(api, prefix + '/members/invite', { login: reviewerLogin, roleId: reviewerRole.roleId, expectedRevision: await revision() });
      const publicApi = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
      try {
        await call(publicApi, '/api/v2/auth/register', { login: reviewerLogin, displayName: `Independent reviewer ${nonce}`, password: reviewerPassword, invitation: { tenantId: tenant, invitationId: invitation.invitationId, token: invitation.token } });
      } finally { await publicApi.dispose(); }
      const person = (await call<{ items: { principalId: string; displayName: string }[] }>(api, prefix + '/members')).items.find(item => item.displayName === `Independent reviewer ${nonce}`)!;
      const policy = await call<{ securityRevision: number; policyRevision: number }>(api, prefix + '/operation-policies/agents.update');
      await call(api, prefix + '/operation-policies/agents.update', { mode: 'always', independentReview: true, expectedRevision: policy.securityRevision, expectedPolicyRevision: policy.policyRevision }, 'PUT');

      await signIn(page, login, password); await page.goto(`/t/${tenant}`);
      const input = page.getByRole('textbox', { name: 'Message', exact: true });
      await expect(input).toBeEnabled();
      await input.fill('synthetic-tenant-management-mutation: ' + JSON.stringify({ tool: 'settings.patch', arguments: { expectedInstanceRevision: 1, section: 'instructions', set: { agentInstructions: after }, clear: [] } }));
      await page.getByRole('button', { name: 'Send', exact: true }).click();
      const conversationApprovals = page.getByRole('region', { name: 'Conversation approvals', exact: true });
      await conversationApprovals.getByRole('link', { name: 'Review details', exact: true }).click();
      const ownDetail = page.getByRole('dialog', { name: 'Approval details', exact: true });
      const action = exceptional ? 'Authorize once' : 'Approve';
      await expect(ownDetail.getByText('Independent review is required. The original requester cannot decide.', { exact: true })).toBeVisible();
      await expect(ownDetail.getByRole('button', { name: action, exact: true })).toBeDisabled();
      await ownDetail.getByRole('button', { name: 'Close', exact: true }).click();
      const pending = await call<{ items: { requestId: string; summary: string; kind: string }[] }>(api, prefix + '/approvals?status=pending');
      const request = pending.items[0]; expect(pending.items).toHaveLength(1);
      expect(request.kind).toBe(exceptional ? 'exceptionalGrant' : 'review');
      await signOutFromChat(page);
      await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();

      await signIn(reviewer, reviewerLogin, reviewerPassword);
      await reviewer.goto(`/t/${tenant}/admin/approvals`);
      await reviewer.getByRole('link', { name: request.summary, exact: true }).click();
      const detail = reviewer.getByRole('dialog', { name: 'Approval details', exact: true });
      await expect(detail.getByRole('region', { name: 'Exact saved changes', exact: true })).toContainText(after);
      await expect(detail.locator('script')).toHaveCount(0);
      for (const width of [320, 390, 768, 1024, 1440]) {
        await reviewer.setViewportSize({ width, height: 900 });
        await expect(detail.getByRole('button', { name: action, exact: true })).toBeEnabled();
        await expect(detail.getByRole('button', { name: action, exact: true })).toBeInViewport();
        expect(await reviewer.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await reviewer.screenshot({ path: info.outputPath(`independent-${exceptional ? 'exception' : 'review'}-${decision}-${width}.png`), fullPage: true, animations: 'disabled' });
      }
      await captureExperience(reviewer, `approval-${exceptional ? 'exception' : 'review'}-${decision}`);
      const decide = decision === 'approve' ? action : 'Reject';
      await detail.getByRole('button', { name: decide, exact: true }).click();
      const confirmation = reviewer.getByRole('dialog', { name: `${decide}?`, exact: true });
      await confirmation.getByRole('button', { name: decide, exact: true }).click();
      await expect(detail.getByText(`${decision === 'approve' ? 'Approved' : 'Rejected'} decision recorded.`, { exact: true })).toBeVisible();
      await expect.poll(async () => {
        const path = prefix + `/agents/${agent.instance.instanceId}`;
        const response = await api.get(path);
        // An approval effect can change the security/Instance witness during this read.
        // Only that explicit read conflict stays inside the existing bounded poll.
        if (response.status() === 409 && (await response.json()).reasonCode === 'stale_revision') return null;
        expect(response.ok(), `GET ${path}: ${response.status()} ${await response.text()}`).toBeTruthy();
        return ((await response.json()) as { instance: { revision: number } }).instance.revision;
      }).toBe(decision === 'approve' ? 2 : 1);
      const saved = await call<{ items: { section: string; effective: { agentInstructions?: string } }[] }>(api, prefix + `/agents/${agent.instance.instanceId}/settings`);
      expect(saved.items.find(item => item.section === 'instructions')!.effective.agentInstructions).toBe(decision === 'approve' ? after : before);
      const decided = await call<{ decision: { principalId: string }; status: string }>(api, prefix + `/approvals/${request.requestId}`);
      expect(decided.decision.principalId).toBe(person.principalId); expect(decided.status).toBe(decision === 'approve' ? 'approved' : 'rejected');
      expect((await call<{ allowed: boolean }>(api, prefix + '/access/check', { principalId: agent.principalId, permission: 'agents.update', resourceType: 'agent', resourceId: agent.instance.instanceId })).allowed).toBe(!exceptional);
      await reviewer.reload();
      await expect(reviewer.getByRole('dialog', { name: 'Approval details', exact: true }).getByRole('button', { name: action, exact: true })).toHaveCount(0);
      expect(errors).toEqual([]); expect(obsolete).toEqual([]);
    } finally { await reviewerContext.close(); await api.dispose(); }
  });
