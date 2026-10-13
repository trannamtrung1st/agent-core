import { expandAdminSection } from "./support/admin-navigation";
import { navigateAdmin } from "./support/admin-navigation";
import { isCanceledEmailAccountPoll } from './support/request-failures';
import { expect, test } from '@playwright/test';
import { draftEditorSection, openDefinitionSettings } from './admin-draft-editor-helpers';
import { isCanceledDraftEvidenceRead } from './admin-definition-gate-helpers';

test('Definition configuration retains edits and navigation across shared sections and JSON recovery', async ({ page }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  const failures: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('requestfailed', request => {
    if (isCanceledEmailAccountPoll(request)) return;
    if (!isCanceledDraftEvidenceRead(request)) failures.push(request.url());
  });
  await page.goto('/');
  await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  await page.goto('/admin/definitions');
  await page.getByRole('button', { name: 'New definition', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New definition', exact: true });
  const id = `shared-configuration-${Date.now()}`;
  await dialog.getByLabel('Definition ID').fill(id);
  await dialog.getByRole('button', { name: 'Create draft', exact: true }).click();
  const editor = draftEditorSection(page);
  await navigateAdmin(page, 'Identity & goals');
  await editor.getByLabel('Definition name').fill('A long Definition name that must wrap without moving the configuration controls outside the viewport');
  await openDefinitionSettings(editor);
  await expect(editor.getByRole('button', { name: 'Operating instructions', exact: true })).toHaveAttribute('aria-expanded', 'true');
  await editor.getByLabel('System instructions').fill('Retain this instruction across every configuration view.');
  await openDefinitionSettings(editor, 'Conversation');
  const questionSwitch = editor.getByRole('switch', { name: 'Ask one question at a time', exact: true });
  const initialQuestion = await questionSwitch.getAttribute('aria-checked');
  await editor.getByText('Ask one question at a time', { exact: true }).click();
  await expect(questionSwitch).toHaveAttribute('aria-checked', initialQuestion === 'true' ? 'false' : 'true');
  await questionSwitch.focus();
  await page.keyboard.press('Space');
  await expect(questionSwitch).toHaveAttribute('aria-checked', initialQuestion!);
  await editor.getByLabel('Max output tokens').fill('512');
  await openDefinitionSettings(editor, 'Trigger restrictions');
  await editor.getByLabel('Minimum recurrence days').fill('0.5');
  await expect(editor.getByLabel('Minimum recurrence days')).toHaveAttribute('aria-invalid', 'true');
  await expect(editor.getByRole('button', { name: 'Save draft', exact: true })).toBeDisabled();
  await editor.getByRole('button', { name: /Trigger restrictions/ }).click();
  await expect(editor.getByText('Needs attention', { exact: true })).toBeVisible();
  await navigateAdmin(editor, 'Profile');
  await expect(editor.getByText(/Review the Trigger restrictions page/)).toBeVisible();
  await openDefinitionSettings(editor, 'Trigger restrictions');
  await editor.getByLabel('Minimum recurrence days').fill('2');
  await expect(editor.getByText('Needs attention', { exact: true })).toHaveCount(0);
  await navigateAdmin(editor, "Advanced JSON");
  const json = editor.getByRole('textbox', { name: 'Advanced JSON', exact: true });
  const valid = await json.inputValue();
  await json.fill('{');
  await navigateAdmin(editor, 'Skills & resources');
  await expect(editor.getByRole('button', { name: 'Add skill', exact: true })).toBeDisabled();
  await expect(editor.getByRole('alert').filter({ hasText: 'Advanced JSON is invalid' })).toBeVisible();
  await navigateAdmin(editor, 'Advanced JSON');
  await expect(json).toHaveValue('{');
  await json.fill(valid);
  await openDefinitionSettings(editor, 'Trigger restrictions');
  await expect(editor.getByLabel('Minimum recurrence days')).toHaveValue('2');
  await navigateAdmin(editor, 'Skills & resources');
  await expect(editor.getByText('No skills yet. A definition without skills runs with an empty skill set.')).toBeVisible();
  await navigateAdmin(editor, 'Resources');
  await expect(editor.getByText('No resources bound to this draft.')).toBeVisible();
  await editor.getByLabel('Resource logical path').fill('references/retained-input.txt');
  await navigateAdmin(editor, 'Capabilities');
  await navigateAdmin(editor, 'Resources');
  await expect(editor.getByLabel('Resource logical path')).toHaveValue('references/retained-input.txt');
  await navigateAdmin(editor, 'Instructions');
  await expect(editor.getByLabel('System instructions')).toHaveValue('Retain this instruction across every configuration view.');
  for (const width of [1440, 768, 767, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await openDefinitionSettings(editor, 'Conversation');
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
    const field = await editor.getByLabel('Max output tokens').boundingBox();
    expect(field!.width).toBeLessThanOrEqual(192);
    if (width >= 768) {
      // Read both bounds in one layout frame; textarea autosizing can settle between separate reads.
      await expect.poll(() => editor.evaluate(root => {
        const field = root.querySelector<HTMLElement>('[aria-label="Max output tokens"]')!.getBoundingClientRect();
        const toggle = root.querySelector<HTMLElement>('[aria-label="Ask one question at a time"]')!.getBoundingClientRect();
        return Math.abs(toggle.bottom - field.bottom);
      })).toBeLessThanOrEqual(1);
    }
    await openDefinitionSettings(editor);
    await editor.getByLabel('System instructions').scrollIntoViewIfNeeded();
    const instructions = await editor.getByLabel('System instructions').boundingBox();
    const instructionsSection = await editor.getByRole('region', { name: 'Instructions', exact: true }).boundingBox();
    expect(Math.abs(instructions!.width - instructionsSection!.width)).toBeLessThanOrEqual(1);
    if (width === 1440) expect(instructions!.width).toBeGreaterThan(768);
    const save = await editor.getByRole('button', { name: 'Save draft', exact: true }).boundingBox();
    expect(save!.y).toBeGreaterThanOrEqual(0);
    expect(save!.y + save!.height).toBeLessThanOrEqual(900);
  }
  await navigateAdmin(editor, 'Resources');
  await editor.getByLabel('Resource logical path').fill('');
  await navigateAdmin(editor, 'Instructions');
  await editor.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByText('Draft saved.', { exact: true })).toBeVisible();
  await page.reload();
  await navigateAdmin(page, 'Identity & goals');
  await expect(editor.getByLabel('Definition name')).toHaveValue(/A long Definition name/);
  await openDefinitionSettings(editor, 'Conversation');
  await expect(editor.getByLabel('Max output tokens')).toHaveValue('512');
  expect(errors).toEqual([]);
  expect(failures).toEqual([]);
});

test('Definition save guidance leads to the capability catalog retry and retains the candidate', async ({ page }) => {
  await page.goto('/admin/definitions');
  await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  await page.route('**/api/v2/admin/tools', route => route.fulfill({
    status: 503, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Registry temporarily unavailable.' })
  }));
  await page.getByRole('button', { name: 'New definition', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New definition', exact: true });
  await dialog.getByLabel('Definition ID').fill(`catalog-recovery-${Date.now()}`);
  await dialog.getByRole('button', { name: 'Create draft', exact: true }).click();
  const editor = draftEditorSection(page);
  await navigateAdmin(page, 'Identity & goals');
  await editor.getByLabel('Definition name').fill('Keep this name through catalog retry');
  await expect(editor.getByText('The capability catalog is unavailable. Open Capabilities and choose Retry tool registry, or use Advanced JSON.')).toBeVisible();
  await expect(editor.getByRole('button', { name: 'Save draft', exact: true })).toBeDisabled();
  await navigateAdmin(editor, 'Capabilities');
  await expect(editor.getByRole('alert')).toContainText('Registry temporarily unavailable.');
  await page.unroute('**/api/v2/admin/tools');
  await editor.getByRole('button', { name: 'Retry tool registry', exact: true }).click();
  await expect(editor.getByText(/The capability catalog is unavailable/)).toHaveCount(0);
  await navigateAdmin(editor, 'Identity & goals');
  await expect(editor.getByLabel('Definition name')).toHaveValue('Keep this name through catalog retry');
  await editor.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByText('Draft saved.', { exact: true })).toBeVisible();
});
