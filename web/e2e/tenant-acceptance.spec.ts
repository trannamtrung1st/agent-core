import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
import { call, expireFixture, ownerLogin, ownerPassword, signIn, tenant } from './support/tenant-acceptance';

let owner: APIRequestContext;
test.beforeAll(async () => {
  owner = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  expect((await (await owner.get('/health')).json()).profile).toBe('Synthetic');
  await call(owner, '/api/v2/auth/login', { login: ownerLogin, password: ownerPassword });
});
test.afterAll(async () => { await owner?.dispose(); });

async function inspectWidths(page: Page, label: string, outputPath: (name: string) => string) {
  for (const width of [320, 390, 768, 1024, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: outputPath(`${label}-${width}.png`), fullPage: true, animations: 'disabled' });
  }
}
async function invite(target: Awaited<ReturnType<typeof tenant>>, login: string, roleId?: string) {
  if (!roleId) roleId = (await call<{ items: { roleId: string; key: string }[] }>(owner, target.prefix + '/roles')).items.find(role => role.key === 'reader')!.roleId;
  return call<{ invitationId: string; token: string }>(owner, target.prefix + '/members/invite', { login, roleId, expectedRevision: await target.revision() });
}
const invitationPath = (target: { id: string }, invitation: { invitationId: string; token: string }) => `/invitations/${invitation.invitationId}#tenant=${target.id}&token=${encodeURIComponent(invitation.token)}`;
async function openInvitation(page: Page, path: string) {
  for (let attempt = 0; attempt < 2; attempt++) {
    await page.goto(path);
    const throttled = page.getByRole('alert').filter({ hasText: 'Too many attempts' });
    await expect.poll(async () => await throttled.count() ? 'throttled'
      : await page.getByLabel('Password', { exact: true }).count() || await page.getByText(/This invitation is expired, revoked, or already accepted\./).count() ? 'ready' : 'loading').not.toBe('loading');
    if (!await throttled.count() || attempt === 1) return;
    // Purpose inspection is read-only. Reopen the original link after an explicit
    // limiter refusal; do not retry account creation or invitation acceptance.
    await new Promise(resolve => setTimeout(resolve, 60_000));
  }
}

