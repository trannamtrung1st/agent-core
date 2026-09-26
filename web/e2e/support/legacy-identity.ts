import { expect, type Page } from "@playwright/test";

/** Matches `AgentPicker` legacy compatibility option labels. */
export const LEGACY_IDENTITY_LABELS = {
  customerSupport: "Sam — Customer support representative (legacy)",
  generalAssistant: "Riley — General assistant (legacy)",
  approvalHarness: "Harper — Approval harness (legacy)"
} as const;

export async function selectLegacyIdentity(page: Page, optionLabel: string): Promise<void> {
  const combobox = page.getByRole("combobox", { name: "Identity" });
  await page.keyboard.press("Escape");
  await combobox.click();
  await combobox.fill(optionLabel);
  const option = page.locator(`.ant-select-item-option[title="${optionLabel}"]`).last();
  await expect(option).toBeVisible({ timeout: 15_000 });
  await option.click();
}
