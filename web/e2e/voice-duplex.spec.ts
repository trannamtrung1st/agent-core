import { expect, test } from "@playwright/test";

test("voice stays full-duplex; mute is input-only; disconnect releases capture", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Start conversation" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening", { timeout: 15_000 });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);

  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackConsumed() ?? 0), { timeout: 20_000 }).toBeGreaterThan(0);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0)).toBeGreaterThan(0);

  await page.getByRole("button", { name: "Mute" }).click();
  await expect(page.getByRole("button", { name: "Unmute" })).toBeVisible({ timeout: 10_000 });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.capturePrepared() ?? false)).toBe(true);
  let last = -1;
  let stable = 0;
  await expect.poll(async () => {
    const sent = await page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0);
    if (sent === last) {
      stable += 1;
    } else {
      last = sent;
      stable = 0;
    }
    return stable >= 4 && sent > 0;
  }, { timeout: 10_000 }).toBe(true);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackConsumed() ?? 0)).toBeGreaterThan(0);

  await page.evaluate(async () => {
    await window.__agentCore?.disconnect();
  });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.capturePrepared() ?? true)).toBe(false);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? true)).toBe(false);
});
