import { approvalFixture, backgroundFixture, mockBackgroundSessions } from './support/background-fixtures';
import { expect, test } from "@playwright/test";

test("admin unattended model and event sources stay operable at wide and narrow widths", async ({ page }) => {
  test.setTimeout(90_000);
  const consoleErrors: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });
  page.on("pageerror", (error) => consoleErrors.push(error.message));

  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/");
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
  const instanceId = await page.evaluate(async () => {
    const token = window.localStorage.getItem("agent-core.owner-capability") ?? "";
    const response = await fetch("/api/v2/admin/agent-instances", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "X-AgentCore-Owner-Capability": token
      },
      body: JSON.stringify({ definitionId: "examiner", version: 1 })
    });
    if (!response.ok) {
      throw new Error(await response.text());
    }
    const body = (await response.json()) as { instanceId: string };
    return body.instanceId;
  });

  const eventKey = "order.placed";
  const eventId = "22222222-2222-4222-8222-222222222222";
  let sourceStatus = "Active";
  let sources: Array<Record<string, unknown>> = [];
  await page.goto(`/admin/instances/${instanceId}`);
  await page.getByRole("tab", { name: "Continuity", exact: true }).click();
  const automation = page.locator(".admin-instance-tabs");
  await page.getByRole("tab", { name: "Automation", exact: true }).click();
  await page.getByRole("tab", { name: "Policies & models", exact: true }).click();
  await expect(automation.getByText(/Effective source: Conversation default/)).toBeVisible();
  const model = automation.getByRole("combobox", { name: "Unattended model" });
  await model.focus();
  await expect(model).toBeFocused();
  await page.keyboard.type("Scripted Alpha");
  await page.keyboard.press("Enter");
  await expect(automation.locator(".ant-select").filter({
    has: page.getByRole("combobox", { name: "Unattended model", exact: true })
  })).toContainText("Scripted Alpha");
  await expect(automation.getByText(/Effective source: Conversation default/)).toBeVisible();
  await expect(automation.getByText("Effective source: Unattended default (Scripted Alpha)")).toHaveCount(0);
  const saved = page.waitForResponse(
    (response) => response.url().includes("/unattended-model") && response.request().method() === "POST"
  );
  await automation.getByRole("button", { name: "Save unattended model" }).click();
  const savedResponse = await saved;
  expect(savedResponse.ok()).toBeTruthy();
  expect((await savedResponse.json()).unattendedModelCatalogKey).toBe("scripted-alpha");
  await expect(automation.getByText("Effective source: Unattended default (Scripted Alpha)")).toBeVisible();
  await expect(automation.getByRole("alert")).toHaveCount(0);
  const reloaded = page.waitForResponse(
    (response) => response.url().includes("/effective-config") && response.request().method() === "GET" && response.ok()
  );
  await page.reload();
  const config = await reloaded;
  expect((await config.json()).unattendedModelCatalogKey).toBe("scripted-alpha");
  await page.getByRole("tab", { name: "Continuity", exact: true }).click();
  await page.getByRole("tab", { name: "Automation", exact: true }).click();
  await page.getByRole("tab", { name: "Policies & models", exact: true }).click();
  await expect(automation.getByText("Effective source: Unattended default (Scripted Alpha)")).toBeVisible({ timeout: 15_000 });

  await page.route("**/api/v2/admin/connections/events**", async (route) => {
    const url = route.request().url();
    const method = route.request().method();
    if (method === "GET" && new URL(url).pathname.endsWith("/catalog")) {
      if (new URL(url).searchParams.get("kind") === "builtin") { await route.continue(); return; }
      await route.fulfill({ json: sources.map(item => ({
        source: { kind: "webhook", eventId: item.eventId }, name: item.displayName, key: item.eventKey,
        description: "Authenticated external signal.", state: item.status, schemaVersion: 1,
        example: {}, fieldSchema: {}, webhook: { ...item, kind: "Webhook" }
      })) });
      return;
    }

    if (method === "POST" && url.endsWith("/connections/events")) {
      sources = [{
        eventId,
        displayName: "Demo Store",
        subscriberCount: 0, activeSubscriberCount: 0, createdAt: "2026-10-08T00:00:00Z", updatedAt: "2026-10-08T00:00:00Z", lastReceivedAt: null,
        eventKey,
        status: sourceStatus,
        revision: 1
      }];
      await route.fulfill({
        json: { eventId, eventKey, token: "once-secret-credential", status: "Active" }
      });
      return;
    }

    if (method === "POST" && url.endsWith("/revoke")) {
      sourceStatus = "Revoked";
      sources = sources.map((item) => ({ ...item, status: "Revoked", revision: 2 }));
      await route.fulfill({ json: sources[0] });
      return;
    }

    await route.continue();
  });

  await page.goto("/admin");
  await page.getByRole("tab", { name: "Connections", exact: true }).click();
  await page.getByRole("tab", { name: "Events", exact: true }).click();
  const sourcesRegion = page.getByRole("region", { name: "Events" });
  await expect(sourcesRegion.getByRole("button", { name: "run.completed", exact: true })).toBeVisible();
  await sourcesRegion.getByRole("combobox", { name: "Event type filter", exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: /^Webhook$/ }).click();
  await expect(sourcesRegion.getByText("No Webhook Events yet. Create one to receive external signals.")).toBeVisible();
  await sourcesRegion.getByRole("button", { name: "New webhook Event" }).click();
  await page.getByLabel("Event name").fill("Demo Store");
  await page.getByLabel("Event key").fill(eventKey);
  const create = page.getByRole("button", { name: "Create Event" });
  await create.focus();
  await expect(create).toBeFocused();
  await page.keyboard.press("Enter");
  const credential = page.getByRole("dialog", { name: "Copy this credential" });
  await expect(credential.getByRole("textbox", { name: "Event credential" })).toHaveValue("once-secret-credential");
  await page.keyboard.press("Escape");
  await expect(credential).toBeHidden();
  await expect(page.getByText("once-secret-credential")).toHaveCount(0);
  await expect(sourcesRegion.getByText(eventKey, { exact: true })).toHaveText(eventKey);

  await page.setViewportSize({ width: 390, height: 800 });
  await expect(sourcesRegion.getByRole("button", { name: "More actions for Demo Store" })).toBeVisible();
  await sourcesRegion.getByRole("button", { name: "More actions for Demo Store" }).click();
  await page.getByRole("menuitem", { name: "Revoke Event", exact: true }).click();
  const confirm = page.getByRole("dialog", { name: "Revoke this Event?" });
  await confirm.getByRole("button", { name: "Revoke Event" }).click();
  await expect(sourcesRegion.getByText("Revoked", { exact: true })).toBeVisible();
  await expect(sourcesRegion.getByRole("row").filter({ hasText: "Demo Store" }).getByText(eventKey, { exact: true })).toBeVisible();
  await expect(page.getByText("once-secret-credential")).toHaveCount(0);

  expect(consoleErrors.filter((line) => !line.includes("[antd: List]"))).toEqual([]);
});

