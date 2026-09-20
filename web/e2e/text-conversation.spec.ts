import { expect, test, type Page } from "@playwright/test";

async function assistantBodyTextLength(page: Page, index: number): Promise<number> {
  return page
    .locator(".chat-message-assistant")
    .nth(index)
    .locator(".assistant-body")
    .evaluate((node) => node.textContent?.length ?? 0);
}

async function expectAssistantTextLengthStable(
  page: Page,
  index: number,
  length: number,
  settleMs = 2_000
): Promise<void> {
  const deadline = Date.now() + settleMs;
  while (Date.now() < deadline) {
    expect(await assistantBodyTextLength(page, index)).toBe(length);
    await page.waitForTimeout(250);
  }
}

async function expectConversationScrolledToBottom(page: Page): Promise<void> {
  await expect
    .poll(async () =>
      page.locator(".conversation-scroll").evaluate((element) => {
        const distance = element.scrollHeight - element.scrollTop - element.clientHeight;
        return distance <= 2;
      })
    )
    .toBe(true);
}

async function startLongHoldResponse(page: Page): Promise<void> {
  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".chat-message-assistant").first()).toContainText("Hello", {
    timeout: 15_000
  });
}

test("session path survives refresh", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i);
  const url = page.url();
  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page).toHaveURL(url);
  await expect(page.locator(".conversation-scroll").getByText("Hello", { exact: true })).toBeVisible();
  await expect(page.locator(".conversation-scroll").getByText("Hello from synthetic.")).toBeVisible();
  await expectConversationScrolledToBottom(page);
});

test("synthetic text conversation, pending voice, and disconnect cleanup", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("navigation", { name: "Chats" })).toBeVisible();
  await expect(page.getByLabel("Identity")).toBeVisible();
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });

  await page.locator("input.attach-input").setInputFiles({
    name: "notes.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("hello file")
  });
  await expect(page.getByRole("button", { name: "Remove notes.txt" })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Send" })).toBeEnabled();
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("link", { name: "notes.txt" })).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".chat-message-assistant")).toHaveCount(2);
  await expect(page.locator(".chat-message-assistant").last()).toContainText("Hello from synthetic.");

  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".chat-message-assistant")).toHaveCount(3);
  await expect(page.locator(".chat-message-assistant").last()).toContainText("Hello");
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible();
  await page.getByRole("button", { name: /^Voice$/ }).click();
  await expect(page.getByTestId("connection")).toHaveText("Starting voice…", { timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Cancel" })).toBeVisible();
  const frames = await page.evaluate(() => window.__agentCore?.audioFramesSent() ?? -1);
  expect(frames).toBe(0);

  await page.evaluate(() => window.__agentCore?.disconnect());
  await expect(page.getByTestId("connection")).toHaveText("Reconnecting to Agent Core…");
});

test("queued send and Stop keep the local queue without starting R2", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await expect(page.getByLabel("Queued messages")).toBeVisible();
  await expect(page.getByLabel("Queued messages")).toContainText("Hello");
  await expect(page.locator(".chat-message-user").filter({ hasText: "Hello" })).toHaveCount(0);
  await page.getByRole("button", { name: "Stop" }).click();
  await expect(page.getByLabel("Queued messages")).toBeVisible();
  await expect(page.locator(".chat-message-user").filter({ hasText: "Hello" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Send" })).toBeEnabled();
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".chat-message-user").filter({ hasText: "Hello" })).toBeVisible({ timeout: 15_000 });
});

const STEER_PROBE = "[test:steer-probe]";

test("Steer interrupts R1 promptly and auto-dispatch follows completion", async ({ page }) => {
  await page.goto("/");
  await startLongHoldResponse(page);
  const r1Length = await assistantBodyTextLength(page, 0);

  await page.getByRole("textbox", { name: "Message" }).fill(`${STEER_PROBE} Alpha`);
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await page.getByRole("textbox", { name: "Message" }).fill(`${STEER_PROBE} Beta`);
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await expect(page.locator(".chat-message-user").filter({ hasText: "Alpha" })).toHaveCount(0);
  await expect(page.locator(".chat-message-user").filter({ hasText: "Beta" })).toHaveCount(0);

  await page.getByRole("button", { name: "Steer queued message 1" }).click();
  await expect(page.locator(".chat-message-assistant").first().getByText("Interrupted")).toBeVisible({
    timeout: 5_000
  });
  await expect(page.getByLabel("Queued messages")).toContainText("Beta", { timeout: 15_000 });
  await expect(page.locator(".chat-message-user").filter({ hasText: "Beta" })).toHaveCount(0);
  await expectAssistantTextLengthStable(page, 0, r1Length);
  await expect(page.locator(".chat-message-user").filter({ hasText: "Alpha" })).toBeVisible({ timeout: 15_000 });

  await expect(page.locator(".chat-message-assistant").filter({ hasText: "Hello from synthetic." })).toHaveCount(2, {
    timeout: 25_000
  });
  await expect(page.locator(".chat-message-user").filter({ hasText: "Beta" })).toBeVisible({ timeout: 25_000 });
});

