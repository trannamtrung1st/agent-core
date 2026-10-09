import { expect, test } from '@playwright/test';

test('Unified Automation authoring retains admitted instructions and source focus across Runs, edits, reload and mobile', async ({ page }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', response => { if (response.status() >= 500) errors.push(`${response.status()} ${response.url()}`); });
  await page.goto('/admin');
  await page.getByRole('tab', { name: 'Instances', exact: true }).click();
  await page.getByRole('button', { name: 'New instance', exact: true }).click();
  for (const [label, text] of [['Definition', 'general-assistant'], ['Published version', 'v21']] as const) {
    const input = page.getByRole('combobox', { name: label, exact: true });
    await input.click(); await input.fill(text);
    await page.locator('.ant-select-item-option').filter({ hasText: label === 'Definition' ? 'General Assistant' : 'v21 · Built-in · Published' }).click();
  }
  await page.getByRole('button', { name: 'Create instance', exact: true }).click();
  await page.getByRole('tab', { name: 'Automation', exact: true }).click();
  const automations = page.getByRole('region', { name: 'Automations', exact: true });
  await expect(automations.getByText(/No automations yet/)).toBeVisible();
  await automations.getByRole('button', { name: 'New automation', exact: true }).click();
  await expect(automations.getByRole('button', { name: 'Create automation', exact: true })).toBeDisabled();
  await automations.getByLabel('Automation name', { exact: true }).fill('Review current orders');
  await automations.getByLabel('Automation instructions', { exact: true }).fill('Review pending store orders; do nothing if nothing needs action.');
  await automations.getByLabel('Schedule time zone', { exact: true }).fill('Asia/Ho_Chi_Minh');
  await automations.getByLabel('Schedule maximum occurrences', { exact: true }).fill('0');
  await expect(automations.getByRole('button', { name: 'Create automation', exact: true })).toBeDisabled();
  await automations.getByLabel('Schedule maximum occurrences', { exact: true }).fill('5');
  await automations.getByRole('button', { name: 'Create automation', exact: true }).click();
  const source = automations.getByRole('button', { name: 'View automation: Review current orders', exact: true });
  await expect(source).toBeVisible();
  if (await source.getAttribute('aria-expanded') !== 'true') await source.click();
  await expect(automations.getByRole('region', { name: 'Automation details', exact: true }).getByText('Admin owner', { exact: true })).toBeVisible();
  const id = new URL(page.url()).pathname.split('/')[3];
  const token = (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))!;
  const headers = { 'X-AgentCore-Owner-Capability': token };
  const path = `/api/v2/admin/agent-instances/${id}/automations`;
  const row = (await (await page.request.get(path, { headers })).json()).items[0];
  expect(row.trigger.schedule.maxOccurrences).toBe(5); expect(row.authorizationOrigin).toBe('AdminOwner');
  let calls = 0;
  await page.route(`**/agent-instances/${id}/automations/*/run`, async route => { calls++; await route.continue(); });
  const run = automations.getByRole('button', { name: 'Run automation now', exact: true });
  await run.evaluate(button => { (button as HTMLButtonElement).click(); (button as HTMLButtonElement).click(); });
  await expect(automations.getByText(/Completed · No action/).first()).toBeVisible({ timeout: 30_000 });
  expect(calls).toBe(1);
  const runLink = automations.getByRole('button', { name: 'View last run: Review current orders', exact: true });
  await runLink.click();
  const details = page.getByRole('dialog', { name: 'Run details', exact: true });
  await expect(details.getByText('No action', { exact: true }).first()).toBeVisible();
  await expect(details.locator('.agent-run-details').getByText('No action', { exact: true })).toBeVisible();
  await details.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(runLink).toBeFocused(); await runLink.click();
  const updatedName = 'Updated order review';
  const update = await page.request.put(`${path}/${row.automationId}`, { headers, data: {
    expectedRevision: row.revision, enabled: true, name: updatedName, instructions: 'Future review instructions', trigger: row.trigger,
    modelKey: row.modelKey, reasoningEffort: row.reasoningEffort, executionTarget: row.executionTarget, completionDelivery: row.completionDelivery
  } });
  expect(update.ok()).toBe(true);
  await details.getByRole('button', { name: 'View Automation', exact: true }).click();
  const updatedSource = automations.getByRole('button', { name: `View automation: ${updatedName}`, exact: true });
  await expect(updatedSource).toBeFocused();
  await page.getByRole('tab', { name: 'Activity', exact: true }).click();
  await page.getByRole('tab', { name: 'Runs', exact: true }).click(); await page.reload();
  const runs = page.getByRole('region', { name: 'Runs', exact: true });
  const admitted = (await (await page.request.get(`/api/v2/agent-instances/${id}/agent-runs`, { headers })).json()).items[0];
  const history = await page.request.get(`/api/v1/sessions/${admitted.sessionId}/messages`, { headers });
  expect(history.ok()).toBe(true);
  expect(await history.text()).toContain(row.instructions);
  expect(await history.text()).not.toContain('Future review instructions');
  const historyLink = runs.getByRole('row')
    .filter({ has: page.getByTitle(admitted.agentRunId, { exact: true }) })
    .getByRole('button', { name: 'Manual task', exact: true });
  await historyLink.click();
  await expect(details.getByText('No action', { exact: true }).first()).toBeVisible();
  await details.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(historyLink).toBeFocused();
  errors.splice(0); // The deliberate 503 is the tested recovery boundary.
  await page.getByRole('tab', { name: 'Automation', exact: true }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  if (await updatedSource.getAttribute('aria-expanded') !== 'true') await updatedSource.click();
  await automations.getByRole('button', { name: 'Edit automation', exact: true }).click();
  await automations.getByLabel('Automation instructions', { exact: true }).fill('Review current orders with a longer instruction that wraps at a narrow viewport.');
  await automations.getByRole('button', { name: 'Save automation', exact: true }).click();
  await automations.getByRole('button', { name: 'Disable automation', exact: true }).click();
  await expect(automations.getByRole('button', { name: 'Run automation now', exact: true })).toBeDisabled();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  await page.reload(); if (await updatedSource.getAttribute('aria-expanded') !== 'true') await updatedSource.click();
  await expect(automations.getByRole('button', { name: 'Run automation now', exact: true })).toBeDisabled();
  const persisted = (await (await page.request.get(path, { headers })).json()).items[0];
  expect(persisted.status).toBe('Disabled'); expect(persisted.authorizationOrigin).toBe('AdminOwner'); expect(persisted.lastAgentRunId).toBeTruthy();
  await automations.getByRole('button', { name: 'Delete automation', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete automation', exact: true }).click();
  await expect(automations.getByText(/No automations yet/)).toBeVisible();
  expect(errors).toEqual([]);
});
