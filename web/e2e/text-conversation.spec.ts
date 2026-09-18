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

test("queued send and Stop keep the local queue without starting R2", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await expect(page.getByLabel("Queued messages")).toBeVisible();
  await expect(page.getByLabel("Queued messages")).toContainText("Hello");
  await expect(page.locator(".chat-message-user").filter({ hasText: "Hello" })).toHaveCount(0);
  await page.getByRole("button", { name: "Stop" }).click();
  await expect(page.getByLabel("Queued messages")).toBeVisible();
  await expect(page.locator(".chat-message-user").filter({ hasText: "Hello" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Send" })).toBeEnabled();
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".chat-message-user").filter({ hasText: "Hello" })).toBeVisible({ timeout: 15_000 });
});

test("Steer sends the queue head and auto-dispatch follows completion", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  await page.getByRole("textbox", { name: "Message" }).fill("Alpha");
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await page.getByRole("textbox", { name: "Message" }).fill("Beta");
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await expect(page.getByLabel("Queued messages")).toBeVisible();
  await expect(page.locator(".chat-message-user").filter({ hasText: "Alpha" })).toHaveCount(0);
  await expect(page.locator(".chat-message-user").filter({ hasText: "Beta" })).toHaveCount(0);
  await page.getByRole("button", { name: "Steer queued message 1" }).click();
  await expect(page.locator(".chat-message-user").filter({ hasText: "Alpha" })).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".chat-message-user").filter({ hasText: "Beta" })).toBeVisible({ timeout: 25_000 });
  await expect(page.locator(".chat-message-assistant").filter({ hasText: "Hello from synthetic." })).toHaveCount(2, {
    timeout: 25_000
  });
});

test("queued attachment stays with the queued item until dispatch", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  await page.locator("input.attach-input").setInputFiles({
    name: "queued.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("queued file")
  });
  await expect(page.getByRole("button", { name: "Remove queued.txt" })).toBeVisible({ timeout: 15_000 });
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await expect(page.getByLabel("Queued messages")).toContainText("queued.txt");
  await expect(page.getByRole("link", { name: "queued.txt" })).toHaveCount(0);
  await page.getByRole("button", { name: "Steer queued message 1" }).click();
  await expect(page.getByRole("link", { name: "queued.txt" })).toBeVisible({ timeout: 15_000 });
});

test("manual pause via deactivate shows Resume and keeps history", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  const deactivated = await page.evaluate(async () => {
    const token = window.localStorage.getItem("agent-core.owner-capability");
    const match = window.location.pathname.match(/\/c\/([0-9a-f-]{36})/i);
    if (!token || !match) {
      return { ok: false, status: 0 };
    }
    const response = await fetch(`/api/v2/sessions/${match[1]}/deactivate`, {
      method: "POST",
      headers: { "X-AgentCore-Owner-Capability": token }
    });
    return { ok: response.ok, status: response.status };
  });
  expect(deactivated.ok).toBe(true);
  await expect(page.getByRole("button", { name: "Resume" })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId("connection")).toHaveText("Paused");
  await expect(page.getByText("Hello from synthetic.")).toBeVisible();
  await page.getByRole("button", { name: "Resume" }).click();
  await expect(page.getByLabel("Message")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Send" })).toBeVisible();
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

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.locator(".markdown-message strong")).toHaveText("three", { timeout: 15_000 });
  await expect(page.locator(".agent-activity")).toHaveCount(0);

  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await page.locator(".session-row-open").first().click();
  await expect(page.locator(".markdown-message strong")).toHaveText("three", { timeout: 15_000 });
});
