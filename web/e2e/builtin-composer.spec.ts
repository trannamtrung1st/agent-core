import { expect, test, type Page } from "@playwright/test";
import { waitForResponseSettled } from "./support/response-settled";

const harness = "builtin:harness-authoring.v1";
const configuration = "builtin:configuration-assistance.v1";

async function setup(page: Page) {
  await page.goto("/");
  await expect.poll(() => page.evaluate(() => localStorage.getItem("agent-core.owner-capability"))).not.toBeNull();
  const headers = { "X-AgentCore-Owner-Capability": (await page.evaluate(() => localStorage.getItem("agent-core.owner-capability")))! };
  const created = await page.request.post("/api/v2/admin/agent-instances", {
    headers, data: { definitionId: "general-assistant", version: 35 },
  });
  expect(created.ok(), await created.text()).toBe(true);
  const instance = await created.json();
  const instanceId: string = instance.instanceId;
  const policyUrl = `/api/v2/admin/agent-instances/${instanceId}/self-management`;
  const policy = { mode: "Assisted", scopes: ["Settings", "KnowledgeResources"], managedAutoApply: [], configurationAssistance: true, harnessAuthoring: true };
  async function setHarness(enabled: boolean) {
    const read = await page.request.get(policyUrl, { headers });
    expect(read.ok(), await read.text()).toBe(true);
    const state = await read.json();
    const saved = await page.request.put(policyUrl, {
      headers, data: { ...policy, expectedRevision: state.instanceRevision, harnessAuthoring: enabled },
    });
    expect(saved.ok(), await saved.text()).toBe(true);
  }
  await setHarness(true);
  await page.reload();
  const identity = page.getByRole("combobox", { name: "Identity", exact: true });
  await identity.click();
  const suffix = instanceId.replace(/-/g, "").slice(-8);
  await identity.fill(suffix);
  await page.locator(".ant-select-item-option").filter({ hasText: suffix }).click();
  return { headers, setHarness };
}

async function choose(page: Page, key: string, plus = false) {
  if (plus) {
    await page.getByRole("button", { name: "Add content" }).click();
    await page.getByRole("menuitem", { name: "Use Skill" }).click();
  } else await page.getByLabel("Message", { exact: true }).pressSequentially("/");
  await page.getByRole("option").filter({ hasText: key }).click();
  await expect(page.getByLabel("Message", { exact: true }).locator(`[data-skill-key="${key}"]`)).toHaveCount(1);
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeEnabled();
  await expect(page.getByText("Invalid message part.", { exact: true })).toHaveCount(0);
}

async function send(page: Page, count: number) {
  await page.getByRole("button", { name: "Send", exact: true }).click();
  // Admission and durable finalization can queue behind other Synthetic work on CI.
  // Use the same bounded response budget as the terminal-state helper below.
  await expect(page.locator(".chat-message-assistant")).toHaveCount(count, { timeout: 30_000 });
  await expect(page.locator(".chat-message-assistant").last()).toContainText("Hello from synthetic.");
  await waitForResponseSettled(page);
}

test("built-in Skills send alone or with text, resolve references, restore history and remain turn-scoped", async ({ page }) => {
  test.setTimeout(90000);
  const { headers } = await setup(page);
  await choose(page, harness);
  await send(page, 1);
  await choose(page, configuration, true);
  await page.getByLabel("Message", { exact: true }).pressSequentially("??");
  await send(page, 2);
  await page.getByRole("button", { name: "Add content" }).click();
  await page.getByRole("menuitem", { name: "Reference resource" }).click();
  await page.getByRole("option").filter({ hasText: harness }).click();
  await send(page, 3);
  const sessionId = page.url().split("/c/")[1];
  const runs = await (await page.request.get(`/api/v2/sessions/${sessionId}/agent-runs`, { headers })).json();
  expect(runs.items[2].composerInput.explicitSkillKeys).toEqual([harness]);
  expect(runs.items[1].composerInput.explicitSkillKeys).toEqual([configuration]);
  expect(runs.items[0].composerInput.explicitSkillKeys).toEqual([]);
  expect(runs.items[0].composerInput.references).toEqual([expect.objectContaining({ kind: "skill", status: "valid" })]);
  await page.reload();
  const messages = page.locator(".chat-message-user");
  await expect(messages.nth(0).locator(".composer-chip")).toHaveText("/Harness authoring");
  await expect(messages.nth(1)).toContainText("??");
  await expect(messages.nth(1).locator(".composer-chip")).toHaveText("/Configuration assistance");
  await expect(messages.nth(2).locator(".composer-chip")).toHaveText("@Harness authoring");
  await page.getByLabel("Message", { exact: true }).fill("Hello");
  await send(page, 4);
  const laterRuns = await (await page.request.get(`/api/v2/sessions/${sessionId}/agent-runs`, { headers })).json();
  expect(laterRuns.items[0].composerInput?.explicitSkillKeys ?? []).toEqual([]);
});

test("revoked built-in selection preserves the draft and sends after re-enabling", async ({ page }) => {
  test.setTimeout(90000);
  const { setHarness } = await setup(page);
  await page.getByLabel("Message", { exact: true }).fill("Hello");
  await send(page, 1);
  await choose(page, harness);
  await setHarness(false);
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.getByText(/Selected Skill builtin:harness-authoring.v1 is unavailable/)).toBeVisible();
  await expect(page.getByLabel("Message", { exact: true }).locator(`[data-skill-key="${harness}"]`)).toHaveCount(1);
  await expect(page.locator(".chat-message-user")).toHaveCount(1);
  await expect(page.locator(".chat-message-assistant")).toHaveCount(1);
  await setHarness(true);
  await send(page, 2);
  await expect(page.locator(".chat-message-user").last().locator(".composer-chip")).toHaveText("/Harness authoring");
});
