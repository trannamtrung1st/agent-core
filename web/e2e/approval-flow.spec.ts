import { expect, test } from "@playwright/test";
import { LEGACY_IDENTITY_LABELS, selectLegacyIdentity } from "./support/legacy-identity";

test.describe.configure({ mode: "serial" });

test("sensitive approval modal approves synthetic action", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.approvalHarness);

  await page.getByLabel("Message").fill("Please run sensitive approval for the harness.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 30_000 });
  await expect(modal).toContainText("Synthetic sensitive approval");

  await modal.getByRole("button", { name: "Approve" }).click();
  await expect(modal).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText("Sensitive action completed after approval.")).toBeVisible({ timeout: 20_000 });
});

test("sensitive approval modal reject dismisses without executing", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.approvalHarness);

  await page.getByLabel("Message").fill("Need sensitive approval for reject path.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 30_000 });
  await modal.getByRole("button", { name: "Reject" }).click();
  await expect(modal).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText("Sensitive action completed after approval.")).toHaveCount(0);
});

test("sensitive approval modal approve via keyboard", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.approvalHarness);

  await page.getByLabel("Message").fill("Please run sensitive approval for the harness.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 30_000 });
  await modal.getByRole("button", { name: "Approve" }).focus();
  await page.keyboard.press("Enter");
  await expect(modal).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText("Sensitive action completed after approval.")).toBeVisible({ timeout: 20_000 });
});
