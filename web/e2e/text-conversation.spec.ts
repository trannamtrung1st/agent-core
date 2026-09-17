import { expect, test } from "@playwright/test";

test("session path survives refresh", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i);
  const url = page.url();
  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page).toHaveURL(url);
  await expect(page.getByText("Hello")).toBeVisible();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible();
});

test("synthetic text conversation, pending voice, and disconnect cleanup", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("navigation", { name: "Chats" })).toBeVisible();
  await expect(page.getByText("No chats yet.")).toBeVisible();
  await expect(page.getByLabel("Identity")).toBeVisible();
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });

  await page.locator("input.attach-input").setInputFiles({
    name: "notes.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("hello file")
  });
  await expect(page.getByRole("button", { name: "Remove notes.txt" })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Send" })).toBeEnabled();
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("link", { name: "notes.txt" })).toBeVisible({ timeout: 15_000 });

  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".chat-message").filter({ hasText: "Hello" }).last()).toBeVisible();
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Starting voice…", { timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Cancel" })).toBeVisible();
  const frames = await page.evaluate(() => window.__agentCore?.audioFramesSent() ?? -1);
  expect(frames).toBe(0);

  await page.evaluate(() => window.__agentCore?.disconnect());
  await expect(page.getByTestId("connection")).toHaveText("Reconnecting…");
});

test("markdown response renders and survives reopen", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Show markdown");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".agent-activity")).toHaveText("Thinking…", { timeout: 15_000 });
  await expect(page.locator(".markdown-message strong")).toHaveText("three", { timeout: 15_000 });
  await expect(page.getByText("Session runtime")).toBeVisible();
  await expect(page.locator(".markdown-message code")).toHaveText("IAgentProvider");
  await expect(page.locator(".agent-activity")).toHaveCount(0);

  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await page.locator(".session-row-open").first().click();
  await expect(page.locator(".markdown-message strong")).toHaveText("three", { timeout: 15_000 });
});
