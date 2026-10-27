import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { canonicalFetch, grantPrivateSpaceRead, signInTenant } from './support/tenant-auth';
async function request(api: APIRequestContext, path: string, data?: unknown) {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  return canonicalFetch(api, path, { method: data === undefined ? 'GET' : 'POST', headers: { 'X-AgentCore-CSRF': csrf.token }, data });
}
async function call<T>(api: APIRequestContext, path: string, data?: unknown): Promise<T> {
  const response = await request(api, path, data); expect(response.ok(), await response.text()).toBe(true); return response.json();
}
async function openCheckpoint(page: Page, title: string) {
  const heading = page.getByRole('button', { name: new RegExp(`^\\d+\\. ${title} ·`) });
  if (await heading.getAttribute('aria-expanded') !== 'true') await heading.click();
  return page.locator('.ant-collapse-item').filter({ has: heading });
}
test('Goal stabilization preserves progress and verified proof and protects optional prerequisites', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await signInTenant(page, 'goals-owner@example.test', 'synthetic-goals-password'); const api = page.request; const nonce = crypto.randomUUID().slice(0, 8);
  const tenant = await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Goal stabilization ${nonce}`, slug: `goal-stabilization-${nonce}` }); const root = `/api/v2/tenants/${tenant.tenantId}`;
  const revision = async () => (await call<{ revision: number }>(api, `${root}/access/me`)).revision;
  const agent = await call<{ principalId: string; instance: { instanceId: string; revision: number } }>(api, `${root}/agents`, { templateId: 'examiner', version: 1, expectedRevision: await revision() });
  await grantPrivateSpaceRead(api, tenant.tenantId, agent.instance.instanceId);
  const role = await call<{ roleId: string }>(api, `${root}/roles`, { name: 'Goal executor', permissions: ['agents.read','agents.chat','sessions.read','agent-runs.read','agent-runs.create','goals.read','goals.manage'], expectedRevision: await revision() });
  await call(api, `${root}/role-assignments`, { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
  const suite = await call<{ suiteId: string; version: number; contentHash: string }>(api, `${root}/evaluation-suites`, { name: 'Accepted Run proof', checks: [{ checkId: 'complete', title: 'Run completed', kind: 0, required: true, predicate: 5 }], expectedVersion: 0, expectedRevision: await revision() });
  const chat = await call<{ sessionId: string }>(api, `${root}/sessions`, { agentInstanceId: agent.instance.instanceId, mode: 'text', retentionKind: 'saved', expectedRevision: await revision(), expectedInstanceRevision: agent.instance.revision });
  const endpoint = `${root}/sessions/${chat.sessionId}/goals`; const id = crypto.randomUUID();
  const cp = (title: string, required: boolean, dependencies: string[] = [], verification: unknown[] = [{ kind: 2 }]) => ({ checkpointId: crypto.randomUUID(), title, expectedOutcome: `${title} meets the saved criterion`, required, dependencies, verification });
  const first = cp('Collect inputs', false); const middle = cp('Review inputs', false, [first.checkpointId]); const proof = cp('Deliver checked result', true, [middle.checkpointId], [{ kind: 0, suiteId: suite.suiteId, suiteVersion: suite.version, suiteHash: suite.contentHash }]);
  const final = cp('Review final result', true, [proof.checkpointId]); const independent = cp('Optional appendix', false);
  await call(api, endpoint, { goalId: id, command: { kind: 0, requestKey: `create-${nonce}`, expectedSecurityRevision: await revision(), expectedGoalRevision: 0, expectedPlanRevision: 0, plan: { title: 'Synthetic stabilization proof', objective: 'Deliver with protected prerequisites and immutable evidence', successCriteria: 'Each required outcome is verified', checkpoints: [first,middle,proof,final,independent], requireFinalAcceptance: true, autoContinuation: { enabled: false, maxRuns: 0, until: null } } } });
  type Goal = { revision: number; planRevision: number; checkpoints: { checkpointId: string; status: number; sourceRunId: string | null; evidence: unknown[] | null; note: string | null }[] };
  const goal = () => call<Goal>(api, `${endpoint}/${id}`);
  await page.goto(`/t/${tenant.tenantId}/c/${chat.sessionId}`); await page.getByRole('button', { name: 'Session goals', exact: true }).click();
  await page.getByRole('button', { name: 'Start goal', exact: true }).click(); await page.getByRole('button', { name: 'Accept plan', exact: true }).click();
  let panel = await openCheckpoint(page, 'Collect inputs'); await panel.getByRole('button', { name: 'Report or verify checkpoint', exact: true }).click();
  await panel.getByRole('textbox', { name: 'Progress or acceptance reason', exact: true }).fill('Waiting for the missing source input.'); await panel.getByRole('button', { name: 'Report blocker', exact: true }).click();
  await expect(page.getByRole('button', { name: /Collect inputs · Blocked/ })).toBeVisible();
  const beforeDecision = await goal();
  for (const action of ['accept','reject']) {
    const response = await request(api, `${endpoint}/${id}/plan-decision`, { kind: 2, requestKey: crypto.randomUUID(), expectedSecurityRevision: await revision(), expectedGoalRevision: beforeDecision.revision, expectedPlanRevision: beforeDecision.planRevision, action });
    expect(response.status()).toBe(409); expect(await goal()).toEqual(beforeDecision);
  }
  await page.getByRole('button', { name: 'Refresh goals', exact: true }).click(); await expect(panel.locator('.ant-typography').filter({ hasText: /^Waiting for the missing source input\.$/ })).toBeVisible();
  await panel.getByRole('button', { name: 'Report or verify checkpoint', exact: true }).click(); await panel.getByRole('textbox', { name: 'Progress or acceptance reason', exact: true }).fill('The prerequisite cannot be waived.');
  await expect(panel.getByRole('button', { name: 'Skip optional checkpoint', exact: true })).toBeDisabled(); await expect(panel.getByText(/Cannot skip: an unfinished required outcome/)).toBeVisible();
  await page.getByRole('button', { name: /Collect inputs · Blocked/ }).click(); panel = await openCheckpoint(page, 'Optional appendix'); await panel.getByRole('button', { name: 'Report or verify checkpoint', exact: true }).click();
  await panel.getByRole('textbox', { name: 'Progress or acceptance reason', exact: true }).fill('Appendix is independent of the required result.'); await panel.getByRole('button', { name: 'Skip optional checkpoint', exact: true }).click(); await expect(page.getByRole('button', { name: /Optional appendix · Skipped/ })).toBeVisible();
  await page.getByRole('button', { name: /Optional appendix · Skipped/ }).click();
  for (const title of ['Collect inputs','Review inputs']) {
    panel = await openCheckpoint(page, title); await panel.getByRole('button', { name: 'Report or verify checkpoint', exact: true }).click(); await panel.getByRole('textbox', { name: 'Progress or acceptance reason', exact: true }).fill('Reviewed and accepted against the displayed criterion.');
    await expect(panel.getByRole('button', { name: 'Skip optional checkpoint', exact: true })).toBeDisabled(); await panel.getByRole('button', { name: 'Accept criterion', exact: true }).click(); await page.getByRole('dialog', { name: 'Accept this checkpoint?', exact: true }).getByRole('button', { name: 'Accept criterion', exact: true }).click();
    await expect(page.getByRole('button', { name: new RegExp(`${title} · Verified`) })).toBeVisible(); await page.getByRole('button', { name: new RegExp(`${title} · Verified`) }).click();
  }
  await page.getByRole('button', { name: 'Focus goal', exact: true }).click(); await page.getByRole('button', { name: 'Work on checkpoint', exact: true }).click();
  await expect.poll(async () => (await goal()).checkpoints.find(c => c.checkpointId === proof.checkpointId)?.sourceRunId).toBeTruthy(); const runId = (await goal()).checkpoints.find(c => c.checkpointId === proof.checkpointId)!.sourceRunId!;
  await expect.poll(async () => (await call<{ status: string }>(api, `${root}/agent-runs/${runId}`)).status).toBe('completed'); const run = await call<{ revision: number }>(api, `${root}/agent-runs/${runId}`);
  const attempt = await call<{ attemptId: string }>(api, `${root}/evaluation-attempts`, { subjectKind: 1, subjectId: runId, subjectRevision: run.revision, suiteId: suite.suiteId, suiteVersion: suite.version, dedupKey: `proof-${nonce}`, expectedRevision: await revision() });
  await page.getByRole('button', { name: 'Refresh goals', exact: true }).click(); panel = await openCheckpoint(page, 'Deliver checked result'); await panel.getByRole('button', { name: 'Report or verify checkpoint', exact: true }).click();
  await panel.getByLabel('Saved Evaluation attempt ID', { exact: true }).fill(attempt.attemptId); await panel.getByRole('button', { name: 'Verify saved evidence', exact: true }).click(); await expect(page.getByRole('button', { name: /Deliver checked result · Verified/ })).toBeVisible();
  const savedProof = (await goal()).checkpoints.find(c => c.checkpointId === proof.checkpointId)!;
  await expect(panel.getByLabel('Exact source Run ID', { exact: true })).toHaveCount(0); await expect(panel.getByText('Verified evidence is preserved. This action audits the saved reference without revalidating its source.')).toBeVisible();
  await panel.getByRole('button', { name: 'Record evidence audit', exact: true }).click(); await expect(page.getByRole('button', { name: 'Refresh goals', exact: true })).toBeEnabled(); expect((await goal()).checkpoints.find(c => c.checkpointId === proof.checkpointId)).toEqual(savedProof);
  const current = await goal(); const replacement = await request(api, `${endpoint}/${id}/checkpoints/${proof.checkpointId}/verify`, { kind: 4, requestKey: crypto.randomUUID(), expectedSecurityRevision: await revision(), expectedGoalRevision: current.revision, expectedPlanRevision: current.planRevision, checkpointId: proof.checkpointId, evidence: { kind: 0, sourceId: crypto.randomUUID(), runId } });
  expect(replacement.status()).toBe(409); expect(await goal()).toEqual(current);
  await page.getByRole('button', { name: /Deliver checked result · Verified/ }).click(); panel = await openCheckpoint(page, 'Review final result'); await panel.getByRole('button', { name: 'Report or verify checkpoint', exact: true }).click(); await panel.getByRole('textbox', { name: 'Progress or acceptance reason', exact: true }).fill('Required result is independently accepted.');
  await panel.getByRole('button', { name: 'Accept criterion', exact: true }).click(); await page.getByRole('dialog', { name: 'Accept this checkpoint?', exact: true }).getByRole('button', { name: 'Accept criterion', exact: true }).click();
  await page.getByRole('textbox', { name: 'Final acceptance reason', exact: true }).fill('All required outcomes hold; the optional appendix was safely skipped.'); await page.getByRole('button', { name: 'Accept completed goal', exact: true }).click(); await page.getByRole('dialog', { name: 'Accept the completed goal?', exact: true }).getByRole('button', { name: 'Accept completed goal', exact: true }).click();
  await expect(page.getByText('Goal completed', { exact: true })).toBeVisible(); expect((await call<{ lifecycleStatus: string }>(api, `${root}/sessions/${chat.sessionId}`)).lifecycleStatus).toBe('active'); expect(errors).toEqual([]);
  await test.info().attach('synthetic-stabilization-fixture', { body: JSON.stringify({ tenant: tenant.tenantId, session: chat.sessionId, goal: id, proofCheckpoint: proof.checkpointId }), contentType: 'application/json' });
});
