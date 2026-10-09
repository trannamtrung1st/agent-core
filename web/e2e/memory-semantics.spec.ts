import { expect, test } from "@playwright/test";

test("closing research preserves memory and report, while envelope deletion requires approval", async ({ page }) => {
  test.setTimeout(120_000);
  const failures: string[] = [];
  page.on("pageerror", error => failures.push(error.message));
  page.on("requestfailed", request => {
    if (!request.failure()?.errorText.includes("ERR_ABORTED")) failures.push(request.url());
  });
  await page.goto("/");
  await expect.poll(() => page.evaluate(() => localStorage.getItem("agent-core.owner-capability"))).not.toBeNull();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const create = await page.request.post("/api/v2/admin/agent-instances", { headers, data: { definitionId: "general-assistant", version: 17 } });
  expect(create.ok(), await create.text()).toBe(true);
  const instanceId = (await create.json()).instanceId as string;
  const newSession = await page.request.post("/api/v2/sessions", { headers, data: { agentInstanceId: instanceId, mode: "text", modelCatalogKey: "scripted-alpha" } });
  expect(newSession.ok()).toBe(true);
  const sessionId = (await newSession.json()).sessionId as string;
  const report = "# Vietnam car prices\nResearch retained after closing work.\n";
  const reportUrl = `/api/v2/sessions/${sessionId}/workspace/content?path=/home/car-prices.md`;
  expect((await page.request.put(reportUrl, { headers, data: report })).ok()).toBe(true);
  const memoryRoot = `/api/v2/admin/agent-instances/${instanceId}/learned-memory`;
  await page.goto(`/c/${sessionId}`);
  await expect(page.getByTestId("profile")).toHaveText("Synthetic");
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  async function send(text: string, expected: string) {
    await page.getByLabel("Message", { exact: true }).fill(text);
    await page.getByRole("button", { name: "Send", exact: true }).click();
    await expect(page.locator(".chat-message-assistant").last()).toContainText(expected);
    await expect(page.getByRole("button", { name: "Stop", exact: true })).toBeHidden();
  }
  await send("synthetic-memory-semantics: start-research", "The research is pending review.");
  const before = await (await page.request.get(memoryRoot + "?scope=IdentityUser", { headers })).json();
  expect(before.items).toHaveLength(1);
  const original = before.items[0];
  await send("synthetic-memory-semantics: propose-delete", "Forgetting requires approval.");
  await expect(page.locator(".chat-message-assistant").last().locator(".ant-typography-danger")).toHaveText(
    "Not forgotten: Vietnam car prices research. Forgetting requires approval.");
  expect((await (await page.request.get(memoryRoot + "?scope=IdentityUser", { headers })).json()).items).toHaveLength(1);
  await send("synthetic-memory-semantics: explain-receipt", "Core rejected a delete attempt; no memory was forgotten.");
  await send("Close the Vietnam car prices research work; keep the report.", "✓ Closed");
  expect((await (await page.request.get(memoryRoot + "?scope=IdentityUser", { headers })).json()).items).toHaveLength(0);
  const inspect = `${memoryRoot}/${original.memoryId}?scope=IdentityUser`;
  const retained = await (await page.request.get(inspect, { headers })).json();
  expect(retained.status).toBe("Resolved");
  expect(retained.content).toBe(original.content);
  expect(await (await page.request.get(reportUrl, { headers })).text()).toBe(report);
  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await expect(page.getByText("✓ Closed", { exact: true })).toBeVisible();
  await send("Close the Vietnam car prices research work; keep the report.", "✓ Closed");
  const replay = await (await page.request.get(inspect, { headers })).json();
  expect(replay.updatedAt).toBe(retained.updatedAt);
  await send("synthetic-memory-semantics: close-missing", "No matching open loop found: Missing research.");
  expect(await (await page.request.get(reportUrl, { headers })).text()).toBe(report);
  expect(failures).toEqual([]);
});
