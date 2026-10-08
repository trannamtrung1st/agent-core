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
