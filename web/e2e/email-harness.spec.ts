import { expect, test } from "@playwright/test";

test("email harness approval modal approves synthetic send", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });

  await page.getByLabel("Message").fill("Please run the email harness end to end.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 30_000 });
  await expect(modal).toContainText("Send email");

  await modal.getByRole("button", { name: "Approve" }).click();
  await expect(modal).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText("Email harness completed after approval.")).toBeVisible({ timeout: 30_000 });
});
