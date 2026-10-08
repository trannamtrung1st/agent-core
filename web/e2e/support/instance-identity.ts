import { expect, type Page } from "@playwright/test";

export const INSTANCE_DEFINITIONS = {
  examiner: { id: "examiner", version: 1 },
  customerSupport: { id: "customer-support", version: 3 },
  generalAssistant: { id: "general-assistant", version: 17 },
  approvalHarness: { id: "approval-demo", version: 1 }
} as const;

/** Provision an explicit fixture identity, then select it through Chat. */
export async function selectInstanceIdentity(page: Page, definition: { id: string; version: number }): Promise<void> {
  await expect.poll(() => page.evaluate(() => localStorage.getItem("agent-core.owner-capability"))).not.toBeNull();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const created = await page.request.post("/api/v2/admin/agent-instances", {
    headers: { "X-AgentCore-Owner-Capability": token! },
    data: { definitionId: definition.id, version: definition.version }
  });
  expect(created.ok(), await created.text()).toBe(true);
  const instanceId = (await created.json()).instanceId as string;
  await page.reload();
  const identity = page.getByRole("combobox", { name: "Identity" });
  await expect(identity).toBeEnabled();
  await identity.click();
  await identity.fill(instanceId.replace(/-/g, "").slice(-8));
  const option = page.locator(".ant-select-item-option").filter({ hasText: instanceId.replace(/-/g, "").slice(-8) });
  await expect(option).toBeVisible();
  await option.click();
}
