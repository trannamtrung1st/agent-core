import { readFile } from "node:fs/promises";
import { completeDefinitionDraftPublishGate, publishDraftFromInstructions } from "./admin-definition-gate-helpers";
import { draftEditorSection } from "./admin-draft-editor-helpers";
import { expect, test } from "@playwright/test";
import { selectInstanceIdentity } from "./support/instance-identity";
import { waitForResponseSettled } from "./support/response-settled";
import type { AgentRun, BackgroundSession, CursorPage } from "../src/services/api";

test("immediate child reports once, then continues as the same Session with a new user run", async ({ page }) => {
  test.setTimeout(180_000);
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.goto("/");
  await expect.poll(() => page.evaluate(() => localStorage.getItem("agent-core.owner-capability"))).not.toBeNull();
  const fixtureToken = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const builtIn = JSON.parse(await readFile(new URL("../../agents/general-assistant-v17.json", import.meta.url), "utf8"));
  const { id: _id, version: _version, ...candidate } = builtIn;
  const definitionId = `background-journey-${Date.now()}`;
  candidate.definitionId = definitionId;
  candidate.environment.capabilities = { mode: "Selected", resolvedCapabilities: ["knowledge.retrieve", "background.start"] };
  candidate.environment.projection = { alwaysCapabilities: ["knowledge.retrieve", "background.start"] };
  candidate.environment.knowledgeSources = [];
  candidate.skills = [];
  // Report-back is autonomous. This fixture explicitly enables the parent policy
  // instead of treating background.start as permission to bypass it.
  candidate.initiativePolicy.enabled = true;
  candidate.initiativePolicy.maxConsecutiveProactiveTurns = 1;
  const created = await page.request.post("/api/v2/admin/definition-drafts", {
    headers: { "X-AgentCore-Owner-Capability": fixtureToken! }, data: { definitionId, candidate }
  });
  expect(created.ok(), await created.text()).toBe(true);
  await page.goto(`/admin/definitions/${definitionId}/drafts`);
  await page.getByRole("button", { name: /^Draft rev / }).first().click();
  const editor = draftEditorSection(page);
  await completeDefinitionDraftPublishGate(page, editor, "knowledge.retrieve", { skipToolAllowlist: true });
  await publishDraftFromInstructions(page, editor);
  await page.goto("/");
  await selectInstanceIdentity(page, { id: definitionId, version: 1 });
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await page.getByLabel("Message", { exact: true }).fill("[test:background-start] Check the progress in the background.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i);
  const parentId = page.url().match(/\/c\/([0-9a-f-]{36})/i)?.[1];
  expect(parentId).toBeTruthy();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const parent = await (await page.request.get(`/api/v2/sessions/${parentId}`, { headers })).json();
  await expect(page.locator(".conversation-scroll")).toContainText("Started the background check. You can keep chatting while it runs.");
  await expect(page.locator(".conversation-scroll")).toContainText("Background work: An unresolved checkpoint needs the owner's attention.", { timeout: 30_000 });
  await waitForResponseSettled(page);
  const childPage = await (await page.request.get(`/api/v2/agent-instances/${parent.agentInstanceId}/background-sessions`, { headers })).json() as CursorPage<BackgroundSession>;
  expect(childPage.items).toHaveLength(1);
  const child = childPage.items[0];
  expect(child.origin.parentSessionId).toBe(parentId);
  expect(child.latestRun?.outcome?.attentionRequired).toBe(true);
  expect(child.surfaces).toEqual(["BackgroundWork"]);
  const parentRuns = await (await page.request.get(`/api/v2/sessions/${parentId}/agent-runs`, { headers })).json() as CursorPage<AgentRun>;
  expect(parentRuns.items.filter(run => run.activationKind === "BackgroundCompleted")).toHaveLength(1);

  await page.getByRole("button", { name: /Background work/ }).click();
  const drawer = page.getByRole("dialog", { name: "Background work", exact: true });
  await drawer.getByRole("button", { name: "View history", exact: true }).click();
  const details = page.getByRole("dialog", { name: "Background progress check", exact: true });
  await expect(details.getByRole("region", { name: "Needs attention", exact: true })).toContainText("An unresolved checkpoint");
  await details.getByRole("button", { name: "Continue in chat", exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/c/${child.session.sessionId}$`));
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await expect(page.locator(".conversation-scroll")).toContainText("An unresolved checkpoint needs the owner's attention.");
  await page.getByLabel("Message", { exact: true }).fill("Check B too.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".chat-message-assistant").last()).toBeVisible();
  await waitForResponseSettled(page);
  await expect.poll(async () => {
    const runs = await (await page.request.get(`/api/v2/sessions/${child.session.sessionId}/agent-runs`, { headers })).json() as CursorPage<AgentRun>;
    return runs.items.filter(run => run.activationKind === "UserTurn" && run.status === "completed").length;
  }).toBe(1);
  const current = await (await page.request.get(`/api/v2/sessions/${child.session.sessionId}/background`, { headers })).json() as BackgroundSession;
  expect(current.session.sessionId).toBe(child.session.sessionId);
  expect(current.origin).toEqual(child.origin);
  expect(current.surfaces).toEqual(["ChatList", "BackgroundWork"]);
  const finalParentRuns = await (await page.request.get(`/api/v2/sessions/${parentId}/agent-runs`, { headers })).json() as CursorPage<AgentRun>;
  expect(finalParentRuns.items.filter(run => run.activationKind === "BackgroundCompleted")).toHaveLength(1);
  expect(errors).toEqual([]);
});
