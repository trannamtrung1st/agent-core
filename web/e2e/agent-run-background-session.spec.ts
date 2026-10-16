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
  await selectInstanceIdentity(page, { id: "general-assistant", version: 21 });
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await page.getByLabel("Message", { exact: true }).fill("[test:background-start] [test:background-files] Check the progress in the background.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i);
  const parentId = page.url().match(/\/c\/([0-9a-f-]{36})/i)?.[1];
  expect(parentId).toBeTruthy();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const parent = await (await page.request.get(`/api/v2/sessions/${parentId}`, { headers })).json();
  await expect(page.locator(".conversation-scroll")).toContainText("Started the background check. You can keep chatting while it runs.");
  await expect(page.locator(".conversation-scroll")).toContainText("Background work: Result A: the original task report is ready.", { timeout: 30_000 });
  await waitForResponseSettled(page);
  const childPage = await (await page.request.get(`/api/v2/agent-instances/${parent.agentInstanceId}/background-sessions`, { headers })).json() as CursorPage<BackgroundSession>;
  expect(childPage.items).toHaveLength(1);
  const child = childPage.items[0];
  expect(child.origin.parentSessionId).toBe(parentId);
  expect(child.completionDelivery?.status).toBe("delivered");
  await expect(page.locator(".conversation-scroll")).toContainText("Background work result");
  expect(child.initialRun?.outcome?.attentionRequired).toBe(true);
  expect(child.surfaces).toEqual(["BackgroundWork"]);
  const parentRuns = await (await page.request.get(`/api/v2/sessions/${parentId}/agent-runs`, { headers })).json() as CursorPage<AgentRun>;
  expect(parentRuns.items.filter(run => run.activationKind === "BackgroundCompleted")).toHaveLength(1);

  await page.getByRole("button", { name: /Background work/ }).click();
  const drawer = page.getByRole("dialog", { name: "Background work", exact: true });
  await drawer.getByRole("button", { name: "View original result", exact: true }).click();
  const details = page.getByRole("dialog", { name: "Background progress check", exact: true });
  await expect(details.getByRole("region", { name: "Needs attention", exact: true })).toContainText("Result A:");
  await details.getByRole("button", { name: "Continue in chat", exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/c/${child.session.sessionId}$`));
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await expect(page.locator(".conversation-scroll")).toContainText("Result A: the original task report is ready.");
  for (const result of ["Result B", "Result C"]) {
    await page.getByLabel("Message", { exact: true }).fill(`[test:followup-file] ${result}`);
    await page.getByRole("button", { name: "Send", exact: true }).click();
    await expect(page.locator(".conversation-scroll")).toContainText("Follow-up report created");
    await waitForResponseSettled(page);
    await expect.poll(async () => {
      const runs = await (await page.request.get(`/api/v2/sessions/${child.session.sessionId}/agent-runs`, { headers })).json() as CursorPage<AgentRun>;
      return runs.items.filter(run => run.activationKind === "UserTurn" && run.status === "completed").length;
    }).toBe(result === "Result B" ? 1 : 2);
  }
  const current = await (await page.request.get(`/api/v2/sessions/${child.session.sessionId}/background`, { headers })).json() as BackgroundSession;
  expect(current.session.sessionId).toBe(child.session.sessionId);
  expect(current.origin).toEqual(child.origin);
  expect(current.surfaces).toEqual(["ChatList", "BackgroundWork"]);
  expect(current.initialRun).toEqual(child.initialRun);
  expect(current.originalTitle).toBe(child.originalTitle);
  expect(child.artifactCount).toBe(1);
  expect(current.artifactCount).toBe(1);
  const allFiles = await (await page.request.get(`/api/v2/sessions/${child.session.sessionId}/artifacts/page`, { headers })).json();
  expect(allFiles.items).toHaveLength(3);
  const originalFiles = await (await page.request.get(`/api/v2/sessions/${child.session.sessionId}/artifacts/page?agentRunId=${child.origin.initialAgentRunId}`, { headers })).json();
  expect(originalFiles.items.map((file: { displayName: string }) => file.displayName)).toEqual(["original-A.md"]);
  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  let foregroundPosts = 0;
  page.on("request", request => { if (request.method() === "POST" && request.url().endsWith("/continue-in-chat")) foregroundPosts++; });
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
    if (!await page.getByRole("button", { name: /Background work/ }).isVisible())
      await page.getByRole("button", { name: "Chats", exact: true }).click();
    await page.getByRole("button", { name: /Background work/ }).click();
    const work = page.getByRole("dialog", { name: "Background work", exact: true });
    await expect(work).toContainText("Result A: the original task report is ready.");
    await expect(work).toContainText("1 file");
    await expect(work).toContainText("Continued in chat");
    await work.getByRole("button", { name: "View original result" }).click();
    const original = page.getByRole("dialog", { name: "Background progress check", exact: true });
    await expect(original.locator("[data-agent-run-id]").first()).toHaveAttribute("data-agent-run-id", child.origin.initialAgentRunId);
    await expect(original.getByRole("region", { name: "Needs attention" })).toContainText("Result A:");
    await expect(original.getByText("Original task files", { exact: true })).toBeVisible();
    await expect(original.getByRole("button", { name: "Download original-A.md" })).toBeVisible();
    await expect(original.getByRole("button", { name: "Download followup.md" })).toHaveCount(0);
    await expect.poll(() => original.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await page.screenshot({ path: `test-results/background-original-${width}.png`, fullPage: true });
    await original.getByRole("link", { name: "Open chat", exact: true }).click();
    await expect(original).toBeHidden();
    await expect(page).toHaveURL(new RegExp(`/c/${child.session.sessionId}$`));
  }
  expect(foregroundPosts).toBe(0);
  const unchangedRuns = await (await page.request.get(`/api/v2/sessions/${child.session.sessionId}/agent-runs`, { headers })).json() as CursorPage<AgentRun>;
  expect(unchangedRuns.items).toHaveLength(3);
  const finalParentRuns = await (await page.request.get(`/api/v2/sessions/${parentId}/agent-runs`, { headers })).json() as CursorPage<AgentRun>;
  expect(finalParentRuns.items.filter(run => run.activationKind === "BackgroundCompleted")).toHaveLength(1);
  expect(errors).toEqual([]);
});

test("missing background completion reports a durable failure once with visible diagnostics", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.goto("/");
  await selectInstanceIdentity(page, { id: "general-assistant", version: 24 });
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await page.getByLabel("Message", { exact: true }).fill("[test:background-start] [test:background-prose] Check progress in the background and report it.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".conversation-scroll")).toContainText("Background work failed.", { timeout: 30_000 });
  await waitForResponseSettled(page);
  const parentId = page.url().match(/\/c\/([0-9a-f-]{36})/i)![1];
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const parent = await (await page.request.get(`/api/v2/sessions/${parentId}`, { headers })).json();
  const children = await (await page.request.get(`/api/v2/agent-instances/${parent.agentInstanceId}/background-sessions`, { headers })).json() as CursorPage<BackgroundSession>;
  const child = children.items[0];
  expect(child.initialRun?.failureCode).toBe("completion-required");
  expect(child.initialRun?.attemptCount).toBe(1);
  expect(child.initialRun?.executionDiagnostic?.failedAttempts).toEqual([
    { attempt: 1, code: "InvalidResponse", reason: "backgroundCompletionRequired", channel: "backgroundCompletion" }
  ]);
  expect(child.completionDelivery?.status).toBe("delivered");
  const text = await page.locator(".conversation-scroll").innerText();
  expect(text).not.toContain("\\n\\n");
  expect(text.match(/Background work failed\./g)).toHaveLength(1);
  await page.getByRole("button", { name: /Background work/ }).click();
  await page.getByRole("dialog", { name: "Background work", exact: true }).getByRole("button", { name: "View original result", exact: true }).click();
  const details = page.getByRole("dialog", { name: "Background progress check", exact: true });
  await expect(details).toContainText("backgroundCompletionRequired");
  await expect(details).toContainText("backgroundCompletion");
  await expect(details).toContainText("Last tool checkpoint");
  await expect(details).toContainText("Completion recovery");
  await expect(details).toContainText("Attempt 1: InvalidResponse");
  const reports = await (await page.request.get(`/api/v2/sessions/${parentId}/agent-runs`, { headers })).json() as CursorPage<AgentRun>;
  expect(reports.items.filter(run => run.activationKind === "BackgroundCompleted")).toHaveLength(1);
  expect(errors).toEqual([]);
});

for (const scenario of ["correct", "invalid"] as const) {
  test(`oversized background completion ${scenario === "correct" ? "recovers once" : "reports invalid completion truthfully"}`, async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto("/");
    await selectInstanceIdentity(page, { id: "general-assistant", version: 24 });
    await expect(page.getByTestId("connection")).toHaveText("Ready");
    await page.getByLabel("Message", { exact: true }).fill(`[test:background-start] [test:background-completion-${scenario}] Collect a digest in the background and report it.`);
    await page.getByRole("button", { name: "Send", exact: true }).click();
    await expect(page.locator(".conversation-scroll")).toContainText(
      scenario === "correct" ? "Collected digest; no further actions were taken." : "Background work failed.", { timeout: 30_000 });
    await waitForResponseSettled(page);
    const parentId = page.url().match(/\/c\/([0-9a-f-]{36})/i)![1];
    const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
    const headers = { "X-AgentCore-Owner-Capability": token! };
    const parent = await (await page.request.get(`/api/v2/sessions/${parentId}`, { headers })).json();
    const children = await (await page.request.get(`/api/v2/agent-instances/${parent.agentInstanceId}/background-sessions`, { headers })).json() as CursorPage<BackgroundSession>;
    const child = children.items.find(item => item.origin.parentSessionId === parentId)!;
    expect(child.completionDelivery?.status).toBe("delivered");
    expect(child.initialRun?.status).toBe(scenario === "correct" ? "completed" : "failed");
    if (scenario === "invalid") {
      expect(child.initialRun?.failureCode).toBe("completion-invalid");
      expect(child.initialRun?.executionDiagnostic?.failedAttempts).toEqual([
        { attempt: 1, code: "InvalidResponse", reason: "backgroundCompletionInvalid", channel: "backgroundCompletion" }
      ]);
      await expect(page.locator(".conversation-scroll")).toContainText("Background work result");
      await expect(page.locator(".conversation-scroll")).not.toContainText("did not provide work.complete");
      await page.getByRole("button", { name: /Background work/ }).click();
      await page.getByRole("dialog", { name: "Background work", exact: true }).getByRole("button", { name: "View original result", exact: true }).click();
      await expect(page.getByRole("dialog", { name: "Background progress check", exact: true })).toContainText("backgroundCompletionInvalid");
    }
    const reports = await (await page.request.get(`/api/v2/sessions/${parentId}/agent-runs`, { headers })).json() as CursorPage<AgentRun>;
    expect(reports.items.filter(run => run.activationKind === "BackgroundCompleted")).toHaveLength(1);
    expect(errors).toEqual([]);
  });
}

for (const exhausted of [false, true]) {
  test(`background semantic response ${exhausted ? "exhausts one correction" : "recovers through completion only"}`, async ({ page }) => {
    const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
    await page.goto("/");
    await selectInstanceIdentity(page, { id: "general-assistant", version: 24 });
    await expect(page.getByTestId("connection")).toHaveText("Ready");
    await page.getByLabel("Message", { exact: true }).fill(`[test:background-start] [test:background-semantic-${exhausted ? "exhausted" : "recovery"}] Verify the bounded background outcome.`);
    await page.getByRole("button", { name: "Send", exact: true }).click();
    await expect(page.locator(".conversation-scroll")).toContainText(exhausted ? "Background work failed." : "Research is incomplete", { timeout: 30_000 });
    await waitForResponseSettled(page);
    await page.getByRole("button", { name: /Background work/ }).click();
    await page.getByRole("dialog", { name: "Background work", exact: true }).getByRole("button", { name: "View original result", exact: true }).click();
    const details = page.getByRole("dialog", { name: "Background progress check", exact: true });
    const recovery = details.getByRole("region", { name: "Response recovery" });
    await expect(recovery).toContainText("Invalid response blocks · Response function");
    await expect(recovery).toContainText("Eligible for one correction · Completion only");
    await expect(recovery).toContainText(exhausted ? "Correction failed" : "Correction completed");
    await expect(details).toContainText("Attempt 1: InvalidResponse · invalidBlocks");
    await expect(details).toContainText("1 of 3");
    for (const width of [1440, 768, 767, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await expect.poll(() => details.evaluate(d => d.scrollWidth <= d.clientWidth)).toBe(true);
      await expect(recovery).toBeVisible();
      expect(await recovery.evaluate(r => r.scrollWidth <= r.clientWidth)).toBe(true);
      expect(await recovery.locator(':scope > div').evaluate(r => getComputedStyle(r).gap)).toBe("8px");
    }
    await page.keyboard.press("Escape"); await expect(details).toBeHidden();
    expect(errors).toEqual([]);
  });
}