test("Steer middle queue item keeps head and tail queued", async ({ page }) => {
  await page.goto("/");
  await startLongHoldResponse(page);
  const r1Length = await assistantBodyTextLength(page, 0);

  await page.getByRole("textbox", { name: "Message" }).fill(`${STEER_PROBE} Alpha`);
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await page.getByRole("textbox", { name: "Message" }).fill(`${STEER_PROBE} Bravo`);
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await page.getByRole("textbox", { name: "Message" }).fill(`${STEER_PROBE} Charlie`);
  await page.locator('button.composer-send[aria-label="Queue"]').click();

  await page.getByRole("button", { name: "Steer queued message 2" }).click();

  await expect(page.locator(".chat-message-assistant").first().getByText("Interrupted")).toBeVisible({
    timeout: 5_000
  });
  const queue = page.getByLabel("Queued messages");
  await expect(queue).toContainText("Alpha", { timeout: 15_000 });
  await expect(queue).toContainText("Charlie");
  await expect(queue.getByText("Bravo")).toHaveCount(0);
  await expectAssistantTextLengthStable(page, 0, r1Length);
  await expect(page.locator(".chat-message-user").filter({ hasText: "Bravo" })).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".chat-message-assistant").nth(1)).toContainText("Hello from synthetic.", {
    timeout: 25_000
  });
});

test("queued attachment stays with the queued item until dispatch", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Please hold the line");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Stop" })).toBeVisible({ timeout: 15_000 });
  await page.locator("input.attach-input").setInputFiles({
    name: "queued.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("queued file")
  });
  await expect(page.getByRole("button", { name: "Remove queued.txt" })).toBeVisible({ timeout: 15_000 });
  await page.locator('button.composer-send[aria-label="Queue"]').click();
  await expect(page.getByLabel("Queued messages")).toContainText("queued.txt");
  await expect(page.getByRole("link", { name: "queued.txt" })).toHaveCount(0);
  await page.getByRole("button", { name: "Steer queued message 1" }).click();
  await expect(page.getByRole("link", { name: "queued.txt" })).toBeVisible({ timeout: 15_000 });
});

test("manual pause via deactivate shows Resume and keeps history", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  const deactivated = await page.evaluate(async () => {
    const token = window.localStorage.getItem("agent-core.owner-capability");
    const match = window.location.pathname.match(/\/c\/([0-9a-f-]{36})/i);
    if (!token || !match) {
      return { ok: false, status: 0 };
    }
    const response = await fetch(`/api/v2/sessions/${match[1]}/deactivate`, {
      method: "POST",
      headers: { "X-AgentCore-Owner-Capability": token }
    });
    return { ok: response.ok, status: response.status };
  });
  expect(deactivated.ok).toBe(true);
  await expect(page.getByRole("button", { name: "Resume" })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Model" })).toHaveCount(0);
  await expect(page.getByTestId("connection")).toHaveText("Paused");
  await expect(page.getByText("Hello from synthetic.")).toBeVisible();
  await page.getByRole("button", { name: "Resume" }).click();
  await expect(page.getByLabel("Message")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Model" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Send" })).toBeVisible();
});

