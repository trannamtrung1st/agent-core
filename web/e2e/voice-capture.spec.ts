import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";
import { expect, test } from "@playwright/test";

async function startVoice(page: import("@playwright/test").Page): Promise<void> {
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.getByRole("button", { name: /^Voice$/ })).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: /^Voice$/ }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening…", { timeout: 15_000 });
}

test("fake-device AudioWorklet streams PCM only after Mode=voice", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await startVoice(page);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.workletLoaded() ?? false)).toBe(true);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0), { timeout: 15_000 }).toBeGreaterThan(0);

  await page.getByRole("button", { name: "End", exact: true }).click();
  await page.getByRole("dialog").filter({ hasText: "End this conversation?" })
    .getByRole("button", { name: "End", exact: true })
    .click();
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.capturePrepared() ?? true)).toBe(false);
});

test("reconnect keeps voice and requires Resume microphone before capture streams", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await startVoice(page);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0), { timeout: 15_000 }).toBeGreaterThan(0);

  await page.evaluate(() => window.__agentCore?.disconnect());
  await expect(page.getByTestId("connection")).toHaveText("Reconnecting to Agent Core…");
  await page.evaluate(() => window.__agentCore?.reconnect?.());
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.getByRole("button", { name: /^Voice$/ })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("button", { name: "Resume microphone" })).toBeVisible();
  const sentAfterReconnect = await page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0);
  expect(sentAfterReconnect).toBe(0);
  expect(await page.evaluate(() => window.__agentCore?.captureAuthorized?.() ?? true)).toBe(false);

  await page.getByRole("button", { name: "Resume microphone" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening…", { timeout: 15_000 });
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.captureStreaming() ?? false)).toBe(true);
  await expect.poll(async () => page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0), { timeout: 15_000 }).toBeGreaterThan(0);
});
