import { expect, test } from "@playwright/test";
import { LEGACY_IDENTITY_LABELS, selectLegacyIdentity } from "./support/legacy-identity";

test("create a schedule in chat, refresh the list, and cancel it", async ({ page }) => {
  test.setTimeout(60_000);
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
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/");
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
  await page.evaluate(async () => {
    const token = window.localStorage.getItem("agent-core.owner-capability") ?? "";
    const headers = {
      "content-type": "application/json",
      "X-AgentCore-Owner-Capability": token
    };
    const current = await fetch("/api/v2/profile", { headers });
    if (!current.ok) {
      throw new Error(`profile ${current.status} token ${token.length}`);
    }
    const profile = (await current.json()) as { revision: number };
    const patched = await fetch("/api/v2/profile", {
      method: "PATCH",
      headers,
      body: JSON.stringify({ expectedRevision: profile.revision, values: { timeZone: "UTC" } })
    });
    if (!patched.ok) {
      throw new Error(await patched.text());
    }
  });
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.generalAssistant);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("remind me tomorrow");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Scheduled Call John.")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "Schedules" }).click();
  const drawer = page.getByRole("dialog", { name: "Schedules" });
  await expect(drawer.getByText("Call John").first()).toBeVisible();
  await expect(drawer.getByText("Active").first()).toBeVisible();
  await page.keyboard.press("Escape");

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByRole("button", { name: "Schedules" }).click();
  await expect(drawer.getByText("Call John").first()).toBeVisible();

  await page.setViewportSize({ width: 390, height: 800 });
  await expect(page.getByRole("navigation", { name: "Chats" })).toBeHidden();
  await expect(drawer.getByText("Call John").first()).toBeVisible();

  await page.route("**/triggers/*/cancel", async (route) => {
    await route.fulfill({
      status: 409,
      contentType: "application/problem+json",
      body: JSON.stringify({ title: "Conflict", detail: "Registration revision is stale." })
    });
  });
  await drawer.getByRole("button", { name: "Cancel Call John" }).first().click();
  await page.getByRole("button", { name: "Cancel schedule" }).click();
  await expect(drawer.getByText("Registration revision is stale.")).toBeVisible();
  await page.unroute("**/triggers/*/cancel");

  await drawer.getByRole("button", { name: "Cancel Call John" }).first().click();
  await page.getByRole("button", { name: "Cancel schedule" }).click();
  await expect(drawer.getByText("Cancelled").first()).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".conversation-scroll")).toBeVisible();
  const unexpectedConsole = consoleErrors.filter(
    (message) => !message.includes("[antd: List]") && !message.includes("409 (Conflict)")
  );
  expect(unexpectedConsole).toEqual([]);
  expect(serverErrors).toEqual([]);
});