test("background work lists scheduled and order-placed sources", async ({ page }) => {
  test.setTimeout(60_000);
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/");
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await page.getByLabel("Message").fill("hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Background work" })).toBeVisible({ timeout: 15_000 });

  await mockBackgroundSessions(page, [backgroundFixture(177, 'Scheduled review', { activationKind: 'ScheduledWork' }),
    backgroundFixture(178, 'Order-placed review', { activationKind: 'ApplicationEvent', status: 'needsApproval', outcome: null, approval: approvalFixture })]);
  const opener = page.getByRole("button", { name: "Background work" });
  await opener.focus();
  await page.keyboard.press("Enter");
  const drawer = page.getByRole("dialog", { name: "Background work" });
  await expect(drawer.getByRole("heading", { name: "Scheduled review", exact: true })).toBeVisible();
  await expect(drawer.getByRole("heading", { name: "Order-placed review", exact: true })).toBeVisible();
  await expect(drawer.getByText("Needs approval")).toBeVisible();
  await expect(drawer.getByText(/sourceEventId|orderReference/)).toHaveCount(0);
  await drawer.getByRole("button", { name: "Close", exact: true }).focus();
  await page.keyboard.press("Escape");
  await expect(drawer).toBeHidden();
  await expect(opener).toBeFocused();

  await page.setViewportSize({ width: 390, height: 800 });
  await opener.click();
  await expect(drawer.getByRole("heading", { name: "Order-placed review", exact: true })).toBeVisible();
  await drawer.getByRole("button", { name: "Close" }).click();
  await expect(drawer).toBeHidden();
});
