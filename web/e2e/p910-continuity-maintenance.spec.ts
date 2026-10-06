import { expect, test, type Page } from '@playwright/test';

test.use({ actionTimeout: 15_000 });
async function select(page: Page, label: string, value: string) {
  const field = page.getByRole('combobox', { name: label, exact: true });
  await field.click(); await field.fill(value);
  await page.locator('.ant-select-item-option').filter({ hasText: value }).last().click();
}

test('Thought consolidates separately owned Memory and Experience; lineage, opt-out, explicit forget and reload remain accurate', async ({ page }, info) => {
  test.setTimeout(180_000);
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', e => { if (e.type() === 'error' && !e.text().startsWith('Warning: [antd:')) errors.push(e.text()); });
  page.on('requestfailed', r => { if (!r.failure()?.errorText.includes('ERR_ABORTED')) errors.push(r.url()); });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/admin');
  await page.getByRole('tab', { name: 'Instances', exact: true }).click();
  await page.getByRole('button', { name: 'New instance', exact: true }).click();
  await select(page, 'Definition', 'General Assistant · general-assistant');
  await select(page, 'Published version', 'v9 · Built-in · Published');
  await page.getByRole('button', { name: 'Create instance', exact: true }).click();
  await page.getByRole('tab', { name: 'Continuity', exact: true }).click();
  const id = new URL(page.url()).pathname.split('/')[3];
  const token = await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': token! };
  const root = `/api/v2/admin/agent-instances/${id}`;
  const setting = page.getByRole('switch', { name: 'Allow agent consolidation', exact: true });
  await expect(setting).toHaveAttribute('aria-checked', 'false');
  await expect(setting).toBeEnabled(); await setting.click();
  await expect(setting).toHaveAttribute('aria-checked', 'true');
  await page.getByRole('tab', { name: 'Experience', exact: true }).click();
  await page.getByRole('switch', { name: 'Enable experience', exact: true }).click();
  await expect(page.getByText('Enabled · completed work may be retrospected')).toBeVisible();
  const sessions: string[] = [];
  for (const subject of ['language', 'samples', 'code']) {
    // Supported create/attach/Send/pause product paths; no database seed or debug endpoint.
    const response = await page.request.post('/api/v2/sessions', { headers, data: { agentInstanceId: id, mode: 'text' } });
    expect(response.ok()).toBe(true);
    const session = (await response.json()).sessionId as string;
    sessions.push(session);
    await page.goto(`/c/${session}`);
    // Ready also describes the initial empty shell. Profile appears only after
    // bootstrap has attached this route and loaded its session catalog.
    await expect(page.getByTestId('profile')).toHaveText('Synthetic');
    await expect(page.getByTestId("connection")).toHaveText("Ready");
    await page.getByLabel('Message', { exact: true }).fill(`synthetic-inferred-frontend:${subject}`);
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Observed a durable frontend preference.', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeHidden();
    expect((await page.request.post(`/api/v2/sessions/${session}/deactivate`, { headers })).ok()).toBe(true);
    await expect(page.getByRole('button', { name: 'Resume', exact: true })).toBeVisible();
    expect((await page.request.post(root + '/experience/checkpoints', { headers, data: { sessionId: session } })).ok()).toBe(true);
  }
  await expect.poll(async () => (await (await page.request.get(root + '/experience', { headers })).json()).items.filter((e: { status: string }) => e.status === 'Completed').length).toBe(3);
  const beforeMemory = await (await page.request.get(root + '/learned-memory?scope=IdentityUser', { headers })).json();
  expect(beforeMemory.items).toHaveLength(3);
  expect(beforeMemory.items.every((m: { provenance: { source: string } }) => m.provenance.source === 'agent_inferred')).toBe(true);
  // The live approval resolves scope and subjects in Core before asking for consent.
  const approvalSessionResponse = await page.request.post('/api/v2/sessions', { headers, data: { agentInstanceId: id, mode: 'text' } });
  expect(approvalSessionResponse.ok()).toBe(true);
  const approvalSession = (await approvalSessionResponse.json()).sessionId as string;
  await page.goto(`/c/${approvalSession}`);
  await expect(page.getByTestId('profile')).toHaveText('Synthetic');
  await expect(page.getByTestId('connection')).toHaveText('Ready');
  await page.getByLabel('Message', { exact: true }).fill('synthetic-maintain-memory');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'Approve identity consolidation', exact: true });
  await expect(approval).toBeVisible({ timeout: 30_000 });
  await expect(approval).toContainText('This Agent Instance and trusted user profile');
  await expect(approval).toContainText('Memory kind');
  await expect(approval).toContainText('Preference');
  await expect(approval).toContainText('Source subjects');
  await expect(approval).toContainText('Prefer TypeScript for frontend examples.');
  for (const source of beforeMemory.items) {
    await expect(approval).toContainText(source.memoryId);
    await expect(approval).toContainText(source.subject);
  }
  await approval.getByRole('button', { name: 'Reject', exact: true }).click();
  await expect(approval).toBeHidden();
  await expect(page.getByText('The selected state could not be consolidated; no further change was made.', { exact: true })).toBeVisible();
  expect((await (await page.request.get(root + '/learned-memory?scope=IdentityUser', { headers })).json()).items).toHaveLength(3);
  expect((await page.request.post(`/api/v2/sessions/${approvalSession}/deactivate`, { headers })).ok()).toBe(true);

  await page.goto(`/admin/instances/${id}`);
  await page.getByRole('tab', { name: 'Automation', exact: true }).click();
  await page.getByRole('tab', { name: 'Thoughts', exact: true }).click();
  const thoughts = page.getByRole('region', { name: 'Thoughts', exact: true });
  async function createAndRun(marker: string) {
    await thoughts.getByLabel('Thinking prompt', { exact: true }).fill(marker);
    await thoughts.getByRole('switch', { name: 'Enable thought activation', exact: true }).click();
    await thoughts.getByRole('button', { name: 'Create thought', exact: true }).click();
    await thoughts.getByRole('button', { name: `View thought: ${marker}`, exact: true }).click();
    await thoughts.getByRole('button', { name: 'Run now', exact: true }).click();
    await expect(thoughts.getByRole('row').filter({ hasText: marker }).getByText('Action completed', { exact: true }).first()).toBeVisible({ timeout: 30_000 });
  }
  await createAndRun('synthetic-maintain-memory');
  await thoughts.getByRole('button', { name: 'View thought: synthetic-maintain-memory', exact: true }).click();
  await createAndRun('synthetic-maintain-experience');
  const work = await (await page.request.get(root + '/work-items', { headers })).json();
  const maintenance = work.items.filter((w: { origin: string }) => w.origin === 'Thought activation');
  expect(maintenance).toHaveLength(2); expect(maintenance.every((w: { attentionRequired: boolean }) => !w.attentionRequired)).toBe(true);
  await thoughts.getByRole('button', { name: 'View run', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Run details', exact: true }).getByText('Thought', { exact: true }).first()).toBeVisible();
  await page.getByRole('dialog', { name: 'Run details', exact: true }).getByRole('button', { name: 'Close', exact: true }).click();
  await page.getByRole('tab', { name: 'Continuity', exact: true }).click();
  await page.getByRole('tab', { name: 'Memory', exact: true }).click();
  await page.getByRole('button', { name: 'Load items', exact: true }).click();
  await expect(page.getByText('Consolidated from 3 memories', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Expand row', exact: true }).click();
  const details = page.getByRole('region', { name: 'Learned memory details', exact: true });
  await details.getByRole('button', { name: 'View sources', exact: true }).click();
  await expect(details.getByText('Superseded', { exact: true })).toHaveCount(3);
  await page.getByRole('tab', { name: 'Experience', exact: true }).click();
  const experiences = page.getByRole('region', { name: 'Experience', exact: true });
  await experiences.getByRole('button', { name: 'View experience: Review repeated observable completed work', exact: true }).click();
  await expect(experiences.getByText('Consolidated from 3 experiences', { exact: true })).toBeVisible();
  await expect(experiences.getByRole('cell', { name: 'Superseded', exact: true })).toHaveCount(3);
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    await page.screenshot({ path: info.outputPath(`p910-${width}.png`), fullPage: true });
  }
  await setting.click(); await expect(setting).toHaveAttribute('aria-checked', 'false');
  await page.getByRole('tab', { name: 'Automation', exact: true }).click();
  await page.getByRole('tab', { name: 'Thoughts', exact: true }).click();
  if (!(await thoughts.getByRole('button', { name: 'Run now', exact: true }).isVisible()))
    await thoughts.getByRole('button', { name: 'View thought: synthetic-maintain-experience', exact: true }).click();
  await thoughts.getByRole('button', { name: 'Run now', exact: true }).click();
  await expect(thoughts.getByText('No action', { exact: true }).first()).toBeVisible({ timeout: 30_000 });
  await page.getByRole('tab', { name: 'Continuity', exact: true }).click();
  await page.getByRole('tab', { name: 'Memory', exact: true }).click();
  const canonical = await (await page.request.get(root + '/learned-memory?scope=IdentityUser', { headers })).json();
  expect(canonical.items).toHaveLength(1);
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: 'Forget this learned-memory item?', exact: true });
  await expect(confirm.getByText(/Source conversations and other retained continuity records are not deleted/)).toBeVisible();
  await confirm.getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(page.getByText('No active learned-memory items in this scope.', { exact: true })).toBeVisible();
  await page.reload(); await page.getByRole('tab', { name: 'Continuity', exact: true }).click();
  await expect(setting).toHaveAttribute('aria-checked', 'false');
  await page.getByRole('button', { name: 'Load items', exact: true }).click();
  await expect(page.getByText('No active learned-memory items in this scope.', { exact: true })).toBeVisible();
  const tombstone = await (await page.request.get(root + `/learned-memory/${canonical.items[0].memoryId}?scope=IdentityUser`, { headers })).json();
  expect(tombstone.status).toBe('Deleted'); expect(tombstone.content).toBe('');
  for (const session of sessions) expect((await page.request.get(`/api/v2/sessions/${session}`, { headers })).ok()).toBe(true);
  expect(errors).toEqual([]);
});
