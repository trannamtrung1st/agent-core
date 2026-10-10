import { expect, type Page, type Locator } from "@playwright/test";

export async function openDefinitionSettings(editor: Locator, section = "Operating instructions") {
  await editor.getByRole("tab", { name: "Identity & version", exact: true }).click();
  await editor.locator(".admin-draft-view-switch").getByText("Form", { exact: true }).click();
  await editor.getByRole("tab", { name: "Settings", exact: true }).click();
  const header = editor.getByRole("button", { name: section, exact: true });
  if (await header.getAttribute("aria-expanded") === "false") await header.click();
}

export async function openDefinitionResources(editor: Locator) {
  await editor.getByRole("tab", { name: "Skills & resources", exact: true }).click();
  await editor.getByRole("tab", { name: "Resources", exact: true }).click();
}

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
  await openDefinitionSettings(editor);
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
  await openDefinitionSettings(editor);
  await expect(editor.getByLabel("System instructions")).toBeVisible({ timeout: 15_000 });
  return editor;
}
