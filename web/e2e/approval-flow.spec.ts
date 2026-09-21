import { expect, test } from "@playwright/test";

test("sensitive approval modal approves synthetic action", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });

  await page.getByLabel("Message").fill("Please run sensitive approval for the harness.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 20_000 });
  await expect(modal).toContainText("Synthetic sensitive approval");

  await modal.getByRole("button", { name: "Approve" }).click();
  await expect(modal).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText("Sensitive action completed after approval.")).toBeVisible({ timeout: 20_000 });
});
