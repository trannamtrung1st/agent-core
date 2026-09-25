import { expect, type Locator, type Page } from "@playwright/test";

async function selectAntdComboboxOption(page: Page, combobox: Locator, optionText: string) {
  await page.keyboard.press("Escape");
  await combobox.click();
  await combobox.fill(optionText);
  const option = page.locator(`.ant-select-item-option[title="${optionText}"]`).last();
  await expect(option).toBeVisible({ timeout: 15_000 });
  await option.click();
}

export async function ensureToolAllowlisted(
  page: Page,
  draftsSection: Locator,
  toolName: string
) {
  await draftsSection.getByRole("tab", { name: "Capabilities" }).click();
  const allowlist = draftsSection.getByLabel("Tool allowlist");
  const alreadySelected = await allowlist
    .locator(".ant-select-selection-item")
    .filter({ hasText: toolName })
    .count();
  if (alreadySelected > 0) {
    return;
  }

  await selectAntdComboboxOption(page, allowlist, toolName);
  const saveDraft = draftsSection.getByRole("button", { name: "Save draft" });
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
  draftsSection: Locator,
  toolName = "knowledge.retrieve",
  options?: { skipToolAllowlist?: boolean }
) {
  if (!options?.skipToolAllowlist) {
    await ensureToolAllowlisted(page, draftsSection, toolName);
  }

  await draftsSection.getByRole("tab", { name: "Test & Publish" }).click();
  const gate = draftsSection.getByLabel("Test validate and publish gate");
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
  await expect(gate.getByText("Validation (revision snapshot)")).toBeVisible({ timeout: 30_000 });
  await expect(gate.getByText(/Diff vs/)).toBeVisible({ timeout: 30_000 });

  await gate.getByRole("button", { name: "Run Synthetic" }).first().click();
  await expect(gate.getByText(/eligible to publish/i)).toBeVisible({ timeout: 30_000 });
}

export async function publishDraftFromInstructions(page: Page, draftsSection: Locator) {
  await draftsSection.getByRole("tab", { name: "Instructions" }).click();
  await draftsSection.getByRole("button", { name: "Publish…" }).click();
  const modal = page.getByRole("dialog");
  await expect(modal).toBeVisible();
  await modal.getByRole("button", { name: "Publish" }).click();
  await expect(page.getByText(/Published version \d+/)).toBeVisible({ timeout: 15_000 });
}
