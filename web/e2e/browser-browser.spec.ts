import { expect, test, type Page } from "@playwright/test";

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    window.__agentCoreSpeechTest = { fakeRecognizer: true, fakeSynthesizer: true };
  });
});

async function startVoice(page: Page): Promise<void> {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Voice" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.clientSpeechListening?.() ?? false), {
    timeout: 15_000
  }).toBe(true);
}

async function emitUtterance(page: Page, text: string): Promise<void> {
  const utteranceId = "22222222-2222-2222-2222-222222222222";
  await page.evaluate(async ({ id, spoken }) => {
    await window.__agentCore?.emitClientSpeech?.({ kind: "started", utteranceId: id, activityScore: 0.8 });
    await window.__agentCore?.emitClientSpeech?.({
      kind: "partial",
      utteranceId: id,
      revision: 1,
      text: spoken,
      activityScore: 0.8
    });
    await window.__agentCore?.emitClientSpeech?.({
      kind: "final",
      utteranceId: id,
      revision: 2,
      text: spoken,
      confidence: 0.9,
      activityScore: 0.8
    });
    await window.__agentCore?.emitClientSpeech?.({ kind: "ended", utteranceId: id, durationMs: 400, activityScore: 0.8 });
  }, { id: utteranceId, spoken: text });
}

test("Browser/Browser preflight skips PCM and fake TTS speaks segments", async ({ page }) => {
  await startVoice(page);
  const debug = await page.evaluate(() => window.__agentCore?.speechDebug?.());
  expect(debug?.sttTransport).toBe("clientTranscript");
  expect(debug?.ttsTransport).toBe("clientSpeech");
  expect(await page.evaluate(() => window.__agentCore?.getUserMediaUsed?.() ?? true)).toBe(false);
  expect(await page.evaluate(() => window.__agentCore?.audioFramesSent?.() ?? -1)).toBe(0);
  await expect(page.getByLabel("Message")).toBeEnabled();

  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.spokenClientSpeech?.().length ?? 0), {
    timeout: 15_000
  }).toBeGreaterThan(0);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.clientSpeechActive?.() === null), {
    timeout: 15_000
  }).toBe(true);
  await expect(page.getByRole("button", { name: "Stop" })).toBeHidden({ timeout: 15_000 });
});

test("Stop during fake Browser TTS keeps the queued draft", async ({ page }) => {
  await startVoice(page);
  await page.evaluate(() => window.__agentCore?.holdFakeSpeechOutput?.());
  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await expect(page.getByLabel("Queued messages")).toContainText("Hello");
  await page.getByRole("button", { name: "Stop" }).click();
  await expect(page.getByLabel("Queued messages")).toBeVisible();
  await expect(page.getByLabel("Queued messages")).toContainText("Hello");
  await expect(page.locator(".chat-message-user").filter({ hasText: "Hello" })).toHaveCount(1);
  await expect(page.getByRole("button", { name: "Send" })).toBeEnabled();
});

test("successful fake playback completion dispatches the queued head", async ({ page }) => {
  await startVoice(page);
  await page.evaluate(() => window.__agentCore?.holdFakeSpeechOutput?.());
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  await page.getByLabel("Message").fill("Queued next");
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await expect(page.getByLabel("Queued messages")).toContainText("Queued next");
  await page.evaluate(() => window.__agentCore?.releaseFakeSpeechOutput?.());
  await expect(page.locator(".chat-message-user").filter({ hasText: "Queued next" })).toBeVisible({ timeout: 20_000 });
  await expect(page.getByLabel("Queued messages")).toHaveCount(0);
});

test("barge-in speech interrupts fake Browser TTS", async ({ page }) => {
  await startVoice(page);
  await page.evaluate(() => window.__agentCore?.holdFakeSpeechOutput?.());
  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  expect(await page.evaluate(() => window.__agentCore?.clientSpeechListening?.() ?? false)).toBe(true);
  await emitUtterance(page, "wait what do you mean");
  await expect(page.getByText("Interrupted")).toBeVisible({ timeout: 15_000 });
});
