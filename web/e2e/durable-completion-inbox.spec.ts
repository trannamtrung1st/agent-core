import { expect, test } from "@playwright/test";
import { selectInstanceIdentity, INSTANCE_DEFINITIONS } from "./support/instance-identity";
import { waitForResponseSettled } from "./support/response-settled";
import type { AgentRun, BackgroundSession, CursorPage } from "../src/services/api";

for (const handled of [true, false]) test(`active parent ${handled ? "handles" : "retains"} a durable child result before automatic reporting`, async ({ page }) => {
  test.setTimeout(75_000);
  const errors: string[] = []; page.on("pageerror", e => errors.push(e.message));
  await page.goto("/"); await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await page.getByLabel("Message", { exact: true }).fill(handled ? "[test:completion-handoff] Use the child result in this answer." : "[test:completion-unhandled] Keep the result pending until this Run ends.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i);
  const parentId = page.url().split("/").at(-1)!;
  const token = (await page.evaluate(() => localStorage.getItem("agent-core.owner-capability")))!;
  const headers = { "X-AgentCore-Owner-Capability": token };
  const parent = await (await page.request.get(`/api/v2/sessions/${parentId}`, { headers })).json();
  async function runs() { return (await (await page.request.get(`/api/v2/sessions/${parentId}/agent-runs`, { headers })).json() as CursorPage<AgentRun>).items; }
  await expect.poll(async () => (await runs())[0]?.status).toBe("waitingForSignal");
  const waiting = (await runs())[0]; expect(waiting.attemptCount).toBe(1); expect(waiting.wait).toBeTruthy();
  expect((await runs()).filter(r => r.activationKind === "BackgroundCompleted")).toHaveLength(0);
  await expect(page.locator(".conversation-scroll")).toContainText("Waiting for", { timeout: 10_000 });
  await expect(page.locator(".conversation-scroll")).toContainText(handled ? "I used the verified background result" : "The active parent Run finished", { timeout: 30_000 });
  if (!handled) await expect(page.locator(".conversation-scroll")).toContainText("Background work: Verified background evidence", { timeout: 30_000 });
  await waitForResponseSettled(page);
  const children = (await (await page.request.get(`/api/v2/agent-instances/${parent.agentInstanceId}/background-sessions`, { headers })).json() as CursorPage<BackgroundSession>).items;
  const child = children.find(c => c.origin.parentSessionId === parentId)!;
  expect(child.completionDelivery?.status).toBe(handled ? "handled" : "delivered");
  const completed = (await runs()).find(r => r.agentRunId === waiting.agentRunId)!;
  expect(completed.status).toBe("completed"); expect(completed.attemptCount).toBe(1); expect(completed.responseId).toBe(waiting.responseId);
  expect((await runs()).filter(r => r.activationKind === "BackgroundCompleted")).toHaveLength(handled ? 0 : 1);
  await page.getByRole("button", { name: /Background work/ }).click();
  const drawer = page.getByRole("dialog", { name: "Background work", exact: true });
  await expect(drawer).toContainText(handled ? "Handled in conversation" : "Reported");
  if (handled) {
    await drawer.getByRole("button", { name: "View handling run", exact: true }).click();
    const details = page.getByRole("dialog", { name: "Run details", exact: true });
    await expect(details).toContainText("I used the verified background result");
    await page.keyboard.press("Escape");
  }
  await page.keyboard.press("Escape"); await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  expect((await runs()).filter(r => r.activationKind === "BackgroundCompleted")).toHaveLength(handled ? 0 : 1);
  expect(errors).toEqual([]);
});

test("duration wait has a distinct Run state and cancellation prevents wakeup", async ({ page }) => {
  await page.goto("/"); await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await page.getByLabel("Message", { exact: true }).fill("[test:execution-wait] Wait then answer.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".conversation-scroll")).toContainText("Waiting for the requested duration");
  await page.getByRole("button", { name: "Stop", exact: true }).click();
  await expect(page.locator(".conversation-scroll")).toContainText("Interrupted");
  await page.waitForTimeout(13_000);
  await expect(page.locator(".conversation-scroll")).not.toContainText("The requested duration elapsed");
});

test("background timeout resumes normally and reports the later unhandled result once", async ({ page }) => {
  await page.goto("/"); await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await page.getByLabel("Message", { exact: true }).fill("[test:completion-timeout] Time out without consuming the child.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".conversation-scroll")).toContainText("The background wait timed out normally", { timeout: 20_000 });
  await expect(page.locator(".conversation-scroll")).toContainText("Background work: Verified background evidence", { timeout: 20_000 });
  await waitForResponseSettled(page);
  const sessionId = page.url().split("/").at(-1)!;
  const token = (await page.evaluate(() => localStorage.getItem("agent-core.owner-capability")))!;
  const response = await page.request.get(`/api/v2/sessions/${sessionId}/agent-runs`, { headers: { "X-AgentCore-Owner-Capability": token } });
  const runs = (await response.json() as CursorPage<AgentRun>).items;
  expect(runs.filter(run => run.activationKind === "BackgroundCompleted")).toHaveLength(1);
  expect(runs.every(run => run.status === "completed")).toBe(true);
});