test("markdown response renders and survives reopen", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Show markdown");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".agent-activity")).toHaveText("Thinking…", { timeout: 15_000 });
  await expect(page.locator(".markdown-message strong")).toHaveText("three", { timeout: 15_000 });
  await expect(page.locator(".markdown-message").getByText("Session runtime")).toBeVisible();
  await expect(page.locator(".markdown-message hr")).toHaveCount(1);
  await expect
    .poll(async () =>
      page.locator(".markdown-message hr").evaluate((hr) => {
        const rule = hr.getBoundingClientRect().width;
        const host = hr.closest(".markdown-message")?.getBoundingClientRect().width ?? 0;
        return host > 0 && rule >= host * 0.9;
      })
    )
    .toBe(true);
  await expect(page.locator(".markdown-message code")).toHaveText("IAgentProvider");
  await expect(page.locator(".agent-activity")).toHaveCount(0);

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.locator(".markdown-message strong")).toHaveText("three", { timeout: 15_000 });
  await expect(page.locator(".agent-activity")).toHaveCount(0);

  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await page.locator(".session-row-open").first().click();
  await expect(page.locator(".markdown-message strong")).toHaveText("three", { timeout: 15_000 });
});

async function selectCustomerSupport(page: Page): Promise<void> {
  await page.getByRole("combobox", { name: "Identity" }).click();
  await page.getByTitle(/Sam —/).click();
}

async function chooseScriptedAlpha(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Model" }).click();
  await page.getByTitle("Scripted Alpha").click();
}

async function expectHistoryOmitsProgress(page: Page): Promise<void> {
  const bodies = page.locator(".chat-message-user, .chat-message-assistant");
  const count = await bodies.count();
  for (let index = 0; index < count; index += 1) {
    await expect(bodies.nth(index)).not.toContainText(
      /Running tools|Reading attachments|Preparing response|Finalizing response/
    );
  }
}

test("progress is visible, replaced, cleared on final, reload, disconnect, and session switch", async ({
  page
}) => {
  test.setTimeout(180_000);
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await selectCustomerSupport(page);
  await chooseScriptedAlpha(page);
  await page.locator("input.attach-input").setInputFiles({
    name: "notes.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("hello file")
  });
  await expect(page.getByRole("button", { name: "Remove notes.txt" })).toBeVisible({ timeout: 15_000 });
  await page.getByLabel("Message").fill("Run the support case for order 91.");
  await page.evaluate(() => {
    const seen = { value: false };
    (window as Window & { __agentCoreProgressSeen?: { value: boolean } }).__agentCoreProgressSeen = seen;
    const observe = () => {
      const text = document.querySelector(".agent-activity")?.textContent ?? "";
      if (/Reading attachments|Running tools/.test(text)) {
        seen.value = true;
      }
    };
    observe();
    new MutationObserver(observe).observe(document.body, {
      subtree: true,
      childList: true,
      characterData: true
    });
  });
  await page.getByRole("button", { name: "Send" }).click();
  await expect
    .poll(async () => page.evaluate(() => (window as Window & { __agentCoreProgressSeen?: { value: boolean } }).__agentCoreProgressSeen?.value === true), {
      timeout: 15_000
    })
    .toBe(true);
  await expect(page.locator(".chat-message-assistant").first()).toContainText(/Order 91 is delayed/i, {
    timeout: 60_000
  });
  await expect(page.locator(".markdown-message strong")).toHaveText("Delayed", { timeout: 30_000 });
  await expect(page.locator(".agent-activity")).toHaveCount(0);
  await expect(page.getByRole("link", { name: "notes.txt" })).toBeVisible();
  await expectHistoryOmitsProgress(page);

  await page.evaluate(async () => {
    await window.__agentCore?.disconnect();
  });
  await expect(page.getByTestId("connection")).toHaveText("Reconnecting to Agent Core…", { timeout: 15_000 });
  await expect(page.locator(".agent-activity")).toHaveText("Reconnecting to Agent Core…");
  await page.evaluate(async () => {
    await window.__agentCore?.reconnect?.();
  });
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  await expect(page.locator(".agent-activity")).toHaveCount(0);
  await expectHistoryOmitsProgress(page);

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.locator(".markdown-message strong")).toHaveText("Delayed", { timeout: 15_000 });
  await expect(page.locator(".agent-activity")).toHaveCount(0);
  await expectHistoryOmitsProgress(page);

  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".agent-activity")).toHaveCount(0);
  await page.getByRole("button", { name: "Run the support case for order 91." }).first().click();
  await expect(page.locator(".markdown-message strong").first()).toHaveText("Delayed", { timeout: 15_000 });
  await expect(page.locator(".agent-activity")).toHaveCount(0);
  await expectHistoryOmitsProgress(page);
});

