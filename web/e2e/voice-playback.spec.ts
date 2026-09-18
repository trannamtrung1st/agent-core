import { expect, test } from "@playwright/test";

test("voice assistant text survives refresh", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening…", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".chat-message-assistant").nth(1)).toContainText("Hello from synthetic.", {
    timeout: 25_000
  });
  await expect.poll(async () => {
    return page.locator(".chat-message-assistant").nth(1).locator(".assistant-body").evaluate((node) => node.textContent?.length ?? 0);
  }, { timeout: 10_000 }).toBeGreaterThan(0);
  await page.waitForTimeout(1_000);
  const url = page.url();

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page).toHaveURL(url);
  await expect(page.locator(".chat-message-assistant")).toHaveCount(2);
  await expect(page.locator(".chat-message-assistant").nth(1)).toContainText("Hello from synthetic.");
  await expect(page.locator(".chat-message-assistant").nth(1).locator(".assistant-body")).toBeVisible();
});

test("output worklet acknowledges playback while capture stays active", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Voice" })).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening…", { timeout: 15_000 });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.outputWorkletLoaded() ?? false)).toBe(true);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);

  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioOutputsReceived() ?? 0), { timeout: 15_000 }).toBeGreaterThan(0);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackConsumed() ?? 0), { timeout: 20_000 }).toBeGreaterThan(0);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0)).toBeGreaterThan(0);
  const audioElement = await page.locator("audio").count();
  expect(audioElement).toBe(0);
});

test("two completed voice turns reset worklet identity and consume each response independently", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Voice" })).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening…", { timeout: 15_000 });

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

test("disconnect during playback then reconnect plays a new response", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Voice" })).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening…", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackConsumed() ?? 0), { timeout: 20_000 }).toBeGreaterThan(0);
  await page.evaluate(() => window.__agentCore?.disconnect());
  await expect(page.getByTestId("connection")).toHaveText("Reconnecting to Agent Core…");
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.flushing?.() ?? false)).toBe(false);
  await page.evaluate(() => window.__agentCore?.reconnect?.());
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening…", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.playbackConsumed() ?? 0), { timeout: 20_000 }).toBeGreaterThan(0);
});
