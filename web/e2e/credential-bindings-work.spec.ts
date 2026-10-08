import { backgroundFixture, mockBackgroundSessions } from './support/background-fixtures';
import { expect, test } from "@playwright/test";
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";

test("credential bindings and quiet background work stay labeled", async ({ page }) => {
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
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  await page.getByLabel("Message").fill("hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  const work = page.getByRole("button", { name: "Background work", exact: true });
  await expect(work).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: /need attention/ })).toHaveCount(0);
  await mockBackgroundSessions(page, []);
  await work.focus(); await page.keyboard.press('Enter');
  const drawer = page.getByRole('dialog', { name: 'Background work', exact: true });
  await expect(drawer.getByText('No background Sessions yet')).toBeVisible();
  await drawer.getByRole('button', { name: 'Close', exact: true }).focus();
  await page.keyboard.press('Escape'); await expect(drawer).toBeHidden();
  await page.unroute('**/background-sessions?**');
  await mockBackgroundSessions(page, [backgroundFixture(161, 'Morning review', { outcome: { kind: 'NeedsAttention', summary: 'Low stock on AC Keyboard.', outcomeEntryId: null, attentionRequired: true } }),
    backgroundFixture(162, 'Quiet check', { outcome: { kind: 'NoAction', summary: 'Stock is unchanged.', outcomeEntryId: null, attentionRequired: false } })]);
  const attentionWork = page.getByRole("button", { name: /Background work, 1 need attention/ });
  await expect(attentionWork).toBeVisible({ timeout: 12_000 });
  await attentionWork.click();
  await expect(drawer.getByText('Unread · needs attention')).toHaveCount(1);
  await expect(drawer.getByRole('button', { name: 'Morning review', exact: true })).toBeVisible();
  const quietRow = drawer.getByRole('listitem').filter({ hasText: 'Quiet check' });
  await expect(quietRow).toBeVisible(); await expect(quietRow.getByText('Unread · needs attention')).toHaveCount(0);
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
  await page.getByRole("tab", { name: "Credentials", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Credential bindings" })).toBeVisible();
  await expect(page.getByText("No credentials bound")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Browser state" })).toBeVisible();
  await page.goto(`/admin/instances/${instanceId}/connections`);
  await expect(page).toHaveURL(new RegExp(`/admin/instances/${instanceId}/connections(?:/credentials)?$`));
  await page.setViewportSize({ width: 390, height: 800 });
  await expect(page.getByRole("heading", { name: "Credential bindings" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Bind credential", exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();

  expect(serverErrors).toEqual([]);
  const unexpected = consoleErrors.filter(
    (line) =>
      !line.includes("[antd: List]") &&
      !line.includes("404 (Not Found)") &&
      !line.includes("400 (Bad Request)"),
  );
  expect(unexpected).toEqual([]);
});
