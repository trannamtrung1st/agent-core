import { isCanceledEmailAccountPoll } from './support/request-failures';
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";
import { expect, test } from "@playwright/test";
import { waitForResponseSettled } from "./support/response-settled";

type TransportDiagnostics = {
  hub: boolean;
  connection: string;
  captureLive: boolean;
  streaming: boolean;
};

async function readTransport(page: import("@playwright/test").Page): Promise<TransportDiagnostics> {
  return page.evaluate(() => ({
    hub: window.__agentCore?.hubConnected?.() ?? false,
    connection: window.__agentCore?.sessionConnection?.() ?? "",
    captureLive: window.__agentCore?.captureLiveState?.() ?? false,
    streaming: window.__agentCore?.captureStreaming?.() ?? false
  }));
}

async function startSyntheticChat(page: import("@playwright/test").Page) {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i);
  await expect.poll(async () => (await readTransport(page)).hub).toBe(true);
  await expect.poll(async () => (await readTransport(page)).connection).toBe("ready");
}

async function openFirstInstanceEffectiveConfig(page: import("@playwright/test").Page) {
  await page.getByRole("link", { name: "Open Admin" }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByRole("heading", { name: "Admin" })).toBeVisible();
  // Internal Admin navigation keeps the underlying Chat subscription alive.
  // Only local audio capture/playback is suspended, never the live text Run.
  await expect.poll(async () => (await readTransport(page)).hub).toBe(true);
  await expect.poll(async () => (await readTransport(page)).connection).toBe("ready");
  await expect.poll(async () => (await readTransport(page)).streaming).toBe(false);
  await expect.poll(async () => (await readTransport(page)).captureLive).toBe(false);

  await page.getByRole("tab", { name: "Instances", exact: true }).click();
  const instances = page.locator('section[aria-label="Instances"]');
  await instances.getByLabel("Search instances", { exact: true }).fill("examiner");
  const instanceLink = instances.getByRole("link", { name: /examiner/i }).first();
  await expect(instanceLink).toBeVisible({ timeout: 15_000 });
  await instanceLink.click();
  await expect(page).toHaveURL(/\/admin\/instances\/[0-9a-f-]{36}$/i);
  await page.getByRole("tab", { name: "Effective configuration", exact: true }).click();
  await expect(page.getByText("Runtime model")).toBeVisible();
  await expect(page.getByText("Interruption classifier")).toBeVisible();
}

test("chat to admin effective config and back with a new turn", async ({ page }) => {
  const consoleErrors: string[] = [];
  const failedRequests: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });
  page.on("requestfailed", (request) => {
    if (isCanceledEmailAccountPoll(request)) return;
    failedRequests.push(`${request.method()} ${request.url()}`);
  });

  await startSyntheticChat(page);
  const chatUrl = page.url();

  await openFirstInstanceEffectiveConfig(page);
  await expect(page.getByText("Agent Instance", { exact: true }).first()).toBeVisible();
  await expect(page.getByRole("region", { name: "Effective configuration" })).toBeVisible();
  await expect(page.getByRole("region", { name: "Automation", exact: true })).toBeVisible();

  await page.getByRole("link", { name: "Return to last chat" }).click();
  await expect(page).toHaveURL(chatUrl);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect.poll(async () => (await readTransport(page)).hub).toBe(true);
  await expect.poll(async () => (await readTransport(page)).captureLive).toBe(false);

  await page.getByLabel("Message").fill("Second turn");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.").first()).toBeVisible();
  await expect(page.locator(".conversation-scroll").getByText("Second turn", { exact: true })).toBeVisible();

  expect(failedRequests.filter((item) => !item.includes("favicon"))).toEqual([]);
  expect(consoleErrors.filter((line) => !line.includes("[antd: List]"))).toEqual([]);
});

test("admin effective config at narrow width", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await startSyntheticChat(page);
  await openFirstInstanceEffectiveConfig(page);
  await expect(page.getByText("Harness references")).toBeVisible();
  await page.getByRole("link", { name: "Return to last chat" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect.poll(async () => (await readTransport(page)).captureLive).toBe(false);
});

for (const returnWhileLive of [true, false]) {
  test(`Admin preserves a streamed text Run when returning ${returnWhileLive ? "during output" : "after completion"}`, async ({ page }) => {
    await page.goto("/");
    await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
    await page.getByLabel("Message").fill("[test:durable-stream] Continue across Admin");
    await page.getByRole("button", { name: "Send", exact: true }).click();
    await expect(page.locator(".chat-message-assistant .assistant-body")).toHaveText("Hello", { timeout: 15_000 });
    const before = await page.evaluate(() => window.__agentCore?.conversationExecution?.());
    const chatUrl = page.url();
    expect(before?.executionId).toBeTruthy();
    expect(before?.responseId).toBeTruthy();
    await page.getByRole("link", { name: "Open Admin" }).click();
    await expect(page).toHaveURL(/\/admin$/);
    expect(await readTransport(page)).toEqual({ hub: true, connection: "ready", captureLive: false, streaming: false });
    expect(await page.evaluate(() => window.__agentCore?.conversationExecution?.())).toEqual(before);
    if (!returnWhileLive) {
      await expect.poll(() => page.evaluate(() => window.__agentCore?.conversationExecution?.().responseId), { timeout: 20_000 }).toBeNull();
    }
    await page.getByRole("link", { name: "Return to last chat" }).click();
    await expect(page).toHaveURL(chatUrl);
    if (returnWhileLive) {
      const after = await page.evaluate(() => window.__agentCore?.conversationExecution?.());
      expect(after?.executionId).toBe(before?.executionId);
      expect(after?.responseId).toBe(before?.responseId);
    }
    await expect(page.locator(".chat-message-assistant .assistant-body")).toHaveText("Hello from synthetic.", { timeout: 20_000 });
    await waitForResponseSettled(page);
    await page.reload();
    await expect(page.locator(".chat-message-assistant .assistant-body")).toHaveText("Hello from synthetic.");
    await expect(page.locator(".chat-message-assistant")).toHaveCount(1);
  });
}

test("Admin leaves Voice and terminalizes playback without reconnecting", async ({ page }) => {
  await startSyntheticChat(page);
  await page.getByRole("button", { name: "Voice", exact: true }).click();
  await expect.poll(async () => (await readTransport(page)).streaming).toBe(true);
  await page.getByLabel("Message").fill("[test:durable-stream] Voice across Admin");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect.poll(() => page.evaluate(() => window.__agentCore?.conversationExecution?.().responseId)).toBeTruthy();
  await page.getByRole("link", { name: "Open Admin" }).click();
  await expect.poll(() => page.evaluate(() => window.__agentCore?.conversationExecution?.().responseId), { timeout: 15_000 }).toBeNull();
  expect(await readTransport(page)).toEqual({ hub: true, connection: "ready", captureLive: false, streaming: false });
  await page.getByRole("link", { name: "Return to last chat" }).click();
  await expect(page.getByRole("button", { name: "Voice", exact: true })).toHaveAttribute("aria-pressed", "false");
  await waitForResponseSettled(page);
  await page.getByLabel("Message").fill("Continue in text");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".chat-message-assistant").last()).toContainText("Hello from synthetic.");
  await waitForResponseSettled(page);
});
