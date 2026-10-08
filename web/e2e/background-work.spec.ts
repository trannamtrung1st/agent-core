import { expect, test } from '@playwright/test';
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from './support/instance-identity';
import { approvalFixture, backgroundFixture, mockBackgroundSessions } from './support/background-fixtures';

test('background Session outcomes and exact approvals remain separate from the chat transcript', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto('/'); await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  await page.getByLabel('Message').fill('hello'); await page.getByRole('button', { name: 'Send', exact: true }).click();
  const opener = page.getByRole('button', { name: 'Background work', exact: true }); await opener.click();
  let drawer = page.getByRole('dialog', { name: 'Background work', exact: true });
  await expect(drawer.getByText('No background Sessions yet')).toBeVisible();
  await drawer.getByRole('button', { name: 'Close', exact: true }).click();
  await mockBackgroundSessions(page, [backgroundFixture(10, 'Oven check', {
    outcome: { kind: 'Response', summary: 'Oven timer finished.', outcomeEntryId: 'result', attentionRequired: false }
  }), backgroundFixture(11, 'Order review', { status: 'needsApproval', outcome: null, approval: approvalFixture, cancellationAvailable: true })]);
  await opener.focus(); await page.keyboard.press('Enter');
  await drawer.getByRole('button', { name: 'View original result', exact: true }).first().click();
  drawer = page.getByRole('dialog', { name: 'Oven check', exact: true });
  await expect(drawer.getByRole('region', { name: 'Response', exact: true })).toContainText('Oven timer finished.');
  await expect(page.getByRole('region', { name: 'Conversation' })).not.toContainText('Oven timer finished.');
  await page.reload(); await opener.click();
  await page.getByRole('dialog', { name: 'Background work', exact: true }).locator('[data-background-session-id]').filter({ hasText: 'Order review' }).getByRole('button', { name: 'View original result', exact: true }).click();
  drawer = page.getByRole('dialog', { name: 'Order review', exact: true });
  await page.setViewportSize({ width: 390, height: 800 });
  await expect(page.getByRole('navigation', { name: 'Chats' })).toBeHidden();
  await expect(drawer.getByRole('region', { name: 'Action awaiting approval' })).toContainText(approvalFixture!.preview);
  await expect(drawer).not.toContainText(/SECRET_BODY|checkpoint/);
  await drawer.getByRole('button', { name: 'Approve action', exact: true }).focus();
  await page.keyboard.press('Enter');
  await page.getByRole('dialog', { name: 'Approve this exact action?' }).getByRole('button', { name: 'Approve action', exact: true }).click();
  await expect(drawer.getByText('Queued', { exact: true })).toBeVisible(); expect(errors).toEqual([]);
});

