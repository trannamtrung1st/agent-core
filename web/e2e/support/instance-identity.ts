import { expect, type Page } from "@playwright/test";
import type { ExecutionBudgetPolicy } from "../../src/services/adminApi";

export const INSTANCE_DEFINITIONS = {
  examiner: { id: "examiner", version: 1 },
  customerSupport: { id: "customer-support", version: 3 },
  generalAssistant: { id: "general-assistant", version: 21 },
  approvalHarness: { id: "approval-demo", version: 1 }
} as const;

/** Provision an explicit fixture identity, then select it through Chat. */
export async function selectInstanceIdentity(page: Page, definition: { id: string; version: number }, executionBudgets?: ExecutionBudgetPolicy): Promise<void> {
  await expect.poll(() => page.evaluate(() => localStorage.getItem("agent-core.owner-capability"))).not.toBeNull();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const created = await page.request.post("/api/v2/admin/agent-instances", {
    headers: { "X-AgentCore-Owner-Capability": token! },
    data: { definitionId: definition.id, version: definition.version }
  });
  expect(created.ok(), await created.text()).toBe(true);
  const instance = await created.json();
  const instanceId = instance.instanceId as string;
  if (executionBudgets) {
    const configured = await page.request.post(`/api/v2/admin/agent-instances/${instanceId}/execution-budgets`, {
      headers: { "X-AgentCore-Owner-Capability": token! },
      data: { expectedRevision: instance.revision, executionBudgets }
    });
    expect(configured.ok(), await configured.text()).toBe(true);
  }
  await page.reload();
  const identity = page.getByRole("combobox", { name: "Identity" });
  await expect(identity).toBeEnabled();
  await identity.click();
  await identity.fill(instanceId.replace(/-/g, "").slice(-8));
  const option = page.locator(".ant-select-item-option").filter({ hasText: instanceId.replace(/-/g, "").slice(-8) });
  await expect(option).toBeVisible();
  await option.click();
}
