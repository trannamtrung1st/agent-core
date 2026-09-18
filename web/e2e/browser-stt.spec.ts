import { expect, test } from "@playwright/test";

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    window.__agentCoreSpeechTest = { fakeRecognizer: true };
  });
});

test("fake Browser STT listens without PCM STT and keeps the composer", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: /^Voice$/ }).click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.clientSpeechListening?.() ?? false), {
    timeout: 15_000
  }).toBe(true);
  expect(await page.evaluate(() => window.__agentCore?.getUserMediaUsed?.() ?? true)).toBe(false);
  expect(await page.evaluate(() => window.__agentCore?.audioFramesSent?.() ?? -1)).toBe(0);
  await expect(page.getByLabel("Message")).toBeEnabled();
  await expect(page.getByRole("button", { name: "Send" })).toBeVisible();

  const utteranceId = "11111111-1111-1111-1111-111111111111";
  await page.evaluate(async (id) => {
    await window.__agentCore?.emitClientSpeech?.({ kind: "started", utteranceId: id });
    await window.__agentCore?.emitClientSpeech?.({ kind: "partial", utteranceId: id, revision: 1, text: "hello there" });
    await window.__agentCore?.emitClientSpeech?.({ kind: "final", utteranceId: id, revision: 2, text: "hello there" });
    await window.__agentCore?.emitClientSpeech?.({ kind: "ended", utteranceId: id, durationMs: 400 });
  }, utteranceId);

  const debug = await page.evaluate(() => window.__agentCore?.speechDebug?.());
  expect(debug?.sttTransport).toBe("clientTranscript");
  expect(debug?.mode).toBe("voice");
  expect(debug?.error).toBeNull();
  expect(debug?.evidenceAttempts ?? 0).toBeGreaterThan(0);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.speechDebug?.()?.userTexts ?? []), {
    timeout: 15_000
  }).toEqual(expect.arrayContaining(["hello there"]));
  await expect(page.getByRole("button", { name: "Queue" })).toHaveCount(0);

  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  expect(await page.evaluate(() => window.__agentCore?.audioFramesSent?.() ?? -1)).toBe(0);
  await expect(page.getByLabel("Message")).toBeEnabled();
});
