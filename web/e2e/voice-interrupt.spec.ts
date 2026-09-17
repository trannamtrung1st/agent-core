import { expect, test } from "@playwright/test";

test("superseding a live voice response flushes R1 before R2 renders and keeps capture", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Voice" })).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening…", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackConsumed() ?? 0), { timeout: 20_000 }).toBeGreaterThan(0);
  const before = await page.evaluate(() => window.__agentCore?.playbackDiagnostics?.());
  const r1 = before?.responseId;
  expect(r1).toBeTruthy();
  const epochBefore = before?.epoch ?? 0;

  await page.getByLabel("Message").fill("Wait");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackDiagnostics?.().epoch ?? 0), { timeout: 20_000 }).toBeGreaterThan(epochBefore);
  const afterFlush = await page.evaluate(() => window.__agentCore?.playbackDiagnostics?.());
  const r1AfterFlush = afterFlush?.rendered[r1 ?? ""] ?? 0;

  await expect.poll(async () => {
    const snapshot = await page.evaluate(() => window.__agentCore?.playbackDiagnostics?.());
    const ids = Object.keys(snapshot?.rendered ?? {}).filter((id) => id !== r1);
    return ids.some((id) => (snapshot?.rendered[id] ?? 0) > 0);
  }, { timeout: 25_000 }).toBe(true);

  const after = await page.evaluate(() => window.__agentCore?.playbackDiagnostics?.());
  const r2Ids = Object.keys(after?.rendered ?? {}).filter((id) => id !== r1);
  expect(r2Ids.length).toBeGreaterThan(0);
  expect(after?.rendered[r2Ids[0] ?? ""]).toBeGreaterThan(0);
  expect(after?.rendered[r1 ?? ""]).toBe(r1AfterFlush);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);
  await expect(page.getByTestId("connection")).toHaveText(/Speaking…|Listening…/);
});