test('unaffiliated account joins two organizations; invalid, revoked and expired invitations cannot confer membership', async ({ page }, info) => {
  const nonce = crypto.randomUUID().slice(0, 8), login = `unaffiliated-${nonce}@example.test`, password = 'synthetic-unaffiliated-password';
  const guest = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }); const errors: string[] = [], leaked: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (/[?&]token=/.test(request.url())) leaked.push(request.url()); });
  try {
    const registrationTenant = await tenant(owner, 'Initial invitation membership');
    const initial = await invite(registrationTenant, login);
    await call(guest, '/api/v2/auth/register', { login, displayName: 'Linh Nguyễn — account without an organization', password, invitation: { tenantId: registrationTenant.id, invitationId: initial.invitationId, token: initial.token } });
    const registered = (await call<{ items: { userId: string; login: string }[] }>(owner, registrationTenant.prefix + '/members')).items.find(item => item.login.toLowerCase() === login)!;
    await call(owner, `${registrationTenant.prefix}/members/${registered.userId}/status`, { status: 2, expectedRevision: await registrationTenant.revision() }, 'PUT');
    await signIn(page, login, password);
    await expect(page.getByText('You have not joined an organization yet. Ask an owner for an invitation.', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Create organization', exact: true })).toHaveCount(0);
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveCount(0);
    await inspectWidths(page, 'no-organization', name => info.outputPath(name));
    const a = await tenant(owner, 'Existing account — first organization'), b = await tenant(owner, 'Existing account — second organization');
    for (const target of [a, b]) {
      const invitation = await invite(target, login);
      await openInvitation(page, invitationPath(target, invitation));
      await expect(page.getByRole('heading', { name: 'Join organization', exact: true })).toBeVisible();
      await expect(page).toHaveURL(new RegExp(`/invitations/${invitation.invitationId}$`));
      await page.getByText('I have an account', { exact: true }).click();
      await page.getByLabel('Password', { exact: true }).fill(password);
      await page.getByRole('button', { name: 'Sign in & join', exact: true }).click();
      await expect(page).toHaveURL(new RegExp(`/t/${target.id}/admin/access$`));
      await expect(page.getByRole('button', { name: 'Create role', exact: true })).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'Invite member', exact: true })).toHaveCount(0);
    }
    await page.goto('/account/organizations');
    await expect(page.getByRole('button', { name: `Open ${a.name}`, exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: `Open ${b.name}`, exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
    await signIn(page, login, password);
    const memberships = await call<{ total: number; items: { principalId: string }[] }>(page.request, '/api/v2/account/tenants');
    expect(memberships.total).toBe(2); expect(memberships.items[0].principalId).not.toBe(memberships.items[1].principalId);
    for (const state of ['invalid', 'revoked', 'expired'] as const) {
      const rejectedLogin = `${state}-${nonce}@example.test`, invitation = await invite(a, rejectedLogin);
      if (state === 'revoked') await call(owner, `${a.prefix}/members/invitations/${invitation.invitationId}/revoke`, { expectedRevision: await a.revision() });
      if (state === 'expired') expireFixture('invitation', a.id, invitation.invitationId);
      await openInvitation(page, invitationPath(a, state === 'invalid' ? { ...invitation, token: 'invalid-purpose-token' } : invitation));
      await expect(page.getByRole('alert')).toContainText('This invitation is expired, revoked, or already accepted.');
      await expect(page.getByRole('link', { name: 'Back to sign in', exact: true })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Create account & join', exact: true })).toHaveCount(0);
      await inspectWidths(page, `${state}-invitation`, name => info.outputPath(name));
    }
    expect((await call<{ total: number }>(owner, a.prefix + '/members')).total).toBe(2);
    expect(leaked).toEqual([]); expect(errors).toEqual([]);
  } finally { await guest.dispose(); }
});

test('real organization, role and member pages retain accurate search, counts, paging and keyboard focus', async ({ page }, info) => {
  const target = await tenant(owner, 'Paged access'), nonce = crypto.randomUUID().slice(0, 8), errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  const names: string[] = [];
  for (let index = 0; index < 12; index++) names.push((await tenant(owner, `Paged organizations ${nonce} ${String(index).padStart(2, '0')} — Équipe internationale`)).name);
  for (let index = 0; index < 120; index++) await call(owner, target.prefix + '/roles', { name: `Paged role ${nonce} ${String(index).padStart(3, '0')} — Nhóm kiểm tra quyền truy cập`, permissions: ['agents.read'], expectedRevision: await target.revision() });
  const anonymous = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try {
    const reader = (await call<{ items: { roleId: string; key: string }[] }>(owner, target.prefix + '/roles')).items.find(role => role.key === 'reader')!;
    for (let index = 0; index < 21; index++) {
      const login = `paged-${nonce}-${String(index).padStart(2, '0')}@example.test`, invitation = await invite(target, login, reader.roleId);
      await call(anonymous, '/api/v2/auth/register', { login, displayName: `Paged member ${String(index).padStart(2, '0')} — Nhóm hỗ trợ quốc tế`, password: 'synthetic-paged-member-password', invitation: { tenantId: target.id, invitationId: invitation.invitationId, token: invitation.token } });
    }
  } finally { await anonymous.dispose(); }
  await signIn(page); const search = page.getByRole('textbox', { name: 'Search organizations', exact: true });
  await search.fill(`Paged organizations ${nonce}`); await expect(page.getByText('12 organizations', { exact: true })).toBeVisible();
  await page.getByTitle('2', { exact: true }).click(); await expect(page.getByRole('button', { name: /^Open Paged organizations/ })).toHaveCount(2);
  await search.fill(names[0]); await expect(page.getByText('1 organization', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: `Open ${names[0]}`, exact: true })).toBeVisible();
  await inspectWidths(page, 'organization-search', name => info.outputPath(name));
  await page.goto(`/t/${target.id}/admin/roles`); const roles = page.getByRole('textbox', { name: 'Search roles', exact: true });
  await roles.fill(`Paged role ${nonce}`); await expect(page.getByText('120 results', { exact: true })).toBeVisible();
  await page.getByTitle('2', { exact: true }).click(); await roles.fill(`Paged role ${nonce} 119`);
  await expect(page.getByText('1 result', { exact: true })).toBeVisible();
  const edit = page.getByRole('button', { name: `Edit Paged role ${nonce} 119 — Nhóm kiểm tra quyền truy cập`, exact: true });
  await edit.click(); await expect(page.getByRole('dialog', { name: 'Edit role', exact: true })).toBeVisible();
  await page.keyboard.press('Escape'); await expect(edit).toBeFocused();
  await inspectWidths(page, 'role-search', name => info.outputPath(name));
  await page.goto(`/t/${target.id}/admin/members`); await expect(page.getByText('22 members', { exact: true })).toBeVisible();
  await page.getByTitle('3', { exact: true }).click(); await expect(page.getByRole('button', { name: /^Manage / })).toHaveCount(2);
  const members = page.getByRole('textbox', { name: 'Search members', exact: true }); await members.fill('Paged member 20');
  await expect(page.getByText('1 member', { exact: true })).toBeVisible(); await expect(page.getByText('Paged member 20 — Nhóm hỗ trợ quốc tế', { exact: true })).toBeVisible();
  await members.fill('No matching account in this organization'); await expect(page.getByText('No members to show.', { exact: true })).toBeVisible();
  await members.fill('Paged member 20'); await inspectWidths(page, 'member-search', name => info.outputPath(name));
  expect(errors).toEqual([]);
});

