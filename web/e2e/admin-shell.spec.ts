import { expandAdminSection } from "./support/admin-navigation";
import { navigateAdmin } from "./support/admin-navigation";
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
  await expect(page).toHaveURL(/\/admin\/instances$/);
  await expect(page.getByRole("heading", { name: "Admin" })).toBeVisible();
  // Internal Admin navigation keeps the underlying Chat subscription alive.
  // Only local audio capture/playback is suspended, never the live text Run.
  await expect.poll(async () => (await readTransport(page)).hub).toBe(true);
  await expect.poll(async () => (await readTransport(page)).connection).toBe("ready");
  await expect.poll(async () => (await readTransport(page)).streaming).toBe(false);
  await expect.poll(async () => (await readTransport(page)).captureLive).toBe(false);

  await navigateAdmin(page, "Instances");
  const instances = page.locator('section[aria-label="Instances"]');
  await instances.getByLabel("Search instances", { exact: true }).fill("examiner");
  const instanceLink = instances.getByRole("link", { name: /examiner/i }).first();
  await expect(instanceLink).toBeVisible({ timeout: 15_000 });
  await instanceLink.click();
  await expect(page).toHaveURL(/\/admin\/instances\/[0-9a-f-]{36}\/overview$/i);
  await navigateAdmin(page, "Effective configuration");
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
    await expect(page).toHaveURL(/\/admin\/instances$/);
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
  await expandAdminSection(page, "Voice");
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

test("Admin Chat opens an empty root while Return preserves the live conversation", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await page.getByLabel("Message").fill("[test:durable-stream] Both Admin destinations");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".chat-message-assistant .assistant-body")).toHaveText("Hello", { timeout: 15_000 });
  const chatUrl = page.url();
  const before = await page.evaluate(() => window.__agentCore?.conversationExecution?.());
  await page.getByRole("link", { name: "Open Admin" }).click();
  await page.getByRole("link", { name: "Return to last chat" }).click();
  expect(await page.evaluate(() => window.__agentCore?.conversationExecution?.())).toEqual(before);
  await expect(page).toHaveURL(chatUrl);
  await page.getByRole("link", { name: "Open Admin" }).click();
  await page.getByRole("link", { name: /Chat$/ }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.locator(".chat-message-assistant, .chat-message-user")).toHaveCount(0);
  await expect.poll(async () => (await readTransport(page)).hub).toBe(false);
  expect(await page.evaluate(() => window.__agentCore?.conversationExecution?.().responseId)).toBeNull();
  // Clearing the surface detaches, never deletes the accepted durable Run.
  await page.goto(chatUrl);
  await expect(page.locator(".chat-message-assistant .assistant-body")).toHaveText("Hello from synthetic.", { timeout: 20_000 });
  await expect(page.locator(".chat-message-assistant")).toHaveCount(1);
});

test("unexpected hub close in Admin recovers the same streamed text Run", async ({ page }) => {
  const sockets: import("@playwright/test").WebSocketRoute[] = [];
  await page.routeWebSocket("**/hubs/session", socket => {
    socket.connectToServer();
    sockets.push(socket);
  });
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await page.getByLabel("Message").fill("[test:durable-stream] Recover while hidden");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".chat-message-assistant .assistant-body")).toHaveText("Hello", { timeout: 15_000 });
  const before = await page.evaluate(() => window.__agentCore?.conversationExecution?.());
  await page.getByRole("link", { name: "Open Admin" }).click();
  sockets.at(-1)!.close({ code: 1011, reason: "Synthetic unexpected disconnect" });
  await expect.poll(() => sockets.length).toBeGreaterThan(1);
  await expect.poll(async () => (await readTransport(page)).connection).toBe("ready");
  expect((await readTransport(page)).captureLive).toBe(false);
  await page.getByRole("link", { name: "Return to last chat" }).click();
  expect(await page.evaluate(() => window.__agentCore?.conversationExecution?.().executionId)).toBe(before?.executionId);
  await expect(page.locator(".chat-message-assistant .assistant-body")).toHaveText("Hello from synthetic.", { timeout: 20_000 });
  await waitForResponseSettled(page);
  await expect(page.locator(".chat-message-assistant")).toHaveCount(1);
});

for (const rejectionCount of [1, 2]) test(`a rejected Admin Voice mode change recovers after ${rejectionCount} rejection(s)`, async ({ page }) => {
  await startSyntheticChat(page);
  const chatUrl = page.url();
  await expandAdminSection(page, "Voice");
  await expect.poll(async () => (await readTransport(page)).streaming).toBe(true);
  await page.getByLabel("Message").fill("[test:durable-stream] Finish after failed mode change");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect.poll(() => page.evaluate(() => window.__agentCore?.conversationExecution?.().responseId)).toBeTruthy();
  // Fault-inject one hub rejection through the actual SignalR client. No server
  // debug endpoint or application test hook is needed for this failure boundary.
  await page.evaluate(async (remaining) => {
    const bundle = performance.getEntriesByType("resource").find(entry => /\/@microsoft_signalr\.js/.test(entry.name))?.name;
    if (!bundle) throw new Error("SignalR client bundle was not loaded");
    const { HubConnection } = await import(/* @vite-ignore */ bundle);
    const original = HubConnection.prototype.invoke;
    HubConnection.prototype.invoke = function(method: string, ...args: [{ payload?: { mode?: string } }]) {
      if (method === "SetMode" && args[0]?.payload?.mode === "text") {
        if (--remaining === 0) HubConnection.prototype.invoke = original;
        return Promise.resolve({ accepted: false, error: {
          category: "Session", code: "SessionBusy", message: "Synthetic mode change rejection.", fatal: false
        } });
      }
      return original.apply(this, [method, ...args]);
    };
  }, rejectionCount);
  await page.getByRole("link", { name: "Open Admin" }).click();
  await expect.poll(async () => (await readTransport(page)).connection).toBe("failed");
  expect((await readTransport(page)).hub).toBe(false);
  expect((await readTransport(page)).captureLive).toBe(false);
  await page.getByRole("link", { name: "Return to last chat" }).click();
  await expect(page).toHaveURL(chatUrl);
  if (rejectionCount === 2) {
    await expect(page.getByRole("button", { name: "Retry", exact: true })).toBeVisible();
    await expect(page).toHaveURL(chatUrl);
    expect((await readTransport(page)).captureLive).toBe(false);
    await page.getByRole("button", { name: "Retry", exact: true }).click();
  }
  await expect.poll(async () => (await readTransport(page)).connection, { timeout: 15_000 }).toBe("ready");
  await expect(page.getByRole("button", { name: "Voice", exact: true })).toHaveAttribute("aria-pressed", "false");
  await waitForResponseSettled(page);
  await page.getByLabel("Message").fill("Recovered text turn");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".chat-message-assistant").last()).toContainText("Hello from synthetic.", { timeout: 20_000 });
  await waitForResponseSettled(page);
});
