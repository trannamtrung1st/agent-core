import { expect, test, type Page } from "@playwright/test";

async function startTextSession(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Start conversation" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
}

async function returnToPicker(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("button", { name: "Start conversation" })).toBeVisible({ timeout: 15_000 });
}

async function renameFirstRow(page: Page, title: string): Promise<void> {
  const row = page.locator(".session-row").first();
  await row.getByRole("button", { name: "Rename" }).click();
  await page.getByLabel("Session title").fill(title);
  await page.getByRole("button", { name: "Save" }).click();
  await expect(row.getByRole("button", { name: title })).toBeVisible({ timeout: 15_000 });
}

test("session catalog orders by latest update, renames, and deletes ended sessions", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("navigation", { name: "Sessions" })).toBeVisible();
  await expect(page.getByText("No sessions yet.")).toBeVisible();

  await startTextSession(page);
  await returnToPicker(page);
  await renameFirstRow(page, "First session");

  await startTextSession(page);
  await returnToPicker(page);
  await renameFirstRow(page, "Second session");

  const rows = page.locator(".session-row");
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0)).toContainText("Second session");
  await expect(rows.nth(1)).toContainText("First session");

  await page.getByRole("button", { name: /First session/ }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(rows.nth(0)).toContainText("Second session");
  await expect(rows.nth(1)).toContainText("First session");

  await page.getByRole("button", { name: /Second session/ }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(rows.nth(0)).toContainText("Second session");
  await expect(rows.nth(1)).toContainText("First session");

  await rows.nth(1).getByRole("button", { name: "Rename" }).click();
  await page.getByLabel("Session title").fill("First session revised");
  await page.getByRole("button", { name: "Save" }).click();
  await expect(rows.nth(0).getByRole("button", { name: /First session revised/ })).toBeVisible({ timeout: 15_000 });
  await expect(rows.nth(1)).toContainText("Second session");

  await page.getByRole("button", { name: /Second session/ }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByRole("button", { name: "End", exact: true }).click();
  await expect(page.getByRole("button", { name: "Start conversation" })).toBeVisible({ timeout: 15_000 });

  const endedRow = rows.filter({ hasText: "Second session" });
  await expect(endedRow).toContainText("Ended");
  await expect(endedRow.getByRole("button", { name: "Rename" })).toHaveCount(0);
  await expect(endedRow.getByRole("button", { name: "Delete" })).toHaveCount(1);

  await endedRow.getByRole("button", { name: "Delete" }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText('Delete “Second session”?');
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).not.toBeVisible();
  await expect(endedRow).toBeVisible();

  await endedRow.getByRole("button", { name: "Delete" }).click();
  await dialog.getByRole("button", { name: "Delete" }).click();
  await expect(page.getByText("Session deleted.").first()).toBeVisible();
  await expect(page.getByRole("button", { name: /Second session/ })).not.toBeVisible();
  await expect(rows).toHaveCount(1);
  await expect(rows.nth(0)).toContainText("First session revised");
});
