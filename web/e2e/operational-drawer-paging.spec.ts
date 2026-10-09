import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from './support/instance-identity';
import { expect, test } from '@playwright/test';
import { backgroundFixture, cursorPage, mockBackgroundSessions } from './support/background-fixtures';

test('background Session pages retry, retain read state and remain usable on mobile', async ({ page }) => {
  test.setTimeout(60000);
  const rows = Array.from({ length: 45 }, (_, index) => backgroundFixture(index, `Review ${index}`, {
    outcome: { kind: 'NeedsAttention', summary: `Result ${index}`, outcomeEntryId: null, attentionRequired: [0,25,44].includes(index) }
  }));
  await mockBackgroundSessions(page, rows);
  let failNext = true;
  await page.route('**/background-sessions?**', route => {
    const url = new URL(route.request().url()); const cursor = url.searchParams.get('cursor');
    if (url.searchParams.get('limit') === '100') return route.fulfill({ json: cursorPage(rows, 0, 100, row => row.session.sessionId) });
    if (cursor && failNext) { failNext = false; return route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Unable to load older Sessions. Try again.' }) }); }
    const start = cursor ? rows.findIndex(row => row.session.sessionId === cursor) + 1 : 0;
    return route.fulfill({ json: cursorPage(rows, start, 20, row => row.session.sessionId) });
  });
  await page.goto('/'); await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await page.getByLabel('Message', { exact: true }).fill('hello');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await page.getByRole('button', { name: 'Background work, 3 need attention', exact: true }).click();
  let drawer = page.getByRole('dialog', { name: 'Background work', exact: true });
  await expect(drawer.locator('.background-work-item')).toHaveCount(20);
  await drawer.locator('.ant-drawer-body').evaluate(element => { element.scrollTop = element.scrollHeight; });
  await expect(drawer.getByText('Unable to load older Sessions. Try again.')).toBeVisible();
  await drawer.getByRole('button', { name: 'Try again', exact: true }).click();
  await expect(drawer.locator('.background-work-item')).toHaveCount(40);
  await drawer.locator('.ant-drawer-body').evaluate(element => { element.scrollTop = element.scrollHeight; });
  await expect(drawer.locator('.background-work-item')).toHaveCount(45);
  await drawer.locator('[data-background-session-id]').filter({ hasText: 'Review 44' }).getByRole('button', { name: 'View original result', exact: true }).click();
  drawer = page.getByRole('dialog', { name: 'Review 44', exact: true });
  await expect(drawer.getByRole('region', { name: 'Needs attention', exact: true })).toHaveText('Result 44');
  await expect(page.getByRole('button', { name: 'Background work, 2 need attention', exact: true })).toBeVisible();
  await drawer.getByRole('button', { name: 'Close', exact: true }).click();
  await page.reload(); await page.setViewportSize({ width: 390, height: 800 });
  await page.getByRole('button', { name: 'Background work, 2 need attention', exact: true }).click();
  drawer = page.getByRole('dialog', { name: 'Background work', exact: true });
  await expect(drawer.locator('.background-work-item')).toHaveCount(20);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('Admin AgentRun pages survive resizing and refresh', async ({ page }) => {
  await page.goto('/'); await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  const response = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'examiner', version: 1 } });
  const { instanceId } = await response.json();
  const runs = Array.from({ length: 45 }, (_, index) => backgroundFixture(index, `Review ${index}`).initialRun!);
  let heads = 0;
  await page.route(`**/agent-instances/${instanceId}/agent-runs?**`, route => {
    const url = new URL(route.request().url()); const before = url.searchParams.get('before'); if (!before) heads++;
    const start = before ? runs.findIndex(row => row.agentRunId === before) + 1 : 0;
    return route.fulfill({ json: cursorPage(runs, start, 20, row => row.agentRunId) });
  });
  await page.goto(`/admin/instances/${instanceId}`);
  await page.getByRole('tab', { name: 'Activity', exact: true }).click();
  await page.getByRole('tab', { name: 'Runs', exact: true }).click();
  const region = page.getByRole('region', { name: 'Runs', exact: true });
  const tableRows = region.locator('tbody tr.ant-table-row');
  await expect(tableRows).toHaveCount(20); await region.getByRole('button', { name: 'Load more', exact: true }).click();
  await expect(tableRows).toHaveCount(40); const before = heads;
  await page.setViewportSize({ width: 390, height: 800 }); expect(heads).toBe(before);
  await expect(tableRows).toHaveCount(40); await region.getByRole('button', { name: 'Load more', exact: true }).click();
  await expect(tableRows).toHaveCount(45); await expect(region.getByText('All items loaded')).toBeVisible();
});
