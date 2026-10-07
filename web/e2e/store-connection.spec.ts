import { expect, test } from "@playwright/test";
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";

test("store connection and quiet background work stay labeled", async ({ page }) => {
  test.setTimeout(120_000);
  const consoleErrors: string[] = [];
  const serverErrors: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });
  page.on("pageerror", (error) => consoleErrors.push(error.message));
  page.on("response", (response) => {
    if (response.status() >= 500) {
      serverErrors.push(`${response.status()} ${response.url()}`);
    }
  });

  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/");
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  await page.getByLabel("Message").fill("hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  const work = page.getByRole("button", { name: "Background work", exact: true });
  await expect(work).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: /need attention/ })).toHaveCount(0);
  await page.route("**/work-items**", async (route) => {
    if (route.request().url().includes("/result")) {
      await route.fulfill({ status: 404, body: "" });
      return;
    }

    await route.fulfill({ json: { items: [] } });
  });
  await work.focus();
  await expect(work).toBeFocused();
  await page.keyboard.press("Enter");
  const drawer = page.getByRole("dialog", { name: "Background work" });
  await expect(drawer).toBeVisible({ timeout: 15_000 });
  await expect(drawer.getByText("No runs yet. Runs appear when schedules, thoughts, events, or retrospection execute.")).toBeVisible({ timeout: 15_000 });
  const close = drawer.getByRole("button", { name: "Close" });
  await close.focus();
  await expect(close).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(drawer).toBeHidden({ timeout: 15_000 });
  await page.unroute("**/work-items**");

  const attentionId = "019944af-00c5-7000-8000-0000000000a1";
  const quietId = "019944af-00c5-7000-8000-0000000000a2";
  await page.route("**/work-items**", async (route) => {
    const url = route.request().url();
    const completed = (workItemId: string, origin: string, attentionRequired: boolean) => ({
      workItemId,
      status: "completed",
      revision: 2,
      origin,
      progress: null,
      needsApproval: false,
      approvalId: null,
      approvalRevision: null,
      approvalPreview: null,
      actionHash: null,
      cancellationAvailable: false,
      failureCode: null,
      failureSummary: null,
      knownEffect: null,
      attentionRequired,
      createdAt: "2026-10-02T09:00:00.000Z",
      updatedAt: "2026-10-02T09:01:00.000Z"
    });
    if (url.includes("/result")) {
      const attention = url.includes(attentionId);
      await route.fulfill({
        json: {
          workItemId: attention ? attentionId : quietId,
          text: attention ? "Low stock on AC Keyboard." : "Stock is unchanged.",
          completedAt: "2026-10-02T09:01:00.000Z",
          attentionRequired: attention
        }
      });
      return;
    }

    await route.fulfill({
      json: {
        items: [
          completed(attentionId, "Morning review", true),
          completed(quietId, "Quiet check", false)
        ]
      }
    });
  });

  const attentionWork = page.getByRole("button", { name: /Background work, 1 need attention/ });
  await expect(attentionWork).toBeVisible({ timeout: 12_000 });
  await attentionWork.click();
  await expect(drawer.getByText("Needs attention")).toHaveCount(1);
  await expect(drawer.getByText("Low stock on AC Keyboard.")).toBeVisible();
  const quietRow = drawer.getByRole("listitem").filter({ hasText: "Stock is unchanged." });
  await expect(quietRow).toBeVisible();
  await expect(quietRow.getByText("Needs attention")).toHaveCount(0);
  const closeAgain = drawer.getByRole("button", { name: "Close" });
  await closeAgain.focus();
  await expect(closeAgain).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(drawer).toBeHidden({ timeout: 15_000 });

  const sessionId = page.url().match(/\/c\/([^/?#]+)/i)?.[1];
  expect(sessionId).toBeTruthy();
  const instanceId = await page.evaluate(async (id) => {
    const capability = window.localStorage.getItem("agent-core.owner-capability");
    const response = await fetch(`/api/v1/sessions/${id}`, {
      headers: capability ? { "X-AgentCore-Owner-Capability": capability } : {}
    });
    if (!response.ok) {
      throw new Error(`session lookup failed: ${response.status}`);
    }
    const session = (await response.json()) as { agentInstanceId?: string | null };
    return session.agentInstanceId ?? null;
  }, sessionId!);
  expect(instanceId).toBeTruthy();
  await expect(page.getByLabel(/Application connection:/)).toHaveCount(0);
  await page.goto(`/admin/instances/${instanceId}`);
  await expect(page).toHaveURL(/\/admin\/instances\/[0-9a-f-]{36}$/i);
  await page.getByRole("tab", { name: "Connections", exact: true }).click();
  const section = page.getByRole("region", { name: "Application connection" });
  await expect(section.getByRole("heading", { name: "Application connection" })).toBeVisible();
  await expect(section.getByText("Connect the supported nopCommerce application to this agent. Authentication stays in this agent's browser profile.")).toBeVisible();
  await expect(section.getByText("Application type")).toBeVisible();
  await expect(section.getByText("nopCommerce", { exact: true }).first()).toBeVisible();
  const existingRevoke = section.getByRole("button", { name: "Revoke connection" });
  if (await existingRevoke.isVisible()) {
    await existingRevoke.click();
    await page.getByRole("dialog", { name: "Revoke this connection?" }).getByRole("button", { name: "Revoke" }).click();
  }
  await expect(section.getByText("Not connected")).toBeVisible();
  await expect(section.getByLabel("Display name")).toHaveValue("");
  await expect(section.getByLabel("Base URL")).toHaveValue("");

  await section.getByLabel("Display name").fill("nopCommerce");
  await section.getByLabel("Base URL").fill("not a url");
  await section.getByRole("button", { name: "Connect" }).focus();
  await expect(section.getByRole("button", { name: "Connect" })).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(section.getByText("Base URL must be an absolute http or https origin without credentials.")).toBeVisible();
  await expect(section.getByText(/cookie|token|profile path/i)).toHaveCount(0);

  await section.getByLabel("Base URL").fill("http://127.0.0.1:5091");
  await section.getByRole("button", { name: "Connect" }).click();
  await expect(section.getByText("Connected", { exact: true })).toBeVisible({ timeout: 45_000 });
  await section.getByRole("button", { name: "Revoke connection" }).click();
  const confirm = page.getByRole("dialog", { name: "Revoke this connection?" });
  await confirm.getByRole("button", { name: "Revoke" }).click();
  await expect(section.getByText("Not connected", { exact: true })).toBeVisible();
  await expect(section.getByText(/cookie|token|profile path/i)).toHaveCount(0);

  await page.goto(`/c/${sessionId}`);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.getByLabel(/Application connection:/)).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Manage application connection" })).toHaveCount(0);

  await page.goto(`/admin/instances/${instanceId}`);
  await page.setViewportSize({ width: 390, height: 800 });
  await page.getByRole("tab", { name: "Connections", exact: true }).click();
  await expect(section.getByRole("heading", { name: "Application connection" })).toBeVisible();
  await expect(section.getByRole("button", { name: "Connect" })).toBeVisible();

  expect(serverErrors).toEqual([]);
  const unexpected = consoleErrors.filter(
    (line) =>
      !line.includes("[antd: List]") &&
      !line.includes("404 (Not Found)") &&
      !line.includes("400 (Bad Request)"),
  );
  expect(unexpected).toEqual([]);
});
