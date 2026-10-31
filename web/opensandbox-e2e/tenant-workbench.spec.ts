import { expect, test, type APIRequestContext } from '@playwright/test';
import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { canonicalFetch, grantPrivateSpaceRead, signInTenant } from '../e2e/support/tenant-auth';
import { selectSpreadsheetCell } from '../e2e/support/spreadsheet';
import { openConversationTool } from '../e2e/support/experience-navigation';

async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data, timeout: 150_000 });
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBe(true);
  return response.json();
}

test('tenant Workbench consent executes native Office files and publishes exact originals', async ({ page }, info) => {
  expect((await (await page.request.get('/health')).json()).profile).toBe('Synthetic');
  const login = process.env.TENANT_UI_LOGIN, password = process.env.TENANT_UI_PASSWORD;
  if (!login || !password) throw new Error('Supply the disposable host bootstrap identity explicitly.');
  await signInTenant(page, login, password);
  const api = page.request, nonce = crypto.randomUUID().slice(0, 8);
  const tenant = (await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Workbench ${nonce}`, slug: `workbench-${nonce}` })).tenantId;
  const root = `/api/v2/tenants/${tenant}`;
  const revision = async () => (await call<{ revision: number }>(api, root + '/access/me')).revision;
  // Exercise exact consent for both native execution and immutable publication.
  // An Allowed decision without an applied effect is deliberately not success.
  await call(api, root + '/operation-policies/artifacts.create', {
    expectedRevision: await revision(), expectedPolicyRevision: 0, mode: 'always', independentReview: false,
  }, 'PUT');
  const profile = await call<{ profile: { id: string; revision: number } }>(api, root + '/sandbox-profiles', {
    name: 'Installed native Workbench', description: 'Disposable current-IAM verification', enabled: true,
    imageReference: process.env.SELFHOST_WORKBENCH_IMAGE, expectedRevision: await revision(), expectedProfileRevision: 0,
  });
  const sourceVerified = await call<{ attempt: { accepted: boolean } }>(api, root + `/sandbox-profiles/${profile.profile.id}/verify`, {
    expectedRevision: await revision(), expectedProfileRevision: profile.profile.revision,
  });
  expect(sourceVerified.attempt.accepted).toBe(true);
  const draft = await call<{ draftId: string; definitionId: string; candidate: Record<string, unknown> }>(api, root + '/agent-definitions', { name: 'Native Office verifier', expectedRevision: await revision() });
  draft.candidate.sandboxPolicy = { defaultProfileId: profile.profile.id, allowedProfileIds: [profile.profile.id] };
  draft.candidate.executionPolicy = { capabilities: { mode: 'Selected', resolvedCapabilities: ['sandbox.exec', 'artifacts.create_from_workspace'] }, projection: { alwaysCapabilities: [] } };
  const updated = await call<{ revision: number }>(api, root + `/definition-drafts/${draft.draftId}`, { candidate: draft.candidate, expectedRevision: await revision(), expectedDraftRevision: 1 }, 'PUT');
  await call(api, root + `/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: updated.revision });
  const agent = await call<{ principalId: string; instance: { instanceId: string; revision: number } }>(api, root + '/agents/from-publication', { definitionId: draft.definitionId, version: 1, expectedRevision: await revision() });
  await grantPrivateSpaceRead(api, tenant, agent.instance.instanceId);
  const role = await call<{ roleId: string }>(api, root + '/roles', { name: 'Exact disposable native executor', permissions: ['agents.read', 'agents.chat', 'sessions.read', 'sessions.create', 'agent-runs.read', 'agent-runs.create', 'tasks.read', 'tasks.delegate', 'sandbox.execute', 'sandbox.read', 'workspaces.read', 'workspaces.write', 'workspaces.delete', 'artifacts.create', 'artifacts.read'], expectedRevision: await revision() });
  await call(api, root + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
  const verification = await call<{ status: string }>(api, root + `/agents/${agent.instance.instanceId}/sandbox/verify`, {
    expectedRevision: await revision(), expectedInstanceRevision: agent.instance.revision, profileId: profile.profile.id, expectedProfileRevision: profile.profile.revision,
  });
  expect(verification.status).toBe('Verified');
  const session = await call<{ sessionId: string }>(api, root + '/sessions', { retentionKind: 'saved', agentInstanceId: agent.instance.instanceId, expectedRevision: await revision(), expectedInstanceRevision: agent.instance.revision });
  await page.goto(`/t/${tenant}/c/${session.sessionId}`);
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('synthetic-sandbox:canonical-demo');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  const approvals = page.getByRole('region', { name: 'Conversation approvals', exact: true });
  for (const file of ['report.xlsx', 'report.xlsx', 'report.docx']) {
    let pendingId = '';
    await expect.poll(async () => {
      const view = await call<{ items: { requestId: string; status: string }[] }>(api, root + `/sessions/${session.sessionId}/approvals`);
      pendingId = view.items.find(item => item.status === 'pending')?.requestId ?? '';
      return pendingId;
    }).not.toBe('');
    await approvals.locator(`a[href$="/${pendingId}"]`).click();
    const detail = page.getByRole('dialog', { name: 'Approval details', exact: true });
    await expect(detail).toContainText(file);
    await detail.getByRole('button', { name: 'Approve', exact: true }).click();
    await page.getByRole('dialog', { name: 'Approve?', exact: true }).getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(detail.getByText('Approved decision recorded.', { exact: true })).toBeVisible();
    await detail.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(detail).toHaveCount(0);
  }
  await expect(page.getByText('Native Workbench completed: Python and Node ran, Excel and Word files validated and published.', { exact: true })).toBeVisible({ timeout: 120_000 });
  await expect(page.getByRole('button', { name: 'Stop', exact: true })).toHaveCount(0);
  await page.reload();
  await expect(page.getByText('Native Workbench completed: Python and Node ran, Excel and Word files validated and published.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Preview report.xlsx', exact: true }).first().click();
  await selectSpreadsheetCell(page.getByRole('dialog', { name: 'report.xlsx', exact: true }), 'B1', '5');
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Preview report.docx', exact: true }).first().click();
  await expect(page.frameLocator('iframe[title="Word document preview"]').getByText('Total: 5', { exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  const artifacts = await call<{ items: { artifactId: string; displayName: string; sha256Hex: string; agentRunId: string }[] }>(api, root + `/sessions/${session.sessionId}/artifacts`);
  expect(artifacts.items).toHaveLength(2);
  for (const file of artifacts.items) {
    const response = await api.get(root + `/sessions/${session.sessionId}/artifacts/${file.artifactId}/content`);
    expect(response.ok()).toBe(true);
    const bytes = await response.body();
    expect(createHash('sha256').update(bytes).digest('hex')).toBe(file.sha256Hex);
    expect(file.agentRunId).toBeTruthy();
    const source = await api.get(root + `/sessions/${session.sessionId}/workspace/content?path=` + encodeURIComponent('/working/' + file.displayName));
    expect(source.ok()).toBe(true);
    expect(await source.body()).toEqual(bytes);
  }
  const worker = await call<{ principalId: string; instance: { instanceId: string; revision: number } }>(api, root + '/agents/from-publication', { definitionId: draft.definitionId, version: 1, expectedRevision: await revision() });
  expect(worker.principalId).not.toBe(agent.principalId);
  await grantPrivateSpaceRead(api, tenant, worker.instance.instanceId);
  await call(api, root + '/role-assignments', { principalId: worker.principalId, roleId: role.roleId, expectedRevision: await revision() });
  const before = await call<{ items: { instanceId: string }[] }>(api, root + `/sessions/${session.sessionId}/task-targets`);
  expect(before.items.some(item => item.instanceId === worker.instance.instanceId)).toBe(false);
  await call(api, root + `/agents/${worker.instance.instanceId}/cooperation`, { cooperation: { acceptTasks: true, acceptDirectMessages: false, allowDirectMessageWake: false, maxConcurrentTasks: 2 }, expectedRevision: await revision(), expectedInstanceRevision: worker.instance.revision }, 'PUT');
  const configuredWorker = await call<{ instance: { revision: number } }>(api, root + `/agents/${worker.instance.instanceId}`);
  const workerVerified = await call<{ status: string }>(api, root + `/agents/${worker.instance.instanceId}/sandbox/verify`, {
    expectedRevision: await revision(), expectedInstanceRevision: configuredWorker.instance.revision, profileId: profile.profile.id, expectedProfileRevision: profile.profile.revision,
  });
  expect(workerVerified.status).toBe('Verified');
  await openConversationTool(page, 'Tasks');
  await page.getByRole('button', { name: /Delegate task$/ }).click();
  await page.getByLabel('Assigned Agent', { exact: true }).click();
  await page.locator('.ant-select-dropdown:visible .ant-select-item-option').filter({ hasText: 'Native Office verifier' }).click();
  await page.getByLabel('Title', { exact: true }).fill('Native delegated Office proof');
  await page.getByLabel('Objective', { exact: true }).fill('synthetic-sandbox:canonical-demo');
  await page.getByRole('checkbox', { name: 'Send one completion report to this conversation', exact: true }).check();
  const assignment = page.waitForRequest(r => r.method() === 'POST' && new URL(r.url()).pathname === root + `/sessions/${session.sessionId}/tasks`);
  await page.getByRole('button', { name: 'Assign task', exact: true }).click();
  const originalCommand = (await assignment).postDataJSON();
  type TaskView = { taskId: string; state: string; workerSessionId: string; selectedTargetInstanceId: string; result: string };
  let task: TaskView | undefined;
  await expect.poll(async () => {
    task = (await call<{ items: TaskView[] }>(api, root + `/sessions/${session.sessionId}/tasks`)).items[0];
    return task?.workerSessionId;
  }).toBeTruthy();
  expect(task!.selectedTargetInstanceId).toBe(worker.instance.instanceId);
  expect(task!.workerSessionId).not.toBe(session.sessionId);
  // Background workers are not human Chat Sessions. Use their canonical
  // organization approval owner, never a conversation-route bypass.
  expect((await api.get(root + `/sessions/${task!.workerSessionId}/approvals`)).status()).toBe(403);
  for (const file of ['report.xlsx', 'report.xlsx', 'report.docx']) {
    let pendingId = '';
    await expect.poll(async () => {
      const view = await call<{ items: { requestId: string; status: string }[] }>(api, root + '/approvals?status=pending');
      pendingId = view.items.find(item => item.status === 'pending')?.requestId ?? '';
      return pendingId;
    }).not.toBe('');
    await page.goto(`/t/${tenant}/admin/approvals/${pendingId}`);
    const detail = page.getByRole('dialog', { name: 'Approval details', exact: true });
    await expect(detail).toContainText(file);
    await detail.getByRole('button', { name: 'Approve', exact: true }).click();
    await page.getByRole('dialog', { name: 'Approve?', exact: true }).getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(detail.getByText('Approved decision recorded.', { exact: true })).toBeVisible();
  }
  await expect.poll(async () => {
    task = await call<TaskView>(api, root + `/tasks/${task!.taskId}`);
    return task.state;
  }, { timeout: 90_000 }).toBe('Succeeded');
  expect(task!.result).toContain('Native Workbench completed');
  const workerArtifacts = await call<typeof artifacts>(api, root + `/sessions/${task!.workerSessionId}/artifacts`);
  expect(workerArtifacts.items).toHaveLength(2);
  for (const file of workerArtifacts.items) {
    const content = await api.get(root + `/sessions/${task!.workerSessionId}/artifacts/${file.artifactId}/content`);
    expect(content.ok()).toBe(true);
    expect(createHash('sha256').update(await content.body()).digest('hex')).toBe(file.sha256Hex);
  }
  const replay = await call<{ replayed: boolean }>(api, root + `/sessions/${session.sessionId}/tasks`, { ...originalCommand, expectedSecurityRevision: await revision() });
  expect(replay.replayed).toBe(true);
  expect((await call<{ items: TaskView[] }>(api, root + `/sessions/${session.sessionId}/tasks`)).items).toHaveLength(1);
  await expect.poll(async () => (await call<{ items: { reportAdmitted: boolean }[] }>(api, root + `/tasks/${task!.taskId}/notifications`)).items.some(n => n.reportAdmitted)).toBe(true);
  await page.goto(`/t/${tenant}/c/${session.sessionId}`);
  await openConversationTool(page, 'Tasks');
  await page.getByRole('listitem').filter({ hasText: 'Native delegated Office proof' }).getByRole('button', { name: 'Inspect task', exact: true }).click();
  await expect(page.getByText('Original worker result', { exact: true })).toBeVisible();
  await expect(page.getByText('Succeeded', { exact: true })).toBeVisible();
  const fixture = { tenant, instance: agent.instance.instanceId, session: session.sessionId, profile: profile.profile.id, artifacts: artifacts.items, task, workerInstance: worker.instance.instanceId, workerArtifacts: workerArtifacts.items };
  writeFileSync(info.outputPath('native-fixture.json'), JSON.stringify(fixture));
  await info.attach('native-tenant-fixture', { body: JSON.stringify(fixture), contentType: 'application/json' });
});
