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
  await drawer.getByRole('button', { name: 'View history', exact: true }).first().click();
  drawer = page.getByRole('dialog', { name: 'Oven check', exact: true });
  await expect(drawer.getByRole('region', { name: 'Response', exact: true })).toContainText('Oven timer finished.');
  await expect(page.getByRole('region', { name: 'Conversation' })).not.toContainText('Oven timer finished.');
  await page.reload(); await opener.click();
  await page.getByRole('dialog', { name: 'Background work', exact: true }).getByRole('button', { name: 'Order review', exact: true }).click();
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
