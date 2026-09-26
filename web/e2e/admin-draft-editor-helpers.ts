import { expect, type Page } from "@playwright/test";

/** Draft list, fork controls, and durable publications (editor closed). */
export function definitionDraftsSection(page: Page) {
  return page.locator('section[aria-label="Definition drafts"]');
}

/** Focused draft workspace (instructions, tabs, save/publish). */
export function draftEditorSection(page: Page) {
  return page.locator('section[aria-label="Draft editor"]');
}

async function selectBaseVersion(page: Page, versionPrefix: string) {
  const drafts = definitionDraftsSection(page);
  const select = drafts.getByLabel("Base version");
  await expect(select).toBeVisible({ timeout: 15_000 });
  await select.click();
  await page.locator(".ant-select-item-option").filter({ hasText: versionPrefix }).first().click();
}

export async function forkBuiltInV1Draft(page: Page) {
  await expect(definitionDraftsSection(page)).toBeVisible({ timeout: 15_000 });
  await selectBaseVersion(page, "v1 · Built-in");
  await definitionDraftsSection(page).getByRole("button", { name: /Fork v1 \(builtIn\)/ }).click();
  const editor = draftEditorSection(page);
  await expect(editor.getByLabel("System instructions")).toBeVisible({ timeout: 15_000 });
  return editor;
}

export async function forkDurablePublicationDraft(page: Page, version: number) {
  await expect(definitionDraftsSection(page)).toBeVisible({ timeout: 15_000 });
  await selectBaseVersion(page, `v${version} · Durable`);
  await definitionDraftsSection(page)
    .getByRole("button", { name: `Fork v${version} (durable)` })
    .click();
  const editor = draftEditorSection(page);
  await expect(editor.getByLabel("System instructions")).toBeVisible({ timeout: 15_000 });
  return editor;
}
