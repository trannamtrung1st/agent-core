import { expect, test, type Page } from '@playwright/test';
import { completeDefinitionDraftPublishGate, publishDraftFromInstructions } from './admin-definition-gate-helpers';
import { draftEditorSection, forkDurablePublicationDraft } from './admin-draft-editor-helpers';

async function choose(page: Page, name: string, text: string) {
  const input = page.getByRole('combobox', { name, exact: true });
  await input.click(); await input.fill(text);
  await page.locator('.ant-select-item-option').filter({ hasText: text }).last().click();
}
async function create(page: Page, name: string, definition = 'General Assistant · general-assistant') {
  await page.goto('/'); await page.getByRole('button', { name: 'Open Admin' }).click();
  await page.getByRole('tab', { name: 'Instances', exact: true }).click();
  await page.getByRole('button', { name: 'New instance', exact: true }).click();
  await choose(page, 'Definition', definition);
  const dialog = page.getByRole('dialog', { name: 'New instance', exact: true });
  await dialog.getByText('Custom persona', { exact: true }).click();
  await dialog.getByLabel('Persona name').fill(name); await dialog.getByLabel('Persona role').fill('Operations');
  await dialog.getByLabel('Persona description').fill('Review observed evidence.'); await dialog.getByLabel('Persona tone').fill('Clear');
  await dialog.getByRole('button', { name: 'Create instance', exact: true }).click();
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  return new URL(page.url()).pathname.split('/')[3];
}
async function chat(page: Page, name: string) {
  await page.locator('.admin-header').getByRole('button', { name: /Chat$/ }).first().click();
  await choose(page, 'Identity', name);
}
async function send(page: Page, text: string) {
  await page.getByLabel('Message', { exact: true }).fill(text);
  await page.getByRole('button', { name: 'Send', exact: true }).click();
}