test('background summary previews files and navigates to the exact source Automation', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto('/'); await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  await page.getByLabel('Message').fill('hello'); await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(page).toHaveURL(/\/c\//);
  const token = (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))!;
  const headers = { 'X-AgentCore-Owner-Capability': token };
  const sessionId = new URL(page.url()).pathname.split('/c/')[1];
  const session = await (await page.request.get(`/api/v2/sessions/${sessionId}`, { headers })).json();
  const instanceId = session.agentInstanceId;
  const created = await page.request.post(`/api/v2/admin/agent-instances/${instanceId}/automations`, { headers, data: {
    executionTarget: { kind: "backgroundSession" }, completionDelivery: { kind: "none" },
    expectedRevision: 0, enabled: true, name: 'Exact background source', instructions: 'Review only, then finish quietly.',
    trigger: { kind: 'schedule', schedule: { kind: 'oneShot', timeZone: 'UTC', atUtc: new Date(Date.now() + 86400000).toISOString() } }
  } });
  expect(created.ok(), await created.text()).toBe(true);
  const automationId = (await created.json()).automationId;
  const row = backgroundFixture(12, 'Source review');
  row.origin = { ...row.origin, kind: 'AutomationOccurrence', automationId };
  row.artifactCount = 50; row.artifactCountHasMore = true;
  await mockBackgroundSessions(page, [row]);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Background work', exact: true }).click();
  const drawer = page.getByRole('dialog', { name: 'Background work', exact: true });
  await expect(drawer.getByText('50+ files', { exact: true })).toBeVisible();
  await expect(drawer.getByText('The background check finished.')).toBeVisible();
  await drawer.getByRole('link', { name: 'View Automation', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/admin/instances/${instanceId}/automation/automations\\?automation=${automationId}`));
  const source = page.getByRole('region', { name: 'Automation details', exact: true });
  await expect(source).toContainText('Review only, then finish quietly.');
  await expect(page.getByRole('button', { name: 'View automation: Exact background source', exact: true })).toHaveAttribute('aria-expanded', 'true');
  await page.reload(); await expect(source).toContainText('Review only, then finish quietly.');
  expect(errors).toEqual([]);
});

test('mark all as read includes unloaded pages, persists, and preserves later attention', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto('/'); await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  await page.getByLabel('Message').fill('hello'); await page.getByRole('button', { name: 'Send', exact: true }).click();
  const rows = Array.from({ length: 101 }, (_, i) => backgroundFixture(100 + i, `Attention result ${i}`, {
    outcome: { kind: 'Response', summary: 'Review this result.', outcomeEntryId: 'result', attentionRequired: true }
  }));
  await mockBackgroundSessions(page, rows);
  const opener = page.locator('.chat-header-background-work');
  await expect(opener).toHaveAttribute('aria-label', 'Background work, 101 need attention');
  let failBulkPage = true;
  await page.route('**/background-sessions?limit=100&cursor=**', route => failBulkPage
    ? route.fulfill({ status: 503, json: { message: 'Bulk page temporarily unavailable' } }) : route.fallback());
  await opener.click();
  const drawer = page.getByRole('dialog', { name: 'Background work', exact: true });
  await expect(drawer.getByText('Unread · needs attention').first()).toBeVisible();
  await drawer.getByRole('button', { name: 'Mark all as read', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(drawer.getByRole('alert')).toBeVisible();
  expect(await page.evaluate(() => localStorage.getItem('agent-core.background-run-read'))).toBeNull();
  failBulkPage = false;
  await drawer.getByRole('button', { name: 'Retry mark all as read', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(drawer.getByRole('status')).toHaveText('Read status saved in this browser.');
  await expect(drawer.getByText('Unread · needs attention')).toHaveCount(0);
  await expect(opener).toHaveAttribute('aria-label', 'Background work');
  await expect.poll(() => page.evaluate(() => Object.keys(JSON.parse(localStorage.getItem('agent-core.background-run-read') ?? '{}')).length)).toBe(101);
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const bounds = await drawer.evaluate(element => {
      const list = element.querySelector('.background-work-list')!;
      const items = [...element.querySelectorAll('.background-work-item')];
      const first = getComputedStyle(items[0]); const last = getComputedStyle(items.at(-1)!);
      return { top: getComputedStyle(list).borderTopWidth, bottom: last.borderBottomWidth,
        firstTop: first.paddingTop, firstBottom: first.paddingBottom, lastTop: last.paddingTop,
        overflow: element.scrollWidth > element.clientWidth };
    });
    expect(bounds).toEqual({ top: '1px', bottom: '1px', firstTop: '16px', firstBottom: '16px', lastTop: '16px', overflow: false });
    await expect(drawer.getByRole('button', { name: 'Mark all as read', exact: true })).toBeVisible();
  }
  await page.reload(); await opener.click();
  await expect(drawer.getByText('Unread · needs attention')).toHaveCount(0);
  rows[0].latestRun = { ...rows[0].latestRun!, revision: rows[0].latestRun!.revision + 1 };
  await drawer.getByRole('button', { name: 'Close', exact: true }).click(); await opener.click();
  await expect(drawer.getByText('Unread · needs attention')).toHaveCount(1);
  expect(errors).toEqual([]);
});
