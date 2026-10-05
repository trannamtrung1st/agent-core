import { expect, test } from "@playwright/test";
import { publishExaminerDraftAndCreateManagedInstance, startSyntheticChat } from "./admin-managed-helpers";

test("p7e admin memory and automation tabs exercise owner-protected APIs", async ({ page }) => {
  test.setTimeout(90_000);

  await startSyntheticChat(page);
  const { instance, firstSession } = await publishExaminerDraftAndCreateManagedInstance(page);

  await page.getByRole("button", { name: "Open Admin" }).click();
  await page.goto(`/admin/instances/${instance.instanceId}`);
  await expect(page.locator(".admin-instance-heading").getByText("Managed", { exact: true })).toBeVisible();

  await page.getByRole("tab", { name: "Continuity", exact: true }).click();
  const memoryAutomation = page.locator(".admin-instance-tabs");
  await memoryAutomation.getByRole("tab", { name: "Memory" }).click();
  await memoryAutomation.getByRole("combobox", { name: "Memory scope" }).click();
  await page.locator(".ant-select-item-option", { hasText: "Session" }).click();
  await memoryAutomation.getByRole("textbox", { name: "Session id" }).fill(firstSession.sessionId);

  const sessionMemoryResponse = page.waitForResponse(
    (response) =>
      response.request().method() === "GET" &&
      response.url().includes(`/api/v2/admin/agent-instances/${instance.instanceId}/learned-memory`) &&
      response.url().includes("scope=Session") &&
      response.url().includes(firstSession.sessionId)
  );
  await memoryAutomation.getByRole("button", { name: "Load items" }).click();
  const sessionMemory = await sessionMemoryResponse;
  if (sessionMemory.ok()) {
    const body = (await sessionMemory.json()) as { items: unknown[] };
    expect(Array.isArray(body.items)).toBe(true);
    await expect(memoryAutomation.getByText("No active learned-memory items in this scope.")).toBeVisible();
  } else {
    expect(sessionMemory.status()).toBe(403);
    const denied = (await sessionMemory.json()) as { detail?: string };
    expect(denied.detail ?? "").toMatch(/Session learned memory is not enabled/i);
    await expect(memoryAutomation.locator(".ant-typography-danger")).toBeVisible();
  }

  await page.getByRole("tab", { name: "Automation", exact: true }).click();
  await page.getByRole("tab", { name: "Policies & models", exact: true }).click();
  const automationResponse = page.waitForResponse(
    (response) =>
      response.request().method() === "GET" &&
      response.url().includes(`/api/v2/admin/agent-instances/${instance.instanceId}/automation/registrations`)
  );
  await memoryAutomation.getByRole("button", { name: "Review advanced registrations" }).click();
  const automation = await automationResponse;
  expect(automation.ok()).toBe(true);
  const automationBody = (await automation.json()) as { items: unknown[] };
  expect(Array.isArray(automationBody.items)).toBe(true);
  await expect(memoryAutomation.getByText("No active or suspended registrations.")).toBeVisible();
});
