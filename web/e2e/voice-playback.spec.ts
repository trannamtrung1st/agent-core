import { expect, test } from "@playwright/test";

test("output worklet acknowledges playback while capture stays active", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Start conversation" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Voice", { timeout: 15_000 });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.outputWorkletLoaded() ?? false)).toBe(true);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);

  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText(/synthetic/i)).toBeVisible({ timeout: 15_000 });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioOutputsReceived() ?? 0), { timeout: 15_000 }).toBeGreaterThan(0);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackConsumed() ?? 0), { timeout: 20_000 }).toBeGreaterThan(0);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0)).toBeGreaterThan(0);
  const audioElement = await page.locator("audio").count();
  expect(audioElement).toBe(0);
});

test("two completed voice turns reset worklet identity and consume each response independently", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Start conversation" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Voice", { timeout: 15_000 });

  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackDiagnostics?.().completedResponses.length ?? 0), {
    timeout: 25_000
  }).toBeGreaterThan(0);
  const first = await page.evaluate(() => window.__agentCore?.playbackDiagnostics?.());
  expect(first?.completedResponses[0]).toBeTruthy();
  expect(first?.rendered[first.completedResponses[0] ?? ""]).toBeGreaterThan(0);

  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackDiagnostics?.().completedResponses.length ?? 0), {
    timeout: 25_000
  }).toBeGreaterThanOrEqual(2);
  const second = await page.evaluate(() => window.__agentCore?.playbackDiagnostics?.());
  expect(second?.completedResponses[0]).not.toBe(second?.completedResponses[1]);
  expect(second?.rendered[second.completedResponses[1] ?? ""]).toBeGreaterThan(0);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);
});
