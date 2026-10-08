import { expect, type Locator, type Page } from "@playwright/test";

async function comboboxOption(page: Page, combobox: Locator, optionText: string) {
  const listboxId = await combobox.getAttribute("aria-controls");
  expect(listboxId).toBeTruthy();
  // A closing portal can stay visible during motion. Match the actual owner,
  // rather than all visible Select portals or whichever option happens to be last.
  const dropdown = page.locator(".ant-select-dropdown").filter({
    has: page.locator(`[id="${listboxId}"]`)
  });
  return dropdown.locator(`.ant-select-item-option[title="${optionText}"]`);
}

async function selectAntdComboboxOption(page: Page, combobox: Locator, optionText: string) {
  await page.keyboard.press("Escape");
  await combobox.click();
  await combobox.fill(optionText);
  const option = await comboboxOption(page, combobox, optionText);
  await expect(option).toBeVisible({ timeout: 15_000 });
  await option.click();
}

export async function ensureToolAllowlisted(
  page: Page,
  draftEditor: Locator,
  toolName: string
) {
  await draftEditor.getByRole("tab", { name: "Capabilities" }).click();
  const allowlist = draftEditor.getByLabel("Authorized capabilities");
  await expect(allowlist).toBeVisible();
  // The tool-offered fixture requires immediate projection as well as authority.
  // Responsive Select hides tags, so inspect each option's selected state.
  for (const selector of [allowlist, draftEditor.getByLabel("Always projected capabilities")]) {
    await page.keyboard.press("Escape");
    await selector.click();
    await selector.fill(toolName);
    const option = await comboboxOption(page, selector, toolName);
    await expect(option).toBeVisible();
    if (await option.getAttribute("aria-selected") !== "true"
        && !(await option.getAttribute("class"))?.includes("ant-select-item-option-selected")) await option.click();
    await page.keyboard.press("Escape");
  }

  const saveDraft = draftEditor.getByRole("button", { name: "Save draft" });
  if (await saveDraft.isDisabled()) return;
  await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === "PUT"
        && response.url().includes("/api/v2/admin/definition-drafts/")
        && response.ok()
    ),
    saveDraft.click()
  ]);
}

export async function completeDefinitionDraftPublishGate(
  page: Page,
  draftEditor: Locator,
  toolName = "knowledge.retrieve",
  options?: { skipToolAllowlist?: boolean }
) {
  if (!options?.skipToolAllowlist) {
    await ensureToolAllowlisted(page, draftEditor, toolName);
  }

  await draftEditor.getByRole("tab", { name: "Test & Publish" }).click();
  const gate = draftEditor.getByLabel("Test validate and publish gate");
  await expect(gate.getByRole("button", { name: "Run validation" })).toBeVisible({ timeout: 15_000 });

  if (await gate.getByText("No evaluation scenarios yet.").isVisible()) {
    const toolSelect = gate.getByLabel("Evaluation tool name");
    await expect(toolSelect).toBeVisible();
    await selectAntdComboboxOption(page, toolSelect, toolName);
    await page.keyboard.press("Escape");
    await gate.getByRole("button", { name: "Save required scenario" }).click();
    await expect(gate.getByText("No evaluation scenarios yet.")).toBeHidden({ timeout: 15_000 });
  }

  await gate.getByRole("button", { name: "Run validation" }).click();
  await expect(gate.getByText("Validation snapshot")).toBeVisible({ timeout: 30_000 });
  await expect(gate.getByText(/Diff vs/)).toBeVisible({ timeout: 30_000 });

  await gate.getByRole("button", { name: "Run Synthetic" }).first().click();
  await expect(gate.getByText("Draft is ready for final publish.")).toBeVisible({ timeout: 30_000 });
}

export async function publishDraftFromInstructions(page: Page, draftEditor: Locator) {
  await draftEditor.getByRole("tab", { name: "Definition" }).click();
  await draftEditor.getByRole("button", { name: "Publish…" }).click();
  const modal = page.getByRole("dialog");
  await expect(modal).toBeVisible();
  await modal.getByRole("button", { name: "Publish" }).click();
  await expect(page.getByText(/Published version \d+/)).toBeVisible({ timeout: 15_000 });
}
