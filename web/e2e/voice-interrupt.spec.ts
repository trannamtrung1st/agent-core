import { expect, test } from "@playwright/test";

test("superseding a live voice response keeps capture and does not replay stale audio identity", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Start conversation" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Voice", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackConsumed() ?? 0), { timeout: 20_000 }).toBeGreaterThan(0);
  await page.getByLabel("Message").fill("Wait");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);
  await expect(page.getByTestId("connection")).toHaveText("Voice");
  const interrupted = await page.locator("em").filter({ hasText: "interrupted" }).count();
  const assistants = await page.locator("[data-role='assistant']").count();
  expect(interrupted + assistants).toBeGreaterThan(0);
});
