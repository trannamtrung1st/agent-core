import { expect, test } from "@playwright/test";
import { waitForSessionAttached } from "./support/session-attached";
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";

test.describe.configure({ mode: "serial" });

test("sensitive approval modal approves synthetic action", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.approvalHarness);

  await page.getByLabel("Message").fill("Please run sensitive approval for the harness.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 30_000 });
  await expect(modal).toContainText("Synthetic sensitive approval");

  await modal.getByRole("button", { name: "Approve" }).click();
  await expect(modal).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText("Sensitive action completed after approval.")).toBeVisible({ timeout: 20_000 });
});

test("approval survives refresh and resumes the same durable execution", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.approvalHarness);
  await page.getByLabel("Message").fill("Please run sensitive approval after refresh.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 30_000 });
  const before = await page.evaluate(() => window.__agentCore?.conversationExecution?.());
  expect(before?.executionId).toBeTruthy();

  await page.reload();
  await waitForSessionAttached(page);
  await expect(page.getByTestId("connection")).toHaveText("Running tools…");
  await expect(modal).toBeVisible({ timeout: 30_000 });
  const after = await page.evaluate(() => window.__agentCore?.conversationExecution?.());
  expect(after?.executionId).toBe(before?.executionId);
  expect(after?.responseId).toBe(before?.responseId);

  await modal.getByRole("button", { name: "Approve" }).click();
  await expect(page.getByText("Sensitive action completed after approval.")).toBeVisible({
    timeout: 20_000
  });
  await expect(page.locator(".chat-message-assistant")).toHaveCount(1);
});

test("sensitive approval modal reject dismisses without executing", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.approvalHarness);

  await page.getByLabel("Message").fill("Need sensitive approval for reject path.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 30_000 });
  await modal.getByRole("button", { name: "Reject" }).click();
  await expect(modal).toBeHidden({ timeout: 20_000 });
  await expect.poll(() => page.evaluate(() => window.__agentCore?.conversationExecution?.()?.responseId)).toBeNull();
  await expect(page.getByText("Sensitive action completed after approval.")).toHaveCount(0);
});

test("sensitive approval modal approve via keyboard", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.approvalHarness);

  await page.getByLabel("Message").fill("Please run sensitive approval for the harness.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 30_000 });
  await modal.getByRole("button", { name: "Approve" }).focus();
  await page.keyboard.press("Enter");
  await expect(modal).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText("Sensitive action completed after approval.")).toBeVisible({ timeout: 20_000 });
});

for (const recovery of ["refresh", "disconnect"] as const) {
  test(`rejection via keyboard recovers after ${recovery}`, async ({ page }) => {
    await page.goto("/");
    await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
    await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.approvalHarness);
    await page.getByLabel("Message").fill("Need sensitive approval for reject recovery.");
    await page.getByRole("button", { name: "Send" }).click();
    const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
    await expect(modal).toBeVisible({ timeout: 30_000 });
    const before = await page.evaluate(() => window.__agentCore?.conversationExecution?.());
    if (recovery === "refresh") {
      await page.reload();
    } else {
      await page.evaluate(() => window.__agentCore?.disconnect?.());
      await modal.getByRole("button", { name: "Reject" }).click();
      await expect(modal).toContainText("Decision was not confirmed");
      await expect(modal).toBeVisible();
      await page.evaluate(() => window.__agentCore?.reconnect?.());
    }
    await waitForSessionAttached(page);
    await expect(modal).toBeVisible();
    const after = await page.evaluate(() => window.__agentCore?.conversationExecution?.());
    expect(after?.executionId).toBe(before?.executionId);
    expect(after?.responseId).toBe(before?.responseId);
    await modal.getByRole("button", { name: "Reject" }).focus();
    await page.keyboard.press("Enter");
    await expect(modal).toBeHidden({ timeout: 20_000 });
    await expect.poll(() => page.evaluate(() => window.__agentCore?.conversationExecution?.()?.responseId)).toBeNull();
    await expect(page.getByText("Sensitive action completed after approval.")).toHaveCount(0);
    await page.reload();
    await waitForSessionAttached(page);
    await expect(modal).toBeHidden();
    await expect(page.getByText("Sensitive action completed after approval.")).toHaveCount(0);
  });
}