test('Instance Skills: authorized Chat creation, next-turn load, owner editor and explicit customization', async ({ page }) => {
  test.setTimeout(120_000);
  const errors: string[] = []; const failed: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('requestfailed', r => failed.push(r.url()));
  const name = `Skills ${Date.now()}`; const id = await create(page, name);
  await chat(page, name); await send(page, 'Learn this accounting Skill.');
  await expect(page.getByText('Saved the accounting Skill. It applies to the next execution.', { exact: true })).toBeVisible();
  await send(page, 'Use my accounting Skill.');
  await expect(page.getByText('Accounting procedure loaded from the pinned Instance Skill.', { exact: true })).toBeVisible();
  const sessionUrl = page.url();
  await page.getByRole('button', { name: 'Open Admin' }).click(); await page.goto(`/admin/instances/${id}/skills`);
  const local = page.getByRole('region', { name: 'Instance Skills', exact: true });
  const definition = page.getByRole('region', { name: 'Definition Skills', exact: true });
  await expect(local.getByText('Accounting', { exact: true })).toBeVisible();
  await local.getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByLabel('Procedure', { exact: true })).toHaveValue(/ACCOUNTING_PROCEDURE/);
  await page.getByLabel('Procedure', { exact: true }).fill('ACCOUNTING_PROCEDURE: UPDATED pinned procedure.');
  await page.getByRole('button', { name: 'Save Skill', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Instance Skill editor', exact: true })).toBeHidden();
  await definition.getByRole('button', { name: 'Inspect', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Definition Skill content', exact: true })).toContainText('Search the trusted fixture');
  await page.getByRole('button', { name: 'Close inspection' }).click();
  await definition.getByRole('button', { name: 'Customize', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('independent Instance Skill');
  await page.getByRole('dialog').getByRole('button', { name: 'Customize', exact: true }).click();
  await expect(page.getByRole('switch', { name: 'Enable Definition Skill Record lookup', exact: true })).not.toBeChecked();
  await expect(local.getByText('Copied from general-assistant v16 · browser.record.lookup', { exact: true })).toBeVisible();
  await page.getByRole('switch', { name: 'Enable Instance Skill Accounting', exact: true }).click();
  await expect(page.getByRole('switch', { name: 'Enable Instance Skill Accounting', exact: true })).not.toBeChecked();
  await page.reload(); await expect(page.getByRole('switch', { name: 'Enable Instance Skill Accounting', exact: true })).not.toBeChecked();
  await page.goto(sessionUrl); await send(page, 'Use my accounting Skill.');
  await expect(page.getByText("Accounting is not present in this execution's pinned catalog.", { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Open Admin' }).click(); await page.goto(`/admin/instances/${id}/skills`);
  const accounting = local.getByRole('row').filter({ hasText: 'Review an accounting entry' });
  await accounting.getByRole('button', { name: 'Delete', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete Skill', exact: true }).click();
  await expect(local.getByText('Accounting', { exact: true })).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  expect(failed).toEqual([]); expect(errors.filter(e => !e.includes('favicon') && !e.includes('[antd:'))).toEqual([]);
});

test('A restricted Definition cannot grant itself Skill management', async ({ page }) => {
  const name = `Restricted ${Date.now()}`;
  const id = await create(page, name, 'Customer Support · customer-support');
  await chat(page, name); await send(page, 'Learn this accounting Skill.');
  await expect(page.getByText('Skill management is not authorized. Nothing was saved.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Open Admin' }).click(); await page.goto(`/admin/instances/${id}/skills`);
  await expect(page.getByRole('region', { name: 'Instance Skills', exact: true }).getByText('Accounting', { exact: true })).toHaveCount(0);
});

test('Definition upgrade and rollback preserve disabled choices and independent Instance content', async ({ page }) => {
  test.setTimeout(180_000);
  const definitionId = `instance-upgrade-${Date.now()}`;
  await page.goto('/admin'); await page.getByRole('button', { name: 'New definition', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New definition' });
  await dialog.getByLabel('Definition ID').fill(definitionId); await dialog.getByRole('button', { name: 'Create draft' }).click();
  let editor = draftEditorSection(page);
  await editor.getByRole('button', { name: 'Add skill' }).click();
  await editor.getByLabel('Skill 1 id').fill('review'); await editor.getByLabel('Skill 1 name').fill('Review');
  await editor.getByLabel('Skill 1 description').fill('Reusable review'); await editor.getByLabel('Skill 1 procedure').fill('ORIGINAL_DEFINITION');
  await editor.getByRole('button', { name: 'Save draft' }).click(); await expect(page.getByText('Draft saved.')).toBeVisible();
  await completeDefinitionDraftPublishGate(page, editor); await publishDraftFromInstructions(page, editor);
  const id = await create(page, `Upgrade ${Date.now()}`, definitionId);
  await page.getByRole('tab', { name: 'Skills', exact: true }).click();
  await page.getByRole('switch', { name: 'Enable Definition Skill Review', exact: true }).click();
  await expect(page.getByRole('switch', { name: 'Enable Definition Skill Review', exact: true })).not.toBeChecked();
  await page.getByRole('button', { name: 'New Instance Skill' }).click();
  await page.getByLabel('Skill name', { exact: true }).fill('Review'); await page.getByLabel('Description', { exact: true }).fill('Independent review');
  await page.getByLabel('Procedure', { exact: true }).fill('LOCAL_INDEPENDENT'); await page.getByRole('button', { name: 'Save Skill' }).click();
  await expect(page.getByRole('region', { name: 'Instance Skill editor' })).toBeHidden();
  await page.goto(`/admin/definitions/${definitionId}`); editor = await forkDurablePublicationDraft(page, 1);
  await editor.getByLabel('Skill 1 procedure').fill('UPGRADED_DEFINITION'); await editor.getByRole('button', { name: 'Save draft' }).click();
  await expect(page.getByText('Draft saved.')).toBeVisible();
  await completeDefinitionDraftPublishGate(page, editor); await publishDraftFromInstructions(page, editor);
  await page.goto(`/admin/instances/${id}`);
  await page.getByLabel('Target definition version').click(); await page.locator('.ant-select-item-option').filter({ hasText: /^v2\b/ }).click();
  await page.getByRole('button', { name: 'Upgrade to v2' }).click(); await expect(page.getByText('Active version set to v2.')).toBeVisible();
  await page.getByRole('tab', { name: 'Skills', exact: true }).click();
  const reusable = page.getByRole('region', { name: 'Definition Skills', exact: true }); const local = page.getByRole('region', { name: 'Instance Skills', exact: true });
  await expect(reusable).toContainText('Version 2'); await expect(reusable.getByRole('switch')).not.toBeChecked();
  await reusable.getByRole('button', { name: 'Inspect' }).click(); await expect(page.getByRole('region', { name: 'Definition Skill content' })).toContainText('UPGRADED_DEFINITION');
  await page.getByRole('button', { name: 'Close inspection' }).click();
  await local.getByRole('button', { name: 'Edit' }).click(); await expect(page.getByLabel('Procedure', { exact: true })).toHaveValue('LOCAL_INDEPENDENT');
  await page.getByRole('button', { name: 'Cancel editing' }).click();
  await page.getByRole('tab', { name: 'Identity & version', exact: true }).click(); await page.getByLabel('Target definition version').click();
  await page.locator('.ant-select-item-option').filter({ hasText: /^v1\b/ }).click(); await page.getByRole('button', { name: 'Rollback to v1' }).click();
  await expect(page.getByText('Active version set to v1.')).toBeVisible(); await page.getByRole('tab', { name: 'Skills', exact: true }).click();
  await expect(reusable.getByRole('switch')).not.toBeChecked(); await reusable.getByRole('button', { name: 'Inspect' }).click();
  await expect(page.getByRole('region', { name: 'Definition Skill content' })).toContainText('ORIGINAL_DEFINITION');
});
