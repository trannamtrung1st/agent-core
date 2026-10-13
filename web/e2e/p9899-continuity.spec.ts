import { navigateAdmin } from "./support/admin-navigation";
import { expect, test, type Page } from '@playwright/test';

test.use({ actionTimeout: 15_000 });

async function select(page: Page, label: string, text: string) {
  await page.getByRole('combobox', { name: label, exact: true }).click();
  if (label === 'Identity' || label === 'Published version' || label === 'Definition') await page.getByRole('combobox', { name: label, exact: true }).fill(text);
  await page.locator('.ant-select-item-option').filter({ hasText: text }).last().click();
}
async function send(page: Page, text: string) {
  await page.getByLabel('Message', { exact: true }).fill(text);
  await page.getByRole('button', { name: 'Send', exact: true }).click();
}

test('Experience informs Chat while unattended Skill management stays unavailable; owner controls work at narrow width', async ({ page }, testInfo) => {
  test.setTimeout(150_000);
  const errors: string[] = [];
  const reviewerName = `Continuity reviewer ${Date.now()}`;
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error' && !message.text().startsWith('Warning: [antd: List]')) errors.push(message.text()); });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');
  await send(page, 'Hello');
  await expect(page.getByText('Hello from synthetic.', { exact: true }).last()).toBeVisible();
  await page.getByRole('link', { name: 'Open Admin' }).click();
  await navigateAdmin(page, 'Instances');
  await page.getByRole('button', { name: 'New instance', exact: true }).click();
  await select(page, 'Definition', 'General Assistant · general-assistant');
  await select(page, 'Published version', 'v21 · Built-in · Published');
  const dialog = page.getByRole('dialog', { name: 'New instance', exact: true });
  await dialog.getByText('Custom persona', { exact: true }).click();
  await dialog.getByLabel('Persona name').fill(reviewerName);
  await dialog.getByLabel('Persona role').fill('Store review');
  await dialog.getByLabel('Persona description').fill('Review observable outcomes.');
  await dialog.getByLabel('Persona tone').fill('Clear');
  await dialog.getByRole('button', { name: 'Create instance', exact: true }).click();
  await expect(page.getByRole('heading', { name: reviewerName, exact: true })).toBeVisible();
  const id = new URL(page.url()).pathname.split('/')[3];
  await navigateAdmin(page, "Continuity");
  await navigateAdmin(page, "Experience");
  const experience = page.getByRole('region', { name: 'Experience', exact: true });
  const initiative = page.getByRole('region', { name: 'Automations', exact: true });
  await expect(experience.getByText('No experience yet. Enable experience and retrospect a completed task.')).toBeVisible();
  await experience.getByRole('switch', { name: 'Enable experience', exact: true }).click();
  await expect(experience.getByText('Enabled · completed work may be retrospected')).toBeVisible();
  await page.locator('.admin-header').getByRole('link', { name: /Chat$/ }).first().click();
  // Returning from Admin preserves the last conversation; choose a new one for this reviewer.
  await page.getByRole('link', { name: 'Start a new chat', exact: true }).click();
  await expect(page.getByRole('combobox', { name: 'Identity', exact: true })).toBeEnabled();
  await select(page, 'Identity', reviewerName);
  await send(page, 'synthetic-fail-turn');
  await expect(page.getByText(/Synthetic turn failure|could not complete|failed/i).last()).toBeVisible({ timeout: 30_000 });
  await send(page, 'A correction: observe the current page before acting.');
  await expect(page.getByText('Hello from synthetic.', { exact: true }).last()).toBeVisible();
  const sessionId = page.url().split('/').at(-1)!;
  const ownerToken = await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'));
  const source = await page.request.get(`/api/v2/sessions/${sessionId}`, { headers: { 'X-AgentCore-Owner-Capability': ownerToken! } });
  expect((await source.json()).agentInstanceId).toBe(id);
  const paused = await page.request.post(`/api/v2/sessions/${sessionId}/deactivate`, { headers: { 'X-AgentCore-Owner-Capability': ownerToken! } });
  expect(paused.ok()).toBe(true);
  await expect(page.getByRole('button', { name: 'Resume', exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Open Admin' }).click();
  await page.goto(`/admin/instances/${id}/profile`);
  await navigateAdmin(page, "Continuity");
  await navigateAdmin(page, "Experience");
  // Pausing retains the source; a manual review uses generic Run execution.
  await expect(experience.getByText(/No experience yet/)).toBeVisible();
  await experience.getByRole("button", { name: "Enter Session ID", exact: true }).click();
  await experience.getByLabel('Session ID', { exact: true }).fill(sessionId);
  await experience.getByRole('button', { name: 'Retrospect now', exact: true }).click();
  await expect(experience.getByRole('button', { name: 'View experience: Review observable completed work', exact: true }).first()).toBeVisible({ timeout: 30_000 });
  await navigateAdmin(page, 'Continuity');
  await navigateAdmin(page, 'Experience');
  await experience.getByRole('button', { name: 'View experience: Review observable completed work', exact: true }).first().click();
  await expect(experience.getByText('The user supplied a correction', { exact: true })).toBeVisible();
  await experience.getByRole('button', { name: 'Retrospect now', exact: true }).click();
  await expect(experience.getByRole('button', { name: 'View experience: Review observable completed work', exact: true })).toHaveCount(1);
  await page.locator('.admin-header').getByRole('link', { name: /Chat$/ }).first().click();
  await page.getByRole('link', { name: 'Start a new chat', exact: true }).click();
  await select(page, 'Identity', reviewerName);
  await send(page, 'Use my recent experience before acting.');
  await expect(page.getByText('I will observe current page state before acting, based on earlier experience. Current policy still controls every action.', { exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Open Admin' }).click();
  await page.goto(`/admin/instances/${id}/profile`);
  await navigateAdmin(page, "Continuity");
  await navigateAdmin(page, "Experience");
  await navigateAdmin(page, 'Automation');
  await navigateAdmin(page, 'Triggers');
  await initiative.getByRole('button', { name: 'New automation', exact: true }).click();
  await initiative.getByLabel('Automation name', { exact: true }).fill('Review recent experience');
  await initiative.getByLabel('Automation instructions', { exact: true }).fill('synthetic-automation-improve: review recent experience and improve only when useful. Otherwise do nothing.');
  await expect(initiative.getByRole('switch', { name: 'Enable automation', exact: true })).not.toBeChecked();
  await initiative.getByRole('switch', { name: 'Enable automation', exact: true }).click();
  await initiative.getByRole('button', { name: 'Create automation', exact: true }).click();
  const automationSource = initiative.getByRole('button', { name: 'View automation: Review recent experience', exact: true });
  await expect(automationSource).toBeVisible();
  if (await automationSource.getAttribute('aria-expanded') !== 'true') await automationSource.click();
  const runPattern = `**/api/v2/admin/agent-instances/${id}/automations/*/run`;
  const statusPattern = `**/api/v2/admin/agent-instances/${id}/automations`;
  const previousStatus = await page.request.get(`/api/v2/admin/agent-instances/${id}/automations`, {
    headers: { 'X-AgentCore-Owner-Capability': ownerToken! }
  });
  expect(previousStatus.ok()).toBe(true);
  const previousStatusBody = await previousStatus.text();
  await page.route(statusPattern, route => route.request().method() === 'GET'
    ? route.fulfill({ status: 200, contentType: 'application/json', body: previousStatusBody })
    : route.continue());
  let runRequests = 0;
  let releaseRun!: () => void;
  const runGate = new Promise<void>(resolve => { releaseRun = resolve; });
  await page.route(runPattern, async route => { runRequests++; await runGate; await route.continue(); });
  const runButton = initiative.getByRole('button', { name: 'Run automation now', exact: true });
  await runButton.evaluate(button => { (button as HTMLButtonElement).click(); (button as HTMLButtonElement).click(); });
  await expect(runButton).toBeDisabled();
  await expect(runButton).toHaveAttribute('aria-busy', 'true');
  await expect.poll(() => runRequests).toBe(1);
  releaseRun();
  await expect(initiative.getByRole('button', { name: 'Refresh automations', exact: true })).toBeEnabled();
  await expect(runButton).toBeDisabled();
  await expect(runButton).toHaveText('Starting…');
  await runButton.evaluate(button => (button as HTMLButtonElement).click());
  expect(runRequests).toBe(1);
  await page.unroute(statusPattern);
  await initiative.getByRole('button', { name: 'Refresh automations', exact: true }).click();

  await expect(initiative.getByText(/Completed · No action/).first()).toBeVisible({ timeout: 30_000 });
  expect(runRequests).toBe(1);
  await page.unroute(runPattern);
  await initiative.getByRole('link', { name: 'View last run: Review recent experience', exact: true }).click();
  const work = page.getByRole('dialog', { name: 'Run details', exact: true });
  await expect(work.locator('.agent-run-details').getByText('No action', { exact: true })).toBeVisible({ timeout: 30_000 });
  await expect(work).toContainText('Skill management is unavailable; no change was made.');
  await expect(work.getByRole('button', { name: 'Approve Automation · Manual', exact: true })).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath('automation-management-boundary-desktop.png'), fullPage: true });
  await page.getByRole('dialog', { name: 'Run details', exact: true }).getByRole('link', { name: 'View Automation', exact: true }).click();
  await navigateAdmin(page, 'Skills & resources');
  await navigateAdmin(page, 'Skills');
  const skills = page.getByRole('region', { name: 'Instance Skills', exact: true });
  await expect(skills).toContainText('Create a local Skill or Customize a Definition Skill to keep your own procedure.');
  await expect(skills.getByText('Experience review', { exact: true })).toHaveCount(0);
  await navigateAdmin(page, 'Automation');
  await navigateAdmin(page, 'Triggers');
  await initiative.getByRole('button', { name: 'Refresh automations', exact: true }).click();
  await expect(initiative.getByText(/Completed · No action/).first()).toBeVisible();
  await initiative.getByRole('button', { name: 'Run automation now', exact: true }).click();
  // Both results are quiet now; the first row's unchanged label cannot prove
  // the second execution finished. Wait for two distinct durable completions.
  await expect.poll(async () => {
    const read = await page.request.get(`/api/v2/agent-instances/${id}/agent-runs`, {
      headers: { 'X-AgentCore-Owner-Capability': ownerToken! }
    });
    expect(read.ok()).toBe(true);
    const pageOfRuns = await read.json();
    return pageOfRuns.items.filter((item: { activationKind: string; automationId?: string; status: string }) =>
      item.activationKind === 'ManualBackground' && item.automationId && item.status === 'completed').length;
  }, { timeout: 30_000 }).toBe(2);
  await expect(initiative.getByText(/Completed · No action/).first()).toBeVisible({ timeout: 30_000 });
  const owner = await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'));
  const response = await page.request.get(`/api/v2/agent-instances/${id}/agent-runs`, { headers: { 'X-AgentCore-Owner-Capability': owner! } });
  expect(response.ok()).toBe(true);
  const data = await response.json();
  const manualRuns = data.items.filter((item: { activationKind: string; automationId?: string }) => item.activationKind === 'ManualBackground' && item.automationId);
  expect(manualRuns).toHaveLength(2); expect(manualRuns.every((item: { outcome: { attentionRequired: boolean } }) => !item.outcome.attentionRequired)).toBe(true);
  expect(manualRuns.every((item: { outcome: { kind: string } }) => item.outcome.kind === 'NoAction')).toBe(true);
  await page.setViewportSize({ width: 390, height: 844 });
  await initiative.getByRole('button', { name: 'Disable automation', exact: true }).click();
  await expect(initiative.getByRole('button', { name: 'Run automation now', exact: true })).toBeDisabled();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  await navigateAdmin(page, 'Continuity');
  await navigateAdmin(page, 'Experience');
  await experience.getByRole('button', { name: 'View experience: Review observable completed work', exact: true }).first().click();
  await experience.getByRole('button', { name: 'Suppress experience', exact: true }).first().click();
  await expect(experience.getByText('Suppressed', { exact: true }).first()).toBeVisible();
  await experience.getByRole('button', { name: 'Reset experience', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Reset experience', exact: true }).click();
  await expect(experience.getByText('No experience yet. Enable experience and retrospect a completed task.')).toBeVisible();
  await navigateAdmin(page, 'Automation');
  await navigateAdmin(page, 'Triggers');
  await initiative.getByRole('button', { name: 'Delete automation', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete automation', exact: true }).click();
  await expect(initiative.getByText('No automations yet. Create one here or ask the agent in Chat to do something later.')).toBeVisible();
  expect(errors).toEqual([]);
});
