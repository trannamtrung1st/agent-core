import { expect, test, type Page } from "@playwright/test";

async function startTextSession(page: Page, text = "Hello"): Promise<void> {
  await page.getByLabel("Message").fill(text);
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

test("deletes the active chat in one confirmation without revision conflict", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i);
  const sessionUrl = page.url();
  const sessionId = sessionUrl.match(/\/c\/([0-9a-f-]{36})/i)?.[1];
  expect(sessionId).toBeTruthy();

  const row = page.locator(".session-row").first();
  await row.getByRole("button", { name: /Actions for / }).click();
  await page.getByRole("menuitem", { name: "Delete" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("button", { name: "Delete" }).click();

  await expect(page.getByText("Session changed")).not.toBeVisible({ timeout: 5_000 });
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await expect(page).not.toHaveURL(/\/c\//);
  await expect(page.getByRole("button", { name: "Stop" })).toHaveCount(0);

  const missing = await page.evaluate(async (id) => {
    const token = window.localStorage.getItem("agent-core.owner-capability");
    if (!token) {
      return 0;
    }
    const response = await fetch(`/api/v2/sessions/${id}`, {
      headers: { "X-AgentCore-Owner-Capability": token }
    });
    return response.status;
  }, sessionId);
  expect(missing).toBe(404);
});

test("session catalog orders by latest update, renames, and deletes ended sessions", async ({ page }) => {
  const stamp = Date.now().toString(36);
  const firstTitle = `First session ${stamp}`;
  const secondTitle = `Second session ${stamp}`;
  const revisedTitle = `First session revised ${stamp}`;

  await page.goto("/");
  await expect(page.getByRole("navigation", { name: "Chats" })).toBeVisible();

  await startTextSession(page, firstTitle);
  await returnToPicker(page);
  await renameFirstRow(page, firstTitle);

  await startTextSession(page, secondTitle);
  await returnToPicker(page);
  await renameFirstRow(page, secondTitle);

  const owned = page.locator(".session-row").filter({ hasText: stamp });
  await expect(owned).toHaveCount(2);
  await expect(owned.nth(0)).toContainText(secondTitle);
  await expect(owned.nth(1)).toContainText(firstTitle);

  await page.getByRole("button", { name: firstTitle, exact: true }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(owned.nth(0)).toContainText(secondTitle);
  await expect(owned.nth(1)).toContainText(firstTitle);

  await page.getByRole("button", { name: secondTitle, exact: true }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(owned.nth(0)).toContainText(secondTitle);
  await expect(owned.nth(1)).toContainText(firstTitle);

  await owned.nth(1).getByRole("button", { name: /Actions for / }).click();
  await page.getByRole("menuitem", { name: "Rename" }).click();
  await page.getByLabel("Session title").fill(revisedTitle);
  await page.getByRole("button", { name: "Save" }).click();
  await expect(owned.nth(0).getByRole("button", { name: revisedTitle, exact: true })).toBeVisible({ timeout: 15_000 });
  await expect(owned.nth(1)).toContainText(secondTitle);

  await page.getByRole("button", { name: secondTitle, exact: true }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page).toHaveURL(/\/c\//);
  const endedSessionUrl = page.url();
  await endConversation(page);
  await expect(page).toHaveURL(endedSessionUrl);
  await expect(page.locator(".conversation-scroll").getByText(secondTitle, { exact: true })).toBeVisible();
  await expect(page.locator(".conversation-scroll").getByText("Hello from synthetic.")).toBeVisible();
  await expect(page.locator(".conversation-composer [aria-label='Message']")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Send" })).toHaveCount(0);

  await page.goto(endedSessionUrl);
  await expect(page).toHaveURL(endedSessionUrl);
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".conversation-scroll").getByText("Hello from synthetic.")).toBeVisible();

  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  const endedRow = owned.filter({ hasText: secondTitle });
  await expect(endedRow).toContainText("Ended");
  await endedRow.getByRole("button", { name: secondTitle, exact: true }).click();
  await expect(page.getByText("This conversation has ended.")).toBeVisible();
  await expect(page.locator(".conversation-scroll").getByText("Hello from synthetic.")).toBeVisible();
  await expect(page.locator(".conversation-composer [aria-label='Message']")).toHaveCount(0);

  await endedRow.getByRole("button", { name: /Actions for / }).click();
  await expect(page.getByRole("menuitem", { name: "Delete" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Rename" })).toHaveCount(0);

  await page.getByRole("menuitem", { name: "Delete" }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText(`Delete “${secondTitle}”?`);
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).not.toBeVisible();
  await expect(endedRow).toBeVisible();

  await endedRow.getByRole("button", { name: /Actions for / }).click();
  await page.getByRole("menuitem", { name: "Delete" }).click();
  await dialog.getByRole("button", { name: "Delete" }).click();
  await expect(page.getByText("Session deleted.").first()).toBeVisible();
  await expect(page.getByRole("button", { name: secondTitle, exact: true })).not.toBeVisible();
  await expect(owned).toHaveCount(1);
  await expect(owned.nth(0)).toContainText(revisedTitle);
});

test("returns from an ended chat to a live session without stuck Connecting", async ({ page }) => {
  const stamp = Date.now().toString(36);
  const liveTitle = `Live session ${stamp}`;
  const endedTitle = `Ended session ${stamp}`;

  await page.goto("/");
  await startTextSession(page, liveTitle);
  await returnToPicker(page);
  await renameFirstRow(page, liveTitle);

  await startTextSession(page, endedTitle);
  await returnToPicker(page);
  await renameFirstRow(page, endedTitle);

  const liveRow = page.locator(".session-row").filter({ hasText: liveTitle });
  const endedRow = page.locator(".session-row").filter({ hasText: endedTitle });

  await endedRow.getByRole("button", { name: endedTitle, exact: true }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await endConversation(page);
  await expect(page.getByText("This conversation has ended.")).toBeVisible();

  await endedRow.getByRole("button", { name: endedTitle, exact: true }).click();
  await expect(page.getByText("This conversation has ended.")).toBeVisible();
  await expect(page.getByTestId("connection")).toHaveText("Ended", { timeout: 15_000 });

  await liveRow.getByRole("button", { name: liveTitle, exact: true }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.getByTestId("connection")).not.toHaveText("Connecting", { timeout: 1_000 });
  await expect(page.locator(".conversation-scroll").getByText(liveTitle, { exact: true })).toBeVisible();
  await expect(page.getByLabel("Message")).toBeVisible();
  await expect(page.getByRole("button", { name: "Send" })).toBeVisible();
});
