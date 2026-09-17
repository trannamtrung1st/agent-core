import { expect, test, type Page } from "@playwright/test";

async function startTextSession(page: Page): Promise<void> {
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
}

async function returnToPicker(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
}

async function renameFirstRow(page: Page, title: string): Promise<void> {
  const row = page.locator(".session-row").first();
  await row.getByRole("button", { name: /Actions for / }).click();
  await page.getByRole("menuitem", { name: "Rename" }).click();
  await page.getByLabel("Session title").fill(title);
  await page.getByRole("button", { name: "Save" }).click();
  await expect(row.getByRole("button", { name: title, exact: true })).toBeVisible({ timeout: 15_000 });
}

async function endConversation(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Conversation actions" }).click();
  await page.getByRole("menuitem", { name: "End" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ended", { timeout: 15_000 });
  await expect(page.getByText("This conversation has ended.")).toBeVisible();
}

test("narrow viewport opens the chat list in a drawer", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Open chats" })).toBeVisible();
  await expect(page.getByTestId("session-rail")).toHaveCount(0);
  await page.getByRole("button", { name: "Open chats" }).click();
  await expect(page.getByRole("dialog", { name: "Chats" })).toBeVisible();
  await expect(page.getByTestId("session-rail")).toBeVisible();
  await expect(page.locator(".session-rail-heading")).not.toContainText("Chats");
});

test("session catalog orders by latest update, renames, and deletes ended sessions", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("navigation", { name: "Chats" })).toBeVisible();
  await expect(page.getByText("No chats yet.")).toBeVisible();

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

  await page.getByRole("button", { name: "First session", exact: true }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(rows.nth(0)).toContainText("Second session");
  await expect(rows.nth(1)).toContainText("First session");

  await page.getByRole("button", { name: "Second session", exact: true }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(rows.nth(0)).toContainText("Second session");
  await expect(rows.nth(1)).toContainText("First session");

  await rows.nth(1).getByRole("button", { name: /Actions for / }).click();
  await page.getByRole("menuitem", { name: "Rename" }).click();
  await page.getByLabel("Session title").fill("First session revised");
  await page.getByRole("button", { name: "Save" }).click();
  await expect(rows.nth(0).getByRole("button", { name: "First session revised", exact: true })).toBeVisible({ timeout: 15_000 });
  await expect(rows.nth(1)).toContainText("Second session");

  await page.getByRole("button", { name: "Second session", exact: true }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page).toHaveURL(/\/c\//);
  const endedSessionUrl = page.url();
  await endConversation(page);
  await expect(page).toHaveURL(endedSessionUrl);
  await expect(page.getByText("Hello", { exact: true })).toBeVisible();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible();
  await expect(page.getByLabel("Message")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Send" })).toHaveCount(0);

  await page.goto(endedSessionUrl);
  await expect(page).toHaveURL(endedSessionUrl);
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("Hello from synthetic.")).toBeVisible();

  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  const endedRow = rows.filter({ hasText: "Second session" });
  await expect(endedRow).toContainText("Ended");
  await endedRow.getByRole("button", { name: "Second session", exact: true }).click();
  await expect(page.getByText("This conversation has ended.")).toBeVisible();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible();
  await expect(page.getByLabel("Message")).toHaveCount(0);

  await endedRow.getByRole("button", { name: /Actions for / }).click();
  await expect(page.getByRole("menuitem", { name: "Delete" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Rename" })).toHaveCount(0);

  await page.getByRole("menuitem", { name: "Delete" }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText('Delete “Second session”?');
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).not.toBeVisible();
  await expect(endedRow).toBeVisible();

  await endedRow.getByRole("button", { name: /Actions for / }).click();
  await page.getByRole("menuitem", { name: "Delete" }).click();
  await dialog.getByRole("button", { name: "Delete" }).click();
  await expect(page.getByText("Session deleted.").first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Second session", exact: true })).not.toBeVisible();
  await expect(rows).toHaveCount(1);
  await expect(rows.nth(0)).toContainText("First session revised");
});
