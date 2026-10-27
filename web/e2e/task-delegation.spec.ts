import { test, expect, type APIRequestContext } from '@playwright/test';
import { canonicalFetch, grantPrivateSpaceRead, signInTenant } from './support/tenant-auth';

async function call<T>(api: APIRequestContext, path: string, data?: unknown, method = data === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method, headers: { 'X-AgentCore-CSRF': csrf.token }, data });
  expect(response.ok(), `${path}: ${response.status()} ${await response.text()}`).toBeTruthy(); return response.json();
}
test('real delegated worker asks, waits, receives human guidance, completes and preserves independent Goals', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await signInTenant(page, 'task-owner@example.test', 'synthetic-task-password');
  const api = page.request; const nonce = crypto.randomUUID().slice(0, 8);
  expect((await call<{ profile: string }>(api, '/health')).profile).toBe('Synthetic');
  const tenant = await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Delegation ${nonce}`, slug: `delegation-${nonce}` });
  const root = `/api/v2/tenants/${tenant.tenantId}`; const revision = async () => (await call<{ revision: number }>(api, `${root}/access/me`)).revision;
  const source = await call<{ principalId: string; instance: { instanceId: string; revision: number } }>(api, `${root}/agents`, { templateId: 'examiner', version: 1, expectedRevision: await revision() });
  const draft = await call<{ draftId: string; definitionId: string; candidate: Record<string, unknown> }>(api, `${root}/agent-definitions`, { name: 'Task reviewer', expectedRevision: await revision() });
  draft.candidate.identity = { name: 'Migration reviewer', role: 'Reviewer', description: 'Reviews assigned Synthetic tasks', tone: 'Precise' };
  draft.candidate.executionPolicy = { harness: [], knowledgeSources: [], toolAllowlist: [], capabilities: { mode: 'Selected', resolvedCapabilities: ['tasks.messages.send', 'tasks.messages.list', 'execution.wait'] }, projection: { alwaysCapabilities: [] } };
  const changed = await call<{ revision: number }>(api, `${root}/definition-drafts/${draft.draftId}`, { candidate: draft.candidate, expectedRevision: await revision(), expectedDraftRevision: 1 }, 'PUT');
  await call(api, `${root}/definition-drafts/${draft.draftId}/publish`, { expectedRevision: await revision(), expectedDraftRevision: changed.revision });
  const worker = await call<{ principalId: string; instance: { instanceId: string; revision: number } }>(api, `${root}/agents/from-publication`, { definitionId: draft.definitionId, version: 1, expectedRevision: await revision() });
  for (const agent of [source, worker]) {
    await grantPrivateSpaceRead(api, tenant.tenantId, agent.instance.instanceId);
    const role = await call<{ roleId: string }>(api, `${root}/roles`, { name: `Explicit execution ${agent.instance.instanceId}`, permissions: ['agents.read', 'agents.chat', 'sessions.read', 'sessions.create', 'agent-runs.read', 'agent-runs.create', 'tasks.read', 'agent-messages.read', 'agent-messages.send'], expectedRevision: await revision() });
    await call(api, `${root}/role-assignments`, { principalId: agent.principalId, roleId: role.roleId, expectedRevision: await revision() });
  }
  await page.goto(`/t/${tenant.tenantId}/admin/agents/${worker.instance.instanceId}/settings`);
  await expect(page.getByRole('checkbox', { name: 'Accept delegated tasks', exact: true })).not.toBeChecked();
  await page.getByRole('checkbox', { name: 'Accept delegated tasks', exact: true }).check();
  await page.getByRole('button', { name: 'Save cooperation', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save cooperation', exact: true })).toBeDisabled();
  const chat = await call<{ sessionId: string }>(api, `${root}/sessions`, { agentInstanceId: source.instance.instanceId, mode: 'text', retentionKind: 'saved', expectedRevision: await revision(), expectedInstanceRevision: source.instance.revision });
  await page.goto(`/t/${tenant.tenantId}/c/${chat.sessionId}`);
  await page.getByRole('button', { name: 'Tasks', exact: true }).click();
  await expect(page.getByText('No tasks created in this conversation')).toBeVisible();
  await page.getByRole('button', { name: /Delegate task$/ }).click();
  await page.getByLabel('Assigned Agent', { exact: true }).click();
  await page.locator('.ant-select-dropdown:visible .ant-select-item-option').filter({ hasText: 'Migration reviewer' }).click();
  await page.getByLabel('Title', { exact: true }).fill('Review migration ownership');
  await page.getByLabel('Objective', { exact: true }).fill('synthetic-task-worker synthetic-task-question: Review the approved migration source.');
  await page.getByRole('button', { name: 'Assign task', exact: true }).click();
  await expect(page.getByText('Which approved source should I use?', { exact: true })).toBeVisible();
  await expect(page.getByText('Waiting', { exact: true })).toBeVisible();
  await page.getByLabel('Human guidance', { exact: true }).fill('Use the reviewed migration proposal only.');
  await page.getByRole('button', { name: 'Send guidance', exact: true }).click();
  await expect(page.getByText('Synthetic worker completed the assigned review. No external effects were performed.', { exact: true })).toBeVisible();
  await expect(page.getByText('Task success is separate from Goal verification.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Acknowledge result', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Acknowledge result', exact: true })).toHaveCount(0);
  const tasks = await call<{ items: { taskId: string; state: string; workerSessionId: string; selectedTargetInstanceId: string; sourceSessionId: string }[] }>(api, `${root}/sessions/${chat.sessionId}/tasks`);
  const task = tasks.items[0]; expect(task.state).toBe('Succeeded'); expect(task.selectedTargetInstanceId).toBe(worker.instance.instanceId); expect(task.workerSessionId).not.toBe(chat.sessionId);
  await test.info().attach('task-fixture', { body: JSON.stringify({ tenantId: tenant.tenantId, sourceSessionId: chat.sessionId, workerInstanceId: worker.instance.instanceId, taskId: task.taskId }), contentType: 'application/json' });
  expect((await call<{ goals: { items: unknown[] } }>(api, `${root}/sessions/${chat.sessionId}/goals`)).goals.items).toEqual([]);
  for (const width of [320, 390, 768, 1024, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(page.getByText('Original worker result', { exact: true })).toBeVisible();
    await expect.poll(async () => page.getByRole('dialog', { name: 'Tasks', exact: true }).evaluate(node => {
      const box = node.getBoundingClientRect(); return box.left >= -1 && box.right <= innerWidth + 1;
    }), { message: `Task drawer must fit the ${width}px viewport` }).toBe(true);
    const box = await page.locator('.session-sandbox-drawer .ant-drawer-body').evaluate(node => ({ scroll: node.scrollWidth, width: node.clientWidth }));
    expect(box.scroll, `Task drawer overflow at ${width}`).toBeLessThanOrEqual(box.width + 1);
    await page.screenshot({ path: test.info().outputPath(`task-detail-${width}.png`), fullPage: true, animations: 'disabled' });
  }
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.keyboard.press('Escape'); await expect(page.getByRole('button', { name: 'Tasks', exact: true })).toBeFocused();
  expect(errors).toEqual([]);
});
