import { expect, test } from "@playwright/test";

test("Stop flushes live voice playback, keeps the queue, and leaves capture live", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Voice" })).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening…", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Please explain");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackConsumed() ?? 0), { timeout: 20_000 }).toBeGreaterThan(0);
  const before = await page.evaluate(() => window.__agentCore?.playbackDiagnostics?.());
  const r1 = before?.responseId;
  expect(r1).toBeTruthy();
  const epochBefore = before?.epoch ?? 0;

  await expect(page.getByRole("button", { name: "Queue" })).toBeVisible({ timeout: 15_000 });
  await page.getByLabel("Message").fill("Wait");
  await expect(page.getByRole("button", { name: "Queue" })).toBeEnabled();
  await page.getByRole("button", { name: "Queue" }).click();
  await expect(page.getByLabel("Queued messages")).toContainText("Wait", { timeout: 15_000 });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackDiagnostics?.().responseId ?? null)).toBe(r1);

  await page.getByRole("button", { name: "Stop" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackDiagnostics?.().epoch ?? 0), { timeout: 20_000 }).toBeGreaterThan(epochBefore);
  await expect(page.getByLabel("Queued messages")).toContainText("Wait");
  await expect.poll(async () => {
    const snapshot = await page.evaluate(() => window.__agentCore?.playbackDiagnostics?.());
    const ids = Object.keys(snapshot?.rendered ?? {}).filter((id) => id !== r1);
    return ids.some((id) => (snapshot?.rendered[id] ?? 0) > 0);
  }, { timeout: 5_000 }).toBe(false);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);
  await expect(page.getByTestId("connection")).toHaveText(/Speaking…|Listening…/);

  await expect(page.getByRole("button", { name: "Send" })).toBeEnabled();
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByLabel("Queued messages")).toHaveCount(0, { timeout: 15_000 });
  await expect.poll(async () => {
    const snapshot = await page.evaluate(() => window.__agentCore?.playbackDiagnostics?.());
    const ids = Object.keys(snapshot?.rendered ?? {}).filter((id) => id !== r1);
    return ids.some((id) => (snapshot?.rendered[id] ?? 0) > 0);
  }, { timeout: 25_000 }).toBe(true);
});
