import { expect, test } from '@playwright/test';

for (const width of [1440, 390]) {
  test(`archived Automation deep links preserve exact completed Run inspection at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.goto('/'); await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
    const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
    const response = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'examiner', version: 1 } });
    expect(response.ok()).toBe(true); const { instanceId } = await response.json();
    const session = await page.request.post('/api/v2/sessions', { headers, data: { agentInstanceId: instanceId, mode: 'text' } });
    expect(session.ok()).toBe(true); const { sessionId } = await session.json();
    await page.goto(`/c/${sessionId}`);
    await page.getByLabel('Message', { exact: true }).fill('Hello');
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeEnabled();
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    let runId = '';
    await expect.poll(async () => {
      const result = await page.request.get(`/api/v2/agent-instances/${instanceId}/agent-runs`, { headers });
      const runs = (await result.json()).items;
      const completed = runs.find((run: { status: string }) => run.status === 'completed');
      runId = completed?.agentRunId ?? ''; return !!completed;
    }).toBe(true);
    const base = `/admin/instances/${instanceId}`;
    await page.goto(`${base}/lifecycle`);
    await page.getByRole('button', { name: 'Archive instance', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Archive', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Unarchive instance', exact: true })).toBeVisible();
    for (const source of ['automations', 'unattended', 'automation/controls']) {
      await page.goto(`${base}/${source}?run=${runId}`);
      await expect(page).toHaveURL(new RegExp(`/activity/runs\\?run=${runId}$`));
      const drawer = page.getByRole('dialog', { name: 'Run details', exact: true });
      await expect(drawer.locator('[data-agent-run-id]')).toHaveAttribute('data-agent-run-id', runId);
      await expect(drawer).toContainText('Completed');
      await expect(page.getByRole('heading', { name: 'Runs', level: 2, exact: true })).toBeVisible();
      await drawer.getByRole('button', { name: 'Close', exact: true }).click();
      await expect(page).toHaveURL(new RegExp('/activity/runs$'));
    }
    await page.goto(`${base}/automations`);
    await expect(page).toHaveURL(/\/lifecycle\?notice=archived-automations$/);
    await expect(page.getByText('Automations are unavailable for this archived Instance', { exact: true })).toBeVisible();
  });
}