test('expired real approval cannot act, and a new independent message can request and complete fresh review', async ({ page, browser }, info) => {
  const target = await tenant(owner, 'Expiry review'), reviewerLogin = `expiry-reviewer-${crypto.randomUUID().slice(0, 8)}@example.test`, reviewerPassword = 'synthetic-expiry-reviewer-password';
  const draft = await call<{ draftId: string; definitionId: string; candidate: Record<string, unknown> }>(owner, target.prefix + '/agent-definitions', { name: 'Expiry review assistant', expectedRevision: await target.revision() });
  const before = 'Original expiry verification instructions.', after = 'Fresh reviewed instructions after an expired exact request.';
  draft.candidate.agentInstructions = before;
  draft.candidate.executionPolicy = { harness: [], knowledgeSources: [], toolAllowlist: [], capabilities: { mode: 'Selected', resolvedCapabilities: ['management.inspect', 'settings.inspect', 'settings.patch'] }, projection: { alwaysCapabilities: [] } };
  draft.candidate.selfManagement = { mode: 'Managed', scopes: ['Settings'], sources: [], eligibleTools: [] };
  const changed = await call<{ revision: number }>(owner, `${target.prefix}/definition-drafts/${draft.draftId}`, { candidate: draft.candidate, expectedRevision: await target.revision(), expectedDraftRevision: 1 }, 'PUT');
  await call(owner, `${target.prefix}/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await target.revision(), expectedDraftRevision: changed.revision });
  const agent = await call<{ instance: { instanceId: string }; principalId: string }>(owner, target.prefix + '/agents/from-publication', { definitionId: draft.definitionId, version: 1, expectedRevision: await target.revision() });
  const executorRole = await call<{ roleId: string }>(owner, target.prefix + '/roles', { name: 'Expiry executor', permissions: ['agents.chat', 'sessions.read', 'agents.read', 'agents.update'], expectedRevision: await target.revision() });
  await call(owner, target.prefix + '/role-assignments', { principalId: agent.principalId, roleId: executorRole.roleId, expectedRevision: await target.revision() });
  const reviewerRole = await call<{ roleId: string }>(owner, target.prefix + '/roles', { name: 'Expiry independent reviewer', permissions: ['agents.read', 'agents.update', 'sessions.read', 'approvals.read', 'approvals.approve'], expectedRevision: await target.revision() });
  const invitation = await invite(target, reviewerLogin, reviewerRole.roleId), anonymous = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  try { await call(anonymous, '/api/v2/auth/register', { login: reviewerLogin, displayName: 'Expiry independent reviewer', password: reviewerPassword, invitation: { tenantId: target.id, invitationId: invitation.invitationId, token: invitation.token } }); }
  finally { await anonymous.dispose(); }
  const policy = await call<{ securityRevision: number; policyRevision: number }>(owner, target.prefix + '/operation-policies/agents.update');
  await call(owner, target.prefix + '/operation-policies/agents.update', { mode: 'always', independentReview: true, expectedRevision: policy.securityRevision, expectedPolicyRevision: policy.policyRevision }, 'PUT');
  const reviewerContext = await browser.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL }), reviewer = await reviewerContext.newPage();
  const errors: string[] = [], decisions: string[] = [];
  for (const current of [page, reviewer]) current.on('pageerror', error => errors.push(error.message));
  reviewer.on('request', request => { if (request.method() === 'POST' && /\/decision$/.test(new URL(request.url()).pathname)) decisions.push(request.url()); });
  try {
    await signIn(page); await page.goto(`/t/${target.id}`);
    const command = 'synthetic-tenant-management-mutation: ' + JSON.stringify({ tool: 'settings.patch', arguments: { expectedInstanceRevision: 1, section: 'instructions', set: { agentInstructions: after }, clear: [] } });
    const send = async () => { await page.getByRole('textbox', { name: 'Message', exact: true }).fill(command); await page.getByRole('button', { name: 'Send', exact: true }).click(); };
    await send();
    let pending: { requestId: string; rootActivationId: string } | undefined;
    await expect.poll(async () => { pending = (await call<{ items: { requestId: string; rootActivationId: string }[] }>(owner, target.prefix + '/approvals?status=pending')).items[0]; return !!pending; }).toBe(true);
    const original = pending!;
    await signIn(reviewer, reviewerLogin, reviewerPassword);
    expireFixture('approval', target.id, original.requestId, 3500);
    await reviewer.goto(`/t/${target.id}/admin/approvals/${original.requestId}`);
    const detail = reviewer.getByRole('dialog', { name: 'Approval details', exact: true });
    await expect(detail.getByRole('button', { name: 'Approve', exact: true })).toBeDisabled();
    await expect.poll(async () => (await call<{ status: string }>(owner, `${target.prefix}/approvals/${original.requestId}`)).status).toBe('expired');
    await detail.getByRole('button', { name: 'Refresh details', exact: true }).click();
    await expect(detail.getByText('Request expired', { exact: true })).toBeVisible();
    await expect(detail.getByText('This request has expired. Ask the agent to prepare a new action.', { exact: true })).toBeVisible();
    await expect(detail.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0);
    await inspectWidths(reviewer, 'expired-approval', name => info.outputPath(name));
    expect(decisions).toEqual([]);
    const expired = await call<{ status: string; requestRevision: number; preparedAction: { actionHash: string } }>(owner, `${target.prefix}/approvals/${original.requestId}`);
    expect(expired.status).toBe('expired');
    const csrf = await call<{ token: string }>(reviewer.request, '/api/v2/auth/csrf');
    const refused = await reviewer.request.post(`${target.prefix}/approvals/${original.requestId}/decision`, { headers: { 'X-AgentCore-CSRF': csrf.token }, data: { decision: 'approve', actionHash: expired.preparedAction.actionHash, expectedRevision: await target.revision(), expectedRequestRevision: expired.requestRevision } });
    expect(refused.status()).toBe(409); expect((await refused.json()).reasonCode).toBe('stale_revision');
    expect((await call<{ instance: { revision: number } }>(owner, `${target.prefix}/agents/${agent.instance.instanceId}`)).instance.revision).toBe(1);
    await page.reload(); await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeVisible(); await send();
    await expect.poll(async () => { pending = (await call<{ items: { requestId: string; rootActivationId: string }[] }>(owner, target.prefix + '/approvals?status=pending')).items.find(item => item.requestId !== original.requestId); return !!pending; }).toBe(true);
    expect(pending!.rootActivationId).not.toBe(original.rootActivationId);
    await reviewer.goto(`/t/${target.id}/admin/approvals/${pending!.requestId}`);
    await detail.getByRole('button', { name: 'Approve', exact: true }).click();
    await reviewer.getByRole('dialog', { name: 'Approve?', exact: true }).getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(detail.getByText('Approved decision recorded.', { exact: true })).toBeVisible();
    await expect.poll(async () => (await call<{ instance: { revision: number } }>(owner, `${target.prefix}/agents/${agent.instance.instanceId}`)).instance.revision).toBe(2);
    const saved = await call<{ items: { section: string; effective: { agentInstructions?: string } }[] }>(owner, `${target.prefix}/agents/${agent.instance.instanceId}/settings`);
    expect(saved.items.find(item => item.section === 'instructions')!.effective.agentInstructions).toBe(after);
    expect(decisions).toHaveLength(1); expect(errors).toEqual([]);
  } finally { await reviewerContext.close(); }
});
