import { expect, test } from "@playwright/test";
import { LEGACY_IDENTITY_LABELS, selectLegacyIdentity } from "./support/legacy-identity";

test("email harness approval modal approves synthetic send", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.generalAssistant);

  await page.getByLabel("Message").fill("Please run the email harness end to end.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 30_000 });
  await expect(modal).toContainText("Send email");
  await expect(modal).toContainText("recipient@example.test");
  await expect(modal).toContainText("bcc@example.test");
  await expect(modal).toContainText("Harness draft");
  await expect(modal).toContainText("Synthetic email harness send path.");

  await modal.getByRole("button", { name: "Approve" }).click();
  await expect(modal).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText("Email harness completed after approval.")).toBeVisible({ timeout: 30_000 });
});
