import { expect, test } from "@playwright/test";

test("fake-device AudioWorklet streams PCM only after Mode=voice", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Start conversation" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening", { timeout: 15_000 });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.workletLoaded() ?? false)).toBe(true);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0), { timeout: 15_000 }).toBeGreaterThan(0);

  await page.getByRole("button", { name: "End", exact: true }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.capturePrepared() ?? true)).toBe(false);
});

test("reconnect requires a fresh Voice click before capture streams", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Start conversation" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening", { timeout: 15_000 });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0), { timeout: 15_000 }).toBeGreaterThan(0);

  await page.evaluate(() => window.__agentCore?.disconnect());
  await expect(page.getByTestId("connection")).toHaveText("Reconnecting");
  await page.evaluate(() => window.__agentCore?.reconnect?.());
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Voice" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Mute" })).toHaveCount(0);
  const sentAfterReconnect = await page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0);
  expect(sentAfterReconnect).toBe(0);

  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening", { timeout: 15_000 });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0), { timeout: 15_000 }).toBeGreaterThan(0);
});
