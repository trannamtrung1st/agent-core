import { expect, test } from "@playwright/test";

test("synthetic text conversation, pending voice, and disconnect cleanup", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("navigation", { name: "Sessions" })).toBeVisible();
  await expect(page.getByText("No sessions yet.")).toBeVisible();
  await expect(page.getByLabel("Identity")).toBeVisible();
  await page.getByRole("button", { name: "Start conversation" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

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
  await expect(page.locator(".transcript li").filter({ hasText: "Hello" }).last()).toBeVisible();
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Starting voice…", { timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Cancel" })).toBeVisible();
  const frames = await page.evaluate(() => window.__agentCore?.audioFramesSent() ?? -1);
  expect(frames).toBe(0);

  await page.evaluate(() => window.__agentCore?.disconnect());
  await expect(page.getByTestId("connection")).toHaveText("Reconnecting");
});
